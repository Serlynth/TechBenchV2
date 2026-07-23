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
$script:ProgramFilesRootPath = [IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\')
$script:ProgramDataRootPath = [IO.Path]::GetFullPath($env:ProgramData).TrimEnd('\')
$script:ProgramDataAnchorPath = [IO.Path]::GetFullPath(
    (Join-Path $script:ProgramDataRootPath 'CSRI')).TrimEnd('\')
$script:ManagerDataDirectory = [IO.Path]::GetFullPath(
    (Join-Path $script:ProgramDataAnchorPath 'TechBench Server Manager')).TrimEnd('\')
$script:ReleaseApiUrl = 'https://api.github.com/repos/Serlynth/TechBenchV2-Releases/releases?per_page=100'
$script:ReleaseDownloadPrefix = 'https://github.com/Serlynth/TechBenchV2-Releases/releases/download/'
$script:AvailableUpdate = $null
$script:AccountFieldIsDirty = $false
$script:UpdatingAccountField = $false
$script:OperationInProgress = $false
$script:RecoveryBlocked = $false
$script:TrayNoticeShown = $false
$script:TrayContextMenu = $null
$script:TrayExitMenuItem = $null
$script:NotifyIcon = $null
$script:ManagerIcon = $null
$script:OrganizationSettingRowVersions = @{}
$script:LatestSageSyncStatus = $null
$script:SynchronizationConfigurationLoaded = $false
$script:MaximumPackageBytes = 536870912
$script:MaximumExpandedBytes = 1073741824
$script:MaximumArchiveEntries = 5000
$script:ManagerCompanionFileNames = @(
    'TechBench-ServerManager.ps1',
    'Start-TechBenchServerManager.ps1',
    'Start-TechBenchServerManager.vbs',
    'csri-techbench-icon.ico'
)

if (-not [Environment]::Is64BitProcess) {
    throw 'TechBench Server Manager requires 64-bit Windows PowerShell 5.1 or later.'
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

$allowedProgramFilesRoot = [IO.Path]::GetFullPath(
    (Join-Path $env:ProgramFiles 'CSRI')).TrimEnd('\') + '\'
$allowedProgramDataRoot = $script:ProgramDataAnchorPath + '\'
if (-not $script:InstallDirectory.StartsWith(
        $allowedProgramFilesRoot, [StringComparison]::OrdinalIgnoreCase) -or
    -not $script:ManagerDirectory.StartsWith(
        $allowedProgramFilesRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "TechBench service and manager directories must remain under '$allowedProgramFilesRoot'."
}
if (-not $script:DataDirectory.StartsWith(
        $allowedProgramDataRoot, [StringComparison]::OrdinalIgnoreCase) -or
    -not $script:ManagerDataDirectory.StartsWith(
        $allowedProgramDataRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "TechBench data directories must remain under '$allowedProgramDataRoot'."
}
Assert-PathTreesDoNotOverlap `
    -FirstPath $script:InstallDirectory -SecondPath $script:ManagerDirectory `
    -FirstName 'InstallDirectory' -SecondName 'ManagerDirectory'
Assert-PathTreesDoNotOverlap `
    -FirstPath $script:DataDirectory -SecondPath $script:ManagerDataDirectory `
    -FirstName 'DataDirectory' -SecondName 'ManagerDataDirectory'

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
                throw "Refusing to adopt a ProgramData directory tree with more than 10000 entries: $Path"
            }
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing to adopt a ProgramData directory tree containing a reparse point: $($item.FullName)"
            }
            if ($item.PSIsContainer) {
                $pending.Push($item.FullName)
            }
        }
    }
}

function New-ProtectedProgramDataAnchorAcl {
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    $administrators = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $users = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-545')
    $security.SetOwner($administrators)
    foreach ($sid in @($administrators, $system)) {
        [void]$security.AddAccessRule(
            [Security.AccessControl.FileSystemAccessRule]::new(
                $sid,
                [Security.AccessControl.FileSystemRights]::FullControl,
                [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit',
                [Security.AccessControl.PropagationFlags]::None,
                [Security.AccessControl.AccessControlType]::Allow))
    }
    [void]$security.AddAccessRule(
        [Security.AccessControl.FileSystemAccessRule]::new(
            $users,
            [Security.AccessControl.FileSystemRights]'ReadAndExecute, Synchronize',
            [Security.AccessControl.InheritanceFlags]::None,
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow))
    return $security
}

function Assert-TrustedDirectoryAcl {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string[]]$AllowedWriteSidValues,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($owner -notin @('S-1-5-18', 'S-1-5-32-544') -or
        -not $acl.AreAccessRulesProtected) {
        throw "Refusing unsafe existing $Description '$Path'. It must be owned by SYSTEM or Administrators and have protected permissions. Inspect and remove any untrusted contents or junctions, then recreate it with an administrator-approved ACL; Server Manager will not take ownership of it."
    }
    $writeMask = [Security.AccessControl.FileSystemRights]::WriteData -bor
        [Security.AccessControl.FileSystemRights]::AppendData -bor
        [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor
        [Security.AccessControl.FileSystemRights]::WriteAttributes -bor
        [Security.AccessControl.FileSystemRights]::Delete -bor
        [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
        [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [Security.AccessControl.FileSystemRights]::TakeOwnership
    $unsafeRule = $acl.GetAccessRules(
        $true, $true, [Security.Principal.SecurityIdentifier]) | Where-Object {
            $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
            $AllowedWriteSidValues -notcontains $_.IdentityReference.Value -and
            ($_.FileSystemRights -band $writeMask) -ne 0
        } | Select-Object -First 1
    if ($null -ne $unsafeRule) {
        throw "Refusing unsafe existing $Description '$Path': '$($unsafeRule.IdentityReference.Value)' has write access. Inspect and remove any untrusted contents or junctions, then recreate it with an administrator-approved ACL."
    }
    return $acl
}

function Resolve-InstalledServiceAccountSidValue {
    $escapedServiceName = $script:ServiceName.Replace("'", "''")
    $service = Get-CimInstance -ClassName Win32_Service `
        -Filter "Name='$escapedServiceName'" -ErrorAction Stop
    if ($null -eq $service -or
        [string]::IsNullOrWhiteSpace([string]$service.StartName)) {
        throw "Windows service '$($script:ServiceName)' is not installed or its account cannot be resolved. Run the verified package installer to repair the ProgramData ACL before opening Server Manager."
    }
    $accountName = [string]$service.StartName
    switch ($accountName.ToUpperInvariant()) {
        'LOCALSYSTEM' { return 'S-1-5-18' }
        'NT AUTHORITY\SYSTEM' { return 'S-1-5-18' }
        'NT AUTHORITY\LOCAL SERVICE' { return 'S-1-5-19' }
        'NT AUTHORITY\LOCALSERVICE' { return 'S-1-5-19' }
        'NT AUTHORITY\NETWORK SERVICE' { return 'S-1-5-20' }
        'NT AUTHORITY\NETWORKSERVICE' { return 'S-1-5-20' }
    }
    try {
        return ([Security.Principal.NTAccount]::new($accountName)).Translate(
            [Security.Principal.SecurityIdentifier]).Value
    } catch {
        throw "The Windows service account '$accountName' cannot be translated to a SID. Run the verified package installer to repair the ProgramData ACL. $($_.Exception.Message)"
    }
}

function Assert-TrustedManagerSecretFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ServiceSidValue,
        [switch]$RequireServiceReadOnly
    )

    [void](Assert-NoReparsePointInPath `
        -Path $Path -TrustedRoot $script:ProgramDataRootPath -AllowLeaf)
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if ($item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "The protected credential path is not a regular file: $Path"
    }
    $linkTypeProperty = $item.PSObject.Properties['LinkType']
    if ($null -ne $linkTypeProperty -and
        -not [string]::IsNullOrWhiteSpace([string]$linkTypeProperty.Value)) {
        throw "Refusing an existing credential file that is a file-system link: $Path"
    }

    $allowedSidValues = @('S-1-5-18', 'S-1-5-32-544', $ServiceSidValue)
    $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($allowedSidValues -notcontains $owner) {
        throw "Refusing unsafe existing credential file '$Path': it must be owned by SYSTEM, Administrators, or the configured service identity. Run the verified package installer to repair and reprovision the credential."
    }
    $unsafeRule = $acl.GetAccessRules(
        $true, $true, [Security.Principal.SecurityIdentifier]) | Where-Object {
            $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
            $allowedSidValues -notcontains $_.IdentityReference.Value
        } | Select-Object -First 1
    if ($null -ne $unsafeRule) {
        throw "Refusing unsafe existing credential file '$Path': '$($unsafeRule.IdentityReference.Value)' has access. Run the verified package installer to repair and reprovision the credential."
    }

    if ($RequireServiceReadOnly) {
        if (-not $acl.AreAccessRulesProtected) {
            throw "The normalized credential file ACL still permits inheritance: $Path"
        }
        $writeMask = [Security.AccessControl.FileSystemRights]::WriteData -bor
            [Security.AccessControl.FileSystemRights]::AppendData -bor
            [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor
            [Security.AccessControl.FileSystemRights]::WriteAttributes -bor
            [Security.AccessControl.FileSystemRights]::Delete -bor
            [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
            [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
            [Security.AccessControl.FileSystemRights]::TakeOwnership
        $serviceWriteRule = $acl.GetAccessRules(
            $true, $true, [Security.Principal.SecurityIdentifier]) | Where-Object {
                $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
                $_.IdentityReference.Value -eq $ServiceSidValue -and
                ($_.FileSystemRights -band $writeMask) -ne 0
            } | Select-Object -First 1
        if ($null -ne $serviceWriteRule) {
            throw "The normalized credential file still grants write access to the service identity: $Path"
        }
    }
}

function Assert-LegacyServiceDataContents {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ServiceSidValue
    )

    [void](Assert-NoReparsePointInPath `
        -Path $Path -TrustedRoot $script:ProgramDataRootPath)
    Assert-NoReparsePointsInDirectoryTree -Path $Path
    $allowedSecretNames = @('whd.secret', 'sage.secret')
    foreach ($entry in @(Get-ChildItem -LiteralPath $Path -Force)) {
        if ($allowedSecretNames -notcontains $entry.Name) {
            throw "The legacy service-data directory contains an unexpected entry: $($entry.FullName). Run the verified package installer after reviewing and removing it."
        }
        Assert-TrustedManagerSecretFile `
            -Path $entry.FullName -ServiceSidValue $ServiceSidValue
    }
}

function Protect-LegacyServiceDataDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ServiceSidValue
    )

    $legacyAllowedWriteSids = @(
        'S-1-5-18',
        'S-1-5-32-544',
        $ServiceSidValue)
    [void](Assert-NoReparsePointInPath `
        -Path $Path -TrustedRoot $script:ProgramDataRootPath)
    [void](Assert-TrustedDirectoryAcl -Path $Path `
        -AllowedWriteSidValues $legacyAllowedWriteSids `
        -Description 'existing TechBench service data directory')
    Assert-LegacyServiceDataContents `
        -Path $Path -ServiceSidValue $ServiceSidValue
    $administrators = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $serviceSid = [Security.Principal.SecurityIdentifier]::new($ServiceSidValue)
    $accessEntries = @(
        [PSCustomObject]@{
            Sid = $system
            Rights = [Security.AccessControl.FileSystemRights]::FullControl
        },
        [PSCustomObject]@{
            Sid = $administrators
            Rights = [Security.AccessControl.FileSystemRights]::FullControl
        },
        [PSCustomObject]@{
            Sid = $serviceSid
            Rights = [Security.AccessControl.FileSystemRights]'ReadAndExecute, Synchronize'
        }
    )

    $directorySecurity = [Security.AccessControl.DirectorySecurity]::new()
    $directorySecurity.SetAccessRuleProtection($true, $false)
    $directorySecurity.SetOwner($administrators)
    foreach ($entry in $accessEntries) {
        [void]$directorySecurity.AddAccessRule(
            [Security.AccessControl.FileSystemAccessRule]::new(
                $entry.Sid,
                $entry.Rights,
                [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit',
                [Security.AccessControl.PropagationFlags]::None,
                [Security.AccessControl.AccessControlType]::Allow))
    }
    Set-Acl -LiteralPath $Path -AclObject $directorySecurity
    [void](Assert-NoReparsePointInPath `
        -Path $Path -TrustedRoot $script:ProgramDataRootPath)
    [void](Assert-TrustedDirectoryAcl -Path $Path `
        -AllowedWriteSidValues @('S-1-5-18', 'S-1-5-32-544') `
        -Description 'normalized TechBench service data directory')

    foreach ($secretName in @('whd.secret', 'sage.secret')) {
        $secretPath = Join-Path $Path $secretName
        [void](Assert-NoReparsePointInPath `
            -Path $secretPath -TrustedRoot $script:ProgramDataRootPath -AllowLeaf)
        if (-not (Test-Path -LiteralPath $secretPath)) { continue }
        Assert-TrustedManagerSecretFile `
            -Path $secretPath -ServiceSidValue $ServiceSidValue
        $fileSecurity = [Security.AccessControl.FileSecurity]::new()
        $fileSecurity.SetAccessRuleProtection($true, $false)
        $fileSecurity.SetOwner($administrators)
        foreach ($entry in $accessEntries) {
            [void]$fileSecurity.AddAccessRule(
                [Security.AccessControl.FileSystemAccessRule]::new(
                    $entry.Sid,
                    $entry.Rights,
                    [Security.AccessControl.AccessControlType]::Allow))
        }
        Set-Acl -LiteralPath $secretPath -AclObject $fileSecurity
        Assert-TrustedManagerSecretFile `
            -Path $secretPath -ServiceSidValue $ServiceSidValue `
            -RequireServiceReadOnly
    }
}

function Assert-LegacyTechBenchAnchorCanMigrate {
    param([Parameter(Mandatory = $true)][string]$Path)

    $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($owner -notin @('S-1-5-18', 'S-1-5-32-544')) {
        throw 'The legacy CSRI anchor is not owned by SYSTEM or Administrators.'
    }
    $allowedChildren = @('TechBench Sync Service', 'TechBench Server Manager')
    foreach ($item in @(Get-ChildItem -LiteralPath $Path -Force)) {
        if (-not $item.PSIsContainer -or
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
            $allowedChildren -notcontains $item.Name) {
            throw "The legacy CSRI anchor contains an unexpected or unsafe entry: $($item.FullName)"
        }
        $childAcl = Get-Acl -LiteralPath $item.FullName -ErrorAction Stop
        $childOwner = $childAcl.GetOwner(
            [Security.Principal.SecurityIdentifier]).Value
        if ($childOwner -notin @('S-1-5-18', 'S-1-5-32-544') -or
            -not $childAcl.AreAccessRulesProtected) {
            throw "The legacy TechBench child is not protected and privileged-owned: $($item.FullName)"
        }
        $allowedWriteSids = @('S-1-5-18', 'S-1-5-32-544')
        if ($item.Name -eq 'TechBench Sync Service') {
            $serviceSidValue = Resolve-InstalledServiceAccountSidValue
            $allowedWriteSids += $serviceSidValue
            Assert-LegacyServiceDataContents `
                -Path $item.FullName -ServiceSidValue $serviceSidValue
        }
        [void](Assert-TrustedDirectoryAcl -Path $item.FullName `
            -AllowedWriteSidValues $allowedWriteSids `
            -Description "legacy $($item.Name) directory")
        Assert-NoReparsePointsInDirectoryTree -Path $item.FullName
    }
}

function New-ProtectedDirectoryAtomically {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ParentPath,
        [Parameter(Mandatory = $true)][Security.AccessControl.DirectorySecurity]$Security,
        [Parameter(Mandatory = $true)][string[]]$AllowedWriteSidValues,
        [Parameter(Mandatory = $true)][string]$Description
    )

    [void](Assert-NoReparsePointInPath `
        -Path $Path -TrustedRoot $script:ProgramDataRootPath)
    if (Test-Path -LiteralPath $Path -PathType Container) {
        [void](Assert-TrustedDirectoryAcl -Path $Path `
            -AllowedWriteSidValues $AllowedWriteSidValues -Description $Description)
        return $false
    }

    $temporaryPath = Join-Path $ParentPath `
        ('.{0}.create-{1}' -f [IO.Path]::GetFileName($Path), [Guid]::NewGuid().ToString('N'))
    $temporaryCreated = $false
    try {
        ([IO.DirectoryInfo]::new($temporaryPath)).Create($Security)
        $temporaryCreated = $true
        [void](Assert-NoReparsePointInPath `
            -Path $temporaryPath -TrustedRoot $script:ProgramDataRootPath)
        [void](Assert-TrustedDirectoryAcl -Path $temporaryPath `
            -AllowedWriteSidValues $AllowedWriteSidValues -Description "temporary $Description")
        try {
            [IO.Directory]::Move($temporaryPath, $Path)
            $temporaryCreated = $false
        } catch {
            if (-not (Test-Path -LiteralPath $Path -PathType Container)) { throw }
            [void](Assert-NoReparsePointInPath `
                -Path $Path -TrustedRoot $script:ProgramDataRootPath)
            [void](Assert-TrustedDirectoryAcl -Path $Path `
                -AllowedWriteSidValues $AllowedWriteSidValues -Description $Description)
        }
    } finally {
        if ($temporaryCreated -and (Test-Path -LiteralPath $temporaryPath -PathType Container)) {
            [void](Assert-NoReparsePointInPath `
                -Path $temporaryPath -TrustedRoot $script:ProgramDataRootPath)
            [void](Assert-TrustedDirectoryAcl -Path $temporaryPath `
                -AllowedWriteSidValues $AllowedWriteSidValues -Description "temporary $Description")
            [IO.Directory]::Delete($temporaryPath, $false)
        }
    }

    [void](Assert-NoReparsePointInPath `
        -Path $Path -TrustedRoot $script:ProgramDataRootPath)
    [void](Assert-TrustedDirectoryAcl -Path $Path `
        -AllowedWriteSidValues $AllowedWriteSidValues -Description $Description)
    return $true
}

function Initialize-ProtectedProgramDataAnchor {
    [void](Assert-NoReparsePointInPath `
        -Path $script:ProgramDataAnchorPath -TrustedRoot $script:ProgramDataRootPath)
    if (Test-Path -LiteralPath $script:ProgramDataAnchorPath -PathType Container) {
        try {
            [void](Assert-TrustedDirectoryAcl -Path $script:ProgramDataAnchorPath `
                -AllowedWriteSidValues @('S-1-5-18', 'S-1-5-32-544') `
                -Description 'ProgramData CSRI anchor')
        } catch {
            $unsafeReason = $_.Exception.Message
            try {
                Assert-LegacyTechBenchAnchorCanMigrate `
                    -Path $script:ProgramDataAnchorPath
            } catch {
                throw "$unsafeReason Automatic legacy-alpha ACL migration was refused: $($_.Exception.Message) Inspect the CSRI directory and repair it manually before retrying."
            }
            Set-Acl -LiteralPath $script:ProgramDataAnchorPath `
                -AclObject (New-ProtectedProgramDataAnchorAcl)
            [void](Assert-NoReparsePointInPath `
                -Path $script:ProgramDataAnchorPath `
                -TrustedRoot $script:ProgramDataRootPath)
            Assert-LegacyTechBenchAnchorCanMigrate `
                -Path $script:ProgramDataAnchorPath
            [void](Assert-TrustedDirectoryAcl -Path $script:ProgramDataAnchorPath `
                -AllowedWriteSidValues @('S-1-5-18', 'S-1-5-32-544') `
                -Description 'migrated ProgramData CSRI anchor')
        }
    } else {
        [void](New-ProtectedDirectoryAtomically `
            -Path $script:ProgramDataAnchorPath `
            -ParentPath $script:ProgramDataRootPath `
            -Security (New-ProtectedProgramDataAnchorAcl) `
            -AllowedWriteSidValues @('S-1-5-18', 'S-1-5-32-544') `
            -Description 'ProgramData CSRI anchor')
    }
    [void](Assert-NoReparsePointInPath `
        -Path $script:ProgramDataAnchorPath -TrustedRoot $script:ProgramDataRootPath)
    return $script:ProgramDataAnchorPath
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

    $powershell = Join-Path ([Environment]::SystemDirectory) `
        'WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path -LiteralPath $powershell -PathType Leaf)) {
        throw "64-bit Windows PowerShell was not found: $powershell"
    }
    $quotedScript = '"{0}"' -f $PSCommandPath
    $argumentLine = '-NoProfile -STA -WindowStyle Hidden -ExecutionPolicy Bypass -File {0} -ServiceName "{1}" -InstallDirectory "{2}" -DataDirectory "{3}" -ManagerDirectory "{4}"' -f
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

[void](Initialize-ProtectedProgramDataAnchor)
[void](Assert-NoReparsePointInPath `
    -Path $script:DataDirectory -TrustedRoot $script:ProgramDataRootPath)
# Credential-directory validation and normalization is deliberately deferred
# until Open-ManagerLifetimeLock succeeds below, so no service-data mutation can
# race another Manager instance.
[void](Assert-NoReparsePointInPath `
    -Path $script:ManagerDataDirectory -TrustedRoot $script:ProgramDataRootPath)
[void](Assert-NoReparsePointInPath `
    -Path $script:InstallDirectory -TrustedRoot $script:ProgramFilesRootPath)
[void](Assert-NoReparsePointInPath `
    -Path $script:ManagerDirectory -TrustedRoot $script:ProgramFilesRootPath)

# The Start Menu shortcut opens in the install directory. Leave it immediately so
# Windows can rename that directory during a staged update and rollback.
Set-Location -LiteralPath ([Environment]::SystemDirectory)

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
    if ($null -ne $script:WhdBaseUrlBox) {
        Set-SynchronizationConfigurationEnabled `
            -Enabled ((-not $Busy) -and $script:SynchronizationConfigurationLoaded)
    }
    if ($null -ne $script:TrayExitMenuItem) {
        $script:TrayExitMenuItem.Enabled = -not $Busy
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
    # Windows PowerShell 5.1 treats a command that returns a JSON array as one
    # nested item when the command itself is placed inside @(...). Capture the
    # response first so @($releaseResponse) enumerates each GitHub release.
    $releaseResponse = Invoke-RestMethod -Uri $script:ReleaseApiUrl -Headers $headers `
        -Method Get -TimeoutSec 30
    $releases = @($releaseResponse)
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
    [void](Initialize-ProtectedProgramDataAnchor)
    $allowedRoot = $script:ProgramDataAnchorPath.TrimEnd('\') + '\'
    $rootPath = [IO.Path]::GetFullPath($script:ManagerDataDirectory)
    if (-not $rootPath.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The Server Manager data directory must remain under '$allowedRoot'."
    }

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

    [void](Assert-NoReparsePointInPath `
        -Path $rootPath -TrustedRoot $script:ProgramDataRootPath)
    if (Test-Path -LiteralPath $rootPath -PathType Container) {
        [void](Assert-TrustedDirectoryAcl -Path $rootPath `
            -AllowedWriteSidValues @('S-1-5-18', 'S-1-5-32-544') `
            -Description 'Server Manager data directory')
    } else {
        [void](New-ProtectedDirectoryAtomically `
            -Path $rootPath `
            -ParentPath $script:ProgramDataAnchorPath `
            -Security $security `
            -AllowedWriteSidValues @('S-1-5-18', 'S-1-5-32-544') `
            -Description 'Server Manager data directory')
    }
    Assert-NoReparsePointsInDirectoryTree -Path $rootPath

    [void](Assert-TrustedDirectoryAcl -Path $rootPath `
        -AllowedWriteSidValues @('S-1-5-18', 'S-1-5-32-544') `
        -Description 'Server Manager data directory')

    return $rootPath
}

function Initialize-AdminUpdateDirectory {
    $rootPath = Initialize-AdminManagerDataRoot

    $updatePath = Join-Path $rootPath `
        ("Update-{0}" -f [Guid]::NewGuid().ToString('N'))
    [void](Assert-NoReparsePointInPath `
        -Path $rootPath -TrustedRoot $script:ProgramDataRootPath)
    New-Item -ItemType Directory -Path $updatePath -Force | Out-Null
    [void](Assert-NoReparsePointInPath `
        -Path $updatePath -TrustedRoot $script:ProgramDataRootPath)
    return $updatePath
}

function Write-UpdateJournal {
    param([Parameter(Mandatory = $true)][object]$State)

    $rootPath = Initialize-AdminManagerDataRoot
    $journalPath = Join-Path $rootPath 'pending-update.json'
    $temporaryPath = Join-Path $rootPath `
        ("pending-update-{0}.tmp" -f [Guid]::NewGuid().ToString('N'))
    [void](Assert-NoReparsePointInPath `
        -Path $journalPath -TrustedRoot $script:ProgramDataRootPath -AllowLeaf)
    [void](Assert-NoReparsePointInPath `
        -Path $temporaryPath -TrustedRoot $script:ProgramDataRootPath -AllowLeaf)
    $jsonBytes = $null
    $journalStream = $null
    try {
        $jsonText = $State | ConvertTo-Json -Depth 4
        $jsonBytes = [Text.UTF8Encoding]::new($false).GetBytes($jsonText)
        $journalStream = [IO.FileStream]::new(
            $temporaryPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None,
            4096,
            [IO.FileOptions]::WriteThrough)
        $journalStream.Write($jsonBytes, 0, $jsonBytes.Length)
        $journalStream.Flush($true)
        $journalStream.Dispose()
        $journalStream = $null
        if (Test-Path -LiteralPath $journalPath -PathType Leaf) {
            [IO.File]::Replace($temporaryPath, $journalPath, $null)
        } else {
            Move-Item -LiteralPath $temporaryPath -Destination $journalPath
        }
    } finally {
        if ($null -ne $journalStream) {
            $journalStream.Dispose()
        }
        if ($null -ne $jsonBytes) {
            [Array]::Clear($jsonBytes, 0, $jsonBytes.Length)
        }
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        }
    }
}

function Remove-UpdateJournal {
    $rootPath = Initialize-AdminManagerDataRoot
    $journalPath = Join-Path $rootPath 'pending-update.json'
    [void](Assert-NoReparsePointInPath `
        -Path $journalPath -TrustedRoot $script:ProgramDataRootPath -AllowLeaf)
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

function Get-ValidatedJournalManagerFiles {
    param([Parameter(Mandatory = $true)]$Journal)

    $formatVersion = [int]$Journal.JournalFormatVersion
    if ($formatVersion -eq 1) {
        $managerTarget = [IO.Path]::GetFullPath([string]$Journal.ManagerTarget)
        $expectedManagerTarget = [IO.Path]::GetFullPath(
            (Join-Path $script:ManagerDirectory 'TechBench-ServerManager.ps1'))
        if (-not $managerTarget.Equals(
                $expectedManagerTarget, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The protected update journal contains an invalid Server Manager target.'
        }
        return ,([PSCustomObject]@{
            Name = 'TechBench-ServerManager.ps1'
            Target = $managerTarget
            Backup = Assert-JournalFilePath -Path ([string]$Journal.ManagerBackup) `
                -Root $script:ManagerDirectory -NamePrefix 'TechBench-ServerManager.backup-'
            Stage = Assert-JournalFilePath -Path ([string]$Journal.ManagerStage) `
                -Root $script:ManagerDirectory -NamePrefix 'TechBench-ServerManager.stage-'
            HadExisting = [bool]$Journal.ManagerHadExisting
            Installed = $false
        })
    }
    if ($formatVersion -ne 2) {
        throw 'The protected update journal uses an unsupported format.'
    }

    $journalFiles = @($Journal.ManagerFiles)
    if ($journalFiles.Count -ne $script:ManagerCompanionFileNames.Count) {
        throw 'The protected update journal does not list the complete Server Manager companion set.'
    }

    $validated = @()
    foreach ($expectedName in $script:ManagerCompanionFileNames) {
        $matches = @($journalFiles | Where-Object {
            ([string]$_.Name).Equals($expectedName, [StringComparison]::Ordinal)
        })
        if ($matches.Count -ne 1) {
            throw "The protected update journal has an invalid companion entry: $expectedName"
        }
        $entry = $matches[0]
        $target = [IO.Path]::GetFullPath([string]$entry.Target)
        $expectedTarget = [IO.Path]::GetFullPath(
            (Join-Path $script:ManagerDirectory $expectedName))
        if (-not $target.Equals($expectedTarget, [StringComparison]::OrdinalIgnoreCase)) {
            throw "The protected update journal has an invalid target for $expectedName."
        }

        $baseName = [IO.Path]::GetFileNameWithoutExtension($expectedName)
        $extension = [IO.Path]::GetExtension($expectedName)
        $backup = Assert-JournalFilePath -Path ([string]$entry.Backup) `
            -Root $script:ManagerDirectory -NamePrefix "$baseName.backup-"
        $stage = Assert-JournalFilePath -Path ([string]$entry.Stage) `
            -Root $script:ManagerDirectory -NamePrefix "$baseName.stage-"
        if (-not [IO.Path]::GetExtension($backup).Equals(
                $extension, [StringComparison]::OrdinalIgnoreCase) -or
            -not [IO.Path]::GetExtension($stage).Equals(
                $extension, [StringComparison]::OrdinalIgnoreCase)) {
            throw "The protected update journal has an invalid staged extension for $expectedName."
        }
        $validated += [PSCustomObject]@{
            Name = $expectedName
            Target = $target
            Backup = $backup
            Stage = $stage
            HadExisting = [bool]$entry.HadExisting
            Installed = [bool]$entry.Installed
        }
    }
    return $validated
}

function Test-ManagerFileMatchesInstalledPackageManifest {
    param([Parameter(Mandatory = $true)]$ManagerFile)

    try {
        $manifestPath = Join-Path $script:InstallDirectory 'package-manifest.json'
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
            -not (Test-Path -LiteralPath $ManagerFile.Target -PathType Leaf)) {
            return $false
        }
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if ($manifest.Product -cne 'TechBench Sync Service' -or
            [int]$manifest.PackageFormatVersion -ne 1) {
            return $false
        }
        $entries = @($manifest.Files | Where-Object {
            ([string]$_.Path).Equals(
                $ManagerFile.Name, [StringComparison]::OrdinalIgnoreCase)
        })
        if ($entries.Count -ne 1 -or
            [string]$entries[0].Sha256 -notmatch '^[0-9A-Fa-f]{64}$') {
            return $false
        }

        $packagedPath = Join-Path $script:InstallDirectory $ManagerFile.Name
        if (-not (Test-Path -LiteralPath $packagedPath -PathType Leaf)) {
            return $false
        }
        foreach ($path in @($packagedPath, $ManagerFile.Target)) {
            $item = Get-Item -LiteralPath $path
            if ($item.Length -ne [int64]$entries[0].Length -or
                -not (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.Equals(
                    [string]$entries[0].Sha256,
                    [StringComparison]::OrdinalIgnoreCase)) {
                return $false
            }
        }
        return $true
    } catch {
        return $false
    }
}

function Restore-ManagerFileFromBackup {
    param(
        [Parameter(Mandatory = $true)][string]$BackupPath,
        [Parameter(Mandatory = $true)][string]$TargetPath
    )

    $temporaryPath = Join-Path $script:ManagerDirectory `
        ('.rollback-{0}.tmp' -f [Guid]::NewGuid().ToString('N'))
    try {
        Copy-Item -LiteralPath $BackupPath -Destination $temporaryPath -Force
        $backupItem = Get-Item -LiteralPath $BackupPath
        $temporaryItem = Get-Item -LiteralPath $temporaryPath
        if ($backupItem.Length -ne $temporaryItem.Length -or
            (Get-FileHash -LiteralPath $BackupPath -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath $temporaryPath -Algorithm SHA256).Hash) {
            throw "The rollback copy failed verification: $BackupPath"
        }
        if (Test-Path -LiteralPath $TargetPath -PathType Leaf) {
            [IO.File]::Replace($temporaryPath, $TargetPath, $null)
        } else {
            Move-Item -LiteralPath $temporaryPath -Destination $TargetPath
        }
        Set-ManagerCompanionFileAcl -Path $TargetPath
    } finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        }
    }
}

function Restore-ManagerCompanionState {
    param(
        [Parameter(Mandatory = $true)][object[]]$ManagerFiles,
        [Parameter(Mandatory = $true)][bool]$SwapStarted
    )

    if (-not $SwapStarted) { return }

    for ($managerIndex = $ManagerFiles.Count - 1;
        $managerIndex -ge 0;
        $managerIndex--) {
        $managerFile = $ManagerFiles[$managerIndex]
        $backupExists = Test-Path -LiteralPath $managerFile.Backup -PathType Leaf
        $stageExists = Test-Path -LiteralPath $managerFile.Stage -PathType Leaf
        $targetExists = Test-Path -LiteralPath $managerFile.Target -PathType Leaf

        if ($backupExists) {
            Restore-ManagerFileFromBackup -BackupPath $managerFile.Backup `
                -TargetPath $managerFile.Target
            continue
        }
        if ($managerFile.HadExisting) {
            if (-not $stageExists -or -not $targetExists) {
                throw "The rollback state for '$($managerFile.Name)' is incomplete; its recovery artifacts were preserved."
            }
            continue
        }
        if ($stageExists -and $targetExists) {
            throw "The rollback state for '$($managerFile.Name)' contains both staged and installed copies; its recovery artifacts were preserved."
        }
        if ($targetExists) {
            # Moving the newly installed file back to Stage records the original
            # absence without consuming the only recovery artifact.
            Move-Item -LiteralPath $managerFile.Target `
                -Destination $managerFile.Stage
        }
    }
}

function Remove-UpdateRecoveryArtifacts {
    param(
        [Parameter(Mandatory = $true)][string]$InstallParent,
        [Parameter(Mandatory = $true)][string]$ServiceStagePath,
        [Parameter(Mandatory = $true)][string]$ServiceBackupPath,
        [Parameter(Mandatory = $true)][object[]]$ManagerFiles
    )

    foreach ($path in @($ServiceStagePath, $ServiceBackupPath)) {
        if (Test-Path -LiteralPath $path) {
            Remove-VerifiedDirectory -Path $path -AllowedRoot $InstallParent
        }
    }
    foreach ($managerFile in $ManagerFiles) {
        foreach ($path in @($managerFile.Stage, $managerFile.Backup)) {
            if (Test-Path -LiteralPath $path) {
                Remove-Item -LiteralPath $path -Force
            }
        }
    }
}

function Repair-InterruptedUpdate {
    $rootPath = Initialize-AdminManagerDataRoot
    $journalPath = Join-Path $rootPath 'pending-update.json'
    [void](Assert-NoReparsePointInPath `
        -Path $journalPath -TrustedRoot $script:ProgramDataRootPath -AllowLeaf)
    if (-not (Test-Path -LiteralPath $journalPath -PathType Leaf)) { return $false }

    Add-StatusLine 'An interrupted service update was detected; recovering it now.'
    $journal = Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json
    if ([int]$journal.JournalFormatVersion -notin @(1, 2) -or
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
    $managerFiles = @(Get-ValidatedJournalManagerFiles -Journal $journal)

    $phase = [string]$journal.Phase
    if ($phase -notin @(
        'Prepared',
        'OldPayloadMoved',
        'NewPayloadInstalled',
        'ManagerSwapPrepared',
        'ManagerInstalled',
        'Committed',
        'RolledBack'
    )) {
        throw "The protected update journal contains an unsupported phase: $phase"
    }
    $legacyManagerNeedsManifestProof = $false
    $managerSwapArtifactEvidence = $false
    foreach ($managerFile in $managerFiles) {
        $backupExists = Test-Path -LiteralPath $managerFile.Backup -PathType Leaf
        $stageExists = Test-Path -LiteralPath $managerFile.Stage -PathType Leaf
        $targetExists = Test-Path -LiteralPath $managerFile.Target -PathType Leaf
        if ($backupExists -or
            (-not $managerFile.HadExisting -and -not $stageExists -and $targetExists)) {
            $managerSwapArtifactEvidence = $true
            break
        }
    }
    $managerStateNeedsClassification =
        $phase -notin @('Committed', 'RolledBack') -and (
            $phase -in @('ManagerSwapPrepared', 'ManagerInstalled') -or
            $managerSwapArtifactEvidence -or
            [int]$journal.JournalFormatVersion -eq 1)
    if ($managerStateNeedsClassification) {
        foreach ($managerFile in $managerFiles) {
            $backupExists = Test-Path -LiteralPath $managerFile.Backup -PathType Leaf
            $stageExists = Test-Path -LiteralPath $managerFile.Stage -PathType Leaf
            $targetExists = Test-Path -LiteralPath $managerFile.Target -PathType Leaf
            if ($managerFile.HadExisting -and -not $backupExists -and -not $stageExists) {
                if ([int]$journal.JournalFormatVersion -eq 1 -and $targetExists) {
                    # Alpha.9 consumed its only Manager backup during rollback and
                    # could then leave the v1 journal behind if journal deletion
                    # failed. Defer classification until the service payload has
                    # been restored, then prove this target matches that payload.
                    $legacyManagerNeedsManifestProof = $true
                } else {
                    throw "The interrupted Server Manager swap for '$($managerFile.Name)' cannot be classified because both its rollback and staged copies are missing. The protected update journal was retained for manual administrator repair."
                }
            }
            if (-not $managerFile.HadExisting -and -not $stageExists -and
                -not $targetExists) {
                throw "The interrupted Server Manager swap for '$($managerFile.Name)' has neither a staged nor installed copy. The protected update journal was retained for manual administrator repair."
            }
            if (-not $managerFile.HadExisting -and $stageExists -and
                $targetExists) {
                throw "The interrupted Server Manager swap for '$($managerFile.Name)' has both a staged and installed copy. The protected update journal was retained for manual administrator repair."
            }
        }
    }

    if ($phase -in @('Committed', 'RolledBack')) {
        Remove-UpdateRecoveryArtifacts -InstallParent $installParent `
            -ServiceStagePath $stagePath -ServiceBackupPath $backupPath `
            -ManagerFiles $managerFiles
        Remove-UpdateJournal
        $description = if ($phase -ceq 'Committed') {
            'committed service update'
        } else {
            'completed service rollback'
        }
        Add-StatusLine "The $description cleanup was completed."
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

    $managerSwapStarted =
        $phase -in @('ManagerSwapPrepared', 'ManagerInstalled') -or
        $managerSwapArtifactEvidence
    if ($legacyManagerNeedsManifestProof) {
        if ($managerFiles.Count -ne 1 -or
            -not (Test-ManagerFileMatchesInstalledPackageManifest `
                -ManagerFile $managerFiles[0])) {
            throw 'The legacy v1 update journal has no Manager rollback artifact, and the installed Manager does not match the restored service package manifest. Manual administrator repair is required.'
        }
        $managerSwapStarted = $false
        Add-StatusLine 'The legacy v1 Manager rollback was verified against the restored service package manifest.'
    }
    Restore-ManagerCompanionState -ManagerFiles $managerFiles `
        -SwapStarted $managerSwapStarted

    if ([bool]$journal.WasRunning) {
        Start-Service -Name $script:ServiceName
        Wait-ForStableRunningService
    }
    $journal.Phase = 'RolledBack'
    Write-UpdateJournal -State $journal
    Remove-UpdateRecoveryArtifacts -InstallParent $installParent `
        -ServiceStagePath $stagePath -ServiceBackupPath $backupPath `
        -ManagerFiles $managerFiles
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
        'Start-TechBenchServerManager.ps1',
        'Start-TechBenchServerManager.vbs',
        'csri-techbench-icon.ico',
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
    [void](Assert-NoReparsePointInPath `
        -Path $fullPath -TrustedRoot $script:ProgramFilesRootPath)
    return $fullPath
}

function Set-ManagerInstallDirectoryAcl {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    [void](Assert-NoReparsePointInPath `
        -Path $fullPath -TrustedRoot $script:ProgramFilesRootPath)
    if (Test-Path -LiteralPath $fullPath -PathType Container) {
        [void](Assert-TrustedDirectoryAcl -Path $fullPath `
            -AllowedWriteSidValues @('S-1-5-18', 'S-1-5-32-544') `
            -Description 'existing Server Manager install directory')
        Assert-NoReparsePointsInDirectoryTree -Path $fullPath
    } else {
        New-Item -ItemType Directory -Path $fullPath | Out-Null
    }
    [void](Assert-NoReparsePointInPath `
        -Path $fullPath -TrustedRoot $script:ProgramFilesRootPath)
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    $administrators = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $security.SetOwner($administrators)
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
        [void]$security.AddAccessRule(
            [Security.AccessControl.FileSystemAccessRule]::new(
                $entry.Sid, $entry.Rights, $inheritance, $propagation, $allow))
    }
    Set-Acl -LiteralPath $fullPath -AclObject $security
    [void](Assert-NoReparsePointInPath `
        -Path $fullPath -TrustedRoot $script:ProgramFilesRootPath)
    [void](Assert-TrustedDirectoryAcl -Path $fullPath `
        -AllowedWriteSidValues @('S-1-5-18', 'S-1-5-32-544') `
        -Description 'Server Manager install directory')
    return $fullPath
}

function Assert-RegularManagerCompanionFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    [void](Assert-NoReparsePointInPath `
        -Path $Path -TrustedRoot $script:ProgramFilesRootPath -AllowLeaf)
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if ($item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "The Server Manager companion is not a regular file: $Path"
    }
    $linkTypeProperty = $item.PSObject.Properties['LinkType']
    if ($null -ne $linkTypeProperty -and
        -not [string]::IsNullOrWhiteSpace([string]$linkTypeProperty.Value)) {
        throw "Refusing a linked Server Manager companion file: $Path"
    }
}

function Set-ManagerCompanionFileAcl {
    param([Parameter(Mandatory = $true)][string]$Path)

    Assert-RegularManagerCompanionFile -Path $Path
    $security = [Security.AccessControl.FileSecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    $administrators = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $security.SetOwner($administrators)
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
        [void]$security.AddAccessRule(
            [Security.AccessControl.FileSystemAccessRule]::new(
                $entry.Sid,
                $entry.Rights,
                [Security.AccessControl.AccessControlType]::Allow))
    }
    Set-Acl -LiteralPath $Path -AclObject $security
    Assert-RegularManagerCompanionFile -Path $Path
    [void](Assert-TrustedDirectoryAcl -Path $Path `
        -AllowedWriteSidValues @('S-1-5-18', 'S-1-5-32-544') `
        -Description 'Server Manager companion file')
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
    $managerPath = Set-ManagerInstallDirectoryAcl -Path $managerPath
    $managerFiles = @($script:ManagerCompanionFileNames | ForEach-Object {
        $name = $_
        $baseName = [IO.Path]::GetFileNameWithoutExtension($name)
        $extension = [IO.Path]::GetExtension($name)
        $target = Join-Path $managerPath $name
        [void](Assert-NoReparsePointInPath `
            -Path $target -TrustedRoot $script:ProgramFilesRootPath -AllowLeaf)
        if (Test-Path -LiteralPath $target) {
            Set-ManagerCompanionFileAcl -Path $target
        }
        [ordered]@{
            Name = $name
            Target = $target
            Stage = Join-Path $managerPath "$baseName.stage-$operationId$extension"
            Backup = Join-Path $managerPath "$baseName.backup-$operationId$extension"
            HadExisting = Test-Path -LiteralPath $target -PathType Leaf
            Installed = $false
        }
    })
    $updateSucceeded = $false
    $rollbackSucceeded = $false
    $managerSwapStarted = $false
    $journalWritten = $false
    $journal = [ordered]@{
        JournalFormatVersion = 2
        Phase = 'Prepared'
        ServiceName = $script:ServiceName
        InstallPath = $installPath
        StagePath = $stagePath
        BackupPath = $backupPath
        ManagerFiles = $managerFiles
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

        # Update the separately installed GUI and its launch/icon companions only
        # after the service payload has passed its running-state stability check.
        # The running scripts and icon are fully loaded in memory.
        foreach ($managerFile in $managerFiles) {
            $packagedFile = Join-Path $PackageDirectory $managerFile.Name
            Copy-Item -LiteralPath $packagedFile `
                -Destination $managerFile.Stage -Force
            Set-ManagerCompanionFileAcl -Path $managerFile.Stage
            if ((Get-FileHash -LiteralPath $packagedFile -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath $managerFile.Stage -Algorithm SHA256).Hash) {
                throw "The staged Server Manager companion failed its final copy verification: $($managerFile.Name)"
            }
        }
        $journal.Phase = 'ManagerSwapPrepared'
        Write-UpdateJournal -State $journal
        $managerSwapStarted = $true
        foreach ($managerFile in $managerFiles) {
            if ([bool]$managerFile.HadExisting) {
                [IO.File]::Replace(
                    $managerFile.Stage, $managerFile.Target, $managerFile.Backup)
            } else {
                Move-Item -LiteralPath $managerFile.Stage `
                    -Destination $managerFile.Target
            }
            Set-ManagerCompanionFileAcl -Path $managerFile.Target
            $managerFile.Installed = $true
            Write-UpdateJournal -State $journal
        }
        $journal.Phase = 'ManagerInstalled'
        Write-UpdateJournal -State $journal
        $journal.Phase = 'Committed'
        Write-UpdateJournal -State $journal
        $updateSucceeded = $true
    } catch {
        $updateError = $_.Exception
        $rollbackMessage = 'The previous service files were not changed.'
        try {
            Restore-ManagerCompanionState -ManagerFiles $managerFiles `
                -SwapStarted $managerSwapStarted
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
            if ($journalWritten) {
                $journal.Phase = 'RolledBack'
                Write-UpdateJournal -State $journal
            }
            $rollbackSucceeded = $true
        } catch {
            $rollbackMessage = "Automatic rollback also failed: $($_.Exception.Message)"
        }
        throw "The service update failed: $($updateError.Message) $rollbackMessage"
    } finally {
        $cleanupIsSafe = -not $journalWritten -or
            $updateSucceeded -or $rollbackSucceeded
        if ($cleanupIsSafe) {
            try {
                Remove-UpdateRecoveryArtifacts -InstallParent $parentPath `
                    -ServiceStagePath $stagePath -ServiceBackupPath $backupPath `
                    -ManagerFiles $managerFiles
                if ($journalWritten) {
                    Remove-UpdateJournal
                    $journalWritten = $false
                }
            } catch {
                if ($journalWritten) {
                    Add-StatusLine "Recovery cleanup is incomplete; the protected update journal and remaining artifacts were retained. $($_.Exception.Message)"
                } else {
                    Add-StatusLine "Temporary update artifacts could not be completely removed. $($_.Exception.Message)"
                }
            }
        }
    }
}

function New-TechBenchDatabaseConnection {
    $configurationPath = Join-Path $script:InstallDirectory 'appsettings.json'
    [void](Assert-NoReparsePointInPath `
        -Path $configurationPath -TrustedRoot $script:ProgramFilesRootPath -AllowLeaf)
    if (-not (Test-Path -LiteralPath $configurationPath -PathType Leaf)) {
        throw 'The installed appsettings.json is missing, so TechBench SQL Server cannot be opened.'
    }
    $configurationItem = Get-Item -LiteralPath $configurationPath -Force
    if (($configurationItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        $configurationItem.PSIsContainer) {
        throw 'The installed appsettings.json is not a trusted regular file.'
    }

    try {
        $settings = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
        $server = [string]$settings.TechBenchSync.SqlServer
        $database = [string]$settings.TechBenchSync.Database
        if ([string]::IsNullOrWhiteSpace($server) -or
            [string]::IsNullOrWhiteSpace($database)) {
            throw 'SqlServer or Database is blank.'
        }

        Add-Type -AssemblyName System.Data
        $builder = [Data.SqlClient.SqlConnectionStringBuilder]::new()
        # Windows PowerShell 5.1 routes dotted assignments on this IDictionary-
        # derived builder through its keyword indexer (for example DataSource),
        # which rejects the property spelling. Use canonical SQL keywords.
        $builder['Data Source'] = $server
        $builder['Initial Catalog'] = $database
        $builder['Integrated Security'] = $true
        $builder['Application Name'] = 'TechBench Server Manager'
        $builder['Connect Timeout'] = 15
        $builder['Encrypt'] = $true
        $builder['TrustServerCertificate'] = [bool]$settings.TechBenchSync.TrustServerCertificate
        return [Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
    } catch {
        throw "The installed SQL configuration could not be read. $($_.Exception.Message)"
    }
}

function New-TechBenchStoredProcedureCommand {
    param(
        [Parameter(Mandatory = $true)][Data.SqlClient.SqlConnection]$Connection,
        [Parameter(Mandatory = $true)][string]$Name,
        [Data.SqlClient.SqlTransaction]$Transaction
    )

    $command = $Connection.CreateCommand()
    $command.CommandType = [Data.CommandType]::StoredProcedure
    $command.CommandText = $Name
    $command.CommandTimeout = 30
    if ($null -ne $Transaction) {
        $command.Transaction = $Transaction
    }
    return $command
}

function Get-TechBenchReaderValue {
    param(
        [Parameter(Mandatory = $true)]$Reader,
        [Parameter(Mandatory = $true)][string]$Name,
        $DefaultValue = $null
    )

    try {
        $ordinal = $Reader.GetOrdinal($Name)
    } catch [IndexOutOfRangeException] {
        return $DefaultValue
    }
    if ($Reader.IsDBNull($ordinal)) {
        return $DefaultValue
    }
    return $Reader.GetValue($ordinal)
}

function Format-TechBenchUtcValue {
    param($Value)

    if ($null -eq $Value -or $Value -is [DBNull]) {
        return 'Never'
    }
    $utc = [DateTime]$Value
    if ($utc.Kind -eq [DateTimeKind]::Unspecified) {
        $utc = [DateTime]::SpecifyKind($utc, [DateTimeKind]::Utc)
    }
    return $utc.ToLocalTime().ToString('g')
}

function Open-TechBenchAdminDatabaseConnection {
    $connection = New-TechBenchDatabaseConnection
    try {
        $connection.Open()
        $command = New-TechBenchStoredProcedureCommand `
            -Connection $connection -Name 'tb_app.GetCurrentUserContext'
        try {
            $reader = $command.ExecuteReader()
            try {
                if (-not $reader.Read()) {
                    throw 'tb_app.GetCurrentUserContext returned no row.'
                }
                $schemaVersion = [int](Get-TechBenchReaderValue `
                    -Reader $reader -Name 'SchemaVersion' -DefaultValue 0)
                $isAdmin = [bool](Get-TechBenchReaderValue `
                    -Reader $reader -Name 'IsAdmin' -DefaultValue $false)
                $loginName = [string](Get-TechBenchReaderValue `
                    -Reader $reader -Name 'AuthenticatedLoginName' -DefaultValue '')
            } finally {
                $reader.Dispose()
            }
        } finally {
            $command.Dispose()
        }

        if ($schemaVersion -ne 12) {
            throw "Server Manager requires TechBench database schema 12; the installed schema is $schemaVersion."
        }
        if (-not $isAdmin) {
            throw "'$loginName' is not a member of the TechBench Admin role. Sign in with an account in CSRI\TechBench_Admins."
        }
        return $connection
    } catch {
        $connection.Dispose()
        throw
    }
}

function Add-TechBenchSqlTextParameter {
    param(
        [Parameter(Mandatory = $true)][Data.SqlClient.SqlCommand]$Command,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][int]$Size,
        [AllowEmptyString()][string]$Value
    )

    $parameter = $Command.Parameters.Add($Name, [Data.SqlDbType]::NVarChar, $Size)
    $parameter.Value = if ($null -eq $Value) { [DBNull]::Value } else { $Value }
    return $parameter
}

function Get-TechBenchSynchronizationConfiguration {
    $connection = Open-TechBenchAdminDatabaseConnection
    try {
        $wantedKeys = @(
            'Whd.BaseUrl',
            'Whd.AuthenticationMode',
            'Whd.ServiceUsername',
            'Whd.AutoSyncEnabled',
            'Whd.AutoSyncMinutes',
            'Sage.SyncDsn',
            'Sage.SyncUsername'
        )
        $settings = @{}
        $rowVersions = @{}
        $command = New-TechBenchStoredProcedureCommand `
            -Connection $connection -Name 'tb_app.GetSettings'
        try {
            $reader = $command.ExecuteReader()
            try {
                while ($reader.Read()) {
                    $key = [string](Get-TechBenchReaderValue `
                        -Reader $reader -Name 'SettingKey' -DefaultValue '')
                    $scope = [string](Get-TechBenchReaderValue `
                        -Reader $reader -Name 'ScopeType' -DefaultValue '')
                    if ($wantedKeys -notcontains $key -or
                        -not $scope.Equals('Organization', [StringComparison]::OrdinalIgnoreCase)) {
                        continue
                    }
                    $settings[$key] = [string](Get-TechBenchReaderValue `
                        -Reader $reader -Name 'SettingValue' -DefaultValue '')
                    $rowVersion = Get-TechBenchReaderValue `
                        -Reader $reader -Name 'RowVersion'
                    if ($null -ne $rowVersion -and $rowVersion -isnot [DBNull]) {
                        $rowVersions[$key] = [byte[]]$rowVersion
                    }
                }
            } finally {
                $reader.Dispose()
            }
        } finally {
            $command.Dispose()
        }

        $whdStatus = [PSCustomObject]@{
            Status = 'NeverRun'
            Message = ''
            QueueDepth = 0
            LastAttemptAtUtc = $null
            LastSuccessfulAtUtc = $null
            LastError = ''
        }
        $command = New-TechBenchStoredProcedureCommand `
            -Connection $connection -Name 'tb_app.GetWhdSyncStatus'
        try {
            $reader = $command.ExecuteReader()
            try {
                if ($reader.Read()) {
                    $whdStatus.Status = [string](Get-TechBenchReaderValue `
                        -Reader $reader -Name 'Status' -DefaultValue 'NeverRun')
                    $whdStatus.Message = [string](Get-TechBenchReaderValue `
                        -Reader $reader -Name 'Message' -DefaultValue '')
                    $whdStatus.QueueDepth = [int](Get-TechBenchReaderValue `
                        -Reader $reader -Name 'QueueDepth' -DefaultValue 0)
                    $whdStatus.LastAttemptAtUtc = Get-TechBenchReaderValue `
                        -Reader $reader -Name 'RequestedAtUtc'
                    $whdStatus.LastSuccessfulAtUtc = Get-TechBenchReaderValue `
                        -Reader $reader -Name 'CompletedAtUtc'
                }
                if ($reader.NextResult() -and $reader.Read()) {
                    $whdStatus.LastAttemptAtUtc = Get-TechBenchReaderValue `
                        -Reader $reader -Name 'LastAttemptAtUtc' `
                        -DefaultValue $whdStatus.LastAttemptAtUtc
                    $whdStatus.LastSuccessfulAtUtc = Get-TechBenchReaderValue `
                        -Reader $reader -Name 'LastSuccessfulAtUtc' `
                        -DefaultValue $whdStatus.LastSuccessfulAtUtc
                    $whdStatus.LastError = [string](Get-TechBenchReaderValue `
                        -Reader $reader -Name 'LastError' -DefaultValue '')
                }
            } finally {
                $reader.Dispose()
            }
        } finally {
            $command.Dispose()
        }

        $sageStatus = [PSCustomObject]@{
            RequestId = $null
            Status = 'NeverRun'
            Message = ''
            QueueDepth = 0
            LastAttemptAtUtc = $null
            LastSuccessfulAtUtc = $null
            LastError = ''
            RequiresLargeRemovalConfirmation = $false
            ExistingCount = 0
            ReadCount = 0
            SavedCount = 0
            StaleCount = 0
        }
        $command = New-TechBenchStoredProcedureCommand `
            -Connection $connection -Name 'tb_app.GetSageSyncStatus'
        try {
            $reader = $command.ExecuteReader()
            try {
                if ($reader.Read()) {
                    $sageStatus.RequestId = Get-TechBenchReaderValue `
                        -Reader $reader -Name 'RequestId'
                    $sageStatus.Status = [string](Get-TechBenchReaderValue `
                        -Reader $reader -Name 'Status' -DefaultValue 'NeverRun')
                    $sageStatus.Message = [string](Get-TechBenchReaderValue `
                        -Reader $reader -Name 'Message' -DefaultValue '')
                    $sageStatus.QueueDepth = [int](Get-TechBenchReaderValue `
                        -Reader $reader -Name 'QueueDepth' -DefaultValue 0)
                    $sageStatus.LastAttemptAtUtc = Get-TechBenchReaderValue `
                        -Reader $reader -Name 'RequestedAtUtc'
                    $sageStatus.LastSuccessfulAtUtc = Get-TechBenchReaderValue `
                        -Reader $reader -Name 'CompletedAtUtc'
                    $sageStatus.RequiresLargeRemovalConfirmation = [bool](
                        Get-TechBenchReaderValue -Reader $reader `
                            -Name 'RequiresLargeRemovalConfirmation' -DefaultValue $false)
                    foreach ($countName in @('ExistingCount', 'ReadCount', 'SavedCount', 'StaleCount')) {
                        $sageStatus.$countName = [int](Get-TechBenchReaderValue `
                            -Reader $reader -Name $countName -DefaultValue 0)
                    }
                }
                if ($reader.NextResult() -and $reader.Read()) {
                    $sageStatus.LastAttemptAtUtc = Get-TechBenchReaderValue `
                        -Reader $reader -Name 'LastAttemptAtUtc' `
                        -DefaultValue $sageStatus.LastAttemptAtUtc
                    $sageStatus.LastSuccessfulAtUtc = Get-TechBenchReaderValue `
                        -Reader $reader -Name 'LastSuccessfulAtUtc' `
                        -DefaultValue $sageStatus.LastSuccessfulAtUtc
                    $sageStatus.LastError = [string](Get-TechBenchReaderValue `
                        -Reader $reader -Name 'LastError' -DefaultValue '')
                }
            } finally {
                $reader.Dispose()
            }
        } finally {
            $command.Dispose()
        }

        $userMappings = @()
        $command = New-TechBenchStoredProcedureCommand `
            -Connection $connection -Name 'tb_app.AdminGetWhdUserMappings'
        try {
            $reader = $command.ExecuteReader()
            try {
                while ($reader.Read()) {
                    $loginName = [string](Get-TechBenchReaderValue `
                        -Reader $reader -Name 'LoginName' -DefaultValue '')
                    $displayName = [string](Get-TechBenchReaderValue `
                        -Reader $reader -Name 'DisplayName' -DefaultValue '')
                    $userMappings += [PSCustomObject]@{
                        LoginName = $loginName
                        Label = if ([string]::IsNullOrWhiteSpace($displayName)) {
                            $loginName
                        } else {
                            "$displayName ($loginName)"
                        }
                        TechnicianExternalId = [string](Get-TechBenchReaderValue `
                            -Reader $reader -Name 'TechnicianExternalId' -DefaultValue '')
                    }
                }
            } finally {
                $reader.Dispose()
            }
        } finally {
            $command.Dispose()
        }

        $technicians = @([PSCustomObject]@{
            ExternalId = ''
            Label = 'No WHD technician (remove mapping)'
        })
        $command = New-TechBenchStoredProcedureCommand `
            -Connection $connection -Name 'tb_app.AdminGetWhdTechnicians'
        try {
            $reader = $command.ExecuteReader()
            try {
                while ($reader.Read()) {
                    $externalId = [string](Get-TechBenchReaderValue `
                        -Reader $reader -Name 'ExternalId' -DefaultValue '')
                    $displayName = [string](Get-TechBenchReaderValue `
                        -Reader $reader -Name 'DisplayName' -DefaultValue $externalId)
                    $isActive = [bool](Get-TechBenchReaderValue `
                        -Reader $reader -Name 'IsActive' -DefaultValue $true)
                    $technicians += [PSCustomObject]@{
                        ExternalId = $externalId
                        Label = $displayName + $(if ($isActive) { '' } else { ' (inactive)' })
                    }
                }
            } finally {
                $reader.Dispose()
            }
        } finally {
            $command.Dispose()
        }

        return [PSCustomObject]@{
            Settings = $settings
            RowVersions = $rowVersions
            WhdStatus = $whdStatus
            SageStatus = $sageStatus
            UserMappings = $userMappings
            Technicians = $technicians
        }
    } finally {
        $connection.Dispose()
    }
}

function Save-TechBenchOrganizationSettings {
    param([Parameter(Mandatory = $true)][hashtable]$Settings)

    $connection = Open-TechBenchAdminDatabaseConnection
    $newRowVersions = @{}
    $transaction = $null
    try {
        $transaction = $connection.BeginTransaction()
        foreach ($key in @($Settings.Keys | Sort-Object)) {
            $command = New-TechBenchStoredProcedureCommand `
                -Connection $connection `
                -Name 'tb_app.AdminSaveOrganizationSetting' `
                -Transaction $transaction
            try {
                [void](Add-TechBenchSqlTextParameter `
                    -Command $command -Name '@SettingKey' -Size 200 -Value $key)
                [void](Add-TechBenchSqlTextParameter `
                    -Command $command -Name '@SettingValue' -Size -1 `
                    -Value ([string]$Settings[$key]))
                $rowVersionParameter = $command.Parameters.Add(
                    '@ExpectedRowVersion', [Data.SqlDbType]::Binary, 8)
                $rowVersionParameter.Value = if (
                    $script:OrganizationSettingRowVersions.ContainsKey($key)) {
                    [byte[]]$script:OrganizationSettingRowVersions[$key]
                } else {
                    [DBNull]::Value
                }
                $requestParameter = $command.Parameters.Add(
                    '@RequestId', [Data.SqlDbType]::UniqueIdentifier)
                $requestParameter.Value = [Guid]::NewGuid()
                $reader = $command.ExecuteReader()
                try {
                    if (-not $reader.Read()) {
                        throw "SQL Server did not return the saved organization setting '$key'."
                    }
                    $newRowVersions[$key] = [byte[]](Get-TechBenchReaderValue `
                        -Reader $reader -Name 'RowVersion')
                } finally {
                    $reader.Dispose()
                }
            } finally {
                $command.Dispose()
            }
        }
        $transaction.Commit()
        foreach ($key in $newRowVersions.Keys) {
            $script:OrganizationSettingRowVersions[$key] = $newRowVersions[$key]
        }
    } catch {
        if ($null -ne $transaction -and $null -ne $transaction.Connection) {
            try { $transaction.Rollback() } catch { }
        }
        throw
    } finally {
        if ($null -ne $transaction) { $transaction.Dispose() }
        $connection.Dispose()
    }
}

function Save-WhdSynchronizationConfiguration {
    $baseUrlText = $script:WhdBaseUrlBox.Text.Trim().TrimEnd('/')
    $baseUri = $null
    if (-not [Uri]::TryCreate($baseUrlText, [UriKind]::Absolute, [ref]$baseUri) -or
        -not $baseUri.Scheme.Equals('https', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Enter the complete HTTPS Web Help Desk base URL.'
    }
    $serviceUsername = $script:WhdServiceUsernameBox.Text.Trim()
    if ([string]::IsNullOrWhiteSpace($serviceUsername)) {
        throw 'Enter the organization-wide WHD service username.'
    }
    $authenticationMode = [string]$script:WhdAuthenticationModeBox.SelectedItem
    if ($authenticationMode -notin @(
            'Auto', 'UsernamePassword', 'ApplicationApiKey', 'TechnicianApiKey')) {
        throw 'Select a supported WHD authentication mode.'
    }
    $minutes = 0
    if (-not [int]::TryParse($script:WhdAutoSyncMinutesBox.Text.Trim(), [ref]$minutes) -or
        $minutes -lt 1 -or $minutes -gt 120) {
        throw 'WHD synchronization interval must be from 1 through 120 minutes.'
    }

    Save-TechBenchOrganizationSettings -Settings @{
        'Whd.BaseUrl' = $baseUrlText
        'Whd.AuthenticationMode' = $authenticationMode
        'Whd.ServiceUsername' = $serviceUsername
        'Whd.AutoSyncEnabled' = $script:WhdAutoSyncEnabledBox.Checked.ToString()
        'Whd.AutoSyncMinutes' = $minutes.ToString()
    }
    Add-StatusLine 'Shared WHD server configuration saved to SQL Server.'
}

function Save-SageSynchronizationConfiguration {
    $dsn = $script:SageSyncDsnBox.Text.Trim()
    $username = $script:SageSyncUsernameBox.Text.Trim()
    if ([string]::IsNullOrWhiteSpace($dsn)) {
        throw 'Enter the server 32-bit Sage System DSN.'
    }
    if ([string]::IsNullOrWhiteSpace($username)) {
        throw 'Enter the organization-wide Sage ODBC username.'
    }
    Save-TechBenchOrganizationSettings -Settings @{
        'Sage.SyncDsn' = $dsn
        'Sage.SyncUsername' = $username
    }
    Add-StatusLine 'Shared Sage server configuration saved to SQL Server.'
}

function Request-WhdSynchronization {
    Save-WhdSynchronizationConfiguration
    $connection = Open-TechBenchAdminDatabaseConnection
    try {
        $command = New-TechBenchStoredProcedureCommand `
            -Connection $connection -Name 'tb_app.AdminRequestWhdSync'
        try {
            [void](Add-TechBenchSqlTextParameter `
                -Command $command -Name '@RequestType' -Size 40 -Value 'Incremental')
            $requestParameter = $command.Parameters.Add(
                '@RequestId', [Data.SqlDbType]::UniqueIdentifier)
            $requestParameter.Value = [Guid]::NewGuid()
            $reader = $command.ExecuteReader()
            try {
                $status = if ($reader.Read()) {
                    [string](Get-TechBenchReaderValue `
                        -Reader $reader -Name 'Status' -DefaultValue 'Queued')
                } else {
                    'Queued'
                }
            } finally {
                $reader.Dispose()
            }
        } finally {
            $command.Dispose()
        }
    } finally {
        $connection.Dispose()
    }
    Add-StatusLine $(if ($status -eq 'AlreadyQueued') {
        'A WHD synchronization is already queued or running.'
    } else {
        'WHD organization synchronization queued on the server.'
    })
}

function Request-SageSynchronization {
    param([switch]$AllowLargeRemoval)

    Save-SageSynchronizationConfiguration
    $confirmedRequestId = $null
    if ($AllowLargeRemoval) {
        if ($null -eq $script:LatestSageSyncStatus -or
            -not $script:LatestSageSyncStatus.RequiresLargeRemovalConfirmation -or
            $null -eq $script:LatestSageSyncStatus.RequestId) {
            throw 'There is no rejected Sage snapshot waiting for large-removal confirmation.'
        }
        $confirmedRequestId = [Guid]$script:LatestSageSyncStatus.RequestId
    }

    $connection = Open-TechBenchAdminDatabaseConnection
    try {
        $command = New-TechBenchStoredProcedureCommand `
            -Connection $connection -Name 'tb_app.AdminRequestSageSync'
        try {
            $requestParameter = $command.Parameters.Add(
                '@RequestId', [Data.SqlDbType]::UniqueIdentifier)
            $requestParameter.Value = [Guid]::NewGuid()
            $allowParameter = $command.Parameters.Add(
                '@AllowLargeRemoval', [Data.SqlDbType]::Bit)
            $allowParameter.Value = [bool]$AllowLargeRemoval
            $confirmedParameter = $command.Parameters.Add(
                '@ConfirmedRequestId', [Data.SqlDbType]::UniqueIdentifier)
            $confirmedParameter.Value = if ($null -eq $confirmedRequestId) {
                [DBNull]::Value
            } else {
                $confirmedRequestId
            }
            $reader = $command.ExecuteReader()
            try {
                $status = if ($reader.Read()) {
                    [string](Get-TechBenchReaderValue `
                        -Reader $reader -Name 'Status' -DefaultValue 'Queued')
                } else {
                    'Queued'
                }
            } finally {
                $reader.Dispose()
            }
        } finally {
            $command.Dispose()
        }
    } finally {
        $connection.Dispose()
    }
    Add-StatusLine $(if ($status -eq 'AlreadyQueued') {
        'A Sage customer synchronization is already queued or running.'
    } elseif ($AllowLargeRemoval) {
        'Confirmed Sage customer synchronization queued on the server.'
    } else {
        'Sage customer synchronization queued on the server.'
    })
}

function Save-WhdUserTechnicianMapping {
    $mapping = $script:WhdUserMappingBox.SelectedItem
    $technician = $script:WhdTechnicianBox.SelectedItem
    if ($null -eq $mapping -or [string]::IsNullOrWhiteSpace([string]$mapping.LoginName)) {
        throw 'Select a TechBench user.'
    }
    if ($null -eq $technician) {
        throw 'Select a WHD technician or the remove-mapping option.'
    }

    $connection = Open-TechBenchAdminDatabaseConnection
    try {
        $command = New-TechBenchStoredProcedureCommand `
            -Connection $connection -Name 'tb_app.AdminSaveWhdUserMapping'
        try {
            [void](Add-TechBenchSqlTextParameter `
                -Command $command -Name '@WindowsLoginName' -Size 256 `
                -Value ([string]$mapping.LoginName))
            $technicianParameter = $command.Parameters.Add(
                '@TechnicianExternalId', [Data.SqlDbType]::NVarChar, 120)
            $technicianParameter.Value = if (
                [string]::IsNullOrWhiteSpace([string]$technician.ExternalId)) {
                [DBNull]::Value
            } else {
                [string]$technician.ExternalId
            }
            $reader = $command.ExecuteReader()
            try {
                if ($reader.Read()) { }
            } finally {
                $reader.Dispose()
            }
        } finally {
            $command.Dispose()
        }
    } finally {
        $connection.Dispose()
    }
    Add-StatusLine "WHD technician mapping saved for $($mapping.LoginName)."
}

function Set-SynchronizationConfigurationEnabled {
    param([Parameter(Mandatory = $true)][bool]$Enabled)

    foreach ($control in @(
        $script:WhdBaseUrlBox,
        $script:WhdAuthenticationModeBox,
        $script:WhdServiceUsernameBox,
        $script:WhdAutoSyncEnabledBox,
        $script:WhdAutoSyncMinutesBox,
        $script:SaveWhdConfigurationButton,
        $script:RequestWhdSyncButton,
        $script:RefreshWhdConfigurationButton,
        $script:WhdUserMappingBox,
        $script:WhdTechnicianBox,
        $script:SaveWhdMappingButton,
        $script:SageSyncDsnBox,
        $script:SageSyncUsernameBox,
        $script:SaveSageConfigurationButton,
        $script:RequestSageSyncButton,
        $script:RefreshSageConfigurationButton
    )) {
        if ($null -ne $control) { $control.Enabled = $Enabled }
    }
    if ($null -ne $script:ConfirmSageRemovalButton) {
        $script:ConfirmSageRemovalButton.Enabled = $Enabled -and
            $null -ne $script:LatestSageSyncStatus -and
            $script:LatestSageSyncStatus.RequiresLargeRemovalConfirmation
    }
}

function Refresh-SynchronizationConfiguration {
    try {
        $configuration = Get-TechBenchSynchronizationConfiguration
        $script:OrganizationSettingRowVersions = $configuration.RowVersions
        $script:LatestSageSyncStatus = $configuration.SageStatus

        $script:WhdBaseUrlBox.Text = [string]$configuration.Settings['Whd.BaseUrl']
        $authenticationMode = [string]$configuration.Settings['Whd.AuthenticationMode']
        if ([string]::IsNullOrWhiteSpace($authenticationMode)) { $authenticationMode = 'Auto' }
        $script:WhdAuthenticationModeBox.SelectedItem = $authenticationMode
        if ($script:WhdAuthenticationModeBox.SelectedIndex -lt 0) {
            $script:WhdAuthenticationModeBox.SelectedItem = 'Auto'
        }
        $script:WhdServiceUsernameBox.Text = [string]$configuration.Settings['Whd.ServiceUsername']
        $autoSyncEnabled = $true
        [void][bool]::TryParse(
            [string]$configuration.Settings['Whd.AutoSyncEnabled'],
            [ref]$autoSyncEnabled)
        $script:WhdAutoSyncEnabledBox.Checked = $autoSyncEnabled
        $minutes = [string]$configuration.Settings['Whd.AutoSyncMinutes']
        $script:WhdAutoSyncMinutesBox.Text = if ([string]::IsNullOrWhiteSpace($minutes)) { '5' } else { $minutes }

        $whdStatus = $configuration.WhdStatus
        $whdHealth = if ([string]::IsNullOrWhiteSpace($whdStatus.LastError)) {
            $whdStatus.Status
        } else {
            'Error: ' + $whdStatus.LastError
        }
        $script:WhdSynchronizationStatusValue.Text =
            "Health: $whdHealth`r`nQueue: $($whdStatus.QueueDepth) | Last attempt: $(Format-TechBenchUtcValue $whdStatus.LastAttemptAtUtc)"

        $selectedLogin = if ($null -ne $script:WhdUserMappingBox.SelectedItem) {
            [string]$script:WhdUserMappingBox.SelectedItem.LoginName
        } else { '' }
        $script:WhdUserMappingBox.Items.Clear()
        foreach ($mapping in $configuration.UserMappings) {
            [void]$script:WhdUserMappingBox.Items.Add($mapping)
        }
        if ($script:WhdUserMappingBox.Items.Count -gt 0) {
            $selection = @($configuration.UserMappings | Where-Object {
                $_.LoginName -eq $selectedLogin
            } | Select-Object -First 1)
            $script:WhdUserMappingBox.SelectedItem = if ($selection.Count -eq 1) {
                $selection[0]
            } else {
                $script:WhdUserMappingBox.Items[0]
            }
        }
        $script:WhdTechnicianBox.Items.Clear()
        foreach ($technician in $configuration.Technicians) {
            [void]$script:WhdTechnicianBox.Items.Add($technician)
        }
        if ($script:WhdTechnicianBox.Items.Count -gt 0) {
            $selectedMapping = $script:WhdUserMappingBox.SelectedItem
            $targetExternalId = if ($null -ne $selectedMapping) {
                [string]$selectedMapping.TechnicianExternalId
            } else { '' }
            $selectedTechnician = @($configuration.Technicians | Where-Object {
                $_.ExternalId -eq $targetExternalId
            } | Select-Object -First 1)
            $script:WhdTechnicianBox.SelectedItem = if ($selectedTechnician.Count -eq 1) {
                $selectedTechnician[0]
            } else {
                $script:WhdTechnicianBox.Items[0]
            }
        }

        $script:SageSyncDsnBox.Text = [string]$configuration.Settings['Sage.SyncDsn']
        $script:SageSyncUsernameBox.Text = [string]$configuration.Settings['Sage.SyncUsername']
        $sageStatus = $configuration.SageStatus
        $sageHealth = if ([string]::IsNullOrWhiteSpace($sageStatus.LastError)) {
            $sageStatus.Status
        } else {
            'Error: ' + $sageStatus.LastError
        }
        $script:SageSynchronizationStatusValue.Text =
            "Health: $sageHealth`r`nQueue: $($sageStatus.QueueDepth) | Last attempt: $(Format-TechBenchUtcValue $sageStatus.LastAttemptAtUtc)`r`nLast snapshot: read $($sageStatus.ReadCount), saved $($sageStatus.SavedCount), stale $($sageStatus.StaleCount)"
        $script:SynchronizationConfigurationLoaded = $true
        Set-SynchronizationConfigurationEnabled -Enabled $true
        Add-StatusLine 'Shared WHD and Sage configuration refreshed from SQL Server.'
    } catch {
        $script:SynchronizationConfigurationLoaded = $false
        $script:LatestSageSyncStatus = $null
        Set-SynchronizationConfigurationEnabled -Enabled $false
        $errorText = "Configuration unavailable: $($_.Exception.Message)"
        if ($null -ne $script:WhdSynchronizationStatusValue) {
            $script:WhdSynchronizationStatusValue.Text = $errorText
        }
        if ($null -ne $script:SageSynchronizationStatusValue) {
            $script:SageSynchronizationStatusValue.Text = $errorText
        }
        throw
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

        $connection = New-TechBenchDatabaseConnection
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
    $managerDataRoot = Initialize-AdminManagerDataRoot
    $pendingJournalPath = Join-Path $managerDataRoot 'pending-update.json'
    [void](Assert-NoReparsePointInPath `
        -Path $pendingJournalPath -TrustedRoot $script:ProgramDataRootPath -AllowLeaf)
    if (Test-Path -LiteralPath $pendingJournalPath) {
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

function Repair-ManagerLaunchIntegration {
    $managerDataRoot = Initialize-AdminManagerDataRoot
    # Never mutate companion files while an interrupted update journal still
    # owns their rollback state. Recovery runs when the form is first shown.
    $pendingJournalPath = Join-Path $managerDataRoot 'pending-update.json'
    [void](Assert-NoReparsePointInPath `
        -Path $pendingJournalPath -TrustedRoot $script:ProgramDataRootPath -AllowLeaf)
    if (Test-Path -LiteralPath $pendingJournalPath -PathType Leaf) {
        return
    }

    $manifestPath = Join-Path $script:InstallDirectory 'package-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw 'The installed service package manifest is missing, so Server Manager launch files cannot be repaired safely.'
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.Product -cne 'TechBench Sync Service' -or
        [int]$manifest.PackageFormatVersion -ne 1) {
        throw 'The installed service package manifest is not a recognized TechBench manifest.'
    }

    [void](Set-ManagerInstallDirectoryAcl -Path $script:ManagerDirectory)
    foreach ($fileName in @(
        'Start-TechBenchServerManager.ps1',
        'Start-TechBenchServerManager.vbs',
        'csri-techbench-icon.ico'
    )) {
        $entries = @($manifest.Files | Where-Object {
            ([string]$_.Path).Equals($fileName, [StringComparison]::OrdinalIgnoreCase)
        })
        if ($entries.Count -ne 1 -or
            [string]$entries[0].Sha256 -notmatch '^[0-9A-Fa-f]{64}$') {
            throw "The installed package manifest does not contain one valid entry for $fileName."
        }
        $sourcePath = Join-Path $script:InstallDirectory $fileName
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "The installed service package is missing Server Manager companion: $fileName"
        }
        $sourceItem = Get-Item -LiteralPath $sourcePath
        $sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
        if ($sourceItem.Length -ne [int64]$entries[0].Length -or
            -not $sourceHash.Equals(
                [string]$entries[0].Sha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw "The installed Server Manager companion failed manifest verification: $fileName"
        }

        $targetPath = Join-Path $script:ManagerDirectory $fileName
        [void](Assert-NoReparsePointInPath `
            -Path $targetPath -TrustedRoot $script:ProgramFilesRootPath -AllowLeaf)
        if (Test-Path -LiteralPath $targetPath) {
            Set-ManagerCompanionFileAcl -Path $targetPath
        }
        if ((Test-Path -LiteralPath $targetPath -PathType Leaf) -and
            (Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash.Equals(
                $sourceHash, [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $temporaryPath = Join-Path $script:ManagerDirectory `
            (".{0}.repair-{1}.tmp" -f $fileName, [Guid]::NewGuid().ToString('N'))
        $backupPath = "$temporaryPath.backup"
        try {
            Copy-Item -LiteralPath $sourcePath -Destination $temporaryPath -Force
            if (-not (Get-FileHash -LiteralPath $temporaryPath -Algorithm SHA256).Hash.Equals(
                    $sourceHash, [StringComparison]::OrdinalIgnoreCase)) {
                throw "The repaired copy of $fileName failed verification."
            }
            if (Test-Path -LiteralPath $targetPath -PathType Leaf) {
                [IO.File]::Replace($temporaryPath, $targetPath, $backupPath)
                Remove-Item -LiteralPath $backupPath -Force
            } else {
                Move-Item -LiteralPath $temporaryPath -Destination $targetPath
            }
            Set-ManagerCompanionFileAcl -Path $targetPath
        } finally {
            foreach ($temporaryFile in @($temporaryPath, $backupPath)) {
                if (Test-Path -LiteralPath $temporaryFile) {
                    Remove-Item -LiteralPath $temporaryFile -Force -ErrorAction SilentlyContinue
                }
            }
        }
    }

    $shortcutDirectory = Join-Path $env:ProgramData `
        'Microsoft\Windows\Start Menu\Programs\CSRI'
    [void](Assert-NoReparsePointInPath `
        -Path $shortcutDirectory -TrustedRoot $script:ProgramDataRootPath)
    New-Item -ItemType Directory -Path $shortcutDirectory -Force | Out-Null
    [void](Assert-NoReparsePointInPath `
        -Path $shortcutDirectory -TrustedRoot $script:ProgramDataRootPath)
    $shortcutPath = Join-Path $shortcutDirectory 'TechBench Server Manager.lnk'
    [void](Assert-NoReparsePointInPath `
        -Path $shortcutPath -TrustedRoot $script:ProgramDataRootPath -AllowLeaf)
    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = Join-Path `
            ([Environment]::SystemDirectory) 'wscript.exe'
        $shortcut.Arguments = '"{0}" "{1}" "{2}" "{3}" "{4}"' -f `
            (Join-Path $script:ManagerDirectory 'Start-TechBenchServerManager.vbs'),
            $script:ServiceName,
            $script:InstallDirectory,
            $script:DataDirectory,
            $script:ManagerDirectory
        $shortcut.WorkingDirectory = $script:ManagerDirectory
        $shortcut.IconLocation = '{0},0' -f `
            (Join-Path $script:ManagerDirectory 'csri-techbench-icon.ico')
        $shortcut.Description = 'Manage and update the TechBench Sync Service'
        $shortcut.Save()
        [void](Assert-NoReparsePointInPath `
            -Path $shortcutPath -TrustedRoot $script:ProgramDataRootPath -AllowLeaf)
    } finally {
        if ($null -ne $shortcut) {
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) | Out-Null
        }
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
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

function Open-ManagerLifetimeLock {
    $managerDataRoot = Initialize-AdminManagerDataRoot
    $lockPath = Join-Path $managerDataRoot 'server-manager.lock'
    [void](Assert-NoReparsePointInPath `
        -Path $lockPath -TrustedRoot $script:ProgramDataRootPath -AllowLeaf)

    try {
        $stream = [IO.File]::Open(
            $lockPath,
            [IO.FileMode]::OpenOrCreate,
            [IO.FileAccess]::ReadWrite,
            [IO.FileShare]::None)
    } catch [IO.IOException] {
        $win32Error = $_.Exception.HResult -band 0xFFFF
        if ($win32Error -in @(32, 33)) {
            [void][Windows.Forms.MessageBox]::Show(
                'TechBench Server Manager is already running. Use its notification area icon to open it.',
                'TechBench Server Manager',
                [Windows.Forms.MessageBoxButtons]::OK,
                [Windows.Forms.MessageBoxIcon]::Information)
            return $null
        }
        throw
    }

    try {
        # Recheck after opening to close the path-validation/use race. The file is
        # intentionally persistent and remains empty between Manager sessions.
        [void](Assert-NoReparsePointInPath `
            -Path $lockPath -TrustedRoot $script:ProgramDataRootPath -AllowLeaf)
        return $stream
    } catch {
        $stream.Dispose()
        throw
    }
}

$script:ManagerLifetimeLock = Open-ManagerLifetimeLock
if ($null -eq $script:ManagerLifetimeLock) {
    return
}

try {
[void](Assert-NoReparsePointInPath `
    -Path $script:DataDirectory -TrustedRoot $script:ProgramDataRootPath)
if (Test-Path -LiteralPath $script:DataDirectory -PathType Container) {
    # This is the one startup normalization site. Serialize the idempotent
    # alpha.9 ACL migration with every other Manager mutation by holding the
    # lifetime lock for the entire operation.
    Protect-LegacyServiceDataDirectory `
        -Path $script:DataDirectory `
        -ServiceSidValue (Resolve-InstalledServiceAccountSidValue)
}
[void](Assert-NoReparsePointInPath `
    -Path $script:DataDirectory -TrustedRoot $script:ProgramDataRootPath)
[void](Assert-NoReparsePointsInDirectoryTree -Path $script:DataDirectory)
$script:LaunchIntegrationWarning = $null
try {
    Repair-ManagerLaunchIntegration
} catch {
    $script:LaunchIntegrationWarning = $_.Exception.Message
    $logPath = Join-Path $script:ManagerDataDirectory 'startup-errors.log'
    try {
        Initialize-AdminManagerDataRoot | Out-Null
        [void](Assert-NoReparsePointInPath `
            -Path $logPath -TrustedRoot $script:ProgramDataRootPath -AllowLeaf)
        $logEntry = '[{0}] Server Manager launch integration repair failed: {1}{2}' -f `
            [DateTime]::UtcNow.ToString('o'),
            $script:LaunchIntegrationWarning,
            [Environment]::NewLine
        [IO.File]::AppendAllText($logPath, $logEntry, [Text.Encoding]::UTF8)
    } catch {
        $logPath = $null
    }
    $message = "Server Manager opened, but its Start Menu launcher could not be repaired.`r`n`r`n$($script:LaunchIntegrationWarning)"
    if (-not [string]::IsNullOrWhiteSpace($logPath)) {
        $message += "`r`n`r`nDetails were logged to:`r`n$logPath"
    }
    [void][Windows.Forms.MessageBox]::Show(
        $message,
        'TechBench Server Manager',
        [Windows.Forms.MessageBoxButtons]::OK,
        [Windows.Forms.MessageBoxIcon]::Warning)
}

$script:MainForm = [Windows.Forms.Form]::new()
$script:MainForm.Text = 'TechBench Server Manager'
$script:MainForm.ClientSize = [Drawing.Size]::new(1190, 715)
$script:MainForm.MinimumSize = [Drawing.Size]::new(1206, 754)
$script:MainForm.StartPosition = [Windows.Forms.FormStartPosition]::CenterScreen
$script:MainForm.AutoScaleMode = [Windows.Forms.AutoScaleMode]::Dpi

# Copy the packaged icon completely into memory before assigning it. Server
# Manager updates can then replace the icon file while this process is open.
$managerIconPath = Join-Path $script:ManagerDirectory 'csri-techbench-icon.ico'
if (Test-Path -LiteralPath $managerIconPath -PathType Leaf) {
    try {
        $iconBytes = [IO.File]::ReadAllBytes($managerIconPath)
        $iconStream = [IO.MemoryStream]::new($iconBytes, $false)
        try {
            $sourceIcon = [Drawing.Icon]::new($iconStream)
            try {
                $script:ManagerIcon = [Drawing.Icon]$sourceIcon.Clone()
            } finally {
                $sourceIcon.Dispose()
            }
        } finally {
            $iconStream.Dispose()
        }
    } catch {
        $script:ManagerIcon = $null
    }
}
$script:MainForm.Icon = if ($null -ne $script:ManagerIcon) {
    $script:ManagerIcon
} else {
    [Drawing.SystemIcons]::Application
}

function Show-ManagerWindow {
    if (-not $script:MainForm.Visible) {
        $script:MainForm.Show()
    }
    if ($script:MainForm.WindowState -eq [Windows.Forms.FormWindowState]::Minimized) {
        $script:MainForm.WindowState = [Windows.Forms.FormWindowState]::Normal
    }
    $script:MainForm.BringToFront()
    [void]$script:MainForm.Activate()
}

function Clear-ManagerSecretFields {
    foreach ($box in @(
        $script:ServicePasswordBox,
        $script:WhdSecretBox,
        $script:SageSecretBox
    )) {
        if ($null -ne $box) {
            $box.Clear()
            $box.UseSystemPasswordChar = $true
        }
    }
    foreach ($checkBox in @(
        $script:ShowServicePasswordCheckBox,
        $script:ShowWhdSecretCheckBox,
        $script:ShowSageSecretCheckBox
    )) {
        if ($null -ne $checkBox) {
            $checkBox.Checked = $false
        }
    }
}

function Hide-ManagerToTray {
    Clear-ManagerSecretFields
    $script:MainForm.Hide()
    if (-not $script:TrayNoticeShown) {
        $script:TrayNoticeShown = $true
        $script:NotifyIcon.ShowBalloonTip(3000)
    }
}

function Request-ManagerExit {
    if ($script:OperationInProgress) {
        Show-ManagerWindow
        Show-ManagerMessage `
            -Text 'Wait for the current server operation to finish before exiting Server Manager.'
        return
    }

    $script:MainForm.Close()
}

$script:TrayContextMenu = [Windows.Forms.ContextMenuStrip]::new()
$trayOpenMenuItem = [Windows.Forms.ToolStripMenuItem]::new()
$trayOpenMenuItem.Text = 'Open TechBench Server Manager'
$script:TrayExitMenuItem = [Windows.Forms.ToolStripMenuItem]::new()
$script:TrayExitMenuItem.Text = 'Exit'
[void]$script:TrayContextMenu.Items.Add($trayOpenMenuItem)
[void]$script:TrayContextMenu.Items.Add(
    [Windows.Forms.ToolStripSeparator]::new())
[void]$script:TrayContextMenu.Items.Add($script:TrayExitMenuItem)

$script:NotifyIcon = [Windows.Forms.NotifyIcon]::new()
$script:NotifyIcon.Text = 'TechBench Server Manager'
$script:NotifyIcon.Icon = $script:MainForm.Icon
$script:NotifyIcon.ContextMenuStrip = $script:TrayContextMenu
$script:NotifyIcon.BalloonTipTitle = 'TechBench Server Manager'
$script:NotifyIcon.BalloonTipText =
    'Server Manager is still running. Double-click the tray icon to reopen it.'
$script:NotifyIcon.BalloonTipIcon = [Windows.Forms.ToolTipIcon]::Info
$script:NotifyIcon.Visible = $true

$trayOpenMenuItem.Add_Click({ Show-ManagerWindow })
$script:TrayExitMenuItem.Add_Click({ Request-ManagerExit })
$script:NotifyIcon.Add_DoubleClick({ Show-ManagerWindow })
$script:MainForm.Add_Resize({
    if ($script:MainForm.WindowState -eq [Windows.Forms.FormWindowState]::Minimized) {
        Hide-ManagerToTray
    }
})

$title = New-Label -Text 'TechBench Server Manager' -X 22 -Y 17 -Width 500 -Height 31 -Bold $true
$title.Font = [Drawing.Font]::new('Segoe UI', 16, [Drawing.FontStyle]::Bold)
$subtitle = New-Label `
    -Text 'Manage the server service, shared synchronization configuration, and machine-protected credentials.' `
    -X 24 -Y 52 -Width 900

$serviceGroup = [Windows.Forms.GroupBox]::new()
$serviceGroup.Text = 'Service'
$serviceGroup.Location = [Drawing.Point]::new(20, 83)
$serviceGroup.Size = [Drawing.Size]::new(750, 146)
$serviceGroup.Anchor = 'Top, Left'

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
$accountGroup.Anchor = 'Top, Left'
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
$credentialGroup.Anchor = 'Top, Left'
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
    -Text 'Shared WHD/Sage settings are managed on the right. Secrets remain machine-protected on this server.' `
    -X 18 -Y 108 -Width 710
$boundaryLabel.ForeColor = [Drawing.SystemColors]::GrayText
$credentialGroup.Controls.Add($boundaryLabel)

$updateGroup = [Windows.Forms.GroupBox]::new()
$updateGroup.Text = 'Service updates'
$updateGroup.Location = [Drawing.Point]::new(20, 539)
$updateGroup.Size = [Drawing.Size]::new(750, 82)
$updateGroup.Anchor = 'Top, Left'
$script:CheckUpdatesButton = New-Button -Text 'Check for updates' -X 18 -Y 29 -Width 150
$script:InstallUpdateButton = New-Button -Text 'Download && Install' -X 178 -Y 29 -Width 165
$script:InstallUpdateButton.Enabled = $false
$script:UpdateValue = New-Label -Text 'Not checked' -X 360 -Y 35 -Width 370
$updateGroup.Controls.AddRange(@(
    $script:CheckUpdatesButton,
    $script:InstallUpdateButton,
    $script:UpdateValue
))

$syncGroup = [Windows.Forms.GroupBox]::new()
$syncGroup.Text = 'Shared synchronization configuration'
$syncGroup.Location = [Drawing.Point]::new(780, 83)
$syncGroup.Size = [Drawing.Size]::new(390, 538)
$syncGroup.Anchor = 'Top, Right'

$syncTabs = [Windows.Forms.TabControl]::new()
$syncTabs.Location = [Drawing.Point]::new(12, 23)
$syncTabs.Size = [Drawing.Size]::new(366, 503)
$syncTabs.Anchor = 'Top, Bottom, Left, Right'

$whdTab = [Windows.Forms.TabPage]::new()
$whdTab.Text = 'Web Help Desk'
$whdTab.Padding = [Windows.Forms.Padding]::new(8)
$whdTab.AutoScroll = $true
$whdTab.Controls.Add((New-Label -Text 'Base URL' -X 12 -Y 12 -Width 150))
$script:WhdBaseUrlBox = [Windows.Forms.TextBox]::new()
$script:WhdBaseUrlBox.Location = [Drawing.Point]::new(12, 34)
$script:WhdBaseUrlBox.Size = [Drawing.Size]::new(326, 24)
$whdTab.Controls.Add($script:WhdBaseUrlBox)
$whdTab.Controls.Add((New-Label -Text 'Authentication mode' -X 12 -Y 67 -Width 180))
$script:WhdAuthenticationModeBox = [Windows.Forms.ComboBox]::new()
$script:WhdAuthenticationModeBox.Location = [Drawing.Point]::new(12, 89)
$script:WhdAuthenticationModeBox.Size = [Drawing.Size]::new(326, 24)
$script:WhdAuthenticationModeBox.DropDownStyle = [Windows.Forms.ComboBoxStyle]::DropDownList
[void]$script:WhdAuthenticationModeBox.Items.AddRange([object[]]@(
    'Auto',
    'UsernamePassword',
    'ApplicationApiKey',
    'TechnicianApiKey'
))
$whdTab.Controls.Add($script:WhdAuthenticationModeBox)
$whdTab.Controls.Add((New-Label -Text 'Organization-wide WHD username' -X 12 -Y 121 -Width 250))
$script:WhdServiceUsernameBox = [Windows.Forms.TextBox]::new()
$script:WhdServiceUsernameBox.Location = [Drawing.Point]::new(12, 143)
$script:WhdServiceUsernameBox.Size = [Drawing.Size]::new(326, 24)
$whdTab.Controls.Add($script:WhdServiceUsernameBox)
$script:WhdAutoSyncEnabledBox = [Windows.Forms.CheckBox]::new()
$script:WhdAutoSyncEnabledBox.Text = 'Automatically synchronize organization tickets'
$script:WhdAutoSyncEnabledBox.Location = [Drawing.Point]::new(12, 178)
$script:WhdAutoSyncEnabledBox.Size = [Drawing.Size]::new(326, 24)
$whdTab.Controls.Add($script:WhdAutoSyncEnabledBox)
$whdTab.Controls.Add((New-Label -Text 'Every' -X 12 -Y 209 -Width 44))
$script:WhdAutoSyncMinutesBox = [Windows.Forms.TextBox]::new()
$script:WhdAutoSyncMinutesBox.Location = [Drawing.Point]::new(57, 206)
$script:WhdAutoSyncMinutesBox.Size = [Drawing.Size]::new(48, 24)
$script:WhdAutoSyncMinutesBox.MaxLength = 3
$whdTab.Controls.Add($script:WhdAutoSyncMinutesBox)
$whdTab.Controls.Add((New-Label -Text 'minutes (1-120)' -X 112 -Y 209 -Width 130))
$script:SaveWhdConfigurationButton = New-Button -Text 'Save settings' -X 12 -Y 240 -Width 102
$script:RequestWhdSyncButton = New-Button -Text 'Sync now' -X 122 -Y 240 -Width 92
$script:RefreshWhdConfigurationButton = New-Button -Text 'Refresh' -X 222 -Y 240 -Width 92
$whdTab.Controls.AddRange(@(
    $script:SaveWhdConfigurationButton,
    $script:RequestWhdSyncButton,
    $script:RefreshWhdConfigurationButton
))
$script:WhdSynchronizationStatusValue = New-Label `
    -Text 'Configuration not loaded.' -X 12 -Y 278 -Width 326 -Height 48
$whdTab.Controls.Add($script:WhdSynchronizationStatusValue)
$mappingHeader = New-Label -Text 'AD user to WHD technician' -X 12 -Y 333 -Width 260 -Bold $true
$whdTab.Controls.Add($mappingHeader)
$script:WhdUserMappingBox = [Windows.Forms.ComboBox]::new()
$script:WhdUserMappingBox.Location = [Drawing.Point]::new(12, 359)
$script:WhdUserMappingBox.Size = [Drawing.Size]::new(326, 24)
$script:WhdUserMappingBox.DropDownStyle = [Windows.Forms.ComboBoxStyle]::DropDownList
$script:WhdUserMappingBox.DisplayMember = 'Label'
$whdTab.Controls.Add($script:WhdUserMappingBox)
$script:WhdTechnicianBox = [Windows.Forms.ComboBox]::new()
$script:WhdTechnicianBox.Location = [Drawing.Point]::new(12, 392)
$script:WhdTechnicianBox.Size = [Drawing.Size]::new(326, 24)
$script:WhdTechnicianBox.DropDownStyle = [Windows.Forms.ComboBoxStyle]::DropDownList
$script:WhdTechnicianBox.DisplayMember = 'Label'
$whdTab.Controls.Add($script:WhdTechnicianBox)
$script:SaveWhdMappingButton = New-Button -Text 'Save user mapping' -X 12 -Y 426 -Width 145
$whdTab.Controls.Add($script:SaveWhdMappingButton)

$sageTab = [Windows.Forms.TabPage]::new()
$sageTab.Text = 'Sage 50'
$sageTab.Padding = [Windows.Forms.Padding]::new(8)
$sageTab.AutoScroll = $true
$sageTab.Controls.Add((New-Label -Text 'Server 32-bit System DSN' -X 12 -Y 12 -Width 220))
$script:SageSyncDsnBox = [Windows.Forms.TextBox]::new()
$script:SageSyncDsnBox.Location = [Drawing.Point]::new(12, 34)
$script:SageSyncDsnBox.Size = [Drawing.Size]::new(326, 24)
$sageTab.Controls.Add($script:SageSyncDsnBox)
$sageTab.Controls.Add((New-Label -Text 'Organization-wide Sage ODBC username' -X 12 -Y 67 -Width 280))
$script:SageSyncUsernameBox = [Windows.Forms.TextBox]::new()
$script:SageSyncUsernameBox.Location = [Drawing.Point]::new(12, 89)
$script:SageSyncUsernameBox.Size = [Drawing.Size]::new(326, 24)
$sageTab.Controls.Add($script:SageSyncUsernameBox)
$sagePasswordNote = New-Label `
    -Text 'The Sage password is entered in Protected server credentials on the left.' `
    -X 12 -Y 122 -Width 326 -Height 38
$sagePasswordNote.ForeColor = [Drawing.SystemColors]::GrayText
$sageTab.Controls.Add($sagePasswordNote)
$script:SaveSageConfigurationButton = New-Button -Text 'Save settings' -X 12 -Y 164 -Width 102
$script:RequestSageSyncButton = New-Button -Text 'Sync now' -X 122 -Y 164 -Width 92
$script:RefreshSageConfigurationButton = New-Button -Text 'Refresh' -X 222 -Y 164 -Width 92
$sageTab.Controls.AddRange(@(
    $script:SaveSageConfigurationButton,
    $script:RequestSageSyncButton,
    $script:RefreshSageConfigurationButton
))
$script:ConfirmSageRemovalButton = New-Button `
    -Text 'Confirm large removal' -X 12 -Y 202 -Width 170
$script:ConfirmSageRemovalButton.Enabled = $false
$sageTab.Controls.Add($script:ConfirmSageRemovalButton)
$script:SageSynchronizationStatusValue = New-Label `
    -Text 'Configuration not loaded.' -X 12 -Y 242 -Width 326 -Height 92
$sageTab.Controls.Add($script:SageSynchronizationStatusValue)
$sageBoundaryNote = New-Label `
    -Text 'The service performs every organization-wide customer read. Client workstations keep only their own posting settings.' `
    -X 12 -Y 344 -Width 326 -Height 55
$sageBoundaryNote.ForeColor = [Drawing.SystemColors]::GrayText
$sageTab.Controls.Add($sageBoundaryNote)

[void]$syncTabs.TabPages.Add($whdTab)
[void]$syncTabs.TabPages.Add($sageTab)
$syncGroup.Controls.Add($syncTabs)

$script:StatusBox = [Windows.Forms.TextBox]::new()
$script:StatusBox.Location = [Drawing.Point]::new(20, 636)
$script:StatusBox.Size = [Drawing.Size]::new(1150, 58)
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
    $syncGroup,
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
$script:WhdUserMappingBox.Add_SelectedIndexChanged({
    $mapping = $script:WhdUserMappingBox.SelectedItem
    if ($null -eq $mapping -or $script:WhdTechnicianBox.Items.Count -eq 0) {
        return
    }
    $targetExternalId = [string]$mapping.TechnicianExternalId
    $selectedTechnician = $null
    foreach ($technician in $script:WhdTechnicianBox.Items) {
        if ([string]$technician.ExternalId -eq $targetExternalId) {
            $selectedTechnician = $technician
            break
        }
    }
    $script:WhdTechnicianBox.SelectedItem = if ($null -ne $selectedTechnician) {
        $selectedTechnician
    } else {
        $script:WhdTechnicianBox.Items[0]
    }
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
$script:SaveWhdConfigurationButton.Add_Click({
    Invoke-ManagerAction -BusyMessage 'Saving shared WHD configuration to SQL Server...' -Action {
        Save-WhdSynchronizationConfiguration
        Refresh-SynchronizationConfiguration
    }
})
$script:RequestWhdSyncButton.Add_Click({
    Invoke-ManagerAction -BusyMessage 'Saving WHD configuration and queuing server synchronization...' -Action {
        Request-WhdSynchronization
        Refresh-SynchronizationConfiguration
    }
})
$script:RefreshWhdConfigurationButton.Add_Click({
    Invoke-ManagerAction -BusyMessage 'Refreshing shared synchronization configuration...' -Action {
        Refresh-SynchronizationConfiguration
    }
})
$script:SaveWhdMappingButton.Add_Click({
    Invoke-ManagerAction -BusyMessage 'Saving the AD user to WHD technician mapping...' -Action {
        Save-WhdUserTechnicianMapping
        Refresh-SynchronizationConfiguration
    }
})
$script:SaveSageConfigurationButton.Add_Click({
    Invoke-ManagerAction -BusyMessage 'Saving shared Sage configuration to SQL Server...' -Action {
        Save-SageSynchronizationConfiguration
        Refresh-SynchronizationConfiguration
    }
})
$script:RequestSageSyncButton.Add_Click({
    Invoke-ManagerAction -BusyMessage 'Saving Sage configuration and queuing customer synchronization...' -Action {
        Request-SageSynchronization
        Refresh-SynchronizationConfiguration
    }
})
$script:RefreshSageConfigurationButton.Add_Click({
    Invoke-ManagerAction -BusyMessage 'Refreshing shared synchronization configuration...' -Action {
        Refresh-SynchronizationConfiguration
    }
})
$script:ConfirmSageRemovalButton.Add_Click({
    $status = $script:LatestSageSyncStatus
    if ($null -eq $status -or -not $status.RequiresLargeRemovalConfirmation) {
        Show-ManagerMessage `
            -Text 'There is no rejected Sage customer snapshot waiting for confirmation.'
        return
    }
    $warning = "The last Sage snapshot would remove $($status.StaleCount) of $($status.ExistingCount) existing Sage customer mapping(s). No customer data has changed yet.`r`n`r`nAuthorize that exact rejected snapshot and run Sage synchronization again?"
    if (Confirm-ManagerAction `
        -Text $warning -Title 'Confirm large Sage customer removal' -Icon Warning) {
        Invoke-ManagerAction -BusyMessage 'Queuing the confirmed Sage customer synchronization...' -Action {
            Request-SageSynchronization -AllowLargeRemoval
            Refresh-SynchronizationConfiguration
        }
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
    Clear-ManagerSecretFields
    if ($null -ne $script:NotifyIcon) {
        $script:NotifyIcon.Visible = $false
        $script:NotifyIcon.ContextMenuStrip = $null
        $script:NotifyIcon.Dispose()
        $script:NotifyIcon = $null
    }
    if ($null -ne $script:TrayContextMenu) {
        $script:TrayContextMenu.Dispose()
        $script:TrayContextMenu = $null
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
        Invoke-ManagerAction -BusyMessage 'Loading shared WHD and Sage configuration from SQL Server...' -Action {
            Refresh-SynchronizationConfiguration
        }
        Add-StatusLine 'Server Manager is ready. Shared synchronization configuration is Admin-managed here.'
    }
    if (-not [string]::IsNullOrWhiteSpace($script:LaunchIntegrationWarning)) {
        Add-StatusLine "WARNING: The Start Menu launcher could not be repaired. $($script:LaunchIntegrationWarning)"
    }
})

[Windows.Forms.Application]::Run($script:MainForm)
$script:MainForm.Dispose()
if ($null -ne $script:ManagerIcon) {
    $script:ManagerIcon.Dispose()
    $script:ManagerIcon = $null
}
} finally {
    if ($null -ne $script:ManagerLifetimeLock) {
        $script:ManagerLifetimeLock.Dispose()
        $script:ManagerLifetimeLock = $null
    }
}
