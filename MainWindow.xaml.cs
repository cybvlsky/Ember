using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
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
    static readonly Brush TextBrush = Br("#E8ECF2"), SubBrush = Br("#8A94A6"), DimBrush = Br("#5E697C"), OkBrush = Br("#3DDC84"), BadBrush = Br("#FF6B5B"), AmberBrush = Br("#FFB347");

    readonly DispatcherTimer timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    readonly WriteableBitmap screenBmp = new(W, H, 96, 96, PixelFormats.Pbgra32, null);
    readonly byte[] screenBuf = new byte[W * H * 4];
    int previewSeen = -1, tick;
    string page = "screen";
    readonly Grid toastHost = new() { IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 26) };
    readonly Grid modalHost = new() { Visibility = Visibility.Collapsed, Background = Br("#B0050709") };

    public MainWindow()
    {
        InitializeComponent();
        ((Grid)Root.Child).Children.Add(toastHost);
        Grid.SetColumnSpan(toastHost, 2);
        ((Grid)Root.Child).Children.Add(modalHost);
        Grid.SetColumnSpan(modalHost, 2);
        using (var logo = Icons.Logo(96, true)) Logo.Source = ToImage(logo);
        Icon = Logo.Source;
        VersionText.Text = "v" + typeof(App).Assembly.GetName().Version!.ToString(2);
        ScreenImage.Source = screenBmp;

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
        SensorSearch.TextChanged += (_, _) => FilterSensors();
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
        if (await Confirm("Niezapisane zmiany", "Motyw w edytorze ma niezapisane zmiany. Zamknąć okno i je porzucić?", "Porzuć", "Wróć do edytora"))
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
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        var st = (ScaleTransform)Root.RenderTransform;
        st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.985, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.985, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        timer.Start();
    }

    [DllImport("dwmapi")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

    // ---------- shell ----------

    void Show(string name)
    {
        if (page == "editor" && name != "editor") StopLiveEdit();
        page = name;
        var pages = new Dictionary<string, FrameworkElement> { ["screen"] = PageScreen, ["themes"] = PageThemes, ["editor"] = PageEditor, ["sensors"] = PageSensors, ["settings"] = PageSettings };
        foreach (var (k, el) in pages) el.Visibility = k == name ? Visibility.Visible : Visibility.Collapsed;
        var shown = pages[name];
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        shown.RenderTransform = new TranslateTransform(0, 10);
        shown.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        shown.RenderTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        TopActions.Children.Clear();
        App.Engine.PreviewWanted = name == "screen";
        switch (name)
        {
            case "screen":
                PageTitle.Text = "Ekran";
                PageSub.Text = "Tak teraz wygląda Twój ekranik. Zmiany widać na żywo.";
                break;
            case "themes":
                PageTitle.Text = "Motywy";
                PageSub.Text = "Gotowe układy na Twoich prawdziwych danych. Kliknij, żeby ustawić.";
                TopActions.Children.Add(Btn("Nowy motyw", "", true, () => NewTheme()));
                TopActions.Children.Add(Gap());
                TopActions.Children.Add(Btn("Importuj", "", false, ImportTheme));
                TopActions.Children.Add(Gap());
                TopActions.Children.Add(Btn("Folder", "", false, () => App.OpenFolder(Themes.UserDir)));
                BuildGallery();
                break;
            case "editor":
                PageTitle.Text = "Edytor";
                ShowEditorActions();
                break;
            case "sensors":
                PageTitle.Text = "Czujniki";
                PageSub.Text = "Wszystko, co Ember odczytuje z komputera. Wartości odświeżają się co sekundę.";
                BuildSensors();
                break;
            case "settings":
                PageTitle.Text = "Ustawienia";
                PageSub.Text = "Połączenie, czujniki i start z systemem.";
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
                    screenBmp.WritePixels(new Int32Rect(0, 0, W, H), screenBuf, W * 4, 0);
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
        if (tick % 15 == 0) StatusDot.Fill = App.Engine.Connected ? OkBrush : DimBrush;
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (page == "editor") EditorKey(e);
    }

    /// `--screenshots <dir>`: a PNG of every page, so the UI can be checked without looking at the monitor.
    public async Task Screenshots(string dir)
    {
        Directory.CreateDirectory(dir);
        async Task Shot(string name)
        {
            await Task.Delay(900);
            var rtb = new RenderTargetBitmap((int)Root.ActualWidth, (int)Root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(Root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var f = File.Create(Path.Combine(dir, name + ".png"));
            enc.Save(f);
        }
        await Shot("1-screen");
        NavThemes.IsChecked = true; await Shot("2-themes");
        NavEditor.IsChecked = true; await Shot("3-editor");
        if (draft != null && draft.Widgets.Count > 8) { Select(Math.Min(8, draft.Widgets.Count - 1)); await Shot("4-editor-selected"); }
        NavSensors.IsChecked = true; await Shot("5-sensors");
        NavSettings.IsChecked = true; await Shot("6-settings");
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
        await shot("7-editor-gauge");
        Select(draft.Widgets.FindIndex(w => w.Type == "graph"));
        MoveLayer(-1); MoveLayer(1); Duplicate(); DeleteSel();
        Select(-1);
        TickEditor();
        var (dx, guide) = SnapMove(10, 100, 225 - 10 - 50 + 1.5, false);
        Log.Info($"uitest snap: dx={dx} guide={guide}");
        for (int i = 0; i < 40; i++) Undo();
        for (int i = 0; i < 5; i++) Redo();
        TickEditor();
        draft.Name = "Test UI Ember";
        Save();
        var saved = App.AllThemes.FirstOrDefault(t => t.Id == draft.Id && !t.BuiltIn);
        Log.Info($"uitest saved={(saved != null)} widgets={saved?.Widgets.Count} undo={undo.Count}");
        if (saved != null) Themes.Delete(saved);
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
        b.Click += (_, _) => click();
        return b;
    }

    public CheckBox Switch(string title, string? sub, bool value, Action<bool> changed)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = title });
        if (sub != null) sp.Children.Add(new TextBlock { Text = sub, Style = (Style)FindResource("SubText"), FontSize = 11.5 });
        var cb = new CheckBox { Style = (Style)FindResource("Switch"), Content = sp, IsChecked = value, Margin = new Thickness(0, 0, 0, 12) };
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

    public WrapPanel Chips<T>(IEnumerable<(T Value, string Label)> items, T selected, Action<T> changed)
    {
        var wp = new WrapPanel();
        string group = Guid.NewGuid().ToString();
        foreach (var (v, l) in items)
        {
            var rb = new RadioButton { Content = l, GroupName = group, Style = (Style)FindResource("Chip"), IsChecked = EqualityComparer<T>.Default.Equals(v, selected) };
            var val = v;
            rb.Checked += (_, _) => changed(val);
            wp.Children.Add(rb);
        }
        return wp;
    }

    public TextBlock Caption(string text, double top = 14) => new() { Text = text, Style = (Style)FindResource("Cap"), Margin = new Thickness(0, top, 0, 8) };

    Border Card(string title, double width, params UIElement[] children)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("H2"), Margin = new Thickness(0, 0, 0, 14) });
        foreach (var c in children) sp.Children.Add(c);
        return new Border { Style = (Style)FindResource("CardBorder"), Width = width, Margin = new Thickness(0, 0, 16, 16), Padding = new Thickness(22, 18, 22, 10), Child = sp };
    }

    public void Toast(string text, bool bad = false)
    {
        var b = new Border
        {
            Background = Br("#1A212D"), BorderBrush = bad ? BadBrush : Br("#3A2A18"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 10, 16, 10), Margin = new Thickness(0, 0, 0, 0),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = bad ? "" : "", FontFamily = (FontFamily)FindResource("Ico"), Foreground = bad ? BadBrush : AmberBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) },
                    new TextBlock { Text = text, Foreground = TextBrush },
                },
            },
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Opacity = 0.5 },
            Opacity = 0,
        };
        toastHost.Children.Clear();
        toastHost.Children.Add(b);
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180))));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2400))));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2800))));
        b.BeginAnimation(OpacityProperty, anim);
    }

    /// In-window confirm dialog (a MessageBox would break the look).
    public Task<bool> Confirm(string title, string text, string yes, string no)
    {
        var tcs = new TaskCompletionSource<bool>();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        void Done(bool v) { modalHost.Visibility = Visibility.Collapsed; modalHost.Children.Clear(); tcs.TrySetResult(v); }
        buttons.Children.Add(Btn(no, null, false, () => Done(false)));
        buttons.Children.Add(Gap(10));
        buttons.Children.Add(Btn(yes, null, true, () => Done(true)));
        var box = new Border
        {
            Style = (Style)FindResource("CardBorder"), Width = 440, Padding = new Thickness(26, 22, 26, 22), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel { Children = { new TextBlock { Text = title, Style = (Style)FindResource("H2") }, new TextBlock { Text = text, Style = (Style)FindResource("SubText"), Margin = new Thickness(0, 10, 0, 0), FontSize = 13 }, buttons } },
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 50, ShadowDepth = 10, Opacity = 0.6 },
        };
        modalHost.Children.Clear();
        modalHost.Children.Add(box);
        modalHost.Visibility = Visibility.Visible;
        modalHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        return tcs.Task;
    }

    // ---------- screen page ----------

    void InitScreenPage()
    {
        BrightSlider.Value = App.Cfg.Brightness;
        BrightText.Text = $"{App.Cfg.Brightness} %";
        BrightSlider.ValueChanged += (_, e) =>
        {
            App.Cfg.Brightness = (int)Math.Round(e.NewValue);
            BrightText.Text = $"{App.Cfg.Brightness} %";
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
        ThemeName.Text = App.Engine.Theme.Name;
        ThemeDesc.Text = App.Engine.Theme.Description ?? "";
    }

    int lastStatusHash;

    void UpdateScreenStatus()
    {
        var e = App.Engine;
        ConnText.Text = e.Status;
        ConnDot.Fill = e.Connected ? OkBrush : e.Status.StartsWith("Szukam") ? DimBrush : BadBrush;
        FpsText.Text = $"{e.Fps:0} kl./s";
        BwText.Text = $"{e.KBps:0} KB/s · {e.KBps * 1024 / FrameDiff.Bandwidth * 100:0}%";
        if (BlankSwitch.IsChecked != e.Blank) BlankSwitch.IsChecked = e.Blank;

        var s = App.Sensors.Current;
        var pawn = Sensors.PawnIoVersion;
        double cpuT = s.Get("cpu.temp");
        int hash = HashCode.Combine(Sensors.Elevated, pawn, double.IsNaN(cpuT), App.Sensors.LhmStatus, double.IsNaN(s.Get("gpu.temp")));
        if (hash == lastStatusHash) return;
        lastStatusHash = hash;
        SensorStatus.Children.Clear();
        StatusRow("Uprawnienia administratora", Sensors.Elevated, Sensors.Elevated ? "są" : "brak");
        StatusRow("Sterownik PawnIO", pawn != null, pawn != null ? $"v{pawn}" : "nie zainstalowany");
        StatusRow("Temperatura CPU", !double.IsNaN(cpuT), double.IsNaN(cpuT) ? "niedostępna" : "odczyt działa");
        StatusRow("Grafika", !double.IsNaN(s.Get("gpu.temp")), s.Str("gpu.name") ?? "brak");
        if (!Sensors.Elevated)
        {
            SensorStatus.Children.Add(new TextBlock { Text = "Temperatura procesora wymaga uprawnień administratora. Start z systemem robi to bez pytania.", Style = (Style)FindResource("SubText"), Margin = new Thickness(0, 6, 0, 10) });
            SensorStatus.Children.Add(Btn("Uruchom jako administrator", "", true, RestartElevated));
        }
        else if (pawn == null)
        {
            SensorStatus.Children.Add(new TextBlock { Text = "Ryzen podaje temperaturę tylko przez sterownik. PawnIO to podpisany, bezpieczny następca starego WinRing0 (tego od bluescreenów).", Style = (Style)FindResource("SubText"), Margin = new Thickness(0, 6, 0, 10) });
            SensorStatus.Children.Add(Btn("Zainstaluj PawnIO", "", true, () => _ = InstallPawnIo()));
        }
    }

    void StatusRow(string label, bool ok, string value)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new Ellipse2(ok ? OkBrush : BadBrush));
        var l = new TextBlock { Text = label, Margin = new Thickness(10, 0, 0, 0), FontSize = 13 };
        Grid.SetColumn(l, 1);
        g.Children.Add(l);
        var v = new TextBlock { Text = value, Foreground = SubBrush, FontSize = 12.5, MaxWidth = 140, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(v, 2);
        g.Children.Add(v);
        SensorStatus.Children.Add(g);
    }

    sealed class Ellipse2 : Border
    {
        public Ellipse2(Brush b) { Width = 8; Height = 8; CornerRadius = new CornerRadius(4); Background = b; VerticalAlignment = VerticalAlignment.Center; }
    }

    void RestartElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--elevated --after-restart") { UseShellExecute = true, Verb = "runas" });
            App.Quit();
        }
        catch (System.ComponentModel.Win32Exception) { Toast("Anulowano", true); }
    }

    async Task InstallPawnIo()
    {
        const string url = "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe";
        var dir = Path.Combine(Path.GetTempPath(), "Ember");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "PawnIO_setup.exe");
        try
        {
            Toast("Pobieram oficjalny instalator PawnIO…");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Ember/1.0");
                await File.WriteAllBytesAsync(file, await http.GetByteArrayAsync(url));
            }
            var signer = await Task.Run(() => Signature.Signer(file));
            if (signer == null)
            {
                Toast("Instalator nie ma ważnego podpisu cyfrowego. Przerwano.", true);
                Log.Warn("PawnIO setup signature invalid");
                return;
            }
            Log.Info($"PawnIO setup signed by {signer}");
            var p = Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
            if (p != null) await p.WaitForExitAsync();
            App.Sensors.Reinit();
            lastStatusHash = 0;
            Toast(Sensors.PawnIoVersion != null ? "PawnIO zainstalowany. Czujniki uruchomione ponownie." : "Instalacja nie została dokończona.", Sensors.PawnIoVersion == null);
        }
        catch (Exception e)
        {
            Log.Error("pawnio install", e);
            Toast("Nie udało się: " + e.Message, true);
        }
    }

    // ---------- gallery ----------

    sealed class Thumb
    {
        public required Theme Theme;
        public required Renderer R;
        public required SKBitmap Frame;
        public required WriteableBitmap Bmp;
        public required Border Card;
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
            var card = ThemeCard(t, bmp);
            thumbs.Add(new Thumb { Theme = t, R = r, Frame = frame, Bmp = bmp, Card = card });
            Gallery.Children.Add(card);
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

    Border ThemeCard(Theme t, WriteableBitmap bmp)
    {
        var img = new Image { Source = bmp, Width = 264, Height = 176, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        var imgBox = new Border { CornerRadius = new CornerRadius(10), Child = img, ClipToBounds = true, Background = Brushes.Black };
        imgBox.Clip = new RectangleGeometry(new Rect(0, 0, 264, 176), 10, 10);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        actions.Children.Add(Btn("Edytuj", "", false, () => OpenEditor(t)));
        actions.Children.Add(Gap());
        actions.Children.Add(Btn("", "", false, () => DuplicateTheme(t), "Duplikuj"));
        if (!t.BuiltIn)
        {
            actions.Children.Add(Gap());
            actions.Children.Add(Btn("", "", false, () => _ = DeleteTheme(t), "Usuń"));
        }
        var badge = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(8, 3, 8, 3), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Tag = "badge" };
        var head = new Grid { Margin = new Thickness(2, 14, 0, 0) };
        head.Children.Add(new TextBlock { Text = t.Name, Style = (Style)FindResource("H2") });
        head.Children.Add(badge);
        var sp = new StackPanel();
        sp.Children.Add(imgBox);
        sp.Children.Add(head);
        sp.Children.Add(new TextBlock { Text = t.Description ?? (t.BuiltIn ? "" : "Twój motyw"), Style = (Style)FindResource("SubText"), Margin = new Thickness(2, 5, 0, 0), Height = 34, TextTrimming = TextTrimming.WordEllipsis });
        sp.Children.Add(actions);
        var card = new Border
        {
            Style = (Style)FindResource("CardBorder"), Padding = new Thickness(14), Margin = new Thickness(0, 0, 18, 18), Width = 294, Child = sp, Cursor = Cursors.Hand,
            BorderThickness = new Thickness(1.5), Tag = t.Id, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1),
        };
        card.MouseEnter += (_, _) => Lift(card, 1.015);
        card.MouseLeave += (_, _) => Lift(card, 1);
        imgBox.MouseLeftButtonUp += (_, _) => { App.SetTheme(t); MarkActive(); UpdateThemeCard(); Toast($"Ustawiono motyw {t.Name}"); };
        head.MouseLeftButtonUp += (_, _) => { App.SetTheme(t); MarkActive(); UpdateThemeCard(); Toast($"Ustawiono motyw {t.Name}"); };
        return card;
    }

    static void Lift(Border b, double s)
    {
        var st = (ScaleTransform)b.RenderTransform;
        var a = new DoubleAnimation(s, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    void MarkActive()
    {
        foreach (var th in thumbs)
        {
            bool active = th.Theme.Id == App.Engine.Theme.Id;
            th.Card.BorderBrush = active ? (Brush)FindResource("AccentSolid") : (Brush)FindResource("Line");
            th.Card.Background = active ? Br("#16130F") : (Brush)FindResource("Card");
            var head = (Grid)((StackPanel)th.Card.Child).Children[1];
            var badge = (Border)head.Children[1];
            badge.Background = active ? (Brush)FindResource("Accent") : Br("#1A212C");
            badge.Child = new TextBlock { Text = active ? "Aktywny" : th.Theme.BuiltIn ? "Wbudowany" : "Twój", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = active ? Br("#1A1208") : SubBrush };
        }
    }

    void DuplicateTheme(Theme t)
    {
        var copy = t.Clone();
        copy.Name = t.Name + " (kopia)";
        copy.Id = Themes.NewId(copy.Name, App.AllThemes);
        Themes.Save(copy);
        BuildGallery();
        Toast("Zrobiono kopię. Możesz ją edytować.");
    }

    async Task DeleteTheme(Theme t)
    {
        if (!await Confirm("Usunąć motyw?", $"„{t.Name}” zniknie na dobre.", "Usuń", "Anuluj")) return;
        Themes.Delete(t);
        if (App.Engine.Theme.Id == t.Id) App.SetTheme(Themes.LoadAll()[0]);
        BuildGallery();
        UpdateThemeCard();
    }

    void ImportTheme()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Motyw Ember (*.json)|*.json", Title = "Importuj motyw" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var t = Theme.Parse(File.ReadAllText(dlg.FileName));
            t.Id = Themes.NewId(t.Name, App.AllThemes);
            Themes.Save(t);
            BuildGallery();
            Toast($"Zaimportowano „{t.Name}”");
        }
        catch (Exception e) { Toast("To nie jest poprawny motyw: " + e.Message, true); }
    }

    // ---------- sensors page ----------

    readonly List<(SensorInfo Info, TextBlock Value, FrameworkElement Row, TextBlock? Header)> sensorRows = new();

    void BuildSensors()
    {
        SensorList.Children.Clear();
        sensorRows.Clear();
        var all = Sensors.Catalog.Concat(App.Sensors.Raw).ToList();
        foreach (var group in all.GroupBy(x => x.Group))
        {
            var header = new TextBlock { Text = group.Key.ToUpperInvariant(), Style = (Style)FindResource("Cap"), Margin = new Thickness(4, 18, 0, 8) };
            SensorList.Children.Add(header);
            var box = new Border { Style = (Style)FindResource("CardBorder"), Padding = new Thickness(6), Margin = new Thickness(0, 0, 8, 0) };
            var sp = new StackPanel();
            box.Child = sp;
            foreach (var info in group)
            {
                var g = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                var b = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 7, 12, 7), Child = g };
                b.MouseEnter += (_, _) => b.Background = Br("#1A212C");
                b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
                var key = info.Key;
                b.MouseLeftButtonUp += (_, _) => { Clipboard.SetText(key); Toast($"Skopiowano {key}"); };
                g.Children.Add(new TextBlock { Text = info.Label, TextTrimming = TextTrimming.CharacterEllipsis });
                var k = new TextBlock { Text = info.Key, FontFamily = (FontFamily)FindResource("Mono"), FontSize = 12, Foreground = DimBrush, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(k, 1);
                g.Children.Add(k);
                var v = new TextBlock { FontFamily = (FontFamily)FindResource("Mono"), FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Right, Foreground = AmberBrush };
                Grid.SetColumn(v, 2);
                g.Children.Add(v);
                sp.Children.Add(b);
                sensorRows.Add((info, v, b, null));
            }
            SensorList.Children.Add(box);
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
        foreach (var (info, value, _, _) in sensorRows)
        {
            string text = info.Key switch
            {
                "time" => now.ToString("HH:mm", Renderer.Pl),
                "date" => now.ToString("dddd, d MMMM", Renderer.Pl),
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
        return unit switch
        {
            "B/s" => Renderer.Format(v, "rate"),
            "MHz" => v.ToString("0", Renderer.Pl) + " MHz",
            "%" or "°C" or "W" or "RPM" => v.ToString(v < 10 ? "0.0" : "0", Renderer.Pl) + " " + unit,
            _ => v.ToString(Math.Abs(v) < 10 ? "0.00" : "0.0", Renderer.Pl) + (unit.Length > 0 ? " " + unit : ""),
        };
    }

    void FilterSensors()
    {
        var q = SensorSearch.Text.Trim();
        foreach (var (info, _, row, _) in sensorRows)
            row.Visibility = q.Length == 0 || info.Label.Contains(q, StringComparison.OrdinalIgnoreCase) || info.Key.Contains(q, StringComparison.OrdinalIgnoreCase) || info.Group.Contains(q, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible : Visibility.Collapsed;
        // Hide group cards whose rows are all filtered out.
        for (int i = 0; i + 1 < SensorList.Children.Count; i += 2)
        {
            var box = (Border)SensorList.Children[i + 1];
            bool any = ((StackPanel)box.Child).Children.Cast<UIElement>().Any(c => c.Visibility == Visibility.Visible);
            box.Visibility = SensorList.Children[i].Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ---------- settings page ----------

    void BuildSettings()
    {
        SettingsPanel.Children.Clear();
        var cfg = App.Cfg;

        var ports = new List<(string, string)> { ("auto", "Automatycznie") };
        var found = TuringLcd.FindPorts();
        foreach (var p in TuringLcd.SerialPortNames()) ports.Add((p, found.Contains(p) ? $"{p} · ekran Turing" : p));
        var conn = Card("Połączenie", 420,
            Caption("PORT", 0),
            Combo(ports, cfg.Port, v => { cfg.Port = v; cfg.Save(); App.Engine.Reconnect(); }),
            Caption("PŁYNNOŚĆ (MAKS. KLATEK NA SEKUNDĘ)"),
            Chips(new[] { (15, "15"), (24, "24"), (30, "30"), (45, "45"), (60, "60") }, cfg.Fps, v => { cfg.Fps = v; cfg.Save(); }),
            new TextBlock { Text = "Ekran przyjmuje ok. 157 KB/s. Ember wysyła tylko zmienione piksele, więc drobne animacje są płynne, a pełne przerysowanie trwa ok. 2 s.", Style = (Style)FindResource("SubText"), Margin = new Thickness(0, 4, 0, 14) },
            new WrapPanel { Children = { Btn("Odśwież obraz", "", false, () => { App.Engine.Redraw(); Toast("Przerysowuję ekran"); }), Gap(), Btn("Połącz ponownie", "", false, () => App.Engine.Reconnect()) }, Margin = new Thickness(0, 0, 0, 12) });

        var gpus = new List<(string, string)> { ("auto", "Automatycznie (dedykowana)") };
        foreach (var (id, name) in Sensors.GpuChoices(App.Sensors.Current)) gpus.Add((id, name));
        var nets = new List<(string, string)> { ("auto", "Automatycznie (najbardziej używana)") };
        foreach (var n in Sensors.NetworkNames()) nets.Add((n, n));
        var sens = Card("Czujniki", 420,
            Caption("ODŚWIEŻANIE", 0),
            Chips(new[] { (500, "0,5 s"), (1000, "1 s"), (2000, "2 s") }, cfg.Interval, v => { cfg.Interval = v; cfg.Save(); }),
            Caption("KARTA GRAFICZNA"),
            Combo(gpus, cfg.Gpu, v => { cfg.Gpu = v; cfg.Save(); }),
            Caption("KARTA SIECIOWA"),
            Combo(nets, cfg.Net, v => { cfg.Net = v; cfg.Save(); }),
            Caption("ZAAWANSOWANE"),
            Switch("Czujniki płyty głównej", "Wentylatory i temperatury z chipu Super I/O. Eksperymentalne: zostaw wyłączone, jeśli nie potrzebujesz.", cfg.Motherboard,
                v => { cfg.Motherboard = v; cfg.Save(); App.Sensors.Reinit(); }));

        bool elevated = Sensors.Elevated;
        var autostart = Switch("Uruchamiaj z Windows", elevated ? "Zadanie w Harmonogramie zadań, startuje z uprawnieniami do czujników, bez pytania UAC." : "Wymaga uruchomienia Ember jako administrator.",
            elevated && Autostart.Exists(), v =>
            {
                bool ok = v ? Autostart.Enable() : Autostart.Disable();
                if (ok) { App.Cfg.Autostart = v; App.Cfg.Save(); }
                Toast(ok ? (v ? "Ember wystartuje razem z Windows" : "Autostart wyłączony") : "Nie udało się zmienić autostartu", !ok);
            });
        autostart.IsEnabled = elevated;
        var sys = Card("System", 420, autostart,
            elevated ? new Border() : Btn("Uruchom jako administrator", "", true, RestartElevated),
            Caption("STAN"),
            new TextBlock { Text = $"Uprawnienia administratora: {(elevated ? "tak" : "nie")}\nPawnIO: {Sensors.PawnIoVersion?.ToString() ?? "brak"}\nLibreHardwareMonitor: {App.Sensors.LhmStatus}", Style = (Style)FindResource("SubText"), Margin = new Thickness(0, 0, 0, 12), LineHeight = 20 });

        var about = Card("O programie", 420,
            new TextBlock { Text = $"Ember {typeof(App).Assembly.GetName().Version!.ToString(3)}. Sterownik ekranu Turing Smart Screen 3.5\" (rev. A).", Style = (Style)FindResource("SubText"), Margin = new Thickness(0, 0, 0, 12) },
            new WrapPanel { Children = { Btn("Logi", "", false, () => App.OpenFolder(App.DataDir)), Gap(), Btn("Motywy", "", false, () => App.OpenFolder(Themes.UserDir)), Gap(), Btn("Zakończ Ember", "", false, App.Quit) }, Margin = new Thickness(0, 0, 0, 12) });

        foreach (var c in new[] { conn, sens, sys, about }) SettingsPanel.Children.Add(c);
    }
}
