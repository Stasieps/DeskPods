using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace PodsView;

/// <summary>
/// Two files, one background writer.
///
///   podsview.log        what a human wants to read: start-up, popup shown/hidden, errors
///   podsview-trace.log  one structured line per radio packet and per popup decision
///
/// Until 0.8.7 every line was written straight from the caller: a lock, a directory
/// check, a FileInfo and a file opened and closed - on the Bluetooth callback thread and
/// on the UI thread, for every packet. Tracing therefore slowed down the very thing it
/// was measuring. Now callers only enqueue a string; one background thread does the I/O
/// in batches, and the queue is bounded so the app can never be dragged down by logging.
/// </summary>
internal static class Logger
{
    private const long MainLogLimit = 2_000_000;
    private const long TraceLogLimit = 6_000_000;
    private const int QueueLimit = 40_000;
    private const int BatchLimit = 400_000;

    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly System.Collections.Concurrent.ConcurrentQueue<(bool Trace, string Text, long Sequence)> Pending = new();
    private static readonly System.Threading.AutoResetEvent Signal = new(false);
    private static readonly object StartSync = new();
    private static System.Threading.Thread? _pump;
    private static volatile bool _stopping;
    private static int _dropped;
    private static readonly object QueueGate = new();
    private static readonly object FileGate = new();
    private static long _enqueued, _written;
    private static string _lastWriteError = string.Empty;

    public static string LogDirectory { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PodsView", "Logs");
    public static string LogPath => Path.Combine(LogDirectory, "podsview.log");
    public static string TracePath => Path.Combine(LogDirectory, "podsview-trace.log");

    internal static void UseDirectoryForVerification(string directory)
    {
        if (_pump is not null) throw new InvalidOperationException("Logger already started");
        LogDirectory = directory;
    }
    public static void Info(string message) => Enqueue(false, "INFO", message);
    public static void Debug(string message) => Enqueue(false, "DEBUG", message);
    public static void Error(string message, Exception exception) => Enqueue(false, "ERROR", $"{message} | {exception.GetType().Name}: {exception.Message}\n{exception.StackTrace}");

    /// <summary>One structured line per packet or popup decision. Never call from a loop that must be fast - it already is.</summary>
    public static void Trace(string message) => Enqueue(true, null, message);

    private static void Enqueue(bool trace, string? level, string message)
    {
        try
        {
            string stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
            lock (QueueGate)
            {
                if (_stopping) return;
                if (Pending.Count >= QueueLimit) { System.Threading.Interlocked.Increment(ref _dropped); return; }
                Pending.Enqueue((trace, level is null ? stamp + " " + message : stamp + " [" + level + "] " + message, ++_enqueued));
            }
            EnsurePump();
            Signal.Set();
        }
        catch { }
    }

    private static void EnsurePump()
    {
        if (_pump is not null) return;
        lock (StartSync)
        {
            if (_pump is not null) return;
            var thread = new System.Threading.Thread(Pump)
            {
                IsBackground = true,
                Name = "PodsView.Logger",
                Priority = System.Threading.ThreadPriority.BelowNormal
            };
            _pump = thread;
            thread.Start();
        }
    }

    private static void Pump()
    {
        var main = new StringBuilder();
        var trace = new StringBuilder();
        while (true)
        {
            Signal.WaitOne(500);
            main.Clear();
            trace.Clear();
            long completed = System.Threading.Interlocked.Read(ref _written);
            while (Pending.TryDequeue(out var line))
            {
                completed = line.Sequence;
                StringBuilder target = line.Trace ? trace : main;
                target.Append(line.Text).Append(Environment.NewLine);
                if (main.Length + trace.Length > BatchLimit) break;
            }

            int dropped = System.Threading.Interlocked.Exchange(ref _dropped, 0);
            if (dropped > 0) main.Append("[WARN] ").Append(dropped).Append(" log lines were dropped to keep the app responsive").Append(Environment.NewLine);
            if (dropped > 0)
                trace.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(" capture dropped=").Append(dropped).Append(Environment.NewLine);
            lock (FileGate)
            {
                if (main.Length > 0) Append(LogPath, main.ToString(), MainLogLimit);
                if (trace.Length > 0) Append(TracePath, trace.ToString(), TraceLogLimit);
                System.Threading.Interlocked.Exchange(ref _written, completed);
            }

            if (!Pending.IsEmpty) { Signal.Set(); continue; }
            if (_stopping) return;
        }
    }

    private static void Append(string path, string text, long limit)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var info = new FileInfo(path);
            if (info.Exists && info.Length > limit)
                File.Move(path, Path.Combine(LogDirectory, Path.GetFileNameWithoutExtension(path) + ".previous.log"), true);
            File.AppendAllText(path, text, Utf8);
        }
        catch (Exception ex) { _lastWriteError = ex.Message; }
    }

    /// <summary>Gives the writer a moment to drain. Used before reading the files back.</summary>
    public static bool Flush()
    {
        long target;
        lock (QueueGate) target = _enqueued;
        if (target == 0) return true;
        EnsurePump();
        Signal.Set();
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (System.Threading.Interlocked.Read(ref _written) < target && wait.ElapsedMilliseconds < 2000)
            System.Threading.Thread.Sleep(10);
        return System.Threading.Interlocked.Read(ref _written) >= target;
    }

    public static void Shutdown()
    {
        try
        {
            lock (QueueGate) _stopping = true;
            Flush();
            Signal.Set();
            _pump?.Join(2000);
        }
        catch { }
    }

    /// <summary>
    /// Exports allowlisted timing fields only. Personal settings and raw radio
    /// captures remain private in AppData. Review timestamps before sharing.
    /// </summary>
    public static string CollectDiagnostics()
    {
        bool flushed = Flush();
        string stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
        string staging = Path.Combine(Path.GetTempPath(), "PodsView-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);

        lock (FileGate)
        {
            if (Directory.Exists(LogDirectory))
                foreach (string file in Directory.GetFiles(LogDirectory, "*.log"))
                {
                    // Free-form main logs may contain paths and device aliases. Only the
                    // structured trace contributes, with all fields allowlisted.
                    if (!Path.GetFileName(file).StartsWith("podsview-trace", StringComparison.OrdinalIgnoreCase)) continue;
                    File.WriteAllLines(Path.Combine(staging, Path.GetFileName(file)),
                        File.ReadLines(file).Select(DiagnosticSanitizer.Sanitize).OfType<string>());
                }
            if (!flushed || _lastWriteError.Length > 0)
                File.WriteAllText(Path.Combine(staging, "capture-warning.txt"),
                    "Logs may be incomplete. Flush completed: " + flushed + ". See the private local log for details.");
        }
        File.WriteAllText(Path.Combine(staging, "README.txt"),
            "PodsView " + App.Version + " safe diagnostics. Only allowlisted timing fields are exported. " +
            "No Bluetooth addresses, raw manufacturer payloads, device aliases, settings.json or local paths. " +
            "Pre-parser message framing, rejection reasons, timer and WPF/native visibility flags are included; no full raw bytes. Visibility flags are not a physical screen measurement; since 0.8.39 the present/verify lines add one number (pixelDiff) comparing the screen under the card with the rendered card - no pixels are exported. Event timestamps remain: review before sharing. " +
            "OS: " + Environment.OSVersion.Version + "; Architecture: " + RuntimeInformation.ProcessArchitecture);

        // Which font families actually won on this machine, so a "fonts look the same"
        // report can be settled with the export instead of another round of guessing.
        try { File.WriteAllText(Path.Combine(staging, "fonts.txt"), FontProbe.Report); } catch { }

        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string folder = string.IsNullOrWhiteSpace(desktop) ? LogDirectory : desktop;
        Directory.CreateDirectory(folder);
        string archive = Path.Combine(folder, "PodsView-diagnostics-" + stamp + ".zip");
        if (File.Exists(archive)) File.Delete(archive);
        System.IO.Compression.ZipFile.CreateFromDirectory(staging, archive);
        try { Directory.Delete(staging, true); } catch { }
        Info("Diagnostics collected into " + archive);
        return archive;
    }
}

internal static class StartupManager
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PodsView";

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) is string value && value.Contains("PodsView", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    internal static void MigrateLegacyMinimizedArgument()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            if (key?.GetValue(ValueName) is not string value || !value.Contains("PodsView", StringComparison.OrdinalIgnoreCase)) return;
            string updated = System.Text.RegularExpressions.Regex.Replace(value, @"\s+--minimized\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (updated != value) key.SetValue(ValueName, updated);
        }
        catch (Exception ex) { Logger.Error("Could not migrate legacy startup argument", ex); }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(KeyPath);
            if (!enabled) key?.DeleteValue(ValueName, false);
            else
            {
                string? executable = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(executable) || Path.GetFileName(executable).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)) return false;
                key?.SetValue(ValueName, $"\"{executable}\"");
            }
            return true;
        }
        catch (Exception ex) { Logger.Error("Startup setting failed", ex); return false; }
    }
}

internal static class WindowAppearance
{
    public static void Apply(Window window)
    {
        try
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (window.AllowsTransparency)
            {
                // Windows 11: suppress any native border; custom WPF geometry owns the edge.
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                {
                    int noBorder = unchecked((int)0xFFFFFFFE);
                    _ = DwmSetWindowAttribute(handle, 34, ref noBorder, sizeof(int));
                }
                return;
            }
            var brush = window.TryFindResource("CanvasBrush") as System.Windows.Media.SolidColorBrush;
            int enabled = brush is null || brush.Color.R < 128 ? 1 : 0;
            _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                int rounded = 2;
                _ = DwmSetWindowAttribute(handle, 33, ref rounded, sizeof(int));
            }
        }
        catch { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);
}

internal static class TrayIconFactory
{
    /// <summary>
    /// Variant H: a neon AirPods case outline. Only two heavy strokes, so the
    /// shape survives the downscale to the 16 px the tray actually paints.
    /// The colour still carries the state: cyan when live, grey when nothing is
    /// connected, amber and red when the earbuds run low.
    /// </summary>
    public static Icon Create(int? battery, bool connected, bool live)
    {
        using var bitmap = new Bitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        Color accent = !connected ? Color.FromArgb(150, 158, 170)
            : battery is <= 20 ? Color.FromArgb(255, 91, 91)
            : battery is <= 40 ? Color.FromArgb(255, 190, 85)
            : Color.FromArgb(122, 245, 255);
        var body = new RectangleF(9f, 13f, 46f, 39f);
        using (GraphicsPath halo = RoundedRectangle(body, 15f))
        using (var glow = new Pen(Color.FromArgb(live ? 78 : 40, accent), 13f) { LineJoin = LineJoin.Round })
            graphics.DrawPath(glow, halo);
        using (GraphicsPath shell = RoundedRectangle(body, 15f))
        using (var stroke = new Pen(accent, 6.5f) { LineJoin = LineJoin.Round })
            graphics.DrawPath(stroke, shell);
        using (var hinge = new Pen(accent, 6f)) graphics.DrawLine(hinge, 11.5f, 32.5f, 52.5f, 32.5f);
        IntPtr handle = bitmap.GetHicon();
        try { using Icon temporary = Icon.FromHandle(handle); return (Icon)temporary.Clone(); }
        finally { DestroyIcon(handle); }
    }

    private static GraphicsPath RoundedRectangle(RectangleF rectangle, float radius)
    {
        float diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}

/// <summary>
/// One global shortcut shows the last known readings when no fresh case packet is available.
/// RegisterHotKey is used without a keyboard hook. The original working binding is retained
/// if another application owns the requested replacement.
/// </summary>
internal sealed class HotkeyService : IDisposable
{
    internal const string Default = AppSettings.DefaultHotkey;

    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0xB0D5;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    // Holding the keys down must not fire a burst of cards.
    private const uint ModNoRepeat = 0x4000;

    private readonly System.Windows.Window _owner;
    private readonly Action _callback;
    private System.Windows.Interop.HwndSource? _source;
    private bool _registered;
    private int _activeId = HotkeyId;
    private readonly Func<IntPtr, int, uint, uint, bool> _register;
    private readonly Func<IntPtr, int, bool> _unregister;
    internal string CurrentCombination => _combo;
    private string _combo = string.Empty;

    internal HotkeyService(System.Windows.Window owner, Action callback,
        Func<IntPtr, int, uint, uint, bool>? register = null, Func<IntPtr, int, bool>? unregister = null)
    {
        _owner = owner;
        _callback = callback;
        _register = register ?? RegisterHotKey;
        _unregister = unregister ?? UnregisterHotKey;
    }

    internal static string Normalize(string? combo) => AppSettings.NormalizeHotkey(combo);

    /// <summary>Reserve the replacement first. A conflict leaves the working shortcut untouched.</summary>
    internal bool Rebind(string? combo)
    {
        string next = Normalize(combo);
        if (_registered && string.Equals(next, _combo, StringComparison.OrdinalIgnoreCase)) return true;
        if (next.Length == 0)
        {
            Unregister();
            _combo = string.Empty;
            Logger.Info("Show-card shortcut is switched off");
            return true;
        }
        if (!TryParse(next, out uint modifiers, out uint key)) return false;
        try
        {
            IntPtr handle = new System.Windows.Interop.WindowInteropHelper(_owner).EnsureHandle();
            if (_source is null)
            {
                _source = System.Windows.Interop.HwndSource.FromHwnd(handle);
                if (_source is null) return false;
                _source.AddHook(Hook);
            }
            int nextId = _activeId == HotkeyId ? HotkeyId + 1 : HotkeyId;
            if (!_register(handle, nextId, modifiers | ModNoRepeat, key))
            {
                Logger.Info($"Shortcut '{next}' unavailable; keeping '{_combo}'");
                return false;
            }
            if (_registered && !_unregister(handle, _activeId))
            {
                _ = _unregister(handle, nextId);
                Logger.Info("Could not release the old shortcut; keeping its binding");
                return false;
            }
            _activeId = nextId;
            _registered = true;
            _combo = next;
            Logger.Info($"Show-card shortcut registered: {_combo}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("Could not register the show-card shortcut", ex);
            return false;
        }
    }

    /// <summary>Accepts "Ctrl+Alt+P". A bare key is refused: Windows needs a modifier.</summary>
    internal static bool TryParse(string combo, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;
        foreach (string part in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control": modifiers |= ModControl; break;
                case "alt": modifiers |= ModAlt; break;
                case "shift": modifiers |= ModShift; break;
                case "win": modifiers |= ModWin; break;
                default:
                    if (key != 0 || !Enum.TryParse(part, true, out System.Windows.Input.Key parsed)) return false;
                    key = (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(parsed);
                    break;
            }
        }
        return key != 0 && modifiers != 0;
    }

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == _activeId)
        {
            handled = true;
            try { _callback(); }
            catch (Exception ex) { Logger.Error("Show-card shortcut handler failed", ex); }
        }
        return IntPtr.Zero;
    }

    private void Unregister()
    {
        try
        {
            if (_registered)
            {
                IntPtr handle = new System.Windows.Interop.WindowInteropHelper(_owner).Handle;
                if (handle != IntPtr.Zero) _ = _unregister(handle, _activeId);
            }
            _source?.RemoveHook(Hook);
        }
        catch (Exception ex) { Logger.Error("Could not release the show-card shortcut", ex); }
        _registered = false;
        _source = null;
    }

    public void Dispose() => Unregister();

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

/// <summary>
/// Resolves the font tokens against the fonts that are actually installed.
///
/// 0.8.18 shipped font tokens that named optional families - "Cascadia Mono" and
/// "Segoe UI Variable Text" are Windows 11 extras. On a machine without them WPF
/// walks the fallback list silently, lands on the system default, and the change is
/// invisible: the fonts simply look the same. Guessing which fonts
/// a machine has is the same mistake as guessing what the radio said, so this probes
/// the installed set, writes the winner into the same resource keys, and logs it.
/// The log line is the proof of what is on screen.
/// </summary>
internal static class FontProbe
{
    // Key, then families in order of preference. The last entry of every row exists
    // on every supported Windows, so there is always a real answer.
    private static readonly string[][] Candidates =
    {
        // 0.8.22: the name is set in Franklin Gothic Medium - a real designed medium
        // weight instead of the system font asked for a heavier one - with Corbel for
        // text and Consolas for the digits. All three ship with Windows, so this list
        // cannot resolve to "nothing changed" the way 0.8.18 did with two Windows-11-only
        // faces. Bahnschrift was the obvious DIN-style candidate and is deliberately not
        // here: it is a variable font, WPF resolves a single static instance of it, and a
        // weight request then returns a face nobody chose.
        new[] { "DisplayFont", "Segoe UI", "Arial" },
        new[] { "UiFont", "Segoe UI", "Arial" },
        new[] { "MonoFont", "Consolas", "Courier New" }
    };

    /// <summary>The families chosen last time Apply ran, for the diagnostics export.</summary>
    internal static string Report { get; private set; } = "not probed";

    internal static void Apply(System.Windows.ResourceDictionary resources)
    {
        try
        {
            var installed = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Windows.Media.FontFamily family in System.Windows.Media.Fonts.SystemFontFamilies)
            {
                foreach (string name in family.FamilyNames.Values) installed.Add(name);
                string source = family.Source;
                if (source.Length > 0) installed.Add(source);
            }

            var chosen = new System.Collections.Generic.List<string>(Candidates.Length);
            foreach (string[] row in Candidates)
            {
                string key = row[0];
                string pick = row[row.Length - 1];
                for (int i = 1; i < row.Length; i++)
                {
                    if (!installed.Contains(row[i])) continue;
                    pick = row[i];
                    break;
                }
                resources[key] = new System.Windows.Media.FontFamily(pick);
                chosen.Add(key + "=" + pick);
            }

            Report = string.Join(", ", chosen);
            Logger.Info($"Fonts resolved: {Report} (families installed: {installed.Count})");
        }
        catch (Exception ex)
        {
            // A failed probe must leave the XAML defaults alone, not blank the app.
            Report = "probe failed";
            Logger.Error("Font probe failed", ex);
        }
    }
}
