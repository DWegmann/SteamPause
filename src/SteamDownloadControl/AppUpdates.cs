using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamDownloadControl;

internal static class AppUpdates
{
    private const string Repository = "DWegmann/SteamDownloadControl";
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
        DefaultRequestHeaders =
        {
            { "User-Agent", "SteamDownloadControl" },
            { "Accept", "application/vnd.github+json" }
        }
    };

    public static async Task<string?> FindNewerTagAsync()
    {
        var release = await Client.GetFromJsonAsync<GitHubRelease>(
            $"https://api.github.com/repos/{Repository}/releases/latest").ConfigureAwait(false);
        var tag = release?.Tag;
        if (tag is null || !Version.TryParse(tag.AsSpan(tag.StartsWith('v') ? 1 : 0), out var parsed))
            throw new JsonException("The GitHub release tag does not contain a version number.");

        var version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), Math.Max(parsed.Revision, 0));
        return version > Program.AppVersion ? tag : null;
    }

    public static string ReleasePage(string tag) => $"https://github.com/{Repository}/releases/tag/{Uri.EscapeDataString(tag)}";

    private sealed record GitHubRelease([property: JsonPropertyName("tag_name")] string? Tag);
}
