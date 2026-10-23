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

    readonly Shapes.Rectangle selRect = new() { Stroke = Br("#FF9A3D"), StrokeThickness = 1, Visibility = Visibility.Collapsed };
    readonly Shapes.Rectangle hoverRect = new() { Stroke = Br("#80FFFFFF"), StrokeThickness = 0.8, StrokeDashArray = new DoubleCollection { 3, 3 }, Visibility = Visibility.Collapsed };
    readonly Shapes.Rectangle[] handles = Enumerable.Range(0, 8).Select(_ => new Shapes.Rectangle { Width = 6, Height = 6, Fill = Br("#FFFFFF"), Stroke = Br("#FF8A3D"), StrokeThickness = 1, Visibility = Visibility.Collapsed }).ToArray();
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
        ["text"] = ("Tekst", ""), ["gauge"] = ("Wskaźnik", ""), ["bar"] = ("Pasek", ""), ["graph"] = ("Wykres", ""),
        ["cores"] = ("Rdzenie", ""), ["icon"] = ("Ikona", ""), ["rect"] = ("Prostokąt", ""), ["glow"] = ("Poświata", ""), ["image"] = ("Obraz", ""),
    };

    static readonly (string Glyph, string Name)[] Glyphs =
    [
        ("E950", "Procesor"), ("E964", "Karta / pamięć"), ("E9CA", "Temperatura"), ("E945", "Moc"), ("E9D9", "Aktywność"), ("E95E", "Puls"),
        ("E74B", "Pobieranie"), ("E74A", "Wysyłanie"), ("E8CB", "Góra i dół"), ("E968", "Sieć"), ("E701", "Wi-Fi"), ("E704", "Antena"),
        ("EDA2", "Dysk"), ("E7F4", "Monitor"), ("E7F8", "Laptop"), ("E917", "Zegar"), ("E916", "Stoper"), ("E787", "Kalendarz"),
        ("E706", "Słońce"), ("E708", "Księżyc"), ("E7E8", "Zasilanie"), ("E713", "Ustawienia"), ("E9F5", "Trybiki"), ("E8D6", "Muzyka"),
        ("E767", "Głośnik"), ("E7F6", "Słuchawki"), ("E7FC", "Pad"), ("E734", "Gwiazdka"), ("E735", "Gwiazda"), ("E781", "Lampa"),
        ("E790", "Paleta"), ("E80F", "Dom"), ("E77B", "Osoba"), ("E946", "Info"), ("E7BA", "Uwaga"), ("E930", "OK"),
        ("E9D2", "Wykres"), ("E753", "Chmura"), ("E9E9", "Suwaki"), ("E7C1", "Flaga"), ("ED5A", "Tarcza zegara"), ("E895", "Synchronizacja"),
    ];

    static readonly (string Name, string A1, string A2, string A3)[] Presets =
    [
        ("Bursztyn", "#FFC53D", "#FF6A3D", "#FF9A3D"), ("Zorza", "#4FE3D6", "#7B6CFF", "#D47CFF"), ("Neon", "#00E5FF", "#FF2E97", "#F9F871"),
        ("Lód", "#7DD3FC", "#A78BFA", "#E0F2FE"), ("Las", "#A3E635", "#22C55E", "#FACC15"), ("Róż", "#FDA4AF", "#F472B6", "#C084FC"),
        ("Krew", "#FF6B6B", "#C81D25", "#FFB4A2"), ("Ocean", "#38BDF8", "#2563EB", "#22D3EE"),
    ];

    static List<string>? fontNames;

    void InitEditor()
    {
        EditImage.Source = editBmp;
        foreach (var (kind, label, glyph) in new[]
                 {
                     ("text", "Tekst", ""), ("value", "Wartość", ""), ("gauge", "Wskaźnik", ""), ("ring", "Pierścień", ""),
                     ("bar", "Pasek", ""), ("graph", "Wykres", ""), ("cores", "Rdzenie", ""), ("icon", "Ikona", ""),
                     ("rect", "Prostokąt", ""), ("glow", "Poświata", ""), ("image", "Obraz", ""), ("clock", "Zegar", ""),
                 })
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Ico"), FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, Foreground = AmberBrush });
            sp.Children.Add(new TextBlock { Text = label, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
            var b = new Button { Content = sp, Style = (Style)FindResource("Ghost"), Width = 64, Height = 56, Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(2), ToolTip = $"Dodaj: {label}" };
            var k = kind;
            b.Click += (_, _) => AddWidget(k);
            AddPanel.Children.Add(b);
        }
        Overlay.Children.Add(hoverRect);
        Overlay.Children.Add(selRect);
        Overlay.Children.Add(guideV);
        Overlay.Children.Add(guideH);
        foreach (var h in handles) Overlay.Children.Add(h);
        sizeLabel = new Border { Background = (Brush)FindResource("Accent"), CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 1, 4, 1), Child = sizeText, Visibility = Visibility.Collapsed };
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
            !await Confirm("Niezapisane zmiany", $"„{draft.Name}” ma niezapisane zmiany. Porzucić je i otworzyć „{t.Name}”?", "Porzuć", "Anuluj"))
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
            Name = "Mój motyw", Description = "Mój własny układ.", Font = "Bahnschrift", Bg = "#090C11",
            Palette = new Dictionary<string, string>
            {
                ["card"] = "#121821", ["line"] = "#202836", ["text"] = "#ECEFF4", ["sub"] = "#8A94A6", ["dim"] = "#5E697C",
                ["track"] = "#1B222E", ["a1"] = "#FFC53D", ["a2"] = "#FF6A3D", ["a3"] = "#FF9A3D", ["warn"] = "#FFB020", ["crit"] = "#FF4D4D",
            },
            Widgets =
            {
                new Widget { Type = "glow", Name = "Poświata", X = 240, Y = -160, W = 440, H = 340, Color = "a2/18" },
                new Widget { Type = "rect", Name = "Karta", X = 10, Y = 10, W = 460, H = 300, Radius = 16, Colors = ["#151B25", "#0F141C"], Stroke = "line" },
                new Widget { Type = "text", Name = "Zegar", X = 240, Y = 120, Align = "center", Text = "{time:HH:mm}", Size = 64, Weight = 600, Color = "text" },
                new Widget { Type = "text", Name = "Data", X = 240, Y = 200, Align = "center", Text = "{date:dddd, d MMMM}", Size = 14, Color = "sub" },
            },
        };
        OpenEditor(t);
    }

    void LoadDraft(Theme t)
    {
        commitTimer.Stop();
        draft = t;
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
        TopActions.Children.Add(Btn("", "", false, Undo, "Cofnij (Ctrl+Z)"));
        TopActions.Children.Add(Gap(6));
        TopActions.Children.Add(Btn("", "", false, Redo, "Ponów (Ctrl+Y)"));
        TopActions.Children.Add(Gap(14));
        liveBox = new CheckBox { Style = (Style)FindResource("Flag"), Content = "Na żywo na ekranie", IsChecked = live, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0), Padding = new Thickness(0), ToolTip = "Każda zmiana od razu trafia na ekranik" };
        liveBox.Checked += (_, _) => { live = true; if (draft != null) App.Engine.Theme = draft.Clone(); };
        liveBox.Unchecked += (_, _) => StopLiveEdit();
        TopActions.Children.Add(liveBox);
        TopActions.Children.Add(Gap(14));
        TopActions.Children.Add(Btn("Eksportuj", "", false, Export, "Zapisz motyw do pliku .json"));
        TopActions.Children.Add(Gap());
        TopActions.Children.Add(Btn("Zapisz", "", true, Save, "Zapisz i ustaw na ekranie (Ctrl+S)"));
    }

    void UpdateEditorSub()
    {
        if (draft == null || page != "editor") return;
        PageSub.Text = draft.BuiltIn ? $"{draft.Name} · motyw wbudowany, zapis utworzy Twoją wersję" : draft.Name + (EditorDirty ? " · niezapisane zmiany" : "");
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
            if (builtIn != null && builtIn.Name == draft.Name) draft.Name += " (moja wersja)";
            draft.Id = Themes.NewId(draft.Name, App.AllThemes);
            draft.BuiltIn = false;
        }
        try
        {
            Themes.Save(draft);
            savedJson = draft.ToJson();
            App.AllThemes = Themes.LoadAll();
            App.SetTheme(App.AllThemes.First(t => t.Id == draft.Id));
            UpdateThemeCard();
            UpdateEditorSub();
            Toast($"Zapisano „{draft.Name}” i ustawiono na ekranie");
        }
        catch (Exception e)
        {
            Log.Error("save theme", e);
            Toast("Nie udało się zapisać: " + e.Message, true);
        }
    }

    void Export()
    {
        if (draft == null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "Motyw Ember (*.json)|*.json", FileName = Themes.NewId(draft.Name, []) + ".json" };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllText(dlg.FileName, draft.ToJson());
        Toast("Wyeksportowano");
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
        var label = TypeInfo.GetValueOrDefault(w.Type, (w.Type, "")).Item1;
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
        if (copy.Name != null) copy.Name += " (kopia)";
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

    void AddWidget(string kind)
    {
        if (draft == null) return;
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
            _ => new() { Type = "text", X = 200, Y = 150, Text = "Tekst", Size = 18, Color = "text" },
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
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Obrazy|*.png;*.jpg;*.jpeg;*.webp;*.bmp", Title = "Wybierz obraz" };
        if (dlg.ShowDialog(this) != true) return null;
        try
        {
            var dir = System.IO.Path.Combine(Themes.UserDir, "assets");
            Directory.CreateDirectory(dir);
            var name = System.IO.Path.GetFileName(dlg.FileName);
            var dest = System.IO.Path.Combine(dir, name);
            if (!string.Equals(System.IO.Path.GetFullPath(dlg.FileName), System.IO.Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase)) File.Copy(dlg.FileName, dest, true);
            using var img = SKBitmap.Decode(dest) ?? throw new InvalidDataException("Nie da się odczytać obrazu");
            float scale = Math.Min(1, Math.Min(240f / img.Width, 160f / img.Height));
            return new Widget { Type = "image", Src = "assets/" + name, X = 120, Y = 80, W = (float)Math.Round(img.Width * scale), H = (float)Math.Round(img.Height * scale) };
        }
        catch (Exception e) { Toast("Nie udało się dodać obrazu: " + e.Message, true); return null; }
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
        head.Children.Add(new TextBlock { Text = info.Item2, FontFamily = (FontFamily)FindResource("Ico"), FontSize = 16, Foreground = AmberBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        head.Children.Add(new TextBlock { Text = info.Item1, Style = (Style)FindResource("H2") });
        Props.Children.Add(head);
        TextRow("Nazwa (dla Ciebie)", () => w.Name, v => w.Name = string.IsNullOrWhiteSpace(v) ? null : v);

        Props.Children.Add(Caption("POŁOŻENIE"));
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
            fw = NumField(pos, 1, 0, w.Type == "text" ? "Maks. szer." : "Szer.", w.W, v => w.W = Math.Max(0, v));
            if (box) fh = NumField(pos, 1, 2, "Wys.", w.H, v => w.H = Math.Max(0, v));
        }
        Props.Children.Add(pos);
        Props.Children.Add(Switch("Zablokuj", "Nie da się go złapać myszą na podglądzie", w.Locked == true, v => { w.Locked = v ? true : null; Changed(true); }));

        switch (w.Type)
        {
            case "text":
                Props.Children.Add(Caption("TREŚĆ"));
                var tb = TextRow(null, () => w.Text, v => w.Text = v, mono: true);
                Props.Children.Add(new TextBlock { Text = "Dane wstawiasz w klamrach: {cpu.temp:0}, {net.down:rate}, {time:HH:mm}, {date:dddd}", Style = (Style)FindResource("SubText"), FontSize = 11, Margin = new Thickness(0, 2, 0, 6) });
                var ins = Btn("Wstaw dane", "", false, () => { });
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
                ColorRows(w, "text", "KOLOR");
                Slider("Poświata (neon)", 0, 24, w.Glow ?? 0, v => w.Glow = v > 0 ? (float)v : null);
                Slider("Krycie", 0.05, 1, w.Opacity ?? 1, v => w.Opacity = v < 0.999 ? (float)v : null, "0.00");
                Thresholds(w, "Czujnik progów koloru");
                Flags(w, ("upper", "WIELKIE LITERY"), ("blink", "Miganie"));
                break;
            case "icon":
                Props.Children.Add(Caption("IKONA"));
                GlyphGrid(w);
                Slider("Rozmiar", 8, 120, w.Size ?? 20, v => w.Size = (float)Math.Round(v));
                ColorRows(w, "a1", "KOLOR");
                Slider("Poświata", 0, 24, w.Glow ?? 0, v => w.Glow = v > 0 ? (float)v : null);
                Slider("Krycie", 0.05, 1, w.Opacity ?? 1, v => w.Opacity = v < 0.999 ? (float)v : null, "0.00");
                Thresholds(w, "Czujnik progów koloru");
                break;
            case "rect":
                ColorRows(w, "card", "WYPEŁNIENIE", allowNone: true);
                Slider("Zaokrąglenie", 0, 80, w.Radius ?? 0, v => w.Radius = v > 0 ? (float)Math.Round(v) : null);
                Props.Children.Add(Caption("OBRYS"));
                ColorButton("Kolor obrysu", () => w.Stroke, v => w.Stroke = v, true);
                Slider("Grubość obrysu", 0.5, 8, w.StrokeWidth ?? 1, v => w.StrokeWidth = Math.Abs(v - 1) > 0.01 ? (float)v : null, "0.0");
                Props.Children.Add(Caption("CIEŃ / POŚWIATA"));
                ColorButton("Kolor cienia", () => w.Shadow, v => w.Shadow = v, true);
                Slider("Rozmycie", 0, 40, w.Glow ?? 0, v => w.Glow = v > 0 ? (float)Math.Round(v) : null);
                Slider("Krycie", 0.05, 1, w.Opacity ?? 1, v => w.Opacity = v < 0.999 ? (float)v : null, "0.00");
                Flags(w, ("radial", "Gradient kołowy"));
                break;
            case "glow":
                Props.Children.Add(Caption("KOLOR"));
                ColorButton("Kolor", () => w.Color, v => w.Color = v ?? "a1/30", false);
                Slider("Krycie", 0.05, 1, w.Opacity ?? 1, v => w.Opacity = v < 0.999 ? (float)v : null, "0.00");
                break;
            case "image":
                Props.Children.Add(Caption("OBRAZ"));
                TextRow("Plik (w folderze motywów)", () => w.Src, v => w.Src = v);
                Slider("Zaokrąglenie", 0, 80, w.Radius ?? 0, v => w.Radius = v > 0 ? (float)Math.Round(v) : null);
                Slider("Krycie", 0.05, 1, w.Opacity ?? 1, v => w.Opacity = v < 0.999 ? (float)v : null, "0.00");
                break;
            case "bar":
                DataRows(w, 100);
                ColorRows(w, "a1", "KOLOR");
                ColorButton("Tło paska", () => w.Track, v => w.Track = v, true, "track");
                Slider("Zaokrąglenie", 0, 40, w.Radius ?? Math.Min(w.W, w.H) / 2, v => w.Radius = (float)Math.Round(v, 1));
                Slider("Segmenty (0 = gładki)", 0, 60, w.Segments ?? 0, v => w.Segments = v >= 1 ? (int)v : null);
                Slider("Odstęp segmentów", 0, 10, w.Gap ?? 2, v => w.Gap = (float)Math.Round(v, 1), "0.0");
                Slider("Poświata", 0, 24, w.Glow ?? 0, v => w.Glow = v > 0 ? (float)v : null);
                Slider("Płynność animacji (s)", 0, 1.5, w.Smooth ?? 0.35, v => w.Smooth = (float)Math.Round(v, 2), "0.00");
                Thresholds(w, null);
                Flags(w, ("vertical", "Pionowy"));
                break;
            case "gauge":
                DataRows(w, 100);
                ColorRows(w, "a1", "KOLOR");
                ColorButton("Tło łuku", () => w.Track, v => w.Track = v, true, "track");
                Props.Children.Add(Caption("KSZTAŁT"));
                Slider("Grubość", 1, 40, w.Thickness ?? 10, v => w.Thickness = (float)Math.Round(v));
                Slider("Kąt początku", -180, 360, w.Start ?? 135, v => w.Start = (float)Math.Round(v));
                Slider("Rozpiętość", 10, 360, w.Sweep ?? 270, v => w.Sweep = (float)Math.Round(v));
                Slider("Segmenty (0 = gładki)", 0, 60, w.Segments ?? 0, v => w.Segments = v >= 1 ? (int)v : null);
                Slider("Odstęp segmentów (°)", 0, 12, w.Gap ?? 3, v => w.Gap = (float)Math.Round(v, 1), "0.0");
                Slider("Poświata", 0, 24, w.Glow ?? 0, v => w.Glow = v > 0 ? (float)v : null);
                Slider("Liczba kresek skali", 2, 60, w.Points ?? 10, v => w.Points = (int)v);
                Slider("Płynność animacji (s)", 0, 1.5, w.Smooth ?? 0.35, v => w.Smooth = (float)Math.Round(v, 2), "0.00");
                Thresholds(w, null);
                Flags(w, ("dot", "Kropka na końcu"), ("flat", "Płaskie końce"), ("ticks", "Kreski skali"), ("alternate", "Zegar: napełnij i opróżnij"));
                break;
            case "graph":
                DataRows(w, 0, "Maks. (0 = automatycznie)");
                ColorRows(w, "a1", "KOLOR");
                ColorButton("Linie siatki", () => w.Track, v => w.Track = v, true, "track");
                Slider("Liczba próbek", 10, Snapshot.HistoryLength, w.Points ?? 60, v => w.Points = (int)v);
                Slider("Grubość linii", 0.5, 6, w.Thickness ?? 2, v => w.Thickness = (float)Math.Round(v, 1), "0.0");
                Slider("Poświata", 0, 16, w.Glow ?? 0, v => w.Glow = v > 0 ? (float)v : null);
                Flags(w, ("sweep", "Oscyloskop (płynny)"), ("dot", "Świecący punkt"), ("grid", "Siatka"), ("sharp", "Ostre łamanie"), ("nofill", "Bez wypełnienia"));
                break;
            case "cores":
                Props.Children.Add(Caption("DANE"));
                TextRow("Prefiks czujników (cpu.t = wątki)", () => w.Sensor, v => w.Sensor = v);
                ColorRows(w, "a1", "KOLOR (OD DOŁU)");
                ColorButton("Tło słupków", () => w.Track, v => w.Track = v, true, "track");
                Slider("Odstęp", 0, 20, w.Gap ?? 3, v => w.Gap = (float)Math.Round(v));
                Slider("Zaokrąglenie", 0, 20, w.Radius ?? 3, v => w.Radius = (float)Math.Round(v));
                Slider("Płynność animacji (s)", 0, 1.5, w.Smooth ?? 0.35, v => w.Smooth = (float)Math.Round(v, 2), "0.00");
                Flags(w, ("horizontal", "Poziomo"));
                break;
        }
        var del = Btn("Usuń element", "", false, DeleteSel);
        del.Margin = new Thickness(0, 18, 0, 6);
        Props.Children.Add(del);
    }

    static string? DefaultFormat(SensorInfo s) => s.Key switch
    {
        "time" => "HH:mm", "date" => "dddd, d MMMM",
        _ when s.Unit == "B/s" => "rate",
        _ when s.Unit is "GB" or "V" or "A" => "0.0",
        _ when s.Unit.Length == 0 => null,
        _ => "0",
    };

    TextBox NumField(Grid g, int row, int col, string label, float value, Action<float> set)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        sp.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = SubBrush, Margin = new Thickness(2, 0, 0, 4) });
        var tb = new TextBox { Text = value.ToString("0.#", CultureInfo.InvariantCulture), FontFamily = (FontFamily)FindResource("Mono") };
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
        var val = new TextBlock { Text = value.ToString(fmt, CultureInfo.InvariantCulture), FontSize = 12, FontFamily = (FontFamily)FindResource("Mono"), HorizontalAlignment = HorizontalAlignment.Right };
        g.Children.Add(val);
        Props.Children.Add(g);
        var s = new System.Windows.Controls.Slider { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), Margin = new Thickness(0, 2, 0, 2) };
        s.ValueChanged += (_, e) => { val.Text = e.NewValue.ToString(fmt, CultureInfo.InvariantCulture); set(e.NewValue); Changed(); };
        Props.Children.Add(s);
    }

    void FontRows(Widget w)
    {
        Props.Children.Add(Caption("CZCIONKA"));
        fontNames ??= Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(n => n).ToList();
        string cur = w.Font ?? draft!.Font;
        var items = new List<(string, string)> { ("", $"Z motywu ({draft!.Font})") };
        items.AddRange(fontNames.Select(n => (n, n)));
        var cb = Combo(items, w.Font ?? "", v => { w.Font = v.Length == 0 ? null : v; Changed(); });
        cb.Margin = new Thickness(0, 0, 0, 6);
        Props.Children.Add(cb);
        Slider("Rozmiar", 6, 160, w.Size ?? 16, v => w.Size = (float)Math.Round(v, 1), "0.#");
        Props.Children.Add(new TextBlock { Text = "Grubość", FontSize = 12, Foreground = SubBrush, Margin = new Thickness(0, 8, 0, 6) });
        Props.Children.Add(Chips(new[] { (300, "Cienka"), (400, "Zwykła"), (600, "Półgruba"), (700, "Gruba") }, w.Weight ?? 400, v => { w.Weight = v == 400 ? null : v; Changed(); }));
        Props.Children.Add(new TextBlock { Text = "Szerokość liter", FontSize = 12, Foreground = SubBrush, Margin = new Thickness(0, 4, 0, 6) });
        Props.Children.Add(Chips(new[] { (3, "Wąskie"), (4, "Półwąskie"), (5, "Normalne") }, w.Stretch ?? 5, v => { w.Stretch = v == 5 ? null : v; Changed(); }));
        Props.Children.Add(new TextBlock { Text = "Wyrównanie do punktu X", FontSize = 12, Foreground = SubBrush, Margin = new Thickness(0, 4, 0, 6) });
        Props.Children.Add(Chips(new[] { ("left", "Do lewej"), ("center", "Środek"), ("right", "Do prawej") }, w.Align ?? "left", v => { w.Align = v == "left" ? null : v; Changed(); }));
        Slider("Odstęp liter", -2, 10, w.Spacing ?? 0, v => w.Spacing = Math.Abs(v) > 0.01 ? (float)Math.Round(v, 1) : null, "0.0");
    }

    void ColorRows(Widget w, string fallback, string title, bool allowNone = false)
    {
        Props.Children.Add(Caption(title));
        bool gradient = w.Colors is { Length: > 1 };
        var toggle = new CheckBox { Style = (Style)FindResource("Flag"), Content = "Gradient", IsChecked = gradient, HorizontalAlignment = HorizontalAlignment.Left };
        toggle.Checked += (_, _) => { w.Colors = [w.Color ?? fallback, "a2"]; w.Color = null; Changed(); RebuildProps(); };
        toggle.Unchecked += (_, _) => { w.Color = w.Colors?.FirstOrDefault() ?? fallback; w.Colors = null; Changed(); RebuildProps(); };
        Props.Children.Add(toggle);
        if (!gradient)
        {
            ColorButton("Kolor", () => w.Color, v => w.Color = v, allowNone, fallback);
            return;
        }
        for (int i = 0; i < w.Colors!.Length; i++)
        {
            int idx = i;
            ColorButton($"Kolor {i + 1}", () => w.Colors[idx], v => { if (v != null) w.Colors[idx] = v; }, false);
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        if (w.Colors.Length < 4) row.Children.Add(Btn("Dodaj kolor", "", false, () => { w.Colors = [.. w.Colors, w.Colors[^1]]; Changed(); RebuildProps(); }));
        if (w.Colors.Length > 2) { row.Children.Add(Gap(6)); row.Children.Add(Btn("", "", false, () => { w.Colors = w.Colors[..^1]; Changed(); RebuildProps(); }, "Usuń ostatni")); }
        Props.Children.Add(row);
        if (w.Type is "rect" or "text" or "icon")
            Slider("Kierunek gradientu (°)", 0, 360, w.Angle ?? 90, v => w.Angle = (float)Math.Round(v));
    }

    void ColorButton(string label, Func<string?> get, Action<string?> set, bool allowNone, string? fallback = null)
    {
        var swatch = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(7), Background = ColorPicker.Checker(), BorderBrush = Br("#2C3646"), BorderThickness = new Thickness(1) };
        var fill = new Border { CornerRadius = new CornerRadius(6) };
        swatch.Child = fill;
        var text = new TextBlock { FontFamily = (FontFamily)FindResource("Mono"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        void Refresh()
        {
            var spec = get();
            var c = Renderer.Resolve(draft!, spec ?? fallback, SKColors.Transparent);
            fill.Background = spec == null && fallback == null ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue));
            text.Text = spec ?? (fallback != null ? $"({fallback})" : "brak");
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
        b.MouseEnter += (_, _) => b.Background = Br("#1A212C");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, _) => new ColorPicker(b, draft!, get(), allowNone, v => { set(v); Refresh(); Changed(); });
        Props.Children.Add(b);
    }

    void DataRows(Widget w, double defMax, string maxLabel = "Maks.")
    {
        Props.Children.Add(Caption("DANE"));
        SensorButton(w);
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        NullableField(g, 0, "Min.", w.Min ?? 0, v => w.Min = v == 0 ? null : v);
        NullableField(g, 2, maxLabel, w.Max ?? defMax, v => w.Max = v == defMax && defMax != 0 ? null : v);
        Props.Children.Add(g);
    }

    void Thresholds(Widget w, string? sensorLabel)
    {
        Props.Children.Add(Caption("PROGI KOLORU"));
        if (sensorLabel != null) SensorButton(w, sensorLabel, true);
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        NullableField(g, 0, "Ostrzeżenie od", w.Warn, v => w.Warn = v, true);
        NullableField(g, 2, "Alarm od", w.Crit, v => w.Crit = v, true);
        Props.Children.Add(g);
        Props.Children.Add(new TextBlock { Text = "Kolory „warn” i „crit” z palety. Puste pole = bez progu.", Style = (Style)FindResource("SubText"), FontSize = 11 });
    }

    void NullableField(Grid g, int col, string label, double? value, Action<double?> set, bool nullable = false)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        sp.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = SubBrush, Margin = new Thickness(2, 6, 0, 4) });
        var tb = new TextBox { Text = value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "", FontFamily = (FontFamily)FindResource("Mono") };
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

    void SensorButton(Widget w, string label = "Czujnik", bool allowNone = false)
    {
        var all = Sensors.Catalog.Concat(App.Sensors.Raw);
        string Describe() => w.Sensor == null ? "wybierz…" : all.FirstOrDefault(s => s.Key == w.Sensor) is { } i ? $"{i.Group} · {i.Label}" : w.Sensor;
        var main = new TextBlock { Text = Describe(), TextTrimming = TextTrimming.CharacterEllipsis };
        var key = new TextBlock { Text = w.Sensor ?? "", FontFamily = (FontFamily)FindResource("Mono"), FontSize = 11, Foreground = DimBrush };
        var sp = new StackPanel { Children = { main, key } };
        var b = new Border
        {
            Child = new DockPanel { Children = { new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Ico"), FontSize = 10, Foreground = SubBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) }, sp } },
            Background = Br("#0E131A"), BorderBrush = Br("#27303F"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 6, 10, 6), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 6),
        };
        DockPanel.SetDock((UIElement)((DockPanel)b.Child).Children[0], Dock.Right);
        b.MouseLeftButtonUp += (_, _) => new SensorPicker(b, allowNone, s => { w.Sensor = s?.Key; main.Text = Describe(); key.Text = w.Sensor ?? ""; Changed(); });
        Props.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = SubBrush, Margin = new Thickness(2, 0, 0, 4) });
        Props.Children.Add(b);
    }

    void Flags(Widget w, params (string Flag, string Label)[] flags)
    {
        Props.Children.Add(Caption("OPCJE"));
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
                Width = 34, Height = 34, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 4, 4), Cursor = Cursors.Hand, ToolTip = name,
                Background = w.Glyph == glyph ? Br("#241A10") : Br("#151B25"), BorderBrush = w.Glyph == glyph ? Br("#FF9A3D") : Br("#27303F"), BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = char.ConvertFromUtf32(Convert.ToInt32(glyph, 16)), FontFamily = (FontFamily)FindResource("Ico"), FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = TextBrush },
            };
            b.MouseLeftButtonUp += (_, _) => { w.Glyph = g; Changed(); RebuildProps(); };
            wp.Children.Add(b);
        }
        Props.Children.Add(wp);
        TextRow("Kod znaku (Segoe Fluent Icons, hex)", () => w.Glyph, v => w.Glyph = v, mono: true);
    }

    void ThemeProps()
    {
        var t = draft!;
        Props.Children.Add(new TextBlock { Text = "Motyw", Style = (Style)FindResource("H2"), Margin = new Thickness(0, 0, 0, 4) });
        Props.Children.Add(new TextBlock { Text = "Kliknij element na podglądzie, żeby go edytować.", Style = (Style)FindResource("SubText"), Margin = new Thickness(0, 0, 0, 10) });
        TextRow("Nazwa", () => t.Name, v => t.Name = string.IsNullOrWhiteSpace(v) ? "Bez nazwy" : v);
        TextRow("Opis", () => t.Description, v => t.Description = string.IsNullOrWhiteSpace(v) ? null : v);
        Props.Children.Add(new TextBlock { Text = "Czcionka motywu", FontSize = 11, Foreground = SubBrush, Margin = new Thickness(2, 6, 0, 4) });
        fontNames ??= Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(n => n).ToList();
        var cb = Combo(fontNames.Select(n => (n, n)), t.Font, v => { t.Font = v; Changed(); });
        cb.Margin = new Thickness(0, 0, 0, 8);
        Props.Children.Add(cb);
        ColorButton("Tło ekranu", () => t.Bg, v => t.Bg = v ?? "#000000", false);

        Props.Children.Add(Caption("SZYBKIE AKCENTY"));
        var wp = new WrapPanel();
        foreach (var (name, a1, a2, a3) in Presets)
        {
            var b = new Border
            {
                Width = 66, Height = 30, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand, ToolTip = name,
                Background = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(a1), (Color)ColorConverter.ConvertFromString(a2), 0),
                Child = new TextBlock { Text = name, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Br("#14100A"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            var (x1, x2, x3) = (a1, a2, a3);
            b.MouseLeftButtonUp += (_, _) => { t.Palette["a1"] = x1; t.Palette["a2"] = x2; t.Palette["a3"] = x3; Changed(); RebuildProps(); };
            wp.Children.Add(b);
        }
        Props.Children.Add(wp);

        Props.Children.Add(Caption("PALETA"));
        Props.Children.Add(new TextBlock { Text = "Elementy używają kolorów po nazwie, np. a1 albo a1/30 (30% krycia). Zmiana tutaj zmienia cały motyw.", Style = (Style)FindResource("SubText"), FontSize = 11, Margin = new Thickness(0, 0, 0, 8) });
        foreach (var name in t.Palette.Keys.ToList())
        {
            var n = name;
            ColorButton(n, () => t.Palette.GetValueOrDefault(n), v => { if (v != null && v != n) t.Palette[n] = v; }, false);
        }
        var add = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var nameBox = new TextBox { FontFamily = (FontFamily)FindResource("Mono"), ToolTip = "Nazwa nowego koloru" };
        var addBtn = Btn("Dodaj", "", false, () =>
        {
            var n = nameBox.Text.Trim();
            if (n.Length == 0 || n.StartsWith('#') || n.Contains('/') || t.Palette.ContainsKey(n)) { Toast("Podaj nową nazwę bez # i /", true); return; }
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
