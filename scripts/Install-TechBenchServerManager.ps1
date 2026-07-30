#Requires -Version 5.1
#Requires -RunAsAdministrator

[CmdletBinding()]
param(
    [string]$PackageDirectory = $PSScriptRoot,
    [string]$ManagerDirectory = "$env:ProgramFiles\CSRI\TechBench Server Manager"
)

$ErrorActionPreference = 'Stop'
$packagePath = [IO.Path]::GetFullPath($PackageDirectory)
if (-not (Test-Path -LiteralPath (Join-Path $packagePath 'package-manifest.json') -PathType Leaf)) {
    # When launched from the extracted package root, PSScriptRoot is already the package.
    $packagePath = [IO.Path]::GetFullPath($PSScriptRoot)
}
$manifestPath = Join-Path $packagePath 'package-manifest.json'
$payloadPath = Join-Path $packagePath 'server-manager'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $payloadPath 'TechBench.ServerManager.exe') -PathType Leaf)) {
    throw 'Run this script from the complete extracted TechBench Sync Service release package.'
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.Product -cne 'TechBench Sync Service' -or
    [int]$manifest.PackageFormatVersion -ne 1 -or
    -not [bool]$manifest.SelfContained) {
    throw 'The package manifest does not identify a supported self-contained TechBench release.'
}

$rootPrefix = $packagePath.TrimEnd('\') + '\'
$managerEntries = @($manifest.Files | Where-Object {
    ([string]$_.Path).StartsWith('server-manager\', [StringComparison]::OrdinalIgnoreCase)
})
if ($managerEntries.Count -lt 3) {
    throw 'The package manifest does not contain the compiled Server Manager payload.'
}
foreach ($entry in $managerEntries) {
    $source = [IO.Path]::GetFullPath((Join-Path $packagePath ([string]$entry.Path)))
    if (-not $source.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "The Server Manager package contains an unsafe or missing path: $($entry.Path)"
    }
    $item = Get-Item -LiteralPath $source
    if ($item.Length -ne [int64]$entry.Length -or
        -not (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.Equals(
            [string]$entry.Sha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Server Manager package verification failed: $($entry.Path)"
    }
}

$managerPath = [IO.Path]::GetFullPath($ManagerDirectory).TrimEnd('\')
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'CSRI')).TrimEnd('\') + '\'
if (-not $managerPath.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "ManagerDirectory must remain under '$allowedRoot'."
}

$stagePath = "$managerPath.stage-$([Guid]::NewGuid().ToString('N'))"
$backupPath = "$managerPath.backup-$([Guid]::NewGuid().ToString('N'))"
try {
    New-Item -ItemType Directory -Path $stagePath -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $payloadPath -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $stagePath -Recurse -Force
    }
    if (-not (Test-Path -LiteralPath (Join-Path $stagePath 'TechBench.ServerManager.exe') -PathType Leaf)) {
        throw 'The staged compiled Server Manager executable is missing.'
    }
    Get-Process -Name 'TechBench.ServerManager' -ErrorAction SilentlyContinue | Stop-Process -Force
    if (Test-Path -LiteralPath $managerPath) { Move-Item -LiteralPath $managerPath -Destination $backupPath }
    Move-Item -LiteralPath $stagePath -Destination $managerPath

    # Explorer must be able to read the target before Windows can show the
    # executable's administrator-elevation prompt. Keep writes restricted to
    # SYSTEM/Administrators and grant built-in Users read/execute only.
    $managerSecurity = [Security.AccessControl.DirectorySecurity]::new()
    $managerSecurity.SetAccessRuleProtection($true, $false)
    $administrators = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $managerSecurity.SetOwner($administrators)
    $inheritance = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $propagation = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow
    foreach ($entry in @(
        [PSCustomObject]@{
            Sid = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
            Rights = [Security.AccessControl.FileSystemRights]::FullControl
        },
        [PSCustomObject]@{
            Sid = $administrators
            Rights = [Security.AccessControl.FileSystemRights]::FullControl
        },
        [PSCustomObject]@{
            Sid = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-545')
            Rights = [Security.AccessControl.FileSystemRights]'ReadAndExecute, Synchronize'
        }
    )) {
        [void]$managerSecurity.AddAccessRule(
            [Security.AccessControl.FileSystemAccessRule]::new(
                $entry.Sid, $entry.Rights, $inheritance, $propagation, $allow))
    }
    Set-Acl -LiteralPath $managerPath -AclObject $managerSecurity

    $shortcutDirectory = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\CSRI'
    New-Item -ItemType Directory -Path $shortcutDirectory -Force | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $null
    try {
        $shortcut = $shell.CreateShortcut((Join-Path $shortcutDirectory 'TechBench Server Manager.lnk'))
        $shortcut.TargetPath = Join-Path $managerPath 'TechBench.ServerManager.exe'
        $shortcut.WorkingDirectory = $managerPath
        $shortcut.IconLocation = "$(Join-Path $managerPath 'TechBench.ServerManager.exe'),0"
        $shortcut.Description = 'Manage and update the TechBench Sync Service'
        $shortcut.Save()
    } finally {
        if ($null -ne $shortcut) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) | Out-Null }
        if ($null -ne $shell) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null }
    }
    if (Test-Path -LiteralPath $backupPath) { Remove-Item -LiteralPath $backupPath -Recurse -Force }
} catch {
    if (Test-Path -LiteralPath $managerPath) { Remove-Item -LiteralPath $managerPath -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $backupPath) { Move-Item -LiteralPath $backupPath -Destination $managerPath -ErrorAction SilentlyContinue }
    throw
} finally {
    if (Test-Path -LiteralPath $stagePath) { Remove-Item -LiteralPath $stagePath -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host "Installed compiled TechBench Server Manager $($manifest.Version)."
Write-Host 'Start Menu shortcut now targets TechBench.ServerManager.exe directly.'
Start-Process -FilePath (Join-Path $managerPath 'TechBench.ServerManager.exe')
