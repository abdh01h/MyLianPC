using System.Reflection;
using System.Runtime.InteropServices;

namespace MyLianPC.Hardware.Smbus;

/// <summary>
/// SMBus access through the signed PawnIO driver (https://pawnio.eu) using its PIIX4 module (AMD FCH).
/// The module blob is the one shipped inside LibreHardwareMonitorLib. Every transfer takes the
/// machine-wide <c>Global\Access_SMBUS.HTP.Method</c> mutex shared by OpenRGB, HWiNFO, LibreHardwareMonitor
/// and others, so well-behaved tools never interleave transactions on the bus.
/// Requires administrator / LocalSystem.
/// </summary>
public sealed class PawnIoSmbus : IDisposable
{
    public const string GlobalMutexName = @"Global\Access_SMBUS.HTP.Method";
    private const string PiixModuleResource = "LibreHardwareMonitor.Resources.PawnIo.SmbusPIIX4.bin";

    private const int Read = 1, Write = 0;
    private const int ByteData = 2, WordData = 3, BlockData = 5;

    private readonly IntPtr _handle;
    private readonly Mutex _mutex;
    private bool _disposed;

    public string Name { get; }
    public int Port { get; }

    private PawnIoSmbus(IntPtr handle, int port, string name)
    {
        _handle = handle;
        Port = port;
        Name = name;
        _mutex = new Mutex(false, GlobalMutexName);
    }

    public static bool IsDriverInstalled => File.Exists(Native.LibraryPath);

    /// <summary>Opens the AMD PIIX4 SMBus on the given port (0 = DIMMs on AM5 boards).</summary>
    public static PawnIoSmbus OpenPiix4(int port = 0)
    {
        if (!IsDriverInstalled) throw new PawnIoUnavailableException("PawnIO is not installed (expected " + Native.LibraryPath + ").");
        var blob = LoadModuleBlob(PiixModuleResource);
        Check(Native.Open(out var handle), "pawnio_open");
        try
        {
            Check(Native.Load(handle, blob, (nuint)blob.Length), "pawnio_load(SmbusPIIX4)");
            using (var m = new Mutex(false, GlobalMutexName))
            {
                m.WaitOne(TimeSpan.FromSeconds(5));
                try { Execute(handle, "ioctl_piix4_port_sel", [(ulong)port], 1); }
                finally { m.ReleaseMutex(); }
            }
            var id = Execute(handle, "ioctl_identity", [0], 3);
            var name = new string([.. BitConverter.GetBytes(id[0]).TakeWhile(b => b != 0).Select(b => (char)b)]);
            return new PawnIoSmbus(handle, port, $"PawnIO SMBus {name} port {port} (PCI {id[2] & 0xFFFF:X4}:{(id[2] >> 16) & 0xFFFF:X4})");
        }
        catch
        {
            Native.Close(handle);
            throw;
        }
    }

    private static byte[] LoadModuleBlob(string resource)
    {
        var asm = typeof(LibreHardwareMonitor.Hardware.Computer).Assembly;
        using var s = asm.GetManifestResourceStream(resource) ?? throw new PawnIoUnavailableException($"PawnIO module '{resource}' not found in LibreHardwareMonitorLib.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public int ReadByte(byte address, byte command)
    {
        var data = Xfer(address, Read, command, ByteData, null);
        return data is null ? -1 : data[0];
    }

    public bool WriteByte(byte address, byte command, byte value) => Xfer(address, Write, command, ByteData, [value]) is not null;

    public bool WriteWord(byte address, byte command, ushort value) =>
        Xfer(address, Write, command, WordData, [(byte)value, (byte)(value >> 8)]) is not null;

    /// <summary>SMBus block write: the controller sends a length byte followed by up to 32 data bytes.</summary>
    public bool WriteBlock(byte address, byte command, ReadOnlySpan<byte> values)
    {
        if (values.Length is 0 or > 32) throw new ArgumentOutOfRangeException(nameof(values), "SMBus blocks carry 1-32 bytes.");
        var block = new byte[values.Length + 1];
        block[0] = (byte)values.Length;
        values.CopyTo(block.AsSpan(1));
        return Xfer(address, Write, command, BlockData, block) is not null;
    }

    /// <summary>
    /// One i2c_smbus_xfer. Inputs: addr, read_write, command, size, then the 34-byte i2c_smbus_data union
    /// packed into 5 ULONG64s. Returns the union bytes, or null if the device NAKed / the transfer failed.
    /// </summary>
    private byte[]? Xfer(byte address, int readWrite, byte command, int size, byte[]? data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Hard guard: never write to the SPD EEPROM / SPD hub address range.
        if (readWrite == Write && address is >= 0x50 and <= 0x57)
            throw new InvalidOperationException($"Refusing SMBus write to SPD address 0x{address:X2}.");

        var input = new ulong[9];
        input[0] = address;
        input[1] = (ulong)readWrite;
        input[2] = command;
        input[3] = (ulong)size;
        if (data is not null)
        {
            var union = new byte[40];
            data.CopyTo(union, 0);
            Buffer.BlockCopy(union, 0, input, 4 * sizeof(ulong), 40);
        }

        if (!_mutex.WaitOne(TimeSpan.FromSeconds(2))) return null;
        try
        {
            var output = new ulong[5];
            int hr = Native.Execute(_handle, "ioctl_smbus_xfer", input, (nuint)input.Length, output, (nuint)output.Length, out _);
            if (hr != 0) return null;
            var bytes = new byte[40];
            Buffer.BlockCopy(output, 0, bytes, 0, 40);
            return bytes;
        }
        catch (AbandonedMutexException) { return null; }
        finally { try { _mutex.ReleaseMutex(); } catch (ApplicationException) { } }
    }

    private static ulong[] Execute(IntPtr handle, string fn, ulong[] input, int outCount)
    {
        var output = new ulong[outCount];
        Check(Native.Execute(handle, fn, input, (nuint)input.Length, output, (nuint)outCount, out _), fn);
        return output;
    }

    private static void Check(int hr, string what)
    {
        if (hr == unchecked((int)0x80070005)) throw new PawnIoUnavailableException($"{what}: access denied (administrator / LocalSystem required).");
        if (hr != 0) throw new PawnIoUnavailableException($"{what} failed (HRESULT 0x{hr:X8}).");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Native.Close(_handle);
        _mutex.Dispose();
    }

    private static class Native
    {
        public static readonly string LibraryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO", "PawnIOLib.dll");

        static Native() => NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, Resolve);

        private static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path) =>
            name == "PawnIOLib" && File.Exists(LibraryPath) ? NativeLibrary.Load(LibraryPath) : IntPtr.Zero;

        [DllImport("PawnIOLib", EntryPoint = "pawnio_open")]
        public static extern int Open(out IntPtr handle);

        [DllImport("PawnIOLib", EntryPoint = "pawnio_load")]
        public static extern int Load(IntPtr handle, byte[] blob, nuint size);

        [DllImport("PawnIOLib", EntryPoint = "pawnio_execute", CharSet = CharSet.Ansi)]
        public static extern int Execute(IntPtr handle, string name, ulong[] input, nuint inSize, ulong[] output, nuint outSize, out nuint returnSize);

        [DllImport("PawnIOLib", EntryPoint = "pawnio_close")]
        public static extern int Close(IntPtr handle);
    }
}

public sealed class PawnIoUnavailableException(string message) : Exception(message);
