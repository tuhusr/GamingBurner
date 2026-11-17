using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace GamingBurner;

/// <summary>What the user chose, kept between runs in settings.json in the user's AppData folder.</summary>
public sealed class Settings
{
    public string Theme { get; set; } = "Dark";                       // "Dark" or "Light"
    public bool Tracking { get; set; } = true;                        // live tracking on or off, remembered between runs
    public bool OverlayOn { get; set; } = true;
    public string Corner { get; set; } = "Top left";
    public int OsdDisplay { get; set; }                               // 0 = the main display (over the game), 1 and up = your other displays
    public List<string> Stats { get; set; } = new() { "max", "min", "avg" };
    public Dictionary<string, string> Labels { get; set; } = new();   // custom on-screen names, by statistic key
    public Dictionary<string, string> Colors { get; set; } = new();   // custom colours (#RRGGBB), by statistic key
    public double OsdScale { get; set; } = 1.0;
    public int OsdOpacity { get; set; } = 80;                         // background opacity, percent
    public bool StartMinimized { get; set; }
    public int PollMs { get; set; } = 1000;                           // how often hardware sensors are read
    public string BenchmarkFile { get; set; } = Path.Combine(AppPaths.LogsDir, "Benchmark.txt");
    public bool AlwaysAdmin { get; set; }                             // reopen as administrator at every start
    public bool AutoUpdateCheck { get; set; } = true;                 // look for a newer release at start and every few hours
    public bool SaveFrameTimes { get; set; } = true;
    public bool LatencyOn { get; set; } = true;                       // follow frames to the display for latency figures
    public bool NetworkOn { get; set; } = true;                       // one small ping a second
    public string PingTarget { get; set; } = "1.1.1.1";
    public Hotkey OverlayKey { get; set; } = new() { Mods = "Ctrl+Shift", Key = "F9" };
    public Hotkey BenchmarkKey { get; set; } = new() { Mods = "Ctrl+Shift", Key = "F10" };
    public Hotkey ResetKey { get; set; } = new() { Mods = "Ctrl+Shift", Key = "F11" };

    static string FilePath => AppPaths.SettingsFile;

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.SettingsDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

/// <summary>A global keyboard shortcut: a modifier combination plus one key.</summary>
public sealed class Hotkey
{
    public static readonly string[] ModChoices = { "None", "Ctrl", "Alt", "Shift", "Ctrl+Shift", "Ctrl+Alt", "Alt+Shift" };
    public static readonly string[] KeyChoices =
    {
        "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
        "Insert", "Home", "End", "Page Up", "Page Down", "Pause", "Scroll Lock"
    };
    public string Mods { get; set; } = "Ctrl+Shift";
    public string Key { get; set; } = "F9";

    [System.Text.Json.Serialization.JsonIgnore] public uint ModFlags => (Mods.Contains("Alt") ? 0x1u : 0) | (Mods.Contains("Ctrl") ? 0x2u : 0) | (Mods.Contains("Shift") ? 0x4u : 0);
    [System.Text.Json.Serialization.JsonIgnore] public uint VirtualKey => Key switch
    {
        "Insert" => 0x2D, "Home" => 0x24, "End" => 0x23, "Page Up" => 0x21, "Page Down" => 0x22,
        "Pause" => 0x13, "Scroll Lock" => 0x91,
        _ => Key.StartsWith("F") && int.TryParse(Key.AsSpan(1), out int n) && n is >= 1 and <= 12 ? (uint)(0x70 + n - 1) : 0
    };
    public override string ToString() => Mods == "None" ? Key : Mods + "+" + Key;
}

/// <summary>Highest, lowest and average frame rate since the game was picked up (or since the last reset).</summary>
public sealed class SessionStats
{
    public double Max, Min = double.MaxValue, Seconds;
    public long Frames;
    public bool HasData => Frames > 0 && Seconds > 0 && Min != double.MaxValue;
    public double Avg => Seconds > 0 ? Frames / Seconds : 0;
    public void Reset() { Max = 0; Min = double.MaxValue; Seconds = 0; Frames = 0; }
}

/// <summary>Every statistic the on-screen display can show.</summary>
public sealed record HudStat(string Key, string Short, string Name, Brush Color, Func<FrameStats, HwSample, SessionStats, Extra, string> Value);

/// <summary>Latency and network readings, passed alongside the frame and hardware readings.</summary>
public struct Extra { public LatencyStats L; public NetStats N; }

public static class Hud
{
    static string Fps(double v) => v > 0 && !double.IsInfinity(v) ? v.ToString("0") : "–";
    static string Ms(double v, string format = "0.0") => double.IsNaN(v) || v <= 0 ? "–" : v.ToString(format) + " ms";
    static string Temp(double v) => double.IsNaN(v) || v <= 0 ? "–" : v.ToString("0") + "°C";

    public static readonly HudStat[] All =
    {
        new("fps", "FPS", "Frame rate now", Theme.DarkP.Accent, (f, h, s, x) => f.Valid ? Fps(f.Fps) : "–"),
        new("max", "MAX", "Highest frame rate", Theme.DarkP.Accent, (f, h, s, x) => s.HasData ? Fps(s.Max) : "–"),
        new("min", "MIN", "Lowest frame rate", Theme.DarkP.Bad, (f, h, s, x) => s.HasData ? Fps(s.Min) : "–"),
        new("avg", "AVG", "Average frame rate", Theme.DarkP.Text, (f, h, s, x) => s.HasData ? Fps(s.Avg) : "–"),
        new("low1", "1% LOW", "1% low", Theme.DarkP.Warn, (f, h, s, x) => f.Valid ? Fps(f.Low1) : "–"),
        new("low01", "0.1% LOW", "0.1% low", Theme.DarkP.Warn, (f, h, s, x) => f.Valid ? Fps(f.Low01) : "–"),
        new("ms", "FRAME", "Frame time", Theme.DarkP.Text, (f, h, s, x) => f.Valid ? f.FrameMs.ToString("0.00") + " ms" : "–"),
        new("pclat", "LATENCY", "PC latency (input to screen)", Theme.DarkP.Accent, (f, h, s, x) => Ms(x.L.Valid ? (double.IsNaN(x.L.PcLatencyMs) ? x.L.FrameToScreenMs : x.L.PcLatencyMs) : double.NaN)),
        new("click", "CLICK", "Click to screen", Theme.DarkP.Accent, (f, h, s, x) => Ms(x.L.Valid && x.L.ClickAgeSec < 10 ? x.L.ClickMs : double.NaN)),
        new("shown", "SHOWN", "Frames shown on screen", Theme.DarkP.Text, (f, h, s, x) => x.L.Valid ? Fps(x.L.ShownFps) : "–"),
        new("limit", "LIMIT", "What is limiting the frame rate", Theme.DarkP.Warn, (f, h, s, x) => x.L.Valid && !string.IsNullOrEmpty(x.L.Limit) ? (x.L.Limit == "Graphics card" ? "GPU" : x.L.Limit == "Processor" ? "CPU" : "EVEN") : "–"),
        new("ping", "PING", "Network ping", Theme.DarkP.Text, (f, h, s, x) => Ms(x.N.Valid ? x.N.PingMs : double.NaN, "0")),
        new("loss", "LOSS", "Network packet loss", Theme.DarkP.Bad, (f, h, s, x) => x.N.Valid ? x.N.LossPct.ToString("0") + "%" : "–"),
        new("gpu", "GPU", "Graphics card load", Theme.DarkP.Blue, (f, h, s, x) => h.GpuOk ? h.GpuUtil.ToString("0") + "%" : "–"),
        new("gtemp", "GPU TEMP", "Graphics card temperature", Theme.DarkP.Blue, (f, h, s, x) => h.GpuOk ? Temp(h.GpuTemp) : "–"),
        new("gpow", "GPU PWR", "Graphics card power", Theme.DarkP.Blue, (f, h, s, x) => h.GpuOk ? h.GpuPowerW.ToString("0") + " W" : "–"),
        new("vram", "VRAM", "Video memory", Theme.DarkP.Blue, (f, h, s, x) => h.GpuOk ? (h.VramUsedMb / 1024).ToString("0.0") + " GB" : "–"),
        new("fan", "FAN", "Graphics card fan", Theme.DarkP.Blue, (f, h, s, x) => h.GpuOk ? h.GpuFanPct.ToString("0") + "%" : "–"),
        new("cpu", "CPU", "Processor load", Theme.DarkP.Violet, (f, h, s, x) => h.CpuThreads != null ? h.CpuTotal.ToString("0") + "%" : "–"),
        new("cpumax", "THREAD", "Busiest processor thread", Theme.DarkP.Violet, (f, h, s, x) => h.CpuThreads != null ? h.CpuMaxThread.ToString("0") + "%" : "–"),
        new("cclk", "CPU CLK", "Processor clock speed", Theme.DarkP.Violet, (f, h, s, x) => h.CpuClockMhz > 0 ? (h.CpuClockMhz / 1000).ToString("0.00") + " GHz" : "–"),
        new("ctemp", "CPU TEMP", "Processor temperature", Theme.DarkP.Violet, (f, h, s, x) => Temp(h.CpuTemp)),
        new("ram", "RAM", "Memory in use", Theme.DarkP.Text, (f, h, s, x) => h.RamTotalGb > 0 ? h.RamUsedGb.ToString("0.0") + " GB" : "–"),
    };
}

/// <summary>
/// The on-screen display: a small always-on-top panel drawn over the game. It never takes
/// focus or mouse clicks, and nothing is injected into the game, so it works with games
/// running borderless or windowed (not exclusive fullscreen).
/// </summary>
public sealed class HudWindow : Window
{
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    readonly Grid _grid = new();
    readonly TextBlock _rec;
    readonly Border _panel;
    readonly List<(HudStat stat, TextBlock value)> _rows = new();
    string _corner = "Top left";
    int _display;
    IntPtr _hwnd;

    public HudWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = 16; Top = 16;

        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 52 });
        _rec = Theme.Number(11.5, Theme.DarkP.Bad, FontWeights.Bold);
        _rec.Text = "● RECORDING"; _rec.Margin = new Thickness(0, 4, 0, 0); _rec.Visibility = Visibility.Collapsed;
        var sp = new StackPanel();
        sp.Children.Add(_grid); sp.Children.Add(_rec);
        Content = _panel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(205, 8, 11, 16)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 7, 12, 8), Child = sp
        };
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            const int exStyle = -20, transparent = 0x20, toolWindow = 0x80, layered = 0x80000, noActivate = 0x08000000;
            SetWindowLong(_hwnd, exStyle, GetWindowLong(_hwnd, exStyle) | transparent | toolWindow | layered | noActivate);
        };
        SizeChanged += (_, _) => Place();
    }

    /// <summary>Chooses which statistics appear, in the fixed order of <see cref="Hud.All"/>.</summary>
    public void Configure(Settings s)
    {
        _corner = s.Corner;
        _display = s.OsdDisplay;
        double scale = Math.Clamp(s.OsdScale, 0.6, 2.5);
        _panel.Background = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(s.OsdOpacity, 0, 100) * 255 / 100), 8, 11, 16));
        _panel.BorderBrush = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(s.OsdOpacity, 0, 100) * 70 / 100), 255, 255, 255));
        _rec.FontSize = 11.5 * scale;
        var chosen = new HashSet<string>(s.Stats);
        _grid.Children.Clear();
        _grid.RowDefinitions.Clear();
        _rows.Clear();
        foreach (var stat in Hud.All)
        {
            if (!chosen.Contains(stat.Key)) continue;
            int row = _grid.RowDefinitions.Count;
            _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock
            {
                Text = s.Labels.TryGetValue(stat.Key, out var custom) && custom.Trim().Length > 0 ? custom.Trim() : stat.Short,
                FontFamily = Theme.Sans, FontSize = 11.5 * scale, FontWeight = FontWeights.Bold,
                Foreground = Theme.DarkP.Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14 * scale, 0)
            };
            Brush color = stat.Color;
            if (s.Colors.TryGetValue(stat.Key, out var hex)) { try { color = Theme.B(hex); } catch { } }
            var value = Theme.Number(19 * scale, color, FontWeights.Bold);
            value.HorizontalAlignment = HorizontalAlignment.Right;
            value.LineHeight = 23 * scale; value.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            Grid.SetRow(label, row); Grid.SetRow(value, row); Grid.SetColumn(value, 1);
            _grid.Children.Add(label); _grid.Children.Add(value);
            _rows.Add((stat, value));
        }
        if (_rows.Count == 0)
        {
            _grid.RowDefinitions.Add(new RowDefinition());
            _grid.Children.Add(new TextBlock { Text = "No statistics selected", Foreground = Theme.DarkP.Dim, FontSize = 12 });
        }
        Place();
    }

    public void Update(FrameStats f, HwSample h, SessionStats s, Extra x, bool recording)
    {
        foreach (var (stat, value) in _rows) value.Text = stat.Value(f, h, s, x);
        _rec.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Keeps the panel in its corner of the main display as its size changes.</summary>
    void Place()
    {
        const double margin = 16;
        // Display bounds come in pixels; WPF positions windows in its own units, which differ when Windows scaling is on.
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var all = Displays.All();
        var d = all[Math.Clamp(_display, 0, all.Count - 1)];
        double left = d.X / scale, top = d.Y / scale, sw = d.Width / scale, sh = d.Height / scale;
        double w = ActualWidth > 0 ? ActualWidth : 120, h = ActualHeight > 0 ? ActualHeight : 60;
        Left = left + (_corner.Contains("right") ? sw - w - margin : margin);
        Top = top + (_corner.StartsWith("Bottom") ? sh - h - margin : margin);
    }

    /// <summary>Games can raise themselves above other windows; put the panel back on top without taking focus.</summary>
    public void KeepOnTop()
    {
        if (_hwnd != IntPtr.Zero)
            SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // no size, no move, no activate
    }
}

/// <summary>The monitors attached to this PC: the main one first, then the others from left to right.</summary>
public static class Displays
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
    delegate bool MonitorProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorProc proc, IntPtr data);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    public static List<Rect> All()
    {
        var found = new List<(Rect bounds, bool primary)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (m, _, _, _) =>
        {
            var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(m, ref info))
                found.Add((new Rect(info.Monitor.L, info.Monitor.T, info.Monitor.R - info.Monitor.L, info.Monitor.B - info.Monitor.T), (info.Flags & 1) != 0));
            return true;
        }, IntPtr.Zero);
        var list = found.OrderByDescending(d => d.primary).ThenBy(d => d.bounds.X).Select(d => d.bounds).ToList();
        if (list.Count == 0) list.Add(new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight));
        return list;
    }

    /// <summary>Names for the choices shown in Properties, in the same order as <see cref="All"/>.</summary>
    public static List<string> Names()
    {
        var all = All();
        var names = new List<string> { "Main display (over the game)" };
        for (int i = 1; i < all.Count; i++)
            names.Add(all.Count == 2 ? "Second display" : $"Display {i + 1}");
        return names;
    }
}