using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace PodsView;

/// <summary>
/// 0.8.46: asks GitHub Releases whether a newer DeskPods exists and, when the user agrees,
/// downloads its setup and runs it silently. It is the app's only network request; it sends
/// nothing but the version in the User-Agent, and it can be switched off in Settings.
/// </summary>
internal static class UpdateService
{
    internal const string LatestApi = "https://api.github.com/repos/" + UpdateRelease.Repository + "/releases/latest";
    internal const string ReleasesPage = UpdateRelease.ReleasesPage;
    // /SP- skips the "this will install" prompt, /SILENT shows only the progress bar, and the
    // setup's own [Run] entry for silent mode starts the new build when it is done.
    internal const string InstallerArguments = "/SP- /SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS";

    private static HttpClient? _http;
    private static HttpClient Http => _http ??= CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DeskPods/" + App.Version);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>The newer release, or null when this build is current or GitHub is unreachable.</summary>
    internal static async Task<UpdateInfo?> CheckAsync(CancellationToken cancel = default)
    {
        using HttpResponseMessage response = await Http.GetAsync(LatestApi, cancel).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"GitHub answered {(int)response.StatusCode}");
        await using Stream body = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        using JsonDocument json = await JsonDocument.ParseAsync(body, cancellationToken: cancel).ConfigureAwait(false);
        return UpdateRelease.Pick(json.RootElement, App.Version);
    }

    /// <summary>Downloads the setup into %TEMP%\DeskPods-update and returns its path.</summary>
    internal static async Task<string> DownloadAsync(UpdateInfo info, IProgress<int>? progress, CancellationToken cancel = default)
    {
        if (!UpdateRelease.TrustedDownload(info.InstallerUrl)) throw new InvalidOperationException("The release has no installer");
        string folder = Path.Combine(Path.GetTempPath(), "DeskPods-update");
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, $"DeskPods_Setup_v{info.Display}.exe");
        string partial = target + ".part";

        using HttpResponseMessage response = await Http.GetAsync(info.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        await using (Stream source = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false))
        await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            long done = 0; int lastPercent = -1, read;
            while ((read = await source.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                done += read;
                if (total is > 0)
                {
                    int percent = (int)(done * 100 / total.Value);
                    if (percent != lastPercent) { lastPercent = percent; progress?.Report(percent); }
                }
            }
        }

        // A setup is a Windows executable: anything else (an HTML error page, a cut-off
        // download) is thrown away instead of being run.
        var bytes = new byte[2];
        using (var check = File.OpenRead(partial))
        {
            if (check.Length < 512 * 1024 || check.Read(bytes, 0, 2) != 2 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
                throw new InvalidDataException("The downloaded file is not a setup program");
        }
        File.Move(partial, target, overwrite: true);
        return target;
    }

    /// <summary>
    /// True for a copy put in place by the setup (Inno writes unins000.exe next to it). A
    /// portable or self-built copy is not replaced behind the user's back: it gets the
    /// release page instead, since a silent setup would install a second, separate copy.
    /// </summary>
    internal static bool IsInstalledCopy
    {
        get
        {
            try { return File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe")); }
            catch { return false; }
        }
    }

    internal static void RunInstaller(string path) =>
        Process.Start(new ProcessStartInfo(path, InstallerArguments) { UseShellExecute = true });

    internal static void OpenReleasePage(string? url = null)
    {
        try { Process.Start(new ProcessStartInfo(UpdateRelease.TrustedDownload(url) ? url! : ReleasesPage) { UseShellExecute = true }); }
        catch (Exception ex) { Logger.Error("Release page could not be opened", ex); }
    }
}
