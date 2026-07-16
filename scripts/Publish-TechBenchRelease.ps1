#Requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [string]$ReleaseNotesPath,

    [string]$RepositoryUrl = 'https://github.com/Serlynth/TechBenchV2-Releases',

    [switch]$Publish,

    [switch]$AllowDirty
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
    Invoke-Checked $dotnet @('test', $testProjectPath, '-c', 'Release')

    Reset-WorkspaceDirectory $publishDirectory
    Reset-WorkspaceDirectory $releaseDirectory
    New-Item -ItemType Directory -Path $distDirectory -Force | Out-Null

    $repositoryUri = [Uri]$RepositoryUrl
    if ($repositoryUri.Scheme -ne 'https' -or $repositoryUri.Host -ne 'github.com') {
        throw 'The V2 release repository must be an HTTPS GitHub repository URL.'
    }

    $existingReleases = @()
    if ($Publish) {
        $repositorySlug = $repositoryUri.AbsolutePath.Trim('/')
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

    $hasExistingRelease = $existingReleases.Count -gt 0
    if ($Publish -and $existingReleases.tagName -contains "v$Version") {
        throw "GitHub release v$Version already exists. Versions are immutable; choose a new version."
    }

    if ($Publish -and $hasExistingRelease) {
        Invoke-Checked $dotnet @(
            'tool', 'run', 'vpk', '--',
            'download', 'github',
            '--outputDir', $releaseDirectory,
            '--channel', 'win',
            '--repoUrl', $RepositoryUrl
        )
    }

    Invoke-Checked $dotnet @(
        'publish', $projectPath,
        '-c', 'Release',
        '-r', 'win-x86',
        '--self-contained', 'true',
        '-o', $publishDirectory,
        '-p:PublishSingleFile=false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        "-p:Version=$Version",
        "-p:AssemblyVersion=$numericVersion.0",
        "-p:FileVersion=$numericVersion.0"
    )

    $packArguments = @(
        'tool', 'run', 'vpk', '--',
        'pack',
        '--outputDir', $releaseDirectory,
        '--channel', 'win',
        '--runtime', 'win-x86',
        '--packId', 'CSRI.TechBenchV2',
        '--packVersion', $Version,
        '--packDir', $publishDirectory,
        '--packAuthors', 'CSRI',
        '--packTitle', 'TechBench V2',
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

    $distSetupPath = Join-Path $distDirectory 'TechBenchV2Setup.exe'
    Copy-Item -LiteralPath $setup.FullName -Destination $distSetupPath -Force

    if ($Publish) {
        $token = (gh auth token).Trim()
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($token)) {
            throw 'GitHub CLI is not authenticated.'
        }

        Invoke-Checked $dotnet @(
            'tool', 'run', 'vpk', '--',
            'upload', 'github',
            '--outputDir', $releaseDirectory,
            '--channel', 'win',
            '--repoUrl', $RepositoryUrl,
            '--token', $token,
            '--publish',
            '--pre', $isPrerelease.ToString().ToLowerInvariant(),
            '--tag', "v$Version",
            '--releaseName', "TechBench V2 $Version"
        )

        Write-Host "Published TechBench V2 $Version to $RepositoryUrl/releases/tag/v$Version"
    } else {
        Write-Host "Built TechBench V2 $Version locally. Configure a V2 repository before using -Publish."
    }

    Write-Host "Installer: $distSetupPath"
}
finally {
    Pop-Location
}
