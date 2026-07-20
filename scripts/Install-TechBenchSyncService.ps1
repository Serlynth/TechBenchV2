#Requires -Version 5.1
#Requires -RunAsAdministrator

[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[^\\]+\\[^\\]+\$?$')]
    [string]$ServiceAccount,

    [PSCredential]$Credential,

    [ValidateNotNullOrEmpty()]
    [string]$SourceDirectory = $PSScriptRoot,

    [ValidateNotNullOrEmpty()]
    [string]$InstallDirectory = "$env:ProgramFiles\CSRI\TechBench Sync Service",

    [ValidateNotNullOrEmpty()]
    [string]$DataDirectory = "$env:ProgramData\CSRI\TechBench Sync Service",

    [ValidateNotNullOrEmpty()]
    [string]$ServiceName = 'TechBenchWhdSync',

    [ValidateNotNullOrEmpty()]
    [string]$DisplayName = 'TechBench Sync Service',

    [switch]$ReplaceConfiguration,

    [switch]$ConfigureWhdCredential,

    [switch]$ConfigureSageCredential,

    [switch]$SkipStart
)

$ErrorActionPreference = 'Stop'
$sourcePath = [IO.Path]::GetFullPath($SourceDirectory)
$installPath = [IO.Path]::GetFullPath($InstallDirectory)
$dataPath = [IO.Path]::GetFullPath($DataDirectory)
$allowedInstallRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'CSRI')).TrimEnd('\') + '\'
$allowedDataRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'CSRI')).TrimEnd('\') + '\'
$sourceExecutable = Join-Path $sourcePath 'TechBench.SyncService.exe'
$installedExecutable = Join-Path $installPath 'TechBench.SyncService.exe'
$isManagedServiceAccount = $ServiceAccount.EndsWith('$', [StringComparison]::Ordinal)

if (-not $installPath.StartsWith($allowedInstallRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "InstallDirectory must be a service-owned child directory under '$allowedInstallRoot': $installPath"
}

if (-not $dataPath.StartsWith($allowedDataRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "DataDirectory must be a service-owned child directory under '$allowedDataRoot': $dataPath"
}

foreach ($protectedPath in @($installPath, $dataPath)) {
    if ((Test-Path -LiteralPath $protectedPath) -and
        ((Get-Item -LiteralPath $protectedPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to install into a reparse-point directory: $protectedPath"
    }
}

function Invoke-ScChecked {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $output = & "$env:SystemRoot\System32\sc.exe" @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Service Control Manager command failed: sc.exe $($Arguments -join ' ')`n$($output -join [Environment]::NewLine)"
    }

    return $output
}

function Resolve-AccountSid {
    param([Parameter(Mandatory = $true)][string]$AccountName)

    try {
        return ([Security.Principal.NTAccount]::new($AccountName)).Translate(
            [Security.Principal.SecurityIdentifier])
    } catch {
        throw "Windows could not resolve service account '$AccountName'. Join this server to the domain and provision the account first. $($_.Exception.Message)"
    }
}

function Set-SecretDirectoryAcl {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][Security.Principal.SecurityIdentifier]$ServiceSid
    )

    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    $inheritance = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $propagation = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow

    $accessEntries = @(
        [PSCustomObject]@{
            Sid = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
            Rights = [Security.AccessControl.FileSystemRights]::FullControl
        },
        [PSCustomObject]@{
            Sid = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
            Rights = [Security.AccessControl.FileSystemRights]::FullControl
        },
        [PSCustomObject]@{
            Sid = $ServiceSid
            Rights = [Security.AccessControl.FileSystemRights]::Modify
        }
    )
    foreach ($entry in $accessEntries) {
        $rule = [Security.AccessControl.FileSystemAccessRule]::new(
            $entry.Sid, $entry.Rights, $inheritance, $propagation, $allow)
        [void]$security.AddAccessRule($rule)
    }

    Set-Acl -LiteralPath $Path -AclObject $security
}

function Add-ServiceReadAcl {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][Security.Principal.SecurityIdentifier]$ServiceSid
    )

    $security = Get-Acl -LiteralPath $Path
    $rule = [Security.AccessControl.FileSystemAccessRule]::new(
        $ServiceSid,
        [Security.AccessControl.FileSystemRights]'ReadAndExecute, Synchronize',
        [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit',
        [Security.AccessControl.PropagationFlags]::None,
        [Security.AccessControl.AccessControlType]::Allow)
    $security.SetAccessRule($rule)
    Set-Acl -LiteralPath $Path -AclObject $security
}

function Add-ServiceLogonRight {
    param([Parameter(Mandatory = $true)][Security.Principal.SecurityIdentifier]$Sid)

    if (-not ('TechBench.Deployment.LsaRights' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TechBench.Deployment
{
    public static class LsaRights
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct LSA_OBJECT_ATTRIBUTES
        {
            public int Length;
            public IntPtr RootDirectory;
            public IntPtr ObjectName;
            public uint Attributes;
            public IntPtr SecurityDescriptor;
            public IntPtr SecurityQualityOfService;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct LSA_UNICODE_STRING
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern uint LsaOpenPolicy(
            IntPtr systemName,
            ref LSA_OBJECT_ATTRIBUTES objectAttributes,
            uint desiredAccess,
            out IntPtr policyHandle);

        [DllImport("advapi32.dll")]
        private static extern uint LsaAddAccountRights(
            IntPtr policyHandle,
            byte[] accountSid,
            LSA_UNICODE_STRING[] userRights,
            uint countOfRights);

        [DllImport("advapi32.dll")]
        private static extern uint LsaClose(IntPtr objectHandle);

        [DllImport("advapi32.dll")]
        private static extern int LsaNtStatusToWinError(uint status);

        private const uint PolicyLookupNames = 0x00000800;
        private const uint PolicyCreateAccount = 0x00000010;

        public static void AddServiceLogonRight(byte[] sid)
        {
            var attributes = new LSA_OBJECT_ATTRIBUTES();
            attributes.Length = Marshal.SizeOf(typeof(LSA_OBJECT_ATTRIBUTES));
            IntPtr policy;
            uint status = LsaOpenPolicy(
                IntPtr.Zero,
                ref attributes,
                PolicyLookupNames | PolicyCreateAccount,
                out policy);
            ThrowIfError(status);

            IntPtr rightBuffer = IntPtr.Zero;
            try
            {
                const string right = "SeServiceLogonRight";
                rightBuffer = Marshal.StringToHGlobalUni(right);
                var rights = new[]
                {
                    new LSA_UNICODE_STRING
                    {
                        Buffer = rightBuffer,
                        Length = (ushort)(right.Length * 2),
                        MaximumLength = (ushort)((right.Length + 1) * 2)
                    }
                };
                status = LsaAddAccountRights(policy, sid, rights, 1);
                ThrowIfError(status);
            }
            finally
            {
                if (rightBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(rightBuffer);
                }
                LsaClose(policy);
            }
        }

        private static void ThrowIfError(uint status)
        {
            if (status != 0)
            {
                throw new Win32Exception(LsaNtStatusToWinError(status));
            }
        }
    }
}
'@
    }

    $sidBytes = New-Object byte[] $Sid.BinaryLength
    $Sid.GetBinaryForm($sidBytes, 0)
    [TechBench.Deployment.LsaRights]::AddServiceLogonRight($sidBytes)
    [Array]::Clear($sidBytes, 0, $sidBytes.Length)
}

function Stop-AndDeleteExistingService {
    param([Parameter(Mandatory = $true)][string]$Name)

    $existing = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $existing) {
        return
    }

    if ($existing.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped) {
        Stop-Service -Name $Name -Force
        (Get-Service -Name $Name).WaitForStatus(
            [ServiceProcess.ServiceControllerStatus]::Stopped,
            [TimeSpan]::FromSeconds(30))
    }

    [void](Invoke-ScChecked @('delete', $Name))
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ((Get-Service -Name $Name -ErrorAction SilentlyContinue) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }

    if (Get-Service -Name $Name -ErrorAction SilentlyContinue) {
        throw "Service '$Name' is still pending deletion. Close Services.msc and retry."
    }
}

if (-not (Test-Path -LiteralPath $sourceExecutable)) {
    throw "The published service executable was not found: $sourceExecutable"
}

if ($isManagedServiceAccount -and $null -ne $Credential) {
    throw 'Do not supply -Credential for a gMSA. Windows retrieves its managed password automatically.'
}

if (-not $isManagedServiceAccount) {
    if ($null -eq $Credential) {
        $Credential = Get-Credential -UserName $ServiceAccount `
            -Message 'Enter the password for the TechBench Windows service account.'
    }

    if (-not $Credential.UserName.Equals($ServiceAccount, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The credential user '$($Credential.UserName)' does not match -ServiceAccount '$ServiceAccount'."
    }
}

$serviceSid = Resolve-AccountSid $ServiceAccount
if ($isManagedServiceAccount) {
    $testAdServiceAccount = Get-Command Test-ADServiceAccount -ErrorAction SilentlyContinue
    if ($null -ne $testAdServiceAccount) {
        $shortName = ($ServiceAccount -split '\\', 2)[1].TrimEnd('$')
        if (-not (Test-ADServiceAccount -Identity $shortName)) {
            throw "Test-ADServiceAccount failed for '$ServiceAccount'. Install the gMSA on this server and verify its password-retrieval permissions."
        }
    }
}

if ($PSCmdlet.ShouldProcess($DisplayName, "Install Windows service as $ServiceAccount")) {
    Stop-AndDeleteExistingService $ServiceName

    New-Item -ItemType Directory -Path $installPath -Force | Out-Null
    $preservedConfiguration = $null
    $installedConfiguration = Join-Path $installPath 'appsettings.json'
    if (-not $ReplaceConfiguration -and (Test-Path -LiteralPath $installedConfiguration)) {
        $preservedConfiguration = Get-Content -LiteralPath $installedConfiguration -Raw
    }

    if (-not $sourcePath.Equals($installPath, [StringComparison]::OrdinalIgnoreCase)) {
        Get-ChildItem -LiteralPath $sourcePath | Where-Object {
            $_.Name -notmatch '(?i)\.sha256$' -and $_.Name -ne 'package-manifest.json'
        } | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $installPath -Recurse -Force
        }
    }

    if ($null -ne $preservedConfiguration) {
        Set-Content -LiteralPath $installedConfiguration -Value $preservedConfiguration -Encoding UTF8
    }

    $installedSettings = Get-Content -LiteralPath $installedConfiguration -Raw | ConvertFrom-Json
    if ($null -eq $installedSettings.TechBenchSync) {
        throw "The installed appsettings.json does not contain the TechBenchSync section."
    }
    $configuredSecretPath = Join-Path $dataPath 'whd.secret'
    if ($installedSettings.TechBenchSync.PSObject.Properties.Name -contains 'SecretPath') {
        $installedSettings.TechBenchSync.SecretPath = $configuredSecretPath
    } else {
        $installedSettings.TechBenchSync | Add-Member -NotePropertyName SecretPath -NotePropertyValue $configuredSecretPath
    }
    $configuredSageSecretPath = Join-Path $dataPath 'sage.secret'
    if ($installedSettings.TechBenchSync.PSObject.Properties.Name -contains 'SageSecretPath') {
        $installedSettings.TechBenchSync.SageSecretPath = $configuredSageSecretPath
    } else {
        $installedSettings.TechBenchSync | Add-Member -NotePropertyName SageSecretPath -NotePropertyValue $configuredSageSecretPath
    }
    $installedSettings | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $installedConfiguration -Encoding UTF8

    Add-ServiceReadAcl -Path $installPath -ServiceSid $serviceSid
    Set-SecretDirectoryAcl -Path $dataPath -ServiceSid $serviceSid
    Add-ServiceLogonRight -Sid $serviceSid

    if ($isManagedServiceAccount) {
        [void](Invoke-ScChecked @(
            'create', $ServiceName,
            'binPath=', "`"$installedExecutable`"",
            'start=', 'delayed-auto',
            'obj=', $ServiceAccount,
            'password=', '',
            'DisplayName=', $DisplayName
        ))
    } else {
        New-Service -Name $ServiceName `
            -BinaryPathName "`"$installedExecutable`"" `
            -DisplayName $DisplayName `
            -Description 'Synchronizes organization-wide Web Help Desk and Sage customer data into TechBench SQL Server.' `
            -StartupType Automatic `
            -Credential $Credential | Out-Null
        [void](Invoke-ScChecked @('config', $ServiceName, 'start=', 'delayed-auto'))
    }

    [void](Invoke-ScChecked @('description', $ServiceName,
        'Synchronizes organization-wide Web Help Desk and Sage customer data into TechBench SQL Server.'))
    [void](Invoke-ScChecked @('failure', $ServiceName,
        'reset=', '86400', 'actions=', 'restart/60000/restart/60000/restart/300000'))
    [void](Invoke-ScChecked @('failureflag', $ServiceName, '1'))

    if ($ConfigureWhdCredential) {
        & (Join-Path $installPath 'Set-TechBenchSyncCredential.ps1') `
            -InstallDirectory $installPath -ServiceName $ServiceName -NoRestart
    }

    if ($ConfigureSageCredential) {
        & (Join-Path $installPath 'Set-TechBenchSageSyncCredential.ps1') `
            -InstallDirectory $installPath -ServiceName $ServiceName -NoRestart
    }

    $credentialPath = Join-Path $dataPath 'whd.secret'
    $shouldStart = -not $SkipStart
    if ($shouldStart -and -not (Test-Path -LiteralPath $credentialPath)) {
        $shouldStart = $false
        Write-Warning "The service was installed but not started because no protected WHD credential exists. Run Set-TechBenchSyncCredential.ps1 to provision it and start the service."
    }

    if ($shouldStart) {
        Start-Service -Name $ServiceName
        (Get-Service -Name $ServiceName).WaitForStatus(
            [ServiceProcess.ServiceControllerStatus]::Running,
            [TimeSpan]::FromSeconds(30))
    }

    $Credential = $null
    Write-Host "Installed '$DisplayName' as $ServiceAccount."
    Write-Host "Protected sync-service data directory: $dataPath"
}
