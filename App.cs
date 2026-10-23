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
    public string Theme { get; set; } = "ember";
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

    static string FilePath => Path.Combine(App.DataDir, "settings.json");

    public static Settings Load(out bool firstRun)
    {
        firstRun = !File.Exists(FilePath);
        if (firstRun) return new Settings();
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Ember.Theme.Json) ?? new Settings(); }
        catch (Exception e)
        {
            Log.Error("settings", e);
            try { File.Copy(FilePath, FilePath + ".bad", true); } catch { }
            return new Settings();
        }
    }

    public void Save()
    {
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

    [STAThread]
    public static int Main(string[] args)
    {
        Directory.CreateDirectory(DataDir);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("unhandled", (Exception)e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("task", e.Exception); e.SetObserved(); };

        int ai = Array.IndexOf(args, "--render");
        if (ai >= 0) return SelfTest.Render(args.ElementAtOrDefault(ai + 1) ?? ".");
        if (args.Contains("--selftest")) return SelfTest.Run();
        ai = Array.IndexOf(args, "--make-icon");
        if (ai >= 0) { Icons.WriteIco(args.ElementAtOrDefault(ai + 1) ?? "app.ico"); return 0; }

        if (args.Contains("--quit")) return Host.QuitRunning() ? 0 : 1;
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
        Run(startHidden: args.Contains("--autostart"));
        GC.KeepAlive(mutex);
        return 0;
    }

    static void Run(bool startHidden)
    {
        Log.Info($"start {Environment.ProcessPath} elevated={Sensors.Elevated} pawnio={Sensors.PawnIoVersion?.ToString() ?? "none"}");
        Cfg = Settings.Load(out bool firstRun);
        AllThemes = Themes.LoadAll();
        var theme = AllThemes.FirstOrDefault(t => t.Id == Cfg.Theme) ?? AllThemes[0];
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

    public static void ShowWindow()
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
            window.Activate();
            window.Topmost = true;
            window.Topmost = false;
        });
    }

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
        var menu = new ContextMenuStrip { Renderer = new DarkRenderer(), ShowImageMargin = false, ShowCheckMargin = true, Font = new Font("Segoe UI Variable Text", 9.5f) };
        menu.Opening += (_, _) => Fill(menu);
        Fill(menu);
        ni.ContextMenuStrip = menu;
        ni.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) App.ShowWindow(); };
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (_, _) =>
        {
            var s = App.Sensors.Current;
            string Num(string k, string u) => double.IsNaN(s.Get(k)) ? "--" : s.Get(k).ToString("0") + u;
            var text = $"Ember · {(App.Engine.Connected ? "ekran działa" : "brak ekranu")}\nCPU {Num("cpu.load", "%")} {Num("cpu.temp", "°C")} · GPU {Num("gpu.load", "%")} {Num("gpu.temp", "°C")}";
            ni.Text = text.Length > 127 ? text[..127] : text;
        };
        t.Start();
        return ni;
    }

    static void Fill(ContextMenuStrip menu)
    {
        menu.Items.Clear();
        var open = new ToolStripMenuItem("Otwórz Ember", null, (_, _) => App.ShowWindow()) { Font = new Font(menu.Font, System.Drawing.FontStyle.Bold) };
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());
        var themes = new ToolStripMenuItem("Motyw");
        foreach (var th in App.AllThemes)
        {
            var t = th;
            themes.DropDownItems.Add(new ToolStripMenuItem(t.Name, null, (_, _) => App.SetTheme(t)) { Checked = App.Engine.Theme.Id == t.Id });
        }
        Style(themes);
        menu.Items.Add(themes);
        var bright = new ToolStripMenuItem("Jasność");
        foreach (var v in new[] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 })
            bright.DropDownItems.Add(new ToolStripMenuItem($"{v} %", null, (_, _) => { App.Cfg.Brightness = v; App.Cfg.Save(); App.Engine.Poke(); }) { Checked = App.Cfg.Brightness == v });
        Style(bright);
        menu.Items.Add(bright);
        menu.Items.Add(new ToolStripMenuItem("Wygaś ekran", null, (_, _) => App.Engine.Blank = !App.Engine.Blank) { Checked = App.Engine.Blank });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Zakończ", null, (_, _) => App.Quit()));
    }

    static void Style(ToolStripMenuItem item)
    {
        if (item.DropDown is ToolStripDropDownMenu dd) { dd.Renderer = new DarkRenderer(); dd.ShowImageMargin = false; dd.ShowCheckMargin = true; }
    }

    sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        static readonly Color Text = Color.FromArgb(0xE8, 0xEC, 0xF2), Dim = Color.FromArgb(0x5E, 0x69, 0x7C), Accent = Color.FromArgb(0xFF, 0x9A, 0x3D);

        public DarkRenderer() : base(new Colors()) => RoundedEdges = false;

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : Dim;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Color.FromArgb(0x8A, 0x94, 0xA6);
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
            static readonly Color Bg = Color.FromArgb(0x12, 0x18, 0x21), Hover = Color.FromArgb(0x1F, 0x27, 0x33), Line = Color.FromArgb(0x27, 0x30, 0x3F);
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

/// The ember logo, drawn with Skia so the tray icon, the window and the exe icon all match.
public static class Icons
{
    static SKPath Flame(float s, bool inner)
    {
        var p = inner
            ? SKPath.ParseSvgPathData("M52 47 C58 57 68 64 68 76 C68 87 60 93 51 93 C42 93 34 87 34 77 C34 68 40 62 45 57 C45 66 48 70 51 71 C49 62 50 54 52 47 Z")
            : SKPath.ParseSvgPathData("M51 5 C63 25 85 38 85 62 C85 82 69 95 50 95 C31 95 15 82 15 62 C15 48 23 37 32 29 C32 43 38 52 44 54 C40 36 44 19 51 5 Z");
        p.Transform(SKMatrix.CreateScale(s / 100, s / 100));
        return p;
    }

    public static SKBitmap Logo(int size, bool tile)
    {
        var bmp = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var c = new SKCanvas(bmp);
        c.Clear(SKColors.Transparent);
        float s = size;
        if (tile)
        {
            using var bg = new SKPaint { IsAntialias = true };
            bg.Shader = SKShader.CreateRadialGradient(new SKPoint(s * 0.7f, s * 0.15f), s * 1.0f, [new SKColor(0x3A, 0x22, 0x10), new SKColor(0x0E, 0x11, 0x17)], null, SKShaderTileMode.Clamp);
            c.DrawRoundRect(new SKRect(0, 0, s, s), s * 0.22f, s * 0.22f, bg);
            c.Translate(s * 0.14f, s * 0.12f);
            c.Scale(0.72f);
        }
        using var outer = Flame(s, false);
        using var inner = Flame(s, true);
        using var op = new SKPaint { IsAntialias = true };
        op.Shader = SKShader.CreateLinearGradient(new SKPoint(0, s * 0.05f), new SKPoint(0, s * 0.95f),
            [new SKColor(0xFF, 0xD2, 0x5A), new SKColor(0xFF, 0x8A, 0x3D), new SKColor(0xF0, 0x4E, 0x2C)], [0, 0.55f, 1], SKShaderTileMode.Clamp);
        c.DrawPath(outer, op);
        using var ip = new SKPaint { IsAntialias = true };
        ip.Shader = SKShader.CreateLinearGradient(new SKPoint(0, s * 0.47f), new SKPoint(0, s * 0.93f),
            [new SKColor(0xFF, 0xF3, 0xC4), new SKColor(0xFF, 0xC5, 0x3D)], null, SKShaderTileMode.Clamp);
        c.DrawPath(inner, ip);
        return bmp;
    }

    public static Icon Tray()
    {
        int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        using var logo = Logo(size, false);
        using var data = logo.Encode(SKEncodedImageFormat.Png, 100);
        using var ms = new MemoryStream(data.ToArray());
        using var bmp = new Bitmap(ms);
        return Icon.FromHandle(bmp.GetHicon());
    }

    public static void WriteIco(string path)
    {
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        var pngs = sizes.Select(sz => { using var b = Logo(sz, true); using var d = b.Encode(SKEncodedImageFormat.Png, 100); return d.ToArray(); }).ToArray();
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

/// Authenticode check for downloaded installers (PawnIO): valid, trusted, and published by namazso.
static class Signature
{
    public static string? Signer(string file)
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
            var subject = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(file).Subject;
#pragma warning restore SYSLIB0057
            return subject.Contains("namazso", StringComparison.OrdinalIgnoreCase) ? subject : null;
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
