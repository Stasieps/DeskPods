using System.Text.Json;

namespace PodsView;

internal sealed record UpdateInfo(Version Version, string Tag, string? InstallerUrl, string PageUrl)
{
    internal string Display => Version.ToString(3);
}

/// <summary>0.8.46: the pure half of the updater - version maths and the release choice - kept apart for the tests.</summary>
internal static class UpdateRelease
{
    internal const string Repository = "Stasieps/DeskPods";
    internal const string ReleasesPage = "https://github.com/" + Repository + "/releases/latest";

    /// <summary>"v0.8.46", "0.8.46" or "0.8.46-beta" to 0.8.46; anything else is rejected.</summary>
    internal static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        string value = text.Trim().TrimStart('v', 'V');
        int cut = value.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0) value = value[..cut];
        if (!Version.TryParse(value, out Version? parsed)) return false;
        version = new Version(parsed.Major, Math.Max(0, parsed.Minor), Math.Max(0, parsed.Build));
        return true;
    }

    internal static bool IsNewer(Version candidate, Version current) => candidate.CompareTo(current) > 0;

    internal static bool TrustedDownload(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>Pure decision over the GitHub release JSON, kept apart for the tests.</summary>
    internal static UpdateInfo? Pick(JsonElement release, string currentText)
    {
        if (release.TryGetProperty("draft", out JsonElement draft) && draft.ValueKind == JsonValueKind.True) return null;
        if (release.TryGetProperty("prerelease", out JsonElement pre) && pre.ValueKind == JsonValueKind.True) return null;
        string? tag = release.TryGetProperty("tag_name", out JsonElement t) ? t.GetString() : null;
        if (!TryParseVersion(tag, out Version latest)) return null;
        if (!TryParseVersion(currentText, out Version current) || !IsNewer(latest, current)) return null;

        string wanted = $"DeskPods_Setup_v{latest.ToString(3)}.exe";
        string? exact = null, generic = null;
        if (release.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string? name = asset.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                string? url = asset.TryGetProperty("browser_download_url", out JsonElement u) ? u.GetString() : null;
                if (name is null || !TrustedDownload(url)) continue;
                if (name.Equals(wanted, StringComparison.OrdinalIgnoreCase)) exact = url;
                else if (name.Equals("DeskPods_Setup.exe", StringComparison.OrdinalIgnoreCase)) generic = url;
            }
        }
        string page = release.TryGetProperty("html_url", out JsonElement h) && TrustedDownload(h.GetString())
            ? h.GetString()! : ReleasesPage;
        return new UpdateInfo(latest, tag!, exact ?? generic, page);
    }
}
