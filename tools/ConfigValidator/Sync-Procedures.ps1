<#
.SYNOPSIS
    Synchronizes procedures.json based on a master text file list of Stored Procedures.

.DESCRIPTION
    Reads a line-separated text file (.txt) of Stored Procedure names.
    Queries the actual SQL database to ensure they exist.
    Updates procedures.json by:
      1. Adding missing stored procedures with auto-inferred and default values.
      2. Removing stored procedures not listed in the text file.
      3. Updating matching stored procedures with the exact Parameters and Threshold from the database.

.EXAMPLE
    .\Sync-Procedures.ps1 -InputFile "splist.txt"
#>

[CmdletBinding()]
param(
    [string]$InputFile,
    
    [string]$SettingsFile
)

# ─── Constants ────────────────────────────────────────────────────────────────
$ScriptDir        = Split-Path -Parent $MyInvocation.MyCommand.Definition
$DefaultSettings  = Join-Path $ScriptDir "validatorsettings.json"
$DefaultInput     = Join-Path $ScriptDir "splist.txt"

if ([string]::IsNullOrWhiteSpace($InputFile)) {
    $InputFile = $DefaultInput
}

# Resolve relative paths gracefully
if (-not [System.IO.Path]::IsPathRooted($InputFile)) {
    # If the user is running it relative to their current directory but the path doesn't exist, try local script dir
    if (-not (Test-Path $InputFile)) {
        $attempt = Join-Path $ScriptDir $InputFile
        if (Test-Path $attempt) {
            $InputFile = $attempt
        }
    }
}

if (-not (Test-Path $InputFile)) {
    Write-Host "  [ERROR] Input text file not found: $InputFile" -ForegroundColor Red
    Write-Host "          Please create a 'splist.txt' file or provide the correct path." -ForegroundColor Gray
    exit 1
}

# ─── Load JSON Settings File ──────────────────────────────────────────────────
if ([string]::IsNullOrWhiteSpace($SettingsFile) -and (Test-Path $DefaultSettings)) {
    $SettingsFile = $DefaultSettings
}

if (-not [string]::IsNullOrWhiteSpace($SettingsFile) -and (Test-Path $SettingsFile)) {
    $jsonSettings = Get-Content $SettingsFile -Raw | ConvertFrom-Json
    
    $Server      = $jsonSettings.ServerName
    $Database    = $jsonSettings.DatabaseName
    $WindowsAuth = ($jsonSettings.Authentication -eq "Windows")
    
    # Path Resolution
    $ConfigPath = [Environment]::ExpandEnvironmentVariables($jsonSettings.ProcedurePath)
    if (-not [System.IO.Path]::IsPathRooted($ConfigPath)) {
        $ConfigPath = Join-Path (Split-Path -Parent $SettingsFile) $ConfigPath
    }
    
    $SettingsSqlUser = $jsonSettings.SqlUser
    $SettingsSqlPass = $jsonSettings.SqlPassword
} else {
    Write-Host "  [ERROR] Configuration settings file ($DefaultSettings) not found." -ForegroundColor Red
    Write-Host "          We need connection details to validate against SQL Server." -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $ConfigPath)) {
    Write-Host "  [ERROR] Target procedures.json not found at: $ConfigPath" -ForegroundColor Red
    exit 1
}

# ─── Connect to Database ──────────────────────────────────────────────────────
if ($WindowsAuth) {
    $connString = "Server=$Server;Database=$Database;Integrated Security=True;TrustServerCertificate=True;"
} else {
    $connString = "Server=$Server;Database=$Database;User Id=$SettingsSqlUser;Password=$SettingsSqlPass;TrustServerCertificate=True;"
}

function Invoke-SqlQuery {
    param([string]$ConnectionString, [string]$Query)
    $connection = New-Object System.Data.SqlClient.SqlConnection($ConnectionString)
    $command    = New-Object System.Data.SqlClient.SqlCommand($Query, $connection)
    $command.CommandTimeout = 30
    $adapter    = New-Object System.Data.SqlClient.SqlDataAdapter($command)
    $dataset    = New-Object System.Data.DataSet
    try {
        $connection.Open()
        [void]$adapter.Fill($dataset)
        return ,$dataset.Tables[0]
    } catch {
        throw "SQL Error: $_"
    } finally {
        $connection.Close()
        $connection.Dispose()
    }
}

function Get-ThresholdFromSource {
    param([string]$SpSource)
    if ($SpSource -match '(?i)if\s+@cnt\s*>\s*(\d+)') { return [long]$Matches[1] }
    return $null
}

Write-Host ""
Write-Host "  ═════════════════════════════════════════════════════════════" -ForegroundColor Magenta
Write-Host "             procedures.json Database Auto-Sync                " -ForegroundColor Magenta
Write-Host "  ═════════════════════════════════════════════════════════════" -ForegroundColor Magenta
Write-Host ""

# ─── Read Text File Master List ───────────────────────────────────────────────
Write-Host "  Reading input list list..." -ForegroundColor Gray
$rawList = Get-Content $InputFile | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_.Trim() }
# Remove duplicates (case insensitive)
$masterSpList = @($rawList | Sort-Object -Unique)

if ($masterSpList.Count -eq 0) {
    Write-Host "  [ERROR] Input file is empty." -ForegroundColor Red
    exit 1
}
Write-Host "  Loaded $($masterSpList.Count) Stored Procedures from text file." -ForegroundColor Cyan


# ─── Fetch DB Metadata ────────────────────────────────────────────────────────
Write-Host "  Validating stored procedures in SQL Server... " -NoNewline

$spMetadataQuery = @"
SELECT
    o.name                       AS SpName,
    p.name                       AS ParamName
FROM sys.objects o
LEFT JOIN sys.parameters p ON p.object_id = o.object_id
WHERE o.type = 'P'
  AND o.name IN ($(($masterSpList | ForEach-Object { "'" + $_.Replace("'","''") + "'" }) -join ','))
ORDER BY o.name, p.parameter_id
"@

$spSourceQuery = @"
SELECT
    o.name       AS SpName,
    m.definition AS SpSource
FROM sys.objects o
JOIN sys.sql_modules m ON m.object_id = o.object_id
WHERE o.type = 'P'
  AND o.name IN ($(($masterSpList | ForEach-Object { "'" + $_.Replace("'","''") + "'" }) -join ','))
"@

try {
    $spMetadata = Invoke-SqlQuery -ConnectionString $connString -Query $spMetadataQuery
    $spSources  = Invoke-SqlQuery -ConnectionString $connString -Query $spSourceQuery
    Write-Host "OK" -ForegroundColor Green
} catch {
    Write-Host "FAILED" -ForegroundColor Red
    Write-Host "  Error: $_" -ForegroundColor Red
    exit 1
}

$dbSpParams = @{}
$dbSpSource = @{}

if ($spMetadata -and $spMetadata.Rows.Count -gt 0) {
    foreach ($row in $spMetadata.Rows) {
        $spName = $row.SpName
        if (-not $dbSpParams.ContainsKey($spName)) {
            $dbSpParams[$spName] = [System.Collections.ArrayList]::new()
        }
        if ($row.ParamName -and $row.ParamName -ne [DBNull]::Value) {
            [void]$dbSpParams[$spName].Add($row.ParamName.TrimStart('@'))
        }
    }
}

if ($spSources -and $spSources.Rows.Count -gt 0) {
    foreach ($row in $spSources.Rows) {
        $dbSpSource[$row.SpName] = $row.SpSource
    }
}

# ─── Execute Sync Logic ───────────────────────────────────────────────────────
$config = Get-Content $ConfigPath -Raw | ConvertFrom-Json
$currentProcedures = if ($null -ne $config.StoredProcedures) { $config.StoredProcedures } else { @() }
$newProcedures = [System.Collections.ArrayList]::new()

$addedCount   = 0
$retainedCount = 0
$updatedCount = 0
$missingInDb  = 0
$unmodified   = 0

Write-Host ""
Write-Host "  ─────────────────────────────────────────────────────────────" -ForegroundColor Gray
Write-Host "  Synchronization Changes" -ForegroundColor Magenta
Write-Host "  ─────────────────────────────────────────────────────────────" -ForegroundColor Gray
Write-Host ""

# 1. Process Master List (Add or Update)
foreach ($reqSp in $masterSpList) {
    # Check if exists in DB first
    # If using case-insensitive lookup
    $dbKey = $dbSpParams.Keys | Where-Object { $_ -eq $reqSp } | Select-Object -First 1
    
    if (-not $dbKey) {
        Write-Host "  [ERROR] Database validation failed: '$reqSp' does NOT exist in SQL Server." -ForegroundColor Red
        Write-Host "          Skipping injection for this procedure." -ForegroundColor Red
        $missingInDb++
        continue
    }

    # Match in DB ensures we use correct casing for DB metadata
    $exactSpName = $dbKey
    $paramsArray = @($dbSpParams[$exactSpName])
    $threshold   = if ($dbSpSource.ContainsKey($exactSpName)) { Get-ThresholdFromSource $dbSpSource[$exactSpName] } else { $null }

    # Check if already in procedures.json
    $existing = $currentProcedures | Where-Object { $_.Name -eq $exactSpName -or $_.Name -eq $reqSp } | Select-Object -First 1
    
    if ($null -eq $existing) {
        # --- ADD NEW SP ---
        $isExport = $exactSpName -match '(?i)_EXP_'
        $isImport = $exactSpName -match '(?i)_IMP_'
        $dataType = if ($isExport) { "Export" } elseif ($isImport) { "Import" } else { "Unknown" }
        
        $displayName = $exactSpName -replace "_", " "
        $filePrefix  = if ($isExport) { $exactSpName -replace '(?i).*_EXP_', 'Export_' }
                       elseif ($isImport) { $exactSpName -replace '(?i).*_IMP_', 'Import_' }
                       else { $exactSpName }

        $newObj = [PSCustomObject]@{
            Name         = $exactSpName
            DisplayName  = $displayName
            DataType     = $dataType
            StagingTable = "TODO_FILL_THIS_IN"   # Action required later
            Parameters   = $paramsArray
            FilePrefix   = $filePrefix
        }
        if ($null -ne $threshold) {
            $newObj | Add-Member -MemberType NoteProperty -Name "Threshold" -Value $threshold
        }

        [void]$newProcedures.Add($newObj)
        $addedCount++
        Write-Host "  [ADDED] '$exactSpName' injected into configuration." -ForegroundColor Green
    } 
    else {
        # --- UPDATE EXISTING SP ---
        $needsUpdate = $false
        
        # Check Param array diff
        $existingParams = @($existing.Parameters)
        $diffParams     = Compare-Object $existingParams $paramsArray -SyncWindow 0
        if ($diffParams) {
            $existing.Parameters = $paramsArray
            $needsUpdate = $true
        }

        # Check Threshold
        $existingThreshold = if ($null -ne $existing.PSObject.Properties['Threshold']) { $existing.Threshold } else { $null }
        if ($threshold -ne $existingThreshold) {
            if ($null -ne $threshold) {
                if ($null -ne $existing.PSObject.Properties['Threshold']) {
                    $existing.Threshold = $threshold
                } else {
                    $existing | Add-Member -MemberType NoteProperty -Name "Threshold" -Value $threshold
                }
            } else {
                if ($null -ne $existing.PSObject.Properties['Threshold']) {
                    $existing.Threshold = 0
                }
            }
            $needsUpdate = $true
        }

        if ($needsUpdate) {
            $updatedCount++
            Write-Host "  [UPDATED] '$exactSpName' metadata refreshed from database." -ForegroundColor Yellow
        } else {
            $unmodified++
        }
        
        [void]$newProcedures.Add($existing)
    }
}

# 2. Retain existing SPs that are NOT in the text file
foreach ($oldSp in $currentProcedures) {
    if ($oldSp.Name -notin $masterSpList) {
        [void]$newProcedures.Add($oldSp)
        $retainedCount++
        # Write-Host "  [RETAINED] '$($oldSp.Name)' kept in configuration." -ForegroundColor DarkGray
    }
}

# ─── Save Results ─────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "  ─────────────────────────────────────────────────────────────" -ForegroundColor Gray
Write-Host "  Summary" -ForegroundColor Magenta
Write-Host "  ─────────────────────────────────────────────────────────────" -ForegroundColor Gray
Write-Host "  Text File Count : $($masterSpList.Count)"
Write-Host "  Added           : $addedCount" -ForegroundColor Green
Write-Host "  Retained        : $retainedCount" -ForegroundColor DarkGray
Write-Host "  Updated         : $updatedCount" -ForegroundColor Yellow
Write-Host "  Unmodified      : $unmodified"
if ($missingInDb -gt 0) {
Write-Host "  Missing in DB   : $missingInDb (Ignored)" -ForegroundColor Red
}
Write-Host ""

if ($addedCount -gt 0 -or $updatedCount -gt 0) {
    $config.StoredProcedures = $newProcedures
    $jsonOutput = $config | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText($ConfigPath, $jsonOutput, [System.Text.Encoding]::UTF8)
    Write-Host "  ✓ procedures.json successfully updated!" -ForegroundColor Green
    
    if ($addedCount -gt 0) {
        Write-Host "  ⚠ WARNING: $addedCount new procedure(s) were added." -ForegroundColor Yellow
        Write-Host "             Their 'StagingTable' value is currently set to 'TODO_FILL_THIS_IN'." -ForegroundColor Yellow
        Write-Host "             Please open procedures.json manually to correct them!" -ForegroundColor Yellow
    }
} else {
    Write-Host "  ✓ procedures.json is already perfectly synced with the master list." -ForegroundColor Green
}
Write-Host ""

exit 0
