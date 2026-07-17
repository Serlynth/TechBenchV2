#Requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [ValidateNotNullOrEmpty()]
    [string]$Configuration = 'Release',

    [switch]$SkipTests,

    [switch]$AllowDirty
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repoPrefix = $repoRoot.TrimEnd('\') + '\'
$projectPath = Join-Path $repoRoot 'TechBench.SyncService\TechBench.SyncService.csproj'
$solutionPath = Join-Path $repoRoot 'TechBenchV2.sln'
$publishDirectory = Join-Path $repoRoot 'artifacts\server\win-x64'
$distDirectory = Join-Path $repoRoot 'dist'
$packageName = "TechBenchSyncService-$Version-win-x64"
$packagePath = Join-Path $distDirectory "$packageName.zip"
$checksumPath = "$packagePath.sha256"
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
        'Set-TechBenchSyncCredential.ps1',
        'Uninstall-TechBenchSyncService.ps1',
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

    Assert-StandaloneSqlIsCurrent

    if (-not $SkipTests) {
        Invoke-Checked $dotnet @(
            'test', $solutionPath, '-c', $Configuration, '--nologo', '-m:1')
    }

    Reset-RepositoryDirectory $publishDirectory
    New-Item -ItemType Directory -Path $distDirectory -Force | Out-Null

    foreach ($existingOutput in @($packagePath, $checksumPath)) {
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
        'Set-TechBenchSyncCredential.ps1',
        'Uninstall-TechBenchSyncService.ps1'
    )) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $scriptName) `
            -Destination (Join-Path $publishDirectory $scriptName) -Force
    }

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
        Product = 'TechBench WHD Sync Service'
        Version = $Version
        Runtime = 'win-x64'
        SelfContained = $true
        CreatedUtc = [DateTime]::UtcNow.ToString('o')
        Files = $manifestFiles
    }
    $manifest | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $publishDirectory 'package-manifest.json') -Encoding UTF8

    Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $packagePath -CompressionLevel Optimal
    $packageHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
    "$packageHash  $([IO.Path]::GetFileName($packagePath))" |
        Set-Content -LiteralPath $checksumPath -Encoding ASCII

    Write-Host "Created service package: $packagePath"
    Write-Host "Created SHA-256 sidecar: $checksumPath"
} finally {
    Pop-Location
}
