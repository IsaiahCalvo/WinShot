using System.Text.Json;
using WinShot.Core;
using Xunit;

namespace WinShot.Tests;

public class UpdateServiceTests
{
    [Theory]
    [InlineData("v1.0.1", "1.0.1")]
    [InlineData("V2.3.4", "2.3.4")]
    [InlineData(" 1.2.3 ", "1.2.3")]
    [InlineData("1.0.0+build42", "1.0.0")]
    [InlineData("1.0.0-beta.2", "1.0.0")]
    [InlineData("v1.2.0-rc1+sha", "1.2.0")]
    public void CleanVersion_StripsPrefixAndSuffixes(string raw, string expected) =>
        Assert.Equal(expected, UpdateService.CleanVersion(raw));

    [Theory]
    [InlineData("1.0.0", "1.0.1", true)]
    [InlineData("1.0.1", "1.0.1", false)]   // equal => not newer
    [InlineData("1.0.2", "1.0.1", false)]   // older latest => not newer
    [InlineData("1.0", "1.0.1", true)]
    [InlineData("garbage", "1.0.1", false)] // unparseable current => up to date, no error
    [InlineData("1.0.0", "garbage", false)] // unparseable latest => up to date, no error
    public void IsNewerVersion_StrictGreaterThanAndSafeOnGarbage(string current, string latest, bool expected) =>
        Assert.Equal(expected, UpdateService.IsNewerVersion(current, latest));

    [Fact]
    public void FindSetupAssetUrl_PicksHttpsGitHubSetupExeOnly()
    {
        const string json = """
        {
          "assets": [
            { "browser_download_url": "https://github.com/IsaiahCalvo/WinShot/releases/download/v1.0.1/WinShot-win-x64.zip" },
            { "browser_download_url": "http://github.com/IsaiahCalvo/WinShot/releases/download/v1.0.1/WinShot_1.0.1-Setup.exe" },
            { "browser_download_url": "https://evil.example.com/WinShot_1.0.1-Setup.exe" },
            { "browser_download_url": "https://github.com/IsaiahCalvo/WinShot/releases/download/v1.0.1/WinShot_1.0.1-Setup.exe" }
          ]
        }
        """;
        using var doc = JsonDocument.Parse(json);

        string? url = UpdateService.FindSetupAssetUrl(doc.RootElement);

        // http:// and the non-github host must be rejected; only the HTTPS github Setup.exe wins.
        Assert.Equal("https://github.com/IsaiahCalvo/WinShot/releases/download/v1.0.1/WinShot_1.0.1-Setup.exe", url);
    }

    [Fact]
    public void FindSetupAssetUrl_ReturnsNullWhenNoSetupExe()
    {
        const string json = """
        { "assets": [ { "browser_download_url": "https://github.com/IsaiahCalvo/WinShot/releases/download/v1.0.1/WinShot-win-x64.zip" } ] }
        """;
        using var doc = JsonDocument.Parse(json);

        Assert.Null(UpdateService.FindSetupAssetUrl(doc.RootElement));
    }

    [Theory]
    [InlineData("abc", "ABC", true)]                 // case-insensitive match
    [InlineData("deadbeef", "deadbeef", true)]
    [InlineData("deadbeef", "deadbee0", false)]      // one nibble off => reject
    public void HashesEqual_MatchesCaseInsensitivelyAndRejectsDifferences(string a, string b, bool expected) =>
        Assert.Equal(expected, UpdateService.HashesEqual(a, b));

    [Fact]
    public void ParseSha256_AcceptsBareHashAndSha256sumFormat()
    {
        string hash = new string('a', 64);
        Assert.Equal(hash, UpdateService.ParseSha256(hash));                       // bare hash
        Assert.Equal(hash, UpdateService.ParseSha256($"  {hash}  \n"));            // surrounding whitespace
        Assert.Equal(hash, UpdateService.ParseSha256($"{hash}  WinShot-Setup.exe")); // sha256sum "<hash>  <file>"
        Assert.Equal(hash, UpdateService.ParseSha256(hash.ToUpperInvariant()));    // normalized to lowercase
    }

    [Theory]
    [InlineData("")]                                 // empty
    [InlineData("not-a-hash")]                        // not hex / wrong length
    [InlineData("abc123")]                            // too short
    public void ParseSha256_RejectsMalformed(string body) =>
        Assert.Null(UpdateService.ParseSha256(body));
}
