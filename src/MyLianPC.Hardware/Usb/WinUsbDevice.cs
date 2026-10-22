using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MyLianPC.Hardware.Usb;

/// <summary>
/// Minimal synchronous WinUSB wrapper (bulk pipes only). WinUSB permits a single opener per
/// device, so <see cref="Open"/> fails with <see cref="DeviceBusyException"/> while another
/// process (e.g. L-Connect's service) holds the device.
/// </summary>
public sealed class WinUsbDevice : IDisposable
{
    private readonly SafeFileHandle _file;
    private readonly IntPtr _usb;
    private bool _disposed;

    public string DevicePath { get; }

    private WinUsbDevice(string path, SafeFileHandle file, IntPtr usb)
    {
        DevicePath = path;
        _file = file;
        _usb = usb;
    }

    /// <summary>Device interface paths for every present device registered under the given WinUSB interface GUID.</summary>
    public static IReadOnlyList<string> FindPaths(Guid interfaceGuid, ushort vid, ushort pid)
    {
        var result = new List<string>();
        var set = Native.SetupDiGetClassDevs(ref interfaceGuid, null, IntPtr.Zero, Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1)) return result;
        try
        {
            var data = new Native.SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<Native.SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref interfaceGuid, i, ref data); i++)
            {
                Native.SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var size, IntPtr.Zero);
                var buf = Marshal.AllocHGlobal((int)size);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6); // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
                    if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref data, buf, size, out _, IntPtr.Zero)) continue;
                    var path = Marshal.PtrToStringUni(buf + 4)!;
                    if (path.Contains($"vid_{vid:x4}&pid_{pid:x4}", StringComparison.OrdinalIgnoreCase)) result.Add(path);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
        return result;
    }

    public static WinUsbDevice Open(string path)
    {
        var file = Native.CreateFile(path, Native.GENERIC_READ | Native.GENERIC_WRITE, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_ATTRIBUTE_NORMAL | Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (file.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            throw err is 5 or 32 ? new DeviceBusyException(path, err) : new Win32Exception(err, $"CreateFile failed for {path}");
        }
        if (!Native.WinUsb_Initialize(file, out var usb))
        {
            int err = Marshal.GetLastWin32Error();
            file.Dispose();
            throw new Win32Exception(err, "WinUsb_Initialize failed");
        }
        return new WinUsbDevice(path, file, usb);
    }

    public void SetPipeTimeout(byte pipe, TimeSpan timeout)
    {
        uint ms = (uint)timeout.TotalMilliseconds;
        if (!Native.WinUsb_SetPipePolicy(_usb, pipe, Native.PIPE_TRANSFER_TIMEOUT, sizeof(uint), ref ms))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WinUsb_SetPipePolicy failed");
    }

    public void Write(byte pipe, ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var buf = data.ToArray();
        if (!Native.WinUsb_WritePipe(_usb, pipe, buf, (uint)buf.Length, out var written, IntPtr.Zero))
        {
            int err = Marshal.GetLastWin32Error();
            if (err == Native.ERROR_SEM_TIMEOUT) throw new TimeoutException($"WinUsb_WritePipe(0x{pipe:X2}) timed out: the device did not accept the transfer.");
            throw new Win32Exception(err, $"WinUsb_WritePipe(0x{pipe:X2}) failed (Win32 error {err})");
        }
        if (written != buf.Length) throw new IOException($"Short write: {written}/{buf.Length} bytes.");
    }

    /// <summary>Reads one transfer. Returns the bytes read, or null on timeout.</summary>
    public byte[]? Read(byte pipe, int maxLength)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var buf = new byte[maxLength];
        if (!Native.WinUsb_ReadPipe(_usb, pipe, buf, (uint)maxLength, out var read, IntPtr.Zero))
        {
            int err = Marshal.GetLastWin32Error();
            if (err == Native.ERROR_SEM_TIMEOUT) return null;
            throw new Win32Exception(err, $"WinUsb_ReadPipe(0x{pipe:X2}) failed");
        }
        return buf[..(int)read];
    }

    public void FlushPipe(byte pipe) => Native.WinUsb_FlushPipe(_usb, pipe);

    /// <summary>Clears a halted (stalled) endpoint on both host and device side.</summary>
    public void ResetPipe(byte pipe) => Native.WinUsb_ResetPipe(_usb, pipe);

    private Thread? _reader;
    private System.Collections.Concurrent.BlockingCollection<byte[]>? _replies;

    /// <summary>
    /// Starts a background thread that keeps exactly one read pending on <paramref name="pipe"/> at all times
    /// (infinite pipe timeout), queueing every transfer it receives. Some firmwares desynchronise when a
    /// partially-completed IN transfer is cancelled, which happens with timed reads; this never cancels.
    /// </summary>
    public void StartContinuousRead(byte pipe, int maxLength)
    {
        if (_reader is not null) return;
        uint infinite = 0;
        Native.WinUsb_SetPipePolicy(_usb, pipe, Native.PIPE_TRANSFER_TIMEOUT, sizeof(uint), ref infinite);
        _replies = new(new System.Collections.Concurrent.ConcurrentQueue<byte[]>(), boundedCapacity: 64);
        _reader = new Thread(() =>
        {
            var buf = new byte[maxLength];
            while (!_disposed)
            {
                if (!Native.WinUsb_ReadPipe(_usb, pipe, buf, (uint)maxLength, out var read, IntPtr.Zero))
                {
                    if (_disposed) break;
                    Thread.Sleep(50); // transient error (e.g. device removal); avoid a hot loop
                    continue;
                }
                if (read > 0 && !_replies.TryAdd(buf[..(int)read])) { _replies.TryTake(out _); _replies.TryAdd(buf[..(int)read]); }
            }
        }) { IsBackground = true, Name = "WinUSB reader " + pipe.ToString("X2") };
        _reader.Start();
    }

    /// <summary>Waits for the next queued transfer from the continuous reader. Null on timeout.</summary>
    public byte[]? TakeReply(TimeSpan timeout) =>
        _replies is { } q && q.TryTake(out var r, timeout) ? r : null;

    /// <summary>Discards queued transfers (late replies to earlier commands). Returns how many were dropped.</summary>
    public int DrainReplies()
    {
        int n = 0;
        while (_replies is { } q && q.TryTake(out _)) n++;
        return n;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Native.WinUsb_AbortPipe(_usb, 0x81);
        _reader?.Join(TimeSpan.FromSeconds(2));
        Native.WinUsb_Free(_usb);
        _file.Dispose();
        _replies?.Dispose();
    }

    private static class Native
    {
        public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
        public const uint FILE_ATTRIBUTE_NORMAL = 0x80, FILE_FLAG_OVERLAPPED = 0x40000000;
        public const uint DIGCF_PRESENT = 2, DIGCF_DEVICEINTERFACE = 0x10;
        public const uint PIPE_TRANSFER_TIMEOUT = 3;
        public const int ERROR_SEM_TIMEOUT = 121;

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr parent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint size, out uint required, IntPtr devInfo);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_Initialize(SafeFileHandle file, out IntPtr handle);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_Free(IntPtr handle);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_SetPipePolicy(IntPtr handle, byte pipe, uint policy, uint length, ref uint value);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_WritePipe(IntPtr handle, byte pipe, byte[] buffer, uint length, out uint transferred, IntPtr overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_ReadPipe(IntPtr handle, byte pipe, byte[] buffer, uint length, out uint transferred, IntPtr overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_FlushPipe(IntPtr handle, byte pipe);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_ResetPipe(IntPtr handle, byte pipe);

        [DllImport("winusb.dll", SetLastError = true)]
        public static extern bool WinUsb_AbortPipe(IntPtr handle, byte pipe);
    }
}

public sealed class DeviceBusyException(string path, int win32Error)
    : IOException($"Device is in use by another process or access was denied (Win32 error {win32Error}): {path}")
{
    public int Win32Error { get; } = win32Error;
}
