using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace GamingBurner;

/// <summary>Everything the main window shows. Rebuilt from scratch whenever the theme changes.</summary>
public sealed partial class MainWindow
{
    readonly Dictionary<string, TextBlock> _vals = new();
    readonly Dictionary<string, TextBlock> _subs = new();
    TextBlock _game, _stateText, _status, _trackLabel, _benchLabel;
    Border _stateDot;
    ToggleButton _trackBtn, _overlayBtn, _benchBtn, _logBtn;
    Button _adminBtn;
    Graph _fpsGraph, _ftGraph;
    ThreadBars _threads;
    bool _tracking;
    bool _benchPending; int _benchPendingTicks;

    public GpuTuning Tuning => _tuning;

    // ---------- Update prompt ----------

    Velopack.UpdateInfo _updateInfo;
    bool _updateDismissed, _updating;
    Border _updateBar;
    TextBlock _updateText, _updateNotes;
    Button _updateBtn, _laterBtn;

    /// <summary>A strip under the header that appears when a newer release is on GitHub.</summary>
    Border BuildUpdateBar()
    {
        _updateText = new TextBlock { FontSize = 13.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        _updateNotes = new TextBlock { FontSize = 12, Foreground = Theme.Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(_updateText); texts.Children.Add(_updateNotes);
        _updateBtn = new Button { Content = "Update and restart", Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(16, 0, 8, 0) };
        _updateBtn.Click += (_, _) => InstallUpdate();
        _laterBtn = new Button { Content = "Later", Padding = new Thickness(16, 7, 16, 7) };
        _laterBtn.Click += (_, _) => { _updateDismissed = true; RefreshUpdateBar(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(_updateBtn); buttons.Children.Add(_laterBtn);
        var dock = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        dock.Children.Add(buttons);
        var icon = Theme.Glyph("\uE896", 18); icon.Foreground = Theme.Accent; icon.Margin = new Thickness(0, 0, 14, 0);
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);
        dock.Children.Add(texts);
        _updateBar = new Border
        {
            Background = Theme.Panel, BorderBrush = Theme.Accent, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18, 12, 14, 12), Margin = new Thickness(0, 0, 0, 16), Child = dock
        };
        RefreshUpdateBar();
        return _updateBar;
    }

    void RefreshUpdateBar()
    {
        if (_updateBar == null) return;
        bool show = _updateInfo != null && (!_updateDismissed || _updating);
        _updateBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        var target = _updateInfo.TargetFullRelease;
        if (!_updating) _updateText.Text = $"GamingBurner {target.Version} is available. You have {Updates.CurrentVersion}.";
        string notes = string.Join("  ·  ", (target.NotesMarkdown ?? "")
            .Split('\n').Select(l => l.Trim().TrimStart('#', '-', '*', ' ')).Where(l => l.Length > 0).Take(3));
        _updateNotes.Text = notes;
        _updateNotes.Visibility = notes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _updateBtn.IsEnabled = _laterBtn.IsEnabled = !_updating;
    }

    /// <summary>Looks for a newer release. Returns a short message for the Properties window.</summary>
    public async Task<string> CheckForUpdatesNow(bool userAsked)
    {
        if (!Updates.Installed) return "Update checks work in the installed app, not in a development build.";
        try
        {
            var info = await Updates.CheckAsync();
            if (info == null) return $"You have the latest version ({Updates.CurrentVersion}).";
            bool isNew = _updateInfo == null || _updateInfo.TargetFullRelease.Version != info.TargetFullRelease.Version;
            _updateInfo = info;
            if (isNew || userAsked) _updateDismissed = false;
            RefreshUpdateBar();
            return $"Version {info.TargetFullRelease.Version} is available.";
        }
        catch (Exception ex) { return "Could not check for updates: " + ex.Message; }
    }

    async void InstallUpdate()
    {
        if (_updateInfo == null || _updating) return;
        _updating = true;
        RefreshUpdateBar();
        var info = _updateInfo;
        try
        {
            _updateText.Text = "Downloading the update…";
            await Updates.DownloadAsync(info, p => Dispatcher.BeginInvoke(() => _updateText.Text = $"Downloading the update… {p}%"));
            _updateText.Text = "Installing. GamingBurner will reopen in a moment.";
            Updates.ApplyAfterExit(info);
            // Closing runs the normal shutdown: a recording is saved, fans go back to automatic, capture stops.
            Close();
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _updating = false;
            RefreshUpdateBar();
            _updateText.Text = "The update could not be installed: " + ex.Message;
        }
    }

    void BuildUi()
    {
        Background = Theme.Bg; Foreground = Theme.Text;
        Resources.Clear();
        Theme.ApplyAccent(Resources);
        _vals.Clear(); _subs.Clear();

        var root = new Grid { Margin = new Thickness(32, 24, 32, 22) };
        for (int i = 0; i < 5; i++) root.RowDefinitions.Add(new RowDefinition { Height = i == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

        // ---------- Header: what is being measured, and the three global controls ----------
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 20) };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _trackLabel = new TextBlock();
        _trackBtn = new ToggleButton { Content = _trackLabel, Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(0, 0, 8, 0), IsChecked = _tracking,
            ToolTip = "Start or stop live tracking. While stopped, GamingBurner does no measuring at all." };
        _trackBtn.Click += (_, _) => SetTracking(_trackBtn.IsChecked == true);
        var theme = new Button { Content = Theme.Glyph(Theme.P.Dark ? "" : "", 15), Padding = new Thickness(11, 8, 11, 8), Margin = new Thickness(0, 0, 8, 0),
            ToolTip = Theme.P.Dark ? "Switch to light mode" : "Switch to dark mode" };
        theme.Click += (_, _) => SetTheme(Theme.P.Dark ? "Light" : "Dark");
        var propsContent = new StackPanel { Orientation = Orientation.Horizontal };
        propsContent.Children.Add(Theme.Glyph("", 14));
        propsContent.Children.Add(new TextBlock { Text = "Properties", Margin = new Thickness(8, 0, 0, 0) });
        var props = new Button { Content = propsContent, Padding = new Thickness(14, 7, 16, 7) };
        props.Click += (_, _) => OpenProperties(0);
        _adminBtn = new Button { Content = "Run as administrator", Padding = new Thickness(14, 7, 16, 7), Margin = new Thickness(0, 0, 8, 0), Visibility = Visibility.Collapsed,
            ToolTip = "Windows only lets administrators measure other programs' frames. Properties → General can make this automatic." };
        _adminBtn.Click += (_, _) => RestartElevated();
        controls.Children.Add(_adminBtn);
        controls.Children.Add(_trackBtn); controls.Children.Add(theme); controls.Children.Add(props);
        DockPanel.SetDock(controls, Dock.Right);
        header.Children.Add(controls);
        try
        {
            var logo = new Image
            {
                Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/GamingBurner-256.png")),
                Width = 40, Height = 40, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
            DockPanel.SetDock(logo, Dock.Left);
            header.Children.Add(logo);
        }
        catch { /* the app still works without its artwork */ }
        _game = new TextBlock { FontFamily = Theme.Display, FontSize = 22, FontWeight = FontWeights.SemiBold, Text = "GamingBurner" };
        _stateDot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Theme.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 7, 0) };
        _stateText = new TextBlock { FontSize = 12.5, Foreground = Theme.Dim };
        var state = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        state.Children.Add(_stateDot); state.Children.Add(_stateText);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(_game); titles.Children.Add(state);
        header.Children.Add(titles);
        var top = new StackPanel();
        top.Children.Add(header);
        top.Children.Add(BuildUpdateBar());
        root.Children.Add(top);

        // ---------- The six numbers that matter most ----------
        var kpis = new Grid();
        (string key, string label, Brush color)[] cells =
        {
            ("fps", "Frame rate", Theme.Accent), ("avg", "Average", Theme.Text), ("low1", "1% low", Theme.Text),
            ("low01", "0.1% low", Theme.Text), ("lat", "Latency", Theme.Text), ("limit", "Limited by", Theme.Text),
        };
        for (int i = 0; i < cells.Length; i++)
        {
            kpis.ColumnDefinitions.Add(new ColumnDefinition());
            var value = Theme.Number(i == 5 ? 24 : 34, cells[i].color);
            value.LineHeight = 40; value.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            if (i == 5) value.Margin = new Thickness(0, 4, 0, 0);
            var sub = new TextBlock { FontSize = 11.5, Foreground = Theme.Faint, Text = " " };
            _vals[cells[i].key] = value; _subs[cells[i].key] = sub;
            var sp = new StackPanel { Margin = new Thickness(i == 0 ? 0 : 22, 0, 0, 0) };
            sp.Children.Add(new TextBlock { Text = cells[i].label, FontSize = 12.5, Foreground = Theme.Dim, Margin = new Thickness(0, 0, 0, 2) });
            sp.Children.Add(value); sp.Children.Add(sub);
            var cell = new Border { BorderBrush = Theme.Line, BorderThickness = new Thickness(i == 0 ? 0 : 1, 0, 0, 0), Child = sp };
            Grid.SetColumn(cell, i);
            kpis.Children.Add(cell);
        }
        var kpiCard = Theme.Surface(kpis, new Thickness(24, 18, 24, 16));
        Grid.SetRow(kpiCard, 1);
        root.Children.Add(kpiCard);

        // ---------- Two graphs ----------
        _fpsGraph = new Graph { Title = "Frame rate over the last two minutes", Unit = "fps", Format = "0", Stroke = () => Theme.Accent };
        _ftGraph = new Graph { Title = "Frame time of the last 1,000 frames", Unit = "ms", Format = "0.00", Stroke = () => Theme.Warn };
        var graphs = new Grid { Margin = new Thickness(0, 14, 0, 14) };
        graphs.ColumnDefinitions.Add(new ColumnDefinition()); graphs.ColumnDefinitions.Add(new ColumnDefinition());
        var g1 = Theme.Surface(_fpsGraph, new Thickness(24, 18, 24, 18)); g1.Margin = new Thickness(0, 0, 7, 0);
        var g2 = Theme.Surface(_ftGraph, new Thickness(24, 18, 24, 18)); g2.Margin = new Thickness(7, 0, 0, 0);
        Grid.SetColumn(g2, 1);
        graphs.Children.Add(g1); graphs.Children.Add(g2);
        Grid.SetRow(graphs, 2);
        root.Children.Add(graphs);

        // ---------- Four plain lists ----------
        _threads = new ThreadBars { Height = 22, Margin = new Thickness(0, 9, 0, 0) };
        var lists = new Grid();
        UIElement[] columns =
        {
            List("Latency", Row("PC latency", "pclat"), Row("Click to screen", "click"), Row("Frame to screen", "fts"),
                Row("Shown on screen", "shown"), Row("Dropped frames", "drop"), Row("Worst frame", "worst")),
            List("Graphics card", Row("Load", "gpu"), Row("Temperature", "gtemp"), Row("Power", "gpow"),
                Row("Video memory", "vram"), Row("Clock", "gclk"), Row("Fan", "fan")),
            List("Processor", Row("Load", "cpu"), Row("Busiest thread", "cpumax"), Row("Clock speed", "cclk"),
                Row("Temperature", "ctemp"), Row("Used by the game", "gamecpu"), _threads),
            List("System", Row("Memory", "ram"), Row("Used by the game", "gameram"), Row("Disk busy", "disk"),
                Row("Paging", "page"), Row("Ping", "net"), Row("Packet loss", "loss")),
        };
        for (int i = 0; i < columns.Length; i++)
        {
            lists.ColumnDefinitions.Add(new ColumnDefinition());
            var card = Theme.Surface(columns[i], new Thickness(20, 16, 20, 14));
            card.Margin = new Thickness(i == 0 ? 0 : 7, 0, i == columns.Length - 1 ? 0 : 7, 0);
            Grid.SetColumn(card, i);
            lists.Children.Add(card);
        }
        Grid.SetRow(lists, 3);
        root.Children.Add(lists);

        // ---------- Footer: recording and output ----------
        var footer = new DockPanel { Margin = new Thickness(0, 18, 0, 0) };
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        _benchLabel = new TextBlock();
        _benchBtn = new ToggleButton { Content = _benchLabel, Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(0, 0, 8, 0), IsChecked = _tracker.Benchmarking || _benchPending };
        _benchBtn.Click += (_, _) => ToggleBenchmark();
        _overlayBtn = new ToggleButton { Content = "On-screen display", Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(0, 0, 8, 0), IsChecked = _overlay != null };
        _overlayBtn.Click += (_, _) => ToggleOverlay();
        _logBtn = new ToggleButton { Content = "Log to file", Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(0, 0, 8, 0), IsChecked = _log != null };
        _logBtn.Click += (_, _) => ToggleLog();
        var open = new Button { Content = "Open logs", Padding = new Thickness(16, 7, 16, 7) };
        open.Click += (_, _) => Process.Start("explorer.exe", Program.DataDir);
        bar.Children.Add(_benchBtn); bar.Children.Add(_overlayBtn); bar.Children.Add(_logBtn); bar.Children.Add(open);
        DockPanel.SetDock(bar, Dock.Left);
        footer.Children.Add(bar);
        _status = new TextBlock { Foreground = Theme.Dim, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(16, 0, 0, 0) };
        footer.Children.Add(_status);
        Grid.SetRow(footer, 4);
        root.Children.Add(footer);

        Content = root;
        UpdateButtons();
    }

    StackPanel List(string title, params UIElement[] rows)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        foreach (var r in rows) sp.Children.Add(r);
        return sp;
    }

    Grid Row(string label, string key)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new TextBlock { Text = label, Foreground = Theme.Dim, FontSize = 12.5, Margin = new Thickness(0, 0, 10, 0) });
        var v = Theme.Number(12.5);
        v.HorizontalAlignment = HorizontalAlignment.Right; v.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(v, 1);
        g.Children.Add(v);
        _vals[key] = v;
        return g;
    }

    /// <summary>Button captions and tooltips that depend on state and on the chosen shortcuts.</summary>
    void UpdateButtons()
    {
        if (_trackBtn == null) return;
        _trackBtn.IsChecked = _tracking;
        _trackLabel.Text = _tracking ? "Live tracking on" : "Start live tracking";
        bool recording = _tracker.Benchmarking || _benchPending;
        _benchBtn.IsChecked = recording;
        _benchLabel.Text = recording ? "Stop recording" : "Record benchmark";
        _benchBtn.ToolTip = "Shortcut: " + _settings.BenchmarkKey;
        _overlayBtn.ToolTip = "Shortcut: " + _settings.OverlayKey;
    }

    void ApplyTitleBar()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;
        int dark = Theme.P.Dark ? 1 : 0;
        DwmSetWindowAttribute(h, 20, ref dark, sizeof(int));
    }

    /// <summary>Switches between light and dark and redraws the window in the new colours.</summary>
    public void SetTheme(string name)
    {
        _settings.Theme = name == "Light" ? "Light" : "Dark";
        _settings.Save();
        Theme.Use(_settings.Theme);
        BuildUi();
        ApplyTitleBar();
    }

    public void OpenProperties(int page) => new PropertiesWindow(this, page) { Owner = this }.ShowDialog();

    /// <summary>
    /// Starts or stops live tracking. Stopped means no frame capture, no latency helper, no sensor
    /// reads and no pings: the app sits idle until tracking is started again or a recording asks for it.
    /// </summary>
    public void SetTracking(bool on)
    {
        if (on && _tracking && _etw != null) { UpdateButtons(); return; }
        if (on)
        {
            _etw = new EtwPresentSource(_tracker);
            if (!_etw.Start()) { _status.Foreground = Theme.Bad; _status.Text = "Frame capture failed: " + _etw.Error; }
            _sensors.Paused = false;
            _net.Enabled = _settings.NetworkOn;
            _overlay?.Show();
        }
        else
        {
            if (_tracker.Benchmarking) FinishBenchmark();
            _benchPending = false;
            if (_log != null) { _log.Dispose(); _log = null; }
            _etw?.Dispose(); _etw = null;
            _latency.Track(0);
            _sensors.Paused = true;
            _net.Enabled = false;
            _pid = 0;
            _session.Reset();
            _fpsHist.Clear();
            _overlay?.Hide();
        }
        _tracking = on;
        _settings.Tracking = on;
        _settings.Save();
        if (_logBtn != null) _logBtn.IsChecked = _log != null;
        UpdateButtons();
        if (!on) ShowIdle();
    }

    long Lost() => _etw?.QueryLostEvents() ?? 0;

    void ShowIdle()
    {
        _game.Text = "GamingBurner";
        _stateDot.Background = Theme.Faint;
        _stateText.Text = "Live tracking is off. Nothing is being measured.";
        foreach (var kv in _vals) { kv.Value.Text = "–"; kv.Value.Foreground = kv.Key == "fps" ? Theme.Accent : Theme.Text; }
        foreach (var s in _subs.Values) s.Text = " ";
        _fpsGraph.Data = Array.Empty<float>(); _ftGraph.Data = Array.Empty<float>();
        _fpsGraph.InvalidateVisual(); _ftGraph.InvalidateVisual();
        _threads.Values = Array.Empty<double>(); _threads.InvalidateVisual();
    }

    /// <summary>Fills in every number on screen from the latest readings.</summary>
    void UpdateView(FrameStats f, HwSample h)
    {
        var l = _extra.L; var n = _extra.N;

        // Header
        _game.Text = f.Valid ? f.Process : "Waiting for a game";
        _adminBtn.Visibility = _etw?.AccessDenied == true && !HwMonitor.IsElevated ? Visibility.Visible : Visibility.Collapsed;
        if (_etw?.Error != null) { _stateDot.Background = Theme.Bad; _stateText.Text = _etw.AccessDenied ? _etw.Error + ". Use Run as administrator." : "Frame capture is unavailable: " + _etw.Error; }
        else if (_lost > 0) { _stateDot.Background = Theme.Warn; _stateText.Text = $"Tracking · {_lost:N0} timing events missed"; }
        else if (f.Valid) { _stateDot.Background = Theme.Accent; _stateText.Text = "Tracking every frame · none missed"; }
        else { _stateDot.Background = Theme.Faint; _stateText.Text = "Tracking is on. Start a game and it is picked up automatically."; }

        // The six headline numbers
        if (f.Valid)
        {
            Set("fps", f.Fps.ToString("0")); _subs["fps"].Text = $"{f.FrameMs:0.00} ms per frame";
            Set("avg", f.AvgLong.ToString("0")); _subs["avg"].Text = "last 30 seconds";
            Set("low1", f.Low1.ToString("0")); _subs["low1"].Text = "99% of frames are faster";
            Set("low01", f.Low01.ToString("0")); _subs["low01"].Text = "99.9% are faster";
            Set("worst", f.MaxMs.ToString("0.0") + " ms");
            Push(_fpsHist, f.Fps);
            _ftGraph.Data = _tracker.RecentFrameTimes(_pid, 1000);
        }
        else
        {
            foreach (var k in new[] { "fps", "avg", "low1", "low01", "worst" }) Set(k, "–");
            foreach (var k in new[] { "fps", "avg", "low1", "low01" }) _subs[k].Text = " ";
        }

        // Latency
        string[] latKeys = { "pclat", "click", "fts", "shown", "drop" };
        if (!_settings.LatencyOn || !_latency.Available || !l.Valid)
        {
            string why = !_settings.LatencyOn ? "off" : !_latency.Available ? "engine missing" : "–";
            foreach (var k in latKeys) Set(k, why, why == "–" ? Theme.Text : Theme.Faint);
            Set("lat", "–"); _subs["lat"].Text = !_settings.LatencyOn ? "latency tracking is off" : " ";
            Set("limit", "–"); _subs["limit"].Text = " ";
        }
        else
        {
            bool reported = !double.IsNaN(l.PcLatencyMs);
            double headline = reported ? l.PcLatencyMs : l.FrameToScreenMs;
            Set("lat", double.IsNaN(headline) ? "–" : headline.ToString("0.0"));
            _subs["lat"].Text = reported ? "ms, input to screen" : "ms, frame to screen";
            Set("limit", string.IsNullOrEmpty(l.Limit) ? "–" : l.Limit);
            _subs["limit"].Text = double.IsNaN(l.GpuBusyShare) ? " " : $"graphics card busy {l.GpuBusyShare * 100:0}%";
            Set("pclat", reported ? l.PcLatencyMs.ToString("0.0") + " ms" : "not reported", reported ? Theme.Text : Theme.Faint);
            Set("click", double.IsNaN(l.ClickMs) ? "click in the game" : l.ClickMs.ToString("0.0") + " ms", double.IsNaN(l.ClickMs) ? Theme.Faint : Theme.Text);
            Set("fts", double.IsNaN(l.FrameToScreenMs) ? "–" : l.FrameToScreenMs.ToString("0.0") + " ms", Theme.Text);
            Set("shown", l.ShownFps.ToString("0") + " fps", Theme.Text);
            Set("drop", l.DroppedPct.ToString("0.0") + " %", l.DroppedPct >= 5 ? Theme.Warn : Theme.Text);
        }

        // Graphics card
        if (h.GpuOk)
        {
            Set("gpu", h.GpuUtil.ToString("0") + " %", Theme.Text);
            Set("gtemp", double.IsNaN(h.GpuHotspot) ? $"{h.GpuTemp:0} °C" : $"{h.GpuTemp:0} °C · hot spot {h.GpuHotspot:0}", Heat(h.GpuTemp, 78, 86));
            Set("gpow", h.GpuPowerW.ToString("0") + " W");
            Set("vram", $"{h.VramUsedMb / 1024:0.0} of {h.VramTotalMb / 1024:0.0} GB", Heat(h.VramUsedMb / Math.Max(1, h.VramTotalMb) * 100, 85, 95));
            Set("gclk", $"{h.GpuClockMhz:0} MHz");
            Set("fan", double.IsNaN(h.GpuFanRpm) ? h.GpuFanPct.ToString("0") + " %" : $"{h.GpuFanPct:0} % · {h.GpuFanRpm:0} rpm");
        }
        else Set("gpu", "unavailable", Theme.Faint);

        // Processor
        if (h.CpuThreads != null)
        {
            Set("cpu", h.CpuTotal.ToString("0") + " %");
            Set("cpumax", h.CpuMaxThread.ToString("0") + " %", Heat(h.CpuMaxThread, 90, 97));
            _threads.Values = h.CpuThreads;
            _threads.InvalidateVisual();
        }
        Set("cclk", h.CpuClockMhz > 0 ? (h.CpuClockMhz / 1000).ToString("0.00") + " GHz" : "–");
        if (!double.IsNaN(h.CpuTemp)) Set("ctemp", h.CpuTemp.ToString("0") + " °C", Heat(h.CpuTemp, 85, 93));
        else Set("ctemp", HwMonitor.IsElevated ? "needs PawnIO" : "administrator only", Theme.Faint);
        Set("gamecpu", _pid != 0 ? h.GameCpuCores.ToString("0.0") + " cores" : "–");

        // System
        if (h.RamTotalGb > 0)
            Set("ram", $"{h.RamUsedGb:0.0} of {h.RamTotalGb:0.0} GB", Heat(h.RamUsedGb / h.RamTotalGb * 100, 85, 93));
        Set("gameram", _pid != 0 && h.GameRamMb > 0 ? (h.GameRamMb / 1024).ToString("0.0") + " GB" : "–");
        Set("disk", h.DiskActivePct.ToString("0") + " %");
        // Sustained paging means Windows is short of memory and is reading it back from the drive.
        Set("page", h.HardFaultsPerSec.ToString("0") + " pages/s", Heat(h.HardFaultsPerSec, 200, 1000));
        if (!_settings.NetworkOn) { Set("net", "off", Theme.Faint); Set("loss", "off", Theme.Faint); }
        else if (!n.Valid) { Set("net", "–", Theme.Text); Set("loss", "–", Theme.Text); }
        else
        {
            if (double.IsNaN(n.PingMs)) Set("net", "no reply", Theme.Bad);
            else Set("net", $"{n.PingMs:0} ms · jitter {n.JitterMs:0.0}", Heat(n.PingMs, 60, 120));
            Set("loss", n.LossPct.ToString("0") + " %", n.LossPct > 0 ? Theme.Bad : Theme.Text);
        }

        _fpsGraph.Data = _fpsHist.ToArray();
        _fpsGraph.InvalidateVisual(); _ftGraph.InvalidateVisual();
    }
}
