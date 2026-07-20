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

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

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

function Test-DirectoryHasProtectedAdminAcl {
    param([Parameter(Mandatory = $true)][string]$Path)

    try {
        $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
        if (-not $acl.AreAccessRulesProtected) { return $false }
        $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
        if ($owner -notin @('S-1-5-18', 'S-1-5-32-544')) { return $false }
        $writeMask = [Security.AccessControl.FileSystemRights]::WriteData -bor
            [Security.AccessControl.FileSystemRights]::AppendData -bor
            [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor
            [Security.AccessControl.FileSystemRights]::WriteAttributes -bor
            [Security.AccessControl.FileSystemRights]::Delete -bor
            [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
            [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
            [Security.AccessControl.FileSystemRights]::TakeOwnership
        $privilegedSids = @('S-1-5-18', 'S-1-5-32-544')
        $unsafeRule = $acl.GetAccessRules(
            $true, $true, [Security.Principal.SecurityIdentifier]) | Where-Object {
                $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
                $privilegedSids -notcontains $_.IdentityReference.Value -and
                ($_.FileSystemRights -band $writeMask) -ne 0
            } | Select-Object -First 1
        return $null -eq $unsafeRule
    } catch {
        return $false
    }
}

function Test-TrustedManagerScriptFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ProgramFilesRoot
    )

    try {
        [void](Assert-NoReparsePointInPath `
            -Path $Path -TrustedRoot $ProgramFilesRoot -AllowLeaf)
        if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
            return $false
        }
        $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
        if ($item.PSIsContainer -or
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            return $false
        }
        $linkTypeProperty = $item.PSObject.Properties['LinkType']
        if ($null -ne $linkTypeProperty -and
            -not [string]::IsNullOrWhiteSpace([string]$linkTypeProperty.Value)) {
            return $false
        }
        return Test-DirectoryHasProtectedAdminAcl -Path $Path
    } catch {
        return $false
    }
}

function Test-TrustedManagerLogDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    try {
        $programDataRoot = [IO.Path]::GetFullPath($env:ProgramData).TrimEnd('\')
        $anchorPath = Join-Path $programDataRoot 'CSRI'
        [void](Assert-NoReparsePointInPath `
            -Path $Path -TrustedRoot $programDataRoot)
        if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
            return $false
        }
        return (Test-DirectoryHasProtectedAdminAcl -Path $anchorPath) -and
            (Test-DirectoryHasProtectedAdminAcl -Path $Path)
    } catch {
        return $false
    }
}

function Get-WindowsPowerShellPath {
    if (-not [Environment]::Is64BitOperatingSystem) {
        throw 'TechBench Server Manager requires 64-bit Windows.'
    }

    $reportedSystemDirectory = [IO.Path]::GetFullPath(
        [Environment]::SystemDirectory).TrimEnd('\')
    $windowsDirectory = Split-Path -Parent $reportedSystemDirectory
    $systemDirectoryName = if ([Environment]::Is64BitProcess) {
        'System32'
    } else {
        # Sysnative bypasses WOW64 redirection for an unusual 32-bit launch.
        'Sysnative'
    }
    $path = Join-Path $windowsDirectory `
        "$systemDirectoryName\WindowsPowerShell\v1.0\powershell.exe"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "64-bit Windows PowerShell 5.1 was not found: $path"
    }
    return $path
}

function Write-StartupFailure {
    param([Parameter(Mandatory = $true)][Management.Automation.ErrorRecord]$ErrorRecord)

    try {
        $identityName = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    } catch {
        $identityName = 'Unknown'
    }
    $entry = @(
        ('[{0}] TechBench Server Manager startup failed.' -f `
            [DateTime]::UtcNow.ToString('o')),
        ('Windows identity: {0}' -f $identityName),
        ('Exception: {0}: {1}' -f `
            $ErrorRecord.Exception.GetType().FullName, $ErrorRecord.Exception.Message),
        ('Script stack: {0}' -f $ErrorRecord.ScriptStackTrace),
        ''
    ) -join [Environment]::NewLine

    $candidateDirectories = @()
    if (Test-IsAdministrator) {
        if (-not [string]::IsNullOrWhiteSpace($script:ManagerDataDirectory) -and
            (Test-TrustedManagerLogDirectory -Path $script:ManagerDataDirectory)) {
            $candidateDirectories += [PSCustomObject]@{
                Path = $script:ManagerDataDirectory
                TrustedRoot = [IO.Path]::GetFullPath($env:ProgramData).TrimEnd('\')
                Create = $false
            }
        }
        # An elevated launcher never writes through a user-owned LocalAppData or
        # Temp tree. A visible error without a log is safer if ManagerData is not
        # already protected.
    } else {
        foreach ($root in @(
            [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData),
            [IO.Path]::GetTempPath()
        )) {
            if (-not [string]::IsNullOrWhiteSpace($root)) {
                $candidateDirectories += [PSCustomObject]@{
                    Path = Join-Path $root 'CSRI\TechBench Server Manager'
                    TrustedRoot = $root
                    Create = $true
                }
            }
        }
    }
    foreach ($candidate in $candidateDirectories) {
        $directory = [string]$candidate.Path
        try {
            [void](Assert-NoReparsePointInPath `
                -Path $directory -TrustedRoot ([string]$candidate.TrustedRoot))
            if ([bool]$candidate.Create) {
                New-Item -ItemType Directory -Path $directory -Force | Out-Null
                [void](Assert-NoReparsePointInPath `
                    -Path $directory -TrustedRoot ([string]$candidate.TrustedRoot))
            }
            $logPath = Join-Path $directory 'startup-errors.log'
            [void](Assert-NoReparsePointInPath `
                -Path $logPath -TrustedRoot ([string]$candidate.TrustedRoot) -AllowLeaf)
            [IO.File]::AppendAllText($logPath, $entry, [Text.Encoding]::UTF8)
            return $logPath
        } catch {
            # Try the next user-writable location without masking the original error.
        }
    }
    return $null
}

function Show-StartupFailure {
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [string]$LogPath
    )

    $text = "TechBench Server Manager could not start.`r`n`r`n$Message"
    if (-not [string]::IsNullOrWhiteSpace($LogPath)) {
        $text += "`r`n`r`nDetails were logged to:`r`n$LogPath"
    }

    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        [void][Windows.Forms.MessageBox]::Show(
            $text,
            'TechBench Server Manager',
            [Windows.Forms.MessageBoxButtons]::OK,
            [Windows.Forms.MessageBoxIcon]::Error)
        return $true
    } catch {
        try {
            $shell = New-Object -ComObject WScript.Shell
            [void]$shell.Popup($text, 0, 'TechBench Server Manager', 16)
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
            return $true
        } catch {
            return $false
        }
    }
}

function Start-CorrectProcess {
    param([Parameter(Mandatory = $true)][string]$PowerShellPath)

    if ([string]::IsNullOrWhiteSpace($PSCommandPath)) {
        throw 'The TechBench Server Manager launcher must be run from its installed .ps1 file.'
    }

    $argumentLine = '-NoLogo -NoProfile -STA -WindowStyle Hidden -ExecutionPolicy Bypass -File "{0}" -ServiceName "{1}" -InstallDirectory "{2}" -DataDirectory "{3}" -ManagerDirectory "{4}"' -f `
        $PSCommandPath,
        $script:ServiceName,
        $script:InstallDirectory,
        $script:DataDirectory,
        $script:ManagerDirectory
    $startArguments = @{
        FilePath = $PowerShellPath
        ArgumentList = @($argumentLine)
        WindowStyle = 'Hidden'
        ErrorAction = 'Stop'
    }
    if (-not (Test-IsAdministrator)) {
        $startArguments.Verb = 'RunAs'
    }

    try {
        Start-Process @startArguments | Out-Null
    } catch {
        throw "Administrator access is required to manage the TechBench service. $($_.Exception.Message)"
    }
}

try {
    $script:ServiceName = $ServiceName
    $script:InstallDirectory = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
    $script:DataDirectory = [IO.Path]::GetFullPath($DataDirectory).TrimEnd('\')
    $script:ManagerDirectory = [IO.Path]::GetFullPath($ManagerDirectory).TrimEnd('\')
    $script:ManagerDataDirectory = [IO.Path]::GetFullPath(
        "$env:ProgramData\CSRI\TechBench Server Manager").TrimEnd('\')

    $allowedProgramFilesRoot = [IO.Path]::GetFullPath(
        (Join-Path $env:ProgramFiles 'CSRI')).TrimEnd('\') + '\'
    $programFilesRoot = [IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\')
    $programDataRoot = [IO.Path]::GetFullPath($env:ProgramData).TrimEnd('\')
    $allowedProgramDataRoot = [IO.Path]::GetFullPath(
        (Join-Path $programDataRoot 'CSRI')).TrimEnd('\') + '\'
    if (-not $script:InstallDirectory.StartsWith(
            $allowedProgramFilesRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not $script:ManagerDirectory.StartsWith(
            $allowedProgramFilesRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "TechBench service and manager directories must remain under '$allowedProgramFilesRoot'."
    }
    if (-not $script:DataDirectory.StartsWith(
            $allowedProgramDataRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The TechBench data directory must remain under '$allowedProgramDataRoot'."
    }
    Assert-PathTreesDoNotOverlap `
        -FirstPath $script:InstallDirectory -SecondPath $script:ManagerDirectory `
        -FirstName 'InstallDirectory' -SecondName 'ManagerDirectory'
    Assert-PathTreesDoNotOverlap `
        -FirstPath $script:DataDirectory -SecondPath $script:ManagerDataDirectory `
        -FirstName 'DataDirectory' -SecondName 'ManagerDataDirectory'
    [void](Assert-NoReparsePointInPath `
        -Path $script:InstallDirectory -TrustedRoot $programFilesRoot)
    [void](Assert-NoReparsePointInPath `
        -Path $script:ManagerDirectory -TrustedRoot $programFilesRoot)
    [void](Assert-NoReparsePointInPath `
        -Path $script:DataDirectory -TrustedRoot $programDataRoot)
    [void](Assert-NoReparsePointInPath `
        -Path $script:ManagerDataDirectory -TrustedRoot $programDataRoot)

    if (-not (Test-DirectoryHasProtectedAdminAcl `
            -Path $script:ManagerDirectory)) {
        throw 'The TechBench Server Manager directory is not protected against non-administrator changes. Run the verified package installer before launching it.'
    }
    $managerPath = Join-Path `
        $script:ManagerDirectory 'TechBench-ServerManager.ps1'
    if (-not (Test-TrustedManagerScriptFile `
            -Path $managerPath -ProgramFilesRoot $programFilesRoot)) {
        throw 'The installed TechBench Server Manager script is missing, linked, nonregular, or writable by a non-administrator. Run the verified package installer before launching it.'
    }

    $powerShellPath = Get-WindowsPowerShellPath
    $requiresRestart = -not [Environment]::Is64BitProcess -or
        [Threading.Thread]::CurrentThread.ApartmentState -ne
            [Threading.ApartmentState]::STA -or
        -not (Test-IsAdministrator)
    if ($requiresRestart) {
        Start-CorrectProcess -PowerShellPath $powerShellPath
        exit 0
    }

    [void](Assert-NoReparsePointInPath `
        -Path $script:ManagerDirectory -TrustedRoot $programFilesRoot)
    if (-not (Test-TrustedManagerScriptFile `
            -Path $managerPath -ProgramFilesRoot $programFilesRoot)) {
        throw 'The TechBench Server Manager script failed its final ACL or regular-file verification.'
    }

    & $managerPath `
        -ServiceName $script:ServiceName `
        -InstallDirectory $script:InstallDirectory `
        -DataDirectory $script:DataDirectory `
        -ManagerDirectory $script:ManagerDirectory
} catch {
    $failure = $_
    $logPath = Write-StartupFailure -ErrorRecord $failure
    $shown = Show-StartupFailure -Message $failure.Exception.Message -LogPath $logPath
    if ($shown) { exit 0 }
    exit 1
}
