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
    '21-V0002-OperationalSchema.sql'
    '22-V0003-SharedReferenceData.sql'
    '23-V0004-AdminOwnedSharedConfig.sql'
    '24-V0005-TechBenchV1ImportSchema.sql'
    '25-V0006-WhdServerSyncSchema.sql'
    '26-V0007-ServerOwnedSageAndAdminPreviewSchema.sql'
    '27-V0008-FireDrillCredentialsSchema.sql'
    '28-V0009-WhdMissingNoteRecovery.sql'
    '29-V0010-ClientPresenceSchema.sql'
    '30-V0011-ClientResponsesSchema.sql'
    '31-V0012-FlexibleCredentialFieldsSchema.sql'
    '32-V0013-WhdClientContactDetailsSchema.sql'
    '33-V0014-EquipmentBoardSchema.sql'
    '34-V0015-EquipmentAnyDeskSchema.sql'
    '35-V0015-EquipmentDeploymentLifecycle.sql'
    '36-V0015-ClientInfoBetaSchema.sql'
    '37-V0015-AuthPointMfaSchema.sql'
    '38-V0015-ClientAttachmentsSchema.sql'
    '30-Security.sql'
    '40-StoredProcedures.sql'
    '41-V0002-WorkProcedures.sql'
    '42-V0002-SharedProcedures.sql'
    '43-V0002-PostingProcedures.sql'
    '44-V0002-SyncImportProcedures.sql'
    '45-V0003-SharedReferenceProcedures.sql'
    '46-V0004-AdminSharedProcedures.sql'
    '47-V0005-TechBenchV1ImportProcedures.sql'
    '48-V0006-WhdServerSyncProcedures.sql'
    '49-V0007-ServerOwnedSageAndAdminPreviewProcedures.sql'
    '50-V0008-FireDrillCredentialsProcedures.sql'
    '51-V0010-ClientPresenceProcedures.sql'
    '52-V0011-ClientResponsesProcedures.sql'
    '53-V0012-FlexibleCredentialFieldsProcedures.sql'
    '54-V0014-EquipmentBoardProcedures.sql'
    '61-V0015-ClientInfoBetaProcedures.sql'
    '62-V0015-ClientInfoBetaImportProcedures.sql'
    '64-V0015-AuthPointMfaProcedures.sql'
    '66-V0015-ClientAttachmentsProcedures.sql'
    '68-V0015-WhdLocalDeleteProcedures.sql'
    '50-Grants.sql'
    '51-V0002-OperationalGrants.sql'
    '52-V0004-AdminSharedGrants.sql'
    '53-V0005-TechBenchV1ImportGrants.sql'
    '54-V0006-WhdServerSyncGrants.sql'
    '55-V0007-ServerOwnedSageAndAdminPreviewGrants.sql'
    '56-V0008-FireDrillCredentialsGrants.sql'
    '57-V0010-ClientPresenceGrants.sql'
    '58-V0011-ClientResponsesGrants.sql'
    '59-V0012-FlexibleCredentialFieldsGrants.sql'
    '60-V0014-EquipmentBoardGrants.sql'
    '63-V0015-ClientInfoBetaGrants.sql'
    '65-V0015-AuthPointMfaGrants.sql'
    '67-V0015-ClientAttachmentsGrants.sql'
    '90-Verify.sql'
    '91-V0002-OperationalVerify.sql'
    '92-V0003-SharedReferenceVerify.sql'
    '93-V0004-AdminSharedVerify.sql'
    '94-V0005-TechBenchV1ImportVerify.sql'
    '95-V0006-WhdServerSyncVerify.sql'
    '96-V0007-ServerOwnedSageAndAdminPreviewVerify.sql'
    '97-V0008-FireDrillCredentialsVerify.sql'
    '98-V0009-WhdMissingNoteRecoveryVerify.sql'
    '99-V0010-ClientPresenceVerify.sql'
    '100-V0011-ClientResponsesVerify.sql'
    '101-V0012-FlexibleCredentialFieldsVerify.sql'
    '102-V0013-WhdClientContactDetailsVerify.sql'
    '103-V0014-EquipmentBoardVerify.sql'
    '104-V0015-EquipmentAnyDeskVerify.sql'
    '105-V0015-EquipmentDeploymentLifecycleVerify.sql'
    '106-V0015-ClientInfoBetaVerify.sql'
    '107-V0015-AuthPointMfaVerify.sql'
    '108-V0015-ClientAttachmentsVerify.sql'
    '109-V0015-WhdLocalDeleteVerify.sql'
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
:setvar SyncServicePrincipal "CSRI\TechBench_Sync"

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
