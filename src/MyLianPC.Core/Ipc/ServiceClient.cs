using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using MyLianPC.Core.Devices;
using MyLianPC.Core.Lighting;
using MyLianPC.Core.Profiles;
using MyLianPC.Core.Sensors;
using MyLianPC.Core.Settings;

namespace MyLianPC.Core.Ipc;

/// <summary>Client side of the service pipe. Thread-safe; reconnects automatically after the service restarts.</summary>
public sealed class ServiceClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private int _nextId;

    public bool IsConnected => _pipe?.IsConnected == true;

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_pipe is { IsConnected: true }) return;
        await DropAsync();
        var pipe = new NamedPipeClientStream(".", IpcProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(1500, ct); }
        catch (TimeoutException)
        {
            await pipe.DisposeAsync();
            throw new ServiceUnavailableException("The MyLianPC service is not running. Start it from Settings or reinstall the application.");
        }
        catch (UnauthorizedAccessException)
        {
            await pipe.DisposeAsync();
            throw new ServiceUnavailableException("Access to the MyLianPC service was denied for this account.");
        }
        _pipe = pipe;
        _reader = new StreamReader(pipe, new UTF8Encoding(false), false, 64 * 1024, leaveOpen: true);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), 64 * 1024, leaveOpen: true) { AutoFlush = true };
    }

    public async Task<T> CallAsync<T>(string method, object? parameters = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    await EnsureConnectedAsync(ct);
                    var req = new IpcRequest(Interlocked.Increment(ref _nextId), method, parameters is null ? null : IpcJson.ToElement(parameters));
                    await _writer!.WriteLineAsync(JsonSerializer.Serialize(req, IpcJson.Options).AsMemory(), ct);
                    var line = await _reader!.ReadLineAsync(ct) ?? throw new IOException("The service closed the connection.");
                    var resp = JsonSerializer.Deserialize<IpcResponse>(line, IpcJson.Options) ?? throw new IOException("Empty response.");
                    if (!resp.Ok) throw new ServiceCallException(resp.Error ?? "The service reported an error.");
                    return IpcJson.From<T>(resp.Result)!;
                }
                catch (IOException) when (attempt == 0)
                {
                    await DropAsync(); // service restarted: reconnect once
                }
            }
        }
        finally { _gate.Release(); }
    }

    public Task<ServiceStatus> StatusAsync(CancellationToken ct = default) => CallAsync<ServiceStatus>(Methods.Status, null, ct);
    public Task<SensorSnapshot> SensorsAsync(CancellationToken ct = default) => CallAsync<SensorSnapshot>(Methods.Sensors, null, ct);
    public Task<ProfileDocument> ProfilesAsync() => CallAsync<ProfileDocument>(Methods.ProfilesList);
    public Task<JsonElement> SaveProfileAsync(HardwareProfile p) => CallAsync<JsonElement>(Methods.ProfileSave, p);
    public Task<ApplyReport> ActivateAsync(Guid id) => CallAsync<ApplyReport>(Methods.ProfileActivate, id);
    public Task<bool> DeleteProfileAsync(Guid id) => CallAsync<bool>(Methods.ProfileDelete, id);
    public Task<HardwareProfile> DuplicateAsync(Guid id, string name) => CallAsync<HardwareProfile>(Methods.ProfileDuplicate, new RenameRequest(id, name));
    public Task<HardwareProfile> RenameAsync(Guid id, string name) => CallAsync<HardwareProfile>(Methods.ProfileRename, new RenameRequest(id, name));
    public Task<string> ExportAsync(Guid id) => CallAsync<string>(Methods.ProfileExport, id);
    public Task<HardwareProfile> ImportAsync(string json) => CallAsync<HardwareProfile>(Methods.ProfileImport, json);
    public Task<ProfileDocument> ResetProfilesAsync() => CallAsync<ProfileDocument>(Methods.ResetProfiles);
    public Task<ApplyReport> ReapplyAsync() => CallAsync<ApplyReport>(Methods.ReapplyAll);
    public Task<ServiceStatus> RescanAsync() => CallAsync<ServiceStatus>(Methods.Rescan);
    public Task<ServiceStatus> ConfigureDevicesAsync(DeviceSettings s) => CallAsync<ServiceStatus>(Methods.DeviceConfigure, new DeviceConfigureRequest(s));
    public Task<string> UploadMediaAsync(string fileName, byte[] data) => CallAsync<string>(Methods.MediaUpload, new MediaUpload(fileName, Convert.ToBase64String(data)));
    public Task<ServiceStatus> RestoreCoolingAsync() => CallAsync<ServiceStatus>(Methods.CoolingRestoreDefaults);
    public Task<string> DiagnosticsAsync() => CallAsync<string>(Methods.Diagnostics);
    public Task<VendorStatus> VendorStatusAsync() => CallAsync<VendorStatus>(Methods.VendorStatus);
    public Task<VendorStatus> VendorTakeoverAsync(params string[] ids) => CallAsync<VendorStatus>(Methods.VendorTakeover, ids);
    public Task<VendorStatus> VendorRestoreAsync() => CallAsync<VendorStatus>(Methods.VendorRestore);

    private async Task DropAsync()
    {
        _reader?.Dispose();
        if (_writer is not null) await _writer.DisposeAsync();
        if (_pipe is not null) await _pipe.DisposeAsync();
        (_reader, _writer, _pipe) = (null, null, null);
    }

    public async ValueTask DisposeAsync() => await DropAsync();
}

public sealed class ServiceUnavailableException(string message) : Exception(message);

public sealed class ServiceCallException(string message) : Exception(message);
