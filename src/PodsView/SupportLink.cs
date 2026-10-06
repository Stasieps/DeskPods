using System.IO;
using System.Text.RegularExpressions;

namespace PodsView;
internal static class SupportLink
{
    // Optional creator-supplied public link, never credentials. Not part of personal settings.
    internal static string? Read()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "support-url.txt");
            return File.Exists(path) ? Normalize(File.ReadAllText(path)) : null;
        }
        catch { return null; }
    }
    internal static string? Normalize(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "ko-fi.com"
            || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || !Regex.IsMatch(uri.AbsolutePath, @"^/[A-Za-z0-9_]{1,64}/?$")) return null;
        return uri.AbsoluteUri;
    }
}
