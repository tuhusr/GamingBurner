using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace GamingBurner;

public static class Program
{
    public static readonly string DataDir = AppPaths.LogsDir;

    [STAThread]
    public static int Main(string[] args)
    {
        // Must run first: lets the installer and updater hook into install, update and uninstall.
        Velopack.VelopackApp.Build().Run();
        AppPaths.MigrateOldSettings();
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        Directory.CreateDirectory(DataDir);
        // Run below normal priority so this app always yields the processor to the game. Frame
        // timestamps are taken by Windows at the moment of each frame, so this costs no accuracy.
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

        if (args.Contains("--probe"))
        {
            Console.WriteLine("elevated: " + HwMonitor.IsElevated);
            using var hw = new HwMonitor();
            Console.WriteLine("open: " + hw.Open() + " " + hw.Error);
            Thread.Sleep(1200);
            hw.Update();
            foreach (var line in hw.Describe()) Console.WriteLine(line);
            Console.WriteLine($"cpu temp {hw.CpuTemp}  gpu hotspot {hw.GpuHotspot}  fan rpm {hw.GpuFanRpm}  can control fan {hw.CanControlFan}");
            var tune = new GpuTuning();
            tune.Init();
            Console.WriteLine("clocks: " + tune.Describe());
            Console.WriteLine($"fan: init {tune.InitFan()} range {tune.FanMinPct}-{tune.FanMaxPct}% {tune.FanError}");
            if (args.Contains("--fan-test") && tune.FanAvailable)
            {
                // Briefly holds the fans at 60%, then returns them to automatic.
                Console.WriteLine("set 60%: " + tune.SetFan(60) + " " + tune.FanError);
                for (int i = 0; i < 5; i++) { Thread.Sleep(1000); hw.Update(); Console.WriteLine($"  rpm {hw.GpuFanRpm:0}"); }
                Console.WriteLine("auto: " + tune.FanAuto() + " " + tune.FanError);
                for (int i = 0; i < 4; i++) { Thread.Sleep(1000); hw.Update(); Console.WriteLine($"  auto rpm {hw.GpuFanRpm:0}"); }
            }
            if (args.Contains("--clock-test") && tune.Available)
            {
                // Re-applies the offsets already in effect: exercises the write path without changing anything.
                Console.WriteLine($"re-apply current ({tune.CoreOffsetMhz}, {tune.MemOffsetMhz}): " + tune.Apply(tune.CoreOffsetMhz, tune.MemOffsetMhz) + " " + tune.Error);
                Console.WriteLine("clocks: " + tune.Describe());
            }
            return 0;
        }

        // --update-test: check the feed, download and apply any update, reopening with --version-file so the result can be checked.
        if (args.Contains("--update-test"))
        {
            Console.WriteLine("installed: " + Updates.Installed + ", current: " + Updates.CurrentVersion);
            var info = Updates.CheckAsync().GetAwaiter().GetResult();
            Console.WriteLine("available: " + (info?.TargetFullRelease.Version.ToString() ?? "none"));
            if (info == null) return 0;
            Updates.DownloadAsync(info, p => { }).GetAwaiter().GetResult();
            Updates.ApplyAfterExit(info, new[] { "--version-file" });
            Console.WriteLine("downloaded; applying after exit");
            return 0;
        }
        if (args.Contains("--version-file"))
        {
            File.WriteAllText(Path.Combine(AppPaths.AppDir, "..", "version-after-update.txt"), Updates.CurrentVersion);
            return 0;
        }

        int ci = Array.IndexOf(args, "--console");
        if (ci >= 0)
        {
            int seconds = ci + 1 < args.Length && int.TryParse(args[ci + 1], out var s) ? s : 15;
            return RunConsole(seconds);
        }

        // "Always start as administrator": reopen elevated (Windows asks once per launch). If declined, carry on normally.
        if (args.Length == 0 && !HwMonitor.IsElevated && Settings.Load().AlwaysAdmin)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = true, Verb = "runas" });
                return 0;
            }
            catch { }
        }

#pragma warning disable WPF0001
        var app = new System.Windows.Application { ThemeMode = System.Windows.ThemeMode.Dark };
#pragma warning restore WPF0001
        var window = new MainWindow();

        // --shot <png>: open off-screen without taking focus, save a picture of the window, exit.
        int si = Array.IndexOf(args, "--shot");
        if (si >= 0 && si + 1 < args.Length)
        {
            string png = args[si + 1];
            string theme = window.Settings.Theme;
            bool tracking = window.Settings.Tracking, idle = args.Contains("--idle");
            if (args.Contains("--light")) window.SetTheme("Light");
            if (args.Contains("--dark")) window.SetTheme("Dark");
            window.Settings.Tracking = !idle;
            OffScreen(window);
            After(6, () =>
            {
                SavePicture(window, png);
                window.SetTheme(theme);              // a test picture must not change the saved settings
                window.Settings.Tracking = tracking;
                window.Close();
            });
            app.Run(window);
            return 0;
        }

        // --bench-test <seconds> <results file>: run a real recording off-screen into a separate file, then exit.
        int bi = Array.IndexOf(args, "--bench-test");
        if (bi >= 0 && bi + 2 < args.Length)
        {
            double seconds = double.Parse(args[bi + 1]);
            string original = window.Settings.BenchmarkFile;
            bool overlay = window.Settings.OverlayOn;
            window.Settings.BenchmarkFile = args[bi + 2];
            window.Settings.OverlayOn = false;
            OffScreen(window);
            After(6, window.ToggleBenchmarkNow);
            After(6 + seconds, () =>
            {
                window.ToggleBenchmarkNow();
                window.Settings.BenchmarkFile = original; // the test must not change the user's settings
                window.Settings.OverlayOn = overlay;
                window.Close();
            });
            app.Run(window);
            return 0;
        }

        // --osd-test <display> <seconds>: off-screen main window with the on-screen display forced on, on the given display.
        int oi = Array.IndexOf(args, "--osd-test");
        if (oi >= 0 && oi + 2 < args.Length)
        {
            bool overlay = window.Settings.OverlayOn, tracking = window.Settings.Tracking; int display = window.Settings.OsdDisplay;
            window.Settings.OverlayOn = true; window.Settings.Tracking = true; window.Settings.OsdDisplay = int.Parse(args[oi + 1]);
            OffScreen(window);
            After(double.Parse(args[oi + 2]), () =>
            {
                window.Settings.OverlayOn = overlay; window.Settings.Tracking = tracking; window.Settings.OsdDisplay = display;
                window.Close();
            });
            app.Run(window);
            return 0;
        }

        // --track-test: off-screen, stop live tracking after 6 s and start it again after 12 s, then exit.
        if (args.Contains("--track-test"))
        {
            bool tracking = window.Settings.Tracking;
            window.Settings.Tracking = true;
            OffScreen(window);
            After(6, () => window.SetTracking(false));
            After(12, () => window.SetTracking(true));
            After(18, () => { window.Settings.Tracking = tracking; window.Close(); });
            app.Run(window);
            return 0;
        }

        // --shot-props <page> <png>: the same for one page of the Properties window.
        int pi = Array.IndexOf(args, "--shot-props");
        if (pi >= 0 && pi + 2 < args.Length)
        {
            foreach (var k in new[] { "overlay", "benchmark", "reset" }) window.HotkeyOk[k] = true;
            if (args.Contains("--light")) Theme.Use("Light");
            if (args.Contains("--dark")) Theme.Use("Dark");
            var props = new PropertiesWindow(window, int.Parse(args[pi + 1]));
            props.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
            OffScreen(props);
            string png = args[pi + 2];
            After(2, () => { SavePicture(props, png); app.Shutdown(); });
            app.Run(props);
            return 0;
        }

        if (window.Settings.StartMinimized) window.WindowState = System.Windows.WindowState.Minimized;
        app.Run(window);
        return 0;
    }

    static void OffScreen(System.Windows.Window w)
    {
        w.ShowActivated = false;
        w.ShowInTaskbar = false;
        w.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
        w.Left = -30000; w.Top = -30000;
        w.Focusable = false;
    }

    static void After(double seconds, Action action)
    {
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        t.Tick += (_, _) => { t.Stop(); action(); };
        t.Start();
    }

    static void SavePicture(System.Windows.Window window, string png)
    {
        var root = (System.Windows.FrameworkElement)window.Content;
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        var dv = new System.Windows.Media.DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(window.Background, null, new System.Windows.Rect(0, 0, window.ActualWidth, window.ActualHeight));
            dc.DrawRectangle(new System.Windows.Media.VisualBrush(root), null,
                new System.Windows.Rect(root.Margin.Left, root.Margin.Top, root.ActualWidth, root.ActualHeight));
        }
        bmp.Render(dv);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(png);
        enc.Save(fs);
    }

    /// <summary>Headless capture: prints one line per second, then a summary. Used for testing.</summary>
    static int RunConsole(int seconds)
    {
        var tracker = new FrameTracker();
        using var etw = new EtwPresentSource(tracker);
        if (!etw.Start()) { Console.WriteLine("ETW: " + etw.Error); return 1; }
        using var sensors = new Sensors();
        sensors.Start();

        Console.WriteLine($"capturing {seconds}s (clock resolution {1e9 / Stopwatch.Frequency:0} ns)");
        uint target = 0; bool bench = false;
        for (int i = 0; i < seconds; i++)
        {
            Thread.Sleep(1000);
            target = tracker.PickTarget(target);
            if (target != 0 && !bench) { tracker.StartBenchmark(target); bench = true; }
            var f = tracker.Snapshot(target);
            var h = sensors.Latest;
            Console.WriteLine(
                $"{i + 1,3}s  {f.Process ?? "-",-22} fps {f.Fps,6:0.0}  avg {f.FrameMs,5:0.00} ms  worst {f.MaxMs,6:0.00} ms  " +
                $"1% {f.Low1,5:0}  | GPU {h.GpuUtil,3:0}% {h.GpuTemp,2:0}C {h.GpuPowerW,3:0}W {h.VramUsedMb,5:0}MB  " +
                $"CPU {h.CpuTotal,3:0}% (max thread {h.CpuMaxThread,3:0}%)  lost {etw.QueryLostEvents()}");
        }
        var frames = tracker.StopBenchmark();
        var r = FrameTracker.Summarize(frames);
        Console.WriteLine();
        Console.WriteLine($"frames {r.Frames} over {r.Seconds:0.000}s");
        Console.WriteLine($"avg {r.AvgFps:0.00} fps | 1% low {r.Low1:0.00} | 0.1% low {r.Low01:0.00} | min {r.MinFps:0.00} | max {r.MaxFps:0.00}");
        Console.WriteLine($"frame time ms: avg {r.AvgMs:0.000} median {r.P50Ms:0.000} p99 {r.P99Ms:0.000} p99.9 {r.P999Ms:0.000} max {r.MaxMs:0.000} stdev {r.StdevMs:0.000}");
        Console.WriteLine($"events lost: {etw.QueryLostEvents()}");
        return 0;
    }
}
