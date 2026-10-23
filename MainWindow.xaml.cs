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
    static readonly Brush TextBrush = Br("#E8ECF2"), SubBrush = Br("#8A94A6"), DimBrush = Br("#5E697C"), OkBrush = Br("#3DDC84"), BadBrush = Br("#FF6B5B"),
        EmberBrush = Br("#FFB347"), LineBrush = Br("#1C2330"), RaisedBrush = Br("#18202B"), RowLine = Br("#1A212C"), HoverFill = Br("#18202B");

    readonly DispatcherTimer timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    readonly WriteableBitmap screenBmp = new(W, H, 96, 96, PixelFormats.Pbgra32, null), miniBmp = new(W, H, 96, 96, PixelFormats.Pbgra32, null);
    readonly byte[] screenBuf = new byte[W * H * 4];
    int previewSeen = -1, tick;
    string page = "screen";
    readonly Grid toastHost = new() { IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 34) };
    readonly Grid modalHost = new() { Visibility = Visibility.Collapsed, Background = Br("#B0050709") };

    static string T(string en) => L.T(en);

    public MainWindow()
    {
        InitializeComponent();
        var root = (Grid)Root.Child;
        root.Children.Add(toastHost);
        Grid.SetRowSpan(toastHost, 2);
        root.Children.Add(modalHost);
        Grid.SetRowSpan(modalHost, 2);
        using (var logo = Icons.Logo(76)) Logo.Source = ToImage(logo);
        using (var icon = Icons.Logo(64)) Icon = ToImage(icon);
        ScreenImage.Source = screenBmp;
        MiniScreen.Source = miniBmp;
        MiniPrev.Click += (_, _) => StepTheme(-1);
        MiniNext.Click += (_, _) => StepTheme(1);
        MiniCard.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not TextBlock { FontFamily.Source: var f } || !f.Contains("Icons")) NavScreen.IsChecked = true; };
        CreditLine.Text = T("made by");
        SideToggle.Click += (_, _) => { pinned = !collapsed; SetSidebar(true); };
        SizeChanged += (_, _) => SetSidebar(true);
        FitToScreen();
        CreditBtn.Click += (_, _) => App.OpenUrl(App.RepoUrl);

        foreach (var (tab, name) in new[] { (NavScreen, "screen"), (NavThemes, "themes"), (NavEditor, "editor"), (NavSensors, "sensors"), (NavSettings, "settings") })
            tab.Checked += (_, _) => { Show(name); MovePill(tab, true); };
        MinBtn.Click += (_, _) => WindowState = WindowState.Minimized;
        MaxBtn.Click += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        CloseBtn.Click += (_, _) => Close();
        StateChanged += (_, _) =>
        {
            Root.Margin = new Thickness(WindowState == WindowState.Maximized ? 7 : 0);
            // Minimised: nothing is visible, so the UI clock (preview, live thumbnails) and the engine's preview copies stop.
            bool min = WindowState == WindowState.Minimized;
            if (min) timer.Stop(); else timer.Start();
            App.Engine.PreviewWanted = !min;
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
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        var st = (ScaleTransform)Root.RenderTransform;
        st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.98, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
        st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.98, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
        SetSidebar(false);
        MovePill(NavPanel.Children.OfType<RadioButton>().First(r => r.IsChecked == true), false);
        timer.Start();
    }

    bool collapsed;
    bool? pinned;

    /// Icon rail or full sidebar: whatever the person chose last, otherwise narrow windows and the editor get the rail.
    void SetSidebar(bool animate)
    {
        bool want = pinned ?? (page == "editor" || ActualWidth is > 0 and < 1240);
        if (want == collapsed && SideBar.Width is 76 or 256) return;
        collapsed = want;
        var d = TimeSpan.FromMilliseconds(animate ? 240 : 0);
        SideBar.BeginAnimation(WidthProperty, new DoubleAnimation(collapsed ? 76 : 256, d) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
        MiniCard.Visibility = CreditIcon.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SideToggle.Content = collapsed ? "" : "";
        SideToggle.ToolTip = collapsed ? T("Expand the menu") : T("Collapse the menu");
        foreach (var tab in NavPanel.Children.OfType<RadioButton>()) tab.ToolTip = collapsed ? tab.Content : null;
    }

    /// The default size suits a big monitor; on a smaller work area take most of it instead of spilling over.
    void FitToScreen()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Round(area.Width * 0.92));
        Height = Math.Min(Height, Math.Round(area.Height * 0.9));
    }

    /// Slides the highlight in the sidebar to `tab`.
    void MovePill(RadioButton tab, bool animate)
    {
        if (!tab.IsLoaded) return;
        double y = tab.TranslatePoint(new Point(0, 0), NavPanel).Y;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        NavPillY.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, TimeSpan.FromMilliseconds(animate ? 260 : 0)) { EasingFunction = ease });
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
        var shown = pages[name];
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        shown.RenderTransform = new TranslateTransform(0, 10);
        shown.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        shown.RenderTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        TopActions.Children.Clear();
        App.Engine.PreviewWanted = true;
        PageTitle.Text = name switch { "themes" => T("Themes"), "editor" => T("Editor"), "sensors" => T("Sensors"), "settings" => T("Settings"), _ => T("Screen") };
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
        if (IsLoaded) SetSidebar(true);
    }

    void Tick()
    {
        tick++;
        switch (page)
        {
            case "screen":
                if (App.Engine.CopyPreview(screenBuf, ref previewSeen))
                    screenBmp.WritePixels(new Int32Rect(0, 0, W, H), screenBuf, W * 4, 0);
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
        // The sidebar thumbnail has its own bitmap at ~4 fps, so it is not redrawn with every preview frame.
        if (tick % 8 == 0 && (page == "screen" || App.Engine.CopyPreview(screenBuf, ref previewSeen)))
            miniBmp.WritePixels(new Int32Rect(0, 0, W, H), screenBuf, W * 4, 0);
        if (tick % 10 == 0) UpdateScreenStatus();
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
        return new Border { Child = wp, Background = Br("#0AFFFFFF"), BorderBrush = (Brush)FindResource("Line2"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
    }

    public TextBlock Caption(string text, double top = 18) => new() { Text = text, Style = (Style)FindResource("Head"), Margin = new Thickness(0, top, 0, 8) };

    public void Toast(string text, bool bad = false)
    {
        var b = new Border
        {
            Background = Br("#1A212D"), BorderBrush = bad ? BadBrush : Br("#3A2A18"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 11, 18, 11),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = bad ? "\uE7BA" : "\uE73E", FontFamily = (FontFamily)FindResource("Ico"), Foreground = bad ? BadBrush : EmberBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 11, 0) },
                    new TextBlock { Text = text, Foreground = TextBrush, VerticalAlignment = VerticalAlignment.Center },
                },
            },
            Opacity = 0, RenderTransform = new TranslateTransform(0, 18),
        };
        toastHost.Children.Clear();
        toastHost.Children.Add(b);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220)), ease));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2600))));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3000))));
        b.BeginAnimation(OpacityProperty, fade);
        b.RenderTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(380)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 } });
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
            Style = (Style)FindResource("CardBox"), BorderBrush = Br("#2C3646"),
            Width = 440, Padding = new Thickness(26, 22, 26, 22), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel { Children = { new TextBlock { Text = title, Style = (Style)FindResource("H2"), FontSize = 18 }, new TextBlock { Text = text, Style = (Style)FindResource("SubText"), Margin = new Thickness(0, 10, 0, 0), FontSize = 13.5 }, buttons } },
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
        ThemeInfo.RenderTransform = new TranslateTransform(d * 12, 0);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ThemeInfo.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        ThemeInfo.RenderTransform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(d * 12, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
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
        MiniName.Text = T(App.Engine.Theme.Name);
        MiniStatus.Text = e.Connected ? L.F("{0:0} fps on {1}", e.Fps, e.Port) : e.Status;
        FpsText.Text = L.F("{0:0} fps", e.Fps);
        BwText.Text = string.Format(L.Culture, "{0:0} KB/s ({1:0}%)", e.KBps, e.KBps * 1024 / FrameDiff.Bandwidth * 100);
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
        var g = new Grid { Margin = new Thickness(0, 0, 0, 11) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = ok ? OkBrush : BadBrush, VerticalAlignment = VerticalAlignment.Center });
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
        public required Border State;
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
        var img = new Image { Source = bmp, Width = 270, Height = 180, Stretch = Stretch.Fill, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1) };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        var shot = new Border { CornerRadius = new CornerRadius(12), Child = img, Background = Brushes.Black, Cursor = Cursors.Hand, ToolTip = T("Show on the screen") };
        shot.Clip = new RectangleGeometry(new Rect(0, 0, 270, 180), 12, 12);

        var state = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(9, 3, 9, 4), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var head = new Grid { Margin = new Thickness(4, 14, 2, 0) };
        head.Children.Add(new TextBlock { Text = T(t.Name), Style = (Style)FindResource("H2") });
        head.Children.Add(state);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        actions.Children.Add(Btn(T("Edit"), "\uE70F", false, () => OpenEditor(t)));
        actions.Children.Add(Gap());
        actions.Children.Add(Btn("", "\uE8C8", false, () => DuplicateTheme(t), T("Duplicate")));
        if (!t.BuiltIn)
        {
            actions.Children.Add(Gap());
            actions.Children.Add(Btn("", "\uE74D", false, () => _ = DeleteTheme(t), T("Delete")));
        }

        var sp = new StackPanel();
        sp.Children.Add(shot);
        sp.Children.Add(head);
        sp.Children.Add(new TextBlock { Text = t.Description is { } d ? T(d) : T("Your theme"), Style = (Style)FindResource("SubText"), Margin = new Thickness(4, 5, 4, 0), Height = 40, TextTrimming = TextTrimming.WordEllipsis });
        sp.Children.Add(actions);
        var card = new Border
        {
            Style = (Style)FindResource("CardBox"), Padding = new Thickness(13), Margin = new Thickness(0, 0, 18, 18), Width = 298, Child = sp, BorderThickness = new Thickness(1.5),
            RenderTransform = new TranslateTransform(), Tag = t.Id,
        };
        void Use() { App.SetTheme(t); MarkActive(); UpdateThemeCard(); Toast(L.F("{0} is on the screen", T(t.Name))); }
        shot.MouseLeftButtonUp += (_, _) => Use();
        head.MouseLeftButtonUp += (_, _) => Use();
        card.MouseEnter += (_, _) => { Hover(card, img, true); if (App.Engine.Theme.Id != t.Id) card.BorderBrush = Br("#3A4658"); };
        card.MouseLeave += (_, _) => { Hover(card, img, false); MarkActive(); };
        thumbs.Add(new Thumb { Theme = t, R = r, Frame = frame, Bmp = bmp, Bezel = card, State = state });
        return card;
    }

    static void Hover(Border card, Image img, bool on)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var d = TimeSpan.FromMilliseconds(on ? 220 : 320);
        card.RenderTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(on ? -3 : 0, d) { EasingFunction = ease });
        var st = (ScaleTransform)img.RenderTransform;
        st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(on ? 1.03 : 1, d) { EasingFunction = ease });
        st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(on ? 1.03 : 1, d) { EasingFunction = ease });
    }

    void MarkActive()
    {
        foreach (var th in thumbs)
        {
            bool active = th.Theme.Id == App.Engine.Theme.Id;
            th.Bezel.BorderBrush = active ? Br("#FF9A3D") : LineBrush;
            th.Bezel.Background = active ? Br("#16140F") : (Brush)FindResource("Card");
            var badge = (Border)th.State;
            badge.Background = active ? (Brush)FindResource("Ember") : th.Theme.BuiltIn ? Brushes.Transparent : Br("#14FFFFFF");
            badge.Child = new TextBlock { Text = active ? T("On the screen") : th.Theme.BuiltIn ? "" : T("Yours"), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = active ? Br("#1A1208") : SubBrush };
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
    readonly List<(Border Panel, StackPanel Rows)> sensorGroups = new();

    void BuildSensors()
    {
        SensorList.Children.Clear();
        sensorRows.Clear();
        sensorGroups.Clear();
        var all = Sensors.Catalog.Concat(App.Sensors.Raw).ToList();
        foreach (var group in all.GroupBy(x => x.Group))
        {
            var header = new TextBlock { Text = T(group.Key), Style = (Style)FindResource("H2"), Margin = new Thickness(12, 0, 0, 8) };
            var rows = new StackPanel();
            foreach (var info in group)
            {
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
                var b = new Border { Padding = new Thickness(12, 8, 12, 8), Child = g, Background = Brushes.Transparent, Cursor = Cursors.Hand, CornerRadius = new CornerRadius(10) };
                b.MouseEnter += (_, _) => b.Background = HoverFill;
                b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
                var key = info.Key;
                b.MouseLeftButtonUp += (_, _) => { Clipboard.SetText(key); Toast(L.F("Copied {0}", key)); };
                System.Windows.Documents.Typography.SetNumeralAlignment(g, FontNumeralAlignment.Tabular);
                g.Children.Add(new TextBlock { Text = T(info.Label), TextTrimming = TextTrimming.CharacterEllipsis });
                var k = new TextBlock { Text = info.Key, FontSize = 12.5, Foreground = DimBrush, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(k, 1);
                g.Children.Add(k);
                var v = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right, FontWeight = FontWeights.SemiBold, Foreground = EmberBrush };
                Grid.SetColumn(v, 2);
                g.Children.Add(v);
                rows.Children.Add(b);
                sensorRows.Add((info, v, b));
            }
            var panel = new Border { Style = (Style)FindResource("CardBox"), Padding = new Thickness(10, 16, 10, 10), Margin = new Thickness(0, 0, 0, 14), Child = new StackPanel { Children = { header, rows } } };
            SensorList.Children.Add(panel);
            sensorGroups.Add((panel, rows));
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
        foreach (var (panel, rows) in sensorGroups)
            panel.Visibility = rows.Children.Cast<UIElement>().Any(c => c.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- settings page ----------

    StackPanel? section;

    void Section(string title)
    {
        section = new StackPanel();
        var panel = new Border { Style = (Style)FindResource("CardBox"), Padding = new Thickness(24, 20, 24, 8), Margin = new Thickness(0, 0, 0, 14), Child = new StackPanel { Children = { new TextBlock { Text = title, Style = (Style)FindResource("H2"), Margin = new Thickness(0, 0, 0, 6) }, section } } };
        SettingsPanel.Children.Add(panel);
    }

    /// Label (and an optional line of explanation) on the left, the control on the right.
    void Row(string label, string? hint, FrameworkElement control)
    {
        var g = new Grid { MinHeight = 52 };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 10, 24, 10) };
        left.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.Medium });
        if (hint != null) left.Children.Add(new TextBlock { Text = hint, Style = (Style)FindResource("SubText"), FontSize = 12, Margin = new Thickness(0, 2, 0, 0) });
        g.Children.Add(left);
        control.VerticalAlignment = VerticalAlignment.Center;
        control.Margin = new Thickness(0);
        if (control is ComboBox) control.Width = 300;
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        section!.Children.Add(new Border { Child = g, BorderBrush = RowLine, BorderThickness = new Thickness(0, section.Children.Count == 0 ? 0 : 1, 0, 0) });
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
