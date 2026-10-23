using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkiaSharp;

namespace Ember;

/// A layout for the 480x320 screen: a palette plus a z-ordered list of widgets. Stored as JSON.
public sealed class Theme
{
    public string Name { get; set; } = "Nowy motyw";
    public string? Description { get; set; }
    public string Font { get; set; } = "Segoe UI Variable Display";
    public string Bg { get; set; } = "#0A0D12";
    public Dictionary<string, string> Palette { get; set; } = new();
    public List<Widget> Widgets { get; set; } = new();

    [JsonIgnore] public string Id { get; set; } = "";
    [JsonIgnore] public bool BuiltIn { get; set; }

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static Theme Parse(string json) => JsonSerializer.Deserialize<Theme>(json, Json) ?? throw new InvalidDataException("Pusty motyw");
    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public Theme Clone() { var t = Parse(ToJson()); t.Id = Id; t.BuiltIn = BuiltIn; return t; }
}

/// One element. Which properties matter depends on Type:
/// rect, glow, text, icon, image, bar, gauge, graph, cores. Null = default for that type.
public sealed class Widget
{
    public string Type { get; set; } = "text";
    public string? Name { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float W { get; set; }
    public float H { get; set; }
    public string? Sensor { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public string? Text { get; set; }
    public string? Font { get; set; }
    public float? Size { get; set; }
    public int? Weight { get; set; }
    public int? Stretch { get; set; }
    public string? Align { get; set; }
    public string? Color { get; set; }
    public string[]? Colors { get; set; }
    public float? Angle { get; set; }
    public string? Track { get; set; }
    public string? Stroke { get; set; }
    public float? StrokeWidth { get; set; }
    public string? Shadow { get; set; }
    public float? Radius { get; set; }
    public float? Thickness { get; set; }
    public float? Start { get; set; }
    public float? Sweep { get; set; }
    public float? Glow { get; set; }
    public float? Opacity { get; set; }
    public double? Warn { get; set; }
    public double? Crit { get; set; }
    public string? Mode { get; set; }
    public int? Segments { get; set; }
    public float? Gap { get; set; }
    public int? Points { get; set; }
    public string? Glyph { get; set; }
    public string? Src { get; set; }
    public float? Spacing { get; set; }
    public float? Smooth { get; set; }
    public bool? Locked { get; set; }

    public bool Has(string flag) => Mode != null && Mode.Split(' ', ',').Contains(flag);
}

public static class Themes
{
    public static string UserDir => Path.Combine(App.DataDir, "themes");

    public static List<Theme> LoadAll()
    {
        var list = new List<Theme>();
        var asm = Assembly.GetExecutingAssembly();
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith("theme.")).OrderBy(n => n))
        {
            try
            {
                using var r = new StreamReader(asm.GetManifestResourceStream(res)!);
                var t = Theme.Parse(r.ReadToEnd());
                t.Id = res[6..^5];
                t.BuiltIn = true;
                list.Add(t);
            }
            catch (Exception e) { Log.Error($"theme {res}", e); }
        }
        // Built-in order: whatever "order" the file names give, but the signature theme first.
        list.Sort((a, b) => a.Id == "ember" ? -1 : b.Id == "ember" ? 1 : string.CompareOrdinal(a.Id, b.Id));
        if (Directory.Exists(UserDir))
            foreach (var f in Directory.GetFiles(UserDir, "*.json").OrderBy(f => f))
            {
                try
                {
                    var t = Theme.Parse(File.ReadAllText(f));
                    t.Id = Path.GetFileNameWithoutExtension(f);
                    list.RemoveAll(x => x.Id == t.Id);
                    list.Add(t);
                }
                catch (Exception e) { Log.Error($"theme {f}", e); }
            }
        return list;
    }

    public static void Save(Theme t)
    {
        Directory.CreateDirectory(UserDir);
        App.WriteAtomic(Path.Combine(UserDir, t.Id + ".json"), t.ToJson());
        t.BuiltIn = false;
    }

    public static void Delete(Theme t)
    {
        var f = Path.Combine(UserDir, t.Id + ".json");
        if (!t.BuiltIn && File.Exists(f)) File.Delete(f);
    }

    public static string NewId(string name, IEnumerable<Theme> existing)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? (ch == 'ł' ? 'l' : ch) : '-');
        }
        var baseId = string.Join("-", sb.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (baseId.Length == 0) baseId = "motyw";
        var ids = existing.Select(t => t.Id).ToHashSet();
        var id = baseId;
        for (int i = 2; ids.Contains(id); i++) id = $"{baseId}-{i}";
        return id;
    }
}

/// Draws a theme with SkiaSharp. One instance per theme version; holds animation state.
/// Bandwidth to the panel is ~80k pixels/s, so everything here is designed to change as few pixels as possible:
/// static widgets are cached, digits are tabular so "41" -> "42" only touches one glyph, graphs can sweep instead of scroll.
public sealed class Renderer : IDisposable
{
    public const int W = TuringLcd.Width, H = TuringLcd.Height;
    public static readonly CultureInfo Pl = CultureInfo.GetCultureInfo("pl-PL");

    readonly Theme th;
    readonly Widget[] ws;
    readonly State[] st;
    readonly int firstDynamic;
    readonly SKColor bg;
    SKBitmap? staticLayer;
    double now;
    DateTime clock;
    public bool Animating { get; private set; }
    public Theme Theme => th;
    public static int Errors;

    sealed class State
    {
        public double V = double.NaN, C, AutoMax = double.NaN;
        public double[]? Vs;
        public List<(string? Lit, string? Key, string? Fmt)>? Tpl;
        public string? LastText;
        public float LastSize;
        public SKTextBlob? Blob;
        public float TextW;
        public SKRect Bounds;
        public SKBitmap? Img;
        public bool ImgTried;
    }

    public Renderer(Theme theme)
    {
        th = theme;
        ws = theme.Widgets.ToArray();
        st = ws.Select(w => new State { Tpl = w.Type == "text" && w.Text?.Contains('{') == true ? ParseTpl(w.Text) : null }).ToArray();
        firstDynamic = Array.FindIndex(ws, IsDynamic);
        if (firstDynamic < 0) firstDynamic = ws.Length;
        bg = Col(theme.Bg, SKColors.Black).WithAlpha(255);
    }

    static bool IsDynamic(Widget w) => w.Sensor != null || w.Type is "graph" or "cores" or "bar" or "gauge" || (w.Text?.Contains('{') ?? false) || w.Has("blink");

    public void Dispose()
    {
        staticLayer?.Dispose();
        foreach (var s in st) { s.Blob?.Dispose(); s.Img?.Dispose(); }
    }

    public void Render(SKCanvas c, Snapshot s, double dt, double now)
    {
        Animating = false;
        this.now = now;
        clock = DateTime.Now;
        if (staticLayer == null)
        {
            staticLayer = new SKBitmap(W, H, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var sc = new SKCanvas(staticLayer);
            sc.Clear(bg);
            for (int i = 0; i < firstDynamic; i++) Draw(sc, i, Snapshot.Empty, 0);
        }
        c.DrawBitmap(staticLayer, 0, 0);
        for (int i = firstDynamic; i < ws.Length; i++) Draw(c, i, s, dt);
    }

    /// Screen-space box of widget i as last drawn (text measures itself), for the editor.
    public SKRect Bounds(int i) => i < st.Length && !st[i].Bounds.IsEmpty ? st[i].Bounds : i < ws.Length ? SKRect.Create(ws[i].X, ws[i].Y, Math.Max(ws[i].W, 4), Math.Max(ws[i].H, 4)) : SKRect.Empty;

    void Draw(SKCanvas c, int i, Snapshot s, double dt)
    {
        var w = ws[i];
        var state = st[i];
        try
        {
            switch (w.Type)
            {
                case "rect": DrawRect(c, w, state); break;
                case "glow": DrawGlow(c, w, state); break;
                case "text": DrawText(c, w, state, s, dt); break;
                case "icon": DrawIcon(c, w, state, s, dt); break;
                case "image": DrawImage(c, w, state); break;
                case "bar": DrawBar(c, w, state, s, dt); break;
                case "gauge": DrawGauge(c, w, state, s, dt); break;
                case "graph": DrawGraph(c, w, state, s, dt); break;
                case "cores": DrawCores(c, w, state, s, dt); break;
            }
        }
        catch (Exception e) { Errors++; Log.Once($"draw {th.Id}/{i}/{w.Type}", e); }
    }

    // ---------- values, colors, animation ----------

    static bool Continuous(string? key) => key is "time.sec" or "time.min" or "time.hour";

    double Value(string? key, Snapshot s) => key switch
    {
        null => double.NaN,
        "time.sec" => clock.Second + clock.Millisecond / 1000.0,
        "time.min" => clock.Minute + clock.Second / 60.0,
        "time.hour" => clock.Hour % 12 + clock.Minute / 60.0,
        _ => s.Get(key),
    };

    double Tween(ref double cur, double target, Widget w, double dt, double range, bool snap)
    {
        if (double.IsNaN(target)) return cur = double.NaN;
        if (double.IsNaN(cur)) cur = w.Min ?? 0;
        double tau = w.Smooth ?? 0.35;
        if (snap || tau <= 0 || dt <= 0) return cur = target;
        cur += (target - cur) * (1 - Math.Exp(-dt / tau));
        if (Math.Abs(target - cur) <= Math.Abs(range) * 0.0015) cur = target;
        else Animating = true;
        return cur;
    }

    /// Normalised, animated value. `px` is how many pixels a full-scale move covers: the result snaps to half pixels,
    /// so the long tail of an easing curve (and slow continuous motion) does not resend the same region every frame.
    double Frac(Widget w, State state, Snapshot s, double dt, double px, out double raw)
    {
        double min = w.Min ?? 0, max = w.Max ?? 100;
        raw = Value(w.Sensor, s);
        bool cont = Continuous(w.Sensor);
        if (cont) Animating = true;
        double v = Tween(ref state.V, raw, w, dt, max - min, cont);
        if (double.IsNaN(v) || max <= min) return 0;
        return Quant(Math.Clamp((v - min) / (max - min), 0, 1), px);
    }

    static double Quant(double f, double px) => px > 1 ? Math.Round(f * px * 2) / (px * 2) : f;

    SKColor Col(string? spec, SKColor fallback) => Resolve(th, spec, fallback, 0);

    public static SKColor Resolve(Theme th, string? spec, SKColor fallback) => Resolve(th, spec, fallback, 0);

    /// "#RGB", "#RRGGBB", "#RRGGBBAA" or a palette name, each optionally "/NN" for NN % opacity ("a1/30").
    static SKColor Resolve(Theme th, string? spec, SKColor fallback, int depth)
    {
        if (string.IsNullOrWhiteSpace(spec)) return fallback;
        string name = spec;
        float pct = 100;
        int slash = spec.IndexOf('/');
        if (slash > 0)
        {
            name = spec[..slash];
            float.TryParse(spec[(slash + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out pct);
        }
        SKColor c;
        if (!name.StartsWith('#') && depth < 4 && th.Palette.TryGetValue(name, out var p)) c = Resolve(th, p, fallback, depth + 1);
        else c = ParseHex(name) ?? (name switch { "warn" => new SKColor(0xFF, 0xB0, 0x20), "crit" => new SKColor(0xFF, 0x4D, 0x4D), _ => fallback });
        return pct < 100 ? c.WithAlpha((byte)(c.Alpha * Math.Clamp(pct, 0, 100) / 100)) : c;
    }

    public static SKColor? ParseHex(string s)
    {
        if (s.Length < 4 || s[0] != '#') return null;
        var h = s[1..];
        if (h.Length is 3 or 4) h = string.Concat(h.Select(ch => $"{ch}{ch}"));
        if (h.Length is not (6 or 8) || !uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return null;
        return h.Length == 6 ? new SKColor((byte)(v >> 16), (byte)(v >> 8), (byte)v) : new SKColor((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    SKColor[] Colors(Widget w, State state, double raw, double dt, string fallback = "a1")
    {
        var cols = w.Colors is { Length: > 0 } ? w.Colors.Select(x => Col(x, SKColors.White)).ToArray() : [Col(w.Color ?? fallback, SKColors.White)];
        if (w.Warn != null || w.Crit != null)
        {
            double level = double.IsNaN(raw) ? 0 : w.Crit != null && raw >= w.Crit ? 2 : w.Warn != null && raw >= w.Warn ? 1 : 0;
            if (dt <= 0) state.C = level;
            else if (Math.Abs(state.C - level) > 0.004) { state.C += (level - state.C) * (1 - Math.Exp(-dt / 0.4)); Animating = true; }
            else state.C = level;
            var warn = Col("warn", SKColors.Orange);
            var crit = Col("crit", SKColors.Red);
            for (int i = 0; i < cols.Length; i++)
                cols[i] = Lerp(Lerp(cols[i], warn, (float)Math.Clamp(state.C, 0, 1)), crit, (float)Math.Clamp(state.C - 1, 0, 1));
        }
        float op = w.Opacity ?? 1;
        if (op < 1) for (int i = 0; i < cols.Length; i++) cols[i] = cols[i].WithAlpha((byte)(cols[i].Alpha * op));
        return cols;
    }

    SKColor Op(Widget w, SKColor c) => w.Opacity is float op && op < 1 ? c.WithAlpha((byte)(c.Alpha * op)) : c;

    public static SKColor Lerp(SKColor a, SKColor b, float t) => new(
        (byte)(a.Red + (b.Red - a.Red) * t), (byte)(a.Green + (b.Green - a.Green) * t),
        (byte)(a.Blue + (b.Blue - a.Blue) * t), (byte)(a.Alpha + (b.Alpha - a.Alpha) * t));

    static SKColor Sample(SKColor[] cols, float t)
    {
        if (cols.Length == 1) return cols[0];
        t = Math.Clamp(t, 0, 1) * (cols.Length - 1);
        int i = Math.Min((int)t, cols.Length - 2);
        return Lerp(cols[i], cols[i + 1], t - i);
    }

    static SKPaint Fill(SKColor c) => new() { IsAntialias = true, Color = c };

    static SKPaint StrokeP(float width, SKColor c, bool round = true) => new()
    {
        IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width, Color = c,
        StrokeCap = round ? SKStrokeCap.Round : SKStrokeCap.Butt, StrokeJoin = SKStrokeJoin.Round,
    };

    static readonly ConcurrentDictionary<float, SKMaskFilter> blurs = new();

    /// Shared, immutable blur filters: one per radius instead of a new native object every frame.
    static SKMaskFilter Blur(float r) => blurs.GetOrAdd(r, k => SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(0.5f, k * 0.5f)));

    static SKShader Linear(SKRect r, float angleDeg, SKColor[] cols)
    {
        double a = angleDeg * Math.PI / 180;
        float dx = (float)Math.Cos(a), dy = (float)Math.Sin(a);
        float half = (Math.Abs(dx) * r.Width + Math.Abs(dy) * r.Height) / 2;
        var c = new SKPoint(r.MidX, r.MidY);
        return SKShader.CreateLinearGradient(new SKPoint(c.X - dx * half, c.Y - dy * half), new SKPoint(c.X + dx * half, c.Y + dy * half), cols, null, SKShaderTileMode.Clamp);
    }

    // ---------- fonts and text ----------

    static readonly ConcurrentDictionary<(string, int, int), SKTypeface> faces = new();
    static readonly ConcurrentDictionary<(IntPtr, float), float> digitWidths = new();

    public static SKTypeface Face(string family, int weight = 400, int stretch = 5) =>
        faces.GetOrAdd((family, weight, stretch), k => SKTypeface.FromFamilyName(k.Item1, (SKFontStyleWeight)k.Item2, (SKFontStyleWidth)k.Item3, SKFontStyleSlant.Upright) ?? SKTypeface.Default);

    static SKFont MakeFont(SKTypeface tf, float size) => new(tf, size) { Edging = SKFontEdging.Antialias, Subpixel = true, Hinting = SKFontHinting.Slight };

    static float DigitWidth(SKFont f) => digitWidths.GetOrAdd((f.Typeface.Handle, f.Size), _ => f.GetGlyphWidths("0123456789").Max());

    /// Positions glyphs with tabular digits (every digit gets the widest digit's cell) plus optional letter spacing.
    static (SKTextBlob? Blob, float Width) Layout(string text, SKFont f, float spacing)
    {
        if (text.Length == 0) return (null, 0);
        var widths = f.GetGlyphWidths(text);
        if (widths.Length != text.Length) return (SKTextBlob.Create(text, f), f.MeasureText(text));
        float dw = DigitWidth(f);
        var pos = new SKPoint[text.Length];
        float x = 0;
        for (int i = 0; i < text.Length; i++)
        {
            bool digit = text[i] is >= '0' and <= '9';
            pos[i] = new SKPoint(x + (digit ? (dw - widths[i]) / 2 : 0), 0);
            x += (digit ? dw : widths[i]) + spacing;
        }
        return (SKTextBlob.CreatePositioned(text, f, pos), x - spacing);
    }

    static List<(string? Lit, string? Key, string? Fmt)> ParseTpl(string text)
    {
        var list = new List<(string?, string?, string?)>();
        int i = 0;
        while (i < text.Length)
        {
            int open = text.IndexOf('{', i);
            int close = open < 0 ? -1 : text.IndexOf('}', open);
            if (open < 0 || close < 0) { list.Add((text[i..], null, null)); break; }
            if (open > i) list.Add((text[i..open], null, null));
            var tok = text[(open + 1)..close];
            int colon = tok.IndexOf(':');
            list.Add((null, colon < 0 ? tok : tok[..colon], colon < 0 ? null : tok[(colon + 1)..]));
            i = close + 1;
        }
        return list;
    }

    string Expand(List<(string? Lit, string? Key, string? Fmt)> tpl, Snapshot s)
    {
        var sb = new StringBuilder();
        foreach (var (lit, key, fmt) in tpl)
        {
            if (lit != null) { sb.Append(lit); continue; }
            if (key == "time") { sb.Append(clock.ToString(fmt ?? "HH:mm", Pl)); continue; }
            if (key == "date") { sb.Append(clock.ToString(fmt ?? "d MMMM", Pl)); continue; }
            if (s.Str(key!) is string str) { sb.Append(str); continue; }
            double v = Value(key, s);
            sb.Append(Format(v, fmt));
        }
        return sb.ToString();
    }

    public static string Format(double v, string? fmt)
    {
        if (double.IsNaN(v)) return fmt is "rateu" ? "" : "--";
        switch (fmt)
        {
            case "rate": return RateValue(v) + " " + RateUnit(v);
            case "ratev": return RateValue(v);
            case "rateu": return RateUnit(v);
            case "mbit": return (v * 8 / 1e6).ToString(v * 8 < 1e7 ? "0.0" : "0", Pl);
            case "ghz": return (v / 1000).ToString("0.00", Pl);
            default:
                try { return v.ToString(fmt ?? "0", Pl); }
                catch (FormatException) { return v.ToString("0", Pl); }
        }
    }

    static string RateUnit(double v) => v < 1024 * 1024 ? "KB/s" : "MB/s";
    static string RateValue(double v) => v < 1024 * 1024 ? (v / 1024).ToString("0", Pl) : (v / 1024 / 1024).ToString(v < 100 * 1024 * 1024 ? "0.0" : "0", Pl);

    void DrawText(SKCanvas c, Widget w, State state, Snapshot s, double dt)
    {
        if (w.Has("blink"))
        {
            Animating = true;
            if (clock.Millisecond >= 500) return;
        }
        string text = state.Tpl != null ? Expand(state.Tpl, s) : w.Text ?? "";
        if (w.Has("upper")) text = text.ToUpper(Pl);
        float size = w.Size ?? 16;
        var tf = Face(w.Font ?? th.Font, w.Weight ?? 400, w.Stretch ?? 5);
        if (text != state.LastText || state.Blob == null && text.Length > 0)
        {
            state.Blob?.Dispose();
            using var f0 = MakeFont(tf, size);
            var (blob, width) = Layout(text, f0, w.Spacing ?? 0);
            if (w.W > 0 && width > w.W && width > 0)
            {
                // Shrink to fit the box (long GPU names and such).
                blob?.Dispose();
                size = Math.Max(6, size * w.W / width);
                using var f1 = MakeFont(tf, size);
                (blob, width) = Layout(text, f1, w.Spacing ?? 0);
            }
            state.Blob = blob; state.TextW = width; state.LastText = text; state.LastSize = size;
        }
        size = state.LastSize;
        using var f = MakeFont(tf, size);
        float cap = f.Metrics.CapHeight;
        float width2 = state.TextW;
        float x = w.Align switch
        {
            "center" => w.W > 0 ? w.X + (w.W - width2) / 2 : w.X - width2 / 2,
            "right" => w.W > 0 ? w.X + w.W - width2 : w.X - width2,
            _ => w.X,
        };
        float baseline = w.Y + cap;
        state.Bounds = new SKRect(x, w.Y, x + Math.Max(width2, 4), w.Y + Math.Max(cap, 4));
        if (state.Blob == null) return;
        double raw = Value(w.Sensor, s);
        var cols = Colors(w, state, raw, dt, "text");
        using var p = Fill(cols[0]);
        using var sh = cols.Length > 1 ? Linear(new SKRect(x, w.Y, x + width2, baseline), w.Angle ?? 90, cols) : null;
        p.Shader = sh;
        if (w.Glow is > 0)
        {
            using var gp = Fill(cols[0].WithAlpha((byte)(cols[0].Alpha * 0.75)));
            gp.MaskFilter = Blur(w.Glow.Value);
            c.DrawText(state.Blob, x, baseline, gp);
        }
        c.DrawText(state.Blob, x, baseline, p);
    }

    void DrawIcon(SKCanvas c, Widget w, State state, Snapshot s, double dt)
    {
        if (w.Glyph == null || !int.TryParse(w.Glyph, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cp)) return;
        float size = w.Size ?? 20;
        using var f = MakeFont(Face(w.Font ?? "Segoe Fluent Icons"), size);
        string g = char.ConvertFromUtf32(cp);
        f.MeasureText(g, out var b);
        float bw = w.W > 0 ? w.W : size, bh = w.H > 0 ? w.H : size;
        float cx = w.X + bw / 2, cy = w.Y + bh / 2;
        state.Bounds = SKRect.Create(w.X, w.Y, bw, bh);
        var cols = Colors(w, state, Value(w.Sensor, s), dt, "a1");
        using var p = Fill(cols[0]);
        using var sh = cols.Length > 1 ? Linear(state.Bounds, w.Angle ?? 90, cols) : null;
        p.Shader = sh;
        if (w.Glow is > 0)
        {
            using var gp = Fill(cols[0].WithAlpha((byte)(cols[0].Alpha * 0.8)));
            gp.MaskFilter = Blur(w.Glow.Value);
            c.DrawText(g, cx - b.MidX, cy - b.MidY, f, gp);
        }
        c.DrawText(g, cx - b.MidX, cy - b.MidY, f, p);
    }

    // ---------- shapes ----------

    void DrawRect(SKCanvas c, Widget w, State state)
    {
        var r = SKRect.Create(w.X, w.Y, w.W, w.H);
        float rad = w.Radius ?? 0;
        if (w.Shadow != null && w.Glow is > 0)
        {
            using var sp = Fill(Op(w, Col(w.Shadow, SKColors.Black)));
            sp.MaskFilter = Blur(w.Glow.Value);
            var sr = r; sr.Offset(0, w.Glow.Value * 0.25f);
            c.DrawRoundRect(sr, rad, rad, sp);
        }
        if (w.Color != null || w.Colors != null)
        {
            var cols = Colors(w, state, double.NaN, 0, "a1");
            using var p = Fill(cols[0]);
            using var sh = cols.Length < 2 ? null : w.Has("radial")
                ? SKShader.CreateRadialGradient(new SKPoint(r.MidX, r.MidY), Math.Max(r.Width, r.Height) / 2, cols, null, SKShaderTileMode.Clamp)
                : Linear(r, w.Angle ?? 90, cols);
            p.Shader = sh;
            c.DrawRoundRect(r, rad, rad, p);
        }
        if (w.Stroke != null)
        {
            float sw = w.StrokeWidth ?? 1;
            using var p = StrokeP(sw, Op(w, Col(w.Stroke, SKColors.White)));
            var ir = r; ir.Inflate(-sw / 2, -sw / 2);
            c.DrawRoundRect(ir, Math.Max(0, rad - sw / 2), Math.Max(0, rad - sw / 2), p);
        }
    }

    void DrawGlow(SKCanvas c, Widget w, State state)
    {
        var col = Op(w, Col(w.Color ?? "a1/35", SKColors.White));
        var center = new SKPoint(w.X + w.W / 2, w.Y + w.H / 2);
        float rx = Math.Max(1, w.W / 2), ry = Math.Max(1, w.H / 2);
        using var p = new SKPaint { IsAntialias = true };
        using var sh = SKShader.CreateRadialGradient(center, rx, [col, col.WithAlpha((byte)(col.Alpha * 0.4)), col.WithAlpha(0)], [0, 0.45f, 1],
            SKShaderTileMode.Clamp, SKMatrix.CreateScale(1, ry / rx, center.X, center.Y));
        p.Shader = sh;
        c.DrawOval(center, new SKSize(rx, ry), p);
    }

    void DrawImage(SKCanvas c, Widget w, State state)
    {
        if (!state.ImgTried)
        {
            state.ImgTried = true;
            var path = w.Src == null ? null : Path.IsPathRooted(w.Src) ? w.Src : Path.Combine(Themes.UserDir, w.Src);
            if (path != null && File.Exists(path)) state.Img = SKBitmap.Decode(path);
        }
        if (state.Img == null) return;
        var r = SKRect.Create(w.X, w.Y, w.W > 0 ? w.W : state.Img.Width, w.H > 0 ? w.H : state.Img.Height);
        using var p = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha((byte)(255 * (w.Opacity ?? 1))) };
        c.Save();
        if (w.Radius is > 0) c.ClipRoundRect(new SKRoundRect(r, w.Radius.Value), SKClipOperation.Intersect, true);
        using var img = SKImage.FromBitmap(state.Img);
        c.DrawImage(img, r, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), p);
        c.Restore();
    }

    void DrawBar(SKCanvas c, Widget w, State state, Snapshot s, double dt)
    {
        bool vertical = w.Has("vertical");
        float frac = (float)Frac(w, state, s, dt, vertical ? w.H : w.W, out double raw);
        var cols = Colors(w, state, raw, dt);
        var track = Op(w, Col(w.Track ?? "track", new SKColor(255, 255, 255, 20)));
                var r = SKRect.Create(w.X, w.Y, w.W, w.H);
        int segs = w.Segments ?? 0;
        if (segs > 0)
        {
            float gap = w.Gap ?? 2;
            float len = vertical ? w.H : w.W;
            float seg = (len - gap * (segs - 1)) / segs;
            float rad = w.Radius ?? 1.5f;
            using var tp = Fill(track);
            using var fp = Fill(SKColors.White);
            for (int i = 0; i < segs; i++)
            {
                var sr = vertical ? SKRect.Create(w.X, w.Y + w.H - (i + 1) * seg - i * gap, w.W, seg) : SKRect.Create(w.X + i * (seg + gap), w.Y, seg, w.H);
                c.DrawRoundRect(sr, rad, rad, tp);
                float lit = Math.Clamp(frac * segs - i, 0, 1);
                if (lit <= 0) continue;
                var col = Sample(cols, (i + 0.5f) / segs);
                fp.Color = col.WithAlpha((byte)(col.Alpha * lit));
                if (w.Glow is > 0 && lit > 0.5f)
                {
                    using var gp = Fill(col.WithAlpha((byte)(col.Alpha * 0.5f * lit)));
                    gp.MaskFilter = Blur(w.Glow.Value);
                    c.DrawRoundRect(sr, rad, rad, gp);
                }
                c.DrawRoundRect(sr, rad, rad, fp);
            }
            return;
        }
        float rr = w.Radius ?? Math.Min(w.W, w.H) / 2;
        using (var tp = Fill(track)) c.DrawRoundRect(r, rr, rr, tp);
        if (frac <= 0.0005f) return;
        float min = 2 * rr;
        float len2 = (vertical ? w.H : w.W) * frac;
        float drawn = Math.Max(len2, Math.Min(min, vertical ? w.H : w.W));
        var fr = vertical ? SKRect.Create(w.X, w.Y + w.H - drawn, w.W, drawn) : SKRect.Create(w.X, w.Y, drawn, w.H);
        byte fade = (byte)(255 * Math.Clamp(len2 / Math.Max(1, min), 0, 1));
        using var p = Fill(SKColors.White.WithAlpha(len2 >= min ? (byte)255 : fade));
        using var sh = cols.Length > 1 ? Linear(r, vertical ? 270 : 0, cols) : null;
        p.Shader = sh;
        if (sh == null) p.Color = cols[0].WithAlpha((byte)(cols[0].Alpha * p.Color.Alpha / 255));
        if (w.Glow is > 0)
        {
            using var gp = p.Clone();
            gp.Color = gp.Color.WithAlpha((byte)(gp.Color.Alpha * 0.6f));
            gp.MaskFilter = Blur(w.Glow.Value);
            c.DrawRoundRect(fr, rr, rr, gp);
        }
        c.DrawRoundRect(fr, rr, rr, p);
    }

    void DrawGauge(SKCanvas c, Widget w, State state, Snapshot s, double dt)
    {
        float thick = w.Thickness ?? 10, start = w.Start ?? 135, sweep = Math.Min(w.Sweep ?? 270, 360);
        float cx = w.X + w.W / 2, cy = w.Y + w.H / 2, r = Math.Min(w.W, w.H) / 2 - thick / 2;
        if (r <= 1) return;
        float frac = (float)Frac(w, state, s, dt, r * sweep * Math.PI / 180, out double raw);
        var cols = Colors(w, state, raw, dt);
        var rect = new SKRect(cx - r, cy - r, cx + r, cy + r);
        var track = Op(w, Col(w.Track ?? "track", new SKColor(255, 255, 255, 20)));
        bool round = !w.Has("flat");
        int segs = w.Segments ?? 0;
        if (segs > 0)
        {
            float gapDeg = w.Gap ?? 3;
            float seg = (sweep - gapDeg * (sweep >= 360 ? segs : segs - 1)) / segs;
            using var tp = StrokeP(thick, track, false);
            using var fp = StrokeP(thick, SKColors.White, false);
            for (int i = 0; i < segs; i++)
            {
                float a = start + i * (seg + gapDeg);
                c.DrawArc(rect, a, seg, false, tp);
                float lit = Math.Clamp(frac * segs - i, 0, 1);
                if (lit <= 0) continue;
                var col = Sample(cols, (i + 0.5f) / segs);
                fp.Color = col.WithAlpha((byte)(col.Alpha * lit));
                if (w.Glow is > 0 && lit > 0.5f)
                {
                    using var gp = StrokeP(thick, col.WithAlpha((byte)(col.Alpha * 0.5f * lit)), false);
                    gp.MaskFilter = Blur(w.Glow.Value);
                    c.DrawArc(rect, a, seg, false, gp);
                }
                c.DrawArc(rect, a, seg, false, fp);
            }
            return;
        }
        using (var tp = StrokeP(thick, track, round)) c.DrawArc(rect, start, sweep, false, tp);
        if (w.Has("ticks"))
        {
            using var kp = StrokeP(1.2f, Op(w, Col(w.Stroke ?? "sub/50", SKColors.Gray)), false);
            int n = w.Points ?? 10;
            for (int i = 0; i <= n; i++)
            {
                double a = (start + sweep * i / n) * Math.PI / 180;
                float r0 = r - thick / 2 - 3, r1 = r0 - (i % 5 == 0 ? 7 : 4);
                c.DrawLine(cx + (float)Math.Cos(a) * r0, cy + (float)Math.Sin(a) * r0, cx + (float)Math.Cos(a) * r1, cy + (float)Math.Sin(a) * r1, kp);
            }
        }
        // "alternate": on odd minutes (time.sec) / hours (time.min) the ring empties instead of filling,
        // so a clock ring never jumps from full to empty.
        bool drain = w.Has("alternate") && (w.Sensor switch { "time.sec" => clock.Minute, "time.min" => clock.Hour, _ => 0 }) % 2 == 1;
        float valSweep = sweep * frac;
        float a0 = drain ? start + valSweep : start;
        if (drain) valSweep = sweep - valSweep;
        if (valSweep <= sweep * 0.0005f) return;
        using var p = StrokeP(thick, cols[0], round);
        SKShader? sweepSh = null;
        if (cols.Length > 1)
        {
            float capDeg = round ? (float)(thick / 2 / r * 180 / Math.PI) : 0;
            float total = Math.Min(360, sweep + 2 * capDeg);
            p.Color = SKColors.White.WithAlpha(cols.Max(x => x.Alpha));
            sweepSh = SKShader.CreateSweepGradient(new SKPoint(cx, cy), cols.Select(x => x.WithAlpha(255)).ToArray(), null, SKShaderTileMode.Clamp, 0, total,
                SKMatrix.CreateRotationDegrees(start - capDeg, cx, cy));
            p.Shader = sweepSh;
        }
        using var sweepOwner = sweepSh;
        if (w.Glow is > 0)
        {
            using var gp = p.Clone();
            gp.Color = gp.Color.WithAlpha((byte)(gp.Color.Alpha * 0.55f));
            gp.MaskFilter = Blur(w.Glow.Value);
            c.DrawArc(rect, a0, valSweep, false, gp);
        }
        c.DrawArc(rect, a0, valSweep, false, p);
        if (w.Has("dot"))
        {
            double a = (drain ? a0 : start + valSweep) * Math.PI / 180;
            var pt = new SKPoint(cx + (float)Math.Cos(a) * r, cy + (float)Math.Sin(a) * r);
            var end = Sample(cols, frac);
            using (var gp = Fill(end.WithAlpha(140))) { gp.MaskFilter = Blur(thick * 0.6f); c.DrawCircle(pt, thick * 0.45f, gp); }
            using (var dp = Fill(Lerp(end, SKColors.White, 0.75f))) c.DrawCircle(pt, thick * 0.3f, dp);
        }
    }

    void DrawCores(SKCanvas c, Widget w, State state, Snapshot s, double dt)
    {
        string prefix = w.Sensor ?? "cpu.t";
        int n = 0;
        while (s.V.ContainsKey($"{prefix}{n + 1}")) n++;
        if (n == 0) n = (int)Math.Max(0, s.Get("cpu.threads") is double d && !double.IsNaN(d) ? d : 0);
        if (n == 0) return;
        if (state.Vs == null || state.Vs.Length != n) state.Vs = Enumerable.Repeat(double.NaN, n).ToArray();
        float gap = w.Gap ?? 3;
        bool horizontal = w.Has("horizontal");
        float bw = ((horizontal ? w.H : w.W) - gap * (n - 1)) / n;
        float rad = w.Radius ?? Math.Min(bw / 2, 3);
        var cols = Colors(w, state, double.NaN, dt);
        var track = Op(w, Col(w.Track ?? "track", new SKColor(255, 255, 255, 20)));
        using var tp = Fill(track);
        using var fp = Fill(SKColors.White);
        var full = SKRect.Create(w.X, w.Y, w.W, w.H);
        using var csh = cols.Length > 1 ? Linear(full, horizontal ? 0 : 270, cols) : null;
        fp.Shader = csh;
        if (csh == null) fp.Color = cols[0];
        double max = w.Max ?? 100;
        for (int i = 0; i < n; i++)
        {
            double v = Tween(ref state.Vs[i], s.Get($"{prefix}{i + 1}"), w, dt, max, false);
            float frac = double.IsNaN(v) ? 0 : (float)Quant(Math.Clamp(v / max, 0, 1), horizontal ? w.W : w.H);
            var tr = horizontal ? SKRect.Create(w.X, w.Y + i * (bw + gap), w.W, bw) : SKRect.Create(w.X + i * (bw + gap), w.Y, bw, w.H);
            c.DrawRoundRect(tr, rad, rad, tp);
            float len = (horizontal ? w.W : w.H) * frac;
            if (len < 0.5f) continue;
            len = Math.Max(len, Math.Min(2 * rad, horizontal ? w.W : w.H));
            var fr = horizontal ? SKRect.Create(w.X, tr.Top, len, bw) : SKRect.Create(tr.Left, w.Y + w.H - len, bw, len);
            c.DrawRoundRect(fr, rad, rad, fp);
        }
    }

    void DrawGraph(SKCanvas c, Widget w, State state, Snapshot s, double dt)
    {
        if (w.Sensor == null || !s.H.TryGetValue(w.Sensor, out var hist)) return;
        int n = Math.Clamp(w.Points ?? 60, 4, hist.Length);
        float thick = w.Thickness ?? 2;
        var cols = Colors(w, state, s.Get(w.Sensor), dt);
        var box = SKRect.Create(w.X, w.Y, w.W, w.H);
        double min = w.Min ?? 0;
        double max = w.Max ?? 0;
        if (max <= min)
        {
            // Auto range with a smoothed ceiling so the curve does not jump when a spike leaves the window.
            double peak = 0;
            for (int i = hist.Length - n; i < hist.Length; i++) if (!float.IsNaN(hist[i])) peak = Math.Max(peak, hist[i]);
            // Snaps to 1/2/2.5/5 x 10^n and only shrinks when the data fell well below: a rescale redraws the whole graph,
            // so it should happen rarely and in one go rather than as an animation.
            double need = Math.Max(peak * 1.1, w.Sensor.StartsWith("net.") ? 128 * 1024 : 1);
            if (double.IsNaN(state.AutoMax) || need > state.AutoMax || need < state.AutoMax * 0.4) state.AutoMax = Nice(need);
            max = min + state.AutoMax;
        }
        float inset = thick / 2 + 1;
        SKPoint P(int slot, float v) => new(w.X + w.W * slot / (n - 1), w.Y + w.H - inset - (float)Math.Clamp((v - min) / (max - min), 0, 1) * (w.H - 2 * inset));

        if (w.Has("grid"))
        {
            using var gp = StrokeP(1, Op(w, Col(w.Track ?? "track", new SKColor(255, 255, 255, 20))), false);
            for (int i = 1; i <= 3; i++) { float y = (float)Math.Round(w.Y + w.H * i / 4) + 0.5f; c.DrawLine(w.X, y, w.X + w.W, y, gp); }
        }

        var runs = new List<List<SKPoint>>();
        SKPoint? head = null;
        if (w.Has("sweep"))
        {
            // Oscilloscope sweep: samples stay where they were drawn and a pen overwrites the oldest ones.
            // Only a thin band around the pen changes per frame, so this animates smoothly within the panel's bandwidth.
            Animating = true;
            long cur = s.Seq - 2;
            if (cur < 1) return;
            double f = Math.Clamp((now - s.T) / s.Interval, 0, 1);
            int gapSlots = Math.Max(2, n / 24);
            float At(long k) { long idx = hist.Length - s.Seq + k; return idx >= 0 && idx < hist.Length ? hist[idx] : float.NaN; }
            List<SKPoint>? run = null;
            for (long k = cur - (n - 1 - gapSlots); k <= cur; k++)
            {
                float v = At(k);
                int slot = (int)(((k % n) + n) % n);
                if (float.IsNaN(v) || slot == 0 && run != null) { if (run is { Count: > 1 }) runs.Add(run); run = null; }
                if (float.IsNaN(v)) continue;
                (run ??= new List<SKPoint>()).Add(P(slot, v));
            }
            float vNext = At(cur + 1), vCur = At(cur);
            int curSlot = (int)(((cur % n) + n) % n);
            if (run != null && !float.IsNaN(vNext) && !float.IsNaN(vCur) && curSlot < n - 1)
            {
                var a = P(curSlot, vCur);
                var b = P(curSlot + 1, vNext);
                f = Quant(f, (b.X - a.X) / 2);
                head = new SKPoint(a.X + (b.X - a.X) * (float)f, a.Y + (b.Y - a.Y) * (float)f);
                run.Add(head.Value);
            }
            else if (run is { Count: > 0 }) head = run[^1];
            if (run is { Count: > 1 }) runs.Add(run);
        }
        else
        {
            var run = new List<SKPoint>();
            for (int i = 0; i < n; i++)
            {
                float v = hist[hist.Length - n + i];
                if (float.IsNaN(v)) { if (run.Count > 1) runs.Add(run); run = new List<SKPoint>(); continue; }
                run.Add(P(i, v));
            }
            if (run.Count > 1) runs.Add(run);
            if (runs.Count > 0) head = runs[^1][^1];
        }

        bool smooth = !w.Has("sweep") && !w.Has("sharp");
        var fillCol = Col(w.Track != null && w.Has("fillTrack") ? w.Track : null, cols[0]);
        foreach (var run in runs)
        {
            using var line = BuildPath(run, smooth);
            if (!w.Has("nofill"))
            {
                using var area = new SKPath(line);
                area.LineTo(run[^1].X, w.Y + w.H);
                area.LineTo(run[0].X, w.Y + w.H);
                area.Close();
                using var ap = new SKPaint { IsAntialias = true };
                using var ash = SKShader.CreateLinearGradient(new SKPoint(0, w.Y), new SKPoint(0, w.Y + w.H),
                    [fillCol.WithAlpha((byte)(fillCol.Alpha * 0.38f)), fillCol.WithAlpha(0)], null, SKShaderTileMode.Clamp);
                ap.Shader = ash;
                c.DrawPath(area, ap);
            }
            using var lp = StrokeP(thick, cols[0]);
            using var lsh = cols.Length > 1 ? Linear(box, 0, cols) : null;
            lp.Shader = lsh;
            if (w.Glow is > 0)
            {
                using var gp = lp.Clone();
                gp.Color = gp.Color.WithAlpha(140);
                gp.MaskFilter = Blur(w.Glow.Value);
                c.DrawPath(line, gp);
            }
            c.DrawPath(line, lp);
        }
        if (head != null && w.Has("dot"))
        {
            var end = cols[^1];
            using (var gp = Fill(end.WithAlpha(130))) { gp.MaskFilter = Blur(thick * 2); c.DrawCircle(head.Value, thick * 1.4f, gp); }
            using (var dp = Fill(Lerp(end, SKColors.White, 0.7f))) c.DrawCircle(head.Value, thick * 1.1f, dp);
        }
    }

    static double Nice(double v)
    {
        double p = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var m in new[] { 1, 2, 2.5, 5, 10 }) if (m * p >= v) return m * p;
        return 10 * p;
    }

    static SKPath BuildPath(List<SKPoint> pts, bool smooth)
    {
        var path = new SKPath();
        path.MoveTo(pts[0]);
        if (!smooth || pts.Count < 3)
        {
            for (int i = 1; i < pts.Count; i++) path.LineTo(pts[i]);
            return path;
        }
        // Catmull-Rom through the samples, as cubic Beziers.
        for (int i = 0; i < pts.Count - 1; i++)
        {
            var p0 = pts[Math.Max(0, i - 1)]; var p1 = pts[i]; var p2 = pts[i + 1]; var p3 = pts[Math.Min(pts.Count - 1, i + 2)];
            var c1 = new SKPoint(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
            var c2 = new SKPoint(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
            path.CubicTo(c1, c2, p2);
        }
        return path;
    }

    /// Renders a theme into a fresh bitmap, for gallery thumbnails and exports.
    public static SKBitmap Still(Theme t, Snapshot s, int warmupFrames = 40)
    {
        var bmp = new SKBitmap(W, H, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var r = new Renderer(t);
        using var c = new SKCanvas(bmp);
        for (int i = 0; i < warmupFrames; i++) r.Render(c, s, i == 0 ? 0 : 0.1, s.T + 0.6);
        r.Render(c, s, 0, s.T + 0.6);
        return bmp;
    }
}
