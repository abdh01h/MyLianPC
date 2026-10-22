namespace MyLianPC.Hardware.Smbus;

/// <summary>
/// In-process lock serialising everything that can touch the SMBus: LibreHardwareMonitor (DIMM temperature
/// reads) and the Corsair DRAM driver. The machine-wide mutex in <see cref="PawnIoSmbus"/> covers other processes.
/// </summary>
public static class SmbusGate
{
    public static readonly object Lock = new();
}
