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
$programFilesRootPath = [IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\')
$programDataRootPath = [IO.Path]::GetFullPath($env:ProgramData).TrimEnd('\')
$allowedInstallRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'CSRI')).TrimEnd('\') + '\'
$allowedDataRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'CSRI')).TrimEnd('\') + '\'

function Assert-PathTreesDoNotOverlap {
    param(
        [Parameter(Mandatory = $true)][string]$FirstPath,
        [Parameter(Mandatory = $true)][string]$SecondPath,
        [Parameter(Mandatory = $true)][string]$FirstName,
        [Parameter(Mandatory = $true)][string]$SecondName
    )

    $separator = [IO.Path]::DirectorySeparatorChar
    $alternateSeparator = [IO.Path]::AltDirectorySeparatorChar
    $firstCanonical = [IO.Path]::GetFullPath($FirstPath).
        Replace($alternateSeparator, $separator).TrimEnd($separator)
    $secondCanonical = [IO.Path]::GetFullPath($SecondPath).
        Replace($alternateSeparator, $separator).TrimEnd($separator)
    $firstPrefix = $firstCanonical + $separator
    $secondPrefix = $secondCanonical + $separator
    if ($firstCanonical.Equals($secondCanonical, [StringComparison]::OrdinalIgnoreCase) -or
        $firstCanonical.StartsWith($secondPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        $secondCanonical.StartsWith($firstPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$FirstName and $SecondName must not be equal or contain one another: '$firstCanonical' and '$secondCanonical'."
    }
}

function Assert-NoReparsePointInPath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$TrustedRoot,
        [switch]$AllowLeaf
    )

    $rootPath = [IO.Path]::GetFullPath($TrustedRoot).TrimEnd('\')
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $trustedRootItem = Get-Item -LiteralPath $rootPath -Force -ErrorAction Stop
    if (-not $trustedRootItem.PSIsContainer -or
        ($trustedRootItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "The trusted root is missing, not a directory, or a reparse point: $rootPath"
    }
    $rootPrefix = $rootPath + '\'
    if (-not $fullPath.Equals($rootPath, [StringComparison]::OrdinalIgnoreCase) -and
        -not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to inspect a path outside trusted root '$rootPath': $fullPath"
    }

    $relativePath = $fullPath.Substring($rootPath.Length).TrimStart('\')
    if ([string]::IsNullOrWhiteSpace($relativePath)) { return $fullPath }
    $segments = $relativePath.Split(
        [char[]]@('\'), [StringSplitOptions]::RemoveEmptyEntries)
    $currentPath = $rootPath
    for ($index = 0; $index -lt $segments.Length; $index++) {
        $currentPath = Join-Path $currentPath $segments[$index]
        try {
            $item = Get-Item -LiteralPath $currentPath -Force -ErrorAction Stop
        } catch [Management.Automation.ItemNotFoundException] {
            break
        }
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Refusing to follow a reparse-point path component: $currentPath"
        }
        $isAllowedLeaf = $AllowLeaf -and $index -eq ($segments.Length - 1)
        if (-not $item.PSIsContainer -and -not $isAllowedLeaf) {
            throw "A directory path component is not a directory: $currentPath"
        }
    }
    return $fullPath
}

function Assert-NoReparsePointsInDirectoryTree {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return }
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push([IO.Path]::GetFullPath($Path))
    $itemCount = 0
    while ($pending.Count -gt 0) {
        $currentDirectory = $pending.Pop()
        foreach ($item in @(Get-ChildItem -LiteralPath $currentDirectory -Force)) {
            $itemCount++
            if ($itemCount -gt 10000) {
                throw "Refusing to remove a directory tree with more than 10000 entries: $Path"
            }
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing to remove a directory tree containing a reparse point: $($item.FullName)"
            }
            if ($item.PSIsContainer) {
                $pending.Push($item.FullName)
            }
        }
    }
}

function Remove-SafeDirectoryTree {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$AllowedRoot,
        [Parameter(Mandatory = $true)][string]$TrustedRoot
    )

    $root = [IO.Path]::GetFullPath($AllowedRoot).TrimEnd('\')
    $rootPrefix = $root + '\'
    $target = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (-not $target.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        $target.Equals($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a directory tree outside '$rootPrefix': $target"
    }
    [void](Assert-NoReparsePointInPath -Path $target -TrustedRoot $TrustedRoot)
    if (-not (Test-Path -LiteralPath $target)) { return }
    Assert-NoReparsePointsInDirectoryTree -Path $target

    $targetPrefix = $target + '\'
    $pendingDirectories = New-Object 'Collections.Generic.Stack[string]'
    $directories = New-Object 'Collections.Generic.List[string]'
    $files = New-Object 'Collections.Generic.List[string]'
    $pendingDirectories.Push($target)

    while ($pendingDirectories.Count -gt 0) {
        $currentPath = $pendingDirectories.Pop()
        [void](Assert-NoReparsePointInPath `
            -Path $currentPath -TrustedRoot $TrustedRoot)
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
        [void](Assert-NoReparsePointInPath `
            -Path $filePath -TrustedRoot $TrustedRoot -AllowLeaf)
        $fileItem = Get-Item -LiteralPath $filePath -Force
        if (($fileItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
            $fileItem.PSIsContainer) {
            throw "Refusing to remove a changed or reparse-point file: $filePath"
        }
        Remove-Item -LiteralPath $filePath -Force
    }

    foreach ($directoryPath in @($directories | Sort-Object Length -Descending)) {
        if (-not (Test-Path -LiteralPath $directoryPath)) { continue }
        [void](Assert-NoReparsePointInPath `
            -Path $directoryPath -TrustedRoot $TrustedRoot)
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

function Open-UninstallManagerLifetimeLock {
    [void](Assert-NoReparsePointInPath `
        -Path $managerDataPath -TrustedRoot $programDataRootPath)
    if (-not (Test-Path -LiteralPath $managerDataPath -PathType Container)) {
        # Older or manually damaged installations may not have Manager state.
        # There is no existing Manager instance to serialize with in that case.
        return $null
    }

    $lockPath = Join-Path $managerDataPath 'server-manager.lock'
    [void](Assert-NoReparsePointInPath `
        -Path $lockPath -TrustedRoot $programDataRootPath -AllowLeaf)
    try {
        $stream = [IO.File]::Open(
            $lockPath,
            [IO.FileMode]::OpenOrCreate,
            [IO.FileAccess]::ReadWrite,
            [IO.FileShare]::None)
    } catch [IO.IOException] {
        $win32Error = $_.Exception.HResult -band 0xFFFF
        if ($win32Error -in @(32, 33)) {
            throw [InvalidOperationException]::new(
                'TechBench Server Manager is running. Exit it from the notification area icon before uninstalling. No service or files were changed.',
                $_.Exception)
        }
        throw
    }

    try {
        # Recheck after opening so a reparse-point substitution cannot redirect
        # the lock between validation and acquisition.
        [void](Assert-NoReparsePointInPath `
            -Path $lockPath -TrustedRoot $programDataRootPath -AllowLeaf)
        return $stream
    } catch {
        $stream.Dispose()
        throw
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
Assert-PathTreesDoNotOverlap `
    -FirstPath $installPath -SecondPath $managerPath `
    -FirstName 'InstallDirectory' -SecondName 'ManagerDirectory'
Assert-PathTreesDoNotOverlap `
    -FirstPath $dataPath -SecondPath $managerDataPath `
    -FirstName 'DataDirectory' -SecondName 'ManagerDataDirectory'

[void](Assert-NoReparsePointInPath `
    -Path $installPath -TrustedRoot $programFilesRootPath)
[void](Assert-NoReparsePointInPath `
    -Path $managerPath -TrustedRoot $programFilesRootPath)
[void](Assert-NoReparsePointInPath `
    -Path $dataPath -TrustedRoot $programDataRootPath)
[void](Assert-NoReparsePointInPath `
    -Path $managerDataPath -TrustedRoot $programDataRootPath)

$pendingUpdateJournalPath = Join-Path $managerDataPath 'pending-update.json'
[void](Assert-NoReparsePointInPath `
    -Path $pendingUpdateJournalPath -TrustedRoot $programDataRootPath -AllowLeaf)
if (Test-Path -LiteralPath $pendingUpdateJournalPath) {
    throw (
        "An interrupted TechBench service update is still pending at '$pendingUpdateJournalPath'. " +
        'Open TechBench Server Manager and let it complete or restore that update, exit the Manager from its notification-area icon, and then retry uninstall. No service or files were changed.')
}

$originalLocationPath = (Get-Location).Path
$managerLifetimeLock = $null
try {
    if (-not $WhatIfPreference) {
        # Acquire the same persistent FileStream lock held by the tray Manager.
        # This happens before any service or file mutation, and the stream stays
        # open until every installed launcher/binary has been removed.
        $managerLifetimeLock = Open-UninstallManagerLifetimeLock
    }
    Set-Location -LiteralPath ([Environment]::SystemDirectory)

    try {
if ($PSCmdlet.ShouldProcess($ServiceName, 'Stop and remove the Windows service')) {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -ne $service) {
        if ($service.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped) {
            Stop-Service -Name $ServiceName -Force
            (Get-Service -Name $ServiceName).WaitForStatus(
                [ServiceProcess.ServiceControllerStatus]::Stopped,
                [TimeSpan]::FromSeconds(30))
        }

        $scExecutable = Join-Path ([Environment]::SystemDirectory) 'sc.exe'
        $output = & $scExecutable delete $ServiceName 2>&1
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
        [void](Assert-NoReparsePointInPath `
            -Path $helper -TrustedRoot $programFilesRootPath -AllowLeaf)
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

        Remove-SafeDirectoryTree -Path $dataPath -AllowedRoot $allowedDataRoot `
            -TrustedRoot $programDataRootPath
    }
}

if (-not $KeepBinaries -and (Test-Path -LiteralPath $installPath)) {
    if ($PSCmdlet.ShouldProcess($installPath, 'Remove the installed service binaries')) {
        Remove-SafeDirectoryTree -Path $installPath -AllowedRoot $allowedInstallRoot `
            -TrustedRoot $programFilesRootPath
    }
}

if (-not $KeepBinaries) {
    $shortcutPath = Join-Path $env:ProgramData `
        'Microsoft\Windows\Start Menu\Programs\CSRI\TechBench Server Manager.lnk'
    [void](Assert-NoReparsePointInPath `
        -Path $shortcutPath -TrustedRoot $programDataRootPath -AllowLeaf)
    if ((Test-Path -LiteralPath $shortcutPath) -and
        $PSCmdlet.ShouldProcess($shortcutPath, 'Remove the TechBench Server Manager shortcut')) {
        Remove-Item -LiteralPath $shortcutPath -Force
    }
    if ((Test-Path -LiteralPath $managerPath) -and
        $PSCmdlet.ShouldProcess($managerPath, 'Remove the TechBench Server Manager files')) {
        Remove-SafeDirectoryTree -Path $managerPath -AllowedRoot $allowedInstallRoot `
            -TrustedRoot $programFilesRootPath
    }
}

    } finally {
        if ($null -ne $managerLifetimeLock) {
            $managerLifetimeLock.Dispose()
            $managerLifetimeLock = $null
        }
    }

    if (-not $KeepBinaries -and (Test-Path -LiteralPath $managerDataPath)) {
        if ($PSCmdlet.ShouldProcess(
                $managerDataPath,
                'Permanently remove TechBench Server Manager update state')) {
            # The lock must be released before its containing tree is removed.
            # By this point the shortcut and Manager binaries are already gone,
            # so a normal Manager launch cannot enter this narrow final window.
            # Never trust paths recorded inside a pending update journal.
            Remove-SafeDirectoryTree -Path $managerDataPath `
                -AllowedRoot $allowedDataRoot -TrustedRoot $programDataRootPath
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
} finally {
    if ($null -ne $managerLifetimeLock) {
        $managerLifetimeLock.Dispose()
    }
    if (Test-Path -LiteralPath $originalLocationPath -PathType Container) {
        Set-Location -LiteralPath $originalLocationPath
    }
}
