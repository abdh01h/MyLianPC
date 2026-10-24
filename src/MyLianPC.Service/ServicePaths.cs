using System.Security.AccessControl;
using System.Security.Principal;
using MyLianPC.Core.Settings;

namespace MyLianPC.Service;

/// <summary>Machine-wide data folder (%ProgramData%\MyLianPC). Writable only by SYSTEM and Administrators.</summary>
public static class ServicePaths
{
    public static string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MyLianPC");
    public static string Profiles => Path.Combine(Root, "profiles.json");
    public static string Devices => Path.Combine(Root, "devices.json");
    public static string VendorState => Path.Combine(Root, "vendor-state.json");
    public static string Network => Path.Combine(Root, "network.json");
    public static string Media => Path.Combine(Root, "media");
    public static string Logs => Path.Combine(Root, "logs");

    public static void Ensure()
    {
        LegacyData.MigrateMachineData(); // profiles/devices written before the app was renamed
        foreach (var dir in new[] { Root, Media, Logs }) Directory.CreateDirectory(dir);
        try
        {
            // Users can read (diagnostics, export) but only the service and admins can change shared state.
            var di = new DirectoryInfo(Root);
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            di.SetAccessControl(acl);
        }
        catch (UnauthorizedAccessException) { /* not elevated (development console run) */ }
        catch (InvalidOperationException) { }
    }
}
