#Requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [string]$ReleaseNotesPath,

    [string]$RepositoryUrl = 'https://github.com/Serlynth/TechBenchV2-Releases',

    [ValidateSet('v2', 'inventory-beta', 'client-info-beta')]
    [string]$Channel = 'v2',

    [switch]$Publish,

    [switch]$AllowDirty,

    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repoPrefix = $repoRoot.TrimEnd('\') + '\'
$publishDirectory = Join-Path $repoRoot 'artifacts\publish\win-x86'
$releaseDirectory = Join-Path $repoRoot 'artifacts\releases'
$distDirectory = Join-Path $repoRoot 'dist'
$projectPath = Join-Path $repoRoot 'TechBench.csproj'
$testProjectPath = Join-Path $repoRoot 'TechBench.Tests\TechBench.Tests.csproj'
$iconPath = Join-Path $repoRoot 'Assets\csri-techbench-icon.ico'
$splashPath = Join-Path $repoRoot 'Assets\csri-techbench-logo.png'
$numericVersion = ($Version -split '-', 2)[0]
$isPrerelease = $Version.Contains('-')
$releaseChannel = $Channel
$isBetaChannel = $releaseChannel -in @('inventory-beta', 'client-info-beta')
$installerFileName = switch ($releaseChannel) {
    'inventory-beta' { 'TechBenchInventoryBetaSetup.exe' }
    'client-info-beta' { 'TechBenchClientInfoBetaSetup.exe' }
    default { 'TechBenchV2Setup.exe' }
}
$packId = 'CSRI.TechBenchV2'
$packTitle = 'TechBench V2'

if ([string]::IsNullOrWhiteSpace($ReleaseNotesPath)) {
    $ReleaseNotesPath = Join-Path $repoRoot "release-notes\$Version.md"
} elseif (-not [IO.Path]::IsPathRooted($ReleaseNotesPath)) {
    $ReleaseNotesPath = Join-Path $repoRoot $ReleaseNotesPath
}

$userDotNet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $userDotNet) {
    $userDotNet
} else {
    (Get-Command dotnet -ErrorAction Stop).Source
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $($LASTEXITCODE): $FilePath $($Arguments -join ' ')"
    }
}

function Reset-WorkspaceDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to reset a directory outside the repository: $fullPath"
    }

    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }

    New-Item -ItemType Directory -Path $fullPath -Force | Out-Null
}

function Assert-ServerBackedPublishOutput {
    param([Parameter(Mandatory = $true)][string]$Path)

    $prohibitedArtifacts = @(Get-ChildItem -LiteralPath $Path -Recurse -File | Where-Object {
        $_.Name -match '(?i)\.(?:db|sqlite|sqlite3)(?:-(?:wal|shm|journal))?$'
    })
    if ($prohibitedArtifacts.Count -gt 0) {
        $artifactNames = $prohibitedArtifacts |
            ForEach-Object { $_.FullName.Substring($Path.Length).TrimStart('\') }
        throw "Server-backed V2 publish contains a prohibited local database artifact: $($artifactNames -join ', ')"
    }

    $dependenciesPath = Join-Path $Path 'TechBenchV2.deps.json'
    if (-not (Test-Path -LiteralPath $dependenciesPath)) {
        throw "Published dependency manifest was not found: $dependenciesPath"
    }

    if (-not (Select-String -LiteralPath $dependenciesPath `
            -Pattern 'Microsoft\.Data\.Sqlite' `
            -Quiet)) {
        throw 'The read-only V1 database importer dependency is missing from the V2 publish.'
    }

    $whdAssemblyPath = Join-Path $Path 'TechBench.WHD.dll'
    if (-not (Test-Path -LiteralPath $whdAssemblyPath)) {
        throw 'The shared TechBench.WHD assembly is missing from the V2 publish.'
    }

    $whdAssemblyBytes = [IO.File]::ReadAllBytes($whdAssemblyPath)
    if ($whdAssemblyBytes.Length -lt 64) {
        throw 'The shared TechBench.WHD assembly is not a valid PE file.'
    }

    $whdPeOffset = [BitConverter]::ToInt32($whdAssemblyBytes, 0x3c)
    if ($whdPeOffset -lt 0 -or $whdPeOffset + 6 -gt $whdAssemblyBytes.Length) {
        throw 'The shared TechBench.WHD assembly has an invalid PE header.'
    }

    $whdMachine = [BitConverter]::ToUInt16($whdAssemblyBytes, $whdPeOffset + 4)
    if ($whdMachine -ne 0x014c) {
        throw ("The x86 client contains an incompatible TechBench.WHD assembly (PE machine 0x{0:X4})." `
            -f $whdMachine)
    }

    foreach ($requiredAssembly in @(
        'Microsoft.Data.Sqlite.dll',
        'SQLitePCLRaw.core.dll',
        'SQLitePCLRaw.provider.e_sqlite3.dll'
    )) {
        if (-not (Test-Path -LiteralPath (Join-Path $Path $requiredAssembly))) {
            throw "The read-only V1 database importer assembly is missing: $requiredAssembly"
        }
    }

    $nativeSqlite = @(Get-ChildItem -LiteralPath $Path -Recurse -File -Filter 'e_sqlite3.dll')
    if ($nativeSqlite.Count -eq 0) {
        throw 'The x86 native SQLite library required by the read-only V1 importer is missing.'
    }

    foreach ($nativeLibrary in $nativeSqlite) {
        $bytes = [IO.File]::ReadAllBytes($nativeLibrary.FullName)
        if ($bytes.Length -lt 64) {
            throw "The native SQLite library is not a valid PE file: $($nativeLibrary.FullName)"
        }

        $peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
        if ($peOffset -lt 0 -or $peOffset + 6 -gt $bytes.Length) {
            throw "The native SQLite library has an invalid PE header: $($nativeLibrary.FullName)"
        }

        $machine = [BitConverter]::ToUInt16($bytes, $peOffset + 4)
        if ($machine -ne 0x014c) {
            throw ("The V1 importer native SQLite library is not x86 (PE machine 0x{0:X4}): {1}" `
                -f $machine, $nativeLibrary.FullName)
        }
    }
}

Push-Location $repoRoot
try {
    if (-not $AllowDirty) {
        $dirtyFiles = @(git status --porcelain)
        if ($LASTEXITCODE -ne 0) {
            throw 'Unable to inspect the Git working tree.'
        }

        if ($dirtyFiles.Count -gt 0) {
            throw 'Commit or stash source changes before building a release, or use -AllowDirty for a local package test.'
        }
    }

    if (-not (Test-Path -LiteralPath $ReleaseNotesPath)) {
        throw "Release notes were not found: $ReleaseNotesPath"
    }

    Invoke-Checked $dotnet @('tool', 'restore')
    if (-not $SkipTests) {
        Invoke-Checked $dotnet @(
            'test', $testProjectPath,
            '-c', 'Release',
            '-m:1',
            '-p:TechBenchTestBuild=true',
            '-p:PlatformTarget=x64'
        )
    }

    Reset-WorkspaceDirectory $publishDirectory
    Reset-WorkspaceDirectory $releaseDirectory
    New-Item -ItemType Directory -Path $distDirectory -Force | Out-Null

    $repositoryUri = [Uri]$RepositoryUrl
    $repositorySlug = $repositoryUri.AbsolutePath.Trim('/')
    if ($repositoryUri.Scheme -ne 'https' `
        -or -not $repositoryUri.Host.Equals('github.com', [StringComparison]::OrdinalIgnoreCase) `
        -or -not $repositoryUri.IsDefaultPort `
        -or -not [string]::IsNullOrEmpty($repositoryUri.Query) `
        -or -not [string]::IsNullOrEmpty($repositoryUri.Fragment) `
        -or $repositorySlug -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
        throw 'The V2 release repository must be an HTTPS GitHub repository URL in https://github.com/owner/repository form.'
    }

    $existingReleases = @()
    if ($Publish) {
        $releaseListJson = gh release list --repo $repositorySlug --limit 100 --json tagName
        if ($LASTEXITCODE -ne 0) {
            throw 'Unable to read existing V2 GitHub releases.'
        }
        $releaseListText = ($releaseListJson -join '').Trim()
        $existingReleases = if ($releaseListText -eq '[]') {
            @()
        } else {
            @($releaseListText | ConvertFrom-Json)
        }
    }

    $hasExistingChannelRelease = @($existingReleases | Where-Object {
        if ($isBetaChannel) {
            $_.tagName -match '^v\d+\.\d+\.\d+-beta\.'
        } else {
            $_.tagName -match '^v0\.' -and $_.tagName -notmatch '-beta\.'
        }
    }).Count -gt 0
    if ($Publish -and $existingReleases.tagName -contains "v$Version") {
        throw "GitHub release v$Version already exists. Versions are immutable; choose a new version."
    }

    if ($Publish -and $hasExistingChannelRelease) {
        Invoke-Checked $dotnet @(
            'tool', 'run', 'vpk', '--',
            'download', 'github',
            '--outputDir', $releaseDirectory,
            '--channel', $releaseChannel,
            '--repoUrl', $RepositoryUrl,
            '--pre', $isBetaChannel.ToString().ToLowerInvariant()
        )
    }

    Invoke-Checked $dotnet @(
        'publish', $projectPath,
        '-c', 'Release',
        '-r', 'win-x86',
        '--self-contained', 'true',
        '-o', $publishDirectory,
        '-p:PlatformTarget=x86',
        '-p:PublishSingleFile=false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        "-p:TechBenchReleaseChannel=$releaseChannel",
        "-p:Version=$Version",
        "-p:AssemblyVersion=$numericVersion.0",
        "-p:FileVersion=$numericVersion.0"
    )

    Assert-ServerBackedPublishOutput $publishDirectory

    $packArguments = @(
        'tool', 'run', 'vpk', '--',
        'pack',
        '--outputDir', $releaseDirectory,
        '--channel', $releaseChannel,
        '--runtime', 'win-x86',
        '--packId', $packId,
        '--packVersion', $Version,
        '--packDir', $publishDirectory,
        '--packAuthors', 'CSRI',
        '--packTitle', $packTitle,
        '--releaseNotes', $ReleaseNotesPath,
        '--icon', $iconPath,
        '--mainExe', 'TechBenchV2.exe',
        '--splashImage', $splashPath,
        '--splashProgressColor', '#3B82F6',
        '--shortcuts', 'Desktop,StartMenuRoot',
        '--exclude', '.*\.(pdb|xml)$'
    )

    if (-not [string]::IsNullOrWhiteSpace($env:TECHBENCH_SIGN_PARAMS)) {
        $packArguments += @('--signParams', $env:TECHBENCH_SIGN_PARAMS)
    }

    Invoke-Checked $dotnet $packArguments

    $setup = Get-ChildItem -LiteralPath $releaseDirectory -Filter '*-Setup.exe' |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -eq $setup) {
        throw 'Velopack did not produce a Setup executable.'
    }

    $distSetupPath = Join-Path $distDirectory $installerFileName
    Copy-Item -LiteralPath $setup.FullName -Destination $distSetupPath -Force
    $distChecksumPath = "$distSetupPath.sha256"
    $setupHash = (Get-FileHash -LiteralPath $distSetupPath -Algorithm SHA256).Hash
    "$setupHash  $([IO.Path]::GetFileName($distSetupPath))" |
        Set-Content -LiteralPath $distChecksumPath -Encoding ASCII

    if ($Publish) {
        $token = (gh auth token).Trim()
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($token)) {
            throw 'GitHub CLI is not authenticated.'
        }

        Invoke-Checked $dotnet @(
            'tool', 'run', 'vpk', '--',
            'upload', 'github',
            '--outputDir', $releaseDirectory,
            '--channel', $releaseChannel,
            '--repoUrl', $RepositoryUrl,
            '--token', $token,
            '--publish',
            '--pre', $isPrerelease.ToString().ToLowerInvariant(),
            '--tag', "v$Version",
            '--releaseName', "TechBench V2 $Version"
        )

        $releaseAssetsJson = gh release view "v$Version" `
            --repo $repositorySlug `
            --json assets
        if ($LASTEXITCODE -ne 0) {
            throw "The client release was uploaded, but GitHub release v$Version could not be read to attach the stable installer links."
        }

        try {
            $releaseAssets = (($releaseAssetsJson -join '') | ConvertFrom-Json).assets
        } catch {
            throw "GitHub returned an invalid asset response for release v${Version}: $($_.Exception.Message)"
        }

        $existingAssetNames = @($releaseAssets | ForEach-Object { $_.name })
        $directInstallerAssets = @($distSetupPath, $distChecksumPath)
        $missingDirectInstallerAssets = @($directInstallerAssets | Where-Object {
            $existingAssetNames -notcontains [IO.Path]::GetFileName($_)
        })
        if ($missingDirectInstallerAssets.Count -gt 0) {
            Invoke-Checked 'gh' (@(
                'release', 'upload', "v$Version"
            ) + $missingDirectInstallerAssets + @(
                '--repo', $repositorySlug
            ))
        }

        Write-Host "Published TechBench V2 $Version to $RepositoryUrl/releases/tag/v$Version"
        Write-Host "Installer: $RepositoryUrl/releases/download/v$Version/$installerFileName"
        Write-Host "Installer checksum: $RepositoryUrl/releases/download/v$Version/$installerFileName.sha256"
    } else {
        Write-Host "Built TechBench V2 $Version locally. Configure a V2 repository before using -Publish."
    }

    Write-Host "Installer: $distSetupPath"
    Write-Host "Installer SHA-256: $distChecksumPath"
}
finally {
    Pop-Location
}
