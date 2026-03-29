<#
.SYNOPSIS
    Validates procedures.json against the actual SQL Server database.

.DESCRIPTION
    Checks each stored procedure defined in procedures.json for:
      1. Existence in the database (sys.objects)
      2. Parameter name alignment (sys.parameters)
      3. Threshold/count value alignment (parsed from SP source via sys.sql_modules)

    Generates a timestamped .txt validation report.

.EXAMPLE
    .\Validate-Procedures.ps1
    # Interactive mode — prompts for server, database, and auth type.

.EXAMPLE
    .\Validate-Procedures.ps1 -Server "MATRIX,1434" -Database "RAW_PROCESS_NEW" -WindowsAuth
    # Non-interactive mode with Windows Authentication.
#>

[CmdletBinding()]
param(
    [string]$Server,
    [string]$Database,
    [switch]$WindowsAuth,
    [string]$ConfigPath,
    [string]$SettingsFile
)

# ─── Constants ────────────────────────────────────────────────────────────────
$ScriptDir        = Split-Path -Parent $MyInvocation.MyCommand.Definition
$DefaultConfig    = Join-Path (Split-Path -Parent (Split-Path -Parent $ScriptDir)) "src\SEZ_AccesDB_Module\procedures.json"
$DefaultSettings  = Join-Path $ScriptDir "validatorsettings.json"
$ReportDir        = Join-Path $ScriptDir "Reports"

# ─── Load JSON Settings File (If Provided/Exists) ─────────────────────────────
if ([string]::IsNullOrWhiteSpace($SettingsFile) -and (Test-Path $DefaultSettings)) {
    $SettingsFile = $DefaultSettings
}

if (-not [string]::IsNullOrWhiteSpace($SettingsFile) -and (Test-Path $SettingsFile)) {
    Write-Host "  Loading settings from: $SettingsFile" -ForegroundColor DarkGray
    $jsonSettings = Get-Content $SettingsFile -Raw | ConvertFrom-Json
    
    if (-not $Server -and $null -ne $jsonSettings.ServerName) { $Server = $jsonSettings.ServerName }
    if (-not $Database -and $null -ne $jsonSettings.DatabaseName) { $Database = $jsonSettings.DatabaseName }
    if (-not $WindowsAuth.IsPresent -and $null -ne $jsonSettings.Authentication -and $jsonSettings.Authentication -eq "Windows") { $WindowsAuth = $true }
    
    if (-not $ConfigPath -and $null -ne $jsonSettings.ProcedurePath) { 
        $ConfigPath = [Environment]::ExpandEnvironmentVariables($jsonSettings.ProcedurePath)
        if (-not [System.IO.Path]::IsPathRooted($ConfigPath)) {
            $ConfigPath = Join-Path (Split-Path -Parent $SettingsFile) $ConfigPath
        }
    }
    
    if ($null -ne $jsonSettings.OutputPath) { 
        $ReportDir = [Environment]::ExpandEnvironmentVariables($jsonSettings.OutputPath)
        if (-not [System.IO.Path]::IsPathRooted($ReportDir)) {
            $ReportDir = Join-Path (Split-Path -Parent $SettingsFile) $ReportDir
        }
    }
    
    $SettingsSqlUser = $jsonSettings.SqlUser
    $SettingsSqlPass = $jsonSettings.SqlPassword
}


# ─── Helper: Execute SQL Query ────────────────────────────────────────────────
function Invoke-SqlQuery {
    param(
        [string]$ConnectionString,
        [string]$Query
    )
    $connection = New-Object System.Data.SqlClient.SqlConnection($ConnectionString)
    $command    = New-Object System.Data.SqlClient.SqlCommand($Query, $connection)
    $command.CommandTimeout = 30
    $adapter    = New-Object System.Data.SqlClient.SqlDataAdapter($command)
    $dataset    = New-Object System.Data.DataSet
    try {
        $connection.Open()
        [void]$adapter.Fill($dataset)
        return ,$dataset.Tables[0]
    }
    catch {
        throw "SQL Error: $_"
    }
    finally {
        $connection.Close()
        $connection.Dispose()
    }
}

# ─── Helper: Parse threshold from SP source code ──────────────────────────────
function Get-ThresholdFromSource {
    param([string]$SpSource)

    # Match patterns like: if @cnt>2100000 or IF @cnt > 1800000
    if ($SpSource -match '(?i)if\s+@cnt\s*>\s*(\d+)') {
        return [long]$Matches[1]
    }
    return $null
}

# ─── Helper: Format status with alignment ─────────────────────────────────────
function Format-Status {
    param([string]$Status)
    switch ($Status) {
        "VALID"    { return "[VALID]   " }
        "MISSING"  { return "[MISSING] " }
        "MISMATCH" { return "[MISMATCH]" }
        default    { return "[$Status]" + (" " * [Math]::Max(0, 10 - $Status.Length - 2)) }
    }
}

# ═══════════════════════════════════════════════════════════════════════════════
#  MAIN SCRIPT
# ═══════════════════════════════════════════════════════════════════════════════

Write-Host ""
Write-Host "  ═════════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "             procedures.json Database Validation Utility       " -ForegroundColor Cyan
Write-Host "  ═════════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

# ── 1. Collect Connection Parameters ──────────────────────────────────────────

if (-not $Server) {
    $Server = Read-Host "  Enter SQL Server name (e.g. MATRIX,1434)"
    if ([string]::IsNullOrWhiteSpace($Server)) {
        Write-Host "  [ERROR] Server name is required." -ForegroundColor Red
        exit 1
    }
}

if (-not $Database) {
    $Database = Read-Host "  Enter Database name (e.g. RAW_PROCESS_NEW)"
    if ([string]::IsNullOrWhiteSpace($Database)) {
        Write-Host "  [ERROR] Database name is required." -ForegroundColor Red
        exit 1
    }
}

if (-not $WindowsAuth) {
    if (-not [string]::IsNullOrWhiteSpace($SettingsSqlUser)) {
        # Use settings file SQL Auth
        $connString = "Server=$Server;Database=$Database;User Id=$SettingsSqlUser;Password=$SettingsSqlPass;TrustServerCertificate=True;"
    }
    else {
        Write-Host ""
        Write-Host "  Authentication Type:" -ForegroundColor Yellow
        Write-Host "    1. Windows Authentication (Integrated Security)"
        Write-Host "    2. SQL Server Authentication (User/Password)"
        $authChoice = Read-Host "  Select (1 or 2)"

        if ($authChoice -eq "2") {
            $sqlUser = Read-Host "  Enter SQL User"
            $sqlPass = Read-Host "  Enter SQL Password" -AsSecureString
            $bstr    = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sqlPass)
            $sqlPassPlain = [Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr)
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
            $connString = "Server=$Server;Database=$Database;User Id=$sqlUser;Password=$sqlPassPlain;TrustServerCertificate=True;"
        }
        else {
            $WindowsAuth = $true
        }
    }
}

if ($WindowsAuth) {
    $connString = "Server=$Server;Database=$Database;Integrated Security=True;TrustServerCertificate=True;"
}

# ── 2. Load procedures.json ───────────────────────────────────────────────────

if (-not $ConfigPath) { $ConfigPath = $DefaultConfig }

if (-not (Test-Path $ConfigPath)) {
    Write-Host "  [ERROR] Configuration file not found: $ConfigPath" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "  Configuration : $ConfigPath" -ForegroundColor Gray
Write-Host "  Server        : $Server" -ForegroundColor Gray
Write-Host "  Database      : $Database" -ForegroundColor Gray
Write-Host "  Auth          : $(if ($WindowsAuth) {'Windows'} else {'SQL Server'})" -ForegroundColor Gray
Write-Host ""

$config = Get-Content $ConfigPath -Raw | ConvertFrom-Json
$procedures = $config.StoredProcedures

if (-not $procedures -or $procedures.Count -eq 0) {
    Write-Host "  [ERROR] No StoredProcedures found in configuration." -ForegroundColor Red
    exit 1
}

Write-Host "  Found $($procedures.Count) stored procedure(s) in configuration." -ForegroundColor Cyan
Write-Host ""

# ── 3. Test Database Connection ───────────────────────────────────────────────

Write-Host "  Testing database connection..." -NoNewline
try {
    $testResult = Invoke-SqlQuery -ConnectionString $connString -Query "SELECT 1 AS Test"
    Write-Host " OK" -ForegroundColor Green
}
catch {
    Write-Host " FAILED" -ForegroundColor Red
    Write-Host "  Error: $_" -ForegroundColor Red
    exit 1
}

# ── 4. Fetch all SP metadata from database in batch ───────────────────────────

Write-Host "  Fetching stored procedure metadata from database..." -NoNewline

# Get all SPs and their parameters in one query
$spMetadataQuery = @"
SELECT
    o.name                       AS SpName,
    p.name                       AS ParamName
FROM sys.objects o
LEFT JOIN sys.parameters p ON p.object_id = o.object_id
WHERE o.type = 'P'
  AND o.name IN ($(($procedures | ForEach-Object { "'" + $_.Name.Replace("'","''") + "'" }) -join ','))
ORDER BY o.name, p.parameter_id
"@

# Get all SP source code for threshold parsing
$spSourceQuery = @"
SELECT
    o.name       AS SpName,
    m.definition AS SpSource
FROM sys.objects o
JOIN sys.sql_modules m ON m.object_id = o.object_id
WHERE o.type = 'P'
  AND o.name IN ($(($procedures | ForEach-Object { "'" + $_.Name.Replace("'","''") + "'" }) -join ','))
"@

try {
    $spMetadata = Invoke-SqlQuery -ConnectionString $connString -Query $spMetadataQuery
    $spSources  = Invoke-SqlQuery -ConnectionString $connString -Query $spSourceQuery
    Write-Host " OK" -ForegroundColor Green
}
catch {
    Write-Host " FAILED" -ForegroundColor Red
    Write-Host "  Error: $_" -ForegroundColor Red
    exit 1
}

# Build lookup dictionaries
$dbSpParams = @{}   # SpName -> list of param names (without @)
$dbSpSource = @{}   # SpName -> source code

if ($spMetadata -and $spMetadata.Rows.Count -gt 0) {
    foreach ($row in $spMetadata.Rows) {
        $spName = $row.SpName
        if (-not $dbSpParams.ContainsKey($spName)) {
            $dbSpParams[$spName] = [System.Collections.ArrayList]::new()
        }
        if ($row.ParamName -and $row.ParamName -ne [DBNull]::Value) {
            $paramClean = $row.ParamName.TrimStart('@')
            [void]$dbSpParams[$spName].Add($paramClean)
        }
    }
}

if ($spSources -and $spSources.Rows.Count -gt 0) {
    foreach ($row in $spSources.Rows) {
        $dbSpSource[$row.SpName] = $row.SpSource
    }
}

# ── 5. Validate Each Stored Procedure ─────────────────────────────────────────

Write-Host ""
Write-Host "  ─────────────────────────────────────────────────────────────" -ForegroundColor Gray
Write-Host "  Validation Results" -ForegroundColor Cyan
Write-Host "  ─────────────────────────────────────────────────────────────" -ForegroundColor Gray
Write-Host ""

$results       = @()
$validCount    = 0
$missingCount  = 0
$mismatchCount = 0

foreach ($sp in $procedures) {
    $spName     = $sp.Name
    $jsonParams = $sp.Parameters | ForEach-Object { $_.ToLower() }
    $jsonThreshold = if ($sp.PSObject.Properties['Threshold']) { $sp.Threshold } else { $null }

    $entry = [PSCustomObject]@{
        Name             = $spName
        DisplayName      = $sp.DisplayName
        Status           = "VALID"
        Details          = [System.Collections.ArrayList]::new()
        SpExists         = $false
        ParamsMatch      = $false
        ThresholdMatch   = $false
        JsonParams       = ($jsonParams -join ', ')
        DbParams         = ""
        DbParamsRaw      = @()
        JsonThreshold    = $jsonThreshold
        DbThreshold      = $null
    }

    # ── Check 1: Existence ────────────────────────────────────────────────
    if (-not $dbSpParams.ContainsKey($spName)) {
        $entry.Status  = "MISSING"
        $entry.SpExists = $false
        [void]$entry.Details.Add("Stored procedure does not exist in database.")
        $missingCount++
    }
    else {
        $entry.SpExists = $true

        # ── Check 2: Parameters ───────────────────────────────────────────
        $dbParams = $dbSpParams[$spName] | ForEach-Object { $_.ToLower() }
        $entry.DbParams = ($dbParams -join ', ')
        $entry.DbParamsRaw = @($dbSpParams[$spName])

        $missingInDb   = $jsonParams | Where-Object { $_ -notin $dbParams }
        $extraInDb     = $dbParams   | Where-Object { $_ -notin $jsonParams }

        if ($missingInDb -or $extraInDb) {
            $entry.ParamsMatch = $false
            if ($entry.Status -eq "VALID") { $entry.Status = "MISMATCH" }

            if ($missingInDb) {
                [void]$entry.Details.Add("Parameters in JSON but NOT in DB: @$($missingInDb -join ', @')")
            }
            if ($extraInDb) {
                [void]$entry.Details.Add("Parameters in DB but NOT in JSON: @$($extraInDb -join ', @')")
            }
        }
        else {
            $entry.ParamsMatch = $true
        }

        # ── Check 3: Threshold ────────────────────────────────────────────
        if ($dbSpSource.ContainsKey($spName)) {
            $dbThreshold = Get-ThresholdFromSource -SpSource $dbSpSource[$spName]
            $entry.DbThreshold = $dbThreshold

            if ($null -ne $jsonThreshold -and $jsonThreshold -gt 0) {
                if ($null -eq $dbThreshold) {
                    [void]$entry.Details.Add("Threshold defined in JSON ($jsonThreshold) but no threshold logic found in SP source.")
                    if ($entry.Status -eq "VALID") { $entry.Status = "MISMATCH" }
                }
                elseif ($jsonThreshold -ne $dbThreshold) {
                    [void]$entry.Details.Add("Threshold MISMATCH: JSON=$($jsonThreshold.ToString('N0'))  DB=$($dbThreshold.ToString('N0'))")
                    if ($entry.Status -eq "VALID") { $entry.Status = "MISMATCH" }
                }
                else {
                    $entry.ThresholdMatch = $true
                }
            }
            elseif ($null -ne $dbThreshold) {
                [void]$entry.Details.Add("Threshold found in SP source ($($dbThreshold.ToString('N0'))) but not defined in JSON.")
                if ($entry.Status -eq "VALID") { $entry.Status = "MISMATCH" }
            }
            else {
                $entry.ThresholdMatch = $true  # Neither has threshold — OK
            }
        }

        if ($entry.Status -eq "MISMATCH") { $mismatchCount++ }
        if ($entry.Status -eq "VALID")    { $validCount++ }
    }

    $results += $entry

    # Console output
    $statusColor = switch ($entry.Status) {
        "VALID"    { "Green" }
        "MISSING"  { "Red" }
        "MISMATCH" { "Yellow" }
    }
    $statusTag = Format-Status $entry.Status
    Write-Host "  $statusTag $spName" -ForegroundColor $statusColor

    foreach ($detail in $entry.Details) {
        Write-Host "             → $detail" -ForegroundColor $statusColor
    }
}

# ── 6. Summary ────────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "  ─────────────────────────────────────────────────────────────" -ForegroundColor Gray
Write-Host "  Summary" -ForegroundColor Cyan
Write-Host "  ─────────────────────────────────────────────────────────────" -ForegroundColor Gray
Write-Host "  Total     : $($procedures.Count)" -ForegroundColor White
Write-Host "  Valid     : $validCount" -ForegroundColor Green
Write-Host "  Missing   : $missingCount" -ForegroundColor $(if ($missingCount -gt 0) {"Red"} else {"Gray"})
Write-Host "  Mismatch  : $mismatchCount" -ForegroundColor $(if ($mismatchCount -gt 0) {"Yellow"} else {"Gray"})
Write-Host ""

# ── 7. Auto-Fix (Prompt) ──────────────────────────────────────────────────────

if ($mismatchCount -gt 0) {
    Write-Host "  ⚠ Mismatches were detected between the database and procedures.json." -ForegroundColor Yellow
    $autoFixChoice = Read-Host "  Do you want to automatically fix procedures.json to match the database? (Y/N)"
    
    if ($autoFixChoice -match '(?i)^y') {
        $fixedCount = 0
        foreach ($sp in $config.StoredProcedures) {
            $entryMatch = $results | Where-Object { $_.Name -eq $sp.Name }
            if ($null -ne $entryMatch -and $entryMatch.Status -eq "MISMATCH") {
                
                # Fix parameters
                if (-not $entryMatch.ParamsMatch) {
                    $sp.Parameters = $entryMatch.DbParamsRaw
                }
                
                # Fix thresholds
                if (-not $entryMatch.ThresholdMatch) {
                    if ($null -ne $entryMatch.DbThreshold) {
                        if ($null -ne $sp.PSObject.Properties['Threshold']) {
                            $sp.Threshold = $entryMatch.DbThreshold
                        } else {
                            $sp | Add-Member -MemberType NoteProperty -Name "Threshold" -Value $entryMatch.DbThreshold
                        }
                    } else {
                        if ($null -ne $sp.PSObject.Properties['Threshold']) {
                            $sp.Threshold = 0
                        }
                    }
                }
                $fixedCount++
            }
        }
        
        # Save back to file with proper formatting (avoiding PowerShell's weird depth truncation)
        $jsonOutput = $config | ConvertTo-Json -Depth 10
        [System.IO.File]::WriteAllText($ConfigPath, $jsonOutput, [System.Text.Encoding]::UTF8)
        
        Write-Host "  ✓ Successfully auto-fixed $fixedCount stored procedure(s) in configuration." -ForegroundColor Green
        Write-Host "  Note: The generated report below reflects the state prior to auto-fixing." -ForegroundColor Gray
        Write-Host ""
    }
}

# ── 8. Generate Report File ──────────────────────────────────────────────────

if (-not (Test-Path $ReportDir)) {
    New-Item -ItemType Directory -Path $ReportDir -Force | Out-Null
}

$timestamp  = Get-Date -Format "yyyyMMdd_HHmmss"
$reportPath = Join-Path $ReportDir "validation_report_$timestamp.txt"

$reportLines = @()
$reportLines += "═══════════════════════════════════════════════════════════════"
$reportLines += "  procedures.json Validation Report"
$reportLines += "  Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$reportLines += "═══════════════════════════════════════════════════════════════"
$reportLines += ""
$reportLines += "  Server        : $Server"
$reportLines += "  Database      : $Database"
$reportLines += "  Auth          : $(if ($WindowsAuth) {'Windows'} else {'SQL Server'})"
$reportLines += "  Config File   : $ConfigPath"
$reportLines += ""
$reportLines += "───────────────────────────────────────────────────────────────"
$reportLines += "  VALIDATION RESULTS"
$reportLines += "───────────────────────────────────────────────────────────────"
$reportLines += ""

foreach ($entry in $results) {
    $statusTag = Format-Status $entry.Status
    $reportLines += "$statusTag $($entry.Name)"
    $reportLines += "           Display Name : $($entry.DisplayName)"
    $reportLines += "           SP Exists    : $(if ($entry.SpExists) {'Yes'} else {'No'})"

    if ($entry.SpExists) {
        $reportLines += "           JSON Params  : $($entry.JsonParams)"
        $reportLines += "           DB Params    : $($entry.DbParams)"
        $reportLines += "           Params Match : $(if ($entry.ParamsMatch) {'Yes'} else {'No'})"

        if ($null -ne $entry.JsonThreshold -and $entry.JsonThreshold -gt 0) {
            $reportLines += "           JSON Threshold: $($entry.JsonThreshold.ToString('N0'))"
        }
        if ($null -ne $entry.DbThreshold) {
            $reportLines += "           DB Threshold  : $($entry.DbThreshold.ToString('N0'))"
        }
        $reportLines += "           Threshold OK : $(if ($entry.ThresholdMatch) {'Yes'} else {'No'})"
    }

    if ($entry.Details.Count -gt 0) {
        $reportLines += ""
        foreach ($detail in $entry.Details) {
            $reportLines += "           ! $detail"
        }
    }

    $reportLines += ""
    $reportLines += "  - - - - - - - - - - - - - - - - - - - - - - - - - - - - - -"
    $reportLines += ""
}

$reportLines += "───────────────────────────────────────────────────────────────"
$reportLines += "  SUMMARY"
$reportLines += "───────────────────────────────────────────────────────────────"
$reportLines += "  Total     : $($procedures.Count)"
$reportLines += "  Valid     : $validCount"
$reportLines += "  Missing   : $missingCount"
$reportLines += "  Mismatch  : $mismatchCount"
$reportLines += ""
$reportLines += "═══════════════════════════════════════════════════════════════"
$reportLines += "  END OF REPORT"
$reportLines += "═══════════════════════════════════════════════════════════════"

$reportLines | Out-File -FilePath $reportPath -Encoding UTF8

Write-Host "  Report saved: $reportPath" -ForegroundColor Cyan
Write-Host ""

# Return exit code based on results
if ($missingCount -gt 0 -or $mismatchCount -gt 0) {
    exit 1
}
exit 0
