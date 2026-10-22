using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using HidSharp;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using MyLianPC.Core.Cooling;
using MyLianPC.Core.Ipc;
using MyLianPC.Core.Lighting;
using MyLianPC.Core.Profiles;
using MyLianPC.Core.Sensors;
using MyLianPC.Hardware.Adapters;
using MyLianPC.Hardware.Corsair;
using MyLianPC.Hardware.Gigabyte;
using MyLianPC.Hardware.LianLi;
using MyLianPC.Hardware.Smbus;
using MyLianPC.Lcd;

// Hardware probe for Phase 1/2. Every command here is read-only unless its name says otherwise.
var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "all";
var elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
Console.WriteLine($"MyLianPC probe | user={Environment.UserName} session={System.Diagnostics.Process.GetCurrentProcess().SessionId} elevated={elevated}");

switch (cmd)
{
    case "hid": Hid(); break;
    case "fusion2": Fusion2Info(); break;
    case "lcd": LianLiWinUsb(); break;
    case "sensors": Sensors(); break;
    case "all": Hid(); Fusion2Info(); LianLiWinUsb(); Sensors(); break;
    case "make-icon": MakeIcon(args.ElementAtOrDefault(1) ?? "app.ico"); break;
    case "lcd-sheet": LcdSheet(args.ElementAtOrDefault(1) ?? "lcd-sheet.png"); break;
    case "lcd-preview": LcdPreview(args.ElementAtOrDefault(1) ?? "minimal-temps", args.ElementAtOrDefault(2) ?? "lcd-preview.png", args.ElementAtOrDefault(3) == "12h"); break;
    case "lcd-demo": LcdDemo(int.TryParse(args.ElementAtOrDefault(1), out var secs) ? secs : 30, args.ElementAtOrDefault(2) ?? "minimal-temps"); break;
    case "cooler-wake": CoolerWake(); break;
    case "ipc": await Ipc(args.Skip(1).ToArray()); break;
    case "cooler-pump": CoolerPump(args); break;
    case "cooler-echo": CoolerEcho(); break;
    case "cooler-hold": CoolerHold(args); break;
    case "cooler-diag": CoolerDiag(); break;
    case "ram-detect": RamDetect(null); break;
    case "ram-static": RamStatic(args); break;
    case "ram-reg": RamReg(args); break;
    case "ram-dump": RamDump(); break;
    case "mem-info": MemInfo(); break;
    case "drives": Drives(); break;
    case "ram-color": RamDetect(RgbColor.ParseHex(args.ElementAtOrDefault(1) ?? "#FF0000")); break;
    case "mb-gen2": MbGen2(); break;
    case "mb-info": MbInfo(); break;
    case "mb-identify": MbIdentify(); break;
    case "mb-zones": MbZones(RgbColor.ParseHex(args.ElementAtOrDefault(1) ?? "#00FF00"), int.TryParse(args.ElementAtOrDefault(2), out var maxIdx) ? maxIdx : 10); break;
    case "mb-color": MbColor(RgbColor.ParseHex(args.ElementAtOrDefault(1) ?? "#FF0000")); break;
    case "ring-color": RingColor(RgbColor.ParseHex(args.ElementAtOrDefault(1) ?? "#FF0000"), args.ElementAtOrDefault(2) == "rainbow"); break;
    case "mb-regs": MbRegs(args); break;
    default: Console.WriteLine("usage: probe [hid|fusion2|lcd|sensors|all|lcd-preview <theme> <file.png>|lcd-demo <seconds> <theme>|ram-detect|ram-dump|ram-color <hex>|ram-static <addr|all> <hex> <leds> <brightness> [select]|ram-reg <addr> [switch]|mb-gen2|mb-color <hex>|mb-regs [hex...]|ring-color <hex> [rainbow]]"); break;
}

// Corsair DRAM over PawnIO SMBus. Without a color: read-only detection. With a color: stores a static color on every module.
static void RamDetect(RgbColor? color)
{
    Section(color is null ? "Corsair DRAM detection (read-only)" : $"Corsair DRAM static color {color}");
    using var bus = PawnIoSmbus.OpenPiix4(0);
    Console.WriteLine(bus.Name);
    var modules = CorsairDramController.Detect(bus);
    if (modules.Count == 0) Console.WriteLine("no Corsair DRAM RGB controllers answered at 0x58-0x5F / 0x18-0x1F");
    foreach (var m in modules)
    {
        Console.WriteLine($"addr=0x{m.Address:X2} vid=0x{m.Vid:X4} pid=0x{m.Pid:X4} fw={m.FirmwareVersion} protocol={m.ProtocolVersion} infoCrcOk={m.DeviceInfoValid}");
        if (color is { } c) Console.WriteLine($"  set static {c}: {(m.SetStaticColors([c]) ? "ACK (checksum matched, committed)" : "REJECTED")}");
    }
}

// One-off diagnosis: store a static colour with an explicit LED count and effect brightness on one module.
// usage: ram-static <addr|all> <#hex> <ledCount> <effectBrightness>
static void RamStatic(string[] a)
{
    var addr = a.ElementAtOrDefault(1) ?? "all";
    var color = RgbColor.ParseHex(a.ElementAtOrDefault(2)?.Split(',')[0] ?? "#00FF00");
    var palette = (a.ElementAtOrDefault(2) ?? "#00FF00").Split(',').Select(RgbColor.ParseHex).ToList();
    int leds = int.TryParse(a.ElementAtOrDefault(3), out var n) ? n : 10;
    byte bright = (byte)(int.TryParse(a.ElementAtOrDefault(4), out var b) ? Math.Clamp(b, 0, 255) : 0);
    bool selectBuffer = a.ElementAtOrDefault(5) == "select";
    var log = @"C:\Users\Public\mylianpc-ram-test.log";
    void Write(string s) { Console.WriteLine(s); File.AppendAllText(log, s + Environment.NewLine); }
    File.WriteAllText(log, $"ram-static {addr} {color} leds={leds} brightness={bright} at {DateTime.Now:HH:mm:ss}{Environment.NewLine}");
    using var bus = PawnIoSmbus.OpenPiix4(0);
    foreach (var m in CorsairDramController.Detect(bus))
    {
        if (!addr.Equals("all", StringComparison.OrdinalIgnoreCase) && m.Address.ToString("X2") != addr.ToUpperInvariant()) continue;
        Write($"module 0x{m.Address:X2} protocol={m.ProtocolVersion}");
        if (selectBuffer) Write($"  select effect buffer (0x26=1): {(bus.WriteByte(m.Address, 0x26, 1) ? "sent" : "failed")}");
        Write($"  effect static brightness {bright}: {(m.SetEffect(CorsairDramMode.Static, 0, 2, false, bright, RgbColor.Black, RgbColor.Black) ? "ACK" : "REJECTED")}");
        Thread.Sleep(15);
        if (selectBuffer) Write($"  select colour buffer (0x26=2): {(bus.WriteByte(m.Address, 0x26, 2) ? "sent" : "failed")}");
        Write($"  colors {string.Join(",", palette)} x{leds}: {(m.UploadStaticColors(palette, leds) ? "ACK" : "REJECTED")}");
    }
}

// Registers of one Corsair DRAM module: reads 0x23 (mode), 0x30 (status), 0x41 (busy) and can write 0x23.
// usage: ram-reg <addr> [switchToModeNormal]
static void RamReg(string[] a)
{
    var addr = a.ElementAtOrDefault(1) ?? "19";
    bool switchMode = a.ElementAtOrDefault(2) is "switch" or "1";
    var log = @"C:\Users\Public\mylianpc-ram-reg.log";
    void Write(string s) { Console.WriteLine(s); File.AppendAllText(log, s + Environment.NewLine); }
    File.WriteAllText(log, $"ram-reg {addr} switch={switchMode} at {DateTime.Now:HH:mm:ss}{Environment.NewLine}");
    using var bus = PawnIoSmbus.OpenPiix4(0);
    var modules = CorsairDramController.Detect(bus);
    foreach (var m in modules)
    {
        Write($"module 0x{m.Address:X2} protocol={m.ProtocolVersion} pid=0x{m.Pid:X4} info={Convert.ToHexString(m.DeviceInfo)}");
        if (!m.Address.ToString("X2").Equals(addr, StringComparison.OrdinalIgnoreCase)) continue;
        Write($"  0x23 (mode: 0=bootloader, 1=normal) = 0x{bus.ReadByte(m.Address, 0x23):X2}");
        Write($"  0x30 (status) = 0x{bus.ReadByte(m.Address, 0x30):X2}");
        Write($"  0x41 (busy)   = 0x{bus.ReadByte(m.Address, 0x41):X2}");
        if (switchMode)
        {
            bus.WriteByte(m.Address, 0x23, 1);
            Thread.Sleep(100);
            Write($"  after 0x23=1: 0x23=0x{bus.ReadByte(m.Address, 0x23):X2} 0x30=0x{bus.ReadByte(m.Address, 0x30):X2}");
        }
    }
}

// Reads back controller registers (default: the effect blocks 0x20-0x27 plus 0x31/0x32/0x60/0x61).
static void MbRegs(string[] a)
{
    Section("Fusion2 register read-back");
    var dev = Fusion2Controller.Find();
    if (dev is null) { Console.WriteLine("no Fusion2 controller"); return; }
    using var ctl = Fusion2Controller.Open(dev);
    var regs = a.Skip(1).Select(x => Convert.ToByte(x, 16)).ToList();
    if (regs.Count == 0) regs = [.. Enumerable.Range(0x20, 8).Select(x => (byte)x), 0x31, 0x32, 0x60, 0x61];
    foreach (var reg in regs)
    {
        byte[] buf;
        try { buf = ctl.ReadBack(reg); }
        catch (Exception e) { Console.WriteLine($"  0x{reg:X2}: FAIL {e.Message}"); continue; }
        Console.WriteLine($"  0x{reg:X2}: {Convert.ToHexString(buf)}");
    }
}

// Read-only: every drive LibreHardwareMonitor finds, with all of its sensors (temperature, life, used space...).
static void Drives()
{
    Section("Drives (Windows storage stack: temperature, health, capacity)");
    var wmi = MyLianPC.Hardware.Sensors.StorageWmi.Read();
    if (wmi.Count == 0) Console.WriteLine("no drives answered (needs administrator or LocalSystem)");
    if (MyLianPC.Hardware.Sensors.StorageWmi.LastError is { } error) Console.WriteLine($"last error: {error}");
    foreach (var (name, d) in wmi)
        Console.WriteLine($"{name}\n   temp={(d.TemperatureCelsius is { } t ? $"{t:0.#} C" : "-")} wear={(d.WearPercent is { } w ? $"{w:0}%" : "-")} health={d.HealthState ?? "-"}" +
                          $" size={(d.TotalBytes is { } s ? MyLianPC.Core.Sensors.Sizes.Capacity(s) : "-")}" +
                          $" free={(d.FreeBytes is { } f ? MyLianPC.Core.Sensors.Sizes.Capacity(f) : "-")} volumes={d.Volumes ?? "-"}");

    Section("Drives (LibreHardwareMonitor)");
    var computer = new Computer { IsStorageEnabled = true };
    computer.Open();
    foreach (var hw in computer.Hardware.Where(h => h.HardwareType == HardwareType.Storage))
    {
        hw.Update();
        Console.WriteLine($"{hw.Name}  [{hw.HardwareType}]");
        foreach (var s in hw.Sensors) Console.WriteLine($"   {s.SensorType,-12} {s.Name,-24} {(s.Value is { } v ? v.ToString("0.##") : "null")}");
    }
    computer.Close();
}

// Read-only: memory modules as the firmware reports them in SMBIOS (rated vs. configured speed, EXPO/XMP state).
static void MemInfo()
{
    Section("Memory modules (SMBIOS)");
    var info = MyLianPC.Hardware.Sensors.SmbiosMemoryInfo.Read();
    if (info.Count == 0) Console.WriteLine("no memory device structures in the SMBIOS table");
    foreach (var m in info.Modules)
        Console.WriteLine($"type=0x{m.MemoryType:X2} rated={m.SpeedMtps} MT/s configured={m.ConfiguredMtps} MT/s (JEDEC baseline {MyLianPC.Hardware.Sensors.SmbiosMemoryInfo.JedecBaselineMtps(m.MemoryType)})");
    Console.WriteLine($"modules={info.Count} speed={info.SpeedMtps} configured={info.ConfiguredMtps} expo/xmp={(info.Count == 0 ? "?" : info.XmpExpoActive ? "on" : "off")}");
}

// Read-only: device info and the stored effect/color buffers of every Corsair DRAM module.
static void RamDump()
{
    Section("Corsair DRAM dump (read-only)");
    using var bus = PawnIoSmbus.OpenPiix4(0);
    foreach (var m in CorsairDramController.Detect(bus))
    {
        Console.WriteLine($"addr=0x{m.Address:X2} vid=0x{m.Vid:X4} pid=0x{m.Pid:X4} fw={m.FirmwareVersion} protocol={m.ProtocolVersion} infoCrcOk={m.DeviceInfoValid}");
        Console.WriteLine($"  info   {Convert.ToHexString(m.DeviceInfo)}");
        var (effect, effectSum) = m.ReadConfiguration(1, 20);
        Console.WriteLine($"  effect {Convert.ToHexString(effect)} module crc=0x{effectSum:X2} crc(20)=0x{CorsairDramController.Crc8(effect):X2}");
        var (colors, colorsSum) = m.ReadConfiguration(2, 48);
        Console.WriteLine($"  colors {Convert.ToHexString(colors)} module crc=0x{colorsSum:X2} crc(40)=0x{CorsairDramController.Crc8(colors.AsSpan(0, 40)):X2} crc(48)=0x{CorsairDramController.Crc8(colors):X2}");
    }
}

// Sweeps commanded duty over the whole range for the pump and for the radiator fans, printing the speeds the cooler
// reports, then restores the profile. Shows whether (and how) each channel follows the commanded duty.
static async Task CoolingSweep(MyLianPC.Core.Ipc.ServiceClient client, string[] args)
{
    var doc = await client.ProfilesAsync();
    var active = doc.Profiles.First(x => x.Id == doc.ActiveProfileId);
    string backup = Path.Combine(Path.GetTempPath(), "mylianpc-cooling-backup.json");
    File.WriteAllText(backup, System.Text.Json.JsonSerializer.Serialize(active, IpcJson.Options));

    string mode = args.ElementAtOrDefault(1) ?? "both";
    int settle = int.TryParse(args.ElementAtOrDefault(2), out var st) ? st : 30;
    var duties = (args.ElementAtOrDefault(3) ?? "20,40,60,80,100").Split(',').Select(int.Parse).ToArray();
    // The channel that is not being swept is pinned, so only the swept channel's speed can change.
    int pinPump = 60, pinFans = 40;

    async Task Save(int pumpDuty, int fansDuty)
    {
        var curves = new Dictionary<string, FanCurve>(active.Cooling?.Curves ?? new Dictionary<string, FanCurve>());
        FanCurve Fixed(string id, int duty)
        {
            var c = curves.TryGetValue(id, out var cur) ? cur : CoolingPresets.Balanced(TemperatureSource.Coolant);
            return c with
            {
                Points = [new(20, duty), new(80, duty), new(FanCurve.CriticalTemperatureC, 100)],
                MinimumDutyPercent = Math.Min(c.MinimumDutyPercent, duty),
            };
        }
        if (mode == "pump") curves[HydroShiftAdapter.FansTarget] = Fixed(HydroShiftAdapter.FansTarget, pinFans);
        if (mode == "fans") curves[HydroShiftAdapter.PumpTarget] = Fixed(HydroShiftAdapter.PumpTarget, pinPump);
        if (mode is "both" or "pump" or "matrix") curves[HydroShiftAdapter.PumpTarget] = Fixed(HydroShiftAdapter.PumpTarget, pumpDuty);
        if (mode is "both" or "fans" or "matrix") curves[HydroShiftAdapter.FansTarget] = Fixed(HydroShiftAdapter.FansTarget, fansDuty);
        await client.SaveProfileAsync(active with { Cooling = (active.Cooling ?? new CoolingSettings()) with { Curves = curves } });
    }

    async Task<(string Text, double? Pump, double?[] Fans)> Sample()
    {
        var s = await client.SensorsAsync();
        double? Get(string key) => s.Values.TryGetValue(key, out var v) ? v : null;
        var fans = new[] { Metrics.AioFan1, Metrics.AioFan2, Metrics.AioFan3 }.Select(Get).ToArray();
        static string Fmt(double? v) => v is { } d ? $"{d,5:0}" : "    -";
        return ($"{Fmt(Get(Metrics.PumpRpm))} {Fmt(fans[0])} {Fmt(fans[1])} {Fmt(fans[2])}", Get(Metrics.PumpRpm), fans);
    }

    Console.WriteLine($"  mode={mode} settle={settle}s pinned pump={pinPump}% fans={pinFans}%");
    Console.WriteLine("  duty | pump  fan1  fan2  fan3   (rpm, average of 3 samples)   last sample");
    var steps = mode == "matrix"
        ? new[] { (20, 20), (20, 100), (100, 20), (100, 100) }
        : duties.Select(d => mode == "pump" ? (d, pinFans) : (mode == "fans" ? (pinPump, d) : (d, d))).ToArray();
    foreach (var (pumpDuty, fansDuty) in steps)
    {
        await Save(pumpDuty, fansDuty);
        await Task.Delay(settle * 1000);
        var samples = new List<(string Text, double? Pump, double?[] Fans)>();
        for (int i = 0; i < 3; i++)
        {
            samples.Add(await Sample());
            await Task.Delay(4000);
        }
        double? Avg(Func<(string Text, double? Pump, double?[] Fans), double?> pick)
        {
            var values = samples.Select(pick).Where(v => v is not null).Select(v => v!.Value).ToArray();
            return values.Length == 0 ? null : values.Average();
        }
        static string F(double? v) => v is { } d ? $"{d,5:0}" : "    -";
        var last = await Sample();
        Console.WriteLine($"  p{pumpDuty,3}/f{fansDuty,3} | {F(Avg(x => x.Pump))} {F(Avg(x => x.Fans[0]))} {F(Avg(x => x.Fans[1]))} {F(Avg(x => x.Fans[2]))}   {last.Text}");
    }

    var restored = System.Text.Json.JsonSerializer.Deserialize<HardwareProfile>(File.ReadAllText(backup), IpcJson.Options)!;
    await client.SaveProfileAsync(restored);
    await Task.Delay(8000);
    Console.WriteLine("  restored | " + (await Sample()).Text);
}

// Talks to the running service over the named pipe (works as a standard user).
static async Task Ipc(string[] a)
{
    Section("Service IPC");
    await using var client = new MyLianPC.Core.Ipc.ServiceClient();
    if (a.ElementAtOrDefault(0) == "activate")
    {
        var doc = await client.ProfilesAsync();
        var p = doc.Profiles.First(x => x.Name.Equals(a[1], StringComparison.OrdinalIgnoreCase));
        var rep = await client.ActivateAsync(p.Id);
        foreach (var r in rep.Results) Console.WriteLine($"  {(r.Success ? "OK  " : "FAIL")} {r.DeviceId} / {r.Part}: {r.Message}");
        return;
    }
    if (a.ElementAtOrDefault(0) == "fan-lighting")
    {
        var cur = await client.StatusAsync();
        var after = await client.ConfigureDevicesAsync(cur.DeviceSettings with { CoolerFanLighting = a.ElementAtOrDefault(1) != "off" });
        Console.WriteLine($"  CoolerFanLighting now {after.DeviceSettings.CoolerFanLighting}");
        return;
    }
    if (a.ElementAtOrDefault(0) == "resave")
    {
        var doc = await client.ProfilesAsync();
        var p = doc.Profiles.First(x => x.Name.Equals(a[1], StringComparison.OrdinalIgnoreCase));
        try { var r = (await client.SaveProfileAsync(p)).ToString(); Console.WriteLine("  saved: " + r[..Math.Min(300, r.Length)]); }
        catch (Exception e) { Console.WriteLine($"  FAIL {e.GetType().Name}: {e.Message}"); }
        return;
    }
    if (a.ElementAtOrDefault(0) == "cooling-sweep")
    {
        await CoolingSweep(client, a);
        return;
    }
    if (a.ElementAtOrDefault(0) == "rescan")
    {
        var after = await client.RescanAsync();
        Console.WriteLine("  devices: " + string.Join(", ", after.Devices.Select(d => $"{d.Name}={d.State}")));
        return;
    }
    if (a.ElementAtOrDefault(0) == "zones")
    {
        // ipc zones <zoneId>=<#hex> ... - per-zone static colours on the ACTIVE profile (SyncAll cleared)
        var doc = await client.ProfilesAsync();
        var active = doc.Profiles.First(x => x.Id == doc.ActiveProfileId);
        var zones = new Dictionary<string, ZoneLighting>();
        foreach (var spec in a.Skip(1))
        {
            var parts = spec.Split('=', 2);
            var value = parts[1];
            var effect = LightingEffect.Static;
            var colon = value.IndexOf(':');
            if (colon > 0)
            {
                effect = Enum.Parse<LightingEffect>(value[(colon + 1)..], ignoreCase: true);
                value = value[..colon];
            }
            zones[parts[0]] = new ZoneLighting { Effect = effect, Colors = [RgbColor.ParseHex(value)], Brightness = 100 };
        }
        var updated = active with { Lighting = (active.Lighting ?? new MyLianPC.Core.Profiles.LightingSettings()) with { SyncAll = null, Zones = zones } };
        var report = await client.SaveProfileAsync(updated);
        Console.WriteLine("  " + report);
        return;
    }
    if (a.ElementAtOrDefault(0) == "color")
    {
        // ipc color <#hex> [brightness] [effect] - edits the ACTIVE profile's shared colour and applies it
        var doc = await client.ProfilesAsync();
        var active = doc.Profiles.First(x => x.Id == doc.ActiveProfileId);
        var cur = active.Lighting?.SyncAll ?? new ZoneLighting();
        var updated = active with
        {
            Lighting = (active.Lighting ?? new MyLianPC.Core.Profiles.LightingSettings()) with
            {
                SyncAll = cur with
                {
                    Effect = Enum.Parse<LightingEffect>(a.ElementAtOrDefault(3) ?? "Static", ignoreCase: true),
                    Colors = [RgbColor.ParseHex(a[1])],
                    Brightness = int.TryParse(a.ElementAtOrDefault(2), out var bright) ? bright : 100,
                },
                Zones = new Dictionary<string, ZoneLighting>(),
            },
        };
        var report = await client.SaveProfileAsync(updated);
        Console.WriteLine("  " + report);
        return;
    }
    var st = await client.StatusAsync();
    Console.WriteLine($"service {st.Version} pawnio={st.PawnIoInstalled} active={st.ActiveProfileName ?? "(none)"}");
    foreach (var d in st.Devices)
        Console.WriteLine($"  [{d.State}] {d.Name} zones={d.Zones.Count} fans={d.Fans.Count} {(d.Error is null ? "" : "error=" + d.Error)}");
    foreach (var w in st.Warnings) Console.WriteLine($"  warning: {w}");
    var sn = await client.SensorsAsync();
    Console.WriteLine("  " + string.Join("  ", sn.Values.Where(v => v.Value is not null).Select(v => $"{v.Key}={v.Value:0.#}")));
    foreach (var d in sn.Storage)
        Console.WriteLine($"  drive {d.Name}: temp={(d.TemperatureCelsius is { } t ? $"{t:0}°C" : "?")} health={(d.HealthPercent is { } h ? $"{h:0}%" : "not reported")}");
    var docs = await client.ProfilesAsync();
    Console.WriteLine("  profiles: " + string.Join(", ", docs.Profiles.Select(x => x.Name)));
}

static void CoolerWake()
{
    Section("HydroShift II wake (StopPlay, StopClock, GetVer)");
    using var dev = HydroShift2Device.Open(HydroShift2Device.Find().FirstOrDefault() ?? throw new InvalidOperationException("cooler not found"));
    dev.Log = m => Console.WriteLine("  [h2] " + m);
    Thread.Sleep(1500); // let the continuous reader collect anything the device still had queued
    bool ok = dev.TryWake();
    Console.WriteLine(ok ? $"RESPONSIVE firmware={dev.FirmwareVersion ?? "?"}" : "UNRESPONSIVE (needs a power cycle)");
    if (ok) Console.WriteLine($"telemetry: {dev.ReadTelemetry()?.ToString() ?? "n/a"}");
}

// Sends raw SyncPumpFan control values so the firmware's own pump/fan mapping can be measured directly.
// cooler-pump <value,value,...> [fanPercent] [settleSeconds]
static void CoolerPump(string[] args)
{
    var values = (args.ElementAtOrDefault(1) ?? "0,150,300,450,600,750,900,1050,1200,1500").Split(',').Select(int.Parse).ToArray();
    byte fan = (byte)Math.Round((double)(int.TryParse(args.ElementAtOrDefault(2), out var f) ? f : 50));
    int settle = int.TryParse(args.ElementAtOrDefault(3), out var s) ? s : 18;
    Section($"HydroShift II raw pump control sweep ({values.Length} values, fans {fan * 100 / 255}%)");
    using var dev = HydroShift2Device.Open(HydroShift2Device.Find().FirstOrDefault() ?? throw new InvalidOperationException("cooler not found"));
    dev.Log = m => Console.WriteLine("  [h2] " + m);
    dev.Initialize();

    // Same layout as HydroShift2Protocol.SyncPumpFanParameters but with the control value taken verbatim.
    byte[] Frame(ushort value)
    {
        var c = new byte[16];
        c[0] = 0xFF; c[1] = 0x0F; c[2] = 0xA2;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(c.AsSpan(4), value);
        c[6] = fan; c[7] = fan; c[8] = fan;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(c.AsSpan(12), HydroShift2Protocol.Crc16(c.AsSpan(0, 12)));
        return HydroShift2Protocol.Command(HydroShift2Protocol.Cmd.SyncPumpFan, c);
    }

    foreach (var value in values)
    {
        var (reply, latency, stale) = dev.Probe(Frame((ushort)value), TimeSpan.FromSeconds(3));
        Console.WriteLine($"--- pump value 0x{value:X3} ({value}) ack={(reply is null ? "NONE" : Convert.ToHexString(reply.AsSpan(0, Math.Min(8, reply.Length))))} stale={stale} {latency.TotalMilliseconds:0}ms");
        Thread.Sleep(settle * 1000);
        for (int i = 0; i < 2; i++)
        {
            var t = dev.ReadTelemetry();
            Console.WriteLine(t is null ? "  telemetry n/a" : $"  coolant {t.CoolantC}C pump {t.PumpRpm} fans {t.FanRpm[0]}/{t.FanRpm[1]}/{t.FanRpm[2]}");
            Thread.Sleep(2500);
        }
    }
}

// Writes a distinctive pump value and fan duties, then dumps the full GetParams frame to see whether the firmware
// stored what was written (i.e. whether the write is accepted for control or ignored).
static void CoolerEcho()
{
    Section("HydroShift II SyncPumpFan echo test");
    using var dev = HydroShift2Device.Open(HydroShift2Device.Find().FirstOrDefault() ?? throw new InvalidOperationException("cooler not found"));
    dev.Log = m => Console.WriteLine("  [h2] " + m);
    dev.Initialize();

    void Dump(string label)
    {
        var (reply, _, stale) = dev.Probe(HydroShift2Protocol.Command(HydroShift2Protocol.Cmd.GetParams), TimeSpan.FromSeconds(3));
        Console.WriteLine($"  {label}: {(reply is null ? "no reply" : Convert.ToHexString(reply))}{(stale > 0 ? " (stale)" : "")}");
    }

    Dump("before");
    byte[] Frame(ushort value, byte f1, byte f2, byte f3)
    {
        var c = new byte[16];
        c[0] = 0xFF; c[1] = 0x0F; c[2] = 0xA2;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(c.AsSpan(4), value);
        c[6] = f1; c[7] = f2; c[8] = f3;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(c.AsSpan(12), HydroShift2Protocol.Crc16(c.AsSpan(0, 12)));
        return HydroShift2Protocol.Command(HydroShift2Protocol.Cmd.SyncPumpFan, c);
    }
    var (ack, _, _) = dev.Probe(Frame(0x04D2, 0xC8, 0x64, 0x32), TimeSpan.FromSeconds(3));
    Console.WriteLine($"  write pump 0x04D2 fans C8/64/32 -> ack={(ack is null ? "NONE" : Convert.ToHexString(ack))}");
    Thread.Sleep(3000);
    Dump("after 1");
    Thread.Sleep(5000);
    Dump("after 2");
    Thread.Sleep(8000);
    Dump("after 3");
}

// Holds one raw pump value and one raw fan duty and samples the speeds every 10 s, so slow firmware ramps are visible.
// cooler-hold <pumpValue> <fanRaw> [seconds] [repeat]
static void CoolerHold(string[] args)
{
    ushort value = (ushort)(int.TryParse(args.ElementAtOrDefault(1), out var v) ? v : 0);
    byte fan = (byte)(int.TryParse(args.ElementAtOrDefault(2), out var f) ? f : 255);
    int seconds = int.TryParse(args.ElementAtOrDefault(3), out var s) ? s : 60;
    int repeat = int.TryParse(args.ElementAtOrDefault(4), out var r) ? r : 1;
    Section($"HydroShift II hold pump 0x{value:X3} ({value}) fans raw {fan} for {seconds}s x{repeat}");
    using var dev = HydroShift2Device.Open(HydroShift2Device.Find().FirstOrDefault() ?? throw new InvalidOperationException("cooler not found"));
    dev.Log = m => Console.WriteLine("  [h2] " + m);
    dev.Initialize();

    byte[] Frame()
    {
        var c = new byte[16];
        c[0] = 0xFF; c[1] = 0x0F; c[2] = 0xA2;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(c.AsSpan(4), value);
        c[6] = fan; c[7] = fan; c[8] = fan;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(c.AsSpan(12), HydroShift2Protocol.Crc16(c.AsSpan(0, 12)));
        return HydroShift2Protocol.Command(HydroShift2Protocol.Cmd.SyncPumpFan, c);
    }

    for (int round = 0; round < repeat; round++)
    {
        Console.WriteLine($"  round {round + 1}: writing every 5 s");
        var until = DateTime.UtcNow.AddSeconds(seconds);
        int tick = 0;
        while (DateTime.UtcNow < until)
        {
            var (ack, _, _) = dev.Probe(Frame(), TimeSpan.FromSeconds(3));
            var t = dev.ReadTelemetry();
            Console.WriteLine($"  +{5 * ++tick,3}s {(ack is null ? "NONE" : Convert.ToHexString(ack.AsSpan(0, 4)))} " +
                (t is null ? "n/a" : $"pump {t.PumpRpm} fans {t.FanRpm[0]}/{t.FanRpm[1]}/{t.FanRpm[2]} coolant {t.CoolantC}C"));
            Thread.Sleep(5000);
        }
    }
}

static void CoolerDiag()
{
    Section("HydroShift II reply timing diagnostics");
    using var dev = HydroShift2Device.Open(HydroShift2Device.Find().FirstOrDefault() ?? throw new InvalidOperationException("cooler not found"));
    foreach (var r in dev.CollectLate(TimeSpan.FromSeconds(2))) Console.WriteLine($"  queued before start: {Convert.ToHexString(r.AsSpan(0, Math.Min(24, r.Length)))} ({r.Length} B)");
    var cmds = new (string Name, byte[] Packet)[]
    {
        ("StopPlay", HydroShift2Protocol.Command(HydroShift2Protocol.Cmd.StopPlay)),
        ("GetVer", HydroShift2Protocol.Command(HydroShift2Protocol.Cmd.GetVersion)),
        ("GetParams", HydroShift2Protocol.Command(HydroShift2Protocol.Cmd.GetParams)),
        ("StopClock", HydroShift2Protocol.Command(HydroShift2Protocol.Cmd.StopClock, [0])),
        ("GetParams", HydroShift2Protocol.Command(HydroShift2Protocol.Cmd.GetParams)),
        ("GetVer", HydroShift2Protocol.Command(HydroShift2Protocol.Cmd.GetVersion)),
    };
    foreach (var (name, pkt) in cmds)
    {
        var (reply, latency, stale) = dev.Probe(pkt, TimeSpan.FromSeconds(5));
        Console.WriteLine($"{name,-10} stale={stale} latency={latency.TotalMilliseconds,6:0}ms reply={(reply is null ? "NONE" : Convert.ToHexString(reply.AsSpan(0, Math.Min(32, reply.Length))) + $" ({reply.Length} B)")}");
        if (name == "GetParams" && reply is not null) Console.WriteLine($"           parsed: {HydroShift2Protocol.ParseTelemetry(reply)}");
        if (name == "GetVer" && reply is not null) Console.WriteLine($"           version: {HydroShift2Protocol.ParseVersion(reply)}");
        Thread.Sleep(300);
    }
    foreach (var r in dev.CollectLate(TimeSpan.FromSeconds(3))) Console.WriteLine($"  late: {Convert.ToHexString(r.AsSpan(0, Math.Min(24, r.Length)))} ({r.Length} B)");
}

static void MbGen2()
{
    Section("Fusion2 Gen2 strip LED auto-detect");
    using var mb = Fusion2Controller.Open(Fusion2Controller.Find() ?? throw new InvalidOperationException("Fusion2 controller not found"));
    Console.WriteLine($"{mb.Description} fw={mb.FirmwareVersion} flags=0x{mb.SupportFlags:X2} stripDetect={mb.StripDetect} gen2={mb.SupportsGen2}");
    for (int slot = 0; slot < 3; slot++) Console.WriteLine($"  ARGB_V2_{slot + 1}: {mb.DetectStripLeds(slot)?.ToString() ?? "n/a"} LEDs reported");
}

static void MbInfo()
{
    Section("Fusion2 controller info (read-only queries 0x60/0x61)");
    using var mb = Fusion2Controller.Open(Fusion2Controller.Find() ?? throw new InvalidOperationException("Fusion2 controller not found"));
    Console.WriteLine($"{mb.Description} fw={mb.FirmwareVersion} pid=0x{mb.ProductId:X4} chip=0x{mb.ChipId:X8} deviceNum={mb.DeviceNum} flags=0x{mb.SupportFlags:X2} stripDetect={mb.StripDetect} gen2={mb.SupportsGen2}");
    for (int i = 0; i < mb.ByteOrders.Count; i++) Console.WriteLine($"  byte order slot {i}: 0x{mb.ByteOrders[i]:X8}");
}

// Distinct static colors per known zone (not saved to flash) so the user can say which light is on which header.
static void MbIdentify()
{
    Section("Fusion2 identify: ARGB_V2_1 red, ARGB_V2_2 green, ARGB_V2_3 blue, chipset white, LED_C yellow");
    using var mb = Fusion2Controller.Open(Fusion2Controller.Find() ?? throw new InvalidOperationException("Fusion2 controller not found"));
    mb.Initialize();
    RgbColor[] colors = [RgbColor.ParseHex("#FF0000"), RgbColor.ParseHex("#00FF00"), RgbColor.ParseHex("#0000FF"), RgbColor.White, RgbColor.ParseHex("#FFFF00")];
    var zones = Fusion2Controller.B850AorusEliteWifi7Zones;
    for (int i = 0; i < zones.Count; i++) mb.SetEffect(zones[i], Fusion2Effect.Static, colors[i], 100, 50);
    mb.Apply();
    Console.WriteLine("sent (not saved to flash)");
}

// Static color on every effect index 0..maxIndex (not saved to flash): finds headers the zone table does not cover.
static void MbZones(RgbColor color, int maxIndex)
{
    Section($"Fusion2 static {color} on effect indexes 0..{maxIndex}");
    using var mb = Fusion2Controller.Open(Fusion2Controller.Find() ?? throw new InvalidOperationException("Fusion2 controller not found"));
    mb.Initialize();
    for (int led = 0; led <= maxIndex; led++) mb.SetEffect(new Fusion2Zone($"idx{led}", $"index {led}", led, false, 0, -1, 0), Fusion2Effect.Static, color, 100, 50);
    mb.Apply();
    Console.WriteLine("sent (not saved to flash)");
}

static void MbColor(RgbColor color)
{
    Section($"Fusion2 static color {color} on all zones");
    using var mb = Fusion2Controller.Open(Fusion2Controller.Find() ?? throw new InvalidOperationException("Fusion2 controller not found"));
    mb.Initialize();
    foreach (var z in Fusion2Controller.B850AorusEliteWifi7Zones) mb.SetEffect(z, Fusion2Effect.Static, color, 100, 50);
    mb.Apply();
    Console.WriteLine("sent: LampArray off, reset, static effect x5 zones, apply");
}

static void RingColor(RgbColor color, bool rainbow)
{
    Section(rainbow ? "HydroShift ring rainbow" : $"HydroShift ring static {color}");
    using var dev = HydroShift2Device.Open(HydroShift2Device.Find().FirstOrDefault() ?? throw new InvalidOperationException("cooler not found"));
    dev.Log = m => Console.WriteLine("  [h2] " + m);
    dev.Initialize();
    var frames = rainbow
        ? Enumerable.Range(0, 48).Select(f => Enumerable.Range(0, 24).SelectMany(i => { var c = RgbColor.FromHsv(i * 15 - f * 7.5); return new[] { c.R, c.G, c.B }; }).ToArray()).ToList()
        : [Enumerable.Range(0, 24).SelectMany(_ => new[] { color.R, color.G, color.B }).ToArray()];
    Console.WriteLine($"upload {frames.Count} frame(s): {(dev.SetRing(frames, 50) ? "ACK 0xFC" : "NO ACK")}");
}

// Builds the application icon (PNG-compressed ICO with 16/24/32/48/256 px images).
static void MakeIcon(string file)
{
    int[] sizes = [16, 24, 32, 48, 256];
    var pngs = sizes.Select(size =>
    {
        using var bmp = new SkiaSharp.SKBitmap(size, size, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul);
        using (var c = new SkiaSharp.SKCanvas(bmp))
        {
            c.Clear(SkiaSharp.SKColors.Transparent);
            float s = size;
            using var bg = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(0x10, 0x14, 0x1E), IsAntialias = true };
            c.DrawRoundRect(SkiaSharp.SKRect.Create(0, 0, s, s), s * 0.22f, s * 0.22f, bg);
            using var ring = new SkiaSharp.SKPaint
            {
                IsAntialias = true, Style = SkiaSharp.SKPaintStyle.Stroke, StrokeWidth = Math.Max(2, s * 0.13f),
                Shader = SkiaSharp.SKShader.CreateSweepGradient(new SkiaSharp.SKPoint(s / 2, s / 2),
                    [new(0xFF, 0x30, 0x60), new(0xFF, 0xC0, 0x20), new(0x20, 0xF0, 0x90), new(0x20, 0xA0, 0xFF), new(0xB0, 0x40, 0xFF), new(0xFF, 0x30, 0x60)]),
            };
            c.DrawCircle(s / 2, s / 2, s * 0.30f, ring);
            using var dot = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(0xE8, 0xEC, 0xF4), IsAntialias = true };
            c.DrawCircle(s / 2, s / 2, Math.Max(1.5f, s * 0.09f), dot);
        }
        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        return img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100).ToArray();
    }).ToList();
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
    int offset = 6 + 16 * sizes.Length;
    for (int i = 0; i < sizes.Length; i++)
    {
        w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        w.Write((byte)0); w.Write((byte)0); w.Write((ushort)1); w.Write((ushort)32);
        w.Write(pngs[i].Length); w.Write(offset);
        offset += pngs[i].Length;
    }
    foreach (var png in pngs) w.Write(png);
    File.WriteAllBytes(file, ms.ToArray());
    File.WriteAllBytes(Path.ChangeExtension(file, ".png"), pngs[^1]);
    Console.WriteLine($"wrote {file}");
}

// Renders every theme into one contact sheet. No hardware access.
static void LcdSheet(string file)
{
    var themes = LcdRenderer.Themes.Where(t => t.Id != "media").ToList();
    using var sheet = new SkiaSharp.SKBitmap(3 * 250, (themes.Count + 2) / 3 * 250);
    using (var c = new SkiaSharp.SKCanvas(sheet))
    {
        c.Clear(SkiaSharp.SKColors.DimGray);
        for (int i = 0; i < themes.Count; i++)
        {
            using var bmp = LcdRenderer.Render(new MyLianPC.Core.Profiles.LcdSettings { ThemeId = themes[i].Id }, LcdMetrics.Sample);
            using var img = SkiaSharp.SKImage.FromBitmap(bmp);
            c.DrawImage(img, SkiaSharp.SKRect.Create(i % 3 * 250 + 5, i / 3 * 250 + 5, 240, 240), new SkiaSharp.SKSamplingOptions(SkiaSharp.SKFilterMode.Linear), null);
        }
    }
    using var data = SkiaSharp.SKImage.FromBitmap(sheet).Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(file, data.ToArray());
    Console.WriteLine($"wrote {file}");
}

// Renders a theme to a PNG file. No hardware access.
static void LcdPreview(string theme, string file, bool twelveHour = false)
{
    var settings = new MyLianPC.Core.Profiles.LcdSettings { ThemeId = theme, Clock24Hour = !twelveHour };
    using var bmp = LcdRenderer.Render(settings, LcdMetrics.Sample);
    using var data = SkiaSharp.SKImage.FromBitmap(bmp).Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(file, data.ToArray());
    Console.WriteLine($"wrote {Path.GetFullPath(file)} ({LcdRenderer.EncodeJpeg(bmp).Length} byte JPEG for the panel)");
}

// Real hardware test: drives the HydroShift II LCD directly. Requires L-Connect's service to be stopped
// (it holds the WinUSB device exclusively). Leaves the last frame on the panel; restarting L-Connect restores its theme.
static void LcdDemo(int seconds, string theme)
{
    Section($"HydroShift II LCD live demo ({seconds}s, theme '{theme}')");
    using (var sc = new System.ServiceProcess.ServiceController("LConnectService"))
    {
        try
        {
            if (sc.Status == System.ServiceProcess.ServiceControllerStatus.StopPending)
                sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            if (sc.Status != System.ServiceProcess.ServiceControllerStatus.Stopped)
            {
                Console.WriteLine($"LConnectService is {sc.Status}. Stop it first (elevated): sc stop LConnectServiceWatcher & sc stop LConnectService");
                return;
            }
        }
        catch (InvalidOperationException) { /* not installed */ }
    }

    var paths = HydroShift2Device.Find();
    if (paths.Count == 0) { Console.WriteLine("no HydroShift II LCD found"); return; }
    using var dev = HydroShift2Device.Open(paths[0]);
    dev.Log = m => Console.WriteLine("  [h2] " + m);
    Console.WriteLine($"opened {dev.DevicePath}");
    dev.Initialize();
    Console.WriteLine($"firmware: {dev.FirmwareVersion ?? "(no reply to GetVer)"}");

    var computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true };
    computer.Open();
    try
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int frame = 0; sw.Elapsed.TotalSeconds < seconds; frame++)
        {
            var t = dev.ReadTelemetry();
            foreach (var hw in computer.Hardware) hw.Update();
            double? Find(HardwareType type, SensorType st, string name) => computer.Hardware.Where(h => h.HardwareType == type)
                .SelectMany(h => h.Sensors).FirstOrDefault(s => s.SensorType == st && s.Name.Contains(name))?.Value is float v && v > 0 ? v : null;

            var m = new LcdMetrics(new Dictionary<string, double?>
            {
                [MyLianPC.Core.Sensors.Metrics.CpuTemp] = Find(HardwareType.Cpu, SensorType.Temperature, "Tctl"),
                [MyLianPC.Core.Sensors.Metrics.GpuTemp] = Find(HardwareType.GpuNvidia, SensorType.Temperature, "GPU Core"),
                [MyLianPC.Core.Sensors.Metrics.CpuLoad] = Find(HardwareType.Cpu, SensorType.Load, "CPU Total"),
                [MyLianPC.Core.Sensors.Metrics.CoolantTemp] = t?.CoolantC,
                [MyLianPC.Core.Sensors.Metrics.PumpRpm] = t?.PumpRpm,
            }, DateTime.Now);
            using var bmp = LcdRenderer.Render(new MyLianPC.Core.Profiles.LcdSettings { ThemeId = theme }, m);
            var jpeg = LcdRenderer.EncodeJpeg(bmp);
            dev.ShowJpeg(jpeg);
            Console.WriteLine($"frame {frame,3}: {jpeg.Length,6} B | coolant={t?.CoolantC.ToString() ?? "n/a"}�C pump={t?.PumpRpm.ToString() ?? "n/a"} fans=[{(t is null ? "n/a" : string.Join(",", t.FanRpm))}]");
            Thread.Sleep(1000);
        }
    }
    finally { computer.Close(); }
    Console.WriteLine("done. The last frame stays on the panel. Start L-Connect again to hand the cooler back: sc start LConnectService & sc start LConnectServiceWatcher");
}

static void Section(string title) => Console.WriteLine($"\n=== {title} ===");

static void Hid()
{
    Section("HID devices (vendor-defined / RGB candidates)");
    foreach (var d in DeviceList.Local.GetHidDevices().OrderBy(d => d.VendorID).ThenBy(d => d.ProductID))
    {
        string product = Try(() => d.GetProductName()) ?? "?";
        string mfr = Try(() => d.GetManufacturer()) ?? "?";
        int feat = Try(() => d.GetMaxFeatureReportLength());
        Console.WriteLine($"{d.VendorID:X4}:{d.ProductID:X4} feat={feat,3} in={Try(() => d.GetMaxInputReportLength()),3} '{mfr}' '{product}'");
        Console.WriteLine($"    {d.DevicePath}");
    }
}

// GIGABYTE RGB Fusion 2 USB (ITE IT5711 / IT8297 family). Command 0x60 asks the controller for its
// identification block; it does not change any lighting state. Layout follows OpenRGB's IT8297Report.
static void Fusion2Info()
{
    Section("GIGABYTE RGB Fusion 2 USB (048D:5711) - read identification");
    foreach (var col in DeviceList.Local.GetHidDevices(0x048D, 0x5711))
    {
        var usages = Try(() => col.GetReportDescriptor().DeviceItems.SelectMany(i => i.Usages.GetAllValues()).Select(u => $"{u >> 16:X4}:{u & 0xFFFF:X4}").Distinct().ToList());
        Console.WriteLine($"collection {col.DevicePath.Split('#')[1]} feature={Try(() => col.GetMaxFeatureReportLength())} usages(page:id)={string.Join(",", usages ?? [])}" +
                          (usages?.Any(u => u.StartsWith("0059")) == true ? "  <- HID LampArray (Windows Dynamic Lighting)" : ""));
    }
    var dev = DeviceList.Local.GetHidDevices(0x048D)
        .Where(d => d.ProductID is 0x5702 or 0x5711 or 0x5712)
        .FirstOrDefault(d => Try(() => d.GetMaxFeatureReportLength()) >= 64);
    if (dev is null) { Console.WriteLine("not found (no collection with 64-byte feature reports)"); return; }
    Console.WriteLine($"path: {dev.DevicePath}");
    try
    {
        using var s = dev.Open();
        var buf = new byte[dev.GetMaxFeatureReportLength()];
        buf[0] = 0xCC; buf[1] = 0x60;
        s.SetFeature(buf);
        Array.Clear(buf); buf[0] = 0xCC;
        s.GetFeature(buf);
        Console.WriteLine("raw: " + Convert.ToHexString(buf));
        var fw = BitConverter.ToUInt32(buf, 4);
        var name = System.Text.Encoding.ASCII.GetString(buf, 12, 32).TrimEnd('\0', ' ');
        Console.WriteLine($"product=0x{buf[1]:X2} device_num={buf[2]} total_leds={buf[3]} fw={fw >> 24 & 0xFF}.{fw >> 16 & 0xFF}.{fw >> 8 & 0xFF}.{fw & 0xFF} " +
                          $"curr_led_count=0x{BitConverter.ToUInt16(buf, 8):X4} name='{name}' chip_id=0x{BitConverter.ToUInt32(buf, 56):X8}");
    }
    catch (Exception e) { Console.WriteLine($"FAILED: {e.GetType().Name}: {e.Message}"); }
}

// Lian Li HydroShift II LCD-C is a WinUSB device. WinUSB allows a single opener, so a failed open
// here means another process (normally LConnectService) owns it. We open and immediately close; no I/O.
static void LianLiWinUsb()
{
    Section("Lian Li HydroShift II LCD-C (1CBE:A021) - WinUSB ownership check");
    using var enumKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB\VID_1CBE&PID_A021");
    if (enumKey is null) { Console.WriteLine("device never enumerated"); return; }
    foreach (var serial in enumKey.GetSubKeyNames())
    {
        using var p = enumKey.OpenSubKey($@"{serial}\Device Parameters");
        var guid = p?.GetValue("DeviceInterfaceGUID") as string ?? (p?.GetValue("DeviceInterfaceGUIDs") as string[])?.FirstOrDefault();
        var path = $@"\\?\USB#VID_1CBE&PID_A021#{serial.ToLowerInvariant()}#{guid?.ToLowerInvariant()}";
        Console.WriteLine($"serial={serial} interface={guid}");
        using SafeFileHandle h = Native.CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            Console.WriteLine($"open: FAILED win32={err} ({new Win32Exception(err).Message})" +
                              (err is 5 or 32 ? " -> device is held by another process (expected: L-Connect service)" : ""));
        }
        else Console.WriteLine("open: OK -> no other process currently owns the device (closed again without sending anything)");
    }
}

static void Sensors()
{
    Section("Sensors via LibreHardwareMonitorLib");
    var c = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true, IsMotherboardEnabled = true, IsControllerEnabled = true };
    try
    {
        c.Open();
        foreach (var hw in c.Hardware) Dump(hw, "");
    }
    catch (Exception e) { Console.WriteLine($"FAILED: {e.Message}"); }
    finally { c.Close(); }

    static void Dump(IHardware hw, string indent)
    {
        hw.Update();
        Console.WriteLine($"{indent}[{hw.HardwareType}] {hw.Name}");
        foreach (var s in hw.Sensors.Where(s => s.SensorType is SensorType.Temperature or SensorType.Fan or SensorType.Control or SensorType.Load or SensorType.Power or SensorType.Clock))
            Console.WriteLine($"{indent}   {s.SensorType,-12} {s.Name,-28} {(s.Value?.ToString("0.#") ?? "n/a")}");
        foreach (var sub in hw.SubHardware) Dump(sub, indent + "  ");
    }
}

static T? Try<T>(Func<T> f) { try { return f(); } catch { return default; } }

static class Native
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
}