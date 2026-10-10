using System.Text.Json;

namespace PodsView;

internal sealed record UpdateInfo(Version Version, string Tag, string? InstallerUrl, string PageUrl, string? InstallerSha256 = null)
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

    /// <summary>
    /// 0.8.47: GitHub publishes a digest for every release asset ("sha256:" and 64 hex digits).
    /// Returns the lower-case hex, or null when the asset has none or it is not SHA-256.
    /// </summary>
    internal static string? Sha256Hex(string? digest)
    {
        const string prefix = "sha256:";
        if (digest is null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        string hex = digest[prefix.Length..].Trim();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex.ToLowerInvariant() : null;
    }

    /// <summary>
    /// 0.8.47: true when the downloaded setup hashes to the published SHA-256. A release without
    /// a digest is accepted, so the executable checks of the download stay the only gate there.
    /// </summary>
    internal static bool DigestAccepts(string? sha256Hex, ReadOnlySpan<byte> hash) =>
        sha256Hex is null || Convert.ToHexString(hash).Equals(sha256Hex, StringComparison.OrdinalIgnoreCase);

    /// <summary>Pure decision over the GitHub release JSON, kept apart for the tests.</summary>
    internal static UpdateInfo? Pick(JsonElement release, string currentText)
    {
        if (release.TryGetProperty("draft", out JsonElement draft) && draft.ValueKind == JsonValueKind.True) return null;
        if (release.TryGetProperty("prerelease", out JsonElement pre) && pre.ValueKind == JsonValueKind.True) return null;
        string? tag = release.TryGetProperty("tag_name", out JsonElement t) ? t.GetString() : null;
        if (!TryParseVersion(tag, out Version latest)) return null;
        if (!TryParseVersion(currentText, out Version current) || !IsNewer(latest, current)) return null;

        string wanted = $"DeskPods_Setup_v{latest.ToString(3)}.exe";
        string? exact = null, generic = null, exactSha = null, genericSha = null;
        if (release.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string? name = asset.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                string? url = asset.TryGetProperty("browser_download_url", out JsonElement u) ? u.GetString() : null;
                if (name is null || !TrustedDownload(url)) continue;
                string? sha = asset.TryGetProperty("digest", out JsonElement d) && d.ValueKind == JsonValueKind.String
                    ? Sha256Hex(d.GetString()) : null;
                if (name.Equals(wanted, StringComparison.OrdinalIgnoreCase)) { exact = url; exactSha = sha; }
                else if (name.Equals("DeskPods_Setup.exe", StringComparison.OrdinalIgnoreCase)) { generic = url; genericSha = sha; }
            }
        }
        string page = release.TryGetProperty("html_url", out JsonElement h) && TrustedDownload(h.GetString())
            ? h.GetString()! : ReleasesPage;
        return exact is not null
            ? new UpdateInfo(latest, tag!, exact, page, exactSha)
            : new UpdateInfo(latest, tag!, generic, page, genericSha);
    }
}
