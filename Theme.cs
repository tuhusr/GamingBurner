using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace GamingBurner;

/// <summary>One complete set of colours.</summary>
public sealed class Palette
{
    public bool Dark;
    public Brush Bg, Panel, PanelHi, Side, Line, Text, Dim, Faint, Accent, OnAccent, Warn, Bad, Blue, Violet;
}

/// <summary>Colours, type and shared building blocks. Everything reads the current palette, so switching theme is one call.</summary>
static class Theme
{
    public static readonly Palette DarkP = new()
    {
        Dark = true,
        Bg = B("#0E1014"), Panel = B("#171A1F"), PanelHi = B("#20242B"), Side = B("#121418"), Line = B("#272C34"),
        Text = B("#F1F3F6"), Dim = B("#9AA3B0"), Faint = B("#687180"),
        Accent = B("#3DD6A8"), OnAccent = B("#06281E"), Warn = B("#F2B544"), Bad = B("#F0616D"), Blue = B("#5EB0FF"), Violet = B("#B594FF"),
    };

    public static readonly Palette LightP = new()
    {
        Dark = false,
        Bg = B("#F3F4F6"), Panel = B("#FFFFFF"), PanelHi = B("#ECEEF1"), Side = B("#E9EBEF"), Line = B("#E0E3E8"),
        Text = B("#14171C"), Dim = B("#586170"), Faint = B("#8891A0"),
        Accent = B("#0B9A72"), OnAccent = B("#FFFFFF"), Warn = B("#B56A00"), Bad = B("#D03545"), Blue = B("#2172D6"), Violet = B("#6D4DD6"),
    };

    public static Palette P { get; private set; } = DarkP;

    public static Brush Bg => P.Bg; public static Brush Panel => P.Panel; public static Brush PanelHi => P.PanelHi;
    public static Brush Side => P.Side; public static Brush Line => P.Line; public static Brush Text => P.Text;
    public static Brush Dim => P.Dim; public static Brush Faint => P.Faint; public static Brush Accent => P.Accent;
    public static Brush OnAccent => P.OnAccent; public static Brush Warn => P.Warn; public static Brush Bad => P.Bad;
    public static Brush Blue => P.Blue; public static Brush Violet => P.Violet;

    public static readonly FontFamily Sans = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    /// <summary>Switches to "Light" or "Dark", including the standard Windows controls.</summary>
    public static void Use(string name)
    {
        P = name == "Light" ? LightP : DarkP;
        try
        {
#pragma warning disable WPF0001
            if (Application.Current != null) Application.Current.ThemeMode = P.Dark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001
        }
        catch { }
    }

    public static Brush B(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public static Color C(Brush b) => ((SolidColorBrush)b).Color;

    /// <summary>Uses the app's own accent on sliders, toggles, checkboxes and primary buttons instead of the Windows accent colour.</summary>
    public static void ApplyAccent(ResourceDictionary resources)
    {
        foreach (var key in new[]
        {
            "AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush",
            "SliderTrackValueFill", "SliderTrackValueFillPointerOver", "SliderTrackValueFillPressed",
            "SliderThumbBackground", "SliderThumbBackgroundPointerOver", "SliderThumbBackgroundPressed",
            "ToggleButtonBackgroundChecked", "ToggleButtonBackgroundCheckedPointerOver", "ToggleButtonBackgroundCheckedPressed",
            "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundFillCheckedPointerOver", "CheckBoxCheckBackgroundFillCheckedPressed",
            "CheckBoxCheckBackgroundStrokeChecked", "CheckBoxCheckBackgroundStrokeCheckedPointerOver",
            "ComboBoxItemPillFillBrush", "TextControlBorderBrushFocused", "ComboBoxBorderBrushFocused"
        })
            resources[key] = Accent;
        foreach (var key in new[]
        {
            "TextOnAccentFillColorPrimaryBrush", "TextOnAccentFillColorSecondaryBrush", "TextOnAccentFillColorSelectedTextBrush",
            "ToggleButtonForegroundChecked", "ToggleButtonForegroundCheckedPointerOver", "ToggleButtonForegroundCheckedPressed",
            "CheckBoxCheckGlyphForegroundChecked", "CheckBoxCheckGlyphForegroundCheckedPointerOver", "CheckBoxCheckGlyphForegroundCheckedPressed"
        })
            resources[key] = OnAccent;
        resources["SliderThumbBackgroundDisabled"] = Faint;
        resources["SliderTrackValueFillDisabled"] = Faint;
    }

    public static TextBlock Label(string text, double size = 11, Brush brush = null) => new()
    {
        Text = text, FontSize = size, Foreground = brush ?? Dim, FontFamily = Sans, FontWeight = FontWeights.SemiBold
    };

    /// <summary>Numbers that keep their width as digits change, so values do not jitter sideways.</summary>
    public static TextBlock Number(double size, Brush brush = null, FontWeight? weight = null)
    {
        var t = new TextBlock
        {
            Text = "–", FontSize = size, Foreground = brush ?? Text, FontFamily = Display,
            FontWeight = weight ?? FontWeights.SemiBold
        };
        Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
        return t;
    }

    /// <summary>A flat surface: no shadow, a hairline edge only where the background would not separate it.</summary>
    public static Border Surface(UIElement child, Thickness? padding = null) => new()
    {
        Background = Panel, CornerRadius = new CornerRadius(12),
        BorderBrush = Line, BorderThickness = new Thickness(P.Dark ? 0 : 1),
        Padding = padding ?? new Thickness(20, 16, 20, 16), Child = child
    };

    public static TextBlock Glyph(string code, double size = 14) => new()
    {
        Text = code, FontFamily = Icons, FontSize = size, VerticalAlignment = VerticalAlignment.Center
    };
}

/// <summary>A clean line graph: title and current value on top, a soft fill underneath, two hairline guides.</summary>
sealed class Graph : FrameworkElement
{
    public string Title = "", Unit = "", Format = "0.#";
    public Func<Brush> Stroke = () => Theme.Accent;
    public float[] Data = Array.Empty<float>();

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 40 || h < 40) return;
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        const double padT = 44, padB = 4;
        double gh = h - padT - padB;

        var data = Data;
        double max = 1, last = double.NaN, avg = 0, dmax = 0, dmin = 0;
        if (data.Length > 0)
        {
            dmax = dmin = data[0];
            double sum = 0;
            foreach (var v in data) { if (v > dmax) dmax = v; if (v < dmin) dmin = v; sum += v; }
            avg = sum / data.Length;
            max = Math.Max(dmax * 1.15, 1e-3);
            last = data[^1];
        }

        dc.DrawText(Txt(Title, 12.5, Theme.Dim, ppd, FontWeights.Normal), new Point(0, 0));
        if (!double.IsNaN(last))
        {
            dc.DrawText(Txt($"{last.ToString(Format)} {Unit}", 17, Theme.Text, ppd, FontWeights.SemiBold), new Point(0, 17));
            var range = Txt($"low {dmin.ToString(Format)}   average {avg.ToString(Format)}   high {dmax.ToString(Format)}",
                11.5, Theme.Faint, ppd, FontWeights.Normal);
            if (range.Width < w * 0.7) dc.DrawText(range, new Point(w - range.Width, 1));
        }

        var guide = new Pen(Theme.Line, 1); guide.Freeze();
        foreach (double frac in new[] { 0.0, 0.5, 1.0 })
        {
            double y = Math.Round(padT + gh * frac) + 0.5;
            dc.DrawLine(guide, new Point(0, y), new Point(w, y));
        }
        if (data.Length < 2) return;

        double Y(double v) => padT + gh * (1 - Math.Clamp(v / max, 0, 1));
        var line = new StreamGeometry();
        var fill = new StreamGeometry();
        using (var g = line.Open())
        using (var f = fill.Open())
        {
            f.BeginFigure(new Point(0, padT + gh), true, true);
            for (int i = 0; i < data.Length; i++)
            {
                var p = new Point(w * i / (data.Length - 1), Y(data[i]));
                if (i == 0) g.BeginFigure(p, false, false); else g.LineTo(p, true, true);
                f.LineTo(p, true, false);
            }
            f.LineTo(new Point(w, padT + gh), true, false);
        }
        line.Freeze(); fill.Freeze();
        var stroke = Stroke();
        var c = Theme.C(stroke);
        var grad = new LinearGradientBrush(Color.FromArgb((byte)(Theme.P.Dark ? 46 : 34), c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B), 90);
        grad.Freeze();
        dc.DrawGeometry(grad, null, fill);
        var pen = new Pen(stroke, 1.6) { LineJoin = PenLineJoin.Round }; pen.Freeze();
        dc.DrawGeometry(null, pen, line);
    }

    static FormattedText Txt(string s, double size, Brush b, double ppd, FontWeight weight) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Theme.Sans, FontStyles.Normal, weight, FontStretches.Normal), size, b, ppd);
}

/// <summary>One small bar per processor thread; a thread near 100% is what limits the frame rate.</summary>
sealed class ThreadBars : FrameworkElement
{
    public double[] Values = Array.Empty<double>();

    protected override void OnRender(DrawingContext dc)
    {
        int n = Values.Length;
        double w = ActualWidth, h = ActualHeight;
        if (n == 0 || w < 10) return;
        const double gap = 3;
        double bw = (w - gap * (n - 1)) / n;
        for (int i = 0; i < n; i++)
        {
            double x = i * (bw + gap), v = Math.Clamp(Values[i] / 100.0, 0, 1);
            dc.DrawRoundedRectangle(Theme.PanelHi, null, new Rect(x, 0, bw, h), 2, 2);
            double bh = Math.Max(2, h * v);
            Brush b = Values[i] >= 90 ? Theme.Bad : Values[i] >= 70 ? Theme.Warn : Theme.Violet;
            dc.DrawRoundedRectangle(b, null, new Rect(x, h - bh, bw, bh), 2, 2);
        }
    }
}
