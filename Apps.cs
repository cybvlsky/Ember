using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using SkiaSharp;
using Windows.Media.Control;

namespace Ember;

/// The apps that can take over the panel for a while: Claude Code (usage from its own session logs) and Spotify
/// (whatever the Windows media controls say is playing). Polled on its own thread; Sensors copies the values into
/// every snapshot so themes and app screens read them like any other sensor.
public sealed class AppWatch : IDisposable
{
    /// The current album cover. Replaced, never disposed, so a frame being drawn keeps a valid bitmap.
    public static volatile SKBitmap? Art;
    public static volatile int ArtVersion;
    public static SKColor? ArtColor;

    public volatile IReadOnlyDictionary<string, double> Values = new Dictionary<string, double>();
    public volatile IReadOnlyDictionary<string, string> Strings = new Dictionary<string, string>();

    readonly Settings cfg;
    readonly ClaudeUsage claude = new();
    readonly MediaWatch media = new();
    readonly AutoResetEvent wake = new(false);
    Thread? thread;
    volatile bool stop;

    public AppWatch(Settings cfg) => this.cfg = cfg;

    /// Copies the latest app readings into a sensor snapshot.
    public void Merge(Snapshot s)
    {
        foreach (var (k, x) in Values) s.V[k] = x;
        foreach (var (k, x) in Strings) s.S[k] = x;
    }

    public void Start()
    {
        if (Sensors.Demo) { Art = DemoArt(); ArtColor = Dominant(Art); ArtVersion++; return; }
        thread = new Thread(Loop) { IsBackground = true, Name = "Apps", Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }

    public void Dispose()
    {
        stop = true;
        wake.Set();
        thread?.Join(2000);
    }

    /// The theme in the editor: its app values are read too, so the canvas shows real ones.
    public static volatile Theme? Editing;

    /// Whether a theme shows Claude or Spotify values, so they are read for it even with the app screens off.
    public static (bool Claude, bool Spotify) Uses(Theme? t)
    {
        bool c = false, sp = false;
        if (t != null)
            foreach (var w in t.Widgets.ToArray()) // the editor's draft changes under us
            {
                var all = $"{w.Sensor} {w.Text} {w.Src}";
                c |= all.Contains("claude.");
                sp |= all.Contains("spotify.") || w.Src == "@art";
            }
        return (c, sp);
    }

    void Loop()
    {
        double nextScan = 0;
        while (!stop)
        {
            var v = new Dictionary<string, double>();
            var s = new Dictionary<string, string>();
            var (onTheme, onTheme2) = Uses(App.Engine?.Theme);
            var (inEditor, inEditor2) = Uses(Editing);
            bool wantClaude = cfg.ClaudeApp || onTheme || inEditor, wantSpotify = cfg.SpotifyApp || onTheme2 || inEditor2;
            try
            {
                if (wantClaude)
                {
                    // The logs are read every few seconds; the countdowns are recomputed every second from what was read.
                    if (Clock.Now >= nextScan)
                    {
                        bool first = nextScan == 0;
                        var t0 = Clock.Now;
                        claude.Scan();
                        nextScan = Clock.Now + 5;
                        if (first) Log.Info($"claude: {claude.Uses.Count} responses in the logs, read in {Clock.Now - t0:0.0} s");
                    }
                    claude.Publish(v, s, cfg, DateTime.UtcNow);
                }
            }
            catch (Exception e) { Log.Once("claude " + e.GetType().Name + ": " + e.Message, e); }
            try { if (wantSpotify) media.Poll(v, s); }
            catch (Exception e) { Log.Once("media " + e.GetType().Name + ": " + e.Message, e); }
            Values = v;
            Strings = s;
            wake.WaitOne(1000);
        }
    }

    /// Readings for screenshots and previews (Snapshot.Demo).
    public static void Demo(Snapshot s)
    {
        void V(string k, double x) => s.V[k] = x;
        V("claude.active", 1); V("claude.busy", 1); V("claude.sessions", 2);
        V("claude.block.cost", 6.84); V("claude.block.tokens", 9.4e6); V("claude.block.left", 2 * 3600 + 14 * 60);
        V("claude.block.elapsed", 55); V("claude.block.pct", 46);
        V("claude.today.cost", 11.92); V("claude.today.tokens", 18.2e6); V("claude.month.cost", 212.4);
        V("claude.plan", 100); V("claude.value", 2.1);
        s.S["claude.reset"] = "18:40";
        s.S["claude.resetText"] = L.F("resets at {0:HH:mm}", new DateTime(2026, 1, 1, 18, 40, 0));
        s.S["claude.limit"] = L.T("estimated from the last limit hit");
        s.S["claude.plan.name"] = "Max 5x";
        s.S["claude.valueText"] = L.F("{0:0.0}× the {1} price", 2.1, "Max 5x");
        s.S["claude.model"] = "Opus 5.5";
        s.S["claude.status"] = L.T("working");
        V("spotify.active", 1); V("spotify.playing", 1); V("spotify.pos", 83); V("spotify.dur", 227); V("spotify.progress", 83 / 2.27);
        s.S["spotify.title"] = "Long Way Home";
        s.S["spotify.artist"] = "Night Signals";
        s.S["spotify.album"] = "Low Tide Radio";
        s.S["spotify.state"] = L.T("Now playing");
    }

    /// A made-up cover for screenshots and the editor, so no real artwork ends up in them.
    public static SKBitmap DemoArt()
    {
        var bmp = new SKBitmap(300, 300, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var c = new SKCanvas(bmp);
        using (var bg = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(300, 300), new[] { new SKColor(0xE8, 0x5D, 0x3A), new SKColor(0x6B, 0x2D, 0x8C) }, null, SKShaderTileMode.Clamp) })
            c.DrawRect(0, 0, 300, 300, bg);
        using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 18, Color = new SKColor(255, 255, 255, 70) };
        c.DrawCircle(210, 96, 54, ring);
        using var dot = new SKPaint { IsAntialias = true, Color = new SKColor(255, 220, 160) };
        c.DrawCircle(92, 214, 34, dot);
        return bmp;
    }

    /// A light, saturated colour from the cover for progress bars and the background wash.
    /// Null for grey covers: the screen then uses the theme's accent.
    public static SKColor? Dominant(SKBitmap bmp)
    {
        using var small = bmp.Resize(new SKImageInfo(16, 16), new SKSamplingOptions(SKFilterMode.Linear));
        double r = 0, g = 0, b = 0, sum = 0;
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                var px = small.GetPixel(x, y);
                px.ToHsv(out _, out float sat, out float val); // Skia: s and v are 0-100
                double wgt = sat * sat * val / 1e6 + 0.001;
                r += px.Red * wgt; g += px.Green * wgt; b += px.Blue * wgt; sum += wgt;
            }
        var c = new SKColor((byte)(r / sum), (byte)(g / sum), (byte)(b / sum));
        c.ToHsv(out float h, out float s, out float v);
        return s < 15 ? null : SKColor.FromHsv(h, Math.Min(s, 70), Math.Max(v, 82));
    }
}

/// Picks what the panel shows next. `available`: app screens that can be shown (switched on, with something to show),
/// `active`: those in use right now, both in screen order. The shortcut steps theme > each available app > theme;
/// automatic rotation shows the theme for `themeSecs`, then each active app for `appSecs`.
public static class Rotation
{
    public static string Pick(string current, double held, bool step, bool auto, List<string> available, List<string> active, double themeSecs, double appSecs)
    {
        if (step)
        {
            int i = current == "theme" ? -1 : available.IndexOf(current);
            return i + 1 < available.Count ? available[i + 1] : "theme";
        }
        if (current != "theme" && !available.Contains(current)) return "theme";
        if (!auto) return current;
        if (current == "theme") return active.Count > 0 && held >= themeSecs ? active[0] : "theme";
        if (held < appSecs) return current;
        // A screen picked by hand need not be active; carry on with the active ones after it.
        int k = available.IndexOf(current);
        foreach (var a in active) if (available.IndexOf(a) > k) return a;
        return "theme";
    }
}

/// Reads Claude Code's own session logs (~/.claude/projects/*/*.jsonl, append-only) and prices every response at
/// API rates, the way usage trackers for Claude Code do. Nothing leaves the machine and no credentials are read.
public sealed class ClaudeUsage
{
    public readonly record struct Use(DateTime At, string Model, long Input, long Output, long Write5m, long Write1h, long Read, double Cost)
    {
        public long Tokens => Input + Output + Write5m + Write1h + Read;
    }

    public static readonly (string Id, string Name, double Price)[] Plans = [("pro", "Pro", 20), ("max5", "Max 5x", 100), ("max20", "Max 20x", 200)];
    static readonly TimeSpan Window = TimeSpan.FromHours(5);

    readonly Dictionary<string, Use> byId = new();
    readonly Dictionary<string, long> offsets = new();
    readonly List<(DateTime At, DateTime Resets)> limits = new();
    /// Five-hour windows open on a 10-minute boundary: both limits in the author's logs reset at :10 past.
    static DateTime Floor10(DateTime t) => new(t.Ticks - t.Ticks % (TimeSpan.TicksPerMinute * 10), t.Kind);
    List<Use> uses = new();
    int sessions;
    bool busy, desktop;

    public IReadOnlyList<Use> Uses => uses;

    public static string Root => Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } d
        ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    /// Per million tokens: input, output, cache read, 5-minute cache write, 1-hour cache write.
    public static (double In, double Out, double Read, double W5, double W1) Price(string model)
    {
        var m = model.ToLowerInvariant();
        if (m.Contains("fable") || m.Contains("mythos")) return (10, 50, m.Contains("5-1") ? 0.25 : 1, 12.5, 20);
        if (m.Contains("opus-5-5")) return (4, 20, 0.2, 5, 8);
        if (m.Contains("opus")) return (5, 25, 0.5, 6.25, 10);
        if (m.Contains("sonnet-5")) return (2, 10, 0.2, 2.5, 4);
        if (m.Contains("sonnet")) return (3, 15, 0.3, 3.75, 6);
        if (m.Contains("haiku-5")) return (0.1, 0.5, 0.01, 0.125, 0.2);
        if (m.Contains("haiku")) return (1, 5, 0.1, 1.25, 2);
        return (4, 20, 0.2, 5, 8); // unknown or newer: priced like the current Opus
    }

    /// "claude-opus-5-5" -> "Opus 5.5", "claude-sonnet-4-5-20250929" -> "Sonnet 4.5".
    public static string Pretty(string model)
    {
        var parts = model.Split('-', StringSplitOptions.RemoveEmptyEntries).Where(p => p != "claude" && !(p.Length >= 8 && p.All(char.IsDigit))).ToList();
        if (parts.Count == 0) return model;
        var name = char.ToUpperInvariant(parts[0][0]) + parts[0][1..];
        var ver = string.Join(".", parts.Skip(1).TakeWhile(p => p.All(char.IsDigit)));
        return ver.Length > 0 ? name + " " + ver : name;
    }

    public void Scan()
    {
        var dir = Path.Combine(Root, "projects");
        if (Directory.Exists(dir))
        {
            var since = DateTime.UtcNow.AddDays(-40);
            bool added = false;
            foreach (var f in Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.AllDirectories))
            {
                var info = new FileInfo(f);
                if (info.LastWriteTimeUtc < since) continue;
                offsets.TryGetValue(f, out long off);
                if (info.Length < off) off = 0; // replaced: read it again
                if (info.Length == off) continue;
                added |= ReadFrom(f, off);
            }
            if (added) Rebuild();
        }
        ReadSessions();
    }

    public void Rebuild() => uses = byId.Values.OrderBy(u => u.At).ToList();

    bool ReadFrom(string file, long off)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        fs.Seek(off, SeekOrigin.Begin);
        var buf = new byte[1 << 20];
        int len = 0;
        bool added = false;
        for (int n; (n = fs.Read(buf, len, buf.Length - len)) > 0;)
        {
            len += n;
            int start = 0;
            for (int nl; (nl = Array.IndexOf(buf, (byte)'\n', start, len - start)) >= 0; start = nl + 1)
                added |= Line(buf.AsMemory(start, nl - start));
            // Only whole lines count: the last one may still be being written.
            off += start;
            Buffer.BlockCopy(buf, start, buf, 0, len - start);
            len -= start;
            if (len == buf.Length) Array.Resize(ref buf, buf.Length * 2);
        }
        offsets[file] = off;
        return added;
    }

    /// One log line. Returns true when it added or improved a priced response.
    public bool Line(ReadOnlyMemory<byte> line)
    {
        bool limit = line.Span.IndexOf("\"rate_limit_info\""u8) >= 0;
        if (!limit && line.Span.IndexOf("\"usage\""u8) < 0) return false;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("timestamp", out var ts) || !DateTime.TryParse(ts.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var at)) return false;
            if (limit && root.TryGetProperty("apiErrorParams", out var ap) && ap.TryGetProperty("rate_limit_info", out var rl)
                && rl.TryGetProperty("rateLimitType", out var type) && type.GetString() == "five_hour" && rl.TryGetProperty("resetsAt", out var resets))
            {
                var r = DateTime.UnixEpoch.AddSeconds(resets.GetInt64());
                if (!limits.Contains((at, r))) limits.Add((at, r));
            }
            if (!root.TryGetProperty("message", out var msg) || !msg.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return false;
            var model = msg.TryGetProperty("model", out var mo) ? mo.GetString() ?? "" : "";
            if (model.Length == 0 || model.StartsWith('<')) return false; // "<synthetic>" messages cost nothing
            long Get(JsonElement e, string k) => e.TryGetProperty(k, out var x) && x.ValueKind == JsonValueKind.Number ? x.GetInt64() : 0;
            long input = Get(u, "input_tokens"), output = Get(u, "output_tokens"), read = Get(u, "cache_read_input_tokens"), write = Get(u, "cache_creation_input_tokens");
            long w1 = 0, w5 = write;
            if (u.TryGetProperty("cache_creation", out var cc) && cc.ValueKind == JsonValueKind.Object)
            {
                w1 = Get(cc, "ephemeral_1h_input_tokens");
                w5 = Get(cc, "ephemeral_5m_input_tokens");
                if (w1 + w5 == 0) w5 = write;
            }
            var p = Price(model);
            double cost = (input * p.In + output * p.Out + read * p.Read + w5 * p.W5 + w1 * p.W1) / 1e6;
            if (u.TryGetProperty("speed", out var sp) && sp.GetString() == "fast") cost *= 2;
            // Streaming writes one line per chunk with the same ids; keep the most complete.
            string id = (msg.TryGetProperty("id", out var mid) ? mid.GetString() : null) + "|" + (root.TryGetProperty("requestId", out var rq) ? rq.GetString() : null);
            if (id == "|") id = at.Ticks.ToString();
            var use = new Use(at, model, input, output, w5, w1, read, cost);
            if (byId.TryGetValue(id, out var old) && old.Output >= output) return false;
            byId[id] = use;
            return true;
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException) { return false; }
    }

    void ReadSessions()
    {
        int n = 0;
        bool b = false;
        var dir = Path.Combine(Root, "sessions");
        if (Directory.Exists(dir))
            foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(f));
                    var r = doc.RootElement;
                    if (!r.TryGetProperty("pid", out var pid) || !Alive(pid.GetInt32())) continue;
                    n++;
                    b |= r.TryGetProperty("status", out var st) && st.GetString() == "busy";
                }
                catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException) { }
            }
        sessions = n;
        busy = b;
        // The desktop app shares the subscription limits, so it counts as Claude being in use too.
        desktop = Process.GetProcessesByName("claude").Any(p =>
        {
            try { return p.MainModule?.FileName.Contains("AnthropicClaude", StringComparison.OrdinalIgnoreCase) == true; }
            catch { return false; }
            finally { p.Dispose(); }
        });
    }

    static bool Alive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    public void Publish(Dictionary<string, double> v, Dictionary<string, string> s, Settings cfg, DateTime nowUtc)
    {
        var list = uses;
        bool recent = list.Count > 0 && nowUtc - list[^1].At < TimeSpan.FromMinutes(10);
        v["claude.active"] = sessions > 0 || desktop || recent ? 1 : 0;
        v["claude.busy"] = busy ? 1 : 0;
        v["claude.sessions"] = sessions;

        // Five-hour windows: the first message after one closed opens the next. A limit hit says exactly when its
        // window ends and roughly what a window holds: the API price spent in it until then.
        // ponytail: claude.ai and phone use share the limit but are not in these logs, so this is an estimate.
        DateTime start = DateTime.MinValue, end = DateTime.MinValue;
        double cost = 0, budget = double.NaN;
        long tokens = 0;
        var hits = limits.OrderBy(l => l.At).ToList();
        int hi = 0;
        void Hits(DateTime until)
        {
            for (; hi < hits.Count && hits[hi].At <= until; hi++)
                if (hits[hi].At < end) { budget = cost; end = hits[hi].Resets; start = end - Window; }
        }
        foreach (var u in list)
        {
            Hits(u.At);
            if (u.At >= end) { start = Floor10(u.At); end = start + Window; cost = 0; tokens = 0; }
            cost += u.Cost;
            tokens += u.Tokens;
        }
        Hits(DateTime.MaxValue);
        bool open = nowUtc < end;
        if (!open) { cost = 0; tokens = 0; }
        double pct = budget > 0 ? Math.Min(100, cost / budget * 100) : double.NaN;
        v["claude.block.cost"] = cost;
        v["claude.block.tokens"] = tokens;
        v["claude.block.left"] = open ? (end - nowUtc).TotalSeconds : double.NaN;
        v["claude.block.elapsed"] = open ? Math.Clamp((nowUtc - start).TotalSeconds / Window.TotalSeconds * 100, 0, 100) : 0;
        v["claude.block.pct"] = pct;
        var blocked = hits.Count > 0 && hits[^1].Resets > nowUtc ? hits[^1].Resets.ToLocalTime() : (DateTime?)null;
        s["claude.reset"] = open ? end.ToLocalTime().ToString("HH:mm", L.Culture) : "--:--";
        s["claude.resetText"] = open ? L.F("resets at {0:HH:mm}", end.ToLocalTime()) : L.T("no window open");
        s["claude.limit"] = blocked is { } back ? L.F("Limit reached until {0:HH:mm}", back)
            : !double.IsNaN(pct) ? L.T("estimated from the last limit hit")
            : L.T("measured after the first limit hit");

        // Day and month in local time; the month is what the plan price compares against.
        var now = nowUtc.ToLocalTime();
        var dayStart = now.Date.ToUniversalTime();
        var monthStart = new DateTime(now.Year, now.Month, 1).ToUniversalTime();
        double day = 0, month = 0;
        long dayTok = 0;
        for (int i = list.Count - 1; i >= 0 && list[i].At >= monthStart; i--)
        {
            month += list[i].Cost;
            if (list[i].At >= dayStart) { day += list[i].Cost; dayTok += list[i].Tokens; }
        }
        var plan = Plans.FirstOrDefault(p => p.Id == cfg.ClaudePlan);
        if (plan.Id == null) plan = Plans[0];
        v["claude.today.cost"] = day;
        v["claude.today.tokens"] = dayTok;
        v["claude.month.cost"] = month;
        v["claude.plan"] = plan.Price;
        v["claude.value"] = month / plan.Price;
        s["claude.plan.name"] = plan.Name;
        s["claude.valueText"] = L.F("{0:0.0}× the {1} price", month / plan.Price, plan.Name);
        s["claude.model"] = list.Count > 0 ? Pretty(list[^1].Model) : "Claude";
        s["claude.status"] = busy ? L.T("working") : sessions > 0 ? L.T("waiting") : desktop ? L.T("desktop app") : L.T("idle");
    }
}

/// Spotify through the Windows media controls (the same source as the volume flyout). Track change loads the cover.
sealed class MediaWatch
{
    GlobalSystemMediaTransportControlsSessionManager? mgr;
    string? track;
    double pausedAt = double.NaN;

    public void Poll(Dictionary<string, double> v, Dictionary<string, string> s)
    {
        mgr ??= GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().GetAwaiter().GetResult();
        var session = mgr.GetSessions().FirstOrDefault(x => x.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase));
        v["spotify.active"] = 0;
        if (session == null) return;
        var props = session.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult();
        if (string.IsNullOrWhiteSpace(props.Title)) return;
        v["spotify.present"] = 1;
        bool playing = session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        // A paused track stays in the rotation for two minutes, then the theme has the panel back.
        if (playing) pausedAt = double.NaN;
        else if (double.IsNaN(pausedAt)) pausedAt = Clock.Now;
        bool show = playing || Clock.Now - pausedAt < 120;

        var tl = session.GetTimelineProperties();
        double dur = (tl.EndTime - tl.StartTime).TotalSeconds;
        double pos = tl.Position.TotalSeconds;
        if (playing) pos += (DateTimeOffset.Now - tl.LastUpdatedTime).TotalSeconds;
        pos = Math.Clamp(pos, 0, dur > 0 ? dur : pos);

        string key = props.Title + "\n" + props.Artist + "\n" + props.AlbumTitle;
        if (key != track)
        {
            track = key;
            LoadArt(props);
        }
        v["spotify.active"] = show ? 1 : 0;
        v["spotify.playing"] = playing ? 1 : 0;
        v["spotify.pos"] = pos;
        v["spotify.dur"] = dur > 0 ? dur : double.NaN;
        v["spotify.progress"] = dur > 0 ? pos / dur * 100 : double.NaN;
        s["spotify.title"] = props.Title;
        s["spotify.artist"] = props.Artist;
        s["spotify.album"] = props.AlbumTitle;
        s["spotify.state"] = playing ? L.T("Now playing") : L.T("Paused");
    }

    static void LoadArt(GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        SKBitmap? bmp = null;
        try
        {
            if (props.Thumbnail != null)
            {
                using var stream = props.Thumbnail.OpenReadAsync().AsTask().GetAwaiter().GetResult().AsStreamForRead();
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                ms.Position = 0;
                bmp = SKBitmap.Decode(ms);
            }
        }
        catch (Exception e) { Log.Once("cover " + e.GetType().Name, e); }
        AppWatch.ArtColor = bmp != null ? AppWatch.Dominant(bmp) : null;
        AppWatch.Art = bmp;
        AppWatch.ArtVersion++;
    }
}
