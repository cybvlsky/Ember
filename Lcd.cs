using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Ember;

/// Turing Smart Screen 3.5" (hardware revision A, USB 1a86:5722, serial "USB35INCHIPSV2").
/// Protocol: 6-byte commands carrying four packed 10-bit coordinates; bitmaps follow as raw RGB565 little-endian.
/// Measured on the real unit: ~157 KiB/s no matter the baud rate or flow control, so a full 480x320 frame takes ~1.9 s.
/// Writes block at that rate and cost nothing extra each: 300 rectangles of 16x8 move pixels as fast as one full frame,
/// with the command and the pixels as separate writes or as one. Several commands in a single write crash the firmware.
/// Raw Win32 handle instead of SerialPort: no hidden event thread that can blow up when the device is unplugged.
public sealed class TuringLcd : IDisposable
{
    public const int Width = 480, Height = 320;
    const byte CmdReset = 101, CmdScreenOff = 108, CmdScreenOn = 109, CmdBrightness = 110, CmdOrientation = 121, CmdBitmap = 197;
    const int Chunk = 16384;

    SafeFileHandle? h;
    public string? Port { get; private set; }
    public bool IsOpen => h is { IsInvalid: false, IsClosed: false };

    /// COM ports of connected rev A screens: USB enum entries of our VID/PID that are present in SERIALCOMM right now.
    public static List<string> FindPorts()
    {
        var present = SerialPortNames();
        var ours = new List<string>();
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB\VID_1A86&PID_5722");
        if (k == null) return ours;
        foreach (var sub in k.GetSubKeyNames())
        {
            using var p = k.OpenSubKey(sub + @"\Device Parameters");
            if (p?.GetValue("PortName") is string n && present.Contains(n, StringComparer.OrdinalIgnoreCase) && !ours.Contains(n)) ours.Add(n);
        }
        return ours;
    }

    public static string[] SerialPortNames()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
        return k?.GetValueNames().Select(n => k.GetValue(n) as string).OfType<string>().OrderBy(s => s).ToArray() ?? [];
    }

    public void Open(string port)
    {
        Close();
        var handle = CreateFile(@"\\.\" + port, 0xC0000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            throw new IOException(err == 5 ? L.F("{0} is in use by another program", port) : L.F("Cannot open {0} (error {1})", port, err));
        }
        try
        {
            var dcb = new DCB { DCBlength = (uint)Marshal.SizeOf<DCB>() };
            if (!GetCommState(handle, ref dcb)) throw new IOException($"GetCommState {port}: {Marshal.GetLastWin32Error()}");
            dcb.BaudRate = 115200;
            dcb.Flags = 0x0001 | 0x0004 | 0x0010 | 0x2000; // fBinary, fOutxCtsFlow, DTR on, RTS handshake: same as pyserial rtscts=True
            dcb.ByteSize = 8; dcb.Parity = 0; dcb.StopBits = 0;
            if (!SetCommState(handle, ref dcb)) throw new IOException($"SetCommState {port}: {Marshal.GetLastWin32Error()}");
            var to = new COMMTIMEOUTS { ReadIntervalTimeout = uint.MaxValue, WriteTotalTimeoutConstant = 2000 };
            if (!SetCommTimeouts(handle, ref to)) throw new IOException($"SetCommTimeouts {port}: {Marshal.GetLastWin32Error()}");
            PurgeComm(handle, 0x0004 | 0x0008);
        }
        catch { handle.Dispose(); throw; }
        h = handle;
        Port = port;
    }

    public void Close()
    {
        h?.Dispose();
        h = null;
    }

    public void Dispose() => Close();

    public static void Pack(Span<byte> b, int x, int y, int ex, int ey, byte cmd)
    {
        b[0] = (byte)(x >> 2);
        b[1] = (byte)(((x & 3) << 6) + (y >> 4));
        b[2] = (byte)(((y & 15) << 4) + (ex >> 6));
        b[3] = (byte)(((ex & 63) << 2) + (ey >> 8));
        b[4] = (byte)(ey & 255);
        b[5] = cmd;
    }

    void Cmd(byte cmd, int x = 0, int y = 0, int ex = 0, int ey = 0)
    {
        Span<byte> b = stackalloc byte[6];
        Pack(b, x, y, ex, ey, cmd);
        Write(b);
    }

    public void ScreenOn() => Cmd(CmdScreenOn);

    /// Reboots the panel's firmware. It drops off USB and comes back after a few seconds (possibly on another COM port).
    public void Reset() => Cmd(CmdReset);
    public void ScreenOff() => Cmd(CmdScreenOff);

    /// 0..100 %, the panel itself counts 0 = brightest .. 255 = dark.
    public void SetBrightness(int pct) => Cmd(CmdBrightness, 255 - Math.Clamp(pct, 0, 100) * 255 / 100);

    /// Landscape, or reverse landscape when flipped. The panel rotates on its own, coordinates stay 480x320.
    public void SetOrientation(bool flip)
    {
        Span<byte> b = stackalloc byte[16];
        Pack(b, 0, 0, 0, 0, CmdOrientation);
        b[6] = (byte)((flip ? 3 : 2) + 100);
        b[7] = Width >> 8; b[8] = Width & 255;
        b[9] = Height >> 8; b[10] = Height & 255;
        Write(b);
    }

    public void Blit(int x, int y, int w, int hgt, ReadOnlySpan<byte> rgb565le)
    {
        Cmd(CmdBitmap, x, y, x + w - 1, y + hgt - 1);
        Write(rgb565le);
    }

    unsafe void Write(ReadOnlySpan<byte> data)
    {
        var handle = h ?? throw new IOException(L.T("Port closed"));
        while (data.Length > 0)
        {
            int n = Math.Min(data.Length, Chunk);
            bool ok;
            int done;
            fixed (byte* p = data) ok = WriteFile(handle, p, n, out done, IntPtr.Zero);
            if (!ok) throw new IOException(L.F("Writing to {0} failed (error {1})", Port, Marshal.GetLastWin32Error()));
            if (done != n) throw new TimeoutException(L.F("The screen stopped taking data ({0}/{1} B)", done, n));
            data = data[n..];
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DCB
    {
        public uint DCBlength, BaudRate, Flags;
        public ushort wReserved, XonLim, XoffLim;
        public byte ByteSize, Parity, StopBits;
        public sbyte XonChar, XoffChar, ErrorChar, EofChar, EvtChar;
        public ushort wReserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct COMMTIMEOUTS
    {
        public uint ReadIntervalTimeout, ReadTotalTimeoutMultiplier, ReadTotalTimeoutConstant, WriteTotalTimeoutMultiplier, WriteTotalTimeoutConstant;
    }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32", SetLastError = true)] static extern bool GetCommState(SafeFileHandle h, ref DCB dcb);
    [DllImport("kernel32", SetLastError = true)] static extern bool SetCommState(SafeFileHandle h, ref DCB dcb);
    [DllImport("kernel32", SetLastError = true)] static extern bool SetCommTimeouts(SafeFileHandle h, ref COMMTIMEOUTS t);
    [DllImport("kernel32", SetLastError = true)] static extern bool PurgeComm(SafeFileHandle h, uint flags);
    [DllImport("kernel32", SetLastError = true)] static extern unsafe bool WriteFile(SafeFileHandle h, byte* buf, int len, out int written, IntPtr overlapped);
}
