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
    [string]$ManagerDirectory = "$env:ProgramFiles\CSRI\TechBench Server Manager",

    [ValidatePattern('^[A-Za-z0-9_.-]+$')]
    [string]$ServiceName = 'TechBenchWhdSync',

    [ValidateNotNullOrEmpty()]
    [string]$DisplayName = 'TechBench Sync Service',

    [switch]$ReplaceConfiguration,

    [switch]$ConfigureWhdCredential,

    [switch]$ConfigureSageCredential,

    [switch]$SkipStart
)

$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitProcess) {
    throw 'Run the TechBench Sync Service installer from 64-bit Windows PowerShell.'
}
$sourcePath = [IO.Path]::GetFullPath($SourceDirectory)
$installPath = [IO.Path]::GetFullPath($InstallDirectory)
$dataPath = [IO.Path]::GetFullPath($DataDirectory)
$managerPath = [IO.Path]::GetFullPath($ManagerDirectory)
$programFilesRootPath = [IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\')
$allowedInstallRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'CSRI')).TrimEnd('\') + '\'
$programDataRootPath = [IO.Path]::GetFullPath($env:ProgramData).TrimEnd('\')
$programDataAnchorPath = [IO.Path]::GetFullPath(
    (Join-Path $programDataRootPath 'CSRI')).TrimEnd('\')
$managerDataRootPath = [IO.Path]::GetFullPath(
    (Join-Path $programDataAnchorPath 'TechBench Server Manager')).TrimEnd('\')
$allowedDataRoot = $programDataAnchorPath + '\'
$sourceExecutable = Join-Path $sourcePath 'TechBench.SyncService.exe'
$installedExecutable = Join-Path $installPath 'TechBench.SyncService.exe'
$isManagedServiceAccount = $ServiceAccount.EndsWith('$', [StringComparison]::Ordinal)

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

if (-not $installPath.StartsWith($allowedInstallRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "InstallDirectory must be a service-owned child directory under '$allowedInstallRoot': $installPath"
}

if (-not $dataPath.StartsWith($allowedDataRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "DataDirectory must be a service-owned child directory under '$allowedDataRoot': $dataPath"
}

if (-not $managerPath.StartsWith($allowedInstallRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "ManagerDirectory must be a child directory under '$allowedInstallRoot': $managerPath"
}

Assert-PathTreesDoNotOverlap `
    -FirstPath $installPath -SecondPath $managerPath `
    -FirstName 'InstallDirectory' -SecondName 'ManagerDirectory'
Assert-PathTreesDoNotOverlap `
    -FirstPath $dataPath -SecondPath $managerDataRootPath `
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
        throw "Refusing unsafe existing $Description '$Path'. It must be owned by SYSTEM or Administrators and have protected permissions. Inspect and remove any untrusted contents or junctions, then recreate it with an administrator-approved ACL; this installer will not take ownership of it."
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

function Assert-LegacyTechBenchAnchorCanMigrate {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [string]$ServiceSidValue
    )

    $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($owner -notin @('S-1-5-18', 'S-1-5-32-544')) {
        throw "The legacy CSRI anchor is not owned by SYSTEM or Administrators."
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
            if ([string]::IsNullOrWhiteSpace($ServiceSidValue)) {
                throw 'The configured service SID is required to validate the legacy service-data ACL.'
            }
            $allowedWriteSids += $ServiceSidValue
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

    [void](Assert-NoReparsePointInPath -Path $Path -TrustedRoot $programDataRootPath)
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
            -Path $temporaryPath -TrustedRoot $programDataRootPath)
        [void](Assert-TrustedDirectoryAcl -Path $temporaryPath `
            -AllowedWriteSidValues $AllowedWriteSidValues -Description "temporary $Description")
        try {
            [IO.Directory]::Move($temporaryPath, $Path)
            $temporaryCreated = $false
        } catch {
            if (-not (Test-Path -LiteralPath $Path -PathType Container)) { throw }
            [void](Assert-NoReparsePointInPath `
                -Path $Path -TrustedRoot $programDataRootPath)
            [void](Assert-TrustedDirectoryAcl -Path $Path `
                -AllowedWriteSidValues $AllowedWriteSidValues -Description $Description)
        }
    } finally {
        if ($temporaryCreated -and (Test-Path -LiteralPath $temporaryPath -PathType Container)) {
            [void](Assert-NoReparsePointInPath `
                -Path $temporaryPath -TrustedRoot $programDataRootPath)
            [void](Assert-TrustedDirectoryAcl -Path $temporaryPath `
                -AllowedWriteSidValues $AllowedWriteSidValues -Description "temporary $Description")
            [IO.Directory]::Delete($temporaryPath, $false)
        }
    }

    [void](Assert-NoReparsePointInPath -Path $Path -TrustedRoot $programDataRootPath)
    [void](Assert-TrustedDirectoryAcl -Path $Path `
        -AllowedWriteSidValues $AllowedWriteSidValues -Description $Description)
    return $true
}

function Initialize-ProtectedProgramDataAnchor {
    param([string]$ServiceSidValue)

    [void](Assert-NoReparsePointInPath `
        -Path $programDataAnchorPath -TrustedRoot $programDataRootPath)
    if (Test-Path -LiteralPath $programDataAnchorPath -PathType Container) {
        try {
            [void](Assert-TrustedDirectoryAcl -Path $programDataAnchorPath `
                -AllowedWriteSidValues @('S-1-5-18', 'S-1-5-32-544') `
                -Description 'ProgramData CSRI anchor')
        } catch {
            $unsafeReason = $_.Exception.Message
            try {
                Assert-LegacyTechBenchAnchorCanMigrate `
                    -Path $programDataAnchorPath -ServiceSidValue $ServiceSidValue
            } catch {
                throw "$unsafeReason Automatic legacy-alpha ACL migration was refused: $($_.Exception.Message) Inspect the CSRI directory and repair it manually before retrying."
            }
            Set-Acl -LiteralPath $programDataAnchorPath `
                -AclObject (New-ProtectedProgramDataAnchorAcl)
            [void](Assert-NoReparsePointInPath `
                -Path $programDataAnchorPath -TrustedRoot $programDataRootPath)
            Assert-LegacyTechBenchAnchorCanMigrate `
                -Path $programDataAnchorPath -ServiceSidValue $ServiceSidValue
            [void](Assert-TrustedDirectoryAcl -Path $programDataAnchorPath `
                -AllowedWriteSidValues @('S-1-5-18', 'S-1-5-32-544') `
                -Description 'migrated ProgramData CSRI anchor')
        }
    } else {
        [void](New-ProtectedDirectoryAtomically `
            -Path $programDataAnchorPath `
            -ParentPath $programDataRootPath `
            -Security (New-ProtectedProgramDataAnchorAcl) `
            -AllowedWriteSidValues @('S-1-5-18', 'S-1-5-32-544') `
            -Description 'ProgramData CSRI anchor')
    }
    [void](Assert-NoReparsePointInPath `
        -Path $programDataAnchorPath -TrustedRoot $programDataRootPath)
    return $programDataAnchorPath
}

[void](Assert-NoReparsePointInPath `
    -Path $installPath -TrustedRoot $programFilesRootPath)
[void](Assert-NoReparsePointInPath `
    -Path $managerPath -TrustedRoot $programFilesRootPath)
[void](Assert-NoReparsePointInPath -Path $dataPath -TrustedRoot $programDataRootPath)
[void](Assert-NoReparsePointInPath `
    -Path $managerDataRootPath -TrustedRoot $programDataRootPath)

function Invoke-ScChecked {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $scExecutable = Join-Path ([Environment]::SystemDirectory) 'sc.exe'
    $output = & $scExecutable @Arguments 2>&1
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

function Show-ServiceAccountCredentialDialog {
    param([Parameter(Mandatory = $true)][string]$AccountName)

    if (-not [Environment]::UserInteractive) {
        throw [PlatformNotSupportedException]::new(
            'The current PowerShell host does not provide an interactive desktop.')
    }

    Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
    Add-Type -AssemblyName System.Drawing -ErrorAction Stop

    $form = [Windows.Forms.Form]::new()
    $passwordBox = [Windows.Forms.TextBox]::new()
    try {
        $form.Text = 'TechBench Sync Service'
        $form.ClientSize = [Drawing.Size]::new(500, 220)
        $form.FormBorderStyle = [Windows.Forms.FormBorderStyle]::FixedDialog
        $form.StartPosition = [Windows.Forms.FormStartPosition]::CenterScreen
        $form.MaximizeBox = $false
        $form.MinimizeBox = $false
        $form.ShowInTaskbar = $true
        $form.AutoScaleMode = [Windows.Forms.AutoScaleMode]::Dpi

        $instructionLabel = [Windows.Forms.Label]::new()
        $instructionLabel.AutoSize = $false
        $instructionLabel.Location = [Drawing.Point]::new(18, 16)
        $instructionLabel.Size = [Drawing.Size]::new(464, 38)
        $instructionLabel.Text =
            'Enter the Windows password used to run the TechBench Sync Service.'

        $accountLabel = [Windows.Forms.Label]::new()
        $accountLabel.AutoSize = $true
        $accountLabel.Location = [Drawing.Point]::new(18, 62)
        $accountLabel.Text = 'Service account'

        $accountBox = [Windows.Forms.TextBox]::new()
        $accountBox.Location = [Drawing.Point]::new(142, 59)
        $accountBox.Size = [Drawing.Size]::new(340, 23)
        $accountBox.ReadOnly = $true
        $accountBox.TabStop = $false
        $accountBox.Text = $AccountName

        $passwordLabel = [Windows.Forms.Label]::new()
        $passwordLabel.AutoSize = $true
        $passwordLabel.Location = [Drawing.Point]::new(18, 99)
        $passwordLabel.Text = 'Password'

        $passwordBox.Location = [Drawing.Point]::new(142, 96)
        $passwordBox.Size = [Drawing.Size]::new(340, 23)
        $passwordBox.UseSystemPasswordChar = $true
        $passwordBox.ShortcutsEnabled = $false
        $passwordBox.AutoCompleteMode = [Windows.Forms.AutoCompleteMode]::None
        $passwordBox.AutoCompleteSource = [Windows.Forms.AutoCompleteSource]::None

        $showPasswordCheckBox = [Windows.Forms.CheckBox]::new()
        $showPasswordCheckBox.AutoSize = $true
        $showPasswordCheckBox.Location = [Drawing.Point]::new(142, 128)
        $showPasswordCheckBox.Text = 'Show password while I verify it'
        $showPasswordCheckBox.Add_CheckedChanged({
            $passwordBox.UseSystemPasswordChar = -not $showPasswordCheckBox.Checked
        })

        $okButton = [Windows.Forms.Button]::new()
        $okButton.Location = [Drawing.Point]::new(326, 174)
        $okButton.Size = [Drawing.Size]::new(75, 28)
        $okButton.Text = 'OK'
        $okButton.DialogResult = [Windows.Forms.DialogResult]::OK
        $okButton.Enabled = $false

        $cancelButton = [Windows.Forms.Button]::new()
        $cancelButton.Location = [Drawing.Point]::new(407, 174)
        $cancelButton.Size = [Drawing.Size]::new(75, 28)
        $cancelButton.Text = 'Cancel'
        $cancelButton.DialogResult = [Windows.Forms.DialogResult]::Cancel

        $passwordBox.Add_TextChanged({
            $okButton.Enabled = $passwordBox.TextLength -gt 0
        })

        $form.AcceptButton = $okButton
        $form.CancelButton = $cancelButton
        $form.Controls.AddRange(@(
            $instructionLabel,
            $accountLabel,
            $accountBox,
            $passwordLabel,
            $passwordBox,
            $showPasswordCheckBox,
            $okButton,
            $cancelButton
        ))
        $form.Add_Shown({ $passwordBox.Focus() })

        $dialogResult = $form.ShowDialog()
        if ($dialogResult -ne [Windows.Forms.DialogResult]::OK) {
            throw [OperationCanceledException]::new(
                'TechBench Sync Service installation was canceled before a password was supplied.')
        }

        $securePassword = ConvertTo-SecureString -String $passwordBox.Text -AsPlainText -Force
        $passwordBox.Clear()
        return [PSCredential]::new($AccountName, $securePassword)
    } finally {
        # Clearing the control keeps the typed value out of the remaining UI object lifetime.
        # Windows Forms still necessarily owns the visible text while Show Password is selected.
        $passwordBox.Clear()
        $form.Dispose()
    }
}

function Read-ServiceAccountCredential {
    param([Parameter(Mandatory = $true)][string]$AccountName)

    try {
        return Show-ServiceAccountCredentialDialog -AccountName $AccountName
    } catch [OperationCanceledException] {
        throw
    } catch {
        Write-Verbose (
            'The revealable Windows credential dialog is unavailable; ' +
            'falling back to the standard protected credential prompt. ' +
            $_.Exception.Message)
    }

    try {
        $fallbackCredential = Get-Credential -UserName $AccountName `
            -Message 'Enter the password for the TechBench Windows service account.'
    } catch {
        throw "This PowerShell host cannot prompt for the service-account password. Supply a PSCredential with -Credential. $($_.Exception.Message)"
    }

    if ($null -eq $fallbackCredential) {
        throw 'No service-account password was supplied. Retry interactively or supply a PSCredential with -Credential.'
    }

    return $fallbackCredential
}

function Assert-TrustedSecretFileAcl {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][Security.Principal.SecurityIdentifier]$ServiceSid,
        [switch]$RequireServiceReadOnly
    )

    [void](Assert-NoReparsePointInPath `
        -Path $Path -TrustedRoot $programDataRootPath -AllowLeaf)
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

    $allowedSidValues = @('S-1-5-18', 'S-1-5-32-544', $ServiceSid.Value)
    $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($allowedSidValues -notcontains $owner) {
        throw "Refusing unsafe existing credential file '$Path': it must be owned by SYSTEM, Administrators, or the configured service identity. Remove it and reprovision the credential."
    }
    $unsafeRule = $acl.GetAccessRules(
        $true, $true, [Security.Principal.SecurityIdentifier]) | Where-Object {
            $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
            $allowedSidValues -notcontains $_.IdentityReference.Value
        } | Select-Object -First 1
    if ($null -ne $unsafeRule) {
        throw "Refusing unsafe existing credential file '$Path': '$($unsafeRule.IdentityReference.Value)' has access. Remove it and reprovision the credential."
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
                $_.IdentityReference.Value -eq $ServiceSid.Value -and
                ($_.FileSystemRights -band $writeMask) -ne 0
            } | Select-Object -First 1
        if ($null -ne $serviceWriteRule) {
            throw "The normalized credential file still grants write access to the service identity: $Path"
        }
    }
}

function Set-SecretDirectoryAcl {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][Security.Principal.SecurityIdentifier]$ServiceSid
    )

    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    $administrators = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $security.SetOwner($administrators)
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
            Rights = [Security.AccessControl.FileSystemRights]'ReadAndExecute, Synchronize'
        }
    )
    foreach ($entry in $accessEntries) {
        $rule = [Security.AccessControl.FileSystemAccessRule]::new(
            $entry.Sid, $entry.Rights, $inheritance, $propagation, $allow)
        [void]$security.AddAccessRule($rule)
    }

    $privilegedWriteSids = @('S-1-5-18', 'S-1-5-32-544')
    $legacyAllowedWriteSids = @($privilegedWriteSids + $ServiceSid.Value)
    [void](Assert-NoReparsePointInPath -Path $Path -TrustedRoot $programDataRootPath)
    if (Test-Path -LiteralPath $Path -PathType Container) {
        [void](Assert-TrustedDirectoryAcl -Path $Path `
            -AllowedWriteSidValues $legacyAllowedWriteSids `
            -Description 'TechBench service data directory')
        Assert-NoReparsePointsInDirectoryTree -Path $Path
    } else {
        [void](New-ProtectedDirectoryAtomically `
            -Path $Path `
            -ParentPath $programDataAnchorPath `
            -Security $security `
            -AllowedWriteSidValues $privilegedWriteSids `
            -Description 'TechBench service data directory')
    }
    $allowedSecretNames = @('whd.secret', 'sage.secret')
    foreach ($entry in @(Get-ChildItem -LiteralPath $Path -Force)) {
        if ($allowedSecretNames -notcontains $entry.Name) {
            throw "The TechBench service data directory contains an unexpected entry: $($entry.FullName). Remove it after verifying its origin, then retry."
        }
        Assert-TrustedSecretFileAcl -Path $entry.FullName -ServiceSid $ServiceSid
    }

    # The directory and its exact, allowlisted contents were proved trusted
    # before this normalization, so Set-Acl never privileges attacker data.
    Set-Acl -LiteralPath $Path -AclObject $security
    [void](Assert-NoReparsePointInPath -Path $Path -TrustedRoot $programDataRootPath)
    Assert-NoReparsePointsInDirectoryTree -Path $Path
    [void](Assert-TrustedDirectoryAcl -Path $Path `
        -AllowedWriteSidValues $privilegedWriteSids `
        -Description 'TechBench service data directory')

    foreach ($secretName in @('whd.secret', 'sage.secret')) {
        $secretPath = Join-Path $Path $secretName
        [void](Assert-NoReparsePointInPath `
            -Path $secretPath -TrustedRoot $programDataRootPath -AllowLeaf)
        if (-not (Test-Path -LiteralPath $secretPath)) { continue }
        Assert-TrustedSecretFileAcl -Path $secretPath -ServiceSid $ServiceSid
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
        [void](Assert-NoReparsePointInPath `
            -Path $secretPath -TrustedRoot $programDataRootPath -AllowLeaf)
        Assert-TrustedSecretFileAcl -Path $secretPath -ServiceSid $ServiceSid `
            -RequireServiceReadOnly
    }
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

function Assert-RegularManagerCompanionFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    [void](Assert-NoReparsePointInPath `
        -Path $Path -TrustedRoot $programFilesRootPath -AllowLeaf)
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

function Install-ServerManagerShortcut {
    param(
        [Parameter(Mandatory = $true)][string]$InstalledDirectory,
        [Parameter(Mandatory = $true)][string]$ManagerDirectory,
        [Parameter(Mandatory = $true)][string]$ServiceName,
        [Parameter(Mandatory = $true)][string]$DataDirectory
    )

    [void](Assert-NoReparsePointInPath `
        -Path $InstalledDirectory -TrustedRoot $programFilesRootPath)
    [void](Assert-NoReparsePointInPath `
        -Path $ManagerDirectory -TrustedRoot $programFilesRootPath)
    $managerPayloadDirectory = Join-Path $InstalledDirectory 'server-manager'
    foreach ($fileName in @(
        'TechBench.ServerManager.exe',
        'TechBench.ServerManager.runtimeconfig.json',
        'TechBench.ServerManager.deps.json'
    )) {
        $sourcePath = Join-Path $managerPayloadDirectory $fileName
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "The package is missing compiled Server Manager file: $fileName"
        }
        Assert-RegularManagerCompanionFile -Path $sourcePath
    }
    $managerPayloadFiles = @(Get-ChildItem -LiteralPath $managerPayloadDirectory -Recurse -File)

    $managerDirectoryExisted = Test-Path -LiteralPath $ManagerDirectory -PathType Container
    if ($managerDirectoryExisted) {
        [void](Assert-TrustedDirectoryAcl -Path $ManagerDirectory `
            -AllowedWriteSidValues @('S-1-5-18', 'S-1-5-32-544') `
            -Description 'existing Server Manager install directory')
        Assert-NoReparsePointsInDirectoryTree -Path $ManagerDirectory
    } else {
        New-Item -ItemType Directory -Path $ManagerDirectory | Out-Null
    }
    [void](Assert-NoReparsePointInPath `
        -Path $ManagerDirectory -TrustedRoot $programFilesRootPath)
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
    Set-Acl -LiteralPath $ManagerDirectory -AclObject $managerSecurity
    [void](Assert-NoReparsePointInPath `
        -Path $ManagerDirectory -TrustedRoot $programFilesRootPath)
    [void](Assert-TrustedDirectoryAcl -Path $ManagerDirectory `
        -AllowedWriteSidValues @('S-1-5-18', 'S-1-5-32-544') `
        -Description 'Server Manager install directory')
    foreach ($sourceFile in $managerPayloadFiles) {
        $relativePath = $sourceFile.FullName.Substring($managerPayloadDirectory.Length).TrimStart('\')
        $destinationFile = Join-Path $ManagerDirectory $relativePath
        $destinationParent = Split-Path -Parent $destinationFile
        New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
        [void](Assert-NoReparsePointInPath `
            -Path $destinationFile -TrustedRoot $programFilesRootPath -AllowLeaf)
        if (Test-Path -LiteralPath $destinationFile) {
            Assert-RegularManagerCompanionFile -Path $destinationFile
        }
        Copy-Item -LiteralPath $sourceFile.FullName -Destination $destinationFile -Force
        Set-ManagerCompanionFileAcl -Path $destinationFile
    }
    foreach ($legacyFile in @(
        'TechBench-ServerManager.ps1',
        'Start-TechBenchServerManager.ps1',
        'Start-TechBenchServerManager.vbs',
        'csri-techbench-icon.ico'
    )) {
        $legacyPath = Join-Path $ManagerDirectory $legacyFile
        if (Test-Path -LiteralPath $legacyPath -PathType Leaf) {
            Remove-Item -LiteralPath $legacyPath -Force
        }
    }

    $shortcutDirectory = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\CSRI'
    [void](Assert-NoReparsePointInPath `
        -Path $shortcutDirectory -TrustedRoot $programDataRootPath)
    New-Item -ItemType Directory -Path $shortcutDirectory -Force | Out-Null
    [void](Assert-NoReparsePointInPath `
        -Path $shortcutDirectory -TrustedRoot $programDataRootPath)
    $shortcutPath = Join-Path $shortcutDirectory 'TechBench Server Manager.lnk'
    [void](Assert-NoReparsePointInPath `
        -Path $shortcutPath -TrustedRoot $programDataRootPath -AllowLeaf)
    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = Join-Path $ManagerDirectory 'TechBench.ServerManager.exe'
        $shortcut.Arguments = ''
        $shortcut.WorkingDirectory = $ManagerDirectory
        $shortcut.IconLocation = '{0},0' -f `
            (Join-Path $ManagerDirectory 'TechBench.ServerManager.exe')
        $shortcut.Description = 'Manage and update the TechBench Sync Service'
        $shortcut.Save()
        [void](Assert-NoReparsePointInPath `
            -Path $shortcutPath -TrustedRoot $programDataRootPath -AllowLeaf)
    } finally {
        if ($null -ne $shortcut) {
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) | Out-Null
        }
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
    }
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

function Get-SafePackageFilePath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    $normalizedRelativePath = $RelativePath.Replace('/', '\').TrimEnd('\')
    if ([string]::IsNullOrWhiteSpace($normalizedRelativePath) -or
        [IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath.IndexOf([char]0) -ge 0 -or
        $RelativePath.Contains(':')) {
        throw "The service package manifest contains an unsafe path: $RelativePath"
    }

    foreach ($component in $normalizedRelativePath.Split('\')) {
        if ([string]::IsNullOrWhiteSpace($component) -or
            $component -eq '.' -or $component -eq '..' -or
            $component.EndsWith(' ', [StringComparison]::Ordinal) -or
            $component.EndsWith('.', [StringComparison]::Ordinal) -or
            [IO.Path]::GetFileNameWithoutExtension($component) -match
                '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') {
            throw "The service package manifest contains an unsafe Windows path: $RelativePath"
        }
    }

    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $fullPath = [IO.Path]::GetFullPath((Join-Path $Root $normalizedRelativePath))
    if (-not $fullPath.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The service package manifest points outside its package directory: $RelativePath"
    }
    return $fullPath
}

function Get-PortableExecutableMachine {
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = [IO.File]::Open(
        $Path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) {
            throw "The package executable is not a valid Windows PE file: $Path"
        }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0x40 -or $peOffset -gt ($stream.Length - 6)) {
            throw "The package executable has an invalid PE header offset: $Path"
        }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw "The package executable has an invalid PE signature: $Path"
        }
        return $reader.ReadUInt16()
    } finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Assert-ServicePackageManifest {
    param([Parameter(Mandatory = $true)][string]$PackageDirectory)

    $packagePath = [IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\')
    if ((Get-Item -LiteralPath $packagePath -Force).Attributes -band
        [IO.FileAttributes]::ReparsePoint) {
        throw "Refusing to install from a reparse-point package directory: $packagePath"
    }
    $packageReparsePoint = Get-ChildItem -LiteralPath $packagePath -Recurse -Force |
        Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } |
        Select-Object -First 1
    if ($null -ne $packageReparsePoint) {
        throw "The service package contains a reparse point: $($packageReparsePoint.FullName)"
    }

    $manifestPath = Join-Path $packagePath 'package-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw 'The service package does not contain package-manifest.json. Install only from the complete verified release ZIP.'
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.Product -cne 'TechBench Sync Service' -or
        [int]$manifest.PackageFormatVersion -ne 1 -or
        $manifest.Runtime -cne 'win-x64' -or
        $manifest.SageOdbcWorkerRuntime -cne 'win-x86' -or
        $manifest.SelfContained -isnot [bool] -or
        -not [bool]$manifest.SelfContained -or
        [string]::IsNullOrWhiteSpace([string]$manifest.Version) -or
        [int]$manifest.RequiredDatabaseSchemaVersion -lt 1) {
        throw 'The service package manifest does not identify a supported TechBench Sync Service release.'
    }

    $seenPaths = New-Object 'Collections.Generic.HashSet[string]' `
        ([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in @($manifest.Files)) {
        $relativePath = [string]$file.Path
        if ($relativePath.Equals('package-manifest.json', [StringComparison]::OrdinalIgnoreCase) -or
            -not $seenPaths.Add($relativePath)) {
            throw "The service package manifest contains an invalid or duplicate path: $relativePath"
        }
        $fullPath = Get-SafePackageFilePath -Root $packagePath -RelativePath $relativePath
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "The service package is missing a manifest file: $relativePath"
        }
        $item = Get-Item -LiteralPath $fullPath
        if ($item.Length -ne [int64]$file.Length -or
            [string]$file.Sha256 -notmatch '^[0-9A-Fa-f]{64}$' -or
            -not (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.Equals(
                [string]$file.Sha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw "The service package failed manifest verification: $relativePath"
        }
    }

    $unexpectedFile = Get-ChildItem -LiteralPath $packagePath -Recurse -File |
        Where-Object {
            -not $_.FullName.Equals($manifestPath, [StringComparison]::OrdinalIgnoreCase)
        } |
        Where-Object {
            $relativePath = $_.FullName.Substring($packagePath.Length).TrimStart('\')
            -not $seenPaths.Contains($relativePath)
        } |
        Select-Object -First 1
    if ($null -ne $unexpectedFile) {
        throw "The service package contains a file not covered by its manifest: $($unexpectedFile.FullName)"
    }

    foreach ($requiredFile in @(
        'TechBench.SyncService.exe',
        'TechBench.SyncService.runtimeconfig.json',
        'TechBench.SyncService.deps.json',
        'appsettings.json',
        'Install-TechBenchSyncService.ps1',
        'Install-TechBenchServerManager.ps1',
        'Set-TechBenchSyncCredential.ps1',
        'Set-TechBenchSageSyncCredential.ps1',
        'TechBench-ServerManager.ps1',
        'Start-TechBenchServerManager.ps1',
        'Start-TechBenchServerManager.vbs',
        'server-manager\TechBench.ServerManager.exe',
        'server-manager\TechBench.ServerManager.runtimeconfig.json',
        'server-manager\TechBench.ServerManager.deps.json',
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

    $expectedVersion = [string]$manifest.Version
    $serviceExecutable = Join-Path $packagePath 'TechBench.SyncService.exe'
    $workerExecutable = Join-Path $packagePath 'sage-odbc-worker\TechBench.SageOdbcWorker.exe'
    $managerExecutable = Join-Path $packagePath 'server-manager\TechBench.ServerManager.exe'
    foreach ($executable in @($serviceExecutable, $workerExecutable, $managerExecutable)) {
        $productVersion = (Get-Item -LiteralPath $executable).VersionInfo.ProductVersion
        if ([string]::IsNullOrWhiteSpace($productVersion) -or
            $productVersion.Split('+', 2)[0] -cne $expectedVersion) {
            throw "The package executable version does not match release ${expectedVersion}: $executable"
        }
    }
    if ((Get-PortableExecutableMachine -Path $serviceExecutable) -ne 0x8664) {
        throw 'The TechBench Sync Service executable is not x64.'
    }
    if ((Get-PortableExecutableMachine -Path $workerExecutable) -ne 0x014C) {
        throw 'The Sage ODBC worker executable is not x86.'
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

    # The source release was verified before it entered the Administrator-only
    # stage. Revalidate the installed copy after replacing its one intentionally
    # mutable, machine-local configuration file and recording that exact hash.
    [void](Assert-ServicePackageManifest -PackageDirectory $PackageDirectory)
}

function Set-AdministratorOnlyDirectoryAcl {
    param([Parameter(Mandatory = $true)][string]$Path)

    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    $administrators = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $security.SetOwner($administrators)
    $inheritance = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $propagation = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow
    foreach ($sidValue in @('S-1-5-18', 'S-1-5-32-544')) {
        $sid = [Security.Principal.SecurityIdentifier]::new($sidValue)
        [void]$security.AddAccessRule(
            [Security.AccessControl.FileSystemAccessRule]::new(
                $sid,
                [Security.AccessControl.FileSystemRights]::FullControl,
                $inheritance,
                $propagation,
                $allow))
    }
    $allowedWriteSids = @('S-1-5-18', 'S-1-5-32-544')
    [void](Assert-NoReparsePointInPath -Path $Path -TrustedRoot $programDataRootPath)
    if (Test-Path -LiteralPath $Path -PathType Container) {
        [void](Assert-TrustedDirectoryAcl -Path $Path `
            -AllowedWriteSidValues $allowedWriteSids `
            -Description 'Server Manager data directory')
    } else {
        [void](New-ProtectedDirectoryAtomically `
            -Path $Path `
            -ParentPath $programDataAnchorPath `
            -Security $security `
            -AllowedWriteSidValues $allowedWriteSids `
            -Description 'Server Manager data directory')
    }
    Assert-NoReparsePointsInDirectoryTree -Path $Path
}

function New-VerifiedAdministratorInstallStage {
    param([Parameter(Mandatory = $true)][string]$PackageDirectory)

    $sourceManifest = Assert-ServicePackageManifest -PackageDirectory $PackageDirectory
    $sourceManifestPath = Join-Path $PackageDirectory 'package-manifest.json'
    $sourceManifestHash = (Get-FileHash -LiteralPath $sourceManifestPath -Algorithm SHA256).Hash
    [void](Initialize-ProtectedProgramDataAnchor)
    $managerDataRoot = $managerDataRootPath
    if (-not $managerDataRoot.StartsWith($allowedDataRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The protected installer staging directory must remain under '$allowedDataRoot'."
    }
    [void](Assert-NoReparsePointInPath `
        -Path $managerDataRoot -TrustedRoot $programDataRootPath)
    Set-AdministratorOnlyDirectoryAcl -Path $managerDataRoot
    [void](Assert-NoReparsePointInPath `
        -Path $managerDataRoot -TrustedRoot $programDataRootPath)
    Assert-NoReparsePointsInDirectoryTree -Path $managerDataRoot
    $stagePath = Join-Path $managerDataRoot `
        ("Install-{0}" -f [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stagePath | Out-Null
    [void](Assert-NoReparsePointInPath `
        -Path $stagePath -TrustedRoot $programDataRootPath)
    try {
        foreach ($file in @($sourceManifest.Files)) {
            $relativePath = [string]$file.Path
            $sourceFile = Get-SafePackageFilePath `
                -Root $PackageDirectory -RelativePath $relativePath
            $destinationFile = Get-SafePackageFilePath `
                -Root $stagePath -RelativePath $relativePath
            $destinationDirectory = Split-Path -Parent $destinationFile
            New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
            Copy-Item -LiteralPath $sourceFile -Destination $destinationFile -Force
        }
        $stagedManifestPath = Join-Path $stagePath 'package-manifest.json'
        Copy-Item -LiteralPath $sourceManifestPath -Destination $stagedManifestPath -Force
        if (-not (Get-FileHash -LiteralPath $stagedManifestPath -Algorithm SHA256).Hash.Equals(
                $sourceManifestHash, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The package manifest changed while it was copied into protected staging.'
        }
        [void](Assert-ServicePackageManifest -PackageDirectory $stagePath)
        return $stagePath
    } catch {
        if (Test-Path -LiteralPath $stagePath) {
            Remove-Item -LiteralPath $stagePath -Recurse -Force -ErrorAction SilentlyContinue
        }
        throw
    }
}

function Remove-AdministratorInstallStage {
    param([Parameter(Mandatory = $true)][string]$Path)

    $allowedRoot = [IO.Path]::GetFullPath(
        (Join-Path $env:ProgramData 'CSRI\TechBench Server Manager')).TrimEnd('\') + '\'
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Path]::GetFileName($fullPath).StartsWith(
            'Install-', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove an unverified installer staging directory: $fullPath"
    }
    [void](Assert-NoReparsePointInPath `
        -Path $fullPath -TrustedRoot $programDataRootPath)
    Assert-NoReparsePointsInDirectoryTree -Path $fullPath
    if ((Get-Item -LiteralPath $fullPath -Force).Attributes -band
        [IO.FileAttributes]::ReparsePoint) {
        throw "Refusing to remove a reparse-point installer staging directory: $fullPath"
    }
    $nestedReparsePoint = Get-ChildItem -LiteralPath $fullPath -Recurse -Force |
        Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } |
        Select-Object -First 1
    if ($null -ne $nestedReparsePoint) {
        throw "Refusing to remove installer staging that contains a reparse point: $($nestedReparsePoint.FullName)"
    }
    Remove-Item -LiteralPath $fullPath -Recurse -Force
}

if (-not (Test-Path -LiteralPath $sourceExecutable)) {
    throw "The published service executable was not found: $sourceExecutable"
}

if ($isManagedServiceAccount -and $null -ne $Credential) {
    throw 'Do not supply -Credential for a gMSA. Windows retrieves its managed password automatically.'
}

if (-not $isManagedServiceAccount -and $null -ne $Credential) {
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
    $administratorInstallStage = $null
    try {
        [void](Initialize-ProtectedProgramDataAnchor `
            -ServiceSidValue $serviceSid.Value)
        Set-SecretDirectoryAcl -Path $dataPath -ServiceSid $serviceSid
        $administratorInstallStage = New-VerifiedAdministratorInstallStage `
            -PackageDirectory $sourcePath
        $sourcePath = $administratorInstallStage
        $sourceExecutable = Join-Path $sourcePath 'TechBench.SyncService.exe'

        if (-not $isManagedServiceAccount -and $null -eq $Credential) {
            $Credential = Read-ServiceAccountCredential -AccountName $ServiceAccount
            if (-not $Credential.UserName.Equals($ServiceAccount, [StringComparison]::OrdinalIgnoreCase)) {
                throw "The credential user '$($Credential.UserName)' does not match -ServiceAccount '$ServiceAccount'."
            }
        }

        Stop-AndDeleteExistingService $ServiceName

    [void](Assert-NoReparsePointInPath `
        -Path $installPath -TrustedRoot $programFilesRootPath)
    New-Item -ItemType Directory -Path $installPath -Force | Out-Null
    [void](Assert-NoReparsePointInPath `
        -Path $installPath -TrustedRoot $programFilesRootPath)
    $preservedConfiguration = $null
    $installedConfiguration = Join-Path $installPath 'appsettings.json'
    if (-not $ReplaceConfiguration -and (Test-Path -LiteralPath $installedConfiguration)) {
        $preservedConfiguration = Get-Content -LiteralPath $installedConfiguration -Raw
    }

    if (-not $sourcePath.Equals($installPath, [StringComparison]::OrdinalIgnoreCase)) {
        [void](Assert-NoReparsePointInPath `
            -Path $installPath -TrustedRoot $programFilesRootPath)
        Get-ChildItem -LiteralPath $sourcePath | Where-Object {
            $_.Name -notmatch '(?i)\.sha256$'
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
    Update-InstalledPackageManifestConfigurationEntry -PackageDirectory $installPath

    try {
        [void](Assert-NoReparsePointInPath `
            -Path $installPath -TrustedRoot $programFilesRootPath)
        [void](Assert-NoReparsePointInPath `
            -Path $managerPath -TrustedRoot $programFilesRootPath)
        Install-ServerManagerShortcut -InstalledDirectory $installPath `
            -ManagerDirectory $managerPath -ServiceName $ServiceName -DataDirectory $dataPath
    } catch {
        Write-Warning "The service was installed, but the compiled Server Manager or its direct Start Menu shortcut could not be installed. Run Install-TechBenchServerManager.ps1 from the verified extracted package. $($_.Exception.Message)"
    }

    [void](Assert-NoReparsePointInPath `
        -Path $installPath -TrustedRoot $programFilesRootPath)
    Add-ServiceReadAcl -Path $installPath -ServiceSid $serviceSid
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
    } finally {
        $Credential = $null
        if ($null -ne $administratorInstallStage -and
            (Test-Path -LiteralPath $administratorInstallStage)) {
            Remove-AdministratorInstallStage -Path $administratorInstallStage
        }
    }
}
