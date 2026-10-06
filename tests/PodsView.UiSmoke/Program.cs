using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PodsView;

internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void CheckBrandResources()
    {
        Check(Application.ResourceAssembly == typeof(Program).Assembly,
            "Resource regression must run under the foreign UiSmoke entry assembly");
        var uri = new Uri("pack://application:,,,/PodsView;component/Assets/deskpods.ico", UriKind.Absolute);
        var info = Application.GetResourceStream(uri);
        Check(info is not null, "Assembly-qualified approved WPF icon resource is missing");
        using var resource = info!.Stream;
        using var wpfBytes = new MemoryStream();
        resource.CopyTo(wpfBytes);
        using var embedded = typeof(App).Assembly.GetManifestResourceStream("DeskPods.Brand.Icon");
        Check(embedded is not null, "Approved tray resource is missing");
        using var trayBytes = new MemoryStream();
        embedded!.CopyTo(trayBytes);
        Check(wpfBytes.ToArray().SequenceEqual(trayBytes.ToArray()), "WPF and tray artwork differ");
        wpfBytes.Position = 0;
        var frame = BitmapFrame.Create(wpfBytes, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Check(frame.PixelWidth > 0 && frame.PixelHeight > 0, "Approved WPF icon cannot decode");
        Console.WriteLine("DeskPods WPF icon: explicit application resource resolved under UiSmoke");
    }
    [STAThread]
    private static int Main(string[] args)
    {
        string folder = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "PodsView-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        App.IsVerificationRun = true;
        Logger.UseDirectoryForVerification(Path.Combine(folder, "logs"));
        App? app = null;
        MainWindow? main = null;
        CasePopupWindow? popup = null;
        try
        {
            app = new App();
            app.InitializeComponent();
            CheckBrandResources();
            ThemeManager.Apply("mono");
            app.Settings.LastCaseBattery = 40;
            main = new MainWindow { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
            main.Show();
            PumpUntil(() => main.ProductPlayerForVerification.IsReady || main.ProductPlayerForVerification.Failed, 10000);
            Check(main.ProductPlayerForVerification.IsReady, "Frame atlas did not load");
            Check(main.Width == 540 && main.Height == 244, "Main geometry changed");
            Check(((TextBlock)main.FindName("VersionLabel")).Text == "v" + App.Version, "Version chip disagrees with assembly");
            if (SystemParameters.ClientAreaAnimation)
            {
                int first = main.ProductPlayerForVerification.CurrentFrame;
                Pump(750);
                Check(first != main.ProductPlayerForVerification.CurrentFrame, "Visible product image did not advance");
            }
            else Console.WriteLine("   motion progress skipped: Windows animations disabled; still frame verified");
            main.Hide();
            int stopped = main.ProductPlayerForVerification.CurrentFrame;
            Pump(150);
            Check(!main.ProductPlayerForVerification.IsRunning && stopped == main.ProductPlayerForVerification.CurrentFrame, "Hidden animation kept running");
            main.Show();
            main.WindowState = WindowState.Minimized;
            Pump(100);
            Check(!main.ProductPlayerForVerification.IsRunning, "Minimized animation kept running");
            main.WindowState = WindowState.Normal;
            Pump(100);

            popup = new CasePopupWindow();
            popup.Prepare();
            var data = new ParsedAirPodsData("AirPods Pro", 90, 100, 40, false, false, true, 1, true);
            var packet = new AirPodsPacket(data, DateTimeOffset.UtcNow, -55, 1, "", DeviceKey: "AirPods Pro/00");
            foreach (string theme in AppSettings.ThemeIds)
            {
                ThemeManager.Apply(theme);
                var status = DeviceStatus.Initial with { Mode = MonitorMode.Connected, IsPaired = true, IsConnected = true };
                main.ApplyPacket(packet with { ReceivedAt = DateTimeOffset.UtcNow }, status);
                Check(((TextBlock)main.FindName("CompactCaseValueText")).Text == "45 %", "Wrong displayed case value");
                main.ApplyPacket(packet with { Data = data with { CaseCached = true }, ReceivedAt = DateTimeOffset.UtcNow }, status);
                var caseText = (TextBlock)main.FindName("CompactCaseValueText");
                Check(caseText.Foreground.ToString() == main.FindResource("SecondaryTextBrush").ToString(), theme + ": cached charge painted live");
                popup.ShowManual(data with { CaseCached = true }, "AirPods Pro");
                Check(((TextBlock)popup.FindName("PopupCaseValueText")).Text == "45 %", "Manual card lost remembered case");
                Check(((TextBlock)popup.FindName("PopupCaseLabelText")).Text == "C LAST", "Manual card claimed live case data");
                popup.HideForSignalTimeout();
                var settings = new SettingsWindow(new AppSettings { Theme = theme })
                { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
                settings.Show();
                settings.UpdateLayout();
                Check(settings.Icon is not null, "Settings icon did not resolve");
                var foreground = ((SolidColorBrush)app.Resources["CanvasBrush"]).Color;
                var background = ((SolidColorBrush)app.Resources["PrimaryTextBrush"]).Color;
                Check(Contrast(foreground, background) >= 4.5, theme + ": Save contrast is below 4.5:1");
                Capture(main, Path.Combine(folder, "main-" + theme + ".png"));
                Capture(settings, Path.Combine(folder, "settings-" + theme + ".png"));
                Check(settings.FindName("PrivacyText") is null, "Obsolete PRIVATE badge remains");
                Check(((TextBlock)settings.FindName("TitleText")).FontFamily.Source.Contains("Segoe UI"), "Settings title is not UI sans");
                Check(((System.Windows.Controls.Button)settings.FindName("SupportButton")).IsEnabled == (SupportLink.Read() is not null), "Support button state disagrees with public URL");
                popup.ShowManual(data with { ModelCode = 0x1420 }, "renamed headphones");
                Check(((TextBlock)popup.FindName("PopupDeviceNameText")).Text == "AirPods Pro 2", "Popup erased generation");
                Capture(popup, Path.Combine(folder, "popup-" + theme + ".png"));
                popup.HideForSignalTimeout();
                settings.Close();
            }
            ThemeManager.Apply("refined");
            foreach (string language in new[] { "uk", "ru", "en" })
            {
                var settings = new SettingsWindow(new AppSettings { Language = language, Theme = "refined" })
                    { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
                settings.Show(); settings.UpdateLayout();
                Capture(settings, Path.Combine(folder, "settings-refined-" + language + ".png"));
                Check(settings.ActualHeight <= Math.Max(400, SystemParameters.WorkArea.Height - 48), "Settings exceeds work area");
                settings.Close();
            }
            Pump(100);
            Check(main.RefinedForVerification, "Refined appearance was not applied");
            Check(!main.ProductPlayerForVerification.IsRunning, "Refined must not run the rejected photo animation");
            Check(((Border)main.FindName("AppearanceFrame")).CornerRadius.TopLeft == 12, "Refined corners differ from the approved design");
            Check(((Grid)main.FindName("AppearanceContent")).Clip is RectangleGeometry, "Rounded content clip is missing");
            foreach (string iconTheme in AppSettings.ThemeIds)
            {
                ThemeManager.Apply(iconTheme);
                foreach (bool lightTaskbar in new[] { false, true })
                {
                    TrayAppearance.LightTaskbarForVerification = lightTaskbar;
                    Check(TrayAppearance.Key == (lightTaskbar ? "deskpods-tray-dp13-dark" : "deskpods-tray-dp13-light"), iconTheme + ": tray cache key does not follow the taskbar tone");
                    foreach (int? level in new int?[] { null,10,30,90 })
                    foreach (bool connected in new[] { false,true })
                    foreach (bool live in new[] { false,true })
                    {
                        using var icon = TrayAppearance.Create(level,connected,live);
                        Check(icon.Width > 0 && icon.Height > 0, "Case tray icon could not load");
                        string tone = lightTaskbar ? "dark" : "light";
                        string state = !connected ? "disconnected" : level is <= 20 ? "low" : level is <= 40 ? "warning" : "normal";
                        using Stream source = typeof(App).Assembly.GetManifestResourceStream(TrayAppearance.ResourceName(lightTaskbar))
                            ?? throw new InvalidOperationException("Expected dp13 tray resource is missing");
                        using var expected = new System.Drawing.Icon(source,icon.Width,icon.Height);
                        using var actualPixels = icon.ToBitmap();
                        using var expectedPixels = expected.ToBitmap();
                        bool equal = actualPixels.Width == expectedPixels.Width && actualPixels.Height == expectedPixels.Height;
                        for (int y = 0; equal && y < actualPixels.Height; y++)
                        for (int x = 0; equal && x < actualPixels.Width; x++)
                            equal = actualPixels.GetPixel(x,y).ToArgb() == expectedPixels.GetPixel(x,y).ToArgb();
                        Check(equal, iconTheme + ": tray pixels do not match the dp13 tray icon");
                    }
                }
            }
            // .34: real loaded WPF layout, named controls and alpha, not a mockup.
            Check(popup.Width == 272 && popup.Height == 120, "34: popup geometry");
            var close34=(System.Windows.Controls.Button)popup.FindName("PopupCloseButton");
            Check(close34.Width>=24 && close34.Height>=24,"34: close target too small");
            Check(((TextBlock)popup.FindName("PopupLeftLabelText")).FontSize>=11,"34: unreadable popup labels");
            Check(!string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(close34)),"34: close has no accessible name");
            Check(!string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName((DependencyObject)main.FindName("ShellSettingsButton"))),"34: settings has no accessible name");
            foreach(string language34 in new[]{"uk","ru","en"})
            {
                app.Settings.Language=language34;
                popup.ShowManual(data with {ModelCode=0x1420},"AirPods Pro 2");popup.UpdateLayout();
                Check(((System.Windows.Shapes.Ellipse)popup.FindName("PopupStatusDot")).Visibility==Visibility.Collapsed,"34: cached manual card claims live status");
                foreach(string textName in new[]{"PopupDeviceNameText","PopupOpenText"})
                {
                    var text34=(TextBlock)popup.FindName(textName);
                    var measure34=new TextBlock {Text=text34.Text,FontFamily=text34.FontFamily,FontSize=text34.FontSize,FontWeight=text34.FontWeight};
                    measure34.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));
                    Check(measure34.DesiredSize.Width<=text34.ActualWidth+1,"34: localized popup heading clipped: "+language34+" "+textName);
                }
                Capture(popup,Path.Combine(folder,"popup34-"+language34+".png"));popup.HideForSignalTimeout();
            }
            using(var icon34=typeof(App).Assembly.GetManifestResourceStream("DeskPods.Brand.Icon"))
            using(var decoded34=new System.Drawing.Icon(icon34!,32,32))
            using(var bitmap34=decoded34.ToBitmap())
            {
                Check(bitmap34.GetPixel(0,0).A==0 && bitmap34.GetPixel(31,31).A==0,"34: opaque exterior icon background returned");
            }
            Console.WriteLine("DeskPods 0.8.37: readable popup and transparent icon verified");
            Check(main.Title == "DeskPods", "Main window brand is not DeskPods");
            Check(main.Icon is not null, "Main window is missing the approved icon");
            Console.WriteLine("DeskPods tray r4: approved logo and WPF resources verified");
            TrayAppearance.LightTaskbarForVerification = null;
            ThemeManager.Apply("mono");
            Pump(100);
            Check(!main.RefinedForVerification, "Classic appearance did not return");
            Check(((Border)main.FindName("AppearanceFrame")).CornerRadius.TopLeft == 16, "Classic corners were lost");
            Check(((Border)main.FindName("AppearanceLeftTrack")).Height == 4, "Classic track height was not restored");
            Check(((Grid)main.FindName("AppearanceLeftRow")).ColumnDefinitions[3].Width.Value == 84, "Classic value width was not restored");
            var registrations = new Dictionary<int, uint>();
            bool allow = true;
            using (var hotkey = new HotkeyService(popup, () => { },
                (handle, id, mods, key) => { if (!allow) return false; registrations.Add(id, key); return true; },
                (handle, id) => registrations.Remove(id)))
            {
                Check(hotkey.Rebind("Ctrl+Alt+P"), "Initial simulated shortcut failed");
                allow = false;
                Check(!hotkey.Rebind("Ctrl+Alt+Q") && hotkey.CurrentCombination == "Ctrl+Alt+P" && registrations.Count == 1,
                    "Conflict destroyed the previous shortcut");
                allow = true;
                Check(hotkey.Rebind("Ctrl+Alt+Q") && registrations.Count == 1, "Successful rebind left duplicate registrations");
                Check(hotkey.Rebind("") && registrations.Count == 0, "Disabling shortcut leaked registration");
            }
            popup.HideForSignalTimeout();
            Pump(50);
            Parallel.For(0, 1000, i => Logger.Info("flush-check-" + i.ToString("D4")));
            Check(Logger.Flush(), "Logger did not finish queued writes");
            string trace = File.ReadAllText(Logger.TracePath);
            Check(trace.Contains("presentation phase=show-return"), "Presentation show-return missing");
            Check(trace.Contains("presentation phase=hide-return"), "Presentation hide-return missing");
            Check(trace.Contains("presentation phase=visibility-changed"), "WPF visibility event missing");
            Check(trace.Contains("presentation phase=settled"), "Deferred presentation snapshot missing");
            // 0.8.39: hiding parks the card far outside every monitor instead of hiding the
            // window, so a hidden card is still a native visible window on purpose. The
            // signal for "the user cannot see it" is IsShown plus the parked position.
            Check(!popup.IsShown && popup.ParkedForVerification, "Popup was not parked after hide");
            Check(popup.ParkedModeForVerification
                ? popup.NativeVisibilityForVerification == 1
                : popup.NativeVisibilityForVerification == 0, "Parked popup lost its native window");
            Check(trace.Split('\n').Any(line => line.Contains("presentation phase=hide-return") && line.Contains("parked=1")), "No observed park in trace");
            Check(trace.Contains("present phase=hide"), "Presentation hide trace missing");
            string log = File.ReadAllText(Logger.LogPath);
            for (int i = 0; i < 1000; i++) Check(log.Contains("flush-check-" + i.ToString("D4")), "Flush returned before a line was written");
            Console.WriteLine($"PodsView WPF smoke: PASS ({_checks} assertions). Screenshots: {folder}");
            Console.WriteLine("Bluetooth, startup registration and real global shortcuts were not exercised.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            popup?.CloseForExit();
            main?.Close();
            app?.Shutdown();
            Logger.Shutdown();
        }
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
    private static void PumpUntil(Func<bool> finished, int limit)
    {
        var time = Stopwatch.StartNew();
        while (!finished() && time.ElapsedMilliseconds < limit) Pump(25);
    }
    private static void Capture(Window window, string file)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file);
        encoder.Save(stream);
    }
    private static double Contrast(Color a, Color b)
    {
        static double L(Color c)
        {
            static double Channel(byte value) { double s = value / 255d; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }
        double first = L(a), second = L(b);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }
}
