using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace Ember;

public static class Clock
{
    static readonly Stopwatch sw = Stopwatch.StartNew();
    public static double Now => sw.Elapsed.TotalSeconds;
}

/// One immutable sample of every sensor. The sensor thread builds a new one each tick and swaps the reference.
public sealed class Snapshot
{
    public const int HistoryLength = 300;
    public readonly Dictionary<string, double> V = new();
    public readonly Dictionary<string, string> S = new();
    public readonly Dictionary<string, float[]> H = new();
    public long Seq;
    public double T;
    public double Interval = 1;

    public double Get(string k) => V.TryGetValue(k, out var v) ? v : double.NaN;
    public string? Str(string k) => S.TryGetValue(k, out var s) ? s : null;

    public static readonly Snapshot Empty = new();

    /// Plausible fake data for gallery thumbnails and the editor when no sensors are available.
    public static Snapshot Demo(double t = 0)
    {
        var s = new Snapshot { Seq = 1000 + (long)t, T = Math.Floor(t), Interval = 1 };
        double W(double phase, double speed) => 0.5 + 0.5 * Math.Sin(t * speed + phase);
        void Set(string k, double v) => s.V[k] = v;
        Set("cpu.load", 28 + 18 * W(0, 0.7)); Set("cpu.loadmax", 64 + 20 * W(1, 0.9)); Set("cpu.temp", 54 + 8 * W(2, 0.3));
        Set("cpu.clock", 5260 + 120 * W(3, 1.1)); Set("cpu.power", 58 + 20 * W(4, 0.6)); Set("cpu.threads", 12);
        for (int i = 1; i <= 12; i++) Set($"cpu.t{i}", Math.Clamp(15 + 70 * W(i * 1.7, 0.8 + i * 0.05) * (i % 3 == 0 ? 0.4 : 1), 0, 100));
        Set("gpu.load", 72 + 14 * W(5, 0.5)); Set("gpu.temp", 61 + 5 * W(6, 0.25)); Set("gpu.hotspot", 72); Set("gpu.memtemp", 66);
        Set("gpu.clock", 2805); Set("gpu.memclock", 15851); Set("gpu.power", 138 + 22 * W(7, 0.5)); Set("gpu.fan", 1460); Set("gpu.fanpct", 42);
        Set("gpu.vram.used", 6.3); Set("gpu.vram.total", 15.9); Set("gpu.vram", 39.6);
        Set("ram.used", 14.6); Set("ram.total", 31.1); Set("ram.free", 16.5); Set("ram.load", 47);
        Set("net.down", 12.6e6 * W(8, 0.4)); Set("net.up", 0.84e6 * W(9, 0.6));
        Set("disk.used", 61); Set("disk.free", 362); Set("disk.total", 931); Set("disk.temp", 41);
        s.S["cpu.name"] = "Ryzen 5 9600X"; s.S["gpu.name"] = "RTX 5060 Ti"; s.S["net.name"] = "Ethernet";
        s.S["disk.name"] = "Lexar NM790"; s.S["sys.uptime"] = "3 h 24 min";
        foreach (var k in Sensors.HistoryKeys)
        {
            if (!s.V.TryGetValue(k, out var v)) continue;
            var h = new float[HistoryLength];
            double seed = k.GetHashCode() % 1000 / 100.0;
            for (int i = 0; i < h.Length; i++)
            {
                double x = (t - (h.Length - 1 - i)) * 0.11 + seed;
                double n = Math.Sin(x) * 0.5 + Math.Sin(x * 2.7 + 1) * 0.3 + Math.Sin(x * 6.1 + 2) * 0.2;
                h[i] = (float)Math.Max(0, v * (1 + 0.35 * n));
            }
            s.H[k] = h;
        }
        return s;
    }
}

public sealed record SensorInfo(string Key, string Label, string Unit, string Group);

public sealed class Sensors : IDisposable
{
    /// Friendly keys the built-in themes use. Raw LHM sensors are also exposed as "lhm:/identifier".
    public static readonly SensorInfo[] Catalog =
    [
        new("cpu.load", "Load", "%", "CPU"), new("cpu.temp", "Temperature", "°C", "CPU"), new("cpu.clock", "Clock speed", "MHz", "CPU"),
        new("cpu.power", "Power draw", "W", "CPU"), new("cpu.loadmax", "Busiest thread", "%", "CPU"), new("cpu.t1", "Thread 1 (cpu.t1 to cpu.tN)", "%", "CPU"),
        new("cpu.name", "Name", "", "CPU"),
        new("gpu.load", "Load", "%", "GPU"), new("gpu.temp", "Temperature", "°C", "GPU"), new("gpu.hotspot", "Hot spot", "°C", "GPU"),
        new("gpu.memtemp", "Memory temperature", "°C", "GPU"), new("gpu.clock", "Clock speed", "MHz", "GPU"), new("gpu.memclock", "Memory clock", "MHz", "GPU"),
        new("gpu.power", "Power draw", "W", "GPU"), new("gpu.fan", "Fan", "RPM", "GPU"), new("gpu.fanpct", "Fan", "%", "GPU"),
        new("gpu.vram", "VRAM used", "%", "GPU"), new("gpu.vram.used", "VRAM used", "GB", "GPU"), new("gpu.vram.total", "VRAM total", "GB", "GPU"),
        new("gpu.name", "Name", "", "GPU"),
        new("ram.load", "Used", "%", "RAM"), new("ram.used", "Used", "GB", "RAM"), new("ram.free", "Free", "GB", "RAM"), new("ram.total", "Total", "GB", "RAM"),
        new("net.down", "Download", "B/s", "Network"), new("net.up", "Upload", "B/s", "Network"), new("net.name", "Adapter", "", "Network"),
        new("disk.used", "System drive used", "%", "Disk"), new("disk.free", "System drive free", "GB", "Disk"), new("disk.total", "System drive total", "GB", "Disk"),
        new("disk.temp", "Drive temperature", "°C", "Disk"), new("disk.name", "Drive name", "", "Disk"),
        new("time", "Time  {time:HH:mm}", "", "Time"), new("date", "Date  {date:dddd, d MMMM}", "", "Time"),
        new("time.sec", "Seconds (smooth)", "s", "Time"), new("time.min", "Minutes (smooth)", "min", "Time"), new("time.hour", "Hours (smooth, 12 h)", "h", "Time"),
        new("sys.uptime", "Uptime", "", "System"),
    ];

    public static readonly string[] HistoryKeys =
    [
        "cpu.load", "cpu.temp", "cpu.clock", "cpu.power", "cpu.loadmax", "gpu.load", "gpu.temp", "gpu.clock", "gpu.power", "gpu.vram", "gpu.fan",
        "ram.load", "ram.used", "net.down", "net.up", "disk.temp",
    ];

    public volatile Snapshot Current = Snapshot.Empty;
    public volatile SensorInfo[] Raw = [];
    public volatile string LhmStatus = "starting";
    /// `--demo`: plausible fake readings instead of hardware, for screenshots.
    public static bool Demo;
    public static readonly bool Elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    readonly Settings cfg;
    readonly Dictionary<string, float[]> rings = HistoryKeys.ToDictionary(k => k, _ => Enumerable.Repeat(float.NaN, Snapshot.HistoryLength).ToArray());
    readonly AutoResetEvent wake = new(false);
    Thread? thread;
    volatile bool stop, reinit;
    Computer? pc;
    double lhmRetryAt;
    long seq;
    string? netName;
    long lastRx, lastTx;
    double lastNetT, netPickAt;
    ulong lastIdle, lastBusy;

    public Sensors(Settings cfg) => this.cfg = cfg;

    public static Version? PawnIoVersion
    {
        get
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var k = b.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
                if (Version.TryParse(k?.GetValue("DisplayVersion") as string, out var v)) return v;
            }
            return null;
        }
    }

    public void Start()
    {
        thread = new Thread(Loop) { IsBackground = true, Name = "Sensors", Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }

    /// Re-open LHM, e.g. after the motherboard toggle changed.
    public void Reinit() { reinit = true; wake.Set(); }

    public void Dispose()
    {
        stop = true;
        wake.Set();
        thread?.Join(3000);
    }

    void Loop()
    {
        while (!stop)
        {
            double t0 = Clock.Now;
            if (Demo) { Current = Snapshot.Demo(t0); wake.WaitOne(1000); continue; }
            try
            {
                if ((pc == null && t0 >= lhmRetryAt) || reinit) OpenLhm();
                Sample();
            }
            catch (Exception e) { Log.Error("sensors", e); }
            int ms = Math.Clamp(cfg.Interval, 250, 5000) - (int)((Clock.Now - t0) * 1000);
            wake.WaitOne(Math.Max(50, ms));
        }
        try { pc?.Close(); } catch { }
    }

    void OpenLhm()
    {
        reinit = false;
        try { pc?.Close(); } catch { }
        pc = null;
        try
        {
            // Memory (SPD over SMBus), controllers and PSU stay off: RAM comes from Windows, the rest is not needed and SMBus is the riskiest bus to poke.
            var c = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsStorageEnabled = Elevated, IsMotherboardEnabled = Elevated && cfg.Motherboard };
            c.Open();
            // LHM samples NVIDIA PCIe throughput on every update: two NVML calls that sit ~30 ms each in the driver, 60 ms of every
            // second. Ember never shows it; with the delegate gone the library's own try/catch skips the call.
            // ponytail: private LHM field, a rename only brings the 60 ms back (logged below); patch LHM if that happens.
            if (PcieHook is { } pcie) pcie.SetValue(null, null); else Log.Info("LHM: PCIe throughput hook not found");
            gpuInUse = null;
            pc = c;
            LhmStatus = Elevated ? "running" : "running without administrator rights";
            Log.Info($"LHM open: {string.Join(", ", c.Hardware.Select(h => h.Name))}");
        }
        catch (Exception e)
        {
            Log.Error("LHM open", e);
            LhmStatus = "error: " + e.Message;
            lhmRetryAt = Clock.Now + 60;
        }
    }

    void Sample()
    {
        var s = new Snapshot { T = Clock.Now, Interval = Math.Clamp(cfg.Interval, 250, 5000) / 1000.0 };
        Ram(s);
        Net(s);
        if (seq % 10 == 0 || !last.TryGetValue("disk.total", out _)) Disk(s); else CopyDisk(s);
        if (pc != null) Lhm(s);
        if (!s.V.ContainsKey("cpu.load")) CpuFallback(s);
        s.S["sys.uptime"] = Uptime(TimeSpan.FromMilliseconds(Environment.TickCount64));

        int slot = (int)(seq % Snapshot.HistoryLength);
        foreach (var (k, ring) in rings)
            ring[slot] = s.V.TryGetValue(k, out var v) ? (float)v : float.NaN;
        seq++;
        s.Seq = seq;
        foreach (var (k, ring) in rings)
        {
            var h = new float[Snapshot.HistoryLength];
            int start = (int)(seq % Snapshot.HistoryLength);
            Array.Copy(ring, start, h, 0, ring.Length - start);
            Array.Copy(ring, 0, h, ring.Length - start, start);
            s.H[k] = h;
        }
        last = s.V;
        Current = s;
    }

    Dictionary<string, double> last = new();

    void Lhm(Snapshot s)
    {
        IHardware? cpu = null;
        var gpus = new List<IHardware>();
        var disks = new List<IHardware>();
        var raw = new List<SensorInfo>();
        foreach (var hw in pc!.Hardware)
        {
            // Disks and GPUs the themes are not showing (the iGPU costs ~5 ms of CPU per update) refresh every 5 s.
            bool idle = hw.HardwareType == HardwareType.Storage || IsGpu(hw) && gpuInUse != null && hw != gpuInUse;
            bool slow = idle && seq % 5 != 0;
            if (!slow)
            {
                try { hw.Update(); foreach (var sub in hw.SubHardware) sub.Update(); }
                catch (Exception e) { Log.Error($"update {hw.Name}", e); }
            }
            switch (hw.HardwareType)
            {
                case HardwareType.Cpu: cpu ??= hw; break;
                case HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel: gpus.Add(hw); break;
                case HardwareType.Storage: disks.Add(hw); break;
            }
            Collect(hw, s, raw, hw.Name);
        }
        Raw = raw.ToArray();

        if (cpu != null)
        {
            Put(s, "cpu.load", Find(cpu, SensorType.Load, "CPU Total"));
            Put(s, "cpu.loadmax", Find(cpu, SensorType.Load, "CPU Core Max"));
            Put(s, "cpu.temp", Positive(Find(cpu, SensorType.Temperature, "Core (Tctl/Tdie)", "CPU Package", "Core (Tdie)", "Core (Tctl)", "Core Average")
                                        ?? cpu.Sensors.Where(x => x.SensorType == SensorType.Temperature && x.Value > 0).Select(x => x.Value).FirstOrDefault()));
            var avg = Positive(Find(cpu, SensorType.Clock, "Cores (Average)"));
            avg ??= Positive(cpu.Sensors.Where(x => x.SensorType == SensorType.Clock && x.Name.StartsWith("Core #") && !x.Name.Contains("Effective")).Max(x => x.Value));
            Put(s, "cpu.clock", avg);
            Put(s, "cpu.power", Positive(Find(cpu, SensorType.Power, "Package", "CPU Package")));
            int n = 0;
            foreach (var t in cpu.Sensors.Where(x => x.SensorType == SensorType.Load && x.Name.StartsWith("CPU Core #")).OrderBy(x => x.Index))
                Put(s, $"cpu.t{++n}", t.Value);
            s.V["cpu.threads"] = n;
            s.S["cpu.name"] = Short(cpu.Name);
        }

        var gpu = gpuInUse = PickGpu(gpus);
        if (gpu != null)
        {
            Put(s, "gpu.load", Find(gpu, SensorType.Load, "GPU Core"));
            Put(s, "gpu.temp", Positive(Find(gpu, SensorType.Temperature, "GPU Core")));
            Put(s, "gpu.hotspot", Positive(Find(gpu, SensorType.Temperature, "GPU Hot Spot")));
            Put(s, "gpu.memtemp", Positive(Find(gpu, SensorType.Temperature, "GPU Memory Junction", "GPU Memory")));
            Put(s, "gpu.clock", Find(gpu, SensorType.Clock, "GPU Core"));
            Put(s, "gpu.memclock", Find(gpu, SensorType.Clock, "GPU Memory"));
            Put(s, "gpu.power", Find(gpu, SensorType.Power, "GPU Package", "GPU Core", "GPU Power") ?? gpu.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Power)?.Value);
            Put(s, "gpu.fan", gpu.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Fan)?.Value);
            Put(s, "gpu.fanpct", gpu.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Control)?.Value);
            var used = Find(gpu, SensorType.SmallData, "GPU Memory Used");
            var total = Find(gpu, SensorType.SmallData, "GPU Memory Total");
            if (used != null) Put(s, "gpu.vram.used", used / 1024);
            if (total is > 0)
            {
                Put(s, "gpu.vram.total", total / 1024);
                if (used != null) Put(s, "gpu.vram", used / total * 100);
            }
            s.S["gpu.name"] = Short(gpu.Name);
        }

        for (int i = 0; i < disks.Count; i++)
        {
            var temp = Positive(disks[i].Sensors.FirstOrDefault(x => x.SensorType == SensorType.Temperature)?.Value);
            Put(s, $"disk{i + 1}.temp", temp);
            if (i == 0) { Put(s, "disk.temp", temp); s.S["disk.name"] = disks[i].Name; }
        }
    }

    IHardware? gpuInUse;

    internal static FieldInfo? PcieHook => typeof(Computer).Assembly.GetType("LibreHardwareMonitor.Interop.NvidiaML")
        ?.GetField("_windowsNvmlDeviceGetPcieThroughputDelegate", BindingFlags.Static | BindingFlags.NonPublic);

    static bool IsGpu(IHardware hw) => hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

    IHardware? PickGpu(List<IHardware> gpus)
    {
        if (gpus.Count == 0) return null;
        if (cfg.Gpu != "auto" && gpus.FirstOrDefault(g => g.Identifier.ToString() == cfg.Gpu) is { } chosen) return chosen;
        // Discrete first: NVIDIA, then any card with more than 2 GB of its own memory, then whatever is left (iGPU).
        return gpus.FirstOrDefault(g => g.HardwareType == HardwareType.GpuNvidia)
            ?? gpus.FirstOrDefault(g => Find(g, SensorType.SmallData, "GPU Memory Total") > 2048)
            ?? gpus[0];
    }

    public static IEnumerable<(string Id, string Name)> GpuChoices(Snapshot s) =>
        s.S.Where(kv => kv.Key.StartsWith("gpuid:")).Select(kv => (kv.Key[6..], kv.Value));

    static void Collect(IHardware hw, Snapshot s, List<SensorInfo> raw, string group)
    {
        if (IsGpu(hw)) s.S["gpuid:" + hw.Identifier] = hw.Name;
        foreach (var x in hw.Sensors)
        {
            if (x.SensorType == SensorType.Throughput && x.Name.StartsWith("GPU PCIe")) continue; // frozen, see OpenLhm
            string key = "lhm:" + x.Identifier;
            raw.Add(new SensorInfo(key, x.Name, Unit(x.SensorType), group));
            if (x.Value is float v && !float.IsNaN(v)) s.V[key] = v;
        }
        foreach (var sub in hw.SubHardware) Collect(sub, s, raw, group + " · " + sub.Name);
    }

    static string Unit(SensorType t) => t switch
    {
        SensorType.Temperature => "°C", SensorType.Load or SensorType.Control or SensorType.Level or SensorType.Humidity => "%",
        SensorType.Clock => "MHz", SensorType.Power => "W", SensorType.Fan => "RPM", SensorType.Voltage => "V", SensorType.Current => "A",
        SensorType.SmallData => "MB", SensorType.Data => "GB", SensorType.Throughput => "B/s", SensorType.Frequency => "Hz", SensorType.Flow => "L/h",
        SensorType.Energy => "mWh", SensorType.Noise => "dBA", _ => "",
    };

    static float? Find(IHardware hw, SensorType type, params string[] names)
    {
        foreach (var n in names)
            foreach (var x in hw.Sensors)
                if (x.SensorType == type && x.Name == n && x.Value is float v && !float.IsNaN(v)) return v;
        return null;
    }

    static float? Positive(float? v) => v is > 0 ? v : null;

    static void Put(Snapshot s, string key, double? v)
    {
        if (v is double d && !double.IsNaN(d) && !double.IsInfinity(d)) s.V[key] = d;
    }

    static string Short(string name) => name
        .Replace("NVIDIA GeForce ", "").Replace("NVIDIA ", "").Replace("AMD Radeon ", "Radeon ").Replace("AMD Ryzen", "Ryzen")
        .Replace("Intel(R) Core(TM) ", "Core ").Replace(" Processor", "").Replace("(TM)", "").Replace("(R)", "")
        .Replace(" 6-Core", "").Replace(" 8-Core", "").Replace(" 12-Core", "").Replace(" 16-Core", "").Trim();

    static string Uptime(TimeSpan t) => t.TotalDays >= 1 ? $"{(int)t.TotalDays} d {t.Hours} h" : t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{t.Minutes} min";

    void Ram(Snapshot s)
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m) || m.ullTotalPhys == 0) return;
        const double GiB = 1 << 30;
        s.V["ram.total"] = m.ullTotalPhys / GiB;
        s.V["ram.free"] = m.ullAvailPhys / GiB;
        s.V["ram.used"] = (m.ullTotalPhys - m.ullAvailPhys) / GiB;
        s.V["ram.load"] = 100.0 * (m.ullTotalPhys - m.ullAvailPhys) / m.ullTotalPhys;
    }

    void Net(Snapshot s)
    {
        double now = Clock.Now;
        NetworkInterface? ni = null;
        var all = NetworkInterface.GetAllNetworkInterfaces();
        string want = cfg.Net;
        if (want != "auto") ni = all.FirstOrDefault(n => n.Name == want);
        else
        {
            if (netName != null && now < netPickAt) ni = all.FirstOrDefault(n => n.Name == netName && n.OperationalStatus == OperationalStatus.Up);
            if (ni == null)
            {
                // The busiest physical adapter that is up and routes somewhere.
                ni = all.Where(n => n.OperationalStatus == OperationalStatus.Up
                                    && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                                    && n.GetIPProperties().GatewayAddresses.Count > 0)
                        .OrderByDescending(n => { var st = n.GetIPStatistics(); return st.BytesReceived + st.BytesSent; })
                        .FirstOrDefault();
                netPickAt = now + 30;
            }
        }
        if (ni == null) return;
        var stats = ni.GetIPStatistics();
        long rx = stats.BytesReceived, tx = stats.BytesSent;
        if (ni.Name == netName && now > lastNetT)
        {
            double dt = now - lastNetT;
            s.V["net.down"] = Math.Max(0, (rx - lastRx) / dt);
            s.V["net.up"] = Math.Max(0, (tx - lastTx) / dt);
        }
        netName = ni.Name;
        lastRx = rx; lastTx = tx; lastNetT = now;
        s.S["net.name"] = ni.Name;
    }

    public static string[] NetworkNames() =>
        NetworkInterface.GetAllNetworkInterfaces().Where(n => n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Select(n => n.Name).OrderBy(n => n).ToArray();

    void Disk(Snapshot s)
    {
        try
        {
            var d = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
            const double GiB = 1 << 30;
            s.V["disk.total"] = d.TotalSize / GiB;
            s.V["disk.free"] = d.AvailableFreeSpace / GiB;
            s.V["disk.used"] = 100.0 * (d.TotalSize - d.TotalFreeSpace) / d.TotalSize;
        }
        catch (Exception e) { Log.Error("disk", e); }
    }

    void CopyDisk(Snapshot s)
    {
        foreach (var k in new[] { "disk.total", "disk.free", "disk.used" })
            if (last.TryGetValue(k, out var v)) s.V[k] = v;
    }

    void CpuFallback(Snapshot s)
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return;
        ulong i = idle, busy = kernel + user - idle;
        if (lastBusy + lastIdle > 0)
        {
            double di = i - lastIdle, db = busy - lastBusy;
            if (di + db > 0) s.V["cpu.load"] = 100 * db / (di + db);
        }
        lastIdle = i; lastBusy = busy;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32", SetLastError = true)] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [DllImport("kernel32", SetLastError = true)] static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
}
