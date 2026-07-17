#Requires -Version 5.1
#Requires -RunAsAdministrator

[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [ValidateNotNullOrEmpty()]
    [string]$ServiceName = 'TechBenchWhdSync',

    [ValidateNotNullOrEmpty()]
    [string]$InstallDirectory = "$env:ProgramFiles\CSRI\TechBench Sync Service",

    [ValidateNotNullOrEmpty()]
    [string]$DataDirectory = "$env:ProgramData\CSRI\TechBench Sync Service",

    [switch]$KeepBinaries,

    [switch]$RemoveCredential
)

$ErrorActionPreference = 'Stop'
$installPath = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$dataPath = [IO.Path]::GetFullPath($DataDirectory).TrimEnd('\')
$allowedInstallRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'CSRI')).TrimEnd('\') + '\'
$allowedDataRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'CSRI')).TrimEnd('\') + '\'

if (-not $installPath.StartsWith($allowedInstallRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to remove an install directory outside '$allowedInstallRoot': $installPath"
}

if (-not $dataPath.StartsWith($allowedDataRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to remove a data directory outside '$allowedDataRoot': $dataPath"
}

foreach ($protectedPath in @($installPath, $dataPath)) {
    if ((Test-Path -LiteralPath $protectedPath) -and
        ((Get-Item -LiteralPath $protectedPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to remove a reparse-point directory: $protectedPath"
    }
}

if ($PSCmdlet.ShouldProcess($ServiceName, 'Stop and remove the Windows service')) {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -ne $service) {
        if ($service.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped) {
            Stop-Service -Name $ServiceName -Force
            (Get-Service -Name $ServiceName).WaitForStatus(
                [ServiceProcess.ServiceControllerStatus]::Stopped,
                [TimeSpan]::FromSeconds(30))
        }

        $output = & "$env:SystemRoot\System32\sc.exe" delete $ServiceName 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to delete Windows service '$ServiceName': $($output -join [Environment]::NewLine)"
        }

        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) -and [DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 250
        }

        if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
            throw "Service '$ServiceName' is still pending deletion. Close Services.msc and retry."
        }
    }
}

if ($RemoveCredential -and (Test-Path -LiteralPath $dataPath)) {
    if ($PSCmdlet.ShouldProcess($dataPath, 'Permanently remove the protected WHD credential and service data')) {
        $helper = Join-Path $installPath 'TechBench.SyncService.exe'
        if (Test-Path -LiteralPath $helper) {
            & $helper --delete-whd-secret
            if ($LASTEXITCODE -ne 0) {
                throw 'The TechBench credential helper could not remove the protected credential.'
            }
        }

        Remove-Item -LiteralPath $dataPath -Recurse -Force
    }
}

if (-not $KeepBinaries -and (Test-Path -LiteralPath $installPath)) {
    if ($PSCmdlet.ShouldProcess($installPath, 'Remove the installed service binaries')) {
        Remove-Item -LiteralPath $installPath -Recurse -Force
    }
}

if (-not $RemoveCredential -and (Test-Path -LiteralPath $dataPath)) {
    Write-Host "The protected WHD credential was preserved at '$dataPath'."
    Write-Host 'Run this script again with -RemoveCredential to delete it permanently.'
}
