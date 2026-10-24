using System.Text.Json;
using System.Text.Json.Serialization;
using MyLianPC.Core.Devices;
using MyLianPC.Core.Settings;

namespace MyLianPC.Core.Ipc;

/// <summary>
/// Desktop app ⇄ service protocol: one JSON object per line over the named pipe <see cref="PipeName"/>.
/// The pipe ACL admits only local interactive users, administrators and SYSTEM; network clients are denied.
/// </summary>
public static class IpcProtocol
{
    public const string PipeName = "MyLianPC.Service.v1";
    public const int MaxMessageBytes = 32 * 1024 * 1024;
}

public sealed record IpcRequest(int Id, string Method, JsonElement? Params = null);

public sealed record IpcResponse(int Id, bool Ok, JsonElement? Result = null, string? Error = null);

/// <summary>Method names. Methods marked admin require an elevated administrator client.</summary>
public static class Methods
{
    public const string Status = "status.get";
    public const string Sensors = "sensors.get";
    public const string Network = "network.get";
    public const string ProfilesList = "profiles.list";
    public const string ProfileSave = "profiles.save";
    public const string ProfileDelete = "profiles.delete";
    public const string ProfileDuplicate = "profiles.duplicate";
    public const string ProfileRename = "profiles.rename";
    public const string ProfileActivate = "profiles.activate";
    public const string ProfileImport = "profiles.import";
    public const string ProfileExport = "profiles.export";

    public const string ReapplyAll = "hardware.reapply";
    public const string Rescan = "devices.rescan";
    public const string DeviceConfigure = "devices.configure";
    public const string MediaUpload = "media.upload";
    public const string CoolingRestoreDefaults = "cooling.restoreDefaults";
    public const string ResetProfiles = "profiles.resetDefaults";
    public const string Diagnostics = "diagnostics.report";
    public const string VendorStatus = "vendor.status";
    public const string VendorTakeover = "vendor.takeover"; // admin
    public const string VendorRestore = "vendor.restore";   // admin
}

public sealed record ServiceStatus
{
    public string Version { get; init; } = "";
    public DateTimeOffset StartedUtc { get; init; }
    public bool PawnIoInstalled { get; init; }
    public IReadOnlyList<DeviceStatus> Devices { get; init; } = [];
    public Guid? ActiveProfileId { get; init; }
    public string? ActiveProfileName { get; init; }
    public ApplyReport? LastApply { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public DeviceSettings DeviceSettings { get; init; } = new();
}

public sealed record VendorComponent(string Id, string Name, string Description, bool Present, bool Active, string State);

public sealed record VendorStatus(IReadOnlyList<VendorComponent> Components, IReadOnlyList<string> ConflictingProcesses);

public sealed record MediaUpload(string FileName, string Base64);

public sealed record DeviceConfigureRequest(DeviceSettings Settings);

public sealed record RenameRequest(Guid Id, string Name);

public static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new(Json.Options) { WriteIndented = false };

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    public static T? From<T>(JsonElement? element) => element is { } e ? e.Deserialize<T>(Options) : default;
}
