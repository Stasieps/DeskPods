using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace PodsView;

internal sealed class PopupPresentationProbe
{
    private readonly Window _window;
    // 0.8.39: the card is a permanently visible window parked outside every monitor, so
    // "popup" now reports the app's own shown flag, and "parked" says where the window is.
    private readonly Func<bool> _shown;
    private readonly Func<bool> _parked;
    private readonly record struct Context(long Op, long Seq, int Peer, string Reason);
    private Context _context = new(0,0,0,"initial");
    internal PopupPresentationProbe(Window window, Func<bool>? shown = null, Func<bool>? parked = null)
    {
        _window = window;
        _shown = shown ?? (() => window.IsVisible);
        _parked = parked ?? (() => false);
        _window.IsVisibleChanged += (_, _) => Snapshot("visibility-changed", _context);
    }
    internal void Begin(string reason, long sequence=0, int peer=0) =>
        _context = new(_context.Op+1,sequence,peer,reason);
    internal int NativeVisibility
    {
        get
        {
            try
            {
                var handle = new WindowInteropHelper(_window).Handle; // Never EnsureHandle here.
                return handle == IntPtr.Zero ? -1 : IsWindowVisible(handle) ? 1 : 0;
            }
            catch { return -1; }
        }
    }
    internal void Complete(string phase)
    {
        Context context = _context;
        Snapshot(phase,context);
        try
        {
            if (!_window.Dispatcher.HasShutdownStarted)
                _ = _window.Dispatcher.InvokeAsync(() => Snapshot("settled",context),DispatcherPriority.Loaded);
        }
        catch { } // Diagnostics must never prevent a show/hide or exit.
    }
    private void Snapshot(string phase, Context context)
    {
        try
        {
            Logger.Trace($"presentation phase={phase} reason={context.Reason} op={context.Op} seq={context.Seq} peer={context.Peer} superseded={(context.Op != _context.Op ? 1 : 0)} popup={(Safe(_shown) ? 1 : 0)} parked={(Safe(_parked) ? 1 : 0)} nativeVisible={NativeVisibility} opacityPct={(int)Math.Round(_window.Opacity*100)}");
        }
        catch { }
    }
    private static bool Safe(Func<bool> read) { try { return read(); } catch { return false; } }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);
}
