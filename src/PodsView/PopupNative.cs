using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PodsView;

/// <summary>
/// 0.8.39: native facts about the popup that WPF cannot report. Everything here is
/// read-only measurement except <see cref="RaiseTopmost"/> and the DWM corner setup.
///
/// Why it exists: the 2026-09-25 trace showed the popup "visible" to WPF and to
/// IsWindowVisible for 5.3 s after the first opening that followed four idle hours,
/// while the user saw nothing on screen. Those flags only say that Windows was asked
/// to show the window. This class answers the questions they cannot: is the window
/// cloaked by DWM, is it really the top window at its own position, and do the
/// pixels on the screen at that position look like the card.
/// </summary>
internal static class PopupNative
{
    private const int GwlExStyle = -20;
    private const int WsExTopmost = 0x00000008;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const int DwmwaCloaked = 14;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const uint GaRoot = 2;

    /// <summary>Puts the window at the top of the topmost band without activating it.
    /// WPF's <c>Topmost = true</c> is a no-op when the property is already true, so it
    /// never re-raises a window that another topmost window has covered since.</summary>
    internal static bool RaiseTopmost(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try { return SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow | SwpNoOwnerZOrder); }
        catch { return false; }
    }

    /// <summary>Windows 11: DWM rounds the corners of the now non-layered card and draws
    /// no accent border of its own; the WPF rim stays the only edge.</summary>
    internal static void ApplyCardCorners(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        try
        {
            int round = 2; // DWMWCP_ROUND
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));
            int noBorder = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
            _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref noBorder, sizeof(int));
        }
        catch { }
    }

    /// <summary>1 when DWM hides the window (another virtual desktop, shell cloak), 0 when
    /// not, -1 when unknown.</summary>
    internal static int Cloaked(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return -1;
        try { return DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int value, sizeof(int)) == 0 ? (value != 0 ? 1 : 0) : -1; }
        catch { return -1; }
    }

    internal static int Topmost(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return -1;
        try { return (GetWindowLong(hwnd, GwlExStyle) & WsExTopmost) != 0 ? 1 : 0; }
        catch { return -1; }
    }

    /// <summary>How many of five points inside the card resolve to the card itself. 5 means
    /// nothing covers it; 0 means another window sits on top or the card is not there.</summary>
    internal static int HitCount(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out Rect32 r)) return -1;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return -1;
        var points = new (int X, int Y)[]
        {
            (r.Left + w / 2, r.Top + h / 2), (r.Left + w / 5, r.Top + h / 3), (r.Right - w / 5, r.Top + h / 3),
            (r.Left + w / 5, r.Bottom - h / 4), (r.Right - w / 5, r.Bottom - h / 4)
        };
        int hits = 0;
        try
        {
            foreach (var p in points)
            {
                IntPtr at = WindowFromPoint(new Point32 { X = p.X, Y = p.Y });
                if (at != IntPtr.Zero && (at == hwnd || GetAncestor(at, GaRoot) == hwnd)) hits++;
            }
        }
        catch { return -1; }
        return hits;
    }

    /// <summary>1 when the window rectangle lies at least half on a real monitor.</summary>
    internal static int OnScreen(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out Rect32 r)) return -1;
        try
        {
            IntPtr monitor = MonitorFromRect(ref r, 0 /* MONITOR_DEFAULTTONULL */);
            if (monitor == IntPtr.Zero) return 0;
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info)) return -1;
            int ix = Math.Max(0, Math.Min(r.Right, info.Monitor.Right) - Math.Max(r.Left, info.Monitor.Left));
            int iy = Math.Max(0, Math.Min(r.Bottom, info.Monitor.Bottom) - Math.Max(r.Top, info.Monitor.Top));
            long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            return area > 0 && ix * (long)iy * 2 >= area ? 1 : 0;
        }
        catch { return -1; }
    }

    internal static int Dpi(IntPtr hwnd)
    {
        try { return hwnd == IntPtr.Zero ? -1 : (int)GetDpiForWindow(hwnd); }
        catch { return -1; }
    }

    /// <summary>
    /// The physical measurement the old trace admitted it did not have. The screen area
    /// under the card is copied and compared with what WPF says the card looks like,
    /// both reduced to a 16x8 grid of average colours. Returns the mean channel
    /// difference (0 = identical, 255 = opposite), or -1 when the screen could not be
    /// read (locked session, secure desktop). Only this one number leaves the method:
    /// no pixels are stored or logged.
    /// </summary>
    internal static int PixelDiff(Window window, IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out Rect32 r)) return -1;
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w < 16 || h < 8 || w > 4000 || h > 4000) return -1;

            double[] screen;
            using (var bitmap = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = System.Drawing.Graphics.FromImage(bitmap))
                    g.CopyFromScreen(r.Left, r.Top, 0, 0, new System.Drawing.Size(w, h),
                        System.Drawing.CopyPixelOperation.SourceCopy | System.Drawing.CopyPixelOperation.CaptureBlt);
                var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try
                {
                    var bytes = new byte[data.Stride * h];
                    Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                    screen = Grid(bytes, data.Stride, w, h);
                }
                finally { bitmap.UnlockBits(data); }
            }

            double scale = Dpi(hwnd) is int dpi && dpi > 0 ? dpi : 96;
            var target = new RenderTargetBitmap(w, h, scale, scale, PixelFormats.Pbgra32);
            target.Render(window);
            int stride = w * 4;
            var expected = new byte[stride * h];
            target.CopyPixels(expected, stride, 0);
            double[] wanted = Grid(expected, stride, w, h);

            double sum = 0;
            for (int i = 0; i < screen.Length; i++) sum += Math.Abs(screen[i] - wanted[i]);
            return (int)Math.Round(sum / screen.Length);
        }
        catch { return -1; }
    }

    private static double[] Grid(byte[] bgra, int stride, int w, int h)
    {
        const int Gx = 16, Gy = 8;
        var cells = new double[Gx * Gy * 3];
        var counts = new int[Gx * Gy];
        // A 2 px inset keeps DWM's rounded corners and the 1 px rim out of the average.
        for (int y = 2; y < h - 2; y += 2)
        {
            int cy = Math.Min(Gy - 1, y * Gy / h);
            for (int x = 2; x < w - 2; x += 2)
            {
                int cx = Math.Min(Gx - 1, x * Gx / w);
                int cell = cy * Gx + cx, o = y * stride + x * 4;
                cells[cell * 3] += bgra[o]; cells[cell * 3 + 1] += bgra[o + 1]; cells[cell * 3 + 2] += bgra[o + 2];
                counts[cell]++;
            }
        }
        for (int c = 0; c < counts.Length; c++)
            if (counts[c] > 0) { cells[c * 3] /= counts[c]; cells[c * 3 + 1] /= counts[c]; cells[c * 3 + 2] /= counts[c]; }
        return cells;
    }

    /// <summary>Hard page faults and working set, to test the "Windows paged the idle app
    /// out" explanation for a slow first frame. Numbers only.</summary>
    internal static (long Faults, long WorkingSetMb) Memory()
    {
        try
        {
            var counters = new ProcessMemoryCounters { Cb = (uint)Marshal.SizeOf<ProcessMemoryCounters>() };
            if (K32GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.Cb))
                return (counters.PageFaultCount, (long)(counters.WorkingSetSize.ToUInt64() / (1024 * 1024)));
        }
        catch { }
        return (-1, -1);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect32 { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point32 { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect32 Monitor; public Rect32 Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Cb; public uint PageFaultCount; public UIntPtr PeakWorkingSetSize; public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage; public UIntPtr QuotaPagedPoolUsage; public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage; public UIntPtr PagefileUsage; public UIntPtr PeakPagefileUsage;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out Rect32 rect);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point32 point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref Rect32 rect, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool K32GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCounters counters, uint size);
}

/// <summary>
/// 0.8.39: Windows 11 may put a background process with no foreground window into
/// EcoQoS ("efficiency mode") and coalesce its timers. A tray app whose whole job is to
/// react within milliseconds to a radio packet is the opposite of that workload, so the
/// process opts out explicitly. Failure is harmless and logged.
/// </summary>
internal static class PowerThrottling
{
    private const int ProcessPowerThrottling = 4;
    private const uint ExecutionSpeed = 0x1;
    private const uint IgnoreTimerResolution = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct State { public uint Version; public uint ControlMask; public uint StateMask; }

    internal static bool OptOut()
    {
        try
        {
            // ControlMask = the policies we take charge of, StateMask 0 = switched off.
            var state = new State { Version = 1, ControlMask = ExecutionSpeed | IgnoreTimerResolution, StateMask = 0 };
            return SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<State>());
        }
        catch { return false; }
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref State info, uint size);
}
