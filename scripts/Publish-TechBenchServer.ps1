#Requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [ValidateNotNullOrEmpty()]
    [string]$Configuration = 'Release',

    [ValidateNotNullOrEmpty()]
    [string]$RepositoryUrl = 'https://github.com/Serlynth/TechBenchV2-Releases',

    [ValidateRange(1, 2147483647)]
    [int]$RequiredDatabaseSchemaVersion = 11,

    [switch]$Publish,

    [switch]$SkipTests,

    [switch]$AllowDirty
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repoPrefix = $repoRoot.TrimEnd('\') + '\'
$projectPath = Join-Path $repoRoot 'TechBench.SyncService\TechBench.SyncService.csproj'
$sageWorkerProjectPath = Join-Path $repoRoot 'TechBench.SageOdbcWorker\TechBench.SageOdbcWorker.csproj'
$managerProjectPath = Join-Path $repoRoot 'TechBench.ServerManager\TechBench.ServerManager.csproj'
$setupProjectPath = Join-Path $repoRoot 'TechBench.ServerSetup\TechBench.ServerSetup.csproj'
$solutionPath = Join-Path $repoRoot 'TechBenchV2.sln'
$publishDirectory = Join-Path $repoRoot 'artifacts\server\win-x64'
$sageWorkerPublishDirectory = Join-Path $publishDirectory 'sage-odbc-worker'
$managerPublishDirectory = Join-Path $publishDirectory 'server-manager'
$setupPublishDirectory = Join-Path $repoRoot 'artifacts\server-setup\win-x64'
$distDirectory = Join-Path $repoRoot 'dist'
$packageName = "TechBenchSyncService-$Version-win-x64"
$packagePath = Join-Path $distDirectory "$packageName.zip"
$checksumPath = "$packagePath.sha256"
$sqlAssetName = "TechBenchV2-SQLServer2016-$Version.sql"
$sqlAssetPath = Join-Path $distDirectory $sqlAssetName
$sqlChecksumPath = "$sqlAssetPath.sha256"
$setupPath = Join-Path $distDirectory 'TechBenchServerSetup.exe'
$setupChecksumPath = "$setupPath.sha256"
$numericVersion = ($Version -split '-', 2)[0]

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

function Resolve-GitHubRepositorySlug {
    param([Parameter(Mandatory = $true)][string]$Url)

    try {
        $uri = [Uri]$Url
    } catch {
        throw "The V2 release repository is not a valid URL: $Url"
    }

    $slug = $uri.AbsolutePath.Trim('/')
    if ($uri.Scheme -ne 'https' `
        -or -not $uri.Host.Equals('github.com', [StringComparison]::OrdinalIgnoreCase) `
        -or -not $uri.IsDefaultPort `
        -or -not [string]::IsNullOrEmpty($uri.Query) `
        -or -not [string]::IsNullOrEmpty($uri.Fragment) `
        -or $slug -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
        throw 'The V2 release repository must be an HTTPS GitHub repository URL in https://github.com/owner/repository form.'
    }

    return $slug
}

function Reset-RepositoryDirectory {
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

function Assert-SafeServicePayload {
    param([Parameter(Mandatory = $true)][string]$Path)

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
        'database\Deploy-CSRI-Standalone.sql',
        'database\README-Deploy.md'
    )) {
        if (-not (Test-Path -LiteralPath (Join-Path $Path $requiredFile))) {
            throw "The service package is missing required file: $requiredFile"
        }
    }

    $prohibitedFiles = @(Get-ChildItem -LiteralPath $Path -Recurse -File | Where-Object {
        $_.Name -match '(?i)(?:^|\.)(?:secret|credential|password|token)(?:\.|$)' -or
        $_.Extension -match '(?i)^\.(?:pfx|p12|snk|key|db|sqlite|sqlite3)$' -or
        $_.Name -match '(?i)^\.env(?:\.|$)'
    })
    if ($prohibitedFiles.Count -gt 0) {
        throw "The service payload contains a prohibited credential or data file: $($prohibitedFiles.Name -join ', ')"
    }

    $settingsPath = Join-Path $Path 'appsettings.json'
    $settingsText = Get-Content -LiteralPath $settingsPath -Raw
    try {
        $settings = $settingsText | ConvertFrom-Json
    } catch {
        throw "Published appsettings.json is not valid JSON: $($_.Exception.Message)"
    }

    if ($null -eq $settings.TechBenchSync) {
        throw 'Published appsettings.json does not contain the TechBenchSync section.'
    }

    $unsafeSetting = @($settings.TechBenchSync.PSObject.Properties | Where-Object {
        $_.Name -match '(?i)(?:password|credential|api.?key|token|secret)(?!path)' -and
        -not [string]::IsNullOrWhiteSpace([string]$_.Value)
    })
    if ($unsafeSetting.Count -gt 0) {
        throw "Published appsettings.json contains a secret-bearing setting: $($unsafeSetting.Name -join ', ')"
    }

    if ($settingsText -match '(?i)Data Source\s*=.*(?:Password|Pwd)\s*=') {
        throw 'Published appsettings.json contains a SQL password. The service must use Windows integrated authentication.'
    }

    $workerExecutable = Join-Path $Path 'sage-odbc-worker\TechBench.SageOdbcWorker.exe'
    $workerBytes = [IO.File]::ReadAllBytes($workerExecutable)
    if ($workerBytes.Length -lt 64) {
        throw "The Sage ODBC worker executable is too small to contain a valid PE header: $workerExecutable"
    }

    $peOffset = [BitConverter]::ToInt32($workerBytes, 0x3c)
    if ($peOffset -lt 0 -or $peOffset + 6 -gt $workerBytes.Length) {
        throw "The Sage ODBC worker executable has an invalid PE header: $workerExecutable"
    }

    $machine = [BitConverter]::ToUInt16($workerBytes, $peOffset + 4)
    if ($machine -ne 0x014c) {
        throw ("The Sage ODBC worker is not x86 (PE machine 0x{0:X4}): {1}" -f $machine, $workerExecutable)
    }
}

function Assert-StandaloneSqlIsCurrent {
    $trackedPath = Join-Path $repoRoot 'database\sqlserver2016\Deploy-CSRI-Standalone.sql'
    $builderPath = Join-Path $repoRoot 'scripts\Build-StandaloneSqlDeployment.ps1'
    $temporaryPath = Join-Path ([IO.Path]::GetTempPath()) `
        ("TechBench-Deploy-{0}.sql" -f [Guid]::NewGuid().ToString('N'))

    try {
        & $builderPath -OutputPath $temporaryPath | Out-Null
        $trackedHash = (Get-FileHash -LiteralPath $trackedPath -Algorithm SHA256).Hash
        $generatedHash = (Get-FileHash -LiteralPath $temporaryPath -Algorithm SHA256).Hash
        if ($trackedHash -ne $generatedHash) {
            throw 'Deploy-CSRI-Standalone.sql is stale. Run Build-StandaloneSqlDeployment.ps1 and review the regenerated deployment before packaging.'
        }
    } finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

Push-Location $repoRoot
try {
    $repositorySlug = Resolve-GitHubRepositorySlug $RepositoryUrl

    if (-not $AllowDirty) {
        $dirtyFiles = @(git status --porcelain)
        if ($LASTEXITCODE -ne 0) {
            throw 'Unable to inspect the Git working tree.'
        }

        if ($dirtyFiles.Count -gt 0) {
            throw 'Commit or stash source changes before packaging, or use -AllowDirty for a local package test.'
        }
    }

    if (-not (Test-Path -LiteralPath $projectPath)) {
        throw "The sync-service project was not found: $projectPath"
    }
    if (-not (Test-Path -LiteralPath $sageWorkerProjectPath)) {
        throw "The Sage ODBC worker project was not found: $sageWorkerProjectPath"
    }
    if (-not (Test-Path -LiteralPath $managerProjectPath)) {
        throw "The compiled Server Manager project was not found: $managerProjectPath"
    }
    if (-not (Test-Path -LiteralPath $setupProjectPath)) {
        throw "The native Server Setup project was not found: $setupProjectPath"
    }

    Assert-StandaloneSqlIsCurrent

    if (-not $SkipTests) {
        Invoke-Checked $dotnet @(
            'test', $solutionPath,
            '-c', $Configuration,
            '--nologo',
            '-m:1',
            '-p:TechBenchTestBuild=true',
            '-p:PlatformTarget=x64')
    }

    Reset-RepositoryDirectory $publishDirectory
    New-Item -ItemType Directory -Path $distDirectory -Force | Out-Null

    foreach ($existingOutput in @(
        $packagePath,
        $checksumPath,
        $sqlAssetPath,
        $sqlChecksumPath,
        $setupPath,
        $setupChecksumPath
    )) {
        if (Test-Path -LiteralPath $existingOutput) {
            Remove-Item -LiteralPath $existingOutput -Force
        }
    }

    Invoke-Checked $dotnet @(
        'publish', $projectPath,
        '-c', $Configuration,
        '-r', 'win-x64',
        '--self-contained', 'true',
        '-o', $publishDirectory,
        '-p:PlatformTarget=x64',
        '-p:PublishSingleFile=false',
        '-p:PublishTrimmed=false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        "-p:Version=$Version",
        "-p:AssemblyVersion=$numericVersion.0",
        "-p:FileVersion=$numericVersion.0"
    )

    New-Item -ItemType Directory -Path $sageWorkerPublishDirectory -Force | Out-Null
    Invoke-Checked $dotnet @(
        'publish', $sageWorkerProjectPath,
        '-c', $Configuration,
        '-r', 'win-x86',
        '--self-contained', 'true',
        '-o', $sageWorkerPublishDirectory,
        '-p:PlatformTarget=x86',
        '-p:PublishSingleFile=false',
        '-p:PublishTrimmed=false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        "-p:Version=$Version",
        "-p:AssemblyVersion=$numericVersion.0",
        "-p:FileVersion=$numericVersion.0"
    )

    New-Item -ItemType Directory -Path $managerPublishDirectory -Force | Out-Null
    Invoke-Checked $dotnet @(
        'publish', $managerProjectPath,
        '-c', $Configuration,
        '-r', 'win-x64',
        '--self-contained', 'true',
        '-o', $managerPublishDirectory,
        '-p:PlatformTarget=x64',
        '-p:PublishSingleFile=false',
        '-p:PublishTrimmed=false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        "-p:Version=$Version",
        "-p:AssemblyVersion=$numericVersion.0",
        "-p:FileVersion=$numericVersion.0"
    )

    foreach ($scriptName in @(
        'Install-TechBenchSyncService.ps1',
        'Install-TechBenchServerManager.ps1',
        'Set-TechBenchSyncCredential.ps1',
        'Set-TechBenchSageSyncCredential.ps1',
        'TechBench-ServerManager.ps1',
        'Start-TechBenchServerManager.ps1',
        'Start-TechBenchServerManager.vbs',
        'Uninstall-TechBenchSyncService.ps1'
    )) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $scriptName) `
            -Destination (Join-Path $publishDirectory $scriptName) -Force
    }

    Copy-Item -LiteralPath (Join-Path $repoRoot 'Assets\csri-techbench-icon.ico') `
        -Destination (Join-Path $publishDirectory 'csri-techbench-icon.ico') -Force

    Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\WHD-SYNC-SERVICE.md') `
        -Destination (Join-Path $publishDirectory 'README-WHD-SYNC-SERVICE.md') -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot "release-notes\$Version.md") `
        -Destination (Join-Path $publishDirectory 'RELEASE-NOTES.md') -Force

    $databaseDirectory = Join-Path $publishDirectory 'database'
    New-Item -ItemType Directory -Path $databaseDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'database\sqlserver2016\Deploy-CSRI-Standalone.sql') `
        -Destination (Join-Path $databaseDirectory 'Deploy-CSRI-Standalone.sql') -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'database\sqlserver2016\README-Deploy.md') `
        -Destination (Join-Path $databaseDirectory 'README-Deploy.md') -Force

    Assert-SafeServicePayload $publishDirectory

    $payloadFiles = @(Get-ChildItem -LiteralPath $publishDirectory -Recurse -File |
        Sort-Object FullName)
    $manifestFiles = @($payloadFiles | ForEach-Object {
        [ordered]@{
            Path = $_.FullName.Substring($publishDirectory.Length).TrimStart('\')
            Length = $_.Length
            Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
    $manifest = [ordered]@{
        Product = 'TechBench Sync Service'
        PackageFormatVersion = 1
        Version = $Version
        Runtime = 'win-x64'
        SageOdbcWorkerRuntime = 'win-x86'
        SelfContained = $true
        RequiredDatabaseSchemaVersion = $RequiredDatabaseSchemaVersion
        CreatedUtc = [DateTime]::UtcNow.ToString('o')
        Files = $manifestFiles
    }
    $manifest | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $publishDirectory 'package-manifest.json') -Encoding UTF8

    Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $packagePath -CompressionLevel Optimal
    $packageHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
    "$packageHash  $([IO.Path]::GetFileName($packagePath))" |
        Set-Content -LiteralPath $checksumPath -Encoding ASCII

    Copy-Item -LiteralPath (Join-Path $repoRoot 'database\sqlserver2016\Deploy-CSRI-Standalone.sql') `
        -Destination $sqlAssetPath -Force
    $sqlAssetHash = (Get-FileHash -LiteralPath $sqlAssetPath -Algorithm SHA256).Hash
    "$sqlAssetHash  $([IO.Path]::GetFileName($sqlAssetPath))" |
        Set-Content -LiteralPath $sqlChecksumPath -Encoding ASCII

    Reset-RepositoryDirectory $setupPublishDirectory
    Invoke-Checked $dotnet @(
        'publish', $setupProjectPath,
        '-c', $Configuration,
        '-r', 'win-x64',
        '--self-contained', 'true',
        '-o', $setupPublishDirectory,
        '-p:PlatformTarget=x64',
        '-p:PublishSingleFile=true',
        '-p:EnableCompressionInSingleFile=true',
        '-p:PublishTrimmed=false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        "-p:TechBenchEmbeddedPayload=$packagePath",
        "-p:Version=$Version",
        "-p:AssemblyVersion=$numericVersion.0",
        "-p:FileVersion=$numericVersion.0"
    )
    $publishedSetup = Join-Path $setupPublishDirectory 'TechBench.ServerSetup.exe'
    if (-not (Test-Path -LiteralPath $publishedSetup)) {
        throw "The native server installer was not published: $publishedSetup"
    }
    $publishedSetupInfo = Get-Item -LiteralPath $publishedSetup
    $packageInfo = Get-Item -LiteralPath $packagePath
    if ($publishedSetupInfo.Length -le $packageInfo.Length) {
        throw 'The native server installer is not large enough to contain the verified embedded service package.'
    }
    $publishedSetupVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($publishedSetup).ProductVersion
    if ([string]::IsNullOrWhiteSpace($publishedSetupVersion) -or
        -not $publishedSetupVersion.Split('+', 2)[0].Equals($Version, [StringComparison]::Ordinal)) {
        throw "The native server installer version does not match package ${Version}: $publishedSetupVersion"
    }
    Copy-Item -LiteralPath $publishedSetup -Destination $setupPath -Force
    $setupHash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash
    "$setupHash  $([IO.Path]::GetFileName($setupPath))" |
        Set-Content -LiteralPath $setupChecksumPath -Encoding ASCII

    if ($Publish) {
        $releaseJson = & gh release view "v$Version" `
            --repo $repositorySlug `
            --json tagName,isDraft,assets
        if ($LASTEXITCODE -ne 0) {
            throw "GitHub release v$Version could not be read from $RepositoryUrl. Confirm gh authentication and publish the matching client release first with Publish-TechBenchRelease.ps1 -Publish."
        }

        try {
            $release = (($releaseJson -join '') | ConvertFrom-Json)
        } catch {
            throw "GitHub returned an invalid response for release v${Version}: $($_.Exception.Message)"
        }

        if ($release.tagName -ne "v$Version" -or $release.isDraft) {
            throw "GitHub release v$Version must be the already-published, non-draft client release before server assets can be attached."
        }

        $assetPaths = @(
            $packagePath,
            $checksumPath,
            $sqlAssetPath,
            $sqlChecksumPath,
            $setupPath,
            $setupChecksumPath
        )
        $assetNames = @($assetPaths | ForEach-Object { [IO.Path]::GetFileName($_) })
        $existingNames = @($release.assets | ForEach-Object { $_.name })
        $conflicts = @($assetNames | Where-Object { $existingNames -contains $_ })
        if ($conflicts.Count -gt 0) {
            throw "Release v$Version already contains immutable server asset(s): $($conflicts -join ', '). Choose a new version instead of overwriting a published asset."
        }

        Invoke-Checked 'gh' (@(
            'release', 'upload', "v$Version"
        ) + $assetPaths + @(
            '--repo', $repositorySlug
        ))
        Write-Host "Published server and SQL assets to $RepositoryUrl/releases/tag/v$Version"
    }

    Write-Host "Created service package: $packagePath"
    Write-Host "Created SHA-256 sidecar: $checksumPath"
    Write-Host "Created standalone SQL asset: $sqlAssetPath"
    Write-Host "Created SQL SHA-256 sidecar: $sqlChecksumPath"
    Write-Host "Created one-click server installer: $setupPath"
    Write-Host "Created installer SHA-256 sidecar: $setupChecksumPath"
} finally {
    Pop-Location
}
