using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SkiaSharp;

namespace Ember;

/// HSV colour popup with the theme palette on top. Reports a palette name ("a1") or "#RRGGBB[AA]", or null for "none".
sealed class ColorPicker : Popup
{
    const double SW = 236, SH = 140;
    readonly Action<string?> picked;
    readonly Rectangle hueFill = new() { Width = SW, Height = SH, RadiusX = 8, RadiusY = 8 };
    readonly Ellipse svThumb = new() { Width = 14, Height = 14, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
    readonly Border hueThumb = Knob(), alphaThumb = Knob();
    readonly Rectangle alphaFill = new() { Height = 12, Width = SW, RadiusX = 6, RadiusY = 6 };
    readonly TextBox hex = new() { Width = 120, FontFamily = new FontFamily("Cascadia Mono, JetBrains Mono, Consolas") };
    readonly Border preview = new() { Width = 30, Height = 30, CornerRadius = new CornerRadius(8), BorderBrush = MainWindow.Br("#454545"), BorderThickness = new Thickness(1) };
    double h, s, v, a = 1;
    bool quiet;

    public ColorPicker(FrameworkElement target, Theme theme, string? current, bool allowNone, Action<string?> picked)
    {
        this.picked = picked;
        PlacementTarget = target;
        TextElement.SetFontFamily(this, new FontFamily("Segoe UI Variable Text, Segoe UI"));
        Placement = PlacementMode.Bottom;
        StaysOpen = false;
        AllowsTransparency = true;
        PopupAnimation = PopupAnimation.Fade;
        VerticalOffset = 6;

        var c = Renderer.Resolve(theme, current, SKColors.White);
        (h, s, v) = ToHsv(c.Red, c.Green, c.Blue);
        a = c.Alpha / 255.0;

        var root = new StackPanel { Width = SW };
        root.Children.Add(new TextBlock { Text = L.T("Theme palette"), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = MainWindow.Br("#C5C5C5"), Margin = new Thickness(0, 0, 0, 8) });
        var pal = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var (name, _) in theme.Palette)
        {
            var col = Renderer.Resolve(theme, name, SKColors.White);
            var sw = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand,
                Background = Checker(), ToolTip = name, BorderBrush = current == name ? MainWindow.Br("#FFA066") : MainWindow.Br("#454545"), BorderThickness = new Thickness(current == name ? 2 : 1),
                Child = new Border { CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(Color.FromArgb(col.Alpha, col.Red, col.Green, col.Blue)) },
            };
            var n = name;
            sw.MouseLeftButtonUp += (_, _) => { picked(n); IsOpen = false; };
            pal.Children.Add(sw);
        }
        root.Children.Add(pal);

        var sv = new Grid { Width = SW, Height = SH, Cursor = Cursors.Cross, Background = Brushes.Transparent };
        sv.Children.Add(hueFill);
        sv.Children.Add(new Rectangle { RadiusX = 8, RadiusY = 8, Fill = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0) });
        sv.Children.Add(new Rectangle { RadiusX = 8, RadiusY = 8, Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90) });
        var svCanvas = new Canvas { IsHitTestVisible = false };
        svCanvas.Children.Add(svThumb);
        sv.Children.Add(svCanvas);
        Drag(sv, p => { s = Math.Clamp(p.X / SW, 0, 1); v = 1 - Math.Clamp(p.Y / SH, 0, 1); Emit(); });
        root.Children.Add(sv);

        var hue = new Grid { Height = 12, Margin = new Thickness(0, 12, 0, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i <= 6; i++) rainbow.GradientStops.Add(new GradientStop(FromHsv(i * 60 % 360, 1, 1, 1), i / 6.0));
        hue.Children.Add(new Rectangle { RadiusX = 6, RadiusY = 6, Fill = rainbow });
        hue.Children.Add(new Canvas { IsHitTestVisible = false, Children = { hueThumb } });
        Drag(hue, p => { h = Math.Clamp(p.X / SW, 0, 0.9999) * 360; Emit(); });
        root.Children.Add(hue);

        var alpha = new Grid { Height = 12, Margin = new Thickness(0, 10, 0, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        alpha.Children.Add(new Rectangle { RadiusX = 6, RadiusY = 6, Fill = Checker() });
        alpha.Children.Add(alphaFill);
        alpha.Children.Add(new Canvas { IsHitTestVisible = false, Children = { alphaThumb } });
        Drag(alpha, p => { a = Math.Clamp(p.X / SW, 0, 1); Emit(); });
        root.Children.Add(alpha);

        var row = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var pv = new Border { Background = Checker(), CornerRadius = new CornerRadius(8), Child = preview, Margin = new Thickness(0, 0, 8, 0) };
        row.Children.Add(pv);
        row.Children.Add(hex);
        if (allowNone)
        {
            var none = new Button { Content = L.T("None"), Style = (Style)Application.Current.MainWindow!.FindResource("Ghost"), Padding = new Thickness(10, 6, 10, 6) };
            none.Click += (_, _) => { picked(null); IsOpen = false; };
            DockPanel.SetDock(none, Dock.Right);
            row.Children.Insert(0, none);
        }
        root.Children.Add(row);
        hex.TextChanged += (_, _) =>
        {
            if (quiet) return;
            var t = hex.Text.Trim();
            if (!t.StartsWith('#')) t = "#" + t;
            if (Renderer.ParseHex(t) is SKColor pc)
            {
                (h, s, v) = ToHsv(pc.Red, pc.Green, pc.Blue);
                a = pc.Alpha / 255.0;
                Sync(false);
                picked(t.ToUpperInvariant());
            }
        };

        Child = new Border
        {
            Background = MainWindow.Br("#2D2D2D"), BorderBrush = MainWindow.Br("#454545"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(14),
            Child = root, Margin = new Thickness(10), Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Opacity = 0.55 },
        };
        Sync(true);
        IsOpen = true;
    }

    static Border Knob() => new() { Width = 8, Height = 16, CornerRadius = new CornerRadius(4), Background = Brushes.White, BorderBrush = MainWindow.Br("#202020"), BorderThickness = new Thickness(1.5), Margin = new Thickness(0, -2, 0, 0) };

    public static Brush Checker()
    {
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(MainWindow.Br("#505050"), null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        g.Children.Add(new GeometryDrawing(MainWindow.Br("#3A3A3A"), null, new GeometryGroup { Children = { new RectangleGeometry(new Rect(0, 0, 4, 4)), new RectangleGeometry(new Rect(4, 4, 4, 4)) } }));
        var b = new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute };
        b.Freeze();
        return b;
    }

    static void Drag(FrameworkElement el, Action<Point> at)
    {
        el.MouseLeftButtonDown += (_, e) => { el.CaptureMouse(); at(e.GetPosition(el)); e.Handled = true; };
        el.MouseMove += (_, e) => { if (el.IsMouseCaptured) at(e.GetPosition(el)); };
        el.MouseLeftButtonUp += (_, _) => el.ReleaseMouseCapture();
    }

    void Emit()
    {
        Sync(true);
        picked(hex.Text);
    }

    void Sync(bool updateHex)
    {
        var c = FromHsv(h, s, v, a);
        hueFill.Fill = new SolidColorBrush(FromHsv(h, 1, 1, 1));
        preview.Background = new SolidColorBrush(c);
        alphaFill.Fill = new LinearGradientBrush(Color.FromArgb(0, c.R, c.G, c.B), Color.FromArgb(255, c.R, c.G, c.B), 0);
        Canvas.SetLeft(svThumb, s * SW - 7);
        Canvas.SetTop(svThumb, (1 - v) * SH - 7);
        Canvas.SetLeft(hueThumb, h / 360 * SW - 4);
        Canvas.SetLeft(alphaThumb, a * SW - 4);
        if (updateHex)
        {
            quiet = true;
            hex.Text = c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.R:X2}{c.G:X2}{c.B:X2}{c.A:X2}";
            quiet = false;
        }
    }

    public static (double H, double S, double V) ToHsv(byte r, byte g, byte b)
    {
        double R = r / 255.0, G = g / 255.0, B = b / 255.0;
        double max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B)), d = max - min;
        double hh = d == 0 ? 0 : max == R ? 60 * (((G - B) / d) % 6) : max == G ? 60 * ((B - R) / d + 2) : 60 * ((R - G) / d + 4);
        if (hh < 0) hh += 360;
        return (hh, max == 0 ? 0 : d / max, max);
    }

    public static Color FromHsv(double hh, double ss, double vv, double aa)
    {
        double c = vv * ss, x = c * (1 - Math.Abs(hh / 60 % 2 - 1)), m = vv - c;
        var (r, g, b) = hh switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) };
        return Color.FromArgb((byte)Math.Round(aa * 255), (byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}

/// Searchable list of every sensor key with its live value.
sealed class SensorPicker : Popup
{
    public SensorPicker(FrameworkElement target, bool allowNone, Action<SensorInfo?> picked)
    {
        PlacementTarget = target;
        TextElement.SetFontFamily(this, new FontFamily("Segoe UI Variable Text, Segoe UI"));
        Placement = PlacementMode.Bottom;
        StaysOpen = false;
        AllowsTransparency = true;
        PopupAnimation = PopupAnimation.Fade;
        VerticalOffset = 6;
        var search = new TextBox { Margin = new Thickness(0, 0, 0, 8) };
        var list = new StackPanel();
        var all = Sensors.Catalog.Concat(App.Sensors.Raw).ToList();
        var snap = App.Sensors.Current;
        var rows = new List<(SensorInfo Info, FrameworkElement Row)>();
        if (allowNone)
        {
            var none = Row(null, L.T("None (no sensor)"), "", "");
            none.MouseLeftButtonUp += (_, _) => { picked(null); IsOpen = false; };
            list.Children.Add(none);
        }
        foreach (var g in all.GroupBy(x => x.Group))
        {
            list.Children.Add(new TextBlock { Text = L.T(g.Key), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = MainWindow.Br("#C5C5C5"), Margin = new Thickness(6, 10, 0, 4) });
            foreach (var info in g)
            {
                var val = snap.Str(info.Key) ?? (double.IsNaN(snap.Get(info.Key)) ? "" : snap.Get(info.Key).ToString(snap.Get(info.Key) < 10 ? "0.0" : "0", L.Culture) + " " + info.Unit);
                var r = Row(info, L.T(info.Label), info.Key, val);
                var i = info;
                r.MouseLeftButtonUp += (_, _) => { picked(i); IsOpen = false; };
                rows.Add((info, r));
                list.Children.Add(r);
            }
        }
        search.TextChanged += (_, _) =>
        {
            var q = search.Text.Trim();
            foreach (var (info, row) in rows)
                row.Visibility = q.Length == 0 || info.Label.Contains(q, StringComparison.OrdinalIgnoreCase) || info.Key.Contains(q, StringComparison.OrdinalIgnoreCase) || info.Group.Contains(q, StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Visible : Visibility.Collapsed;
        };
        var root = new DockPanel { Width = 380, Height = 420 };
        DockPanel.SetDock(search, Dock.Top);
        root.Children.Add(search);
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Child = new Border
        {
            Background = MainWindow.Br("#2D2D2D"), BorderBrush = MainWindow.Br("#454545"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12),
            Child = root, Margin = new Thickness(10), Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Opacity = 0.55 },
        };
        IsOpen = true;
        Opened += (_, _) => search.Focus();
    }

    static Border Row(SensorInfo? info, string label, string key, string value)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel();
        left.Children.Add(new TextBlock { Text = label, Foreground = MainWindow.Br("#FFFFFF"), FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis });
        if (key.Length > 0) left.Children.Add(new TextBlock { Text = key, Foreground = MainWindow.Br("#9A9A9A"), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis });
        g.Children.Add(left);
        var v = new TextBlock { Text = value, Foreground = MainWindow.Br("#C5C5C5"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        Grid.SetColumn(v, 1);
        g.Children.Add(v);
        var b = new Border { Child = g, Padding = new Thickness(8, 5, 8, 5), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        b.MouseEnter += (_, _) => b.Background = MainWindow.Br("#383838");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        return b;
    }
}

/// Mouse-wheel scrolling that glides instead of jumping 48 px: the wheel moves a target and every frame eases toward it.
/// The innermost list that can still move takes the wheel, so a page also scrolls over its text boxes and at a list's end.
/// Lists that scroll by items (CanContentScroll) keep the stock behaviour.
static class SmoothScroll
{
    sealed class Glide { public double Target, Pos; public bool Running; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ScrollViewer, Glide> glides = new();
    static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

    public static void Install() => EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.MouseWheelEvent, new MouseWheelEventHandler(OnWheel));

    static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || Keyboard.Modifiers != ModifierKeys.None) return;
        var sv = (ScrollViewer?)sender;
        while (sv != null && !CanMove(sv, e.Delta)) sv = Outer(sv);
        if (sv == null || sv.CanContentScroll) return;
        e.Handled = true;
        var g = glides.GetValue(sv, _ => new Glide());
        // Not gliding, or something else moved it (scroll bar, keyboard): start from where it is.
        if (!g.Running || Math.Abs(sv.VerticalOffset - g.Pos) > 2) g.Target = g.Pos = sv.VerticalOffset;
        g.Target = Math.Clamp(g.Target - e.Delta / 120.0 * SystemParameters.WheelScrollLines * 32, 0, sv.ScrollableHeight);
        if (g.Running) return;
        g.Running = true;
        double last = clock.Elapsed.TotalSeconds;
        EventHandler? tick = null;
        tick = (_, _) =>
        {
            double now = clock.Elapsed.TotalSeconds, dt = Math.Min(0.05, now - last);
            last = now;
            bool moved = Math.Abs(sv.VerticalOffset - g.Pos) > 2;
            g.Pos += (g.Target - g.Pos) * (1 - Math.Exp(-dt / 0.075));
            if (Math.Abs(g.Target - g.Pos) < 0.5) g.Pos = g.Target;
            if (!moved) sv.ScrollToVerticalOffset(g.Pos);
            if (moved || g.Pos == g.Target || !sv.IsVisible)
            {
                CompositionTarget.Rendering -= tick;
                g.Running = false;
            }
        };
        CompositionTarget.Rendering += tick;
    }

    static bool CanMove(ScrollViewer sv, int delta)
    {
        if (sv.ScrollableHeight <= 0) return false;
        double at = glides.TryGetValue(sv, out var g) && g.Running ? g.Target : sv.VerticalOffset;
        return delta > 0 ? at > 0 : at < sv.ScrollableHeight;
    }

    static ScrollViewer? Outer(DependencyObject d)
    {
        for (var p = VisualTreeHelper.GetParent(d); p != null; p = VisualTreeHelper.GetParent(p))
            if (p is ScrollViewer sv) return sv;
        return null;
    }
}
