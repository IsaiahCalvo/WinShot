using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace WinShot.Core;

public enum UpdateState { UpToDate, UpdateAvailable, Error }

/// <summary>Thrown when a downloaded installer fails its SHA-256 integrity check.</summary>
public sealed class UpdateVerificationException(string message) : Exception(message);

/// <summary>
/// Result of an update check. <see cref="LatestVersion"/> and <see cref="DownloadUrl"/> are only
/// populated when <see cref="State"/> is <see cref="UpdateState.UpdateAvailable"/>.
/// </summary>
public sealed record UpdateCheckResult(
    UpdateState State,
    string? LatestVersion = null,
    string? DownloadUrl = null,
    string? Message = null);

/// <summary>
/// Anonymous GitHub-Releases poller for IsaiahCalvo/WinShot, modelled on Clip's updater:
/// hits the public "latest release" REST endpoint with a User-Agent (GitHub rejects requests
/// without one), parses the tag, and compares it to the running assembly version.
/// All network/parse failures collapse to <see cref="UpdateState.Error"/> — it never throws.
/// </summary>
public static class UpdateService
{
    // HTTPS-only, hardcoded to the official repo. Never accept a URL from outside this file.
    private const string LatestReleaseUrl =
        "https://api.github.com/repos/IsaiahCalvo/WinShot/releases/latest";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Running version, read from the assembly (defaults to 0.0.0 if unset).</summary>
    public static string CurrentVersion => CleanVersion(
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0");

    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            request.Headers.UserAgent.ParseAdd("WinShotUpdateChecker/1.0");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return new UpdateCheckResult(UpdateState.Error, Message: $"GitHub returned {(int)response.StatusCode}.");

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            string? tag = GetString(root, "tag_name") ?? GetString(root, "name");
            if (string.IsNullOrWhiteSpace(tag))
                return new UpdateCheckResult(UpdateState.Error, Message: "Release has no tag.");

            string latest = CleanVersion(tag);
            if (!IsNewerVersion(CurrentVersion, latest))
                return new UpdateCheckResult(UpdateState.UpToDate, latest);

            string? downloadUrl = FindSetupAssetUrl(root);
            if (downloadUrl is null)
                return new UpdateCheckResult(UpdateState.Error, Message: "No Setup.exe asset on the latest release.");

            return new UpdateCheckResult(UpdateState.UpdateAvailable, latest, downloadUrl,
                $"WinShot {latest} is available.");
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(UpdateState.Error, Message: ex.Message);
        }
    }

    /// <summary>
    /// First release asset whose download URL ends in "-Setup.exe" (the official installer),
    /// served only from github.com over HTTPS. Anything else is ignored on purpose.
    /// </summary>
    internal static string? FindSetupAssetUrl(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var asset in assets.EnumerateArray())
        {
            string? url = GetString(asset, "browser_download_url");
            if (url is null) continue;
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsGitHubHost(url)) continue;
            if (url.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase))
                return url;
        }
        return null;
    }

    /// <summary>
    /// Download the installer from <paramref name="downloadUrl"/> (must be an HTTPS github.com URL)
    /// to %TEMP%\WinShot\updates, verify its SHA-256 against the sibling "&lt;setupname&gt;.sha256"
    /// release asset, and launch it with Inno Setup's silent switches. The installer's own
    /// CloseApplications/RestartApplications closes and relaunches WinShot, so the caller must
    /// shut WinShot down right after this returns (no return value — failures throw).
    /// </summary>
    /// <exception cref="InvalidOperationException">Non-GitHub URL.</exception>
    /// <exception cref="UpdateVerificationException">The downloaded file's hash did not match the sidecar.</exception>
    public static async Task DownloadAndLaunchAsync(string downloadUrl, string version, CancellationToken ct = default)
    {
        if (!downloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || !IsGitHubHost(downloadUrl))
            throw new InvalidOperationException("Refusing to download from a non-GitHub URL.");

        string dir = Path.Combine(Path.GetTempPath(), "WinShot", "updates");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"WinShot_{version}-Setup.exe");

        using (var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl))
        {
            request.Headers.UserAgent.ParseAdd("WinShotUpdateChecker/1.0");
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using var src = await response.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(path);
            await src.CopyToAsync(dst, ct);
        }

        // Defense-in-depth + corruption check: the release publishes "<setupname>.sha256" next to
        // the .exe. Fetch it (same https + github.com pin), and only launch if the local file's
        // digest matches. Older releases have no sidecar — log and proceed for back-compat.
        string? expected = await TryDownloadSha256Async(downloadUrl + ".sha256", ct);
        if (expected is not null)
        {
            string actual = ComputeFileSha256(path);
            if (!HashesEqual(expected, actual))
            {
                try { File.Delete(path); } catch (Exception ex) { Log.Error("Failed to delete unverified installer", ex); }
                throw new UpdateVerificationException(
                    $"Installer hash mismatch (expected {expected}, got {actual}).");
            }
        }
        else
        {
            Log.Info("No .sha256 sidecar on this release; skipping installer hash verification (back-compat).");
        }

        // Inno Setup silent switches: install over the running app, suppress prompts, no reboot.
        // The .iss has CloseApplications=force + RestartApplications=yes, so it swaps WinShot.exe
        // and relaunches it; we just need to exit so our files are unlocked.
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            Arguments = "/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS /SUPPRESSMSGBOXES /NORESTART",
            UseShellExecute = true,
        });
    }

    /// <summary>
    /// Fetch the "<c>.sha256</c>" sidecar and return its first hex token (lowercased), or null if the
    /// asset is missing (404 → older release) or unreadable. Enforces the same https + github.com pin.
    /// </summary>
    private static async Task<string?> TryDownloadSha256Async(string sha256Url, CancellationToken ct)
    {
        if (!sha256Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || !IsGitHubHost(sha256Url))
            throw new InvalidOperationException("Refusing to download checksum from a non-GitHub URL.");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, sha256Url);
            request.Headers.UserAgent.ParseAdd("WinShotUpdateChecker/1.0");
            using var response = await Http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            response.EnsureSuccessStatusCode();
            string body = await response.Content.ReadAsStringAsync(ct);
            return ParseSha256(body);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to fetch installer checksum", ex);
            return null;
        }
    }

    /// <summary>First whitespace-delimited token of a checksum file (handles "&lt;hash&gt;  &lt;filename&gt;"
    /// sha256sum format and a bare hash), lowercased, or null if it isn't 64 hex chars.</summary>
    internal static string? ParseSha256(string body)
    {
        string token = body.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } parts
            ? parts[0]
            : string.Empty;
        token = token.ToLowerInvariant();
        if (token.Length != 64) return null;
        foreach (char c in token)
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f'))) return null;
        return token;
    }

    /// <summary>Case-insensitive compare of two hex digests.</summary>
    internal static bool HashesEqual(string expected, string actual) =>
        string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

    private static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool IsGitHubHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase));

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>True only if both parse as System.Version AND latest is strictly greater.</summary>
    internal static bool IsNewerVersion(string current, string latest) =>
        Version.TryParse(current, out var c) && Version.TryParse(latest, out var l) && l > c;

    /// <summary>Trim, drop a leading 'v'/'V', then cut at the first '+' (build) or '-' (prerelease).</summary>
    internal static string CleanVersion(string raw)
    {
        string v = raw.Trim();
        if (v.Length > 0 && (v[0] == 'v' || v[0] == 'V'))
            v = v[1..];
        int cut = v.IndexOfAny(['+', '-']);
        if (cut >= 0)
            v = v[..cut];
        return v;
    }
}
