using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using SkiaSharp;

namespace Ember;

public delegate void BlitFn(int x, int y, int w, int h, ReadOnlySpan<byte> rgb565le);

/// Keeps a copy of what the panel shows and sends only what changed, in small prioritised rectangles.
/// The panel moves ~157 KB/s, so each call sends at most `budget` bytes: small animated regions stay fluid
/// while big changes (theme switch, scrolling graph) trickle in over a few frames instead of freezing everything.
public sealed class FrameDiff
{
    public const int W = TuringLcd.Width, H = TuringLcd.Height, TW = 16, TH = 8, TX = W / TW, TY = H / TH;
    public const double Bandwidth = 157_000;

    public readonly ushort[] Shown = new ushort[W * H];
    readonly bool[] unknown = new bool[TX * TY];
    readonly bool[] dirty = new bool[TX * TY];
    readonly double[] since = new double[TX * TY];
    readonly byte[] buf = new byte[W * H * 2];
    readonly List<Rect> rects = new();

    record struct Rect(int X0, int Y0, int X1, int Y1, double Age, bool Unknown)
    {
        public int Bytes => (X1 - X0) * (Y1 - Y0) * 2;
    }

    public FrameDiff() => Invalidate();

    /// Panel content unknown (fresh connection, rotation): everything gets resent.
    public void Invalidate()
    {
        Array.Fill(unknown, true);
        Array.Clear(since);
    }

    /// Sends up to `budget` bytes of differences between `next` and the panel. Returns bytes sent; `pending` = more is waiting.
    public int Send(ushort[] next, double t, double budget, BlitFn blit, out bool pending)
    {
        pending = false;
        int n = MarkDirty(next, t);
        if (n == 0) return 0;
        BuildRects(next);
        // Priority: bytes the panel could have moved while this rect waited, minus its own size.
        // Fresh small changes go first, but anything big that has waited ~0.1 s+ is not starved.
        rects.Sort((a, b) => ((t - b.Age) * Bandwidth - b.Bytes).CompareTo((t - a.Age) * Bandwidth - a.Bytes));
        int sent = 0;
        for (int i = 0; i < rects.Count; i++)
        {
            var r = rects[i];
            int rowBytes = (r.X1 - r.X0) * 2;
            int rows = r.Y1 - r.Y0;
            if (sent + r.Bytes > budget)
            {
                int fit = (int)((budget - sent) / rowBytes);
                if (r.Unknown) fit = fit / TH * TH;
                if (sent == 0) fit = Math.Max(fit, r.Unknown ? TH : 1);
                if (fit <= 0 || fit < rows / 4 && sent > 0) { pending = true; break; }
                rows = Math.Min(rows, fit);
                pending = true;
            }
            sent += Blit(next, r.X0, r.Y0, r.X1 - r.X0, rows, blit);
            if (pending) break;
        }
        return sent;
    }

    int MarkDirty(ushort[] next, double t)
    {
        int n = 0;
        for (int ty = 0; ty < TY; ty++)
        {
            int row0 = ty * TX;
            for (int tx = 0; tx < TX; tx++) dirty[row0 + tx] = unknown[row0 + tx];
            for (int y = ty * TH; y < ty * TH + TH; y++)
            {
                var a = next.AsSpan(y * W, W);
                var b = Shown.AsSpan(y * W, W);
                if (a.SequenceEqual(b)) continue;
                for (int tx = 0; tx < TX; tx++)
                    if (!dirty[row0 + tx] && !a.Slice(tx * TW, TW).SequenceEqual(b.Slice(tx * TW, TW))) dirty[row0 + tx] = true;
            }
            for (int tx = 0; tx < TX; tx++)
            {
                int i = row0 + tx;
                if (dirty[i]) { n++; if (since[i] == 0) since[i] = t; }
                else since[i] = 0;
            }
        }
        return n;
    }

    void BuildRects(ushort[] next)
    {
        rects.Clear();
        // Horizontal runs of dirty tiles per band; a run spanning the same columns as one in the band above extends it.
        var open = new Dictionary<(int, int), int>();
        var cont = new Dictionary<(int, int), int>();
        for (int ty = 0; ty < TY; ty++)
        {
            cont.Clear();
            int tx = 0;
            while (tx < TX)
            {
                if (!dirty[ty * TX + tx]) { tx++; continue; }
                int s0 = tx;
                double age = double.MaxValue;
                bool unk = false;
                while (tx < TX && dirty[ty * TX + tx]) { age = Math.Min(age, since[ty * TX + tx]); unk |= unknown[ty * TX + tx]; tx++; }
                var key = (s0, tx);
                if (open.TryGetValue(key, out int ri))
                {
                    var r = rects[ri];
                    rects[ri] = r with { Y1 = (ty + 1) * TH, Age = Math.Min(r.Age, age), Unknown = r.Unknown | unk };
                }
                else
                {
                    rects.Add(new Rect(s0 * TW, ty * TH, tx * TW, (ty + 1) * TH, age, unk));
                    ri = rects.Count - 1;
                }
                cont[key] = ri;
            }
            (open, cont) = (cont, open);
        }
        // Shrink each known rect to the exact changed pixels.
        for (int i = rects.Count - 1; i >= 0; i--)
        {
            var r = rects[i];
            if (r.Unknown) continue;
            int minX = r.X1, maxX = r.X0 - 1, minY = -1, maxY = -1, len = r.X1 - r.X0;
            for (int y = r.Y0; y < r.Y1; y++)
            {
                var a = next.AsSpan(y * W + r.X0, len);
                var b = Shown.AsSpan(y * W + r.X0, len);
                int f = a.CommonPrefixLength(b);
                if (f == len) continue;
                int l = len - 1;
                while (l > f && a[l] == b[l]) l--;
                if (minY < 0) minY = y;
                maxY = y;
                minX = Math.Min(minX, r.X0 + f);
                maxX = Math.Max(maxX, r.X0 + l);
            }
            if (minY < 0) rects.RemoveAt(i);
            else rects[i] = r with { X0 = minX, Y0 = minY, X1 = maxX + 1, Y1 = maxY + 1 };
        }
    }

    int Blit(ushort[] next, int x, int y, int w, int h, BlitFn blit)
    {
        int bytes = w * h * 2, o = 0;
        for (int yy = y; yy < y + h; yy++, o += w * 2)
            MemoryMarshal.AsBytes(next.AsSpan(yy * W + x, w)).CopyTo(buf.AsSpan(o));
        blit(x, y, w, h, buf.AsSpan(0, bytes));
        for (int yy = y; yy < y + h; yy++) next.AsSpan(yy * W + x, w).CopyTo(Shown.AsSpan(yy * W + x, w));
        // Tiles now fully covered are known again.
        if (h >= TH && w >= TW)
            for (int ty = (y + TH - 1) / TH; ty < (y + h) / TH; ty++)
                for (int tx = (x + TW - 1) / TW; tx < (x + w) / TW; tx++)
                    unknown[ty * TX + tx] = false;
        return bytes;
    }

    static readonly int[] Bayer = [0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5];

    /// BGRA8888 -> RGB565 with a 4x4 ordered dither. Ordered (not error diffusion) keeps static pixels identical
    /// between frames, so dithering costs no bandwidth while removing banding from gradients.
    /// `cache` (optional, W*H*4) holds the last converted frame: rows that did not change are skipped.
    /// `lut` (optional, see ColorFix) remaps each channel first; clear `cache` whenever it changes.
    public static unsafe void To565(SKBitmap src, ushort[] dst, byte[]? cache = null, byte[]? lut = null)
    {
        byte* p = (byte*)src.GetPixels();
        int stride = src.RowBytes;
        fixed (ushort* d = dst)
        fixed (byte* L = lut)
            for (int y = 0; y < H; y++)
            {
                byte* row = p + y * stride;
                if (cache != null)
                {
                    var now = new ReadOnlySpan<byte>(row, W * 4);
                    var old = cache.AsSpan(y * W * 4, W * 4);
                    if (now.SequenceEqual(old)) continue;
                    now.CopyTo(old);
                }
                ushort* o = d + y * W;
                int by = (y & 3) * 4;
                for (int x = 0; x < W; x++)
                {
                    int t = Bayer[by + (x & 3)];
                    int b0 = row[0], g0 = row[1], r0 = row[2];
                    if (L != null) { r0 = L[r0]; g0 = L[256 + g0]; b0 = L[512 + b0]; }
                    int b = Math.Min(255, b0 + (t >> 1));
                    int g = Math.Min(255, g0 + (t >> 2));
                    int r = Math.Min(255, r0 + (t >> 1));
                    o[x] = (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
                    row += 4;
                }
            }
    }
}

/// The background loop: render -> convert -> diff -> send, plus connection and power handling.
public sealed class Engine : IDisposable
{
    readonly Settings cfg;
    readonly Sensors sensors;
    readonly TuringLcd lcd = new();
    readonly FrameDiff diff = new();
    readonly ushort[] next = new ushort[FrameDiff.W * FrameDiff.H];
    readonly byte[] converted = new byte[FrameDiff.W * FrameDiff.H * 4];
    readonly SKBitmap frame = new(FrameDiff.W, FrameDiff.H, SKColorType.Bgra8888, SKAlphaType.Premul);
    readonly AutoResetEvent wake = new(false);
    readonly Pacer pacer = new();
    readonly ManualResetEventSlim darkened = new(true);
    readonly object previewLock = new();
    readonly byte[] preview = new byte[FrameDiff.W * FrameDiff.H * 4];
    Thread? thread;
    volatile bool stop, invalidate, reconnect, suspended, monitorsOff, userBlank;
    volatile Theme theme;
    Renderer? renderer;
    double resumeAt;
    bool lcdDark, resetNext;
    bool? appliedFlip;
    int previewSeq, statLines;

    // Backlight choreography. The link is far too slow to fade pixels, but the backlight is one 6-byte command:
    // every time the panel (re)appears the frame is drawn in the dark and the backlight fades up. The first
    // connection shows the start-up card first.
    int phase;           // 0 live, 1 drawing in the dark, 2 fading in, 3 holding the card, 4 fading the card out
    double phaseAt;
    bool intro, introDue;
    int level = -1;      // brightness % last sent
    SKBitmap? introFrame;
    byte[]? lut;
    (int, double, int, int, int)? lutFor;
    // App screens take turns with the theme (Rotation). `screen` is what is drawn; `switchTo` waits for the fade out.
    volatile string screen = "theme";
    string? switchTo;
    double shownAt = Clock.Now;
    readonly List<string> apps = new(2);
    Theme? appScreen, appLook;
    string? appFor;
    int appArt;
    const double FadeIn = 0.7, Hold = 2.8, FadeOut = 0.5, DrawTimeout = 4;

    public volatile bool PreviewWanted;
    /// The theme editor is open: no app screens over the theme being edited.
    public volatile bool HoldTheme;
    public volatile string Status = L.T("Looking for the screen…");
    public volatile bool Connected, Searching = true;
    public volatile string Port = "";
    public volatile float Fps, KBps;
    public volatile float RenderMs;

    public Engine(Settings cfg, Sensors sensors, Theme theme)
    {
        this.cfg = cfg;
        this.sensors = sensors;
        this.theme = theme;
        introDue = cfg.Intro && !Sensors.Demo;
    }

    public Theme Theme { get => theme; set { theme = value; screen = "theme"; shownAt = Clock.Now; wake.Set(); } }
    public bool Blank { get => userBlank; set { userBlank = value; wake.Set(); } }
    public bool MonitorsOff { set { monitorsOff = value; wake.Set(); } }
    public void Poke() => wake.Set();
    public void Redraw() { invalidate = true; wake.Set(); }
    public void Reconnect() { reconnect = true; wake.Set(); }

    public void Start()
    {
        thread = new Thread(Loop) { IsBackground = true, Name = "Engine", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    /// System going to sleep: darken and let go of the port before the USB stack powers down.
    public void Suspend()
    {
        darkened.Reset();
        suspended = true;
        wake.Set();
        darkened.Wait(1500);
    }

    public void Resume()
    {
        resumeAt = Clock.Now + 3;
        suspended = false;
        wake.Set();
    }

    public void Dispose()
    {
        stop = true;
        wake.Set();
        thread?.Join(4000);
    }

    public bool CopyPreview(byte[] dst, ref int seen)
    {
        lock (previewLock)
        {
            if (seen == previewSeq) return false;
            Buffer.BlockCopy(preview, 0, dst, 0, preview.Length);
            seen = previewSeq;
            return true;
        }
    }

    void Loop()
    {
        double last = Clock.Now, nextTry = 0, statT = Clock.Now;
        int frames = 0;
        long bytes = 0;
        while (!stop)
        {
            double t0 = Clock.Now, dt = Math.Min(0.25, t0 - last);
            last = t0;
            try
            {
                bool active = !suspended && !userBlank && !(monitorsOff && cfg.SleepWithMonitors);
                var snap = sensors.Current;
                if (phase == 0) Rotate(snap, t0, lcd.IsOpen && active);
                var showing = Showing();
                bool fresh = renderer == null || renderer.Theme != showing;
                if (fresh) { renderer?.Dispose(); renderer = new Renderer(showing); }
                if (reconnect) { reconnect = false; Disconnect(L.T("Reconnecting…")); nextTry = 0; }
                if (invalidate) { invalidate = false; diff.Invalidate(); }

                if (lcd.IsOpen && !active && !lcdDark) { Backlight(0); lcdDark = true; phase = 0; intro = false; }
                if (suspended)
                {
                    if (lcd.IsOpen) Disconnect(L.T("Asleep"));
                    darkened.Set();
                }
                else if (active && !lcd.IsOpen && t0 >= nextTry && t0 >= resumeAt) { nextTry = t0 + 2; Connect(); }
                if (lcd.IsOpen && active)
                {
                    // Back from dark: whatever the panel still holds is stale, so draw first and fade up after.
                    if (lcdDark) { lcdDark = false; Begin(t0); }
                    ApplyControls();
                }

                double r0 = Clock.Now;
                using (var c = new SKCanvas(frame))
                {
                    if (intro) c.DrawBitmap(introFrame ??= Icons.Intro(), 0, 0);
                    else renderer!.Render(c, snap, fresh ? 0 : dt, t0);
                }
                RenderMs = (float)((Clock.Now - r0) * 1000);
                if (PreviewWanted)
                    lock (previewLock) { Marshal.Copy(frame.GetPixels(), preview, 0, preview.Length); previewSeq++; }

                int sent = 0;
                bool pending = false;
                int fps = Math.Clamp(cfg.Fps, 5, 60);
                if (lcd.IsOpen && active)
                {
                    var colors = (cfg.ColorTemp, cfg.Gamma, cfg.GainR, cfg.GainG, cfg.GainB);
                    if (colors != lutFor) { lutFor = colors; lut = ColorFix.Lut(cfg); Array.Clear(converted); }
                    FrameDiff.To565(frame, next, converted, lut);
                    sent = diff.Send(next, t0, Math.Max(4096, FrameDiff.Bandwidth / fps), lcd.Blit, out pending);
                    if (sent > 0) { frames++; bytes += sent; }
                    Sequence(Clock.Now, pending);
                }
                if (Clock.Now - statT >= 1)
                {
                    double span = Clock.Now - statT;
                    Fps = (float)(frames / span);
                    KBps = (float)(bytes / span / 1024);
                    frames = 0; bytes = 0; statT = Clock.Now;
                    if (Connected && ++statLines % 60 == 1) Log.Info($"stats: {Fps:0.0} fps, {KBps:0.0} KB/s, render {RenderMs:0.00} ms, theme {theme.Id}");
                }
                double period = phase is 2 or 4 ? 1.0 / 60 : renderer!.Animating || pending || sent > 0 ? 1.0 / fps : phase == 3 ? 0.1 : 0.25;
                double wait = period - (Clock.Now - t0);
                if (wait > 0.001) pacer.Wait(wait, wake);
            }
            catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
            {
                Log.Warn("lcd: " + e.Message);
                // The port took no data for 2 s: the firmware is stuck, so the next connection reboots it first.
                if (e is TimeoutException) resetNext = true;
                Disconnect(e.Message);
                nextTry = Clock.Now + 2;
            }
            catch (Exception e)
            {
                Log.Once("engine " + e.GetType().Name + ": " + e.Message, e);
                Thread.Sleep(500);
            }
        }
        try { if (lcd.IsOpen) { lcd.SetBrightness(0); lcd.ScreenOff(); } } catch { }
        lcd.Close();
        renderer?.Dispose();
        introFrame?.Dispose();
    }

    void Backlight(int pct)
    {
        if (pct == level) return;
        lcd.SetBrightness(pct);
        level = pct;
    }

    /// Dark, then draw. The card only shows on the first connection after Ember starts.
    void Begin(double now)
    {
        Backlight(0);
        intro = introDue;
        introDue = false;
        phase = 1;
        phaseAt = now;
    }

    void Sequence(double now, bool pending)
    {
        switch (phase)
        {
            case 1 when !pending || now - phaseAt > DrawTimeout:
                phase = 2; phaseAt = now;
                break;
            case 2:
                double k = Math.Min(1, (now - phaseAt) / FadeIn);
                Backlight((int)Math.Round(cfg.Brightness * Ease(k)));
                if (k >= 1) { phase = intro ? 3 : 0; phaseAt = now; shownAt = now; }
                break;
            case 3 when now - phaseAt >= Hold:
                phase = 4; phaseAt = now;
                break;
            case 4:
                double j = Math.Min(1, (now - phaseAt) / FadeOut);
                Backlight((int)Math.Round(cfg.Brightness * (1 - Ease(j))));
                if (j >= 1)
                {
                    intro = false;
                    if (switchTo != null) { screen = switchTo; switchTo = null; }
                    phase = 1; phaseAt = now;
                }
                break;
        }
    }

    static double Ease(double k) => k * k * (3 - 2 * k);

    /// The theme for ThemeSecs, then each app in use for AppSecs. On the panel every switch fades through black,
    /// like start-up: the link needs a couple of seconds for a whole new screen and the dark hides the redraw.
    void Rotate(Snapshot s, double now, bool onPanel)
    {
        if (switchTo != null) { screen = switchTo; switchTo = null; shownAt = now; } // the fade was cut short
        apps.Clear();
        if (!HoldTheme && cfg.ClaudeApp && s.Get("claude.active") > 0) apps.Add("claude");
        if (!HoldTheme && cfg.SpotifyApp && s.Get("spotify.active") > 0) apps.Add("spotify");
        var next = Rotation.Next(screen, now - shownAt, apps, Math.Max(3, cfg.ThemeSecs), Math.Max(3, cfg.AppSecs));
        if (next == screen) return;
        Log.Info($"screen: {next}");
        if (onPanel) { switchTo = next; phase = 4; phaseAt = now; }
        else { screen = next; shownAt = now; }
    }

    Theme Showing()
    {
        var id = screen;
        if (id == "theme") return theme;
        // Rebuilt when the theme changes (the screen wears its colours) or a new cover arrives.
        if (appScreen == null || appFor != id || appLook != theme || appArt != AppWatch.ArtVersion)
        {
            appArt = AppWatch.ArtVersion;
            appScreen = Themes.Screen(id, theme);
            appFor = id;
            appLook = theme;
        }
        return appScreen;
    }

    void Connect()
    {
        var ports = cfg.Port == "auto" ? TuringLcd.FindPorts() : [cfg.Port];
        if (ports.Count == 0) { Status = L.T("No screen found. Check the USB cable."); Connected = false; Searching = true; return; }
        string err = "";
        foreach (var port in ports)
        {
            try
            {
                lcd.Open(port);
                if (resetNext)
                {
                    resetNext = false;
                    Log.Warn($"resetting panel on {port}");
                    try { lcd.Reset(); } catch (Exception e) { Log.Warn("reset: " + e.Message); }
                    lcd.Close();
                    resumeAt = Clock.Now + 6;
                    Status = L.T("Restarting the screen…");
                    return;
                }
                level = -1;
                Backlight(0);
                lcd.ScreenOn();
                lcd.SetOrientation(cfg.Flip);
                appliedFlip = cfg.Flip;
                lcdDark = false;
                diff.Invalidate();
                Begin(Clock.Now);
                Connected = true;
                Searching = false;
                Port = port;
                Status = L.F("Connected on {0}", port);
                Log.Info($"connected {port}");
                return;
            }
            catch (Exception e) { err = e.Message; lcd.Close(); }
        }
        Connected = false;
        Searching = false;
        Status = err;
    }

    void Disconnect(string why)
    {
        lcd.Close();
        Connected = false;
        Searching = false;
        phase = 0;
        intro = false;
        Status = why;
        diff.Invalidate();
    }

    void ApplyControls()
    {
        if (appliedFlip != cfg.Flip)
        {
            lcd.SetOrientation(cfg.Flip);
            appliedFlip = cfg.Flip;
            diff.Invalidate();
        }
        if (phase == 0) Backlight(cfg.Brightness);
    }
}

/// Frame pacing with a high-resolution waitable timer: the default 15.6 ms system tick would turn 33 ms frames
/// into an uneven 31/47 ms rhythm. Falls back to a plain wait where the flag is not supported.
sealed class Pacer
{
    readonly WaitHandle? timer;

    public Pacer()
    {
        var h = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, 0x2 /* HIGH_RESOLUTION */, 0x1F0003);
        if (h != IntPtr.Zero) timer = new TimerHandle(h);
    }

    public void Wait(double seconds, WaitHandle wake)
    {
        if (timer == null) { wake.WaitOne(TimeSpan.FromSeconds(seconds)); return; }
        long due = -(long)(seconds * 10_000_000);
        if (!SetWaitableTimerEx(timer.SafeWaitHandle, ref due, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0)) { wake.WaitOne(TimeSpan.FromSeconds(seconds)); return; }
        WaitHandle.WaitAny([timer, wake]);
    }

    sealed class TimerHandle : WaitHandle
    {
        public TimerHandle(IntPtr h) => SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(h, true);
    }

    [DllImport("kernel32", SetLastError = true)] static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, IntPtr name, uint flags, uint access);
    [DllImport("kernel32", SetLastError = true)] static extern bool SetWaitableTimerEx(Microsoft.Win32.SafeHandles.SafeWaitHandle h, ref long due, int period, IntPtr completion, IntPtr arg, IntPtr wakeContext, uint tolerableDelay);
}

/// Per-channel curves for panels that come out too blue or too contrasty: a gain per channel, a shared gamma
/// and a warm/cool shift. Only the panel gets them; the window preview shows the theme as designed.
public static class ColorFix
{
    /// R, G and B tables (768 bytes), or null when the settings change nothing.
    public static byte[]? Lut(Settings cfg)
    {
        double t = Math.Clamp(cfg.ColorTemp, -50, 50) / 50.0; // +1 warmest, -1 coolest
        double gr = Math.Clamp(cfg.GainR, 0, 100) / 100.0 * (t < 0 ? 1 + 0.3 * t : 1);
        double gg = Math.Clamp(cfg.GainG, 0, 100) / 100.0 * (t > 0 ? 1 - 0.08 * t : 1 + 0.04 * t);
        double gb = Math.Clamp(cfg.GainB, 0, 100) / 100.0 * (t > 0 ? 1 - 0.3 * t : 1);
        double gamma = Math.Clamp(cfg.Gamma, 0.5, 2.0);
        if (Math.Abs(gr - 1) < 1e-3 && Math.Abs(gg - 1) < 1e-3 && Math.Abs(gb - 1) < 1e-3 && Math.Abs(gamma - 1) < 1e-3) return null;
        var lut = new byte[768];
        for (int i = 0; i < 256; i++)
        {
            double v = Math.Pow(i / 255.0, gamma);
            lut[i] = (byte)Math.Round(255 * Math.Clamp(v * gr, 0, 1));
            lut[256 + i] = (byte)Math.Round(255 * Math.Clamp(v * gg, 0, 1));
            lut[512 + i] = (byte)Math.Round(255 * Math.Clamp(v * gb, 0, 1));
        }
        return lut;
    }
}
