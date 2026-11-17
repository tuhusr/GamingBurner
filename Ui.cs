using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace GamingBurner;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    readonly FrameTracker _tracker = new();
    EtwPresentSource _etw;
    readonly HwMonitor _hw = new();
    readonly Sensors _sensors;
    readonly GpuTuning _tuning = new();
    readonly LatencyEngine _latency = new();
    readonly NetMonitor _net = new();
    Extra _extra;
    readonly DispatcherTimer _timer;
    readonly List<float> _fpsHist = new();
    const int HistLen = 480; // 2 minutes at 4 samples per second

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    readonly Settings _settings = Settings.Load();
    readonly SessionStats _session = new();
    uint _sessionPid;
    long _sessionLastFrames, _sessionLastStamp;
    int _foregroundTicks;
    HudWindow _overlay;
    StreamWriter _log;
    uint _pid;
    int _tick;
    long _lost;
    DateTime _benchStart;

    public MainWindow()
    {
        Title = "GamingBurner";
        Width = 1240; Height = 860; MinWidth = 1080; MinHeight = 780;
        FontFamily = Theme.Sans; FontSize = 13;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/GamingBurner.ico")); } catch { }

        _sensors = new Sensors { Hw = _hw };
        Theme.Use(_settings.Theme);
        BuildUi();
        ShowIdle();

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Tick();

        SourceInitialized += (_, _) =>
        {
            ApplyTitleBar();
        };
        Loaded += async (_, _) =>
        {
            await Task.Run(() => { _hw.Open(); _tuning.Init(); _tuning.InitFan(); });
            _sensors.Start();
            _net.Start();
            _timer.Start();
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle).AddHook(WndProc);
            _loaded = true;
            ApplySettings();
            SetTracking(_settings.Tracking);

            // Look for a newer release now and every six hours.
            if (_settings.AutoUpdateCheck)
            {
                _ = CheckForUpdatesNow(false);
                var updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
                updateTimer.Tick += (_, _) => { if (_settings.AutoUpdateCheck) _ = CheckForUpdatesNow(false); };
                updateTimer.Start();
            }
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            if (_tracker.Benchmarking) FinishBenchmark();
            _log?.Dispose();
            _overlay?.Close();
            var h = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(h, 1); UnregisterHotKey(h, 2); UnregisterHotKey(h, 3);
            _settings.Save();
            _etw?.Dispose();
            _sensors.Dispose();
            _latency.Dispose();
            _net.Dispose();
            if (_tuning.FanManual) _tuning.FanAuto(); // never leave the fans pinned after exit
            _hw.Dispose();
        };
    }

    // ---------- Settings shared with the Properties window ----------

    public Settings Settings => _settings;
    /// <summary>Whether each shortcut ("overlay", "benchmark", "reset") was accepted by Windows.</summary>
    public Dictionary<string, bool> HotkeyOk { get; } = new();
    public void ResetSession() => _session.Reset();
    bool _loaded;

    /// <summary>Saves the settings and makes every part of the app follow them.</summary>
    public void ApplySettings()
    {
        _settings.Save();
        if (!_loaded) return;

        // Shortcuts: release the old ones, then claim the chosen ones.
        var h = new WindowInteropHelper(this).Handle;
        var keys = new (int id, string name, Hotkey hk)[] { (1, "overlay", _settings.OverlayKey), (2, "benchmark", _settings.BenchmarkKey), (3, "reset", _settings.ResetKey) };
        foreach (var k in keys) UnregisterHotKey(h, k.id);
        foreach (var k in keys)
            HotkeyOk[k.name] = k.hk.VirtualKey != 0 && RegisterHotKey(h, k.id, k.hk.ModFlags | 0x4000 /* no auto-repeat */, k.hk.VirtualKey);
        UpdateButtons();

        // On-screen display: show or hide to match, then restyle it.
        bool showing = _overlay != null;
        if (_settings.OverlayOn != showing) { _overlayBtn.IsChecked = _settings.OverlayOn; ToggleOverlay(); }
        else _overlay?.Configure(_settings);

        _sensors.PollMs = _settings.PollMs;
        _net.Enabled = _settings.NetworkOn && _tracking;
        if (!_tracking) _overlay?.Hide();
        _net.Target = string.IsNullOrWhiteSpace(_settings.PingTarget) ? "1.1.1.1" : _settings.PingTarget.Trim();
    }

    public void RestartElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = true, Verb = "runas" });
            Close();
        }
        catch { _status.Text = "Administrator restart was cancelled"; }
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312) // WM_HOTKEY
        {
            if (wParam.ToInt32() == 1) { _overlayBtn.IsChecked = _overlayBtn.IsChecked != true; ToggleOverlay(); }
            if (wParam.ToInt32() == 2) ToggleBenchmarkNow();
            if (wParam.ToInt32() == 3) _session.Reset();
            handled = true;
        }
        return IntPtr.Zero;
    }

    void Set(string key, string text, Brush brush = null)
    {
        var t = _vals[key];
        t.Text = text;
        if (brush != null) t.Foreground = brush;
    }

    static void Push(List<float> list, double v)
    {
        list.Add((float)v);
        if (list.Count > HistLen) list.RemoveAt(0);
    }

    static Brush Heat(double v, double warn, double bad) => v >= bad ? Theme.Bad : v >= warn ? Theme.Warn : Theme.Text;

    // ---------- Update loop ----------

    void Tick()
    {
        _tick++;
        if (!_tracking) return; // live tracking is stopped: do nothing at all
        _pid = _tracker.PickTarget(_pid);
        if (_benchPending)
        {
            // A recording was requested before any game was seen: begin as soon as one appears.
            if (_pid != 0) { _benchPending = false; _benchBtn.IsChecked = true; ToggleBenchmark(); }
            else if (++_benchPendingTicks > 60) { _benchPending = false; _status.Text = "No game appeared, so nothing was recorded"; UpdateButtons(); }
        }
        _sensors.GamePid = _pid;
        _latency.Track(_settings.LatencyOn ? _pid : 0);
        if (_tick % 2 == 0) _extra = new Extra { L = _settings.LatencyOn ? _latency.Snapshot() : default, N = _settings.NetworkOn ? _net.Snapshot() : default };
        var f = _tracker.Snapshot(_pid);
        var h = _sensors.Latest;
        if (_tick % 4 == 1) _lost = Lost();

        // Work that must continue while the window is minimized: session figures, the on-screen display, the log.
        UpdateSession(f);
        if (_overlay != null && _tick % 2 == 0)
        {
            _overlay.Update(f, h, _session, _extra, _tracker.Benchmarking);
            if (_tick % 4 == 0) _overlay.KeepOnTop();
        }
        WriteLogRow(f, h);
        if (_tracker.Benchmarking)
        {
            // One row per fresh sensor reading, so no reading is repeated or skipped.
            if (h.Seq != _benchSeq) { _benchSeq = h.Seq; RecordBenchRow(f, h); }
            GetWindowThreadProcessId(GetForegroundWindow(), out uint front);
            if (front != _pid) _benchBackgroundSeconds += 0.25;
        }
        // Nothing below is visible while minimized, so skip the drawing work and leave the processor to the game.
        if (WindowState == WindowState.Minimized) return;

        UpdateView(f, h);

        if (_tracker.Benchmarking)
        {
            _status.Foreground = Theme.Dim;
            _status.Text = $"Benchmark running · {(DateTime.Now - _benchStart).TotalSeconds:0} s · {_tracker.BenchmarkFrames:N0} frames";
        }

    }

    void WriteLogRow(FrameStats f, HwSample h)
    {
        if (_log != null && _tick % 4 == 0)
        {
            _log.WriteLine(string.Join(",", new[]
            {
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), f.Process ?? "",
                f.Fps.ToString("0.00"), f.FrameMs.ToString("0.000"), f.MaxMs.ToString("0.000"),
                f.Low1.ToString("0.00"), f.Low01.ToString("0.00"),
                h.GpuUtil.ToString("0"), h.GpuTemp.ToString("0"), Num(h.GpuHotspot), h.GpuPowerW.ToString("0.0"),
                h.VramUsedMb.ToString("0"), h.GpuClockMhz.ToString("0"), h.GpuMemClockMhz.ToString("0"), h.GpuFanPct.ToString("0"), Num(h.GpuFanRpm),
                h.CpuTotal.ToString("0.0"), h.CpuMaxThread.ToString("0.0"), Num(h.CpuTemp), h.GameCpuCores.ToString("0.00"),
                h.RamUsedGb.ToString("0.00"), h.GameRamMb.ToString("0"), _lost.ToString()
            }));
            _log.Flush();
        }
    }

    static string Num(double v) => double.IsNaN(v) ? "" : v.ToString("0.#");

    // ---------- Actions ----------

    void ToggleOverlay()
    {
        if (_overlayBtn.IsChecked == true)
        {
            _overlay = new HudWindow();
            _overlay.Configure(_settings);
            _overlay.Show();
            if (!_tracking) _overlay.Hide();
        }
        else { _overlay?.Close(); _overlay = null; }
        _settings.OverlayOn = _overlayBtn.IsChecked == true;
        _settings.Save();
    }

    /// <summary>
    /// Highest, lowest and average frame rate for this game since it was picked up. Only counted
    /// while the game is the active window, so time spent alt-tabbed does not drag the numbers down.
    /// </summary>
    void UpdateSession(FrameStats f)
    {
        long now = Stopwatch.GetTimestamp();
        if (!f.Valid || f.Pid != _sessionPid)
        {
            _session.Reset();
            _sessionPid = f.Valid ? f.Pid : 0;
            _sessionLastFrames = f.TotalFrames; _sessionLastStamp = now; _foregroundTicks = 0;
            return;
        }
        long frames = f.TotalFrames - _sessionLastFrames;
        double seconds = (now - _sessionLastStamp) / (double)Stopwatch.Frequency;
        _sessionLastFrames = f.TotalFrames; _sessionLastStamp = now;

        GetWindowThreadProcessId(GetForegroundWindow(), out uint foregroundPid);
        if (foregroundPid != f.Pid) { _foregroundTicks = 0; return; }
        if (++_foregroundTicks <= 8) return; // let two seconds pass so the reading no longer includes background frames
        _session.Frames += frames;
        _session.Seconds += seconds;
        if (f.Fps > _session.Max) _session.Max = f.Fps;
        if (f.Fps < _session.Min) _session.Min = f.Fps;
    }

    void ToggleLog()
    {
        _status.Foreground = Theme.Dim;
        if (_logBtn.IsChecked == true)
        {
            if (!_tracking) { SetTracking(true); _logBtn.IsChecked = true; } // logging needs live tracking
            string path = Path.Combine(Program.DataDir, $"log-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            _log = new StreamWriter(path);
            _log.WriteLine("time,process,fps,frame_ms,worst_ms_1s,low1_fps_30s,low01_fps_30s,gpu_pct,gpu_temp_c,gpu_hotspot_c,gpu_power_w,vram_mb,gpu_clock_mhz,gpu_mem_clock_mhz,gpu_fan_pct,gpu_fan_rpm,cpu_pct,cpu_busiest_thread_pct,cpu_temp_c,game_cpu_cores,ram_used_gb,game_ram_mb,events_lost");
            _status.Text = "Logging to " + Path.GetFileName(path);
        }
        else
        {
            _log?.Dispose(); _log = null;
            _status.Text = "Logging stopped";
        }
    }

    void ToggleBenchmark()
    {
        _status.Foreground = Theme.Dim;
        if (_benchBtn.IsChecked == true)
        {
            if (!_tracking) SetTracking(true); // recording needs live tracking
            if (_pid == 0) { _benchPending = true; _benchPendingTicks = 0; _status.Text = "Waiting for a game to record…"; UpdateButtons(); return; }
            _tracker.StartBenchmark(_pid);
            _latency.StartRecording();
            _benchStart = DateTime.Now;
            _benchClock = Stopwatch.StartNew();
            _benchRows = new List<double[]>();
            _benchLostAtStart = Math.Max(0, Lost());
            _benchProcess = _tracker.Snapshot(_pid).Process ?? "";
            _benchBackgroundSeconds = 0;
            _benchSeq = -1;
        }
        else if (_benchPending) { _benchPending = false; _status.Text = "Recording cancelled"; }
        else FinishBenchmark();
        UpdateButtons();
    }

    /// <summary>Starts or stops a recording exactly as the button and hotkey do (used by the self-test).</summary>
    public void ToggleBenchmarkNow() { _benchBtn.IsChecked = _benchBtn.IsChecked != true; ToggleBenchmark(); }

    // Once a second during a recording, everything the sensors report is kept alongside the frames.
    static readonly string[] BenchColumns =
    {
        "elapsed_s", "fps", "frame_ms", "worst_frame_ms", "gpu_pct", "gpu_temp_c", "gpu_hotspot_c", "gpu_memory_temp_c", "gpu_power_w",
        "vram_mb", "gpu_clock_mhz", "gpu_memory_clock_mhz", "gpu_fan_pct", "gpu_fan_rpm", "cpu_pct", "cpu_busiest_thread_pct",
        "cpu_temp_c", "game_cpu_cores", "ram_used_gb", "game_ram_mb",
        "pc_latency_ms", "frame_to_screen_ms", "shown_fps", "dropped_pct", "gpu_busy_pct", "ping_ms", "packet_loss_pct",
        "disk_busy_pct", "paging_per_s", "cpu_clock_mhz"
    };

    /// <summary>Average and 99th percentile of the readings that were available.</summary>
    static (double avg, double p99, int n) Dist(IEnumerable<float> values)
    {
        var v = values.Where(x => !float.IsNaN(x)).OrderBy(x => x).ToArray();
        if (v.Length == 0) return (0, 0, 0);
        return (v.Average(x => (double)x), v[(int)Math.Min(v.Length - 1, Math.Ceiling(0.99 * (v.Length - 1)))], v.Length);
    }
    List<double[]> _benchRows;
    Stopwatch _benchClock;
    long _benchLostAtStart, _benchSeq;
    string _benchProcess = "";
    double _benchBackgroundSeconds;

    void RecordBenchRow(FrameStats f, HwSample h)
    {
        if (_benchRows == null || !f.Valid) return;
        _benchRows.Add(new[]
        {
            _benchClock.Elapsed.TotalSeconds, f.Fps, f.FrameMs, f.MaxMs, h.GpuUtil, h.GpuTemp, h.GpuHotspot, h.GpuMemTemp, h.GpuPowerW,
            h.VramUsedMb, h.GpuClockMhz, h.GpuMemClockMhz, h.GpuFanPct, h.GpuFanRpm, h.CpuTotal, h.CpuMaxThread,
            h.CpuTemp, h.GameCpuCores, h.RamUsedGb, h.GameRamMb,
            _extra.L.Valid ? _extra.L.PcLatencyMs : double.NaN, _extra.L.Valid ? _extra.L.FrameToScreenMs : double.NaN,
            _extra.L.Valid ? _extra.L.ShownFps : double.NaN, _extra.L.Valid ? _extra.L.DroppedPct : double.NaN,
            _extra.L.Valid ? _extra.L.GpuBusyShare * 100 : double.NaN,
            _extra.N.Valid ? _extra.N.PingMs : double.NaN, _extra.N.Valid ? _extra.N.LossPct : double.NaN,
            h.DiskActivePct, h.HardFaultsPerSec, h.CpuClockMhz > 0 ? h.CpuClockMhz : double.NaN
        });
    }

    /// <summary>Average, lowest and highest of one recorded column, ignoring readings that were unavailable.</summary>
    (double avg, double min, double max, bool any) Col(string name)
    {
        int c = Array.IndexOf(BenchColumns, name);
        double sum = 0, min = double.MaxValue, max = double.MinValue; int n = 0;
        foreach (var row in _benchRows)
        {
            double v = row[c];
            if (double.IsNaN(v)) continue;
            sum += v; n++; if (v < min) min = v; if (v > max) max = v;
        }
        return n == 0 ? (0, 0, 0, false) : (sum / n, min, max, true);
    }

    string HwLine(string label, string column, string unit, string format = "0")
    {
        var (avg, min, max, any) = Col(column);
        return any
            ? $"  {label,-22}: avg {avg.ToString(format)} {unit}, low {min.ToString(format)}, high {max.ToString(format)}\r\n"
            : $"  {label,-22}: not available\r\n";
    }

    static string CpuName()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "unknown";
        }
        catch { return "unknown"; }
    }

    void FinishBenchmark()
    {
        var frames = _tracker.StopBenchmark();
        var lat = _latency.StopRecording();
        string presentMode = _latency.PresentModeNow;
        var rows = _benchRows ?? new List<double[]>();
        if (frames.Length < 2) { _status.Text = "Benchmark stopped: no frames recorded"; return; }
        var r = FrameTracker.Summarize(frames);
        var f = _tracker.Snapshot(_pid);
        var h = _sensors.Latest;
        string stamp = _benchStart.ToString("yyyyMMdd-HHmmss");

        int over16 = 0, over33 = 0, stutters = 0;
        foreach (var ms in frames)
        {
            if (ms > 16.667) over16++;
            if (ms > 33.333) over33++;
            if (ms > r.P50Ms * 2.5) stutters++;
        }
        string Share(int n) => $"{n:N0} ({100.0 * n / frames.Length:0.000} %)";

        var text = new System.Text.StringBuilder();
        long missed = Math.Max(0, Lost() - _benchLostAtStart); // during this recording only
        text.Append($"{_benchStart:yyyy-MM-dd HH:mm:ss}  {(_benchProcess.Length > 0 ? _benchProcess : f.Process)}\r\n");
        text.Append($"  Duration              : {r.Seconds:0.000} s, {r.Frames:N0} frames, {missed} timing events missed\r\n");
        if (_benchBackgroundSeconds >= 1)
            text.Append($"  Note                  : the game was not the active window for about {_benchBackgroundSeconds:0} s of this recording; games usually slow down in the background, which lowers the figures below\r\n");
        text.Append($"  System                : {CpuName()} | {h.GpuName} (driver {_sensors.DriverVersion}) | {h.RamTotalGb:0} GB memory | {GetSystemMetrics(0)}x{GetSystemMetrics(1)} main display\r\n");
        text.Append("  -- Frame rate --\r\n");
        text.Append($"  Average               : {r.AvgFps:0.00} fps\r\n");
        text.Append($"  1% low                : {r.Low1:0.00} fps\r\n");
        text.Append($"  0.1% low              : {r.Low01:0.00} fps\r\n");
        if (rows.Count > 0)
        {
            var (_, lowSec, highSec, _) = Col("fps");
            text.Append($"  Lowest one second     : {lowSec:0.00} fps\r\n");
            text.Append($"  Highest one second    : {highSec:0.00} fps\r\n");
        }
        text.Append($"  Slowest single frame  : {r.MaxMs:0.000} ms ({r.MinFps:0.00} fps)\r\n");
        text.Append($"  Fastest single frame  : {r.MinMs:0.000} ms ({r.MaxFps:0.00} fps)\r\n");
        text.Append($"  Frame time            : avg {r.AvgMs:0.000} ms, median {r.P50Ms:0.000}, 99th {r.P99Ms:0.000}, 99.9th {r.P999Ms:0.000}, stdev {r.StdevMs:0.000}\r\n");
        text.Append($"  Stutters              : {Share(stutters)} frames took over 2.5x the median\r\n");
        text.Append($"  Frames over 16.7 ms   : {Share(over16)}\r\n");
        text.Append($"  Frames over 33.3 ms   : {Share(over33)}\r\n");
        if (rows.Count > 0)
        {
            text.Append("  -- Graphics card --\r\n");
            text.Append(HwLine("Load", "gpu_pct", "%"));
            text.Append(HwLine("Temperature", "gpu_temp_c", "C"));
            text.Append(HwLine("Hot spot", "gpu_hotspot_c", "C"));
            text.Append(HwLine("Memory temperature", "gpu_memory_temp_c", "C"));
            text.Append(HwLine("Power", "gpu_power_w", "W"));
            text.Append(HwLine("Video memory", "vram_mb", "MB"));
            text.Append(HwLine("Clock", "gpu_clock_mhz", "MHz"));
            text.Append(HwLine("Fan", "gpu_fan_pct", "%"));
            text.Append(HwLine("Fan speed", "gpu_fan_rpm", "rpm"));
            text.Append("  -- Processor --\r\n");
            text.Append(HwLine("Load", "cpu_pct", "%"));
            text.Append(HwLine("Busiest thread", "cpu_busiest_thread_pct", "%"));
            text.Append(HwLine("Temperature", "cpu_temp_c", "C"));
            text.Append(HwLine("Used by the game", "game_cpu_cores", "cores", "0.0"));
            text.Append(HwLine("Clock speed", "cpu_clock_mhz", "MHz"));
            text.Append("  -- Memory and disk --\r\n");
            text.Append(HwLine("Memory in use", "ram_used_gb", "GB", "0.0"));
            text.Append(HwLine("Used by the game", "game_ram_mb", "MB"));
            text.Append(HwLine("Disk busy", "disk_busy_pct", "%"));
            text.Append(HwLine("Paging from disk", "paging_per_s", "pages/s"));
            text.Append("  -- Network --\r\n");
            text.Append(_settings.NetworkOn ? HwLine("Ping to " + _settings.PingTarget, "ping_ms", "ms") : "  Ping                  : turned off\r\n");
            if (_settings.NetworkOn) text.Append(HwLine("Packet loss", "packet_loss_pct", "%"));
        }
        // Latency and display timing, from the frames the latency engine followed to the screen.
        text.Append("  -- Latency and display --\r\n");
        if (lat.Length == 0)
            text.Append(_settings.LatencyOn ? "  Latency               : no data (the latency engine reported no frames)\r\n" : "  Latency               : tracking was turned off\r\n");
        else
        {
            var pc = Dist(lat.Select(x => x.PcLatencyMs));
            var fts = Dist(lat.Select(x => x.FrameToScreenMs));
            var click = Dist(lat.Select(x => x.ClickMs));
            var gap = Dist(lat.Select(x => x.DisplayGapMs > 0 ? x.DisplayGapMs : float.NaN));
            int dropped = lat.Count(x => float.IsNaN(x.UntilShownMs));
            double busy = lat.Where(x => !float.IsNaN(x.GpuBusyMs) && !float.IsNaN(x.FrameMs)).Select(x => (double)x.GpuBusyMs).DefaultIfEmpty(0).Sum();
            double total = lat.Where(x => !float.IsNaN(x.GpuBusyMs) && !float.IsNaN(x.FrameMs)).Select(x => (double)x.FrameMs).DefaultIfEmpty(0).Sum();
            double share = total > 0 ? busy / total : double.NaN;
            text.Append(pc.n > 0
                ? $"  PC latency            : avg {pc.avg:0.0} ms, 99th {pc.p99:0.0} ms (input received by the PC to frame on screen, as reported by the game)\r\n"
                : "  PC latency            : not reported by this game\r\n");
            text.Append(fts.n > 0
                ? $"  Frame to screen       : avg {fts.avg:0.0} ms, 99th {fts.p99:0.0} ms (start of a frame's work to it appearing on screen)\r\n"
                : "  Frame to screen       : not available\r\n");
            text.Append(click.n > 0
                ? $"  Click to screen       : avg {click.avg:0.0} ms, 99th {click.p99:0.0} ms over {click.n:N0} clicks\r\n"
                : "  Click to screen       : no mouse clicks during the recording\r\n");
            if (gap.n > 0) text.Append($"  Shown on screen       : {1000.0 / gap.avg:0.00} fps\r\n");
            text.Append($"  Dropped frames        : {dropped:N0} of {lat.Length:N0} ({100.0 * dropped / lat.Length:0.00} %) were rendered but never shown\r\n");
            if (!double.IsNaN(share))
                text.Append($"  Limited by            : {(share >= 0.92 ? "the graphics card" : share <= 0.80 ? "the processor" : "both about equally")} (graphics card busy {share * 100:0} % of each frame)\r\n");
            if (!string.IsNullOrEmpty(presentMode)) text.Append($"  Display path          : {presentMode}\r\n");
        }
        text.Append("\r\n");
        _benchRows = null;

        try
        {
            string file = string.IsNullOrWhiteSpace(_settings.BenchmarkFile) ? Path.Combine(Program.DataDir, "Benchmark.txt") : _settings.BenchmarkFile;
            string folder = Path.GetDirectoryName(file);
            Directory.CreateDirectory(folder);
            File.AppendAllText(file, text.ToString());
            if (rows.Count > 0)
            {
                // One row per second: frame rate and every sensor, for charting how the run went.
                using var t = new StreamWriter(Path.Combine(folder, $"benchmark-{stamp}-timeline.csv"));
                t.WriteLine(string.Join(",", BenchColumns));
                foreach (var row in rows)
                    t.WriteLine(string.Join(",", row.Select((v, i) => double.IsNaN(v) ? "" : v.ToString(i == 0 ? "0.00" : "0.###"))));
            }
            if (_settings.SaveFrameTimes)
            {
                using var w = new StreamWriter(Path.Combine(folder, $"benchmark-{stamp}-frametimes.csv"));
                w.WriteLine("frame,elapsed_s,frame_ms,fps");
                double elapsed = 0;
                for (int i = 0; i < frames.Length; i++)
                {
                    elapsed += frames[i];
                    w.WriteLine($"{i + 1},{elapsed / 1000:0.00000},{frames[i]:0.0000},{1000.0 / frames[i]:0.00}");
                }
            }
        }
        catch (Exception ex)
        {
            _status.Foreground = Theme.Bad;
            _status.Text = "Benchmark could not be saved: " + ex.Message;
            return;
        }
        _status.Text = $"Benchmark saved · average {r.AvgFps:0.0} fps · 1% low {r.Low1:0.0} · 0.1% low {r.Low01:0.0} · {r.Seconds:0} s";
    }
}
