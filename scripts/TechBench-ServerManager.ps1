#Requires -Version 5.1

[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9_.-]+$')]
    [string]$ServiceName = 'TechBenchWhdSync',

    [ValidateScript({ $_.IndexOf('"') -lt 0 })]
    [string]$InstallDirectory = "$env:ProgramFiles\CSRI\TechBench Sync Service",

    [ValidateScript({ $_.IndexOf('"') -lt 0 })]
    [string]$DataDirectory = "$env:ProgramData\CSRI\TechBench Sync Service",

    [ValidateScript({ $_.IndexOf('"') -lt 0 })]
    [string]$ManagerDirectory = "$env:ProgramFiles\CSRI\TechBench Server Manager"
)

$ErrorActionPreference = 'Stop'
$script:ServiceName = $ServiceName
$script:InstallDirectory = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$script:DataDirectory = [IO.Path]::GetFullPath($DataDirectory).TrimEnd('\')
$script:ManagerDirectory = [IO.Path]::GetFullPath($ManagerDirectory).TrimEnd('\')
$script:ManagerDataDirectory = [IO.Path]::GetFullPath(
    "$env:ProgramData\CSRI\TechBench Server Manager").TrimEnd('\')
$script:ReleaseApiUrl = 'https://api.github.com/repos/Serlynth/TechBenchV2-Releases/releases?per_page=100'
$script:ReleaseDownloadPrefix = 'https://github.com/Serlynth/TechBenchV2-Releases/releases/download/'
$script:AvailableUpdate = $null
$script:AccountFieldIsDirty = $false
$script:UpdatingAccountField = $false
$script:OperationInProgress = $false
$script:RecoveryBlocked = $false
$script:MaximumPackageBytes = 536870912
$script:MaximumExpandedBytes = 1073741824
$script:MaximumArchiveEntries = 5000

if (-not [Environment]::Is64BitProcess) {
    throw 'TechBench Server Manager requires 64-bit Windows PowerShell 5.1 or later.'
}

$allowedProgramFilesRoot = [IO.Path]::GetFullPath(
    (Join-Path $env:ProgramFiles 'CSRI')).TrimEnd('\') + '\'
$allowedProgramDataRoot = [IO.Path]::GetFullPath(
    (Join-Path $env:ProgramData 'CSRI')).TrimEnd('\') + '\'
if (-not $script:InstallDirectory.StartsWith(
        $allowedProgramFilesRoot, [StringComparison]::OrdinalIgnoreCase) -or
    -not $script:ManagerDirectory.StartsWith(
        $allowedProgramFilesRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "TechBench service and manager directories must remain under '$allowedProgramFilesRoot'."
}
if ($script:InstallDirectory.Equals(
    $script:ManagerDirectory, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The TechBench service and Server Manager must use separate directories.'
}
if (-not $script:DataDirectory.StartsWith(
        $allowedProgramDataRoot, [StringComparison]::OrdinalIgnoreCase) -or
    -not $script:ManagerDataDirectory.StartsWith(
        $allowedProgramDataRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "TechBench data directories must remain under '$allowedProgramDataRoot'."
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Start-ElevatedCopy {
    if ([string]::IsNullOrWhiteSpace($PSCommandPath)) {
        throw 'TechBench Server Manager must be run from its .ps1 file so it can request administrator access.'
    }

    $powershell = Join-Path $env:SystemRoot `
        'System32\WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path -LiteralPath $powershell -PathType Leaf)) {
        throw "64-bit Windows PowerShell was not found: $powershell"
    }
    $quotedScript = '"{0}"' -f $PSCommandPath
    $argumentLine = '-NoProfile -ExecutionPolicy Bypass -File {0} -ServiceName "{1}" -InstallDirectory "{2}" -DataDirectory "{3}" -ManagerDirectory "{4}"' -f
        $quotedScript,
        $script:ServiceName,
        $script:InstallDirectory,
        $script:DataDirectory,
        $script:ManagerDirectory
    try {
        Start-Process -FilePath $powershell -Verb RunAs -WindowStyle Hidden -ArgumentList @(
            $argumentLine
        ) | Out-Null
    } catch {
        throw "Administrator access is required to manage the TechBench service. $($_.Exception.Message)"
    }
}

if (-not (Test-IsAdministrator)) {
    Start-ElevatedCopy
    return
}

# The Start Menu shortcut opens in the install directory. Leave it immediately so
# Windows can rename that directory during a staged update and rollback.
Set-Location -LiteralPath $env:SystemRoot

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

[Windows.Forms.Application]::EnableVisualStyles()

function Show-ManagerMessage {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [string]$Title = 'TechBench Server Manager',
        [Windows.Forms.MessageBoxIcon]$Icon = [Windows.Forms.MessageBoxIcon]::Information
    )

    [void][Windows.Forms.MessageBox]::Show(
        $script:MainForm,
        $Text,
        $Title,
        [Windows.Forms.MessageBoxButtons]::OK,
        $Icon)
}

function Confirm-ManagerAction {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [string]$Title = 'TechBench Server Manager',
        [Windows.Forms.MessageBoxIcon]$Icon = [Windows.Forms.MessageBoxIcon]::Question
    )

    return [Windows.Forms.MessageBox]::Show(
        $script:MainForm,
        $Text,
        $Title,
        [Windows.Forms.MessageBoxButtons]::YesNo,
        $Icon,
        [Windows.Forms.MessageBoxDefaultButton]::Button2) -eq [Windows.Forms.DialogResult]::Yes
}

function Add-StatusLine {
    param([Parameter(Mandatory = $true)][string]$Text)

    $line = '{0:HH:mm:ss}  {1}' -f [DateTime]::Now, $Text
    if ($script:StatusBox.TextLength -gt 0) {
        $script:StatusBox.AppendText([Environment]::NewLine)
    }
    $script:StatusBox.AppendText($line)
    $script:StatusBox.SelectionStart = $script:StatusBox.TextLength
    $script:StatusBox.ScrollToCaret()
    [Windows.Forms.Application]::DoEvents()
}

function Set-ManagerBusy {
    param(
        [Parameter(Mandatory = $true)][bool]$Busy,
        [string]$Message
    )

    $script:OperationInProgress = $Busy
    $script:MainForm.UseWaitCursor = $Busy
    foreach ($control in @(
        $script:RefreshButton,
        $script:StartButton,
        $script:StopButton,
        $script:RestartButton,
        $script:ApplyAccountButton,
        $script:SaveWhdButton,
        $script:SaveSageButton,
        $script:CheckUpdatesButton,
        $script:InstallUpdateButton
    )) {
        $control.Enabled = -not $Busy
    }
    if ($Busy -and -not [string]::IsNullOrWhiteSpace($Message)) {
        Add-StatusLine $Message
    }
    [Windows.Forms.Application]::DoEvents()
}

function Invoke-ManagerAction {
    param(
        [Parameter(Mandatory = $true)][string]$BusyMessage,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    Set-ManagerBusy -Busy $true -Message $BusyMessage
    try {
        & $Action
    } catch [OperationCanceledException] {
        Add-StatusLine 'Operation canceled.'
    } catch {
        Add-StatusLine ("ERROR: {0}" -f $_.Exception.Message)
        Show-ManagerMessage -Text $_.Exception.Message -Icon Error
    } finally {
        Set-ManagerBusy -Busy $false
        Update-ServiceDisplay
    }
}

function Get-ServiceDetails {
    $service = Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        return [PSCustomObject]@{
            Installed = $false
            Status = 'Not installed'
            Account = ''
            Version = 'Not installed'
        }
    }

    $escapedName = $script:ServiceName.Replace("'", "''")
    $configuration = Get-CimInstance -ClassName Win32_Service `
        -Filter "Name='$escapedName'" -ErrorAction SilentlyContinue
    $executable = Join-Path $script:InstallDirectory 'TechBench.SyncService.exe'
    $version = 'Unknown'
    if (Test-Path -LiteralPath $executable) {
        $versionInfo = (Get-Item -LiteralPath $executable).VersionInfo
        $version = if (-not [string]::IsNullOrWhiteSpace($versionInfo.ProductVersion)) {
            $versionInfo.ProductVersion.Split('+', 2)[0]
        } elseif (-not [string]::IsNullOrWhiteSpace($versionInfo.FileVersion)) {
            $versionInfo.FileVersion
        } else {
            'Unknown'
        }
    }

    return [PSCustomObject]@{
        Installed = $true
        Status = [string]$service.Status
        Account = if ($null -ne $configuration) { [string]$configuration.StartName } else { '' }
        Version = $version
    }
}

function Update-ServiceDisplay {
    $details = Get-ServiceDetails
    $script:ServiceStatusValue.Text = $details.Status
    $script:ServiceVersionValue.Text = $details.Version
    $script:ServiceAccountValue.Text = if ([string]::IsNullOrWhiteSpace($details.Account)) {
        'Unknown'
    } else {
        $details.Account
    }

    if (-not $script:AccountFieldIsDirty -and -not [string]::IsNullOrWhiteSpace($details.Account)) {
        $script:UpdatingAccountField = $true
        try {
            $script:ServiceAccountBox.Text = $details.Account
        } finally {
            $script:UpdatingAccountField = $false
        }
    }

    $script:WhdConfiguredValue.Text = if (Test-Path -LiteralPath (Join-Path $script:DataDirectory 'whd.secret')) {
        'Configured'
    } else {
        'Not configured'
    }
    $script:SageConfiguredValue.Text = if (Test-Path -LiteralPath (Join-Path $script:DataDirectory 'sage.secret')) {
        'Configured'
    } else {
        'Not configured'
    }

    if (-not $script:MainForm.UseWaitCursor) {
        $script:StartButton.Enabled = $details.Installed -and $details.Status -eq 'Stopped'
        $script:StopButton.Enabled = $details.Installed -and $details.Status -ne 'Stopped'
        $script:RestartButton.Enabled = $details.Installed -and $details.Status -eq 'Running'
        $script:InstallUpdateButton.Enabled = $null -ne $script:AvailableUpdate
        if ($script:RecoveryBlocked) {
            foreach ($control in @(
                $script:StartButton,
                $script:StopButton,
                $script:RestartButton,
                $script:ApplyAccountButton,
                $script:SaveWhdButton,
                $script:SaveSageButton,
                $script:InstallUpdateButton
            )) {
                $control.Enabled = $false
            }
        }
    }
}

function Wait-ForServiceStatus {
    param(
        [Parameter(Mandatory = $true)]
        [ServiceProcess.ServiceControllerStatus]$Status
    )

    (Get-Service -Name $script:ServiceName -ErrorAction Stop).WaitForStatus(
        $Status,
        [TimeSpan]::FromSeconds(30))
}

function Wait-ForStableRunningService {
    param([int]$Seconds = 15)

    Wait-ForServiceStatus -Status Running
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 500
        [Windows.Forms.Application]::DoEvents()
        if ((Get-Service -Name $script:ServiceName -ErrorAction Stop).Status -ne
            [ServiceProcess.ServiceControllerStatus]::Running) {
            throw "The service stopped during its $Seconds-second stability check."
        }
    }
}

function Invoke-ServiceControl {
    param([Parameter(Mandatory = $true)][ValidateSet('Start', 'Stop', 'Restart')][string]$Action)

    $service = Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        throw 'The TechBench Sync Service is not installed yet.'
    }

    switch ($Action) {
        'Start' {
            if ($service.Status -ne [ServiceProcess.ServiceControllerStatus]::Running) {
                Start-Service -Name $script:ServiceName
                Wait-ForServiceStatus -Status Running
            }
        }
        'Stop' {
            if ($service.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped) {
                Stop-Service -Name $script:ServiceName
                Wait-ForServiceStatus -Status Stopped
            }
        }
        'Restart' {
            if ($service.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped) {
                Stop-Service -Name $script:ServiceName
                Wait-ForServiceStatus -Status Stopped
            }
            Start-Service -Name $script:ServiceName
            Wait-ForStableRunningService
        }
    }

    Add-StatusLine ("Service {0} completed." -f $Action.ToLowerInvariant())
}

function Find-DeploymentScript {
    param([Parameter(Mandatory = $true)][string]$Name)

    foreach ($directory in @($PSScriptRoot, $script:InstallDirectory)) {
        $candidate = Join-Path $directory $Name
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    throw "The required deployment helper '$Name' was not found beside Server Manager or in the installed service directory."
}

function New-SecureStringFromBox {
    param(
        [Parameter(Mandatory = $true)][Windows.Forms.TextBox]$TextBox,
        [Parameter(Mandatory = $true)][string]$Description
    )

    if ([string]::IsNullOrWhiteSpace($TextBox.Text)) {
        throw "$Description cannot be empty."
    }

    try {
        return ConvertTo-SecureString -String $TextBox.Text -AsPlainText -Force
    } finally {
        $TextBox.Clear()
    }
}

function Initialize-ServiceCredentialValidator {
    if ('TechBench.ServerManager.ServiceCredentialValidator' -as [type]) { return }

    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TechBench.ServerManager
{
    public static class ServiceCredentialValidator
    {
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool LogonUser(
            string userName,
            string domain,
            string password,
            int logonType,
            int logonProvider,
            out IntPtr token);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        public static void Validate(string accountName, string password)
        {
            var separator = accountName.IndexOf('\\');
            if (separator <= 0 || separator == accountName.Length - 1)
            {
                throw new ArgumentException("Use DOMAIN\\Account format.", "accountName");
            }

            var domain = accountName.Substring(0, separator);
            var userName = accountName.Substring(separator + 1);
            IntPtr token;
            const int logon32LogonService = 5;
            const int logon32ProviderDefault = 0;
            if (!LogonUser(
                userName,
                domain,
                password,
                logon32LogonService,
                logon32ProviderDefault,
                out token))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            CloseHandle(token);
        }
    }
}
'@
}

function Set-ExistingServiceCredential {
    param(
        [Parameter(Mandatory = $true)][string]$Account,
        [Parameter(Mandatory = $true)][Security.SecureString]$SecurePassword
    )

    $details = Get-ServiceDetails
    if ([string]::IsNullOrWhiteSpace($details.Account) -or
        -not $details.Account.Equals($Account, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The installed service runs as '$($details.Account)'. For safety, Server Manager can rotate the password only for that same identity. Use a controlled reinstall to change to a different domain account."
    }

    Initialize-ServiceCredentialValidator
    $bstr = [IntPtr]::Zero
    $plainText = $null
    $service = Get-Service -Name $script:ServiceName -ErrorAction Stop
    $wasRunning = $service.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped
    $configurationChanged = $false
    try {
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecurePassword)
        $plainText = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
        [TechBench.ServerManager.ServiceCredentialValidator]::Validate($Account, $plainText)

        if ($wasRunning) {
            Stop-Service -Name $script:ServiceName
            Wait-ForServiceStatus -Status Stopped
        }

        $escapedName = $script:ServiceName.Replace("'", "''")
        $serviceInstance = Get-CimInstance -ClassName Win32_Service `
            -Filter "Name='$escapedName'" -ErrorAction Stop
        $changeArguments = @{
            StartName = $Account
            StartPassword = $plainText
        }
        try {
            $result = Invoke-CimMethod -InputObject $serviceInstance `
                -MethodName Change -Arguments $changeArguments -ErrorAction Stop
        } finally {
            $changeArguments.Clear()
        }
        if ([uint32]$result.ReturnValue -ne 0) {
            throw "Windows rejected the service credential update (Win32_Service.Change return code $($result.ReturnValue))."
        }
        $configurationChanged = $true

        if ($wasRunning) {
            Start-Service -Name $script:ServiceName
            Wait-ForStableRunningService
        }
    } catch {
        if (-not $configurationChanged -and $wasRunning -and
            (Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue).Status -eq
                [ServiceProcess.ServiceControllerStatus]::Stopped) {
            Start-Service -Name $script:ServiceName -ErrorAction SilentlyContinue
        }
        throw
    } finally {
        $plainText = $null
        if ($bstr -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
        }
        $SecurePassword = $null
    }
}

function Install-OrUpdateService {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDirectory,
        [Parameter(Mandatory = $true)][string]$ServiceAccount,
        [PSCredential]$Credential
    )

    $installer = Join-Path $SourceDirectory 'Install-TechBenchSyncService.ps1'
    if (-not (Test-Path -LiteralPath $installer)) {
        throw "The verified service package is missing its installer: $installer"
    }

    $arguments = @{
        ServiceAccount = $ServiceAccount
        SourceDirectory = $SourceDirectory
        InstallDirectory = $script:InstallDirectory
        DataDirectory = $script:DataDirectory
        ManagerDirectory = $script:ManagerDirectory
        ServiceName = $script:ServiceName
        Confirm = $false
    }
    if ($null -ne $Credential) {
        $arguments.Credential = $Credential
    }

    & $installer @arguments
}

function Apply-ServiceAccount {
    $account = $script:ServiceAccountBox.Text.Trim()
    if ($account -notmatch '^[^\\]+\\[^\\]+\$?$') {
        throw 'Enter a domain service identity in DOMAIN\Account format.'
    }

    $isManagedAccount = $account.EndsWith('$', [StringComparison]::Ordinal)
    $credential = $null
    try {
        if (-not $isManagedAccount) {
            $securePassword = New-SecureStringFromBox `
                -TextBox $script:ServicePasswordBox `
                -Description 'The Windows service-account password'
            $credential = [PSCredential]::new($account, $securePassword)
        } else {
            $script:ServicePasswordBox.Clear()
        }

        $existingService = Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue
        if ($null -eq $existingService) {
            $packageExecutable = Join-Path $PSScriptRoot 'TechBench.SyncService.exe'
            $packageManifest = Join-Path $PSScriptRoot 'package-manifest.json'
            if (-not (Test-Path -LiteralPath $packageExecutable -PathType Leaf) -or
                -not (Test-Path -LiteralPath $packageManifest -PathType Leaf)) {
                throw 'Creating or repairing the Windows service requires the complete extracted TechBench service release package. Close Server Manager, verify and extract the matching service ZIP, then run TechBench-ServerManager.ps1 from that extracted package.'
            }
            Install-OrUpdateService -SourceDirectory $PSScriptRoot `
                -ServiceAccount $account -Credential $credential
        } elseif ($isManagedAccount) {
            throw 'Changing an installed service to a gMSA requires a controlled reinstall. Server Manager will not recreate a working service for a routine identity change.'
        } else {
            Set-ExistingServiceCredential -Account $account `
                -SecurePassword $credential.Password
        }
        $script:AccountFieldIsDirty = $false
        Add-StatusLine "Service credential applied for $account."
    } finally {
        $script:ServicePasswordBox.Clear()
        $credential = $null
    }
}

function Set-ExternalSecret {
    param([Parameter(Mandatory = $true)][ValidateSet('WHD', 'Sage')][string]$Kind)

    if ($Kind -eq 'WHD') {
        $textBox = $script:WhdSecretBox
        $parameterName = 'WhdCredential'
        $scriptName = 'Set-TechBenchSyncCredential.ps1'
        $description = 'The WHD API key, token, or password'
    } else {
        $textBox = $script:SageSecretBox
        $parameterName = 'SageCredential'
        $scriptName = 'Set-TechBenchSageSyncCredential.ps1'
        $description = 'The Sage ODBC password'
    }

    $secureSecret = $null
    $service = Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue
    $wasRunning = $null -ne $service -and
        $service.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped
    try {
        $secureSecret = New-SecureStringFromBox -TextBox $textBox -Description $description
        $helper = Find-DeploymentScript -Name $scriptName
        $arguments = @{
            InstallDirectory = $script:InstallDirectory
            ServiceName = $script:ServiceName
            NoRestart = $true
            Confirm = $false
        }
        $arguments[$parameterName] = $secureSecret
        & $helper @arguments
        if ($wasRunning) {
            Invoke-ServiceControl -Action Restart
            Add-StatusLine "$Kind protected credential saved; the running service was restarted."
        } else {
            Add-StatusLine "$Kind protected credential saved; the service remains stopped."
        }
    } finally {
        $textBox.Clear()
        $secureSecret = $null
    }
}

function ConvertTo-SemanticVersionParts {
    param([Parameter(Mandatory = $true)][string]$Version)

    $normalized = $Version.Trim().TrimStart('v').Split('+', 2)[0]
    if ($normalized -notmatch '^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:-(?<pre>[0-9A-Za-z.-]+))?$') {
        return $null
    }

    return [PSCustomObject]@{
        Major = [int64]$Matches.major
        Minor = [int64]$Matches.minor
        Patch = [int64]$Matches.patch
        PreRelease = [string]$Matches.pre
        Normalized = $normalized
    }
}

function Compare-SemanticVersion {
    param(
        [Parameter(Mandatory = $true)][string]$Left,
        [Parameter(Mandatory = $true)][string]$Right
    )

    $leftParts = ConvertTo-SemanticVersionParts $Left
    $rightParts = ConvertTo-SemanticVersionParts $Right
    if ($null -eq $leftParts -or $null -eq $rightParts) {
        return [string]::Compare($Left, $Right, [StringComparison]::OrdinalIgnoreCase)
    }

    foreach ($property in @('Major', 'Minor', 'Patch')) {
        if ($leftParts.$property -lt $rightParts.$property) { return -1 }
        if ($leftParts.$property -gt $rightParts.$property) { return 1 }
    }
    if ([string]::IsNullOrEmpty($leftParts.PreRelease)) {
        return $(if ([string]::IsNullOrEmpty($rightParts.PreRelease)) { 0 } else { 1 })
    }
    if ([string]::IsNullOrEmpty($rightParts.PreRelease)) { return -1 }

    $leftIdentifiers = $leftParts.PreRelease.Split('.')
    $rightIdentifiers = $rightParts.PreRelease.Split('.')
    $count = [Math]::Max($leftIdentifiers.Length, $rightIdentifiers.Length)
    for ($index = 0; $index -lt $count; $index++) {
        if ($index -ge $leftIdentifiers.Length) { return -1 }
        if ($index -ge $rightIdentifiers.Length) { return 1 }
        $leftNumber = 0L
        $rightNumber = 0L
        $leftIsNumber = [int64]::TryParse($leftIdentifiers[$index], [ref]$leftNumber)
        $rightIsNumber = [int64]::TryParse($rightIdentifiers[$index], [ref]$rightNumber)
        if ($leftIsNumber -and $rightIsNumber) {
            if ($leftNumber -lt $rightNumber) { return -1 }
            if ($leftNumber -gt $rightNumber) { return 1 }
        } elseif ($leftIsNumber) {
            return -1
        } elseif ($rightIsNumber) {
            return 1
        } else {
            $comparison = [string]::Compare(
                $leftIdentifiers[$index],
                $rightIdentifiers[$index],
                [StringComparison]::Ordinal)
            if ($comparison -ne 0) { return $comparison }
        }
    }
    return 0
}

function Assert-ApprovedReleaseAssetUrl {
    param([Parameter(Mandatory = $true)][string]$Url)

    $uri = [Uri]$Url
    if ($uri.Scheme -ne 'https' -or
        -not $uri.Host.Equals('github.com', [StringComparison]::OrdinalIgnoreCase) -or
        -not $uri.AbsoluteUri.StartsWith($script:ReleaseDownloadPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "GitHub returned an unexpected release-asset URL: $Url"
    }
}

function Invoke-BoundedDownload {
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [Parameter(Mandatory = $true)][int64]$MaximumBytes,
        [int]$TimeoutSeconds = 120
    )

    Assert-ApprovedReleaseAssetUrl $Url
    $request = [Net.HttpWebRequest]::Create($Url)
    $request.Method = 'GET'
    $request.UserAgent = 'TechBench-ServerManager'
    $request.Accept = 'application/octet-stream'
    $request.AllowAutoRedirect = $true
    $request.MaximumAutomaticRedirections = 5
    $request.Timeout = $TimeoutSeconds * 1000
    $request.ReadWriteTimeout = $TimeoutSeconds * 1000
    $response = $null
    $responseStream = $null
    $fileStream = $null
    try {
        $response = [Net.HttpWebResponse]$request.GetResponse()
        $finalUri = $response.ResponseUri
        $allowedDownloadHosts = @(
            'github.com',
            'objects.githubusercontent.com',
            'release-assets.githubusercontent.com',
            'github-releases.githubusercontent.com'
        )
        if ($finalUri.Scheme -ne 'https' -or
            $allowedDownloadHosts -notcontains $finalUri.Host.ToLowerInvariant()) {
            throw "The release download redirected to an unapproved host: $finalUri"
        }
        if ($response.ContentLength -gt $MaximumBytes) {
            throw "The release download is larger than the allowed $MaximumBytes bytes."
        }

        $responseStream = $response.GetResponseStream()
        $fileStream = [IO.File]::Open(
            $DestinationPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None)
        $buffer = New-Object byte[] 81920
        $totalBytes = 0L
        while (($bytesRead = $responseStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $totalBytes += $bytesRead
            if ($totalBytes -gt $MaximumBytes) {
                throw "The release download exceeded the allowed $MaximumBytes bytes."
            }
            $fileStream.Write($buffer, 0, $bytesRead)
        }
        $fileStream.Flush()
        if ($totalBytes -lt 1) {
            throw 'The release download was empty.'
        }
    } catch {
        if ($null -ne $fileStream) {
            $fileStream.Dispose()
            $fileStream = $null
        }
        if (Test-Path -LiteralPath $DestinationPath) {
            Remove-Item -LiteralPath $DestinationPath -Force -ErrorAction SilentlyContinue
        }
        throw
    } finally {
        if ($null -ne $fileStream) { $fileStream.Dispose() }
        if ($null -ne $responseStream) { $responseStream.Dispose() }
        if ($null -ne $response) { $response.Dispose() }
    }
}

function Get-AvailableServiceUpdate {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $headers = @{
        Accept = 'application/vnd.github+json'
        'User-Agent' = 'TechBench-ServerManager'
    }
    $releases = @(Invoke-RestMethod -Uri $script:ReleaseApiUrl -Headers $headers `
        -Method Get -TimeoutSec 30)
    $currentVersion = (Get-ServiceDetails).Version
    $currentParts = ConvertTo-SemanticVersionParts $currentVersion
    $currentIsStable = $null -ne $currentParts -and
        [string]::IsNullOrEmpty($currentParts.PreRelease)
    $candidates = @()
    foreach ($release in $releases) {
        if ($release.draft -or [string]::IsNullOrWhiteSpace([string]$release.tag_name)) {
            continue
        }
        $versionParts = ConvertTo-SemanticVersionParts ([string]$release.tag_name)
        if ($null -eq $versionParts) { continue }
        $versionIsPrerelease = -not [string]::IsNullOrEmpty($versionParts.PreRelease)
        if ([bool]$release.prerelease -ne $versionIsPrerelease) { continue }
        if ($currentIsStable -and $versionIsPrerelease) { continue }

        $zipName = "TechBenchSyncService-$($versionParts.Normalized)-win-x64.zip"
        $checksumName = "$zipName.sha256"
        $zipAsset = @($release.assets | Where-Object { $_.name -ceq $zipName }) | Select-Object -First 1
        $checksumAsset = @($release.assets | Where-Object { $_.name -ceq $checksumName }) | Select-Object -First 1
        if ($null -eq $zipAsset -or $null -eq $checksumAsset) { continue }

        Assert-ApprovedReleaseAssetUrl ([string]$zipAsset.browser_download_url)
        Assert-ApprovedReleaseAssetUrl ([string]$checksumAsset.browser_download_url)
        $candidates += [PSCustomObject]@{
            Version = $versionParts.Normalized
            Tag = [string]$release.tag_name
            Name = [string]$release.name
            PublishedUtc = [DateTime]$release.published_at
            ZipName = $zipName
            ZipUrl = [string]$zipAsset.browser_download_url
            ChecksumName = $checksumName
            ChecksumUrl = [string]$checksumAsset.browser_download_url
            ZipSize = [int64]$zipAsset.size
            ChecksumSize = [int64]$checksumAsset.size
            ReleaseUrl = [string]$release.html_url
            IsPrerelease = $versionIsPrerelease
        }
    }

    $best = $null
    foreach ($candidate in $candidates) {
        if ($null -eq $best -or
            (Compare-SemanticVersion -Left $candidate.Version -Right $best.Version) -gt 0) {
            $best = $candidate
        }
    }
    return $best
}

function Get-SafeArchiveDestination {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    $normalizedRelativePath = $RelativePath.Replace('/', '\').TrimEnd('\')
    if ([string]::IsNullOrWhiteSpace($normalizedRelativePath) -or
        [IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath.IndexOf([char]0) -ge 0 -or
        $RelativePath.Contains(':')) {
        throw "The service package contains an unsafe path: $RelativePath"
    }

    foreach ($component in $normalizedRelativePath.Split('\')) {
        if ([string]::IsNullOrWhiteSpace($component) -or
            $component -eq '.' -or $component -eq '..' -or
            $component.EndsWith(' ', [StringComparison]::Ordinal) -or
            $component.EndsWith('.', [StringComparison]::Ordinal) -or
            [IO.Path]::GetFileNameWithoutExtension($component) -match
                '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') {
            throw "The service package contains an unsafe Windows path component: $RelativePath"
        }
    }

    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $destination = [IO.Path]::GetFullPath((Join-Path $Root $normalizedRelativePath))
    if (-not $destination.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The service package contains a path outside its extraction directory: $RelativePath"
    }
    return $destination
}

function Expand-VerifiedServiceArchive {
    param(
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    New-Item -ItemType Directory -Path $DestinationPath -Force | Out-Null
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        if ($archive.Entries.Count -gt $script:MaximumArchiveEntries) {
            throw "The service package contains too many archive entries ($($archive.Entries.Count))."
        }
        $seenDestinations = New-Object 'Collections.Generic.HashSet[string]' `
            ([StringComparer]::OrdinalIgnoreCase)
        $expandedBytes = 0L
        foreach ($entry in $archive.Entries) {
            $destination = Get-SafeArchiveDestination `
                -Root $DestinationPath -RelativePath $entry.FullName
            if (-not $seenDestinations.Add($destination)) {
                throw "The service package contains duplicate Windows paths: $($entry.FullName)"
            }
            $unixFileType = ([int64]$entry.ExternalAttributes -shr 16) -band 0xF000
            if ($unixFileType -eq 0xA000 -or
                (([int64]$entry.ExternalAttributes) -band
                    [int][IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "The service package contains a symbolic link or reparse point: $($entry.FullName)"
            }
            if ($entry.Length -gt $script:MaximumPackageBytes) {
                throw "An individual service package entry is too large: $($entry.FullName)"
            }
            $expandedBytes += $entry.Length
            if ($expandedBytes -gt $script:MaximumExpandedBytes) {
                throw 'The expanded service package exceeds the allowed size.'
            }
        }
    } finally {
        $archive.Dispose()
    }
    [IO.Compression.ZipFile]::ExtractToDirectory($ArchivePath, $DestinationPath)
    $extractedReparsePoint = Get-ChildItem -LiteralPath $DestinationPath -Recurse -Force |
        Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } |
        Select-Object -First 1
    if ($null -ne $extractedReparsePoint) {
        throw "The extracted package contains a reparse point: $($extractedReparsePoint.FullName)"
    }
}

function Initialize-AdminManagerDataRoot {
    $allowedRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'CSRI')).TrimEnd('\') + '\'
    $rootPath = [IO.Path]::GetFullPath($script:ManagerDataDirectory)
    if (-not $rootPath.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The Server Manager data directory must remain under '$allowedRoot'."
    }
    if ((Test-Path -LiteralPath $rootPath) -and
        ((Get-Item -LiteralPath $rootPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to stage updates in a reparse-point directory: $rootPath"
    }

    New-Item -ItemType Directory -Path $rootPath -Force | Out-Null
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    $administrators = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $security.SetOwner($administrators)
    foreach ($sid in @($administrators, $system)) {
        $rule = [Security.AccessControl.FileSystemAccessRule]::new(
            $sid,
            [Security.AccessControl.FileSystemRights]::FullControl,
            [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit',
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow)
        [void]$security.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $rootPath -AclObject $security

    return $rootPath
}

function Initialize-AdminUpdateDirectory {
    $rootPath = Initialize-AdminManagerDataRoot

    $updatePath = Join-Path $rootPath `
        ("Update-{0}" -f [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $updatePath -Force | Out-Null
    return $updatePath
}

function Write-UpdateJournal {
    param([Parameter(Mandatory = $true)][Collections.IDictionary]$State)

    $rootPath = Initialize-AdminManagerDataRoot
    $journalPath = Join-Path $rootPath 'pending-update.json'
    $temporaryPath = Join-Path $rootPath `
        ("pending-update-{0}.tmp" -f [Guid]::NewGuid().ToString('N'))
    try {
        $State | ConvertTo-Json -Depth 4 |
            Set-Content -LiteralPath $temporaryPath -Encoding UTF8
        if (Test-Path -LiteralPath $journalPath -PathType Leaf) {
            [IO.File]::Replace($temporaryPath, $journalPath, $null)
        } else {
            Move-Item -LiteralPath $temporaryPath -Destination $journalPath
        }
    } finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        }
    }
}

function Remove-UpdateJournal {
    $journalPath = Join-Path $script:ManagerDataDirectory 'pending-update.json'
    if (Test-Path -LiteralPath $journalPath) {
        Remove-Item -LiteralPath $journalPath -Force
    }
}

function Assert-JournalFilePath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$NamePrefix
    )

    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Path]::GetFileName($fullPath).StartsWith(
            $NamePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The protected update journal contains an invalid path: $Path"
    }
    return $fullPath
}

function Repair-InterruptedUpdate {
    $journalPath = Join-Path $script:ManagerDataDirectory 'pending-update.json'
    if (-not (Test-Path -LiteralPath $journalPath -PathType Leaf)) { return $false }

    Add-StatusLine 'An interrupted service update was detected; recovering it now.'
    $journal = Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json
    if ([int]$journal.JournalFormatVersion -ne 1 -or
        ([string]$journal.ServiceName) -cne $script:ServiceName -or
        -not ([IO.Path]::GetFullPath([string]$journal.InstallPath)).Equals(
            $script:InstallDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The protected update journal does not match this TechBench service. Manual administrator review is required.'
    }

    $installParent = Split-Path -Parent $script:InstallDirectory
    $backupPath = Assert-JournalFilePath -Path ([string]$journal.BackupPath) `
        -Root $installParent -NamePrefix 'TechBench Sync Service.backup-'
    $stagePath = Assert-JournalFilePath -Path ([string]$journal.StagePath) `
        -Root $installParent -NamePrefix 'TechBench Sync Service.stage-'
    $managerTarget = [IO.Path]::GetFullPath([string]$journal.ManagerTarget)
    $expectedManagerTarget = [IO.Path]::GetFullPath(
        (Join-Path $script:ManagerDirectory 'TechBench-ServerManager.ps1'))
    if (-not $managerTarget.Equals(
        $expectedManagerTarget, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The protected update journal contains an invalid Server Manager target.'
    }
    $managerBackup = Assert-JournalFilePath -Path ([string]$journal.ManagerBackup) `
        -Root $script:ManagerDirectory -NamePrefix 'TechBench-ServerManager.backup-'
    $managerStage = Assert-JournalFilePath -Path ([string]$journal.ManagerStage) `
        -Root $script:ManagerDirectory -NamePrefix 'TechBench-ServerManager.stage-'

    $phase = [string]$journal.Phase
    if ([bool]$journal.ManagerHadExisting) {
        $managerBackupExists = Test-Path -LiteralPath $managerBackup -PathType Leaf
        $managerStageExists = Test-Path -LiteralPath $managerStage -PathType Leaf
        if ($phase -ceq 'ManagerInstalled' -and -not $managerBackupExists) {
            throw 'The interrupted update says Server Manager was replaced, but its required rollback copy is missing. The protected update journal was retained for manual administrator repair.'
        }
        if ($phase -ceq 'ManagerSwapPrepared' -and
            -not $managerBackupExists -and -not $managerStageExists) {
            throw 'The interrupted Server Manager swap cannot be classified because both its rollback copy and staged copy are missing. The protected update journal was retained for manual administrator repair.'
        }
    }

    if ($phase -ceq 'Committed') {
        if (Test-Path -LiteralPath $backupPath) {
            Remove-VerifiedDirectory -Path $backupPath -AllowedRoot $installParent
        }
        if (Test-Path -LiteralPath $stagePath) {
            Remove-VerifiedDirectory -Path $stagePath -AllowedRoot $installParent
        }
        foreach ($path in @($managerBackup, $managerStage)) {
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
        }
        Remove-UpdateJournal
        Add-StatusLine 'The committed service update cleanup was completed.'
        return $true
    }

    $service = Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue
    if ($null -ne $service -and
        $service.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped) {
        Stop-Service -Name $script:ServiceName
        Wait-ForServiceStatus -Status Stopped
    }
    if (Test-Path -LiteralPath $backupPath -PathType Container) {
        if (Test-Path -LiteralPath $script:InstallDirectory) {
            Remove-VerifiedDirectory -Path $script:InstallDirectory -AllowedRoot $installParent
        }
        Move-Item -LiteralPath $backupPath -Destination $script:InstallDirectory
    } elseif (-not (Test-Path -LiteralPath $script:InstallDirectory -PathType Container)) {
        throw 'The interrupted update has neither an installed payload nor a rollback payload. Manual repair is required.'
    }

    if (Test-Path -LiteralPath $managerBackup -PathType Leaf) {
        if (Test-Path -LiteralPath $managerTarget) {
            Remove-Item -LiteralPath $managerTarget -Force
        }
        Move-Item -LiteralPath $managerBackup -Destination $managerTarget
    } elseif (-not [bool]$journal.ManagerHadExisting -and
        (Test-Path -LiteralPath $managerTarget -PathType Leaf)) {
        Remove-Item -LiteralPath $managerTarget -Force
    }
    if (Test-Path -LiteralPath $managerStage) {
        Remove-Item -LiteralPath $managerStage -Force
    }
    if (Test-Path -LiteralPath $stagePath) {
        Remove-VerifiedDirectory -Path $stagePath -AllowedRoot $installParent
    }

    if ([bool]$journal.WasRunning) {
        Start-Service -Name $script:ServiceName
        Wait-ForStableRunningService
    }
    Remove-UpdateJournal
    Add-StatusLine 'The previous service payload and state were restored after the interrupted update.'
    return $true
}

function Assert-ServicePackageManifest {
    param(
        [Parameter(Mandatory = $true)][string]$PackageDirectory,
        [Parameter(Mandatory = $true)][string]$ExpectedVersion
    )

    $manifestPath = Join-Path $PackageDirectory 'package-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        throw 'The downloaded service package does not contain package-manifest.json.'
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.Product -cne 'TechBench Sync Service' -or
        $manifest.Runtime -cne 'win-x64' -or
        $manifest.SageOdbcWorkerRuntime -cne 'win-x86' -or
        $manifest.SelfContained -isnot [bool] -or
        -not [bool]$manifest.SelfContained -or
        [int]$manifest.PackageFormatVersion -ne 1 -or
        ([string]$manifest.Version) -cne $ExpectedVersion) {
        throw 'The downloaded package manifest does not identify the expected TechBench Sync Service release.'
    }
    if ($null -eq $manifest.RequiredDatabaseSchemaVersion -or
        [int]$manifest.RequiredDatabaseSchemaVersion -lt 1) {
        throw 'The downloaded package manifest does not state its required TechBench database schema version.'
    }

    $seenPaths = New-Object 'Collections.Generic.HashSet[string]' `
        ([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in @($manifest.Files)) {
        $relativePath = [string]$file.Path
        if ($relativePath.Equals('package-manifest.json', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The package manifest must not list itself as a payload file.'
        }
        if (-not $seenPaths.Add($relativePath)) {
            throw "The package manifest contains a duplicate path: $relativePath"
        }
        $fullPath = Get-SafeArchiveDestination -Root $PackageDirectory -RelativePath $relativePath
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "The service package is missing a manifest file: $relativePath"
        }
        $item = Get-Item -LiteralPath $fullPath
        if ($item.Length -ne [int64]$file.Length) {
            throw "The service package has an unexpected file length: $relativePath"
        }
        if ([string]$file.Sha256 -notmatch '^[0-9A-Fa-f]{64}$') {
            throw "The package manifest contains an invalid SHA-256 value: $relativePath"
        }
        $actualHash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
        if (-not $actualHash.Equals([string]$file.Sha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw "The service package failed manifest verification: $relativePath"
        }
    }

    $unexpectedFiles = @(Get-ChildItem -LiteralPath $PackageDirectory -Recurse -File |
        Where-Object {
            -not $_.FullName.Equals($manifestPath, [StringComparison]::OrdinalIgnoreCase)
        } |
        Where-Object {
            $relativePath = $_.FullName.Substring($PackageDirectory.Length).TrimStart('\')
            -not $seenPaths.Contains($relativePath)
        })
    if ($unexpectedFiles.Count -gt 0) {
        throw "The service package contains a file not covered by its manifest: $($unexpectedFiles[0].FullName)"
    }

    foreach ($requiredFile in @(
        'TechBench.SyncService.exe',
        'TechBench.SyncService.runtimeconfig.json',
        'TechBench.SyncService.deps.json',
        'appsettings.json',
        'Install-TechBenchSyncService.ps1',
        'Set-TechBenchSyncCredential.ps1',
        'Set-TechBenchSageSyncCredential.ps1',
        'TechBench-ServerManager.ps1',
        'Uninstall-TechBenchSyncService.ps1',
        'sage-odbc-worker\TechBench.SageOdbcWorker.exe',
        'sage-odbc-worker\TechBench.SageOdbcWorker.runtimeconfig.json',
        'sage-odbc-worker\TechBench.SageOdbcWorker.deps.json',
        'README-WHD-SYNC-SERVICE.md',
        'RELEASE-NOTES.md',
        'database\Deploy-CSRI-Standalone.sql',
        'database\README-Deploy.md'
    )) {
        if (-not $seenPaths.Contains($requiredFile)) {
            throw "The service package manifest is missing required file: $requiredFile"
        }
    }

    $serviceExecutable = Join-Path $PackageDirectory 'TechBench.SyncService.exe'
    $packageVersion = (Get-Item -LiteralPath $serviceExecutable).VersionInfo.ProductVersion
    if ([string]::IsNullOrWhiteSpace($packageVersion) -or
        $packageVersion.Split('+', 2)[0] -cne $ExpectedVersion) {
        throw "The service executable version does not match release $ExpectedVersion."
    }

    $workerExecutable = Join-Path $PackageDirectory `
        'sage-odbc-worker\TechBench.SageOdbcWorker.exe'
    $workerVersion = (Get-Item -LiteralPath $workerExecutable).VersionInfo.ProductVersion
    if ([string]::IsNullOrWhiteSpace($workerVersion) -or
        $workerVersion.Split('+', 2)[0] -cne $ExpectedVersion) {
        throw "The Sage ODBC worker executable version does not match release $ExpectedVersion."
    }

    $stream = [IO.File]::Open(
        $workerExecutable,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D) {
            throw 'The Sage ODBC worker executable does not contain a valid DOS/PE header.'
        }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0 -or ([int64]$peOffset + 6) -gt $stream.Length) {
            throw 'The Sage ODBC worker executable contains an invalid PE header offset.'
        }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw 'The Sage ODBC worker executable does not contain a valid PE signature.'
        }
        $machine = $reader.ReadUInt16()
        if ($machine -ne 0x014C) {
            throw ("The Sage ODBC worker executable is not x86 (PE machine 0x{0:X4})." -f $machine)
        }
    } finally {
        $reader.Dispose()
    }

    return $manifest
}

function Update-InstalledPackageManifestConfigurationEntry {
    param([Parameter(Mandatory = $true)][string]$PackageDirectory)

    $manifestPath = Join-Path $PackageDirectory 'package-manifest.json'
    $configurationPath = Join-Path $PackageDirectory 'appsettings.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $configurationEntries = @($manifest.Files | Where-Object {
        ([string]$_.Path).Equals(
            'appsettings.json', [StringComparison]::OrdinalIgnoreCase)
    })
    if ($configurationEntries.Count -ne 1) {
        throw 'The installed package manifest must contain exactly one appsettings.json entry.'
    }

    $configurationItem = Get-Item -LiteralPath $configurationPath
    $configurationEntries[0].Length = [int64]$configurationItem.Length
    $configurationEntries[0].Sha256 =
        (Get-FileHash -LiteralPath $configurationPath -Algorithm SHA256).Hash
    $manifest | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath $manifestPath -Encoding UTF8
}

function Assert-ServiceDeploymentPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $allowedRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'CSRI')).TrimEnd('\') + '\'
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a service directory outside '$allowedRoot': $fullPath"
    }
    if ((Test-Path -LiteralPath $fullPath) -and
        ((Get-Item -LiteralPath $fullPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to modify a reparse-point service directory: $fullPath"
    }
    return $fullPath
}

function Set-ManagerInstallDirectoryAcl {
    param([Parameter(Mandatory = $true)][string]$Path)

    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    $inheritance = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $propagation = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow
    foreach ($entry in @(
        [PSCustomObject]@{
            Sid = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
            Rights = [Security.AccessControl.FileSystemRights]::FullControl
        },
        [PSCustomObject]@{
            Sid = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
            Rights = [Security.AccessControl.FileSystemRights]::FullControl
        },
        [PSCustomObject]@{
            Sid = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-545')
            Rights = [Security.AccessControl.FileSystemRights]'ReadAndExecute, Synchronize'
        }
    )) {
        [void]$security.AddAccessRule(
            [Security.AccessControl.FileSystemAccessRule]::new(
                $entry.Sid, $entry.Rights, $inheritance, $propagation, $allow))
    }
    Set-Acl -LiteralPath $Path -AclObject $security
}

function Remove-VerifiedDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$AllowedRoot
    )

    if (-not (Test-Path -LiteralPath $Path)) { return }
    $root = [IO.Path]::GetFullPath($AllowedRoot).TrimEnd('\') + '\'
    $target = [IO.Path]::GetFullPath($Path)
    if (-not $target.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -or
        $target.Equals($root.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a directory outside its verified workspace: $target"
    }
    if ((Get-Item -LiteralPath $target -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Refusing to remove a reparse-point directory: $target"
    }
    $nestedReparsePoint = Get-ChildItem -LiteralPath $target -Recurse -Force |
        Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } |
        Select-Object -First 1
    if ($null -ne $nestedReparsePoint) {
        throw "Refusing to recursively remove a directory containing a reparse point: $($nestedReparsePoint.FullName)"
    }
    Remove-Item -LiteralPath $target -Recurse -Force
}

function Install-VerifiedServicePayload {
    param([Parameter(Mandatory = $true)][string]$PackageDirectory)

    $service = Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        throw 'The TechBench Sync Service is not installed. Use Install / Apply password for the first installation.'
    }

    $installPath = Assert-ServiceDeploymentPath $script:InstallDirectory
    if (-not (Test-Path -LiteralPath $installPath -PathType Container)) {
        throw "The installed service directory was not found: $installPath"
    }
    $installedConfiguration = Join-Path $installPath 'appsettings.json'
    if (-not (Test-Path -LiteralPath $installedConfiguration -PathType Leaf)) {
        throw 'The installed appsettings.json is missing. The updater will not replace SQL-owned configuration with package defaults.'
    }

    $escapedServiceName = $script:ServiceName.Replace("'", "''")
    $serviceConfiguration = Get-CimInstance -ClassName Win32_Service `
        -Filter "Name='$escapedServiceName'" -ErrorAction Stop
    $configuredPath = [Environment]::ExpandEnvironmentVariables(
        ([string]$serviceConfiguration.PathName).Trim())
    if ($configuredPath -match '^"(?<path>[^"]+)"\s*$') {
        $configuredExecutable = $Matches.path
    } elseif ($configuredPath -match '^(?<path>\S+)\s*$') {
        $configuredExecutable = $Matches.path
    } else {
        throw 'The Windows service contains an unexpected executable command line. Server Manager will not swap its files.'
    }
    $expectedExecutable = [IO.Path]::GetFullPath(
        (Join-Path $installPath 'TechBench.SyncService.exe'))
    if (-not [IO.Path]::GetFullPath($configuredExecutable).Equals(
        $expectedExecutable, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The Windows service executable is outside the managed install directory: $configuredExecutable"
    }

    $parentPath = Split-Path -Parent $installPath
    $operationId = [Guid]::NewGuid().ToString('N')
    $stagePath = Assert-ServiceDeploymentPath (Join-Path $parentPath "TechBench Sync Service.stage-$operationId")
    $backupPath = Assert-ServiceDeploymentPath (Join-Path $parentPath "TechBench Sync Service.backup-$operationId")
    $wasRunning = $service.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped
    $oldPayloadMoved = $false
    $newPayloadInstalled = $false
    $managerPath = Assert-ServiceDeploymentPath $script:ManagerDirectory
    if ((Test-Path -LiteralPath $managerPath) -and
        ((Get-Item -LiteralPath $managerPath -Force).Attributes -band
            [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to update a reparse-point Server Manager directory: $managerPath"
    }
    New-Item -ItemType Directory -Path $managerPath -Force | Out-Null
    Set-ManagerInstallDirectoryAcl -Path $managerPath
    $managerTarget = Join-Path $managerPath 'TechBench-ServerManager.ps1'
    $managerStage = Join-Path $managerPath "TechBench-ServerManager.stage-$operationId.ps1"
    $managerBackup = Join-Path $managerPath "TechBench-ServerManager.backup-$operationId.ps1"
    $managerHadExisting = Test-Path -LiteralPath $managerTarget -PathType Leaf
    $managerMoved = $false
    $managerInstalled = $false
    $updateSucceeded = $false
    $journalWritten = $false
    $journal = [ordered]@{
        JournalFormatVersion = 1
        Phase = 'Prepared'
        ServiceName = $script:ServiceName
        InstallPath = $installPath
        StagePath = $stagePath
        BackupPath = $backupPath
        ManagerTarget = $managerTarget
        ManagerStage = $managerStage
        ManagerBackup = $managerBackup
        ManagerHadExisting = $managerHadExisting
        WasRunning = $wasRunning
    }
    try {
        New-Item -ItemType Directory -Path $stagePath -Force | Out-Null
        Set-Acl -LiteralPath $stagePath -AclObject (Get-Acl -LiteralPath $installPath)
        Get-ChildItem -LiteralPath $PackageDirectory -Force | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $stagePath -Recurse -Force
        }

        # Shared connection/tuning settings stay exactly as installed. The package's
        # default appsettings.json is never allowed to overwrite SQL-owned settings.
        Copy-Item -LiteralPath $installedConfiguration `
            -Destination (Join-Path $stagePath 'appsettings.json') -Force
        Update-InstalledPackageManifestConfigurationEntry -PackageDirectory $stagePath

        Write-UpdateJournal -State $journal
        $journalWritten = $true

        if ($service.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped) {
            Add-StatusLine 'Stopping the current service gracefully...'
            Stop-Service -Name $script:ServiceName
            Wait-ForServiceStatus -Status Stopped
        }

        Move-Item -LiteralPath $installPath -Destination $backupPath
        $oldPayloadMoved = $true
        $journal.Phase = 'OldPayloadMoved'
        Write-UpdateJournal -State $journal
        Move-Item -LiteralPath $stagePath -Destination $installPath
        $newPayloadInstalled = $true
        $journal.Phase = 'NewPayloadInstalled'
        Write-UpdateJournal -State $journal

        # Always validate through SCM so the unsigned payload runs only under the
        # configured least-privilege service identity, never as Server Manager.
        Add-StatusLine 'Starting the updated service under its configured identity...'
        Start-Service -Name $script:ServiceName
        Wait-ForStableRunningService
        if (-not $wasRunning) {
            Stop-Service -Name $script:ServiceName
            Wait-ForServiceStatus -Status Stopped
            Add-StatusLine 'The service passed its running-state stability check and was returned to Stopped.'
        }

        # Update the separately installed GUI only after the service payload has
        # passed its running-state stability check. The running script is already loaded in memory.
        $packagedManager = Join-Path $PackageDirectory 'TechBench-ServerManager.ps1'
        Copy-Item -LiteralPath $packagedManager -Destination $managerStage -Force
        if ((Get-FileHash -LiteralPath $packagedManager -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $managerStage -Algorithm SHA256).Hash) {
            throw 'The staged Server Manager script failed its final copy verification.'
        }
        $journal.Phase = 'ManagerSwapPrepared'
        Write-UpdateJournal -State $journal
        if ($managerHadExisting) {
            [IO.File]::Replace($managerStage, $managerTarget, $managerBackup)
            $managerMoved = $true
        } else {
            Move-Item -LiteralPath $managerStage -Destination $managerTarget
        }
        $managerInstalled = $true
        $journal.Phase = 'ManagerInstalled'
        Write-UpdateJournal -State $journal
        $journal.Phase = 'Committed'
        Write-UpdateJournal -State $journal
        $updateSucceeded = $true
    } catch {
        $updateError = $_.Exception
        $rollbackMessage = 'The previous service files were not changed.'
        $rollbackSucceeded = $false
        try {
            if ($managerInstalled -and (Test-Path -LiteralPath $managerTarget)) {
                Remove-Item -LiteralPath $managerTarget -Force
                $managerInstalled = $false
            }
            if ($managerMoved -and (Test-Path -LiteralPath $managerBackup)) {
                Move-Item -LiteralPath $managerBackup -Destination $managerTarget
                $managerMoved = $false
            }
            if ($oldPayloadMoved) {
                $currentService = Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue
                if ($null -ne $currentService -and
                    $currentService.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped) {
                    Stop-Service -Name $script:ServiceName
                    Wait-ForServiceStatus -Status Stopped
                }
                if ($newPayloadInstalled -and (Test-Path -LiteralPath $installPath)) {
                    Remove-VerifiedDirectory -Path $installPath -AllowedRoot $parentPath
                }
                Move-Item -LiteralPath $backupPath -Destination $installPath
                $oldPayloadMoved = $false
                if ($wasRunning) {
                    Start-Service -Name $script:ServiceName
                    Wait-ForServiceStatus -Status Running
                }
                $rollbackMessage = 'The previous service files were restored successfully.'
            } elseif ($wasRunning -and
                (Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue).Status -eq
                    [ServiceProcess.ServiceControllerStatus]::Stopped) {
                Start-Service -Name $script:ServiceName
                Wait-ForServiceStatus -Status Running
            }
            $rollbackSucceeded = $true
        } catch {
            $rollbackMessage = "Automatic rollback also failed: $($_.Exception.Message)"
        }
        if ($rollbackSucceeded -and $journalWritten) {
            Remove-UpdateJournal
            $journalWritten = $false
        }
        throw "The service update failed: $($updateError.Message) $rollbackMessage"
    } finally {
        if (Test-Path -LiteralPath $stagePath) {
            Remove-VerifiedDirectory -Path $stagePath -AllowedRoot $parentPath
        }
        if (Test-Path -LiteralPath $managerStage) {
            Remove-Item -LiteralPath $managerStage -Force
        }
        if ($updateSucceeded -and (Test-Path -LiteralPath $backupPath)) {
            try {
                Remove-VerifiedDirectory -Path $backupPath -AllowedRoot $parentPath
            } catch {
                Add-StatusLine "Update succeeded, but its backup could not be removed: $($_.Exception.Message)"
            }
        }
        if ($updateSucceeded -and (Test-Path -LiteralPath $managerBackup)) {
            try {
                Remove-Item -LiteralPath $managerBackup -Force
            } catch {
                Add-StatusLine "Update succeeded, but the prior Server Manager script could not be removed: $($_.Exception.Message)"
            }
        }
        if ($updateSucceeded -and $journalWritten) {
            Remove-UpdateJournal
            $journalWritten = $false
        }
    }
}

function Assert-RequiredDatabaseSchema {
    param([Parameter(Mandatory = $true)][int]$RequiredVersion)

    $configurationPath = Join-Path $script:InstallDirectory 'appsettings.json'
    if (-not (Test-Path -LiteralPath $configurationPath -PathType Leaf)) {
        throw 'The installed appsettings.json is missing, so the TechBench database schema cannot be verified.'
    }
    try {
        $settings = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
        $server = [string]$settings.TechBenchSync.SqlServer
        $database = [string]$settings.TechBenchSync.Database
        if ([string]::IsNullOrWhiteSpace($server) -or [string]::IsNullOrWhiteSpace($database)) {
            throw 'SqlServer or Database is blank.'
        }

        Add-Type -AssemblyName System.Data
        $builder = [Data.SqlClient.SqlConnectionStringBuilder]::new()
        $builder.DataSource = $server
        $builder.InitialCatalog = $database
        $builder.IntegratedSecurity = $true
        $builder.ApplicationName = 'TechBench Server Manager'
        $builder.ConnectTimeout = 15
        $builder.Encrypt = $true
        $builder.TrustServerCertificate = [bool]$settings.TechBenchSync.TrustServerCertificate

        $connection = [Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
        try {
            $connection.Open()
            $command = $connection.CreateCommand()
            $command.CommandType = [Data.CommandType]::StoredProcedure
            $command.CommandText = 'tb_app.GetCurrentUserContext'
            $command.CommandTimeout = 30
            $reader = $command.ExecuteReader()
            try {
                if (-not $reader.Read()) {
                    throw 'tb_app.GetCurrentUserContext returned no row.'
                }
                $ordinal = $reader.GetOrdinal('SchemaVersion')
                $installedVersion = $reader.GetInt32($ordinal)
            } finally {
                $reader.Dispose()
                $command.Dispose()
            }
        } finally {
            $connection.Dispose()
        }
    } catch {
        throw "Server Manager could not verify the TechBench database schema with your Windows identity. Have a DBA apply the matching SQL installer and retry. $($_.Exception.Message)"
    }

    if ($installedVersion -ne $RequiredVersion) {
        throw "This service package requires TechBench database schema $RequiredVersion, but the server reports schema $installedVersion. Have a DBA apply the matching SQL installer before updating the service."
    }
    return $installedVersion
}

function Check-ForServiceUpdates {
    $latest = Get-AvailableServiceUpdate
    if ($null -eq $latest) {
        throw 'No published TechBench Sync Service package was found in the release repository.'
    }

    $current = (Get-ServiceDetails).Version
    if ($current -ne 'Unknown' -and $current -ne 'Not installed' -and
        (Compare-SemanticVersion -Left $latest.Version -Right $current) -le 0) {
        $script:AvailableUpdate = $null
        $script:UpdateValue.Text = "Current ($current)"
        Add-StatusLine "The service is current at version $current."
        Show-ManagerMessage "TechBench Sync Service $current is already current."
        return
    }

    $script:AvailableUpdate = $latest
    $script:UpdateValue.Text = "Version $($latest.Version) available"
    $script:InstallUpdateButton.Enabled = $true
    Add-StatusLine "Service update $($latest.Version) is available."
}

function Download-AndInstallServiceUpdate {
    if ($null -eq $script:AvailableUpdate) {
        throw 'Check for an update before downloading and installing it.'
    }
    if (Test-Path -LiteralPath (Join-Path $script:ManagerDataDirectory 'pending-update.json')) {
        throw 'An earlier update journal still exists. Close and reopen Server Manager so recovery can complete before installing another update.'
    }
    $update = $script:AvailableUpdate
    if ($update.ZipSize -lt 1 -or $update.ZipSize -gt $script:MaximumPackageBytes -or
        $update.ChecksumSize -lt 1 -or $update.ChecksumSize -gt 16384) {
        throw 'GitHub reported an invalid or excessive service update asset size.'
    }
    $updateRoot = $null
    try {
        # A routine binary update never needs or reads the Windows account password.
        $script:ServicePasswordBox.Clear()

        $updateRoot = Initialize-AdminUpdateDirectory
        $zipPath = Join-Path $updateRoot $update.ZipName
        $checksumPath = Join-Path $updateRoot $update.ChecksumName
        Add-StatusLine "Downloading service update $($update.Version)..."
        Invoke-BoundedDownload -Url $update.ZipUrl -DestinationPath $zipPath `
            -MaximumBytes $script:MaximumPackageBytes -TimeoutSeconds 120
        Invoke-BoundedDownload -Url $update.ChecksumUrl -DestinationPath $checksumPath `
            -MaximumBytes 16384 -TimeoutSeconds 30
        if ((Get-Item -LiteralPath $zipPath).Length -gt $script:MaximumPackageBytes -or
            (Get-Item -LiteralPath $checksumPath).Length -gt 16384) {
            throw 'The downloaded service update exceeds its allowed size.'
        }

        $checksumText = (Get-Content -LiteralPath $checksumPath -Raw).Trim()
        if ($checksumText -notmatch ('^(?<hash>[0-9A-Fa-f]{64})\s+\*?' +
            [Regex]::Escape($update.ZipName) + '$')) {
            throw 'The downloaded SHA-256 sidecar is malformed or names a different package.'
        }
        $expectedHash = $Matches.hash
        $actualHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
        if (-not $actualHash.Equals($expectedHash, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The downloaded service package failed SHA-256 verification and will not be installed.'
        }
        Add-StatusLine 'Release checksum verified.'

        $extractPath = Join-Path $updateRoot 'package'
        Expand-VerifiedServiceArchive -ArchivePath $zipPath -DestinationPath $extractPath
        $manifest = Assert-ServicePackageManifest -PackageDirectory $extractPath `
            -ExpectedVersion $update.Version
        Add-StatusLine 'Package contents and manifest verified.'
        $databaseVersion = Assert-RequiredDatabaseSchema `
            -RequiredVersion ([int]$manifest.RequiredDatabaseSchemaVersion)
        Add-StatusLine "TechBench database schema $databaseVersion verified."

        $warning = @"
Install unsigned TechBench Sync Service $($update.Version)?

The SHA-256 sidecar and package manifest match the public release bytes, but this alpha package is not digitally signed. A hash detects corruption; it is not a Windows publisher signature.

Required TechBench database schema: $($manifest.RequiredDatabaseSchemaVersion) (verified)
Server Manager verified the schema using your Windows identity. It does not alter SQL Server.

The existing Windows service identity, exact appsettings.json, and ProgramData WHD/Sage secrets will be preserved. A failed file verification or service running-state stability check automatically restores the previous payload.

If the service is currently stopped, it will run under its configured service identity for a 15-second running-state stability check and then return to Stopped.
"@
        if (-not (Confirm-ManagerAction -Text $warning -Title 'Unsigned alpha package' -Icon Warning)) {
            throw [OperationCanceledException]::new('The service update was canceled.')
        }

        # Reverify after the confirmation pause so a staged-file change cannot
        # cross the trust boundary unnoticed.
        $actualHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
        if (-not $actualHash.Equals($expectedHash, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The staged service package changed after verification and will not be installed.'
        }
        [void](Assert-ServicePackageManifest -PackageDirectory $extractPath `
            -ExpectedVersion $update.Version)
        Get-ChildItem -LiteralPath $extractPath -Recurse -File | Unblock-File
        Install-VerifiedServicePayload -PackageDirectory $extractPath
        $script:AvailableUpdate = $null
        $script:UpdateValue.Text = "Installed $($update.Version)"
        Add-StatusLine "TechBench Sync Service $($update.Version) installed successfully."
        Show-ManagerMessage "TechBench Sync Service $($update.Version) was installed successfully."
    } finally {
        $script:ServicePasswordBox.Clear()
        if ($null -ne $updateRoot -and (Test-Path -LiteralPath $updateRoot)) {
            try {
                Remove-VerifiedDirectory -Path $updateRoot `
                    -AllowedRoot $script:ManagerDataDirectory
            } catch {
                Add-StatusLine "Temporary update files could not be removed: $($_.Exception.Message)"
            }
        }
    }
}

function New-Label {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][int]$X,
        [Parameter(Mandatory = $true)][int]$Y,
        [int]$Width = 160,
        [int]$Height = 22,
        [bool]$Bold = $false
    )

    $label = [Windows.Forms.Label]::new()
    $label.Location = [Drawing.Point]::new($X, $Y)
    $label.Size = [Drawing.Size]::new($Width, $Height)
    $label.Text = $Text
    if ($Bold) {
        $label.Font = [Drawing.Font]::new($label.Font, [Drawing.FontStyle]::Bold)
    }
    return $label
}

function New-Button {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][int]$X,
        [Parameter(Mandatory = $true)][int]$Y,
        [int]$Width = 100
    )

    $button = [Windows.Forms.Button]::new()
    $button.Location = [Drawing.Point]::new($X, $Y)
    $button.Size = [Drawing.Size]::new($Width, 30)
    $button.Text = $Text
    return $button
}

function New-SecretTextBox {
    param(
        [Parameter(Mandatory = $true)][int]$X,
        [Parameter(Mandatory = $true)][int]$Y,
        [Parameter(Mandatory = $true)][int]$Width
    )

    $box = [Windows.Forms.TextBox]::new()
    $box.Location = [Drawing.Point]::new($X, $Y)
    $box.Size = [Drawing.Size]::new($Width, 24)
    $box.UseSystemPasswordChar = $true
    # Long API tokens are commonly pasted. The control never copies or logs the
    # value itself, but administrators should still clear sensitive clipboards.
    $box.ShortcutsEnabled = $true
    $box.AutoCompleteMode = [Windows.Forms.AutoCompleteMode]::None
    $box.AutoCompleteSource = [Windows.Forms.AutoCompleteSource]::None
    return $box
}

$script:MainForm = [Windows.Forms.Form]::new()
$script:MainForm.Text = 'TechBench Server Manager'
$script:MainForm.ClientSize = [Drawing.Size]::new(790, 715)
$script:MainForm.MinimumSize = [Drawing.Size]::new(806, 754)
$script:MainForm.StartPosition = [Windows.Forms.FormStartPosition]::CenterScreen
$script:MainForm.Icon = [Drawing.SystemIcons]::Application
$script:MainForm.AutoScaleMode = [Windows.Forms.AutoScaleMode]::Dpi

$title = New-Label -Text 'TechBench Server Manager' -X 22 -Y 17 -Width 500 -Height 31 -Bold $true
$title.Font = [Drawing.Font]::new('Segoe UI', 16, [Drawing.FontStyle]::Bold)
$subtitle = New-Label `
    -Text 'Manage the server service and its machine-protected credentials.' `
    -X 24 -Y 52 -Width 620

$serviceGroup = [Windows.Forms.GroupBox]::new()
$serviceGroup.Text = 'Service'
$serviceGroup.Location = [Drawing.Point]::new(20, 83)
$serviceGroup.Size = [Drawing.Size]::new(750, 146)
$serviceGroup.Anchor = 'Top, Left, Right'

$serviceGroup.Controls.Add((New-Label -Text 'Status' -X 18 -Y 27 -Width 90))
$script:ServiceStatusValue = New-Label -Text 'Loading...' -X 120 -Y 27 -Width 205 -Bold $true
$serviceGroup.Controls.Add($script:ServiceStatusValue)
$serviceGroup.Controls.Add((New-Label -Text 'Version' -X 18 -Y 54 -Width 90))
$script:ServiceVersionValue = New-Label -Text 'Loading...' -X 120 -Y 54 -Width 205
$serviceGroup.Controls.Add($script:ServiceVersionValue)
$serviceGroup.Controls.Add((New-Label -Text 'Runs as' -X 350 -Y 27 -Width 80))
$script:ServiceAccountValue = New-Label -Text 'Loading...' -X 435 -Y 27 -Width 290
$serviceGroup.Controls.Add($script:ServiceAccountValue)

$script:RefreshButton = New-Button -Text 'Refresh' -X 18 -Y 94 -Width 105
$script:StartButton = New-Button -Text 'Start' -X 133 -Y 94 -Width 105
$script:StopButton = New-Button -Text 'Stop' -X 248 -Y 94 -Width 105
$script:RestartButton = New-Button -Text 'Restart' -X 363 -Y 94 -Width 105
$serviceGroup.Controls.AddRange(@(
    $script:RefreshButton,
    $script:StartButton,
    $script:StopButton,
    $script:RestartButton
))

$accountGroup = [Windows.Forms.GroupBox]::new()
$accountGroup.Text = 'Windows service identity'
$accountGroup.Location = [Drawing.Point]::new(20, 239)
$accountGroup.Size = [Drawing.Size]::new(750, 131)
$accountGroup.Anchor = 'Top, Left, Right'
$accountGroup.Controls.Add((New-Label -Text 'Domain account' -X 18 -Y 28 -Width 120))
$script:ServiceAccountBox = [Windows.Forms.TextBox]::new()
$script:ServiceAccountBox.Location = [Drawing.Point]::new(144, 25)
$script:ServiceAccountBox.Size = [Drawing.Size]::new(278, 24)
$script:ServiceAccountBox.Text = 'CSRI\TechBench_Sync'
$accountGroup.Controls.Add($script:ServiceAccountBox)
$accountGroup.Controls.Add((New-Label -Text 'Password' -X 18 -Y 62 -Width 120))
$script:ServicePasswordBox = New-SecretTextBox -X 144 -Y 59 -Width 278
$accountGroup.Controls.Add($script:ServicePasswordBox)
$script:ShowServicePasswordCheckBox = [Windows.Forms.CheckBox]::new()
$script:ShowServicePasswordCheckBox.Location = [Drawing.Point]::new(438, 28)
$script:ShowServicePasswordCheckBox.Size = [Drawing.Size]::new(230, 24)
$script:ShowServicePasswordCheckBox.Text = 'Show service password'
$accountGroup.Controls.Add($script:ShowServicePasswordCheckBox)
$script:ApplyAccountButton = New-Button -Text 'Install / Apply password' -X 438 -Y 59 -Width 190
$accountGroup.Controls.Add($script:ApplyAccountButton)
$gmsaLabel = New-Label `
    -Text 'For a gMSA ending in $, leave the password blank.' `
    -X 144 -Y 92 -Width 420
$gmsaLabel.ForeColor = [Drawing.SystemColors]::GrayText
$accountGroup.Controls.Add($gmsaLabel)

$credentialGroup = [Windows.Forms.GroupBox]::new()
$credentialGroup.Text = 'Protected server credentials'
$credentialGroup.Location = [Drawing.Point]::new(20, 380)
$credentialGroup.Size = [Drawing.Size]::new(750, 149)
$credentialGroup.Anchor = 'Top, Left, Right'
$credentialGroup.Controls.Add((New-Label -Text 'WHD secret' -X 18 -Y 29 -Width 110))
$script:WhdSecretBox = New-SecretTextBox -X 132 -Y 26 -Width 284
$credentialGroup.Controls.Add($script:WhdSecretBox)
$script:ShowWhdSecretCheckBox = [Windows.Forms.CheckBox]::new()
$script:ShowWhdSecretCheckBox.Location = [Drawing.Point]::new(426, 28)
$script:ShowWhdSecretCheckBox.Size = [Drawing.Size]::new(65, 24)
$script:ShowWhdSecretCheckBox.Text = 'Show'
$credentialGroup.Controls.Add($script:ShowWhdSecretCheckBox)
$script:SaveWhdButton = New-Button -Text 'Save / Rotate' -X 497 -Y 23 -Width 125
$credentialGroup.Controls.Add($script:SaveWhdButton)
$script:WhdConfiguredValue = New-Label -Text 'Loading...' -X 630 -Y 29 -Width 105
$credentialGroup.Controls.Add($script:WhdConfiguredValue)
$credentialGroup.Controls.Add((New-Label -Text 'Sage password' -X 18 -Y 70 -Width 110))
$script:SageSecretBox = New-SecretTextBox -X 132 -Y 67 -Width 284
$credentialGroup.Controls.Add($script:SageSecretBox)
$script:ShowSageSecretCheckBox = [Windows.Forms.CheckBox]::new()
$script:ShowSageSecretCheckBox.Location = [Drawing.Point]::new(426, 69)
$script:ShowSageSecretCheckBox.Size = [Drawing.Size]::new(65, 24)
$script:ShowSageSecretCheckBox.Text = 'Show'
$credentialGroup.Controls.Add($script:ShowSageSecretCheckBox)
$script:SaveSageButton = New-Button -Text 'Save / Rotate' -X 497 -Y 64 -Width 125
$credentialGroup.Controls.Add($script:SaveSageButton)
$script:SageConfiguredValue = New-Label -Text 'Loading...' -X 630 -Y 70 -Width 105
$credentialGroup.Controls.Add($script:SageConfiguredValue)
$boundaryLabel = New-Label `
    -Text 'WHD username, Sage DSN/username, schedules, and other shared settings remain Admin-managed in SQL.' `
    -X 18 -Y 108 -Width 710
$boundaryLabel.ForeColor = [Drawing.SystemColors]::GrayText
$credentialGroup.Controls.Add($boundaryLabel)

$updateGroup = [Windows.Forms.GroupBox]::new()
$updateGroup.Text = 'Service updates'
$updateGroup.Location = [Drawing.Point]::new(20, 539)
$updateGroup.Size = [Drawing.Size]::new(750, 82)
$updateGroup.Anchor = 'Top, Left, Right'
$script:CheckUpdatesButton = New-Button -Text 'Check for updates' -X 18 -Y 29 -Width 150
$script:InstallUpdateButton = New-Button -Text 'Download && Install' -X 178 -Y 29 -Width 165
$script:InstallUpdateButton.Enabled = $false
$script:UpdateValue = New-Label -Text 'Not checked' -X 360 -Y 35 -Width 370
$updateGroup.Controls.AddRange(@(
    $script:CheckUpdatesButton,
    $script:InstallUpdateButton,
    $script:UpdateValue
))

$script:StatusBox = [Windows.Forms.TextBox]::new()
$script:StatusBox.Location = [Drawing.Point]::new(20, 636)
$script:StatusBox.Size = [Drawing.Size]::new(750, 58)
$script:StatusBox.Anchor = 'Top, Bottom, Left, Right'
$script:StatusBox.Multiline = $true
$script:StatusBox.ReadOnly = $true
$script:StatusBox.ScrollBars = [Windows.Forms.ScrollBars]::Vertical
$script:StatusBox.BackColor = [Drawing.SystemColors]::Window

$script:MainForm.Controls.AddRange(@(
    $title,
    $subtitle,
    $serviceGroup,
    $accountGroup,
    $credentialGroup,
    $updateGroup,
    $script:StatusBox
))

$script:ServiceAccountBox.Add_TextChanged({
    if (-not $script:UpdatingAccountField) {
        $script:AccountFieldIsDirty = $true
    }
})
$script:ShowServicePasswordCheckBox.Add_CheckedChanged({
    $script:ServicePasswordBox.UseSystemPasswordChar =
        -not $script:ShowServicePasswordCheckBox.Checked
})
$script:ShowWhdSecretCheckBox.Add_CheckedChanged({
    $script:WhdSecretBox.UseSystemPasswordChar =
        -not $script:ShowWhdSecretCheckBox.Checked
})
$script:ShowSageSecretCheckBox.Add_CheckedChanged({
    $script:SageSecretBox.UseSystemPasswordChar =
        -not $script:ShowSageSecretCheckBox.Checked
})
$script:RefreshButton.Add_Click({ Update-ServiceDisplay; Add-StatusLine 'Service status refreshed.' })
$script:StartButton.Add_Click({
    Invoke-ManagerAction -BusyMessage 'Starting the service...' -Action {
        Invoke-ServiceControl -Action Start
    }
})
$script:StopButton.Add_Click({
    Invoke-ManagerAction -BusyMessage 'Stopping the service...' -Action {
        Invoke-ServiceControl -Action Stop
    }
})
$script:RestartButton.Add_Click({
    Invoke-ManagerAction -BusyMessage 'Restarting the service...' -Action {
        Invoke-ServiceControl -Action Restart
    }
})
$script:ApplyAccountButton.Add_Click({
    if (Confirm-ManagerAction `
        -Text 'Apply this Windows credential? A first installation creates the service. For an installed service, only the password for its existing identity is updated in place; changing to another account is blocked.' `
        -Title 'Apply service credential' -Icon Warning) {
        Invoke-ManagerAction -BusyMessage 'Applying the Windows service identity...' -Action {
            Apply-ServiceAccount
        }
    }
})
$script:SaveWhdButton.Add_Click({
    Invoke-ManagerAction -BusyMessage 'Protecting and saving the WHD credential...' -Action {
        Set-ExternalSecret -Kind WHD
    }
})
$script:SaveSageButton.Add_Click({
    Invoke-ManagerAction -BusyMessage 'Protecting and saving the Sage credential...' -Action {
        Set-ExternalSecret -Kind Sage
    }
})
$script:CheckUpdatesButton.Add_Click({
    Invoke-ManagerAction -BusyMessage 'Checking the public TechBench release repository...' -Action {
        Check-ForServiceUpdates
    }
})
$script:InstallUpdateButton.Add_Click({
    Invoke-ManagerAction -BusyMessage 'Preparing the verified service update...' -Action {
        Download-AndInstallServiceUpdate
    }
})
$script:MainForm.Add_FormClosed({
    foreach ($box in @(
        $script:ServicePasswordBox,
        $script:WhdSecretBox,
        $script:SageSecretBox
    )) {
        $box.Clear()
    }
})
$script:MainForm.Add_FormClosing({
    param($sender, $eventArguments)
    if ($script:OperationInProgress) {
        $eventArguments.Cancel = $true
        [void][Windows.Forms.MessageBox]::Show(
            $script:MainForm,
            'Wait for the current server operation to finish before closing Server Manager.',
            'TechBench Server Manager',
            [Windows.Forms.MessageBoxButtons]::OK,
            [Windows.Forms.MessageBoxIcon]::Information)
    }
})
$script:MainForm.Add_Shown({
    Invoke-ManagerAction -BusyMessage 'Checking for interrupted server operations...' -Action {
        try {
            [void](Repair-InterruptedUpdate)
        } catch {
            $script:RecoveryBlocked = $true
            throw
        }
    }
    if ($script:RecoveryBlocked) {
        Add-StatusLine 'Server Manager changes are blocked until an administrator repairs the interrupted update.'
    } else {
        Add-StatusLine 'Server Manager is ready. Shared configuration remains managed by TechBench Admins in the client.'
    }
})

[void]$script:MainForm.ShowDialog()
$script:MainForm.Dispose()
