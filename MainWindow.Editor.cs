using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SkiaSharp;
using Shapes = System.Windows.Shapes;

namespace Ember;

/// The layout editor: live canvas with drag/resize/snap, layer list, property panel, undo, live push to the panel.
public partial class MainWindow
{
    Theme? draft;
    Renderer? editR;
    bool editRDirty = true, editFirst = true, layersQuiet, fieldsQuiet, live;
    readonly SKBitmap editFrame = new(W, H, SKColorType.Bgra8888, SKAlphaType.Premul);
    readonly WriteableBitmap editBmp = new(W, H, 96, 96, PixelFormats.Pbgra32, null);
    int sel = -1, hover = -1;
    readonly List<string> undo = new();
    int undoAt = -1;
    string savedJson = "";
    readonly DispatcherTimer commitTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    readonly DispatcherTimer liveTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    CheckBox? liveBox;

    readonly Shapes.Rectangle selRect = new() { Stroke = Br("#FFA066"), StrokeThickness = 1, Visibility = Visibility.Collapsed };
    readonly Shapes.Rectangle hoverRect = new() { Stroke = Br("#80FFFFFF"), StrokeThickness = 0.8, StrokeDashArray = new DoubleCollection { 3, 3 }, Visibility = Visibility.Collapsed };
    readonly Shapes.Rectangle[] handles = Enumerable.Range(0, 8).Select(_ => new Shapes.Rectangle { Width = 6, Height = 6, Fill = Br("#FFFFFF"), Stroke = Br("#FFA066"), StrokeThickness = 1, Visibility = Visibility.Collapsed }).ToArray();
    readonly Shapes.Line guideV = new() { Stroke = Br("#FF3DAE"), StrokeThickness = 0.8, Y1 = -20, Y2 = H + 20, Visibility = Visibility.Collapsed };
    readonly Shapes.Line guideH = new() { Stroke = Br("#FF3DAE"), StrokeThickness = 0.8, X1 = -20, X2 = W + 20, Visibility = Visibility.Collapsed };
    readonly TextBlock sizeText = new() { FontSize = 8, Foreground = Br("#1A1208"), FontWeight = FontWeights.SemiBold };
    Border? sizeLabel;

    int dragMode, dragHandle;
    Point dragStart;
    float ox, oy, ow, oh;
    SKRect ob;
    bool dragMoved;
    TextBox? fx, fy, fw, fh;

    bool EditorDirty => draft != null && draft.ToJson() != savedJson;

    static readonly string[] BoxTypes = ["rect", "glow", "image", "bar", "gauge", "graph", "cores"];

    static readonly Dictionary<string, (string Label, string Glyph)> TypeInfo = new()
    {
        ["text"] = ("Text", ""), ["gauge"] = ("Gauge", ""), ["bar"] = ("Bar", ""), ["graph"] = ("Graph", ""),
        ["cores"] = ("Cores", ""), ["icon"] = ("Icon", ""), ["rect"] = ("Rectangle", ""), ["glow"] = ("Glow", ""), ["image"] = ("Image", ""),
    };

    static readonly (string Glyph, string Name)[] Glyphs =
    [
        ("E950", "Processor"), ("E964", "Card or memory"), ("E9CA", "Temperature"), ("E945", "Power"), ("E9D9", "Activity"), ("E95E", "Pulse"),
        ("E74B", "Download"), ("E74A", "Upload"), ("E8CB", "Up and down"), ("E968", "Network"), ("E701", "Wi-Fi"), ("E704", "Antenna"),
        ("EDA2", "Disk"), ("E7F4", "Monitor"), ("E7F8", "Laptop"), ("E917", "Clock"), ("E916", "Stopwatch"), ("E787", "Calendar"),
        ("E706", "Sun"), ("E708", "Moon"), ("E7E8", "Power button"), ("E713", "Settings"), ("E9F5", "Gears"), ("E8D6", "Music"),
        ("E767", "Speaker"), ("E7F6", "Headphones"), ("E7FC", "Gamepad"), ("E734", "Star outline"), ("E735", "Star"), ("E781", "Lamp"),
        ("E790", "Palette"), ("E80F", "Home"), ("E77B", "Person"), ("E946", "Info"), ("E7BA", "Warning"), ("E930", "OK"),
        ("E9D2", "Chart"), ("E753", "Cloud"), ("E9E9", "Sliders"), ("E7C1", "Flag"), ("ED5A", "Clock face"), ("E895", "Sync"),
    ];

    static readonly (string Name, string A1, string A2, string A3)[] Presets =
    [
        ("Amber", "#FFC53D", "#FF6A3D", "#FFA066"), ("Aurora", "#4FE3D6", "#7B6CFF", "#D47CFF"), ("Neon", "#00E5FF", "#FF2E97", "#F9F871"),
        ("Ice", "#7DD3FC", "#A78BFA", "#E0F2FE"), ("Forest", "#A3E635", "#22C55E", "#FACC15"), ("Rose", "#FDA4AF", "#F472B6", "#C084FC"),
        ("Blood", "#FF6B6B", "#C81D25", "#FFB4A2"), ("Ocean", "#38BDF8", "#2563EB", "#22D3EE"),
    ];

    static List<string>? fontNames;

    void InitEditor()
    {
        EditImage.Source = editBmp;
        foreach (var (kind, label, glyph) in new[]
                 {
                     ("text", "Text", "\uE8D2"), ("value", "Value", "\uE9CA"), ("gauge", "Gauge", "\uEC4A"), ("ring", "Ring", "\uEA3A"),
                     ("bar", "Bar", "\uE9E9"), ("graph", "Graph", "\uE9D2"), ("cores", "Cores", "\uE950"), ("icon", "Icon", "\uE734"),
                     ("rect", "Rectangle", "\uE739"), ("glow", "Glow", "\uE706"), ("image", "Image", "\uEB9F"), ("clock", "Clock", "\uE917"),
                     ("nowplaying", "Music", "\uE8D6"), ("cover", "Cover", "\uE93C"), ("claude", "Claude", "\uE8BD"),
                 })
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Ico"), FontSize = 13, Width = 18, Foreground = EmberBrush, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = T(label), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var b = new Button { Content = sp, Style = (Style)FindResource("Tool") };
            var k = kind;
            b.Click += (_, _) => AddWidget(k);
            AddPanel.Children.Add(b);
        }
        Overlay.Children.Add(hoverRect);
        Overlay.Children.Add(selRect);
        Overlay.Children.Add(guideV);
        Overlay.Children.Add(guideH);
        foreach (var h in handles) Overlay.Children.Add(h);
        sizeLabel = new Border { Background = (Brush)FindResource("Ember"), CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 1, 4, 1), Child = sizeText, Visibility = Visibility.Collapsed };
        Overlay.Children.Add(sizeLabel);

        CanvasHost.MouseLeftButtonDown += CanvasDown;
        CanvasHost.MouseMove += CanvasMove;
        CanvasHost.MouseLeftButtonUp += CanvasUp;
        CanvasHost.MouseLeave += (_, _) => { hover = -1; CursorPos.Text = ""; };
        Layers.SelectionChanged += (_, _) => { if (!layersQuiet && Layers.SelectedItem is ListBoxItem li) Select((int)li.Tag); };
        LayerUp.Click += (_, _) => MoveLayer(1);
        LayerDown.Click += (_, _) => MoveLayer(-1);
        LayerDup.Click += (_, _) => Duplicate();
        LayerDel.Click += (_, _) => DeleteSel();
        commitTimer.Tick += (_, _) => { commitTimer.Stop(); Commit(); };
        liveTimer.Tick += (_, _) => { liveTimer.Stop(); if (live && draft != null) App.Engine.Theme = draft.Clone(); };
    }

    void DisposeEditor()
    {
        editR?.Dispose();
        editR = null;
    }

    // ---------- draft lifecycle ----------

    async void OpenEditor(Theme t)
    {
        if (draft != null && EditorDirty && draft.Id != t.Id &&
            !await Confirm(T("Unsaved changes"), L.F("{0} has unsaved changes. Discard them and open {1}?", T(draft.Name), T(t.Name)), T("Discard"), T("Cancel")))
        {
            NavEditor.IsChecked = true;
            return;
        }
        LoadDraft(t.Clone());
        if (NavEditor.IsChecked == true) Show("editor"); else NavEditor.IsChecked = true;
    }

    void NewTheme()
    {
        var t = new Theme
        {
            Name = T("My theme"), Font = "Bahnschrift", Bg = "#0D0C0B",
            Palette = new Dictionary<string, string>
            {
                ["card"] = "#1A1816", ["line"] = "#2E2A27", ["text"] = "#FFFFFF", ["sub"] = "#C5C5C5", ["dim"] = "#9A9A9A",
                ["track"] = "#2A2623", ["a1"] = "#FFB88A", ["a2"] = "#FF6A1F", ["a3"] = "#FF8A3D", ["warn"] = "#FFB020", ["crit"] = "#FF4D4D",
            },
            Widgets =
            {
                new Widget { Type = "glow", Name = T("Glow"), X = 240, Y = -160, W = 440, H = 340, Color = "a2/18" },
                new Widget { Type = "rect", Name = T("Card"), X = 10, Y = 10, W = 460, H = 300, Radius = 16, Colors = ["#1C1A18", "#141210"], Stroke = "line" },
                new Widget { Type = "text", Name = T("Clock"), X = 240, Y = 120, Align = "center", Text = "{time:HH:mm}", Size = 64, Weight = 600, Color = "text" },
                new Widget { Type = "text", Name = T("Date"), X = 240, Y = 200, Align = "center", Text = "{date:dddd, d MMMM}", Size = 14, Color = "sub" },
            },
        };
        OpenEditor(t);
    }

    void LoadDraft(Theme t)
    {
        commitTimer.Stop();
        draft = t;
        AppWatch.Editing = t;
        sel = -1;
        hover = -1;
        undo.Clear();
        undo.Add(t.ToJson());
        undoAt = 0;
        savedJson = t.Id == "" ? "" : undo[0];
        editRDirty = true;
        RebuildLayers();
        RebuildProps();
        if (live) App.Engine.Theme = draft.Clone();
    }

    void ShowEditorActions()
    {
        if (draft == null) LoadDraft(App.Engine.Theme.Clone());
        UpdateEditorSub();
        TopActions.Children.Add(Btn("", "\uE7A7", false, Undo, T("Undo (Ctrl+Z)")));
        TopActions.Children.Add(Gap(6));
        TopActions.Children.Add(Btn("", "\uE7A6", false, Redo, T("Redo (Ctrl+Y)")));
        TopActions.Children.Add(Gap(14));
        liveBox = new CheckBox { Style = (Style)FindResource("Flag"), Content = T("Live on the screen"), IsChecked = live, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0), ToolTip = T("Every change goes to the panel right away") };
        liveBox.Checked += (_, _) => { live = true; if (draft != null) App.Engine.Theme = draft.Clone(); };
        liveBox.Unchecked += (_, _) => StopLiveEdit();
        TopActions.Children.Add(liveBox);
        TopActions.Children.Add(Gap(14));
        TopActions.Children.Add(Btn(T("Export"), null, false, Export, T("Save the theme to a .json file")));
        TopActions.Children.Add(Gap());
        TopActions.Children.Add(Btn(T("Save"), null, true, Save, T("Save and show on the screen (Ctrl+S)")));
    }

    void UpdateEditorSub()
    {
        if (draft == null) return;
        EditorName.Text = T(draft.Name);
        EditorState.Text = draft.BuiltIn ? T("Built in. Saving makes your own copy.") : EditorDirty ? T("Unsaved changes") : "";
        UpdateLinkCost();
    }

    int costRun;

    /// Simulates the draft on a copy in the background; only the newest run gets to show its result.
    void UpdateLinkCost()
    {
        if (draft == null) return;
        var copy = draft.Clone();
        int run = ++costRun;
        System.Threading.Tasks.Task.Run(() => FrameDiff.LinkUse(copy)).ContinueWith(r =>
        {
            if (run != costRun || r.IsFaulted) return;
            double pct = r.Result / FrameDiff.Bandwidth * 100;
            LinkCost.Text = L.F("Link {0:0}%", pct);
            LinkCost.Foreground = pct >= 80 ? Br("#FFC857") : SubBrush;
        }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
    }

    void StopLiveEdit()
    {
        if (!live) return;
        live = false;
        if (liveBox != null) liveBox.IsChecked = false;
        App.Engine.Theme = App.AllThemes.FirstOrDefault(t => t.Id == App.Cfg.Theme) ?? App.Engine.Theme;
    }

    void Save()
    {
        if (draft == null) return;
        commitTimer.Stop();
        Commit();
        App.AllThemes = Themes.LoadAll();
        if (draft.BuiltIn || draft.Id == "")
        {
            var builtIn = App.AllThemes.FirstOrDefault(t => t.Id == draft.Id && t.BuiltIn);
            if (builtIn != null && builtIn.Name == draft.Name) draft.Name = L.F("{0} (mine)", T(draft.Name));
            draft.Id = Themes.NewId(draft.Name, App.AllThemes);
            draft.BuiltIn = false;
        }
        try
        {
            Themes.Save(draft);
            savedJson = draft.ToJson();
            App.AllThemes = Themes.LoadAll();
            App.SetTheme(App.AllThemes.FirstOrDefault(t => t.Id == draft.Id) ?? draft);
            UpdateThemeCard();
            UpdateEditorSub();
            Toast(L.F("Saved {0}. It is on the screen now.", draft.Name));
        }
        catch (Exception e)
        {
            Log.Error("save theme", e);
            Toast(T("Could not save: ") + e.Message, true);
        }
    }

    void Export()
    {
        if (draft == null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = T("Ember theme") + " (*.json)|*.json", FileName = Themes.NewId(draft.Name, []) + ".json" };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllText(dlg.FileName, draft.ToJson());
        Toast(T("Exported"));
    }

    void Commit()
    {
        if (draft == null) return;
        var j = draft.ToJson();
        if (undoAt >= 0 && undo[undoAt] == j) return;
        undo.RemoveRange(undoAt + 1, undo.Count - undoAt - 1);
        undo.Add(j);
        if (undo.Count > 300) undo.RemoveAt(0);
        undoAt = undo.Count - 1;
        UpdateEditorSub();
    }

    void Undo()
    {
        commitTimer.Stop();
        Commit();
        if (undoAt <= 0) return;
        undoAt--;
        Restore(undo[undoAt]);
    }

    void Redo()
    {
        if (undoAt >= undo.Count - 1) return;
        undoAt++;
        Restore(undo[undoAt]);
    }

    void Restore(string json)
    {
        var t = Theme.Parse(json);
        t.Id = draft!.Id;
        t.BuiltIn = draft.BuiltIn;
        draft = t;
        AppWatch.Editing = t;
        if (sel >= draft.Widgets.Count) sel = -1;
        editRDirty = true;
        RebuildLayers();
        RebuildProps();
        ScheduleLive();
        UpdateEditorSub();
    }

    void Changed(bool structural = false)
    {
        editRDirty = true;
        commitTimer.Stop();
        commitTimer.Start();
        ScheduleLive();
        if (structural) RebuildLayers();
        else if (sel >= 0 && Layers.Items.Count == draft!.Widgets.Count) ((ListBoxItem)Layers.Items[draft.Widgets.Count - 1 - sel]).Content = LayerRow(draft.Widgets[sel]);
    }

    void ScheduleLive()
    {
        if (!live) return;
        liveTimer.Stop();
        liveTimer.Start();
    }

    // ---------- canvas ----------

    void TickEditor()
    {
        if (draft == null) return;
        if (editRDirty || editR == null)
        {
            editR?.Dispose();
            editR = new Renderer(draft);
            editRDirty = false;
            editFirst = true;
        }
        using (var c = new SKCanvas(editFrame)) editR.Render(c, SnapForPreview(), editFirst ? 0 : 0.033, Clock.Now);
        editFirst = false;
        editBmp.WritePixels(new Int32Rect(0, 0, W, H), editFrame.GetPixels(), editFrame.ByteCount, editFrame.RowBytes);
        UpdateOverlay();
    }

    void UpdateOverlay()
    {
        if (draft == null || editR == null) return;
        Place(hoverRect, hover >= 0 && hover != sel && hover < draft.Widgets.Count ? editR.Bounds(hover) : (SKRect?)null, 1);
        SKRect? sb = sel >= 0 && sel < draft.Widgets.Count ? editR.Bounds(sel) : null;
        Place(selRect, sb, 1);
        bool resizable = sb != null && BoxTypes.Contains(draft.Widgets[sel].Type) && draft.Widgets[sel].Locked != true;
        for (int i = 0; i < 8; i++)
        {
            handles[i].Visibility = resizable ? Visibility.Visible : Visibility.Collapsed;
            if (!resizable) continue;
            var p = HandlePos(sb!.Value, i);
            Canvas.SetLeft(handles[i], p.X - 3);
            Canvas.SetTop(handles[i], p.Y - 3);
        }
        if (sb != null)
        {
            var w = draft.Widgets[sel];
            sizeText.Text = BoxTypes.Contains(w.Type) ? $"{w.W:0} × {w.H:0}" : $"{w.X:0}, {w.Y:0}";
            sizeLabel!.Visibility = Visibility.Visible;
            Canvas.SetLeft(sizeLabel, sb.Value.Left);
            Canvas.SetTop(sizeLabel, sb.Value.Top - 15 < 0 ? sb.Value.Bottom + 3 : sb.Value.Top - 15);
        }
        else sizeLabel!.Visibility = Visibility.Collapsed;
    }

    static void Place(Shapes.Rectangle r, SKRect? b, double pad)
    {
        if (b == null) { r.Visibility = Visibility.Collapsed; return; }
        r.Visibility = Visibility.Visible;
        Canvas.SetLeft(r, b.Value.Left - pad);
        Canvas.SetTop(r, b.Value.Top - pad);
        r.Width = Math.Max(1, b.Value.Width + 2 * pad);
        r.Height = Math.Max(1, b.Value.Height + 2 * pad);
    }

    static Point HandlePos(SKRect b, int i) => i switch
    {
        0 => new(b.Left, b.Top), 1 => new(b.MidX, b.Top), 2 => new(b.Right, b.Top), 3 => new(b.Right, b.MidY),
        4 => new(b.Right, b.Bottom), 5 => new(b.MidX, b.Bottom), 6 => new(b.Left, b.Bottom), _ => new(b.Left, b.MidY),
    };

    int HandleAt(Point p)
    {
        if (draft == null || editR == null || sel < 0 || sel >= draft.Widgets.Count) return -1;
        var w = draft.Widgets[sel];
        if (!BoxTypes.Contains(w.Type) || w.Locked == true) return -1;
        var b = editR.Bounds(sel);
        for (int i = 0; i < 8; i++)
        {
            var hp = HandlePos(b, i);
            if (Math.Abs(hp.X - p.X) <= 5 && Math.Abs(hp.Y - p.Y) <= 5) return i;
        }
        return -1;
    }

    int HitTest(Point p)
    {
        if (draft == null || editR == null) return -1;
        int background = -1;
        for (int i = draft.Widgets.Count - 1; i >= 0; i--)
        {
            var w = draft.Widgets[i];
            if (w.Locked == true) continue;
            var b = editR.Bounds(i);
            if (!b.Contains((float)p.X, (float)p.Y)) continue;
            // Glows and full-screen backgrounds only win when nothing else is under the cursor.
            if (w.Type == "glow" || b.Width >= W - 1 && b.Height >= H - 1) { if (background < 0) background = i; continue; }
            return i;
        }
        return background;
    }

    void CanvasDown(object sender, MouseButtonEventArgs e)
    {
        if (draft == null || editR == null) return;
        CanvasHost.Focus();
        var p = e.GetPosition(CanvasHost);
        int h = HandleAt(p);
        if (h < 0)
        {
            int i = HitTest(p);
            if (i != sel) Select(i);
            if (sel < 0) return;
            if (draft.Widgets[sel].Locked == true) return;
            dragMode = 1;
        }
        else { dragMode = 2; dragHandle = h; }
        var w = draft.Widgets[sel];
        dragStart = p;
        ox = w.X; oy = w.Y; ow = w.W; oh = w.H;
        ob = editR.Bounds(sel);
        dragMoved = false;
        CanvasHost.CaptureMouse();
        e.Handled = true;
    }

    void CanvasMove(object sender, MouseEventArgs e)
    {
        if (draft == null || editR == null) return;
        var p = e.GetPosition(CanvasHost);
        CursorPos.Text = $"{Math.Clamp((int)p.X, 0, W - 1)}, {Math.Clamp((int)p.Y, 0, H - 1)}";
        if (dragMode == 0)
        {
            hover = HitTest(p);
            int h = HandleAt(p);
            CanvasHost.Cursor = h < 0 ? (hover >= 0 ? Cursors.SizeAll : Cursors.Cross) : h is 0 or 4 ? Cursors.SizeNWSE : h is 2 or 6 ? Cursors.SizeNESW : h is 1 or 5 ? Cursors.SizeNS : Cursors.SizeWE;
            return;
        }
        double dx = p.X - dragStart.X, dy = p.Y - dragStart.Y;
        if (!dragMoved && Math.Abs(dx) < 1.5 && Math.Abs(dy) < 1.5) return;
        dragMoved = true;
        var w = draft.Widgets[sel];
        bool snap = !Keyboard.IsKeyDown(Key.LeftAlt) && !Keyboard.IsKeyDown(Key.RightAlt);
        bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
        guideV.Visibility = guideH.Visibility = Visibility.Collapsed;
        if (dragMode == 1)
        {
            if (shift) { if (Math.Abs(dx) > Math.Abs(dy)) dy = 0; else dx = 0; }
            if (snap)
            {
                (dx, var gx) = SnapMove(ob.Left, ob.Width, dx, false);
                (dy, var gy) = SnapMove(ob.Top, ob.Height, dy, true);
                ShowGuides(gx, gy);
            }
            w.X = (float)Math.Round(ox + dx);
            w.Y = (float)Math.Round(oy + dy);
        }
        else
        {
            double l = ob.Left, t = ob.Top, r = ob.Right, b = ob.Bottom;
            double? gx = null, gy = null;
            if (dragHandle is 0 or 6 or 7) (l, gx) = SnapEdge(l + dx, false, snap);
            if (dragHandle is 2 or 3 or 4) (r, gx) = SnapEdge(r + dx, false, snap);
            if (dragHandle is 0 or 1 or 2) (t, gy) = SnapEdge(t + dy, true, snap);
            if (dragHandle is 4 or 5 or 6) (b, gy) = SnapEdge(b + dy, true, snap);
            if (r - l < 4) { if (dragHandle is 0 or 6 or 7) l = r - 4; else r = l + 4; }
            if (b - t < 2) { if (dragHandle is 0 or 1 or 2) t = b - 2; else b = t + 2; }
            if (shift && dragHandle is 0 or 2 or 4 or 6)
            {
                double side = Math.Max(r - l, b - t);
                if (dragHandle is 0 or 6) l = r - side; else r = l + side;
                if (dragHandle is 0 or 2) t = b - side; else b = t + side;
            }
            ShowGuides(gx, gy);
            w.X = (float)Math.Round(l); w.Y = (float)Math.Round(t);
            w.W = (float)Math.Round(r - l); w.H = (float)Math.Round(b - t);
        }
        editRDirty = true;
        UpdatePosFields();
        ScheduleLive();
    }

    void CanvasUp(object sender, MouseButtonEventArgs e)
    {
        CanvasHost.ReleaseMouseCapture();
        if (dragMode != 0 && dragMoved) { commitTimer.Stop(); Commit(); }
        dragMode = 0;
        guideV.Visibility = guideH.Visibility = Visibility.Collapsed;
    }

    void ShowGuides(double? gx, double? gy)
    {
        if (gx is double x) { guideV.X1 = guideV.X2 = x; guideV.Visibility = Visibility.Visible; }
        if (gy is double y) { guideH.Y1 = guideH.Y2 = y; guideH.Visibility = Visibility.Visible; }
    }

    List<double> SnapTargets(bool vertical)
    {
        var list = vertical ? new List<double> { 0, H / 2.0, H } : new List<double> { 0, W / 2.0, W };
        for (int i = 0; i < draft!.Widgets.Count; i++)
        {
            if (i == sel || draft.Widgets[i].Type == "glow") continue;
            var b = editR!.Bounds(i);
            if (b.Width >= W - 1 && b.Height >= H - 1) continue;
            if (vertical) list.AddRange([b.Top, b.MidY, b.Bottom]); else list.AddRange([b.Left, b.MidX, b.Right]);
        }
        return list;
    }

    /// Moves a box so one of its edges or its centre lands on another element's edge/centre when within 4 px.
    (double Delta, double? Guide) SnapMove(double start, double size, double delta, bool vertical)
    {
        double best = 4.01;
        double? guide = null;
        foreach (var t in SnapTargets(vertical))
            foreach (var edge in new[] { start + delta, start + delta + size / 2, start + delta + size })
                if (Math.Abs(t - edge) < Math.Abs(best)) { best = t - edge; guide = t; }
        return guide != null ? (delta + best, guide) : (Math.Round(start + delta) - start, null);
    }

    (double Value, double? Guide) SnapEdge(double v, bool vertical, bool snap)
    {
        if (!snap) return (v, null);
        foreach (var t in SnapTargets(vertical))
            if (Math.Abs(t - v) <= 4) return (t, t);
        return (Math.Round(v), null);
    }

    void EditorKey(KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (ctrl && e.Key == Key.S) { Save(); e.Handled = true; return; }
        if (Keyboard.FocusedElement is TextBox) return;
        if (ctrl && e.Key == Key.Z) Undo();
        else if (ctrl && e.Key == Key.Y) Redo();
        else if (ctrl && e.Key == Key.D) Duplicate();
        else if (!CanvasHost.IsKeyboardFocused && Keyboard.FocusedElement is System.Windows.Controls.Slider or ComboBox) return;
        else if (e.Key == Key.Delete) DeleteSel();
        else if (e.Key == Key.Escape) Select(-1);
        else if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down && sel >= 0 && draft != null)
        {
            var w = draft.Widgets[sel];
            float step = shift ? 10 : 1;
            if (e.Key == Key.Left) w.X -= step;
            if (e.Key == Key.Right) w.X += step;
            if (e.Key == Key.Up) w.Y -= step;
            if (e.Key == Key.Down) w.Y += step;
            UpdatePosFields();
            Changed();
        }
        else return;
        e.Handled = true;
    }

    // ---------- selection and layers ----------

    void Select(int i)
    {
        sel = draft == null || i >= draft.Widgets.Count ? -1 : i;
        hover = -1;
        SyncLayerSel();
        RebuildProps();
    }

    void SyncLayerSel()
    {
        if (draft == null) return;
        layersQuiet = true;
        Layers.SelectedIndex = sel < 0 ? -1 : draft.Widgets.Count - 1 - sel;
        if (Layers.SelectedItem != null) Layers.ScrollIntoView(Layers.SelectedItem);
        layersQuiet = false;
    }

    void RebuildLayers()
    {
        if (draft == null) return;
        layersQuiet = true;
        Layers.Items.Clear();
        for (int i = draft.Widgets.Count - 1; i >= 0; i--)
            Layers.Items.Add(new ListBoxItem { Tag = i, Content = LayerRow(draft.Widgets[i]) });
        layersQuiet = false;
        SyncLayerSel();
    }

    Grid LayerRow(Widget w)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var info = TypeInfo.GetValueOrDefault(w.Type, ("?", ""));
        g.Children.Add(new TextBlock { Text = info.Item2, FontFamily = (FontFamily)FindResource("Ico"), FontSize = 11, Foreground = DimBrush, VerticalAlignment = VerticalAlignment.Center });
        var name = new TextBlock { Text = DisplayName(w), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 1);
        g.Children.Add(name);
        if (w.Locked == true)
        {
            var l = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Ico"), FontSize = 10, Foreground = DimBrush, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(l, 2);
            g.Children.Add(l);
        }
        return g;
    }

    static string DisplayName(Widget w)
    {
        if (!string.IsNullOrWhiteSpace(w.Name)) return w.Name!;
        var label = T(TypeInfo.GetValueOrDefault(w.Type, (w.Type, "")).Item1);
        return w.Text != null ? $"{label}: {w.Text}" : w.Sensor != null ? $"{label}: {w.Sensor}" : label;
    }

    void MoveLayer(int d)
    {
        if (draft == null || sel < 0) return;
        int j = sel + d;
        if (j < 0 || j >= draft.Widgets.Count) return;
        (draft.Widgets[sel], draft.Widgets[j]) = (draft.Widgets[j], draft.Widgets[sel]);
        sel = j;
        Changed(true);
        commitTimer.Stop();
        Commit();
    }

    void Duplicate()
    {
        if (draft == null || sel < 0) return;
        var copy = JsonClone(draft.Widgets[sel]);
        copy.X += 8; copy.Y += 8;
        copy.Locked = null;
        if (copy.Name != null) copy.Name = L.F("{0} copy", copy.Name);
        draft.Widgets.Insert(sel + 1, copy);
        sel++;
        Changed(true);
        RebuildProps();
        commitTimer.Stop();
        Commit();
    }

    void DeleteSel()
    {
        if (draft == null || sel < 0) return;
        draft.Widgets.RemoveAt(sel);
        sel = -1;
        Changed(true);
        RebuildProps();
        commitTimer.Stop();
        Commit();
    }

    static Widget JsonClone(Widget w) => System.Text.Json.JsonSerializer.Deserialize<Widget>(System.Text.Json.JsonSerializer.Serialize(w, Theme.Json), Theme.Json)!;

    /// Ready-made groups for the app values: a card with everything laid out, to move and restyle like any widgets.
    static List<Widget>? Block(string kind) => kind switch
    {
        "nowplaying" =>
        [
            new() { Type = "rect", Name = "Now playing card", X = 40, Y = 80, W = 400, H = 160, Radius = 10, Color = "card" },
            new() { Type = "rect", Name = "Cover backdrop", X = 56, Y = 96, W = 128, H = 128, Radius = 6, Color = "track" },
            new() { Type = "image", Name = "Cover", X = 56, Y = 96, W = 128, H = 128, Radius = 6, Src = "@art" },
            new() { Type = "text", Name = "State", X = 200, Y = 104, Text = "{spotify.state}", Size = 12, Weight = 600, Color = "a1" },
            new() { Type = "text", Name = "Title", X = 200, Y = 124, W = 224, Text = "{spotify.title}", Size = 20, Weight = 600, Color = "text", Mode = "ellipsis" },
            new() { Type = "text", Name = "Artist", X = 200, Y = 154, W = 224, Text = "{spotify.artist}", Size = 14, Color = "sub", Mode = "ellipsis" },
            new() { Type = "bar", Name = "Progress", X = 200, Y = 196, W = 224, H = 4, Radius = 2, Sensor = "spotify.progress", Color = "a1", Track = "track" },
            new() { Type = "text", Name = "Position", X = 200, Y = 208, Text = "{spotify.pos:mss}", Size = 11, Color = "dim" },
            new() { Type = "text", Name = "Length", X = 424, Y = 208, Align = "right", Text = "{spotify.dur:mss}", Size = 11, Color = "dim" },
        ],
        "claude" =>
        [
            new() { Type = "rect", Name = "Claude card", X = 40, Y = 80, W = 400, H = 160, Radius = 10, Color = "card" },
            new() { Type = "text", Name = "Claude Code", X = 56, Y = 94, Text = "Claude Code", Size = 13, Weight = 600, Color = "sub" },
            new() { Type = "text", Name = "Reset", X = 424, Y = 95, Align = "right", Text = "{claude.resetText}", Size = 11, Color = "dim" },
            new() { Type = "text", Name = "Session cost", X = 56, Y = 118, Text = "{claude.block.cost:usd}", Size = 36, Weight = 600, Color = "text" },
            new() { Type = "text", Name = "Session tokens", X = 424, Y = 124, Align = "right", Text = "{claude.block.tokens:tok} tokens", Size = 13, Color = "sub" },
            new() { Type = "text", Name = "Month", X = 424, Y = 144, Align = "right", Text = "{claude.month.cost:usd} this month", Size = 13, Color = "sub" },
            new() { Type = "bar", Name = "Limit bar", X = 56, Y = 186, W = 368, H = 6, Radius = 3, Sensor = "claude.block.pct", Color = "a1", Track = "track", Warn = 75, Crit = 90 },
            new() { Type = "text", Name = "Limit", X = 56, Y = 202, Text = "Session limit {claude.block.pct:0}%", Size = 11, Color = "dim" },
        ],
        "cover" => [new() { Type = "image", Name = "Cover", X = 176, Y = 96, W = 128, H = 128, Radius = 6, Src = "@art" }],
        _ => null,
    };

    void AddWidget(string kind)
    {
        if (draft == null) return;
        if (Block(kind) is { } block)
        {
            draft.Widgets.AddRange(block);
            sel = draft.Widgets.Count - block.Count;
            Changed(true);
            RebuildProps();
            commitTimer.Stop();
            Commit();
            CanvasHost.Focus();
            return;
        }
        Widget w = kind switch
        {
            "value" => new() { Type = "text", X = 240, Y = 140, Align = "center", Text = "{cpu.temp:0}°", Sensor = "cpu.temp", Size = 40, Weight = 600, Color = "text", Warn = 80, Crit = 90 },
            "clock" => new() { Type = "text", X = 240, Y = 130, Align = "center", Text = "{time:HH:mm}", Size = 48, Weight = 600, Color = "text" },
            "gauge" => new() { Type = "gauge", X = 180, Y = 100, W = 120, H = 120, Sensor = "cpu.load", Thickness = 10, Colors = ["a1", "a2"], Glow = 8, Mode = "dot" },
            "ring" => new() { Type = "gauge", X = 190, Y = 110, W = 100, H = 100, Sensor = "gpu.load", Thickness = 8, Start = -90, Sweep = 360, Colors = ["a1", "a2"] },
            "bar" => new() { Type = "bar", X = 160, Y = 156, W = 160, H = 8, Sensor = "ram.load", Colors = ["a1", "a2"] },
            "graph" => new() { Type = "graph", X = 140, Y = 120, W = 200, H = 70, Sensor = "cpu.load", Max = 100, Points = 80, Thickness = 2, Colors = ["a1", "a2"], Glow = 4, Mode = "sweep dot" },
            "cores" => new() { Type = "cores", X = 120, Y = 110, W = 240, H = 100, Sensor = "cpu.t", Colors = ["a1", "a2"], Gap = 4 },
            "icon" => new() { Type = "icon", X = 228, Y = 148, Size = 24, Glyph = "E950", Color = "a1" },
            "rect" => new() { Type = "rect", X = 140, Y = 100, W = 200, H = 120, Radius = 14, Color = "card", Stroke = "line" },
            "glow" => new() { Type = "glow", X = 140, Y = 60, W = 200, H = 200, Color = "a1/25" },
            "image" => PickImage() ?? new Widget { Type = "none" },
            _ => new() { Type = "text", X = 200, Y = 150, Text = T("Text"), Size = 18, Color = "text" },
        };
        if (w.Type == "none") return;
        draft.Widgets.Add(w);
        sel = draft.Widgets.Count - 1;
        Changed(true);
        RebuildProps();
        commitTimer.Stop();
        Commit();
        CanvasHost.Focus();
    }

    Widget? PickImage()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = T("Images") + "|*.png;*.jpg;*.jpeg;*.webp;*.bmp", Title = T("Choose an image") };
        if (dlg.ShowDialog(this) != true) return null;
        try
        {
            var dir = System.IO.Path.Combine(Themes.UserDir, "assets");
            Directory.CreateDirectory(dir);
            var name = System.IO.Path.GetFileName(dlg.FileName);
            var dest = System.IO.Path.Combine(dir, name);
            if (!string.Equals(System.IO.Path.GetFullPath(dlg.FileName), System.IO.Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase)) File.Copy(dlg.FileName, dest, true);
            using var img = SKBitmap.Decode(dest) ?? throw new InvalidDataException(T("Cannot read the image"));
            float scale = Math.Min(1, Math.Min(240f / img.Width, 160f / img.Height));
            return new Widget { Type = "image", Src = "assets/" + name, X = 120, Y = 80, W = (float)Math.Round(img.Width * scale), H = (float)Math.Round(img.Height * scale) };
        }
        catch (Exception e) { Toast(T("Could not add the image: ") + e.Message, true); return null; }
    }

    // ---------- property panel ----------

    void RebuildProps()
    {
        Props.Children.Clear();
        fx = fy = fw = fh = null;
        if (draft == null) return;
        if (sel < 0 || sel >= draft.Widgets.Count) { ThemeProps(); return; }
        var w = draft.Widgets[sel];
        var info = TypeInfo.GetValueOrDefault(w.Type, (w.Type, ""));
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        head.Children.Add(new TextBlock { Text = info.Item2, FontFamily = (FontFamily)FindResource("Ico"), FontSize = 16, Foreground = EmberBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        head.Children.Add(new TextBlock { Text = T(info.Item1), Style = (Style)FindResource("H2") });
        Props.Children.Add(head);
        TextRow(T("Layer name"), () => w.Name, v => w.Name = string.IsNullOrWhiteSpace(v) ? null : v);

        Props.Children.Add(Caption(T("Position")));
        bool box = BoxTypes.Contains(w.Type);
        var pos = new Grid();
        pos.ColumnDefinitions.Add(new ColumnDefinition());
        pos.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        pos.ColumnDefinitions.Add(new ColumnDefinition());
        pos.RowDefinitions.Add(new RowDefinition());
        pos.RowDefinitions.Add(new RowDefinition());
        fx = NumField(pos, 0, 0, "X", w.X, v => w.X = v);
        fy = NumField(pos, 0, 2, "Y", w.Y, v => w.Y = v);
        if (box || w.Type == "text")
        {
            fw = NumField(pos, 1, 0, w.Type == "text" ? T("Max width") : T("Width"), w.W, v => w.W = Math.Max(0, v));
            if (box) fh = NumField(pos, 1, 2, T("Height"), w.H, v => w.H = Math.Max(0, v));
        }
        Props.Children.Add(pos);
        Props.Children.Add(Switch(T("Lock"), T("The mouse ignores it on the canvas"), w.Locked == true, v => { w.Locked = v ? true : null; Changed(true); }));

        switch (w.Type)
        {
            case "text":
                Props.Children.Add(Caption(T("Content")));
                var tb = TextRow(null, () => w.Text, v => w.Text = v, mono: true);
                Props.Children.Add(new TextBlock { Text = T("Put live values in braces: {cpu.temp:0}, {net.down:rate}, {time:HH:mm}, {date:dddd}"), Style = (Style)FindResource("SubText"), FontSize = 11, Margin = new Thickness(0, 2, 0, 6) });
                var ins = Btn(T("Insert a value"), null, false, () => { });
                ins.Click += (_, _) => new SensorPicker(ins, false, s =>
                {
                    if (s == null) return;
                    string tok = "{" + s.Key + (DefaultFormat(s) is string f ? ":" + f : "") + "}";
                    int at = tb.CaretIndex;
                    tb.Text = tb.Text.Insert(Math.Clamp(at, 0, tb.Text.Length), tok);
                    tb.CaretIndex = at + tok.Length;
                    if (w.Sensor == null && s.Unit.Length > 0 && !s.Key.StartsWith("time")) w.Sensor = s.Key;
                });
                Props.Children.Add(ins);
                FontRows(w);
                ColorRows(w, "text", T("Color"));
                Slider(T("Glow"), 0, 24, w.Glow ?? 0, v => w.Glow = v > 0 ? (float)v : null);
                Slider(T("Opacity"), 0.05, 1, w.Opacity ?? 1, v => w.Opacity = v < 0.999 ? (float)v : null, "0.00");
                Thresholds(w, T("Sensor for the color thresholds"));
                Flags(w, ("upper", T("Uppercase")), ("blink", T("Blink")));
                break;
            case "icon":
                Props.Children.Add(Caption(T("Icon")));
                GlyphGrid(w);
                Slider(T("Size"), 8, 120, w.Size ?? 20, v => w.Size = (float)Math.Round(v));
                ColorRows(w, "a1", T("Color"));
                Slider(T("Glow"), 0, 24, w.Glow ?? 0, v => w.Glow = v > 0 ? (float)v : null);
                Slider(T("Opacity"), 0.05, 1, w.Opacity ?? 1, v => w.Opacity = v < 0.999 ? (float)v : null, "0.00");
                Thresholds(w, T("Sensor for the color thresholds"));
                break;
            case "rect":
                ColorRows(w, "card", T("Fill"), allowNone: true);
                Slider(T("Corner radius"), 0, 80, w.Radius ?? 0, v => w.Radius = v > 0 ? (float)Math.Round(v) : null);
                Props.Children.Add(Caption(T("Outline")));
                ColorButton(T("Outline color"), () => w.Stroke, v => w.Stroke = v, true);
                Slider(T("Outline width"), 0.5, 8, w.StrokeWidth ?? 1, v => w.StrokeWidth = Math.Abs(v - 1) > 0.01 ? (float)v : null, "0.0");
                Props.Children.Add(Caption(T("Shadow")));
                ColorButton(T("Shadow color"), () => w.Shadow, v => w.Shadow = v, true);
                Slider(T("Blur"), 0, 40, w.Glow ?? 0, v => w.Glow = v > 0 ? (float)Math.Round(v) : null);
                Slider(T("Opacity"), 0.05, 1, w.Opacity ?? 1, v => w.Opacity = v < 0.999 ? (float)v : null, "0.00");
                Flags(w, ("radial", T("Radial gradient")));
                break;
            case "glow":
                Props.Children.Add(Caption(T("Color")));
                ColorButton(T("Color"), () => w.Color, v => w.Color = v ?? "a1/30", false);
                Slider(T("Opacity"), 0.05, 1, w.Opacity ?? 1, v => w.Opacity = v < 0.999 ? (float)v : null, "0.00");
                break;
            case "image":
                Props.Children.Add(Caption(T("Image")));
                if (w.Src == "@art") Props.Children.Add(new TextBlock { Text = T("The cover of what Spotify is playing."), Style = (Style)FindResource("SubText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
                else
                {
                    TextRow(T("File, relative to the themes folder"), () => w.Src, v => w.Src = v);
                    Props.Children.Add(Btn(T("Use the album cover"), "\uE93C", false, () => { w.Src = "@art"; Changed(true); RebuildProps(); }));
                }
                Slider(T("Corner radius"), 0, 80, w.Radius ?? 0, v => w.Radius = v > 0 ? (float)Math.Round(v) : null);
                Slider(T("Opacity"), 0.05, 1, w.Opacity ?? 1, v => w.Opacity = v < 0.999 ? (float)v : null, "0.00");
                break;
            case "bar":
                DataRows(w, 100);
                ColorRows(w, "a1", T("Color"));
                ColorButton(T("Track"), () => w.Track, v => w.Track = v, true, "track");
                Slider(T("Corner radius"), 0, 40, w.Radius ?? Math.Min(w.W, w.H) / 2, v => w.Radius = (float)Math.Round(v, 1));
                Slider(T("Segments (0 = solid)"), 0, 60, w.Segments ?? 0, v => w.Segments = v >= 1 ? (int)v : null);
                Slider(T("Segment gap"), 0, 10, w.Gap ?? 2, v => w.Gap = (float)Math.Round(v, 1), "0.0");
                Slider(T("Glow"), 0, 24, w.Glow ?? 0, v => w.Glow = v > 0 ? (float)v : null);
                Slider(T("Smoothing (s)"), 0, 1.5, w.Smooth ?? 0.35, v => w.Smooth = (float)Math.Round(v, 2), "0.00");
                Thresholds(w, null);
                Flags(w, ("vertical", T("Vertical")));
                break;
            case "gauge":
                DataRows(w, 100);
                ColorRows(w, "a1", T("Color"));
                ColorButton(T("Track"), () => w.Track, v => w.Track = v, true, "track");
                Props.Children.Add(Caption(T("Shape")));
                Slider(T("Thickness"), 1, 40, w.Thickness ?? 10, v => w.Thickness = (float)Math.Round(v));
                Slider(T("Start angle"), -180, 360, w.Start ?? 135, v => w.Start = (float)Math.Round(v));
                Slider(T("Sweep"), 10, 360, w.Sweep ?? 270, v => w.Sweep = (float)Math.Round(v));
                Slider(T("Segments (0 = solid)"), 0, 60, w.Segments ?? 0, v => w.Segments = v >= 1 ? (int)v : null);
                Slider(T("Segment gap (°)"), 0, 12, w.Gap ?? 3, v => w.Gap = (float)Math.Round(v, 1), "0.0");
                Slider(T("Glow"), 0, 24, w.Glow ?? 0, v => w.Glow = v > 0 ? (float)v : null);
                Slider(T("Tick count"), 2, 60, w.Points ?? 10, v => w.Points = (int)v);
                Slider(T("Smoothing (s)"), 0, 1.5, w.Smooth ?? 0.35, v => w.Smooth = (float)Math.Round(v, 2), "0.00");
                Thresholds(w, null);
                Flags(w, ("dot", T("Dot at the tip")), ("flat", T("Flat ends")), ("ticks", T("Ticks")), ("alternate", T("Clock: fill, then empty")));
                break;
            case "graph":
                DataRows(w, 0, T("Max (0 = auto)"));
                ColorRows(w, "a1", T("Color"));
                ColorButton(T("Grid lines"), () => w.Track, v => w.Track = v, true, "track");
                Slider(T("Samples"), 10, Snapshot.HistoryLength, w.Points ?? 60, v => w.Points = (int)v);
                Slider(T("Line width"), 0.5, 6, w.Thickness ?? 2, v => w.Thickness = (float)Math.Round(v, 1), "0.0");
                Slider(T("Glow"), 0, 16, w.Glow ?? 0, v => w.Glow = v > 0 ? (float)v : null);
                Flags(w, ("sweep", T("Sweep like a scope")), ("dot", T("Glowing dot")), ("grid", T("Grid")), ("sharp", T("Sharp corners")), ("nofill", T("No fill")));
                break;
            case "cores":
                Props.Children.Add(Caption(T("Data")));
                TextRow(T("Sensor prefix (cpu.t = threads)"), () => w.Sensor, v => w.Sensor = v);
                ColorRows(w, "a1", T("Color (bottom up)"));
                ColorButton(T("Track"), () => w.Track, v => w.Track = v, true, "track");
                Slider(T("Gap"), 0, 20, w.Gap ?? 3, v => w.Gap = (float)Math.Round(v));
                Slider(T("Corner radius"), 0, 20, w.Radius ?? 3, v => w.Radius = (float)Math.Round(v));
                Slider(T("Smoothing (s)"), 0, 1.5, w.Smooth ?? 0.35, v => w.Smooth = (float)Math.Round(v, 2), "0.00");
                Flags(w, ("horizontal", T("Horizontal")));
                break;
        }
        var del = Btn(T("Delete layer"), null, false, DeleteSel);
        del.Margin = new Thickness(0, 18, 0, 6);
        Props.Children.Add(del);
    }

    static string? DefaultFormat(SensorInfo s) => s.Key switch
    {
        "time" => "HH:mm", "date" => "dddd, d MMMM",
        "claude.block.left" => "hmm", "spotify.pos" or "spotify.dur" => "mss",
        _ when s.Unit == "B/s" => "rate",
        _ when s.Unit == "$" => "usd",
        _ when s.Unit == "tok" => "tok",
        _ when s.Unit == "×" => "0.0",
        _ when s.Unit is "GB" or "V" or "A" => "0.0",
        _ when s.Unit.Length == 0 => null,
        _ => "0",
    };

    TextBox NumField(Grid g, int row, int col, string label, float value, Action<float> set)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        sp.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = SubBrush, Margin = new Thickness(2, 0, 0, 4) });
        var tb = new TextBox { Text = value.ToString("0.#", CultureInfo.InvariantCulture) };
        tb.TextChanged += (_, _) =>
        {
            if (fieldsQuiet) return;
            if (float.TryParse(tb.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) { set(v); Changed(); }
        };
        tb.PreviewMouseWheel += (_, e) =>
        {
            if (!float.TryParse(tb.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return;
            tb.Text = (v + Math.Sign(e.Delta) * (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1)).ToString("0.#", CultureInfo.InvariantCulture);
            e.Handled = true;
        };
        sp.Children.Add(tb);
        Grid.SetRow(sp, row);
        Grid.SetColumn(sp, col);
        g.Children.Add(sp);
        return tb;
    }

    void UpdatePosFields()
    {
        if (draft == null || sel < 0) return;
        var w = draft.Widgets[sel];
        fieldsQuiet = true;
        if (fx != null) fx.Text = w.X.ToString("0.#", CultureInfo.InvariantCulture);
        if (fy != null) fy.Text = w.Y.ToString("0.#", CultureInfo.InvariantCulture);
        if (fw != null) fw.Text = w.W.ToString("0.#", CultureInfo.InvariantCulture);
        if (fh != null) fh.Text = w.H.ToString("0.#", CultureInfo.InvariantCulture);
        fieldsQuiet = false;
    }

    TextBox TextRow(string? label, Func<string?> get, Action<string?> set, bool mono = false)
    {
        if (label != null) Props.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = SubBrush, Margin = new Thickness(2, 6, 0, 4) });
        var tb = new TextBox { Text = get() ?? "", Margin = new Thickness(0, 0, 0, 6) };
        if (mono) tb.FontFamily = (FontFamily)FindResource("Mono");
        tb.TextChanged += (_, _) => { set(tb.Text); Changed(); UpdateEditorSub(); };
        Props.Children.Add(tb);
        return tb;
    }

    void Slider(string label, double min, double max, double value, Action<double> set, string fmt = "0")
    {
        var g = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        g.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = SubBrush });
        var val = new TextBlock { Text = value.ToString(fmt, CultureInfo.InvariantCulture), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };
        g.Children.Add(val);
        Props.Children.Add(g);
        var s = new System.Windows.Controls.Slider { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), Margin = new Thickness(0, 2, 0, 2) };
        s.ValueChanged += (_, e) => { val.Text = e.NewValue.ToString(fmt, CultureInfo.InvariantCulture); set(e.NewValue); Changed(); };
        Props.Children.Add(s);
    }

    void FontRows(Widget w)
    {
        Props.Children.Add(Caption(T("Font")));
        fontNames ??= Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(n => n).ToList();
        string cur = w.Font ?? draft!.Font;
        var items = new List<(string, string)> { ("", L.F("Theme font ({0})", draft!.Font)) };
        items.AddRange(fontNames.Select(n => (n, n)));
        var cb = Combo(items, w.Font ?? "", v => { w.Font = v.Length == 0 ? null : v; Changed(); });
        cb.Margin = new Thickness(0, 0, 0, 6);
        Props.Children.Add(cb);
        Slider(T("Size"), 6, 160, w.Size ?? 16, v => w.Size = (float)Math.Round(v, 1), "0.#");
        Props.Children.Add(new TextBlock { Text = T("Weight"), FontSize = 12, Foreground = SubBrush, Margin = new Thickness(0, 8, 0, 6) });
        Props.Children.Add(Chips(new[] { (300, T("Light")), (400, T("Regular")), (600, T("Semibold")), (700, T("Bold")) }, w.Weight ?? 400, v => { w.Weight = v == 400 ? null : v; Changed(); }));
        Props.Children.Add(new TextBlock { Text = T("Width"), FontSize = 12, Foreground = SubBrush, Margin = new Thickness(0, 4, 0, 6) });
        Props.Children.Add(Chips(new[] { (3, T("Condensed")), (4, T("Semi-condensed")), (5, T("Normal")) }, w.Stretch ?? 5, v => { w.Stretch = v == 5 ? null : v; Changed(); }));
        Props.Children.Add(new TextBlock { Text = T("Alignment to X"), FontSize = 12, Foreground = SubBrush, Margin = new Thickness(0, 4, 0, 6) });
        Props.Children.Add(Chips(new[] { ("left", T("Left")), ("center", T("Center")), ("right", T("Right")) }, w.Align ?? "left", v => { w.Align = v == "left" ? null : v; Changed(); }));
        Slider(T("Letter spacing"), -2, 10, w.Spacing ?? 0, v => w.Spacing = Math.Abs(v) > 0.01 ? (float)Math.Round(v, 1) : null, "0.0");
    }

    void ColorRows(Widget w, string fallback, string title, bool allowNone = false)
    {
        Props.Children.Add(Caption(title));
        bool gradient = w.Colors is { Length: > 1 };
        var toggle = new CheckBox { Style = (Style)FindResource("Flag"), Content = T("Gradient"), IsChecked = gradient, HorizontalAlignment = HorizontalAlignment.Left };
        toggle.Checked += (_, _) => { w.Colors = [w.Color ?? fallback, "a2"]; w.Color = null; Changed(); RebuildProps(); };
        toggle.Unchecked += (_, _) => { w.Color = w.Colors?.FirstOrDefault() ?? fallback; w.Colors = null; Changed(); RebuildProps(); };
        Props.Children.Add(toggle);
        if (!gradient)
        {
            ColorButton(T("Color"), () => w.Color, v => w.Color = v, allowNone, fallback);
            return;
        }
        for (int i = 0; i < w.Colors!.Length; i++)
        {
            int idx = i;
            ColorButton(L.F("Color {0}", i + 1), () => w.Colors[idx], v => { if (v != null) w.Colors[idx] = v; }, false);
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        if (w.Colors.Length < 4) row.Children.Add(Btn(T("Add a color"), null, false, () => { w.Colors = [.. w.Colors, w.Colors[^1]]; Changed(); RebuildProps(); }));
        if (w.Colors.Length > 2) { row.Children.Add(Gap(6)); row.Children.Add(Btn("", "\uE738", false, () => { w.Colors = w.Colors[..^1]; Changed(); RebuildProps(); }, T("Remove the last one"))); }
        Props.Children.Add(row);
        if (w.Type is "rect" or "text" or "icon")
            Slider(T("Gradient angle (°)"), 0, 360, w.Angle ?? 90, v => w.Angle = (float)Math.Round(v));
    }

    void ColorButton(string label, Func<string?> get, Action<string?> set, bool allowNone, string? fallback = null)
    {
        var swatch = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(7), Background = ColorPicker.Checker(), BorderBrush = LineBrush, BorderThickness = new Thickness(1) };
        var fill = new Border { CornerRadius = new CornerRadius(6) };
        swatch.Child = fill;
        var text = new TextBlock { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        void Refresh()
        {
            var spec = get();
            var c = Renderer.Resolve(draft!, spec ?? fallback, SKColors.Transparent);
            fill.Background = spec == null && fallback == null ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue));
            text.Text = spec ?? (fallback != null ? $"({fallback})" : T("none"));
            text.Foreground = spec == null ? DimBrush : TextBrush;
        }
        Refresh();
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = SubBrush, VerticalAlignment = VerticalAlignment.Center });
        var right = new StackPanel { Orientation = Orientation.Horizontal, Children = { swatch, text } };
        Grid.SetColumn(right, 1);
        g.Children.Add(right);
        var b = new Border { Child = g, Padding = new Thickness(8, 5, 8, 5), CornerRadius = new CornerRadius(9), Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(-8, 0, 0, 2) };
        b.MouseEnter += (_, _) => b.Background = RaisedBrush;
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, _) => new ColorPicker(b, draft!, get(), allowNone, v => { set(v); Refresh(); Changed(); });
        Props.Children.Add(b);
    }

    void DataRows(Widget w, double defMax, string? maxLabel = null)
    {
        Props.Children.Add(Caption(T("Data")));
        SensorButton(w);
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        NullableField(g, 0, T("Min"), w.Min ?? 0, v => w.Min = v == 0 ? null : v);
        NullableField(g, 2, maxLabel ?? T("Max"), w.Max ?? defMax, v => w.Max = v == defMax && defMax != 0 ? null : v);
        Props.Children.Add(g);
    }

    void Thresholds(Widget w, string? sensorLabel)
    {
        Props.Children.Add(Caption(T("Color thresholds")));
        if (sensorLabel != null) SensorButton(w, sensorLabel, true);
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        NullableField(g, 0, T("Warning from"), w.Warn, v => w.Warn = v, true);
        NullableField(g, 2, T("Alarm from"), w.Crit, v => w.Crit = v, true);
        Props.Children.Add(g);
        Props.Children.Add(new TextBlock { Text = T("Uses the warn and crit colors from the palette. Leave empty for no threshold."), Style = (Style)FindResource("SubText"), FontSize = 11 });
    }

    void NullableField(Grid g, int col, string label, double? value, Action<double?> set, bool nullable = false)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        sp.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = SubBrush, Margin = new Thickness(2, 6, 0, 4) });
        var tb = new TextBox { Text = value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "" };
        tb.TextChanged += (_, _) =>
        {
            var t = tb.Text.Trim().Replace(',', '.');
            if (t.Length == 0 && nullable) { set(null); Changed(); }
            else if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) { set(v); Changed(); }
        };
        sp.Children.Add(tb);
        Grid.SetColumn(sp, col);
        g.Children.Add(sp);
    }

    void SensorButton(Widget w, string? label = null, bool allowNone = false)
    {
        var all = Sensors.Catalog.Concat(App.Sensors.Raw);
        string Describe() => w.Sensor == null ? T("choose…") : all.FirstOrDefault(s => s.Key == w.Sensor) is { } i ? $"{T(i.Group)} {T(i.Label).ToLower(L.Culture)}" : w.Sensor;
        var main = new TextBlock { Text = Describe(), TextTrimming = TextTrimming.CharacterEllipsis };
        var key = new TextBlock { Text = w.Sensor ?? "", FontSize = 11.5, Foreground = DimBrush };
        var sp = new StackPanel { Children = { main, key } };
        var b = new Border
        {
            Child = new DockPanel { Children = { new TextBlock { Text = "\uE70D", FontFamily = (FontFamily)FindResource("Ico"), FontSize = 9, Foreground = SubBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) }, sp } },
            Background = (Brush)FindResource("Field"), BorderBrush = LineBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 6, 10, 6), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 6),
        };
        DockPanel.SetDock((UIElement)((DockPanel)b.Child).Children[0], Dock.Right);
        b.MouseLeftButtonUp += (_, _) => new SensorPicker(b, allowNone, s => { w.Sensor = s?.Key; main.Text = Describe(); key.Text = w.Sensor ?? ""; Changed(); });
        Props.Children.Add(new TextBlock { Text = label ?? T("Sensor"), FontSize = 12, Foreground = SubBrush, Margin = new Thickness(0, 0, 0, 4) });
        Props.Children.Add(b);
    }

    void Flags(Widget w, params (string Flag, string Label)[] flags)
    {
        Props.Children.Add(Caption(T("Options")));
        var wp = new WrapPanel();
        foreach (var (flag, label) in flags)
        {
            var cb = new CheckBox { Style = (Style)FindResource("Flag"), Content = label, IsChecked = w.Has(flag) };
            var f = flag;
            cb.Checked += (_, _) => { SetFlag(w, f, true); Changed(); };
            cb.Unchecked += (_, _) => { SetFlag(w, f, false); Changed(); };
            wp.Children.Add(cb);
        }
        Props.Children.Add(wp);
    }

    static void SetFlag(Widget w, string flag, bool on)
    {
        var set = (w.Mode ?? "").Split(' ', ',', StringSplitOptions.RemoveEmptyEntries).ToList();
        set.Remove(flag);
        if (on) set.Add(flag);
        w.Mode = set.Count == 0 ? null : string.Join(' ', set);
    }

    void GlyphGrid(Widget w)
    {
        var wp = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var (glyph, name) in Glyphs)
        {
            var g = glyph;
            var b = new Border
            {
                Width = 34, Height = 34, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 4, 4), Cursor = Cursors.Hand, ToolTip = T(name),
                Background = w.Glyph == glyph ? (Brush)FindResource("EmberSoft") : (Brush)FindResource("Field"), BorderBrush = w.Glyph == glyph ? EmberBrush : LineBrush, BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = char.ConvertFromUtf32(Convert.ToInt32(glyph, 16)), FontFamily = (FontFamily)FindResource("Ico"), FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = TextBrush },
            };
            b.MouseLeftButtonUp += (_, _) => { w.Glyph = g; Changed(); RebuildProps(); };
            wp.Children.Add(b);
        }
        Props.Children.Add(wp);
        TextRow(T("Glyph code (Segoe Fluent Icons, hex)"), () => w.Glyph, v => w.Glyph = v, mono: true);
    }

    void ThemeProps()
    {
        var t = draft!;
        Props.Children.Add(new TextBlock { Text = T("Theme"), Style = (Style)FindResource("H2"), Margin = new Thickness(0, 0, 0, 4) });
        Props.Children.Add(new TextBlock { Text = T("Click something on the canvas to edit it."), Style = (Style)FindResource("SubText"), Margin = new Thickness(0, 0, 0, 10) });
        TextRow(T("Name"), () => t.Name, v => t.Name = string.IsNullOrWhiteSpace(v) ? T("Untitled") : v);
        TextRow(T("Description"), () => t.Description, v => t.Description = string.IsNullOrWhiteSpace(v) ? null : v);
        Props.Children.Add(new TextBlock { Text = T("Theme font"), FontSize = 11, Foreground = SubBrush, Margin = new Thickness(2, 6, 0, 4) });
        fontNames ??= Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(n => n).ToList();
        var cb = Combo(fontNames.Select(n => (n, n)), t.Font, v => { t.Font = v; Changed(); });
        cb.Margin = new Thickness(0, 0, 0, 8);
        Props.Children.Add(cb);
        ColorButton(T("Background"), () => t.Bg, v => t.Bg = v ?? "#000000", false);

        Props.Children.Add(Caption(T("Accent presets")));
        var wp = new WrapPanel();
        foreach (var (name, a1, a2, a3) in Presets)
        {
            var b = new Border
            {
                Width = 66, Height = 30, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand, ToolTip = T(name),
                Background = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(a1), (Color)ColorConverter.ConvertFromString(a2), 0),
                Child = new TextBlock { Text = T(name), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Br("#14100A"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            var (x1, x2, x3) = (a1, a2, a3);
            b.MouseLeftButtonUp += (_, _) => { t.Palette["a1"] = x1; t.Palette["a2"] = x2; t.Palette["a3"] = x3; Changed(); RebuildProps(); };
            wp.Children.Add(b);
        }
        Props.Children.Add(wp);

        Props.Children.Add(Caption(T("Palette")));
        Props.Children.Add(new TextBlock { Text = T("Layers refer to these colors by name, like a1, or a1/30 for 30% opacity. Changing one here changes the whole theme."), Style = (Style)FindResource("SubText"), FontSize = 11, Margin = new Thickness(0, 0, 0, 8) });
        foreach (var name in t.Palette.Keys.ToList())
        {
            var n = name;
            ColorButton(n, () => t.Palette.GetValueOrDefault(n), v => { if (v != null && v != n) t.Palette[n] = v; }, false);
        }
        var add = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var nameBox = new TextBox { FontFamily = (FontFamily)FindResource("Mono"), ToolTip = T("Name of the new color") };
        var addBtn = Btn(T("Add"), null, false, () =>
        {
            var n = nameBox.Text.Trim();
            if (n.Length == 0 || n.StartsWith('#') || n.Contains('/') || t.Palette.ContainsKey(n)) { Toast(T("Pick a new name without # or /"), true); return; }
            t.Palette[n] = "#FFFFFF";
            Changed();
            RebuildProps();
        });
        DockPanel.SetDock(addBtn, Dock.Right);
        addBtn.Margin = new Thickness(8, 0, 0, 0);
        add.Children.Add(addBtn);
        add.Children.Add(nameBox);
        Props.Children.Add(add);
    }
}
