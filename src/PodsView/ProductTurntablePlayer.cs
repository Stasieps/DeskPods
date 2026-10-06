using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Image = System.Windows.Controls.Image;

namespace PodsView;

/// <summary>
/// Plays verified photo-derived frames, not a speculative WPF 3D scene. The original
/// photo is preserved, and the same atlas produces the downloadable motion preview.
/// No per-frame allocation, timers while hidden, or dependency on a 3D-capable driver.
/// </summary>
internal sealed class ProductTurntablePlayer : IDisposable
{
    private readonly Window _owner;
    private readonly Image _image;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _time = new();
    private BitmapSource[]? _frames;
    private bool _loading, _failed, _disposed, _reportedMotion;
    private bool _enabled = true;
    internal int CurrentFrame { get; private set; } = -1;
    internal bool IsReady => _frames is not null;
    internal bool IsRunning => _timer.IsEnabled;
    internal bool Failed => _failed;

    internal ProductTurntablePlayer(Window owner, Image image)
    {
        _owner = owner;
        _image = image;
        _timer = new DispatcherTimer(DispatcherPriority.Background, owner.Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(1000.0 / 30) };
        _timer.Tick += Tick;
        owner.Loaded += Loaded;
        owner.IsVisibleChanged += VisibleChanged;
        owner.StateChanged += StateChanged;
        owner.Closed += Closed;
        SystemParameters.StaticPropertyChanged += SystemSettingChanged;
    }

    private async void Loaded(object sender, RoutedEventArgs args)
    {
        if (_loading || _frames is not null || _failed || _disposed) return;
        _loading = true;
        try
        {
            BitmapSource[] frames = await Task.Run(LoadFrames);
            if (_disposed) return;
            _frames = frames;
            if (_enabled) PaintFrame(ProductMotion.FrameCount / 2);
            Logger.Info($"Product animation loaded: {frames.Length} frames, {ProductMotion.PeriodSeconds}s cycle");
            UpdatePlayback();
        }
        catch (Exception ex)
        {
            _failed = true;
            Logger.Error("Product animation unavailable; keeping the still photo", ex);
        }
        finally { _loading = false; }
    }

    private static BitmapSource[] LoadFrames()
    {
        var atlas = new BitmapImage();
        atlas.BeginInit();
        atlas.CacheOption = BitmapCacheOption.OnLoad;
        atlas.UriSource = new Uri("pack://application:,,,/PodsView;component/Assets/airpods-turn-atlas.png", UriKind.Absolute);
        atlas.EndInit();
        atlas.Freeze();
        int rows = (ProductMotion.FrameCount + ProductMotion.Columns - 1) / ProductMotion.Columns;
        if (atlas.PixelWidth != ProductMotion.Columns * ProductMotion.FrameSize || atlas.PixelHeight != rows * ProductMotion.FrameSize)
            throw new InvalidOperationException("Product frame atlas dimensions do not match its manifest");
        var frames = new BitmapSource[ProductMotion.FrameCount];
        for (int i = 0; i < frames.Length; i++)
        {
            var crop = new CroppedBitmap(atlas, new Int32Rect(
                i % ProductMotion.Columns * ProductMotion.FrameSize,
                i / ProductMotion.Columns * ProductMotion.FrameSize,
                ProductMotion.FrameSize, ProductMotion.FrameSize));
            crop.Freeze();
            frames[i] = crop;
        }
        return frames;
    }

    internal void SetEnabled(bool enabled)
    {
        if (_disposed || _enabled == enabled) return;
        _enabled = enabled;
        if (!enabled)
        {
            var photo = new BitmapImage(new Uri("pack://application:,,,/PodsView;component/Assets/airpods-case-open.png", UriKind.Absolute));
            photo.Freeze();
            _image.Source = photo;
            CurrentFrame = -1;
        }
        else if (_frames is not null) PaintFrame(ProductMotion.FrameAt(_time.Elapsed.TotalSeconds));
        UpdatePlayback();
    }

    private void VisibleChanged(object sender, DependencyPropertyChangedEventArgs args) => UpdatePlayback();
    private void StateChanged(object? sender, EventArgs args) => UpdatePlayback();
    private void SystemSettingChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_disposed || args.PropertyName != nameof(SystemParameters.ClientAreaAnimation)) return;
        if (_owner.Dispatcher.CheckAccess()) UpdatePlayback();
        else _ = _owner.Dispatcher.InvokeAsync(UpdatePlayback);
    }
    private void UpdatePlayback()
    {
        if (_disposed) return;
        if (!_enabled) { _timer.Stop(); _time.Stop(); return; }
        bool shouldRun = _frames is not null && _owner.IsVisible && _owner.WindowState != WindowState.Minimized
            && SystemParameters.ClientAreaAnimation;
        if (shouldRun)
        {
            _time.Start();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            _time.Stop();
            if (_frames is not null && !SystemParameters.ClientAreaAnimation) PaintFrame(ProductMotion.FrameCount / 2);
        }
    }
    private void Tick(object? sender, EventArgs args) => PaintFrame(ProductMotion.FrameAt(_time.Elapsed.TotalSeconds));
    private void PaintFrame(int index)
    {
        if (_frames is null || CurrentFrame == index) return;
        if (!_reportedMotion && CurrentFrame >= 0 && _timer.IsEnabled)
        {
            _reportedMotion = true;
            Logger.Info($"Product animation advanced: frame {CurrentFrame} -> {index}, visible={_owner.IsVisible}");
        }
        _image.Source = _frames[index];
        CurrentFrame = index;
    }
    private void Closed(object? sender, EventArgs args) => Dispose();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= Tick;
        _time.Stop();
        _owner.Loaded -= Loaded;
        _owner.IsVisibleChanged -= VisibleChanged;
        _owner.StateChanged -= StateChanged;
        _owner.Closed -= Closed;
        SystemParameters.StaticPropertyChanged -= SystemSettingChanged;
        _frames = null;
    }
}
