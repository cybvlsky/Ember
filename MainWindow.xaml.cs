using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SkiaSharp;

namespace Ember;

public partial class MainWindow : Window
{
    const int W = TuringLcd.Width, H = TuringLcd.Height;
    const int GlowW = 24, GlowH = 16;
    static readonly Brush TextBrush = Br("#EEE8E2"), SubBrush = Br("#A69D94"), DimBrush = Br("#756D66"), OkBrush = Br("#8BD49C"), BadBrush = Br("#FF7466"),
        EmberBrush = Br("#FF7A33"), LineBrush = Br("#3A3633"), RaisedBrush = Br("#34302D");

    readonly DispatcherTimer timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    readonly WriteableBitmap screenBmp = new(W, H, 96, 96, PixelFormats.Pbgra32, null);
    readonly WriteableBitmap glowBmp = new(GlowW, GlowH, 96, 96, PixelFormats.Pbgra32, null);
    readonly byte[] screenBuf = new byte[W * H * 4], glowBuf = new byte[GlowW * GlowH * 4];
    int previewSeen = -1, tick;
    string page = "screen";
    readonly Grid toastHost = new() { IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 64) };
    readonly Grid modalHost = new() { Visibility = Visibility.Collapsed, Background = Br("#A0141210") };

    static string T(string en) => L.T(en);

    public MainWindow()
    {
        InitializeComponent();
        var root = (Grid)Root.Child;
        root.Children.Add(toastHost);
        Grid.SetRowSpan(toastHost, 2);
        root.Children.Add(modalHost);
        Grid.SetRowSpan(modalHost, 2);
        using (var logo = Icons.Logo(44)) Logo.Source = ToImage(logo);
        using (var icon = Icons.Logo(64)) Icon = ToImage(icon);
        ScreenImage.Source = screenBmp;
        GlowImage.Source = glowBmp;

        NavScreen.Checked += (_, _) => Show("screen");
        NavThemes.Checked += (_, _) => Show("themes");
        NavEditor.Checked += (_, _) => Show("editor");
        NavSensors.Checked += (_, _) => Show("sensors");
        NavSettings.Checked += (_, _) => Show("settings");
        MinBtn.Click += (_, _) => WindowState = WindowState.Minimized;
        MaxBtn.Click += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        CloseBtn.Click += (_, _) => Close();
        StateChanged += (_, _) =>
        {
            Root.Margin = new Thickness(WindowState == WindowState.Maximized ? 7 : 0);
            // Minimised: nothing is visible, so the UI clock (preview, live thumbnails) and the engine's preview copies stop.
            bool min = WindowState == WindowState.Minimized;
            if (min) timer.Stop(); else timer.Start();
            App.Engine.PreviewWanted = !min && page == "screen";
        };

        InitScreenPage();
        InitEditor();
        SensorSearch.TextChanged += (_, _) => { SearchHint.Visibility = SensorSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; FilterSensors(); };
        timer.Tick += (_, _) => Tick();
        Loaded += (_, _) => OnLoaded();
        Closing += (_, e) => { if (EditorDirty && !closeConfirmed) { e.Cancel = true; _ = ConfirmClose(); } };
        Closed += (_, _) => { timer.Stop(); App.Engine.PreviewWanted = false; StopLiveEdit(); DisposeGallery(); DisposeEditor(); };
        PreviewKeyDown += OnKey;
        Show("screen");
    }

    bool closeConfirmed;

    async Task ConfirmClose()
    {
        if (await Confirm(T("Unsaved changes"), T("The theme in the editor has unsaved changes. Close the window and lose them?"), T("Discard"), T("Back to the editor")))
        {
            closeConfirmed = true;
            Close();
        }
        else NavEditor.IsChecked = true;
    }

    void OnLoaded()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int round = 2;
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int)); // Windows 11 rounded corners
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        timer.Start();
    }

    [DllImport("dwmapi")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

    // ---------- shell ----------

    public void GoTo(string name) =>
        (name switch { "themes" => NavThemes, "editor" => NavEditor, "sensors" => NavSensors, "settings" => NavSettings, _ => NavScreen }).IsChecked = true;

    void Show(string name)
    {
        if (page == "editor" && name != "editor") StopLiveEdit();
        page = name;
        var pages = new Dictionary<string, FrameworkElement> { ["screen"] = PageScreen, ["themes"] = PageThemes, ["editor"] = PageEditor, ["sensors"] = PageSensors, ["settings"] = PageSettings };
        foreach (var (k, el) in pages) el.Visibility = k == name ? Visibility.Visible : Visibility.Collapsed;
        pages[name].BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        TopActions.Children.Clear();
        App.Engine.PreviewWanted = name == "screen";
        switch (name)
        {
            case "themes":
                TopActions.Children.Add(Btn(T("Import"), null, false, ImportTheme));
                TopActions.Children.Add(Gap());
                TopActions.Children.Add(Btn(T("Open folder"), null, false, () => App.OpenFolder(Themes.UserDir)));
                TopActions.Children.Add(Gap());
                TopActions.Children.Add(Btn(T("New theme"), null, true, () => NewTheme()));
                BuildGallery();
                break;
            case "editor":
                ShowEditorActions();
                break;
            case "sensors":
                BuildSensors();
                break;
            case "settings":
                BuildSettings();
                break;
        }
        if (name != "themes") DisposeGallery();
    }

    void Tick()
    {
        tick++;
        switch (page)
        {
            case "screen":
                if (App.Engine.CopyPreview(screenBuf, ref previewSeen))
                {
                    screenBmp.WritePixels(new Int32Rect(0, 0, W, H), screenBuf, W * 4, 0);
                    if (tick % 4 == 0) UpdateGlow();
                }
                if (tick % 10 == 0) UpdateScreenStatus();
                break;
            case "themes":
                if (tick % 4 == 0) TickGallery();
                break;
            case "editor":
                TickEditor();
                break;
            case "sensors":
                if (tick % 30 == 0) UpdateSensors();
                break;
        }
    }

    /// Averages the frame into a 24x16 grid; blown up and blurred behind the device it reads as the panel lighting the desk.
    void UpdateGlow()
    {
        const int cw = W / GlowW, ch = H / GlowH;
        for (int gy = 0; gy < GlowH; gy++)
            for (int gx = 0; gx < GlowW; gx++)
            {
                int r = 0, g = 0, b = 0, n = 0;
                for (int y = gy * ch + 2; y < (gy + 1) * ch; y += 4)
                    for (int x = gx * cw + 2; x < (gx + 1) * cw; x += 4)
                    {
                        int i = (y * W + x) * 4;
                        b += screenBuf[i]; g += screenBuf[i + 1]; r += screenBuf[i + 2]; n++;
                    }
                int o = (gy * GlowW + gx) * 4;
                glowBuf[o] = (byte)Math.Min(255, b * 3 / 2 / n);
                glowBuf[o + 1] = (byte)Math.Min(255, g * 3 / 2 / n);
                glowBuf[o + 2] = (byte)Math.Min(255, r * 3 / 2 / n);
                glowBuf[o + 3] = 255;
            }
        glowBmp.WritePixels(new Int32Rect(0, 0, GlowW, GlowH), glowBuf, GlowW * 4, 0);
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (page == "editor") EditorKey(e);
    }

    /// `--screenshots <dir>`: every page as a PNG at 2x, for checking the UI without a monitor and for the README.
    public async Task Screenshots(string dir)
    {
        Directory.CreateDirectory(dir);
        async Task Shot(string name)
        {
            await Task.Delay(1200);
            int w = (int)Root.ActualWidth, h = (int)Root.ActualHeight;
            var rtb = new RenderTargetBitmap(w * 2, h * 2, 192, 192, PixelFormats.Pbgra32);
            rtb.Render(Root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var f = File.Create(Path.Combine(dir, name + ".png"));
            enc.Save(f);
        }
        await Shot("screen");
        NavThemes.IsChecked = true; await Shot("themes");
        NavEditor.IsChecked = true; await Shot("editor");
        if (draft != null && draft.Widgets.Count > 12) { Select(draft.Widgets.FindIndex(w => w.Type == "gauge")); await Shot("editor-selected"); }
        NavSensors.IsChecked = true; await Shot("sensors");
        NavSettings.IsChecked = true; await Shot("settings");
        await ExerciseEditor(Shot);
    }

    /// Drives the editor through every widget type and edit operation; failures land in the log as "ui" errors.
    async Task ExerciseEditor(Func<string, Task> shot)
    {
        NavEditor.IsChecked = true;
        string before = App.Engine.Theme.Id;
        NewTheme();
        await Task.Delay(300);
        foreach (var k in new[] { "text", "value", "gauge", "ring", "bar", "graph", "cores", "icon", "rect", "glow", "clock" })
        {
            AddWidget(k);
            for (int i = 0; i < 3; i++) TickEditor();
            await Task.Delay(30);
        }
        int n = draft!.Widgets.Count;
        for (int i = 0; i < n; i++) { Select(i); TickEditor(); await Task.Delay(20); }
        Select(draft.Widgets.FindIndex(w => w.Type == "gauge"));
        await shot("uitest-gauge");
        Select(draft.Widgets.FindIndex(w => w.Type == "graph"));
        MoveLayer(-1); MoveLayer(1); Duplicate(); DeleteSel();
        Select(-1);
        TickEditor();
        var (dx, guide) = SnapMove(10, 100, 225 - 10 - 50 + 1.5, false);
        Log.Info($"uitest snap: dx={dx} guide={guide}");
        for (int i = 0; i < 40; i++) Undo();
        for (int i = 0; i < 5; i++) Redo();
        TickEditor();
        draft.Name = "UI test";
        Save();
        bool saved = File.Exists(Path.Combine(Themes.UserDir, draft.Id + ".json"));
        Log.Info($"uitest saved={saved} widgets={draft.Widgets.Count} undo={undo.Count}");
        Themes.Delete(draft);
        App.AllThemes = Themes.LoadAll();
        App.SetTheme(App.AllThemes.FirstOrDefault(t => t.Id == before) ?? App.AllThemes[0]);
        savedJson = draft.ToJson();
        NavThemes.IsChecked = true;
        await Task.Delay(400);
        NavScreen.IsChecked = true;
        await Task.Delay(300);
        Log.Info("uitest done");
    }

    // ---------- helpers ----------

    public static SolidColorBrush Br(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public static BitmapSource ToImage(SKBitmap bmp)
    {
        var src = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96, PixelFormats.Pbgra32, null, bmp.GetPixels(), bmp.ByteCount, bmp.RowBytes);
        src.Freeze();
        return src;
    }

    static FrameworkElement Gap(double w = 8) => new Border { Width = w };

    public Button Btn(string text, string? icon, bool primary, Action click, string? tip = null)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon != null) sp.Children.Add(new TextBlock { Text = icon, FontFamily = (FontFamily)FindResource("Ico"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, text.Length > 0 ? 8 : 0, 0) });
        if (text.Length > 0) sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Content = sp, Style = (Style)FindResource(primary ? "Primary" : "Ghost"), ToolTip = tip };
        if (text.Length == 0) b.Padding = new Thickness(9, 7, 9, 7);
        b.Click += (_, _) => click();
        return b;
    }

    Button Link(string text, Action click)
    {
        var b = new Button { Content = text, Style = (Style)FindResource("Link"), Margin = new Thickness(0, 0, 18, 0) };
        b.Click += (_, _) => click();
        return b;
    }

    public CheckBox Switch(string title, string? sub, bool value, Action<bool> changed)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = title });
        if (sub != null) sp.Children.Add(new TextBlock { Text = sub, Style = (Style)FindResource("SubText"), FontSize = 12, Margin = new Thickness(0, 2, 0, 0) });
        var cb = new CheckBox { Style = (Style)FindResource("Switch"), Content = sp, IsChecked = value, Margin = new Thickness(0, 4, 0, 12) };
        cb.Checked += (_, _) => changed(true);
        cb.Unchecked += (_, _) => changed(false);
        return cb;
    }

    public ComboBox Combo(IEnumerable<(string Value, string Label)> items, string selected, Action<string> changed)
    {
        var cb = new ComboBox();
        foreach (var (v, l) in items) cb.Items.Add(new ComboBoxItem { Content = l, Tag = v });
        cb.SelectedItem = cb.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == selected) ?? (cb.Items.Count > 0 ? cb.Items[0] : null);
        cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is ComboBoxItem i) changed((string)i.Tag); };
        return cb;
    }

    /// A segmented control: one frame, the chosen segment filled.
    public Border Chips<TV>(IEnumerable<(TV Value, string Label)> items, TV selected, Action<TV> changed)
    {
        var wp = new WrapPanel();
        string group = Guid.NewGuid().ToString();
        foreach (var (v, l) in items)
        {
            var rb = new RadioButton { Content = l, GroupName = group, Style = (Style)FindResource("Seg"), IsChecked = EqualityComparer<TV>.Default.Equals(v, selected) };
            var val = v;
            rb.Checked += (_, _) => changed(val);
            wp.Children.Add(rb);
        }
        return new Border { Child = wp, BorderBrush = (Brush)FindResource("Line2"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
    }

    public TextBlock Caption(string text, double top = 18) => new() { Text = text, Style = (Style)FindResource("Head"), Margin = new Thickness(0, top, 0, 8) };

    public void Toast(string text, bool bad = false)
    {
        var b = new Border
        {
            Background = bad ? BadBrush : TextBrush, CornerRadius = new CornerRadius(6), Padding = new Thickness(16, 9, 16, 10),
            Child = new TextBlock { Text = text, Foreground = Br("#1E1C1A"), FontWeight = FontWeights.SemiBold, FontSize = 13 },
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Opacity = 0.4 },
            Opacity = 0,
        };
        toastHost.Children.Clear();
        toastHost.Children.Add(b);
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150))));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2600))));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3000))));
        b.BeginAnimation(OpacityProperty, anim);
    }

    /// In-window confirm dialog (a MessageBox would break the look).
    public Task<bool> Confirm(string title, string text, string yes, string no)
    {
        var tcs = new TaskCompletionSource<bool>();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        void Done(bool v) { modalHost.Visibility = Visibility.Collapsed; modalHost.Children.Clear(); tcs.TrySetResult(v); }
        buttons.Children.Add(Btn(no, null, false, () => Done(false)));
        buttons.Children.Add(Gap(10));
        buttons.Children.Add(Btn(yes, null, true, () => Done(true)));
        var box = new Border
        {
            Background = (Brush)FindResource("Panel"), BorderBrush = (Brush)FindResource("Line2"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Width = 440, Padding = new Thickness(26, 22, 26, 22), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel { Children = { new TextBlock { Text = title, FontSize = 18 }, new TextBlock { Text = text, Style = (Style)FindResource("SubText"), Margin = new Thickness(0, 10, 0, 0), FontSize = 13.5 }, buttons } },
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 40, ShadowDepth = 10, Opacity = 0.5 },
        };
        modalHost.Children.Clear();
        modalHost.Children.Add(box);
        modalHost.Visibility = Visibility.Visible;
        modalHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        return tcs.Task;
    }

    // ---------- screen page ----------

    void InitScreenPage()
    {
        BrightSlider.Value = App.Cfg.Brightness;
        BrightText.Text = $"{App.Cfg.Brightness}%";
        BrightSlider.ValueChanged += (_, e) =>
        {
            App.Cfg.Brightness = (int)Math.Round(e.NewValue);
            BrightText.Text = $"{App.Cfg.Brightness}%";
            App.Engine.Poke();
            App.SaveSoon();
        };
        FlipSwitch.IsChecked = App.Cfg.Flip;
        FlipSwitch.Checked += (_, _) => { App.Cfg.Flip = true; App.Cfg.Save(); App.Engine.Poke(); };
        FlipSwitch.Unchecked += (_, _) => { App.Cfg.Flip = false; App.Cfg.Save(); App.Engine.Poke(); };
        SleepSwitch.IsChecked = App.Cfg.SleepWithMonitors;
        SleepSwitch.Checked += (_, _) => { App.Cfg.SleepWithMonitors = true; App.Cfg.Save(); App.Engine.Poke(); };
        SleepSwitch.Unchecked += (_, _) => { App.Cfg.SleepWithMonitors = false; App.Cfg.Save(); App.Engine.Poke(); };
        BlankSwitch.IsChecked = App.Engine.Blank;
        BlankSwitch.Checked += (_, _) => App.Engine.Blank = true;
        BlankSwitch.Unchecked += (_, _) => App.Engine.Blank = false;
        PrevTheme.Click += (_, _) => StepTheme(-1);
        NextTheme.Click += (_, _) => StepTheme(1);
        BrowseThemes.Click += (_, _) => NavThemes.IsChecked = true;
        UpdateThemeCard();
        UpdateScreenStatus();
    }

    void StepTheme(int d)
    {
        var list = App.AllThemes;
        int i = list.FindIndex(t => t.Id == App.Engine.Theme.Id);
        App.SetTheme(list[((i < 0 ? 0 : i) + d + list.Count) % list.Count]);
        UpdateThemeCard();
    }

    void UpdateThemeCard()
    {
        ThemeName.Text = T(App.Engine.Theme.Name);
        ThemeDesc.Text = App.Engine.Theme.Description is { } d ? T(d) : "";
    }

    int lastStatusHash;

    void UpdateScreenStatus()
    {
        var e = App.Engine;
        ConnText.Text = e.Status;
        ConnDot.Fill = e.Connected ? OkBrush : e.Searching ? DimBrush : BadBrush;
        FpsText.Text = L.F("{0:0} fps", e.Fps);
        BwText.Text = L.F("{0:0} KB/s, {1:0}% of the link", e.KBps, e.KBps * 1024 / FrameDiff.Bandwidth * 100);
        if (BlankSwitch.IsChecked != e.Blank) BlankSwitch.IsChecked = e.Blank;

        var s = App.Sensors.Current;
        var pawn = Sensors.PawnIoVersion;
        bool elevated = Sensors.Elevated || Sensors.Demo;
        double cpuT = s.Get("cpu.temp");
        int hash = HashCode.Combine(elevated, pawn, double.IsNaN(cpuT), App.Sensors.LhmStatus, double.IsNaN(s.Get("gpu.temp")), L.Lang);
        if (hash == lastStatusHash) return;
        lastStatusHash = hash;
        SensorStatus.Children.Clear();
        StatusRow(T("Administrator rights"), elevated, elevated ? T("yes") : T("no"));
        StatusRow(T("PawnIO driver"), pawn != null || Sensors.Demo, pawn != null ? pawn.ToString(3) : Sensors.Demo ? "2.2.0" : T("not installed"));
        StatusRow(T("CPU temperature"), !double.IsNaN(cpuT), double.IsNaN(cpuT) ? T("unavailable") : T("reading"));
        StatusRow(T("Graphics card"), !double.IsNaN(s.Get("gpu.temp")), s.Str("gpu.name") ?? T("none"));
        if (!elevated)
        {
            SensorStatus.Children.Add(new TextBlock { Text = T("CPU temperature needs administrator rights. Ember gets them on its own when it starts with Windows."), Style = (Style)FindResource("SubText"), Margin = new Thickness(0, 8, 0, 12) });
            SensorStatus.Children.Add(Btn(T("Restart as administrator"), null, true, RestartElevated));
        }
        else if (pawn == null && !Sensors.Demo)
        {
            SensorStatus.Children.Add(new TextBlock { Text = T("Ryzen and Core CPUs only report temperature through a driver. PawnIO is the signed replacement for WinRing0."), Style = (Style)FindResource("SubText"), Margin = new Thickness(0, 8, 0, 12) });
            SensorStatus.Children.Add(Btn(T("Install PawnIO"), null, true, () => _ = InstallPawnIo()));
        }
    }

    void StatusRow(string label, bool ok, string value)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), Background = ok ? OkBrush : BadBrush, VerticalAlignment = VerticalAlignment.Center });
        var l = new TextBlock { Text = label, Margin = new Thickness(11, 0, 0, 0) };
        Grid.SetColumn(l, 1);
        g.Children.Add(l);
        var v = new TextBlock { Text = value, Foreground = SubBrush, MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(v, 2);
        g.Children.Add(v);
        SensorStatus.Children.Add(g);
    }

    void RestartElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--elevated --after-restart") { UseShellExecute = true, Verb = "runas" });
            App.Quit();
        }
        catch (System.ComponentModel.Win32Exception) { Toast(T("Cancelled"), true); }
    }

    async Task InstallPawnIo()
    {
        Toast(T("Downloading the PawnIO installer…"));
        var result = await PawnIo.Install();
        App.Sensors.Reinit();
        lastStatusHash = 0;
        Toast(result switch
        {
            PawnIo.Result.Installed => T("PawnIO installed. Sensors restarted."),
            PawnIo.Result.NotFinished => T("PawnIO setup was closed before it finished."),
            PawnIo.Result.BadSignature => T("The download is not signed by the PawnIO author. Nothing was run."),
            _ => T("Could not download PawnIO. Check the connection and try again."),
        }, result != PawnIo.Result.Installed);
    }

    // ---------- gallery ----------

    sealed class Thumb
    {
        public required Theme Theme;
        public required Renderer R;
        public required SKBitmap Frame;
        public required WriteableBitmap Bmp;
        public required Border Bezel;
        public required TextBlock State;
    }

    readonly List<Thumb> thumbs = new();

    void BuildGallery()
    {
        DisposeGallery();
        Gallery.Children.Clear();
        App.AllThemes = Themes.LoadAll();
        var snap = SnapForPreview();
        foreach (var t in App.AllThemes)
        {
            var frame = new SKBitmap(W, H, SKColorType.Bgra8888, SKAlphaType.Premul);
            var r = new Renderer(t);
            using (var c = new SKCanvas(frame)) r.Render(c, snap, 0, Clock.Now);
            var bmp = new WriteableBitmap(W, H, 96, 96, PixelFormats.Pbgra32, null);
            bmp.WritePixels(new Int32Rect(0, 0, W, H), frame.GetPixels(), frame.ByteCount, frame.RowBytes);
            Gallery.Children.Add(ThemeCard(t, r, frame, bmp));
        }
        MarkActive();
    }

    static Snapshot SnapForPreview() => App.Sensors.Current.Seq > 1 ? App.Sensors.Current : Snapshot.Demo(Clock.Now);

    void TickGallery()
    {
        var snap = SnapForPreview();
        double now = Clock.Now;
        foreach (var th in thumbs)
        {
            using (var c = new SKCanvas(th.Frame)) th.R.Render(c, snap, 0.133, now);
            th.Bmp.WritePixels(new Int32Rect(0, 0, W, H), th.Frame.GetPixels(), th.Frame.ByteCount, th.Frame.RowBytes);
        }
    }

    void DisposeGallery()
    {
        foreach (var t in thumbs) { t.R.Dispose(); t.Frame.Dispose(); }
        thumbs.Clear();
    }

    FrameworkElement ThemeCard(Theme t, Renderer r, SKBitmap frame, WriteableBitmap bmp)
    {
        var img = new Image { Source = bmp, Width = 276, Height = 184, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        var bezel = new Border
        {
            Background = Br("#0C0B0A"), Padding = new Thickness(9), CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1.5), BorderBrush = LineBrush,
            Child = new Border { Background = Brushes.Black, Child = img }, Cursor = Cursors.Hand, ToolTip = T("Show on the screen"),
        };
        void Use() { App.SetTheme(t); MarkActive(); UpdateThemeCard(); Toast(L.F("{0} is on the screen", T(t.Name))); }
        bezel.MouseLeftButtonUp += (_, _) => Use();
        bezel.MouseEnter += (_, _) => { if (App.Engine.Theme.Id != t.Id) bezel.BorderBrush = (Brush)FindResource("Line2"); };
        bezel.MouseLeave += (_, _) => MarkActive();

        var state = new TextBlock { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        var head = new Grid { Margin = new Thickness(2, 14, 2, 0) };
        head.Children.Add(new TextBlock { Text = T(t.Name), Style = (Style)FindResource("H2") });
        head.Children.Add(state);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 10, 0, 0) };
        actions.Children.Add(Link(T("Edit"), () => OpenEditor(t)));
        actions.Children.Add(Link(T("Duplicate"), () => DuplicateTheme(t)));
        if (!t.BuiltIn) actions.Children.Add(Link(T("Delete"), () => _ = DeleteTheme(t)));

        var sp = new StackPanel { Width = 296, Margin = new Thickness(0, 0, 30, 34) };
        sp.Children.Add(bezel);
        sp.Children.Add(head);
        sp.Children.Add(new TextBlock { Text = t.Description is { } d ? T(d) : T("Your theme"), Style = (Style)FindResource("SubText"), Margin = new Thickness(2, 4, 2, 0), Height = 36, TextTrimming = TextTrimming.WordEllipsis });
        sp.Children.Add(actions);
        thumbs.Add(new Thumb { Theme = t, R = r, Frame = frame, Bmp = bmp, Bezel = bezel, State = state });
        return sp;
    }

    void MarkActive()
    {
        foreach (var th in thumbs)
        {
            bool active = th.Theme.Id == App.Engine.Theme.Id;
            th.Bezel.BorderBrush = active ? EmberBrush : LineBrush;
            th.State.Text = active ? T("On the screen") : th.Theme.BuiltIn ? "" : T("Yours");
            th.State.Foreground = active ? EmberBrush : DimBrush;
        }
    }

    void DuplicateTheme(Theme t)
    {
        var copy = t.Clone();
        copy.Name = L.F("{0} copy", T(t.Name));
        copy.Id = Themes.NewId(copy.Name, App.AllThemes);
        Themes.Save(copy);
        BuildGallery();
        Toast(L.F("Saved as {0}", copy.Name));
    }

    async Task DeleteTheme(Theme t)
    {
        if (!await Confirm(T("Delete this theme?"), L.F("{0} will be deleted from the themes folder.", t.Name), T("Delete"), T("Keep"))) return;
        Themes.Delete(t);
        if (App.Engine.Theme.Id == t.Id) App.SetTheme(Themes.LoadAll()[0]);
        BuildGallery();
        UpdateThemeCard();
    }

    void ImportTheme()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = T("Ember theme") + " (*.json)|*.json", Title = T("Import theme") };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var t = Theme.Parse(File.ReadAllText(dlg.FileName));
            t.Id = Themes.NewId(t.Name, App.AllThemes);
            Themes.Save(t);
            BuildGallery();
            Toast(L.F("Imported {0}", t.Name));
        }
        catch (Exception e) { Toast(T("Not an Ember theme: ") + e.Message, true); }
    }

    // ---------- sensors page ----------

    readonly List<(SensorInfo Info, TextBlock Value, FrameworkElement Row)> sensorRows = new();
    readonly List<(TextBlock Header, StackPanel Rows)> sensorGroups = new();

    void BuildSensors()
    {
        SensorList.Children.Clear();
        sensorRows.Clear();
        sensorGroups.Clear();
        var all = Sensors.Catalog.Concat(App.Sensors.Raw).ToList();
        foreach (var group in all.GroupBy(x => x.Group))
        {
            var header = new TextBlock { Text = T(group.Key), Style = (Style)FindResource("Head"), Margin = new Thickness(10, 22, 0, 6) };
            var rows = new StackPanel();
            foreach (var info in group)
            {
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
                var b = new Border { Padding = new Thickness(10, 7, 10, 7), Child = g, Background = Brushes.Transparent, Cursor = Cursors.Hand, BorderBrush = Br("#2F2C29"), BorderThickness = new Thickness(0, 0, 0, 1) };
                b.MouseEnter += (_, _) => b.Background = RaisedBrush;
                b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
                var key = info.Key;
                b.MouseLeftButtonUp += (_, _) => { Clipboard.SetText(key); Toast(L.F("Copied {0}", key)); };
                g.Children.Add(new TextBlock { Text = T(info.Label), TextTrimming = TextTrimming.CharacterEllipsis });
                var k = new TextBlock { Text = info.Key, FontSize = 12.5, Foreground = DimBrush, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(k, 1);
                g.Children.Add(k);
                var v = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right };
                Grid.SetColumn(v, 2);
                g.Children.Add(v);
                rows.Children.Add(b);
                sensorRows.Add((info, v, b));
            }
            SensorList.Children.Add(header);
            SensorList.Children.Add(rows);
            sensorGroups.Add((header, rows));
        }
        UpdateSensors();
        FilterSensors();
    }

    void UpdateSensors()
    {
        if (sensorRows.Count == 0) return;
        if (App.Sensors.Raw.Length + Sensors.Catalog.Length != sensorRows.Count) { BuildSensors(); return; }
        var s = App.Sensors.Current;
        var now = DateTime.Now;
        var cu = L.Culture;
        foreach (var (info, value, _) in sensorRows)
        {
            string text = info.Key switch
            {
                "time" => now.ToString("HH:mm", cu),
                "date" => now.ToString("dddd, d MMMM", cu),
                "time.sec" => now.Second.ToString(),
                "time.min" => now.Minute.ToString(),
                "time.hour" => (now.Hour % 12).ToString(),
                _ => s.Str(info.Key) ?? Fmt(s.Get(info.Key), info.Unit),
            };
            if (value.Text != text) value.Text = text;
        }
    }

    static string Fmt(double v, string unit)
    {
        if (double.IsNaN(v)) return "-";
        var cu = L.Culture;
        return unit switch
        {
            "B/s" => Renderer.Format(v, "rate"),
            "MHz" => v.ToString("0", cu) + " MHz",
            "%" or "°C" or "W" or "RPM" => v.ToString(v < 10 ? "0.0" : "0", cu) + " " + unit,
            _ => v.ToString(Math.Abs(v) < 10 ? "0.00" : "0.0", cu) + (unit.Length > 0 ? " " + unit : ""),
        };
    }

    void FilterSensors()
    {
        var q = SensorSearch.Text.Trim();
        foreach (var (info, _, row) in sensorRows)
            row.Visibility = q.Length == 0 || T(info.Label).Contains(q, StringComparison.OrdinalIgnoreCase) || info.Key.Contains(q, StringComparison.OrdinalIgnoreCase) || T(info.Group).Contains(q, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (header, rows) in sensorGroups)
            header.Visibility = rows.Visibility = rows.Children.Cast<UIElement>().Any(c => c.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- settings page ----------

    void Section(string title) => SettingsPanel.Children.Add(new TextBlock { Text = title, FontSize = 18, Margin = new Thickness(0, 30, 0, 10) });

    /// Label (and an optional line of explanation) on the left, the control on the right.
    void Row(string label, string? hint, FrameworkElement control)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 0), MinHeight = 46 };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 24, 8) };
        left.Children.Add(new TextBlock { Text = label });
        if (hint != null) left.Children.Add(new TextBlock { Text = hint, Style = (Style)FindResource("SubText"), FontSize = 12, Margin = new Thickness(0, 2, 0, 0) });
        g.Children.Add(left);
        control.VerticalAlignment = VerticalAlignment.Center;
        control.Margin = new Thickness(0);
        if (control is ComboBox) control.Width = 280;
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        SettingsPanel.Children.Add(new Border { Child = g, BorderBrush = Br("#2F2C29"), BorderThickness = new Thickness(0, 0, 0, 1) });
    }

    static CheckBox Toggle(bool value, Action<bool> changed, Style style)
    {
        var cb = new CheckBox { Style = style, IsChecked = value };
        cb.Checked += (_, _) => changed(true);
        cb.Unchecked += (_, _) => changed(false);
        return cb;
    }

    void BuildSettings()
    {
        SettingsPanel.Children.Clear();
        var cfg = App.Cfg;
        var sw = (Style)FindResource("Switch");

        Section(T("Screen"));
        var ports = new List<(string, string)> { ("auto", T("Automatic")) };
        var found = TuringLcd.FindPorts();
        foreach (var p in TuringLcd.SerialPortNames()) ports.Add((p, found.Contains(p) ? L.F("{0} (Turing screen)", p) : p));
        Row(T("Port"), null, Combo(ports, cfg.Port, v => { cfg.Port = v; cfg.Save(); App.Engine.Reconnect(); }));
        Row(T("Frame rate limit"), T("Small animations reach this. Large changes take longer, the USB link moves about 157 KB/s."),
            Chips(new[] { (15, "15"), (24, "24"), (30, "30"), (45, "45"), (60, "60") }, cfg.Fps, v => { cfg.Fps = v; cfg.Save(); }));
        Row(T("Intro when Ember starts"), T("Logo, version and link on the screen for a few seconds."), Toggle(cfg.Intro, v => { cfg.Intro = v; cfg.Save(); }, sw));
        Row(T("Connection"), null, new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { Btn(T("Redraw"), null, false, () => { App.Engine.Redraw(); Toast(T("Redrawing the screen")); }), Gap(), Btn(T("Reconnect"), null, false, () => App.Engine.Reconnect()) },
        });

        Section(T("Sensors"));
        Row(T("Refresh"), null, Chips(new[] { (500, L.F("{0} s", 0.5)), (1000, "1 s"), (2000, "2 s") }, cfg.Interval, v => { cfg.Interval = v; cfg.Save(); }));
        var gpus = new List<(string, string)> { ("auto", T("Automatic (dedicated card)")) };
        foreach (var (id, name) in Sensors.GpuChoices(App.Sensors.Current)) gpus.Add((id, name));
        Row(T("Graphics card"), null, Combo(gpus, cfg.Gpu, v => { cfg.Gpu = v; cfg.Save(); }));
        var nets = new List<(string, string)> { ("auto", T("Automatic (busiest)")) };
        foreach (var n in Sensors.NetworkNames()) nets.Add((n, n));
        Row(T("Network adapter"), null, Combo(nets, cfg.Net, v => { cfg.Net = v; cfg.Save(); }));
        Row(T("Motherboard sensors"), T("Fans and temperatures from the Super I/O chip. Experimental, leave off unless you need them."),
            Toggle(cfg.Motherboard, v => { cfg.Motherboard = v; cfg.Save(); App.Sensors.Reinit(); }, sw));

        Section(T("Startup"));
        bool elevated = Sensors.Elevated;
        var autostart = Toggle(elevated && Autostart.Exists(), v =>
        {
            bool ok = v ? Autostart.Enable() : Autostart.Disable();
            if (ok) { App.Cfg.Autostart = v; App.Cfg.Save(); }
            Toast(ok ? (v ? T("Ember will start with Windows") : T("Ember will not start with Windows")) : T("Could not change the startup task"), !ok);
        }, sw);
        autostart.IsEnabled = elevated;
        Row(T("Start with Windows"), elevated ? T("Runs from Task Scheduler with sensor access, no UAC prompt.") : T("Needs Ember running as administrator."), autostart);
        if (!elevated) Row(T("Administrator rights"), null, Btn(T("Restart as administrator"), null, false, RestartElevated));

        Section(T("Language"));
        Row(T("Interface and built-in themes"), null, Chips(new[] { ("en", "English"), ("pl", "Polski") }, L.Lang, v =>
        {
            cfg.Language = v;
            cfg.Save();
            App.ApplyLanguage();
        }));

        Section(T("About"));
        var version = typeof(App).Assembly.GetName().Version!.ToString(3);
        Row(L.F("Ember {0}", version), T("For the Turing Smart Screen 3.5\" (revision A)."), new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { Btn("GitHub", null, false, () => App.OpenUrl(App.RepoUrl)), Gap(), Btn(T("Log"), null, false, () => App.OpenFolder(App.DataDir)), Gap(), Btn(T("Quit Ember"), null, false, App.Quit) },
        });
    }
}
