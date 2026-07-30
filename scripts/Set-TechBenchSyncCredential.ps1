#Requires -Version 5.1
#Requires -RunAsAdministrator

[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [Security.SecureString]$WhdCredential,

    [ValidateNotNullOrEmpty()]
    [string]$InstallDirectory = "$env:ProgramFiles\CSRI\TechBench Sync Service",

    [ValidateNotNullOrEmpty()]
    [string]$ServiceName = 'TechBenchWhdSync',

    [switch]$NoRestart
)

$ErrorActionPreference = 'Stop'
$executablePath = Join-Path ([IO.Path]::GetFullPath($InstallDirectory)) 'TechBench.SyncService.exe'
if (-not (Test-Path -LiteralPath $executablePath)) {
    throw "The installed sync-service executable was not found: $executablePath"
}

if ($null -eq $WhdCredential) {
    $WhdCredential = Read-Host 'WHD API key, token, or password' -AsSecureString
}

if ($PSCmdlet.ShouldProcess('TechBench WHD Sync Service', 'Replace the protected server-local WHD credential')) {
    $bstr = [IntPtr]::Zero
    $plainText = $null
    try {
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($WhdCredential)
        $plainText = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
        if ([string]::IsNullOrWhiteSpace($plainText)) {
            throw 'The WHD credential cannot be empty.'
        }

        $startInfo = New-Object Diagnostics.ProcessStartInfo
        $startInfo.FileName = $executablePath
        $startInfo.Arguments = '--set-whd-secret'
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardInput = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true

        $process = New-Object Diagnostics.Process
        $process.StartInfo = $startInfo
        if (-not $process.Start()) {
            throw 'Unable to start the TechBench credential helper.'
        }

        $process.StandardInput.Write($plainText)
        $process.StandardInput.Close()
        $plainText = $null

        $standardOutput = $process.StandardOutput.ReadToEnd()
        $standardError = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) {
            throw "The TechBench credential helper failed with exit code $($process.ExitCode): $standardError"
        }

        if (-not [string]::IsNullOrWhiteSpace($standardOutput)) {
            Write-Host $standardOutput.Trim()
        }
    } finally {
        $plainText = $null
        if ($bstr -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
        }
        $WhdCredential = $null
    }

    if (-not $NoRestart) {
        $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($null -ne $service) {
            if ($service.Status -eq [ServiceProcess.ServiceControllerStatus]::Running) {
                Restart-Service -Name $ServiceName
            } else {
                Start-Service -Name $ServiceName
            }

            (Get-Service -Name $ServiceName).WaitForStatus(
                [ServiceProcess.ServiceControllerStatus]::Running,
                [TimeSpan]::FromSeconds(30))
        }
    }
}
