using System.IO;
using System.IO.Pipes;

namespace WinShot.Core;

/// <summary>
/// Single-instance command routing over the "WinShot.Commands" named pipe.
/// A second WinShot process (e.g. launched via winshot://) forwards its command with
/// <see cref="TrySendToRunningInstance"/> and exits; the primary instance runs a
/// <see cref="CommandServer"/> that raises <see cref="CommandReceived"/> for each line.
/// The event fires on the accept-loop thread — callers marshal to the UI thread.
/// </summary>
/// <summary>
/// The per-invocation overrides carried by a <c>winshot://</c> URL's query string —
/// <c>?then=</c> replaces the after-capture set for this capture only, <c>delayed=</c>
/// counts down first. <see cref="None"/> means "do exactly what the settings say".
/// </summary>
public sealed record CaptureCommandOptions(PostCaptureSet? Then, int DelaySeconds)
{
    public static readonly CaptureCommandOptions None = new(null, 0);
}

public sealed class CommandServer : IDisposable
{
    public const string PipeName = "WinShot.Commands";

    private static readonly HashSet<string> ValidCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "capture-area",
        "capture-window",
        "capture-fullscreen",
        "capture-display",
        "capture-previous",
        "capture-window-background",
        "all-in-one",
        "record",
        "record-display",
        "ocr",
        "scrolling",
        "scroll-horizontal",
        "history",
        "settings",
        "self-timer",
        "restore-last",
        "exit",
    };

    // Formats GDI+ can decode — what Explorer "Open with" may hand us for the editor.
    private static readonly HashSet<string> EditableImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp",
    };

    private CancellationTokenSource? _cts;

    public event Action<string>? CommandReceived;

    /// <summary>Starts the background accept loop. Safe to call once; later calls are no-ops.</summary>
    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        _ = Task.Run(() => RunAsync(token), CancellationToken.None);
    }

    public void Dispose()
    {
        if (_cts is null) return;
        try
        {
            _cts.Cancel();
            _cts.Dispose();
        }
        catch
        {
            // Shutdown must never throw.
        }
        _cts = null;
    }

    /// <summary>
    /// Tries to hand a command to an already-running WinShot instance.
    /// Returns false when no instance is listening (2.5 s connect timeout).
    /// </summary>
    public static bool TrySendToRunningInstance(string command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2500);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(command);
            client.WaitForPipeDrain();
            return true;
        }
        catch (TimeoutException)
        {
            return false; // no running instance — the normal first-launch path
        }
        catch (Exception ex)
        {
            Log.Error("Failed to send command to running WinShot instance", ex);
            return false;
        }
    }

    /// <summary>
    /// Probes whether a primary instance is actually listening on the pipe, independent of
    /// the single-instance mutex. Used so a stale/abandoned mutex (e.g. left by a crashed
    /// instance) doesn't leave WinShot permanently unlaunchable.
    /// </summary>
    public static bool IsInstanceRunning()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(400);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Maps "winshot://capture-area", "winshot://capture-area?copy=1",
    /// "--capture-area", or bare "capture-area" to the canonical command name;
    /// unknown input returns null.
    /// </summary>
    public static string? ParseCommand(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg)) return null;

        string s = arg.Trim().Trim('"');

        // Explorer "Open with" hands us a bare image path (or a second instance forwards
        // it as edit:<path>) — resolve it before the URL munging below can eat legal
        // path characters like '?' or '#'.
        if (TryParseEditCommand(s, out string? edit))
            return edit;

        if (s.StartsWith("winshot://", StringComparison.OrdinalIgnoreCase))
            s = s["winshot://".Length..];
        else if (s.StartsWith("winshot:", StringComparison.OrdinalIgnoreCase))
            s = s["winshot:".Length..];

        int queryStart = s.IndexOfAny(['?', '#']);
        if (queryStart >= 0)
            s = s[..queryStart];

        s = s.TrimStart('-');
        s = s.Trim('/');

        // Parameterized automation command (scroll-region:x,y,w,h[,auto][,save]) — the
        // args ride in the command itself, so it bypasses the fixed-name table.
        if (s.StartsWith("scroll-region:", StringComparison.OrdinalIgnoreCase))
            return s;

        return ValidCommands.TryGetValue(s, out string? command) ? command : null;
    }

    /// <summary>
    /// The query half of a <c>winshot://</c> URL: what to do with the capture
    /// (<c>?then=copy,save,edit,pin,thumbnail</c>) and how long to wait before taking it
    /// (<c>?delayed=10</c>). Both are per-invocation overrides — nothing here is persisted,
    /// so a scripted capture cannot quietly change what the user's own hotkeys do.
    /// </summary>
    public static CaptureCommandOptions ParseOptions(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
            return CaptureCommandOptions.None;

        string s = arg.Trim().Trim('"');
        int queryStart = QueryStart(s);
        if (queryStart < 0 || queryStart == s.Length - 1)
            return CaptureCommandOptions.None;

        PostCaptureSet? then = null;
        int delaySeconds = 0;
        foreach (string pair in s[(queryStart + 1)..].Split(['&', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string key = (eq < 0 ? pair : pair[..eq]).Trim();
            string value = eq < 0 ? string.Empty : Uri.UnescapeDataString(pair[(eq + 1)..].Trim());

            if (key.Equals("then", StringComparison.OrdinalIgnoreCase))
                then = PostCaptureSet.TryParseThen(value) ?? then;
            else if (key.Equals("delayed", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("delay", StringComparison.OrdinalIgnoreCase))
                delaySeconds = ClampDelay(value);
        }
        return then is null && delaySeconds == 0 ? CaptureCommandOptions.None : new(then, delaySeconds);
    }

    /// <summary>
    /// Re-attaches the raw URL's query to an already-resolved command name. A second
    /// WinShot process forwards the resolved command (so a relative "Open with" path is
    /// made absolute against the right working directory) — without this the primary
    /// instance would receive the verb and lose the <c>?then=</c> that came with it.
    /// </summary>
    public static string WithQuery(string command, string? rawArg)
    {
        if (string.IsNullOrWhiteSpace(rawArg))
            return command;

        string s = rawArg.Trim().Trim('"');
        int queryStart = QueryStart(s);
        return queryStart < 0 || queryStart == s.Length - 1 ? command : command + "?" + s[(queryStart + 1)..];
    }

    /// <summary>
    /// Where a <c>winshot://</c> URL's query begins, or -1 when the argument has none.
    /// A file path is not a URL: '#' and '?' are legal in a Windows file name, so an
    /// argument that resolves to an <c>edit:&lt;path&gt;</c> command has no query at all —
    /// the same guard <see cref="ParseCommand"/> runs before its own URL munging.
    /// Without it, "Open with" on <c>note#1.png</c> while WinShot was already running was
    /// forwarded as <c>edit:…note#1.png?1.png</c>, which the running instance could not
    /// parse back into a path, and the file opened nowhere.
    /// </summary>
    private static int QueryStart(string trimmed) =>
        TryParseEditCommand(trimmed, out _) ? -1 : trimmed.IndexOfAny(['?', '#']);

    /// <summary>
    /// A delay is seconds, and a capture nobody is waiting for is a bug rather than a
    /// feature: anything unparseable, negative or past a minute is refused rather than
    /// parking the app in a countdown a script cannot cancel.
    /// </summary>
    private static int ClampDelay(string value) =>
        int.TryParse(value, out int seconds) && seconds > 0 ? Math.Min(seconds, 60) : 0;

    /// <summary>Maps an existing image-file path (bare or "edit:"-prefixed) to
    /// "edit:&lt;full path&gt;"; anything else returns false.</summary>
    private static bool TryParseEditCommand(string s, out string? command)
    {
        command = null;
        string path = s.StartsWith("edit:", StringComparison.OrdinalIgnoreCase)
            ? s["edit:".Length..].Trim().Trim('"')
            : s;
        if (!EditableImageExtensions.Contains(Path.GetExtension(path)))
            return false;
        try
        {
            path = Path.GetFullPath(path);
            if (!File.Exists(path))
                return false;
        }
        catch
        {
            return false; // not a well-formed path — let the command table have a go
        }
        command = "edit:" + path;
        return true;
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                using var reader = new StreamReader(server);
                string? line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    try
                    {
                        CommandReceived?.Invoke(line.Trim());
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Command handler threw", ex);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error("Command pipe accept loop error", ex);
                try
                {
                    await Task.Delay(250, token).ConfigureAwait(false); // avoid a tight failure loop
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
