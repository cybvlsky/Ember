using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Threading;
using SkiaSharp;

namespace Ember;

public sealed class Settings
{
    public string Theme { get; set; } = "studio";
    public int Brightness { get; set; } = 50;
    public bool Flip { get; set; } = true;
    public int Fps { get; set; } = 30;
    public string Port { get; set; } = "auto";
    public string Net { get; set; } = "auto";
    public string Gpu { get; set; } = "auto";
    public bool Motherboard { get; set; }
    public bool SleepWithMonitors { get; set; } = true;
    public int Interval { get; set; } = 1000;
    /// null = never chosen: autostart gets switched on the first time we run elevated.
    public bool? Autostart { get; set; }
    public string Language { get; set; } = "en";
    /// The start-up card on the panel.
    public bool Intro { get; set; } = true;

    static string FilePath => Path.Combine(App.DataDir, "settings.json");

    public static Settings Load(out bool firstRun)
    {
        firstRun = !File.Exists(FilePath);
        if (firstRun) return new Settings();
        try
        {
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Ember.Theme.Json) ?? new Settings();
            if (!L.Languages.Contains(s.Language)) s.Language = "en";
            // Built-in themes that were renamed in 1.0.
            s.Theme = s.Theme switch { "rdzenie" => "cores", "zegar" => "clock", _ => s.Theme };
            return s;
        }
        catch (Exception e)
        {
            Log.Error("settings", e);
            try { File.Copy(FilePath, FilePath + ".bad", true); } catch { }
            return new Settings();
        }
    }

    public void Save()
    {
        if (App.Shots) return; // the UI test changes settings; the person's own must survive it
        try { App.WriteAtomic(FilePath, JsonSerializer.Serialize(this, Ember.Theme.Json)); }
        catch (Exception e) { Log.Error("settings save", e); }
    }
}

public static class Log
{
    static readonly object gate = new();
    static readonly HashSet<string> seen = new();

    public static string FilePath => Path.Combine(App.DataDir, "ember.log");
    public static void Info(string m) => Write("INFO", m);
    public static void Warn(string m) => Write("WARN", m);
    public static void Error(string what, Exception e) => Write("ERR ", $"{what}: {e}");

    /// Logs a given failure only the first time, so a broken widget cannot flood the log at 30 fps.
    public static void Once(string key, Exception e)
    {
        lock (seen) if (!seen.Add(key)) return;
        Error(key, e);
    }

    static void Write(string level, string m)
    {
        lock (gate)
        {
            try
            {
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 1_000_000) File.Move(FilePath, FilePath + ".1", true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {m}{Environment.NewLine}");
            }
            catch { }
        }
    }
}

/// Start with Windows through Task Scheduler: a logon task with highest privileges starts us elevated without a UAC prompt,
/// which LibreHardwareMonitor needs for CPU temperatures.
public static class Autostart
{
    public const string TaskName = "Ember";

    public static bool Exists() => Sch($"/Query /TN {TaskName}", out _) == 0;

    public static bool Run() => Sch($"/Run /TN {TaskName}", out _) == 0;

    public static bool Disable() => Sch($"/Delete /TN {TaskName} /F", out _) == 0;

    /// Re-registers the task when the exe moved (dev build vs Program Files).
    public static void RefreshPath()
    {
        if (Sch($"/Query /TN {TaskName} /XML", out var xml) == 0 && !xml.Contains(SecurityElement.Escape(Environment.ProcessPath!), StringComparison.OrdinalIgnoreCase))
            Enable();
    }

    public static bool Enable()
    {
        var exe = SecurityElement.Escape(Environment.ProcessPath!);
        var dir = SecurityElement.Escape(AppContext.BaseDirectory.TrimEnd('\\'));
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.3" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Author>Ember</Author><Description>Ember: ekran Turing 3.5". Start z systemem, z dostępem do czujników.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{sid}</UserId><Delay>PT3S</Delay></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{sid}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
                <RestartOnFailure><Interval>PT1M</Interval><Count>5</Count></RestartOnFailure>
              </Settings>
              <Actions Context="Author"><Exec><Command>"{exe}"</Command><Arguments>--autostart</Arguments><WorkingDirectory>{dir}</WorkingDirectory></Exec></Actions>
            </Task>
            """;
        var tmp = Path.Combine(Path.GetTempPath(), $"ember-task-{Environment.ProcessId}.xml");
        try
        {
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            int rc = Sch($"/Create /TN {TaskName} /XML \"{tmp}\" /F", out var output);
            if (rc != 0) Log.Warn($"schtasks create failed ({rc}): {output}");
            return rc == 0;
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    static int Sch(string args, out string output)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15000)) { p.Kill(); output = "timeout"; return -1; }
            output = o.Result + e.Result;
            return p.ExitCode;
        }
        catch (Exception ex) { output = ex.Message; return -1; }
    }
}

/// Hidden top-level window: receives power/display broadcasts and the "show yourself" ping from a second instance.
sealed class Host : NativeWindow
{
    const string Caption = "Ember.Host";
    static readonly int WmShow = (int)RegisterWindowMessage("Ember.Show"), WmQuit = (int)RegisterWindowMessage("Ember.Quit");
    static Guid displayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");
    readonly IntPtr notify;

    public Host()
    {
        CreateHandle(new CreateParams { Caption = Caption });
        // The elevated instance must accept the ping from a non-elevated one (UIPI).
        ChangeWindowMessageFilterEx(Handle, (uint)WmShow, 1, IntPtr.Zero);
        ChangeWindowMessageFilterEx(Handle, (uint)WmQuit, 1, IntPtr.Zero);
        notify = RegisterPowerSettingNotification(Handle, ref displayState, 0);
    }

    public void Close()
    {
        if (notify != IntPtr.Zero) UnregisterPowerSettingNotification(notify);
        DestroyHandle();
    }

    public static bool SignalRunning()
    {
        var h = FindWindow(null, Caption);
        if (h == IntPtr.Zero) return false;
        PostMessage(h, WmShow, IntPtr.Zero, IntPtr.Zero);
        return true;
    }

    /// Asks a running instance to exit cleanly (it darkens the panel on the way out). Used by the installer.
    public static bool QuitRunning()
    {
        var h = FindWindow(null, Caption);
        if (h == IntPtr.Zero) return false;
        PostMessage(h, WmQuit, IntPtr.Zero, IntPtr.Zero);
        WaitGone();
        return FindWindow(null, Caption) == IntPtr.Zero;
    }

    public static void WaitGone()
    {
        for (int i = 0; i < 80 && FindWindow(null, Caption) != IntPtr.Zero; i++) Thread.Sleep(100);
    }

    public static void SignalWhenUp()
    {
        for (int i = 0; i < 40; i++)
        {
            if (SignalRunning()) return;
            Thread.Sleep(250);
        }
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_POWERBROADCAST = 0x218, WM_QUERYENDSESSION = 0x11, WM_ENDSESSION = 0x16;
        if (m.Msg == WmShow) App.ShowWindow();
        else if (m.Msg == WmQuit) App.Quit();
        else if (m.Msg == WM_POWERBROADCAST)
        {
            switch ((int)m.WParam)
            {
                case 0x4: App.Engine.Suspend(); break; // PBT_APMSUSPEND
                case 0x7 or 0x12: App.Engine.Resume(); break; // PBT_APMRESUMESUSPEND / AUTOMATIC
                case 0x8013: // PBT_POWERSETTINGCHANGE
                    if (Marshal.PtrToStructure<Guid>(m.LParam) == displayState)
                        App.Engine.MonitorsOff = Marshal.ReadInt32(m.LParam, 20) == 0;
                    break;
            }
            m.Result = 1;
            return;
        }
        else if (m.Msg == WM_QUERYENDSESSION) { m.Result = 1; return; }
        else if (m.Msg == WM_ENDSESSION && m.WParam != IntPtr.Zero)
        {
            // Windows is shutting down or logging off: darken the panel now, the process will not get another chance.
            App.ShutdownNow();
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    [DllImport("user32", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string? cls, string caption);
    [DllImport("user32")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32")] static extern bool ChangeWindowMessageFilterEx(IntPtr h, uint msg, uint action, IntPtr info);
    [DllImport("user32")] static extern IntPtr RegisterPowerSettingNotification(IntPtr h, ref Guid g, int flags);
    [DllImport("user32")] static extern bool UnregisterPowerSettingNotification(IntPtr h);
}

public static class App
{
    public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ember");
    public static Settings Cfg = new();
    public static Sensors Sensors = null!;
    public static Engine Engine = null!;
    public static List<Theme> AllThemes = new();
    public static System.Windows.Application Wpf = null!;
    static MainWindow? window;
    static NotifyIcon? tray;
    static Host? host;
    static DispatcherTimer? saveTimer;
    static int shuttingDown;
    static string? shotDir;
    public static bool Shots => shotDir != null;

    [STAThread]
    public static int Main(string[] args)
    {
        Directory.CreateDirectory(DataDir);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("unhandled", (Exception)e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("task", e.Exception); e.SetObserved(); };

        int ai = Array.IndexOf(args, "--render");
        if (ai >= 0)
        {
            if (args.Contains("pl")) L.Lang = "pl";
            return SelfTest.Render(args.ElementAtOrDefault(ai + 1) ?? ".");
        }
        if (args.Contains("--selftest")) return SelfTest.Run();
        ai = Array.IndexOf(args, "--make-icon");
        if (ai >= 0) { Icons.WriteIco(args.ElementAtOrDefault(ai + 1) ?? "app.ico"); return 0; }

        if (args.Contains("--install-pawnio")) return Sensors.PawnIoVersion != null || PawnIo.Install().GetAwaiter().GetResult() == PawnIo.Result.Installed ? 0 : 1;
        if (args.Contains("--quit")) return Host.QuitRunning() ? 0 : 1;
        if (args.Contains("--demo")) Sensors.Demo = true;
        if (args.Contains("--after-restart")) Host.WaitGone();
        if (Host.SignalRunning()) return 0;

        if (!Sensors.Elevated && !args.Contains("--no-elevate") && !args.Contains("--elevated"))
        {
            // The logon task runs us elevated without a prompt; otherwise ask once through UAC.
            if (Autostart.Exists() && Autostart.Run()) { Host.SignalWhenUp(); return 0; }
            try
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!, string.Join(' ', args.Append("--elevated"))) { UseShellExecute = true, Verb = "runas" });
                return 0;
            }
            catch (Win32Exception) { Log.Info("UAC declined, running without admin rights"); }
        }

        Mutex mutex;
        try
        {
            mutex = new Mutex(true, @"Local\Ember.Screen", out bool created);
            if (!created) { Host.SignalWhenUp(); return 0; }
        }
        catch (UnauthorizedAccessException) { Host.SignalWhenUp(); return 0; }

        ai = Array.IndexOf(args, "--screenshots");
        shotDir = ai >= 0 ? args.ElementAtOrDefault(ai + 1) : null;
        // The installer passes the language its wizard ran in, so the first start matches it.
        ai = Array.IndexOf(args, "--language");
        Run(startHidden: args.Contains("--autostart"), firstLanguage: ai >= 0 ? args.ElementAtOrDefault(ai + 1) : null);
        GC.KeepAlive(mutex);
        return 0;
    }

    static void Run(bool startHidden, string? firstLanguage)
    {
        Log.Info($"start {Environment.ProcessPath} elevated={Sensors.Elevated} pawnio={Sensors.PawnIoVersion?.ToString() ?? "none"}");
        Cfg = Settings.Load(out bool firstRun);
        if ((firstRun || Shots) && firstLanguage != null && L.Languages.Contains(firstLanguage)) Cfg.Language = firstLanguage;
        L.Lang = Cfg.Language;
        AllThemes = Themes.LoadAll();
        var theme = (Shots ? null : AllThemes.FirstOrDefault(t => t.Id == Cfg.Theme)) ?? AllThemes[0];
        Sensors = new Sensors(Cfg);
        Sensors.Start();
        Engine = new Engine(Cfg, Sensors, theme);
        Engine.Start();
        if (Sensors.Elevated)
        {
            if (Cfg.Autostart != false && !Ember.Autostart.Exists()) { Cfg.Autostart = Ember.Autostart.Enable(); Cfg.Save(); }
            else if (Ember.Autostart.Exists()) Ember.Autostart.RefreshPath();
        }
        if (firstRun) Cfg.Save();

        Wpf = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        Wpf.DispatcherUnhandledException += (_, e) => { Log.Error("ui", e.Exception); e.Handled = true; };
        host = new Host();
        tray = Tray.Create();
        if (!startHidden || shotDir != null) ShowWindow();
        if (shotDir != null) Wpf.Dispatcher.BeginInvoke(async () => { await Task.Delay(1500); await window!.Screenshots(shotDir); Quit(); }, DispatcherPriority.ApplicationIdle);
        Wpf.Run();
        ShutdownNow();
    }

    public const string RepoUrl = "https://github.com/cybvlsky/Ember";
    public static string Version => typeof(App).Assembly.GetName().Version!.ToString(3);

    public static void ShowWindow(string? page = null)
    {
        if (Wpf == null) return;
        Wpf.Dispatcher.BeginInvoke(() =>
        {
            if (window == null)
            {
                window = new MainWindow();
                window.Closed += (_, _) =>
                {
                    window = null;
                    // Collect once the window is really unreachable (its own Closed handlers still hold it here), so the
                    // tray-only process shrinks back to its idle size.
                    Wpf.Dispatcher.BeginInvoke(async () =>
                    {
                        await Task.Delay(2000);
                        if (window != null) return;
                        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                        GC.Collect(2, GCCollectionMode.Forced, true, true);
                    }, DispatcherPriority.ApplicationIdle);
                };
                window.Show();
            }
            if (window.WindowState == System.Windows.WindowState.Minimized) window.WindowState = System.Windows.WindowState.Normal;
            if (page != null) window.GoTo(page);
            window.Activate();
            window.Topmost = true;
            window.Topmost = false;
        });
    }

    /// Switches the UI and the built-in theme texts to Cfg.Language: the window is rebuilt and the panel redrawn.
    public static void ApplyLanguage()
    {
        L.Lang = Cfg.Language;
        Engine.Theme = Engine.Theme.Clone();
        if (window is { } w)
        {
            w.Closed += (_, _) => Wpf.Dispatcher.BeginInvoke(() => ShowWindow("settings"));
            w.Close();
        }
    }

    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    public static void SetTheme(Theme t)
    {
        Engine.Theme = t;
        Cfg.Theme = t.Id;
        Cfg.Save();
    }

    /// Debounced save for sliders and the like.
    public static void SaveSoon()
    {
        if (saveTimer == null)
        {
            saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            saveTimer.Tick += (_, _) => { saveTimer.Stop(); Cfg.Save(); };
        }
        saveTimer.Stop();
        saveTimer.Start();
    }

    public static void Quit() => Wpf.Shutdown();

    public static void ShutdownNow()
    {
        if (Interlocked.Exchange(ref shuttingDown, 1) == 1) return;
        Log.Info("stop");
        try { window?.Close(); } catch { }
        if (tray != null) { tray.Visible = false; tray.Dispose(); }
        Engine?.Dispose();
        Sensors?.Dispose();
        Cfg.Save();
    }

    public static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, true);
    }

    public static void OpenFolder(string dir)
    {
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }
}

static class Tray
{
    public static NotifyIcon Create()
    {
        var ni = new NotifyIcon { Icon = Icons.Tray(), Text = "Ember", Visible = true };
        var menu = new ContextMenuStrip { Renderer = new DarkRenderer(), ShowImageMargin = false, ShowCheckMargin = true, Font = new Font("Segoe UI", 9.5f) };
        menu.Opening += (_, _) => Fill(menu);
        Fill(menu);
        ni.ContextMenuStrip = menu;
        ni.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) App.ShowWindow(); };
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (_, _) =>
        {
            var s = App.Sensors.Current;
            string Num(string k, string u) => double.IsNaN(s.Get(k)) ? "--" : s.Get(k).ToString("0") + u;
            var text = $"Ember: {(App.Engine.Connected ? L.T("screen connected") : L.T("no screen"))}\nCPU {Num("cpu.load", "%")} {Num("cpu.temp", "°C")}   GPU {Num("gpu.load", "%")} {Num("gpu.temp", "°C")}";
            ni.Text = text.Length > 127 ? text[..127] : text;
        };
        t.Start();
        return ni;
    }

    static void Fill(ContextMenuStrip menu)
    {
        menu.Items.Clear();
        var open = new ToolStripMenuItem(L.T("Open Ember"), null, (_, _) => App.ShowWindow()) { Font = new Font(menu.Font, System.Drawing.FontStyle.Bold) };
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());
        var themes = new ToolStripMenuItem(L.T("Theme"));
        foreach (var th in App.AllThemes)
        {
            var t = th;
            themes.DropDownItems.Add(new ToolStripMenuItem(L.T(t.Name), null, (_, _) => App.SetTheme(t)) { Checked = App.Engine.Theme.Id == t.Id });
        }
        Style(themes);
        menu.Items.Add(themes);
        var bright = new ToolStripMenuItem(L.T("Brightness"));
        foreach (var v in new[] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 })
            bright.DropDownItems.Add(new ToolStripMenuItem($"{v}%", null, (_, _) => { App.Cfg.Brightness = v; App.Cfg.Save(); App.Engine.Poke(); }) { Checked = App.Cfg.Brightness == v });
        Style(bright);
        menu.Items.Add(bright);
        menu.Items.Add(new ToolStripMenuItem(L.T("Turn the screen off"), null, (_, _) => App.Engine.Blank = !App.Engine.Blank) { Checked = App.Engine.Blank });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(L.T("Quit"), null, (_, _) => App.Quit()));
    }

    static void Style(ToolStripMenuItem item)
    {
        if (item.DropDown is ToolStripDropDownMenu dd) { dd.Renderer = new DarkRenderer(); dd.ShowImageMargin = false; dd.ShowCheckMargin = true; }
    }

    sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        static readonly Color Text = Color.FromArgb(0xFF, 0xFF, 0xFF), Dim = Color.FromArgb(0x9A, 0x9A, 0x9A), Accent = Color.FromArgb(0xFF, 0xA0, 0x66);

        public DarkRenderer() : base(new Colors()) => RoundedEdges = false;

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : Dim;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Color.FromArgb(0xC5, 0xC5, 0xC5);
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            int d = Math.Min(r.Width, r.Height) / 2;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var b = new SolidBrush(Accent);
            e.Graphics.FillEllipse(b, r.X + (r.Width - d) / 2, r.Y + (r.Height - d) / 2, d, d);
        }

        sealed class Colors : ProfessionalColorTable
        {
            static readonly Color Bg = Color.FromArgb(0x2C, 0x2C, 0x2C), Hover = Color.FromArgb(0x38, 0x38, 0x38), Line = Color.FromArgb(0x3A, 0x3A, 0x3A);
            public override Color ToolStripDropDownBackground => Bg;
            public override Color ImageMarginGradientBegin => Bg;
            public override Color ImageMarginGradientMiddle => Bg;
            public override Color ImageMarginGradientEnd => Bg;
            public override Color MenuBorder => Line;
            public override Color MenuItemBorder => Hover;
            public override Color MenuItemSelected => Hover;
            public override Color MenuItemSelectedGradientBegin => Hover;
            public override Color MenuItemSelectedGradientEnd => Hover;
            public override Color MenuItemPressedGradientBegin => Hover;
            public override Color MenuItemPressedGradientEnd => Hover;
            public override Color SeparatorDark => Line;
            public override Color SeparatorLight => Bg;
            public override Color CheckBackground => Bg;
            public override Color CheckSelectedBackground => Hover;
            public override Color CheckPressedBackground => Hover;
        }
    }
}

/// The Ember mark, drawn with Skia so the tray, the window, the exe icon and the panel match.
public static class Icons
{
    static SKColor C(uint rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    public static SKBitmap Logo(int size)
    {
        var bmp = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var c = new SKCanvas(bmp);
        c.Clear(SKColors.Transparent);
        Draw(c, size);
        return bmp;
    }

    /// A flat orange tile with a white E whose middle arm ends in a dot. Up to 32 px every edge is snapped to
    /// whole pixels so the tray icon stays crisp.
    public static void Draw(SKCanvas c, float s)
    {
        bool small = s <= 32;
        float P(float f) => small ? MathF.Round(f * s) : f * s;
        using var tile = new SKPaint { IsAntialias = true, Color = C(0xF2702F) };
        c.DrawRoundRect(new SKRect(0, 0, s, s), s * .22f, s * .22f, tile);
        using var ink = new SKPaint { IsAntialias = !small, Color = SKColors.White };
        void Bar(float x0, float y0, float x1, float y1)
        {
            var r = new SKRect(P(x0), P(y0), P(x1), P(y1));
            float k = small ? 0 : r.Height * .18f;
            c.DrawRoundRect(r, k, k, ink);
        }
        Bar(.28f, .24f, .40f, .76f);
        Bar(.28f, .24f, .70f, .355f);
        Bar(.28f, .445f, .58f, .555f);
        Bar(.28f, .645f, .70f, .76f);
        ink.IsAntialias = true;
        c.DrawCircle(s * .76f, small ? (P(.445f) + P(.555f)) / 2 : s * .5f, s * (small ? .075f : .065f), ink);
    }

    /// The card the panel shows for a few seconds when Ember starts.
    public static SKBitmap Intro()
    {
        const int w = TuringLcd.Width, h = TuringLcd.Height;
        var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var c = new SKCanvas(bmp);
        c.Clear(C(0x1C1C1C));
        c.Save();
        c.Translate(w / 2f - 40, 58);
        Draw(c, 80);
        c.Restore();
        using var bold = SKTypeface.FromFamilyName("Segoe UI Variable Display", SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
            ?? SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default;
        using var regular = SKTypeface.FromFamilyName("Segoe UI Variable Text", SKFontStyle.Normal) ?? SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal) ?? SKTypeface.Default;
        void Text(string text, SKTypeface tf, float size, float y, uint color)
        {
            using var f = new SKFont(tf, size) { Edging = SKFontEdging.SubpixelAntialias };
            using var p = new SKPaint { IsAntialias = true, Color = C(color) };
            c.DrawText(text, (w - f.MeasureText(text)) / 2, y, f, p);
        }
        Text("Ember", bold, 36, 190, 0xFFFFFF);
        Text(L.F("version {0}", App.Version), regular, 14, 214, 0xC5C5C5);
        Text("github.com/cybvlsky/Ember", regular, 13, 280, 0x9A9A9A);
        Text(L.T("made by cybvlsky"), regular, 13, 299, 0x7A7A7A);
        return bmp;
    }

    public static Icon Tray()
    {
        int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        using var logo = Logo(size);
        using var data = logo.Encode(SKEncodedImageFormat.Png, 100);
        using var ms = new MemoryStream(data.ToArray());
        using var bmp = new Bitmap(ms);
        return Icon.FromHandle(bmp.GetHicon());
    }

    public static void WriteIco(string path)
    {
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        var pngs = sizes.Select(sz => { using var b = Logo(sz); using var d = b.Encode(SKEncodedImageFormat.Png, 100); return d.ToArray(); }).ToArray();
        using var f = File.Create(path);
        using var w = new BinaryWriter(f);
        w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
            w.Write(pngs[i].Length); w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) w.Write(p);
    }
}

/// Downloads the official PawnIO installer, checks the signature and runs it. Used by the Screen page and by
/// the Ember installer (`--install-pawnio`), which runs elevated, so PawnIO's own setup needs no second prompt.
public static class PawnIo
{
    public enum Result { Installed, NotFinished, BadSignature, Failed }
    const string Url = "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe";

    public static async Task<Result> Install()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Ember");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "PawnIO_setup.exe");
        try
        {
            using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(2) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Ember/" + App.Version);
                await File.WriteAllBytesAsync(file, await http.GetByteArrayAsync(Url));
            }
            // Held read-only with no write/delete sharing from verification to launch, so the file that runs (elevated) is the file that was checked.
            using var hold = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            var signer = await Task.Run(() => Signature.Signer(file, "namazso.eu"));
            if (signer == null)
            {
                Log.Warn("PawnIO setup signature invalid");
                return Result.BadSignature;
            }
            Log.Info($"PawnIO setup signed by {signer}");
            using var p = Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
            if (p != null) await p.WaitForExitAsync();
            return Sensors.PawnIoVersion != null ? Result.Installed : Result.NotFinished;
        }
        catch (Exception e)
        {
            Log.Error("pawnio install", e);
            return Result.Failed;
        }
    }
}

/// Authenticode check for downloaded installers (PawnIO): valid, trusted, and published by namazso.
static class Signature
{
    /// The full subject when `file` carries a valid Authenticode signature whose certificate CN is exactly `commonName`.
    public static string? Signer(string file, string commonName)
    {
        var fi = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = file };
        IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fi, pFile, false);
            var data = new WINTRUST_DATA { cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(), dwUIChoice = 2, dwUnionChoice = 1, pFile = pFile };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            if (WinVerifyTrust(new IntPtr(-1), ref action, ref data) != 0) return null;
#pragma warning disable SYSLIB0057
            using var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(file));
#pragma warning restore SYSLIB0057
            return string.Equals(cert.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false), commonName, StringComparison.OrdinalIgnoreCase) ? cert.Subject : null;
        }
        catch { return null; }
        finally { Marshal.FreeHGlobal(pFile); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WINTRUST_FILE_INFO { public uint cbStruct; public string pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }

    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_DATA
    {
        public uint cbStruct; public IntPtr pPolicyCallbackData, pSIPClientData; public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice;
        public IntPtr pFile; public uint dwStateAction; public IntPtr hWVTStateData, pwszURLReference; public uint dwProvFlags, dwUIContext; public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust", CharSet = CharSet.Unicode)] static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);
}
