using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using MyLianPC.Core.Ipc;
using MyLianPC.Core.Lighting;
using MyLianPC.Core.Profiles;
using Microsoft.Win32.SafeHandles;

namespace MyLianPC.Service;

/// <summary>
/// Local IPC endpoint. Security model:
/// * Pipe ACL: SYSTEM and Administrators full control; locally logged-on interactive users read/write;
///   network logons are explicitly denied (and remote clients rejected), so only people sitting at this PC
///   (both Windows accounts) can talk to the service.
/// * Vendor takeover/restore additionally require the caller's token to be an elevated administrator.
/// * Every request is size-limited and validated before it can reach hardware.
/// </summary>
public sealed class PipeServer(HardwareManager hw, VendorManager vendor, DiagnosticsReport diagnostics, ILogger<PipeServer> log) : BackgroundService
{
    private const int MaxClients = 8;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var clients = new List<Task>();
        while (!stop.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(IpcProtocol.PipeName, PipeDirection.InOut, MaxClients, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 64 * 1024, 64 * 1024, Security());
            }
            catch (IOException e)
            {
                log.LogWarning("Pipe busy ({Message}); retrying", e.Message);
                await Task.Delay(1000, stop);
                continue;
            }
            try { await pipe.WaitForConnectionAsync(stop); }
            catch (OperationCanceledException) { await pipe.DisposeAsync(); break; }
            clients.RemoveAll(t => t.IsCompleted);
            clients.Add(Task.Run(() => Serve(pipe, stop), stop));
        }
        await Task.WhenAll(clients.Where(t => !t.IsCompleted)).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private static PipeSecurity Security()
    {
        var s = new PipeSecurity();
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return s;
    }

    private async Task Serve(NamedPipeServerStream pipe, CancellationToken stop)
    {
        await using var _ = pipe;
        string user = "?";
        string? sid = null;
        bool elevatedAdmin = false, identified = false;
        void Identify()
        {
            if (identified) return;
            identified = true;
            try
            {
                using var id = ClientIdentity(pipe);
                user = id.Name;
                sid = id.User?.Value;
                elevatedAdmin = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception e) { log.LogWarning("Could not identify pipe client: {Message}", e.Message); }
        }

        var input = new BufferedStream(pipe, 64 * 1024);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 64 * 1024, leaveOpen: true) { AutoFlush = true };
        while (!stop.IsCancellationRequested && pipe.IsConnected)
        {
            string? line;
            try { line = await ReadLineLimited(input, stop); }
            catch (Exception) { break; }
            if (line is null) break;
            Identify();
            IpcResponse response;
            IpcRequest? req = null;
            try
            {
                req = JsonSerializer.Deserialize<IpcRequest>(line, IpcJson.Options) ?? throw new JsonException("empty request");
                response = new IpcResponse(req.Id, true, Handle(req, elevatedAdmin, user, sid));
            }
            catch (Exception e) when (e is ProfileValidationException or ArgumentException or KeyNotFoundException or JsonException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException or InvalidDataException)
            {
                response = new IpcResponse(req?.Id ?? 0, false, Error: e.Message);
            }
            catch (Exception e)
            {
                log.LogError(e, "Request {Method} failed", req?.Method);
                response = new IpcResponse(req?.Id ?? 0, false, Error: "Internal error: " + e.Message);
            }
            try { await writer.WriteLineAsync(JsonSerializer.Serialize(response, IpcJson.Options)); }
            catch (IOException) { break; }
        }
    }

    /// <summary>
    /// The caller's identity, taken from its process token. Impersonating the caller (RunAsClient) is not used:
    /// once it succeeded, profile saves on that connection failed with access denied on %ProgramData%\MyLianPC.
    /// </summary>
    private static WindowsIdentity ClientIdentity(NamedPipeServerStream pipe)
    {
        if (!Native.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid)) throw new Win32Exception();
        using var process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process.IsInvalid) throw new Win32Exception();
        if (!Native.OpenProcessToken(process, Native.TOKEN_QUERY | Native.TOKEN_DUPLICATE, out var token)) throw new Win32Exception();
        using (token) return new WindowsIdentity(token.DangerousGetHandle());
    }

    private static class Native
    {
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, TOKEN_DUPLICATE = 0x0002, TOKEN_QUERY = 0x0008;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, uint processId);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    }

    /// <summary>Reads one newline-terminated UTF-8 line, refusing anything over the message limit.</summary>
    /// <remarks>The stream is a <see cref="BufferedStream"/>, so single-byte reads are served from memory.</remarks>
    internal static async Task<string?> ReadLineLimited(Stream stream, CancellationToken stop)
    {
        var buffer = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            int n = await stream.ReadAsync(one, stop);
            if (n == 0) return buffer.Length > 0 ? Encoding.UTF8.GetString(buffer.ToArray()) : null;
            if (one[0] == (byte)'\n') return Encoding.UTF8.GetString(buffer.ToArray()).TrimEnd('\r');
            buffer.WriteByte(one[0]);
            if (buffer.Length > IpcProtocol.MaxMessageBytes) throw new InvalidDataException("Message too large.");
        }
    }

    private JsonElement? Handle(IpcRequest req, bool elevatedAdmin, string user, string? sid)
    {
        T P<T>() => IpcJson.From<T>(req.Params) ?? throw new ArgumentException($"Missing parameters for {req.Method}.");
        JsonElement R<T>(T value) => IpcJson.ToElement(value);

        switch (req.Method)
        {
            case Methods.Status: return R(hw.Status());
            case Methods.Sensors: return R(hw.Snapshot);
            case Methods.Network: return R(hw.Network.Current);
            case Methods.ProfilesList: return R(hw.Profiles.Snapshot);
            case Methods.ProfileSave:
            {
                var saved = hw.Profiles.Upsert(P<HardwareProfile>());
                log.LogInformation("{User} saved profile '{Profile}'", user, saved.Name);
                if (hw.Profiles.Active?.Id == saved.Id) return R(hw.ApplyActive("active profile edited"));
                return R(saved);
            }
            case Methods.ProfileDelete: hw.Profiles.Delete(P<Guid>()); return R(true);
            case Methods.ProfileDuplicate: { var r = P<RenameRequest>(); return R(hw.Profiles.Duplicate(r.Id, r.Name)); }
            case Methods.ProfileRename: { var r = P<RenameRequest>(); return R(hw.Profiles.Rename(r.Id, r.Name)); }
            case Methods.ProfileActivate:
                log.LogInformation("{User} activated profile {Id}", user, req.Params);
                return R(hw.ActivateProfile(P<Guid>(), sid));
            case Methods.ProfileImport: return R(hw.Profiles.Import(P<string>()));
            case Methods.ProfileExport: return R(hw.Profiles.Export(P<Guid>()));
            case Methods.ResetProfiles:
                foreach (var p in hw.Profiles.Snapshot.Profiles) hw.Profiles.Delete(p.Id);
                foreach (var p in DefaultProfiles.All()) hw.Profiles.Upsert(p);
                return R(hw.Profiles.Snapshot);
            case Methods.ReapplyAll: return R(hw.ApplyActive($"requested by {user}"));
            case Methods.Rescan: hw.Rescan(); return R(hw.Status());
            case Methods.DeviceConfigure: hw.UpdateDeviceSettings(P<DeviceConfigureRequest>().Settings); return R(hw.Status());
            case Methods.MediaUpload:
            {
                var m = P<MediaUpload>();
                return R(hw.StoreMedia(m.FileName, Convert.FromBase64String(m.Base64)));
            }
            case Methods.CoolingRestoreDefaults: hw.RestoreCoolingDefaults(); return R(hw.Status());
            case Methods.Diagnostics: return R(diagnostics.Build());
            case Methods.VendorStatus: return R(vendor.Status());
            case Methods.VendorTakeover:
                if (!elevatedAdmin) throw new UnauthorizedAccessException("Taking over from vendor software needs administrator rights. Restart MyLianPC as administrator.");
                log.LogInformation("{User} requested vendor takeover", user);
                var status = vendor.Takeover(IpcJson.From<string[]>(req.Params) ?? []);
                hw.Rescan();
                return R(status);
            case Methods.VendorRestore:
                if (!elevatedAdmin) throw new UnauthorizedAccessException("Restoring vendor software needs administrator rights. Restart MyLianPC as administrator.");
                log.LogInformation("{User} requested vendor restore", user);
                return R(vendor.Restore());
            default: throw new NotSupportedException($"Unknown method '{req.Method}'.");
        }
    }
}
