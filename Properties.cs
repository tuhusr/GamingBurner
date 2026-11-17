using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace GamingBurner;

/// <summary>
/// The Properties window: every customisation in one place, arranged as pages in a sidebar.
/// Changes take effect and are saved as they are made, so there is no Apply step.
/// </summary>
public sealed class PropertiesWindow : Window
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static readonly string[] Palette = { "#3DD6A8", "#F2B544", "#F0616D", "#5EB0FF", "#B594FF", "#E8ECF2" };

    readonly MainWindow _host;
    readonly ContentControl _page = new();
    readonly List<(Border item, TextBlock text, Border bar)> _nav = new();
    readonly (string name, string blurb, Func<UIElement> build)[] _pages;
    Settings S => _host.Settings;

    public PropertiesWindow(MainWindow host, int startPage = 0)
    {
        _host = host;
        Title = "GamingBurner properties";
        Width = 900; Height = 680; MinWidth = 780; MinHeight = 520;
        Background = Theme.Bg; Foreground = Theme.Text; FontFamily = Theme.Sans; FontSize = 13;
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Theme.ApplyAccent(Resources);
        try { Icon = host.Icon; } catch { }

        _pages = new (string, string, Func<UIElement>)[]
        {
            ("General", "How the app starts and how often it reads your hardware.", BuildGeneral),
            ("Monitoring", "Choose what appears on the on-screen display, and what each item is called and coloured.", BuildMonitoring),
            ("On-screen display", "Where the display sits over the game and how it looks.", BuildOsd),
            ("Benchmark", "Where recordings are saved and what they include.", BuildBenchmark),
            ("Hotkeys", "Keyboard shortcuts that work while the game is in front.", BuildHotkeys),
            ("Tuning", "Fan speed and clock offsets for the graphics card.", BuildTuning),
        };

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(216) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        // ----- Sidebar -----
        var side = new StackPanel { Margin = new Thickness(14, 20, 10, 16) };
        var t = Theme.Label("PROPERTIES", 11, Theme.Accent); t.Margin = new Thickness(12, 0, 0, 12);
        side.Children.Add(t);
        for (int i = 0; i < _pages.Length; i++)
        {
            int index = i;
            var bar = new Border { Width = 3, CornerRadius = new CornerRadius(2), Background = Theme.Accent, Margin = new Thickness(0, 9, 0, 9), Visibility = Visibility.Hidden };
            var text = new TextBlock { Text = _pages[i].name, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            var row = new DockPanel();
            DockPanel.SetDock(bar, Dock.Left);
            row.Children.Add(bar); row.Children.Add(text);
            var item = new Border { CornerRadius = new CornerRadius(7), Height = 38, Margin = new Thickness(0, 0, 0, 3), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = row };
            item.MouseLeftButtonUp += (_, _) => Select(index);
            item.MouseEnter += (_, _) => { if (item.Tag == null) item.Background = Theme.Panel; };
            item.MouseLeave += (_, _) => { if (item.Tag == null) item.Background = Brushes.Transparent; };
            _nav.Add((item, text, bar));
            side.Children.Add(item);
        }
        var sideWrap = new Border { Background = Theme.Side, BorderBrush = Theme.Line, BorderThickness = new Thickness(0, 0, 1, 0), Child = side };
        root.Children.Add(sideWrap);

        // ----- Content -----
        var content = new DockPanel { Margin = new Thickness(28, 22, 10, 16) };
        var footer = new DockPanel { Margin = new Thickness(0, 12, 18, 0) };
        var close = new Button { Content = "Close", Padding = new Thickness(22, 6, 22, 6), IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        footer.Children.Add(close);
        footer.Children.Add(new TextBlock { Text = "Changes are saved as you make them.", Foreground = Theme.Faint, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(footer, Dock.Bottom);
        content.Children.Add(footer);
        content.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _page, Padding = new Thickness(0, 0, 18, 0) });
        Grid.SetColumn(content, 1);
        root.Children.Add(content);
        Content = root;

        SourceInitialized += (_, _) =>
        {
            int dark = 1;
            dark = Theme.P.Dark ? 1 : 0;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
        };
        Select(Math.Clamp(startPage, 0, _pages.Length - 1));
    }

    void Select(int index)
    {
        for (int i = 0; i < _nav.Count; i++)
        {
            bool on = i == index;
            _nav[i].item.Tag = on ? "selected" : null;
            _nav[i].item.Background = on ? Theme.PanelHi : Brushes.Transparent;
            _nav[i].text.Foreground = on ? Theme.Text : Theme.Dim;
            _nav[i].text.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            _nav[i].bar.Visibility = on ? Visibility.Visible : Visibility.Hidden;
        }
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = _pages[index].name, FontFamily = Theme.Display, FontSize = 24, FontWeight = FontWeights.SemiBold });
        sp.Children.Add(new TextBlock { Text = _pages[index].blurb, Foreground = Theme.Dim, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 14) });
        sp.Children.Add(_pages[index].build());
        _page.Content = sp;
    }

    void Changed() => _host.ApplySettings();

    // ---------- Building blocks ----------

    /// <summary>One setting: a title and explanation on the left, its control on the right.</summary>
    static Border Row(string title, string description, FrameworkElement control)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0) };
        texts.Children.Add(new TextBlock { Text = title, FontSize = 13.5 });
        if (!string.IsNullOrEmpty(description))
            texts.Children.Add(new TextBlock { Text = description, Foreground = Theme.Dim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        g.Children.Add(texts);
        control.VerticalAlignment = VerticalAlignment.Center;
        control.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return new Border { BorderBrush = Theme.Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 13, 0, 13), Child = g };
    }

    static Border Group(params UIElement[] rows)
    {
        var sp = new StackPanel();
        foreach (var r in rows) sp.Children.Add(r);
        if (sp.Children.Count > 0 && sp.Children[^1] is Border last) last.BorderThickness = new Thickness(0);
        return new Border
        {
            Background = Theme.Panel, BorderBrush = Theme.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 0, 0, 14), Child = sp
        };
    }

    static CheckBox Check(bool value, Action<bool> set)
    {
        var cb = new CheckBox { IsChecked = value, MinWidth = 0 };
        cb.Click += (_, _) => set(cb.IsChecked == true);
        return cb;
    }

    static ComboBox Combo(IEnumerable<string> items, string selected, Action<string> set, double width = 190)
    {
        var c = new ComboBox { Width = width };
        foreach (var i in items) c.Items.Add(i);
        c.SelectedItem = selected;
        c.SelectionChanged += (_, _) => { if (c.SelectedItem is string s) set(s); };
        return c;
    }

    // ---------- Pages ----------

    UIElement BuildGeneral()
    {
        bool startup;
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey)) startup = key?.GetValue("GamingBurner") != null;

        var poll = new Dictionary<string, int> { ["Twice a second"] = 500, ["Every second"] = 1000, ["Every 2 seconds"] = 2000 };
        string pollNow = poll.FirstOrDefault(p => p.Value == S.PollMs).Key ?? "Every second";

        FrameworkElement admin;
        if (HwMonitor.IsElevated) admin = new TextBlock { Text = "On", Foreground = Theme.Accent, FontWeight = FontWeights.SemiBold };
        else
        {
            var b = new Button { Content = "Restart as administrator", Padding = new Thickness(14, 6, 14, 6) };
            b.Click += (_, _) => { Close(); _host.RestartElevated(); };
            admin = b;
        }

        return new StackPanel
        {
            Children =
            {
                Group(
                    Row("Appearance", "Light or dark. The on-screen display over the game always stays dark so it reads against any scene.",
                        Combo(new[] { "Dark", "Light" }, S.Theme == "Light" ? "Light" : "Dark", s =>
                        {
                            // Rebuild both windows in the new colours.
                            Close();
                            _host.Dispatcher.BeginInvoke(() => { _host.SetTheme(s); _host.OpenProperties(0); });
                        }))),
                Group(
                    Row("Start with Windows", "Open GamingBurner automatically when you sign in.", Check(startup, on =>
                    {
                        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                        if (on) key.SetValue("GamingBurner", "\"" + Environment.ProcessPath + "\"");
                        else key.DeleteValue("GamingBurner", false);
                    })),
                    Row("Start minimized", "Open straight to the taskbar; the on-screen display still appears.", Check(S.StartMinimized, on => { S.StartMinimized = on; Changed(); }))),
                Group(
                    Row("Hardware polling", "How often temperatures, loads and memory are read. Frame timing is not affected: every frame is always counted.",
                        Combo(poll.Keys, pollNow, s => { S.PollMs = poll[s]; Changed(); }))),
                Group(
                    Row("Latency tracking", "Follows every frame to the display to measure latency, dropped frames and what is limiting the frame rate. Uses a little more processor time; turn it off for the lightest footprint.",
                        Check(S.LatencyOn, on => { S.LatencyOn = on; Changed(); })),
                    Row("Network test", "Sends one small ping a second to measure your connection's delay, jitter and loss. This is your connection quality, not the game server's own ping.",
                        Check(S.NetworkOn, on => { S.NetworkOn = on; Changed(); })),
                    Row("Network test address", "The address that is pinged.", PingBox())),
                Group(
                    Row("Administrator mode", "Windows needs it to measure frames on most PCs, and to change fan speed and clock offsets.", admin),
                    Row("Always start as administrator", "Reopens GamingBurner as administrator every time it starts. Windows asks for permission each time.",
                        Check(S.AlwaysAdmin, on => { S.AlwaysAdmin = on; Changed(); }))),
                Group(
                    Row("Version", "Updates come from the GamingBurner releases page on GitHub.", UpdateCheck()),
                    Row("Check for updates automatically", "When a new version is out, a prompt appears at the top of the main window.",
                        Check(S.AutoUpdateCheck, on => { S.AutoUpdateCheck = on; Changed(); }))),
            }
        };
    }

    FrameworkElement UpdateCheck()
    {
        var result = new TextBlock { Text = Updates.CurrentVersion, FontSize = 12.5, Foreground = Theme.Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), MaxWidth = 260, TextWrapping = TextWrapping.Wrap };
        var check = new Button { Content = "Check now", Padding = new Thickness(14, 6, 14, 6) };
        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            result.Text = "Checking…";
            result.Text = await _host.CheckForUpdatesNow(true);
            check.IsEnabled = true;
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(result); sp.Children.Add(check);
        return sp;
    }

    TextBox PingBox()
    {
        var box = new TextBox { Text = S.PingTarget, Width = 190, MaxLength = 60 };
        box.LostFocus += (_, _) => { S.PingTarget = box.Text.Trim().Length > 0 ? box.Text.Trim() : "1.1.1.1"; box.Text = S.PingTarget; Changed(); };
        return box;
    }

    UIElement BuildMonitoring()
    {
        var table = new StackPanel();
        var head = TableRow(
            Theme.Label("STATISTIC"), Theme.Label("SHOW"), Theme.Label("NAME ON SCREEN"), Theme.Label("COLOUR"));
        head.Padding = new Thickness(0, 10, 0, 8);
        table.Children.Add(head);

        foreach (var stat in Hud.All)
        {
            string key = stat.Key;
            string defaultHex = Hex(stat.Color);

            var show = Check(S.Stats.Contains(key), on =>
            {
                if (on) { if (!S.Stats.Contains(key)) S.Stats.Add(key); } else S.Stats.Remove(key);
                Changed();
            });
            show.HorizontalAlignment = HorizontalAlignment.Left;

            var name = new TextBox { Text = S.Labels.TryGetValue(key, out var l) && l.Length > 0 ? l : stat.Short, Width = 130, MaxLength = 14, HorizontalAlignment = HorizontalAlignment.Left };
            name.TextChanged += (_, _) =>
            {
                string v = name.Text.Trim();
                if (v.Length == 0 || v == stat.Short) S.Labels.Remove(key); else S.Labels[key] = v;
                Changed();
            };

            var swatches = new StackPanel { Orientation = Orientation.Horizontal };
            void Paint()
            {
                string current = S.Colors.TryGetValue(key, out var c) ? c : defaultHex;
                foreach (Border b in swatches.Children)
                    b.BorderBrush = string.Equals((string)b.Tag, current, StringComparison.OrdinalIgnoreCase) ? Theme.Text : Brushes.Transparent;
            }
            foreach (var hex in Palette)
            {
                var dot = new Border
                {
                    Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 0, 6, 0), Cursor = Cursors.Hand, Tag = hex,
                    BorderThickness = new Thickness(2), Padding = new Thickness(2),
                    Child = new Border { CornerRadius = new CornerRadius(8), Background = Theme.B(hex) }
                };
                dot.MouseLeftButtonUp += (_, _) =>
                {
                    if (string.Equals(hex, defaultHex, StringComparison.OrdinalIgnoreCase)) S.Colors.Remove(key); else S.Colors[key] = hex;
                    Paint();
                    Changed();
                };
                swatches.Children.Add(dot);
            }
            Paint();

            table.Children.Add(TableRow(new TextBlock { Text = stat.Name, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center }, show, name, swatches));
        }
        if (table.Children[^1] is Border lastRow) lastRow.BorderThickness = new Thickness(0);

        var reset = new Button { Content = "Restore default names and colours", Padding = new Thickness(14, 6, 14, 6), HorizontalAlignment = HorizontalAlignment.Left };
        reset.Click += (_, _) => { S.Labels.Clear(); S.Colors.Clear(); Changed(); Select(1); };

        return new StackPanel
        {
            Children =
            {
                new Border
                {
                    Background = Theme.Panel, BorderBrush = Theme.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(18, 0, 18, 2), Margin = new Thickness(0, 0, 0, 14), Child = table
                },
                reset
            }
        };
    }

    static Border TableRow(UIElement a, UIElement b, UIElement c, UIElement d)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) });
        var cells = new[] { a, b, c, d };
        for (int i = 0; i < 4; i++)
        {
            if (cells[i] is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(cells[i], i);
            g.Children.Add(cells[i]);
        }
        return new Border { BorderBrush = Theme.Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 7, 0, 7), Child = g };
    }

    static string Hex(Brush b)
    {
        var c = ((SolidColorBrush)b).Color;
        return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    UIElement BuildOsd()
    {
        var sizes = new Dictionary<string, double> { ["Small"] = 0.85, ["Medium"] = 1.0, ["Large"] = 1.25, ["Extra large"] = 1.6 };
        string sizeNow = sizes.OrderBy(p => Math.Abs(p.Value - S.OsdScale)).First().Key;

        var displayNames = Displays.Names();
        var value = Theme.Number(13); value.Text = S.OsdOpacity + " %"; value.Width = 48; value.TextAlignment = TextAlignment.Right;
        var slider = new Slider { Minimum = 0, Maximum = 100, TickFrequency = 5, IsSnapToTickEnabled = true, Value = S.OsdOpacity, Width = 200 };
        slider.ValueChanged += (_, _) => { S.OsdOpacity = (int)slider.Value; value.Text = S.OsdOpacity + " %"; Changed(); };
        var opacity = new StackPanel { Orientation = Orientation.Horizontal };
        opacity.Children.Add(slider); opacity.Children.Add(value);

        return new StackPanel
        {
            Children =
            {
                Group(
                    Row("Show the on-screen display", "Draws your chosen statistics over the game. Works when the game runs borderless or windowed.",
                        Check(S.OverlayOn, on => { S.OverlayOn = on; Changed(); }))),
                Group(
                    Row("Display", "Drawing it over the game makes Windows blend the two, which adds a few milliseconds of latency. On another display it costs nothing.",
                        Combo(displayNames, displayNames[Math.Clamp(S.OsdDisplay, 0, displayNames.Count - 1)], s => { S.OsdDisplay = displayNames.IndexOf(s); Changed(); }, 250)),
                    Row("Position", "Which corner of that display it sits in.",
                        Combo(new[] { "Top left", "Top right", "Bottom left", "Bottom right" }, S.Corner, s => { S.Corner = s; Changed(); })),
                    Row("Size", "How large the text is.", Combo(sizes.Keys, sizeNow, s => { S.OsdScale = sizes[s]; Changed(); })),
                    Row("Background", "How solid the panel behind the text is. 0 % leaves only the text.", opacity)),
                new TextBlock
                {
                    Text = "Pick which statistics appear, and their names and colours, on the Monitoring page.",
                    Foreground = Theme.Faint, FontSize = 12, TextWrapping = TextWrapping.Wrap
                }
            }
        };
    }

    UIElement BuildBenchmark()
    {
        var path = new TextBox { Text = S.BenchmarkFile, Width = 250, IsReadOnly = true };
        var browse = new Button { Content = "Browse…", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var dlg = new SaveFileDialog { Title = "Save benchmark results to", Filter = "Text file (*.txt)|*.txt", FileName = Path.GetFileName(S.BenchmarkFile), OverwritePrompt = false };
            try { dlg.InitialDirectory = Path.GetDirectoryName(S.BenchmarkFile); } catch { }
            if (dlg.ShowDialog(this) == true) { S.BenchmarkFile = dlg.FileName; path.Text = dlg.FileName; Changed(); }
        };
        var file = new StackPanel { Orientation = Orientation.Horizontal };
        file.Children.Add(path); file.Children.Add(browse);

        var open = new Button { Content = "Open results", Padding = new Thickness(14, 6, 14, 6) };
        open.Click += (_, _) =>
        {
            if (File.Exists(S.BenchmarkFile)) Process.Start(new ProcessStartInfo(S.BenchmarkFile) { UseShellExecute = true });
        };
        var reset = new Button { Content = "Reset now", Padding = new Thickness(14, 6, 14, 6) };
        reset.Click += (_, _) => _host.ResetSession();

        return new StackPanel
        {
            Children =
            {
                Group(
                    Row("Results file", "Each recording adds a summary here: average, 1% low, 0.1% low, lowest, highest.", file),
                    Row("Save every frame time", "Also writes a spreadsheet file listing each frame of the recording, next to the results file.",
                        Check(S.SaveFrameTimes, on => { S.SaveFrameTimes = on; Changed(); })),
                    Row("Past results", "", open)),
                Group(
                    Row("Highest, lowest and average", "These run from when the game was picked up. Reset them to start counting again.", reset)),
            }
        };
    }

    UIElement BuildTuning()
    {
        var tune = _host.Tuning;
        bool admin = HwMonitor.IsElevated;
        static string Signed(double v) => v > 0 ? "+" + v.ToString("0") : v.ToString("0");
        var result = new TextBlock { FontSize = 12, Foreground = Theme.Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };

        // ----- Fan -----
        var fanValue = Theme.Number(13); fanValue.Width = 52; fanValue.TextAlignment = TextAlignment.Right; fanValue.Text = "auto";
        var fanSlider = new Slider { Minimum = tune.FanMinPct, Maximum = tune.FanMaxPct, TickFrequency = 1, IsSnapToTickEnabled = true, Value = 50, Width = 220, IsEnabled = tune.FanManual };
        fanSlider.ValueChanged += (_, _) =>
        {
            if (!fanSlider.IsEnabled) return;
            double v = fanSlider.Value;
            fanValue.Text = v.ToString("0") + " %";
            Task.Run(() => { if (!tune.SetFan(v)) Dispatcher.BeginInvoke(() => { result.Foreground = Theme.Bad; result.Text = "Fan change failed: " + tune.FanError; }); });
        };
        var manual = Check(tune.FanManual, on =>
        {
            fanSlider.IsEnabled = on;
            if (on) { double v = fanSlider.Value; fanValue.Text = v.ToString("0") + " %"; Task.Run(() => tune.SetFan(v)); }
            else { fanValue.Text = "auto"; Task.Run(() => tune.FanAuto()); }
        });
        var fan = new StackPanel { Orientation = Orientation.Horizontal };
        fan.Children.Add(fanSlider); fan.Children.Add(fanValue);

        // ----- Clocks -----
        (Slider slider, StackPanel panel) ClockSlider(int min, int max, int now, double step)
        {
            var value = Theme.Number(13); value.Width = 84; value.TextAlignment = TextAlignment.Right; value.Text = Signed(now) + " MHz";
            var s = new Slider { Minimum = min, Maximum = Math.Max(max, min + 1), TickFrequency = step, IsSnapToTickEnabled = true, SmallChange = step, LargeChange = step * 5, Value = now, Width = 220 };
            s.ValueChanged += (_, _) => value.Text = Signed(s.Value) + " MHz";
            var p = new StackPanel { Orientation = Orientation.Horizontal };
            p.Children.Add(s); p.Children.Add(value);
            return (s, p);
        }
        var core = ClockSlider(tune.CoreMinMhz, tune.CoreMaxMhz, tune.CoreOffsetMhz, 5);
        var mem = ClockSlider(tune.MemMinMhz, tune.MemMaxMhz, tune.MemOffsetMhz, 10);
        void Apply(int c, int m)
        {
            bool ok = tune.Apply(c, m);
            result.Foreground = ok ? Theme.Accent : Theme.Bad;
            result.Text = ok ? $"Applied: core {Signed(c)} MHz, memory {Signed(m)} MHz." : "Clock change failed: " + tune.Error;
        }
        var apply = new Button { Content = "Apply clocks", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 8, 0) };
        apply.Click += (_, _) => Apply((int)core.slider.Value, (int)mem.slider.Value);
        var reset = new Button { Content = "Reset to stock", Padding = new Thickness(16, 6, 16, 6) };
        reset.Click += (_, _) => { core.slider.Value = 0; mem.slider.Value = 0; Apply(0, 0); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(apply); buttons.Children.Add(reset);

        // The NVIDIA driver refuses these changes unless the app runs as administrator.
        bool fanOk = admin && tune.FanAvailable, clockOk = admin && tune.Available;
        manual.IsEnabled = fanOk;
        if (!fanOk) fanSlider.IsEnabled = false;
        core.slider.IsEnabled = mem.slider.IsEnabled = apply.IsEnabled = reset.IsEnabled = clockOk;
        foreach (var s in new[] { fanSlider, core.slider, mem.slider }) s.Opacity = s.IsEnabled ? 1 : 0.4;
        fanSlider.IsEnabledChanged += (_, _) => fanSlider.Opacity = fanSlider.IsEnabled ? 1 : 0.4;

        var page = new StackPanel();
        if (!admin)
        {
            var unlock = new Button { Content = "Restart as administrator", Padding = new Thickness(14, 6, 14, 6) };
            unlock.Click += (_, _) => { Close(); _host.RestartElevated(); };
            page.Children.Add(Group(Row("Locked", "The NVIDIA driver only accepts fan and clock changes from an administrator.", unlock)));
        }
        else if (!tune.Available) { result.Foreground = Theme.Bad; result.Text = "Clock control is unavailable: " + tune.Error; }
        page.Children.Add(Group(
            Row("Set fan speed by hand", "Holds the fans at the speed you choose. Untick, or close GamingBurner, to hand them back to the card.", manual),
            Row("Fan speed", "", fan)));
        page.Children.Add(Group(
            Row("Core clock offset", "Raises or lowers the graphics processor clock.", core.panel),
            Row("Memory clock offset", "Raises or lowers the video memory clock.", mem.panel),
            Row("Apply", "Offsets take effect when you press Apply and return to stock when the PC restarts.", buttons)));
        page.Children.Add(result);
        page.Children.Add(new TextBlock
        {
            Text = "Raise clocks in small steps and test between each. Too high causes crashes or visual glitches; close the game before changing them.",
            Foreground = Theme.Faint, FontSize = 12, TextWrapping = TextWrapping.Wrap
        });
        return page;
    }

    UIElement BuildHotkeys()
    {
        Border HotkeyRow(string title, string description, string id, Hotkey hk)
        {
            var status = new TextBlock { FontSize = 11.5, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
            void Paint()
            {
                bool ok = _host.HotkeyOk.TryGetValue(id, out var v) && v;
                status.Text = ok ? "Active: " + hk : "Unavailable: another app is using " + hk;
                status.Foreground = ok ? Theme.Faint : Theme.Bad;
            }
            var mods = Combo(Hotkey.ModChoices, hk.Mods, s => { hk.Mods = s; Changed(); Paint(); }, 130);
            var key = Combo(Hotkey.KeyChoices, hk.Key, s => { hk.Key = s; Changed(); Paint(); }, 120);
            key.Margin = new Thickness(8, 0, 0, 0);
            var combos = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            combos.Children.Add(mods); combos.Children.Add(key);
            var right = new StackPanel();
            right.Children.Add(combos); right.Children.Add(status);
            Paint();
            return Row(title, description, right);
        }

        return new StackPanel
        {
            Children =
            {
                Group(
                    HotkeyRow("Show or hide the on-screen display", "", "overlay", S.OverlayKey),
                    HotkeyRow("Start or stop a benchmark", "", "benchmark", S.BenchmarkKey),
                    HotkeyRow("Reset highest, lowest and average", "", "reset", S.ResetKey)),
                new TextBlock
                {
                    Text = "A shortcut can only belong to one app at a time. If another program (MSI Afterburner uses F10, F11 and F12 by default) already holds it, choose a different combination.",
                    Foreground = Theme.Faint, FontSize = 12, TextWrapping = TextWrapping.Wrap
                }
            }
        };
    }
}
