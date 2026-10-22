using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
using MyLianPC.Core.Settings;
using MyLianPC.Service;

// Command-line tools used by the installer (run elevated):
//   MyLianPC.Service.exe --vendor-status | --vendor-takeover | --vendor-restore
// Development: MyLianPC.Service.exe --console   (runs in the foreground, elevated prompt required)
if (args.Length > 0 && args[0].StartsWith("--vendor", StringComparison.Ordinal))
{
    ServicePaths.Ensure();
    using var factory = LoggerFactory.Create(b => b.AddProvider(new FileLoggerProvider(ServicePaths.Logs)).AddSimpleConsole());
    var vendor = new VendorManager(factory.CreateLogger<VendorManager>());
    var result = args[0] switch
    {
        "--vendor-takeover" => vendor.Takeover(args.Skip(1).ToArray()),
        "--vendor-restore" => vendor.Restore(),
        _ => vendor.Status(),
    };
    Console.WriteLine(JsonSerializer.Serialize(result, Json.Options));
    return 0;
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
var fileLogs = new FileLoggerProvider(ServicePaths.Logs);
builder.Logging.ClearProviders();
builder.Logging.AddProvider(fileLogs);
if (args.Contains("--console")) builder.Logging.AddSimpleConsole();
else
{
    builder.Services.AddWindowsService(o => o.ServiceName = "MyLianPCService");
    builder.Services.AddSingleton<IHostLifetime, SessionAwareLifetime>();
}
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton(fileLogs);
builder.Services.AddSingleton<VendorManager>();
builder.Services.AddSingleton<HardwareManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HardwareManager>());
builder.Services.AddSingleton<DiagnosticsReport>();
builder.Services.AddHostedService<PipeServer>();
builder.Build().Run();
return 0;

/// <summary>
/// Windows service lifetime that also receives session notifications. When a user logs on, unlocks or
/// switches to the console, the per-user software of that account (GIGABYTE Control Center, Windows
/// Dynamic Lighting) may repaint the lighting, so the service re-asserts the active profile shortly after.
/// </summary>
internal sealed class SessionAwareLifetime : WindowsServiceLifetime
{
    private readonly IServiceProvider _services;
    private readonly ILogger _log;
    private CancellationTokenSource? _pending, _pendingResume;

    public SessionAwareLifetime(IHostEnvironment env, IHostApplicationLifetime app, ILoggerFactory logs, IOptions<HostOptions> host,
        IOptions<WindowsServiceLifetimeOptions> options, IServiceProvider services)
        : base(env, app, logs, host, options)
    {
        _services = services;
        _log = logs.CreateLogger<SessionAwareLifetime>();
        CanHandleSessionChangeEvent = true;
        CanHandlePowerEvent = true;
        CanShutdown = true;
    }

    /// <summary>
    /// Sleep and Fast Startup (a "shut down" that hibernates the service) keep this process alive while the
    /// devices lose power, so their state has to be re-applied once they are back.
    /// </summary>
    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        if (powerStatus is PowerBroadcastStatus.ResumeAutomatic or PowerBroadcastStatus.ResumeSuspend)
        {
            _pendingResume?.Cancel();
            var cts = _pendingResume = new CancellationTokenSource();
            var hw = _services.GetRequiredService<HardwareManager>();
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(5000, cts.Token); // let USB devices re-enumerate first
                    hw.ReassertAfterResume();
                }
                catch (OperationCanceledException) { /* a newer power event replaced this one */ }
                catch (Exception e) { _log.LogError(e, "Re-applying the profile after resume failed"); }
            });
        }
        return base.OnPowerEvent(powerStatus);
    }

    protected override void OnSessionChange(SessionChangeDescription change)
    {
        base.OnSessionChange(change);
        if (change.Reason is not (SessionChangeReason.SessionLogon or SessionChangeReason.SessionUnlock or SessionChangeReason.ConsoleConnect)) return;
        // This runs on the service-control callback: anything that escapes takes the service (and every device it
        // owns) down. A sign-in must never be able to stop the lighting, so all failures stop here.
        try { ScheduleSessionApply(change); }
        catch (Exception e) { _log.LogError(e, "Could not handle the {Reason} session change", change.Reason); }
    }

    private void ScheduleSessionApply(SessionChangeDescription change)
    {
        _pending?.Cancel();
        var cts = _pending = new CancellationTokenSource();
        var hw = _services.GetRequiredService<HardwareManager>();
        int sessionId = (int)change.SessionId;
        string reason = $"{change.Reason} (session {change.SessionId})";
        _ = Task.Run(async () =>
        {
            try
            {
                // An unlock keeps the account that is already signed in, so only a logon or a console switch has
                // to look up the new account.
                string? sid = change.Reason == SessionChangeReason.SessionUnlock ? null : UserSidOfSession(sessionId);
                // Twice: once early, once after the new account's startup apps have finished.
                foreach (var delay in new[] { 4000, 20000 })
                {
                    await Task.Delay(delay, cts.Token);
                    // A different account may have signed in: switch to the profile it used last (and re-assert
                    // the lighting when that profile is already the active one).
                    hw.ActivateForUser(sid, reason);
                }
            }
            catch (OperationCanceledException) { /* a newer session change replaced this one */ }
            catch (Exception e) { _log.LogError(e, "Applying the profile after {Reason} failed", reason); }
        });
    }

    /// <summary>SID of the account signed in to a session (the service runs as LocalSystem, so it may query the token).</summary>
    private static string? UserSidOfSession(int sessionId)
    {
        try
        {
            if (WTSQueryUserToken((uint)sessionId, out var token) && token != IntPtr.Zero)
            {
                try
                {
                    using var identity = new System.Security.Principal.WindowsIdentity(token);
                    return identity.User?.Value;
                }
                finally { CloseHandle(token); }
            }
        }
        catch (Exception) { /* fall back to the user name below */ }

        // Without SeTcbPrivilege (or for a session that is still coming up) the token query fails; the user name
        // of the session is readable in that case and maps to the same SID.
        try
        {
            if (WTSQuerySessionInformation(IntPtr.Zero, sessionId, WtsUserName, out var buffer, out _) && buffer != IntPtr.Zero)
            {
                try
                {
                    if (System.Runtime.InteropServices.Marshal.PtrToStringUni(buffer) is { Length: > 0 } name)
                        return new System.Security.Principal.NTAccount(name).Translate(typeof(System.Security.Principal.SecurityIdentifier)).Value;
                }
                finally { WTSFreeMemory(buffer); }
            }
        }
        catch (Exception) { }
        return null;
    }

    private const int WtsUserName = 5; // WTS_INFO_CLASS.WTSUserName

    // The exports are spelled WTS... (upper case): a lower-case "WtsQueryUserToken" does not exist and threw
    // EntryPointNotFoundException from the session-change callback, which killed the service on every sign-in.
    [System.Runtime.InteropServices.DllImport("Wtsapi32.dll", EntryPoint = "WTSQueryUserToken", ExactSpelling = true, SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [System.Runtime.InteropServices.DllImport("Wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", ExactSpelling = true, SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytesReturned);

    [System.Runtime.InteropServices.DllImport("Wtsapi32.dll", EntryPoint = "WTSFreeMemory", ExactSpelling = true)]
    private static extern void WTSFreeMemory(IntPtr memory);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
