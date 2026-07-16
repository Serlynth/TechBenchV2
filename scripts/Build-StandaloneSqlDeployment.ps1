[CmdletBinding()]
param(
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sqlDirectory = Join-Path $repositoryRoot 'database\sqlserver2016'

if ([string]::IsNullOrWhiteSpace($OutputPath))
{
    $OutputPath = Join-Path $sqlDirectory 'Deploy-CSRI-Standalone.sql'
}

$orderedScripts = @(
    '00-Preflight.sql'
    '10-CreateDatabase.sql'
    '20-BaselineSchema.sql'
    '30-Security.sql'
    '40-StoredProcedures.sql'
    '50-Grants.sql'
    '90-Verify.sql'
)

$sections = [System.Collections.Generic.List[string]]::new()
$sections.Add(@'
/*
    Self-contained TechBench V2 database deployment for CSRI-SQL.

    Requirements:
      - Run in SQL Server Management Studio connected to CSRI-SQL.
      - Use an existing SQL Server sysadmin login.
      - Enable Query > SQLCMD Mode before execution.

    This file has no external file references and contains no password.
*/

:ON ERROR EXIT

:setvar DatabaseName "TechBench"
:setvar UserGroup "CSRI\TechBench_Users"
:setvar AdminGroup "CSRI\TechBench_Admins"

USE [master];
GO

IF UPPER(CONVERT(nvarchar(128), SERVERPROPERTY(N'MachineName'))) <> N'CSRI-SQL'
BEGIN
    ;THROW 51000, N'This deployment is restricted to CSRI-SQL.', 1;
END;
GO
'@.Trim())

foreach ($scriptName in $orderedScripts)
{
    $scriptPath = Join-Path $sqlDirectory $scriptName
    if (-not (Test-Path -LiteralPath $scriptPath))
    {
        throw "Required SQL deployment script was not found: $scriptPath"
    }

    $scriptText = Get-Content -LiteralPath $scriptPath -Raw
    $sections.Add(
        "-- ============================================================================`r`n" +
        "-- BEGIN $scriptName`r`n" +
        "-- ============================================================================`r`n`r`n" +
        $scriptText.Trim() +
        "`r`n`r`n" +
        "-- ============================================================================`r`n" +
        "-- END $scriptName`r`n" +
        "-- ============================================================================"
    )
}

$sections.Add(@'
PRINT N'TechBench deployment completed successfully on CSRI-SQL.';
GO
'@.Trim())

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory))
{
    [System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
}

$utf8WithBom = [System.Text.UTF8Encoding]::new($true)
$content = [string]::Join("`r`n`r`n", $sections) + "`r`n"
[System.IO.File]::WriteAllText($OutputPath, $content, $utf8WithBom)

Write-Output $OutputPath
