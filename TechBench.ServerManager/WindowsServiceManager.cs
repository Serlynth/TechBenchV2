using System.ComponentModel;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;

namespace TechBench.ServerManager;

internal sealed class WindowsServiceManager(AppPaths paths)
{
    public ServiceDetails GetDetails()
    {
        try
        {
            using var controller = new ServiceController(paths.ServiceName);
            var status = controller.Status.ToString();
            return new(true, status, QueryAccountName(), paths.CurrentVersion);
        }
        catch (InvalidOperationException)
        {
            return new(false, "Not installed", string.Empty, paths.CurrentVersion);
        }
    }

    public void Start()
    {
        using var controller = RequiredController();
        controller.Refresh();
        if (controller.Status == ServiceControllerStatus.Running) return;
        controller.Start();
        controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    }

    public void Stop()
    {
        using var controller = RequiredController();
        controller.Refresh();
        if (controller.Status == ServiceControllerStatus.Stopped) return;
        controller.Stop();
        controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
    }

    public void Restart()
    {
        Stop();
        Start();
    }

    public void ChangeIdentity(string account, string? password)
    {
        if (string.IsNullOrWhiteSpace(account)) throw new ArgumentException("Enter the domain service account.");
        var installedAccount = QueryAccountName();
        if (!installedAccount.Equals(account.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The installed service runs as '{installedAccount}'. Changing to a different identity also requires SQL and folder-permission changes; use the verified service installer for that controlled migration.");
        if (string.IsNullOrEmpty(password) && !account.Trim().EndsWith('$'))
            throw new ArgumentException("Enter the Windows service account password. Only a gMSA ending in $ uses a blank password.");
        if (account.Trim().EndsWith('$') && string.IsNullOrEmpty(password)) password = null;
        using var scm = NativeMethods.OpenSCManager(null, null, NativeMethods.ScManagerConnect);
        if (scm.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows Service Control Manager could not be opened.");
        using var service = NativeMethods.OpenService(scm, paths.ServiceName, NativeMethods.ServiceChangeConfig | NativeMethods.ServiceQueryConfig);
        if (service.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Service '{paths.ServiceName}' could not be opened.");
        if (!NativeMethods.ChangeServiceConfig(service, NativeMethods.ServiceNoChange, NativeMethods.ServiceNoChange,
                NativeMethods.ServiceNoChange, null, null, IntPtr.Zero, null, account.Trim(), password, null))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The Windows service account or password was rejected.");
        }
    }

    private ServiceController RequiredController()
    {
        var controller = new ServiceController(paths.ServiceName);
        try { _ = controller.Status; return controller; }
        catch { controller.Dispose(); throw new InvalidOperationException("The TechBench Sync Service is not installed. Run the package installer first."); }
    }

    private string QueryAccountName()
    {
        using var scm = NativeMethods.OpenSCManager(null, null, NativeMethods.ScManagerConnect);
        if (scm.IsInvalid) return "Unknown";
        using var service = NativeMethods.OpenService(scm, paths.ServiceName, NativeMethods.ServiceQueryConfig);
        if (service.IsInvalid) return "Unknown";
        _ = NativeMethods.QueryServiceConfig(service, IntPtr.Zero, 0, out var bytesNeeded);
        var buffer = Marshal.AllocHGlobal(checked((int)bytesNeeded));
        try
        {
            if (!NativeMethods.QueryServiceConfig(service, buffer, bytesNeeded, out _)) return "Unknown";
            var config = Marshal.PtrToStructure<NativeMethods.QueryServiceConfigData>(buffer);
            return Marshal.PtrToStringUni(config.ServiceStartName) ?? "Unknown";
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static class NativeMethods
    {
        internal const uint ScManagerConnect = 0x0001;
        internal const uint ServiceQueryConfig = 0x0001;
        internal const uint ServiceChangeConfig = 0x0002;
        internal const uint ServiceNoChange = 0xFFFFFFFF;

        [StructLayout(LayoutKind.Sequential)]
        internal struct QueryServiceConfigData
        {
            public uint ServiceType;
            public uint StartType;
            public uint ErrorControl;
            public IntPtr BinaryPathName;
            public IntPtr LoadOrderGroup;
            public uint TagId;
            public IntPtr Dependencies;
            public IntPtr ServiceStartName;
            public IntPtr DisplayName;
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern SafeServiceHandle OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern SafeServiceHandle OpenService(SafeServiceHandle scm, string serviceName, uint desiredAccess);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryServiceConfig(SafeServiceHandle service, IntPtr config, uint bufferSize, out uint bytesNeeded);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ChangeServiceConfig(SafeServiceHandle service, uint serviceType, uint startType,
            uint errorControl, string? binaryPathName, string? loadOrderGroup, IntPtr tagId, string? dependencies,
            string? serviceStartName, string? password, string? displayName);
    }

    private sealed class SafeServiceHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
        [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseServiceHandle(IntPtr handle);
    }
}
