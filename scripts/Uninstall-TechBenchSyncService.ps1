#Requires -Version 5.1
#Requires -RunAsAdministrator

[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [ValidatePattern('^[A-Za-z0-9_.-]+$')]
    [string]$ServiceName = 'TechBenchWhdSync',

    [ValidateNotNullOrEmpty()]
    [string]$InstallDirectory = "$env:ProgramFiles\CSRI\TechBench Sync Service",

    [ValidateNotNullOrEmpty()]
    [string]$DataDirectory = "$env:ProgramData\CSRI\TechBench Sync Service",

    [ValidateNotNullOrEmpty()]
    [string]$ManagerDirectory = "$env:ProgramFiles\CSRI\TechBench Server Manager",

    [switch]$KeepBinaries,

    [switch]$RemoveCredential
)

$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitProcess) {
    throw 'Run the TechBench Sync Service uninstaller from 64-bit Windows PowerShell.'
}
$installPath = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$dataPath = [IO.Path]::GetFullPath($DataDirectory).TrimEnd('\')
$managerPath = [IO.Path]::GetFullPath($ManagerDirectory).TrimEnd('\')
$managerDataPath = [IO.Path]::GetFullPath(
    (Join-Path $env:ProgramData 'CSRI\TechBench Server Manager')).TrimEnd('\')
$allowedInstallRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'CSRI')).TrimEnd('\') + '\'
$allowedDataRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'CSRI')).TrimEnd('\') + '\'

function Remove-SafeDirectoryTree {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$AllowedRoot
    )

    $root = [IO.Path]::GetFullPath($AllowedRoot).TrimEnd('\')
    $rootPrefix = $root + '\'
    $target = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (-not $target.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        $target.Equals($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a directory tree outside '$rootPrefix': $target"
    }
    if (-not (Test-Path -LiteralPath $target)) { return }

    $targetPrefix = $target + '\'
    $pendingDirectories = New-Object 'Collections.Generic.Stack[string]'
    $directories = New-Object 'Collections.Generic.List[string]'
    $files = New-Object 'Collections.Generic.List[string]'
    $pendingDirectories.Push($target)

    while ($pendingDirectories.Count -gt 0) {
        $currentPath = $pendingDirectories.Pop()
        $currentItem = Get-Item -LiteralPath $currentPath -Force
        if (($currentItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
            -not $currentItem.PSIsContainer) {
            throw "Refusing to remove a reparse-point or non-directory tree: $currentPath"
        }
        [void]$directories.Add($currentPath)

        foreach ($child in @(Get-ChildItem -LiteralPath $currentPath -Force)) {
            $childPath = [IO.Path]::GetFullPath($child.FullName)
            if (-not $childPath.StartsWith(
                    $targetPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Refusing to remove a directory entry outside '$targetPrefix': $childPath"
            }
            if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing to remove a directory tree containing a reparse point: $childPath"
            }
            if ($child.PSIsContainer) {
                $pendingDirectories.Push($childPath)
            } else {
                [void]$files.Add($childPath)
            }
        }
    }

    foreach ($filePath in $files) {
        if (-not (Test-Path -LiteralPath $filePath)) { continue }
        $fileItem = Get-Item -LiteralPath $filePath -Force
        if (($fileItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
            $fileItem.PSIsContainer) {
            throw "Refusing to remove a changed or reparse-point file: $filePath"
        }
        Remove-Item -LiteralPath $filePath -Force
    }

    foreach ($directoryPath in @($directories | Sort-Object Length -Descending)) {
        if (-not (Test-Path -LiteralPath $directoryPath)) { continue }
        $directoryItem = Get-Item -LiteralPath $directoryPath -Force
        if (($directoryItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
            -not $directoryItem.PSIsContainer) {
            throw "Refusing to remove a changed or reparse-point directory: $directoryPath"
        }
        if ($null -ne (Get-ChildItem -LiteralPath $directoryPath -Force |
                Select-Object -First 1)) {
            throw "The directory tree changed during safe removal: $directoryPath"
        }
        Remove-Item -LiteralPath $directoryPath -Force
    }
}

if (-not $installPath.StartsWith($allowedInstallRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to remove an install directory outside '$allowedInstallRoot': $installPath"
}

if (-not $dataPath.StartsWith($allowedDataRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to remove a data directory outside '$allowedDataRoot': $dataPath"
}

if (-not $managerDataPath.StartsWith($allowedDataRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to remove Manager state outside '$allowedDataRoot': $managerDataPath"
}

if (-not $managerPath.StartsWith($allowedInstallRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to remove a manager directory outside '$allowedInstallRoot': $managerPath"
}

foreach ($protectedPath in @($installPath, $dataPath, $managerPath, $managerDataPath)) {
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
    if ($PSCmdlet.ShouldProcess($dataPath, 'Permanently remove the protected WHD/Sage credentials and service data')) {
        $helper = Join-Path $installPath 'TechBench.SyncService.exe'
        if (Test-Path -LiteralPath $helper) {
            & $helper --delete-whd-secret
            if ($LASTEXITCODE -ne 0) {
                throw 'The TechBench credential helper could not remove the protected WHD credential.'
            }

            & $helper --delete-sage-secret
            if ($LASTEXITCODE -ne 0) {
                throw 'The TechBench credential helper could not remove the protected Sage credential.'
            }
        }

        Remove-SafeDirectoryTree -Path $dataPath -AllowedRoot $allowedDataRoot
    }
}

if (-not $KeepBinaries -and (Test-Path -LiteralPath $managerDataPath)) {
    if ($PSCmdlet.ShouldProcess(
            $managerDataPath,
            'Permanently remove TechBench Server Manager update state')) {
        # Remove the fixed Manager state tree itself. Never trust paths recorded
        # inside a pending update journal during uninstall.
        Remove-SafeDirectoryTree -Path $managerDataPath -AllowedRoot $allowedDataRoot
    }
}

if (-not $KeepBinaries -and (Test-Path -LiteralPath $installPath)) {
    if ($PSCmdlet.ShouldProcess($installPath, 'Remove the installed service binaries')) {
        Remove-SafeDirectoryTree -Path $installPath -AllowedRoot $allowedInstallRoot
    }
}

if (-not $KeepBinaries) {
    $shortcutPath = Join-Path $env:ProgramData `
        'Microsoft\Windows\Start Menu\Programs\CSRI\TechBench Server Manager.lnk'
    if ((Test-Path -LiteralPath $shortcutPath) -and
        $PSCmdlet.ShouldProcess($shortcutPath, 'Remove the TechBench Server Manager shortcut')) {
        Remove-Item -LiteralPath $shortcutPath -Force
    }
    if ((Test-Path -LiteralPath $managerPath) -and
        $PSCmdlet.ShouldProcess($managerPath, 'Remove the TechBench Server Manager files')) {
        Remove-SafeDirectoryTree -Path $managerPath -AllowedRoot $allowedInstallRoot
    }
}

if ($KeepBinaries) {
    Write-Warning (
        "-KeepBinaries preserved the service and Server Manager binaries and " +
        "Manager update state at '$managerDataPath', including any pending journal or staged download. " +
        'Run a full uninstall without -KeepBinaries before a clean reinstall.')
}

if (-not $RemoveCredential -and (Test-Path -LiteralPath $dataPath)) {
    Write-Host "Protected WHD/Sage credentials and service data were preserved at '$dataPath'."
    Write-Host 'Run this script again with -RemoveCredential to delete them permanently.'
}
