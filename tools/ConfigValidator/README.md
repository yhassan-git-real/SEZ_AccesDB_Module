# procedures.json ↔ Database Validation Utility

Lightweight PowerShell tool that validates `procedures.json` configuration against the live SQL Server database.

## What It Validates

| Check | Method | Status |
|---|---|---|
| **SP Existence** | `sys.objects` | MISSING if not found |
| **Parameter Names** | `sys.parameters` | MISMATCH if names differ |
| **Threshold Values** | Parsed from SP source via `sys.sql_modules` | MISMATCH if values differ |

## Usage

### JSON Configuration (Recommended)
You can configure your settings in a `validatorsettings.json` file in the same directory as the script. The script will automatically load it.
```json
{
  "ServerName": "MATRIX,1434",
  "DatabaseName": "RAW_PROCESS_NEW",
  "Authentication": "Windows",
  "SqlUser": "",
  "SqlPassword": "",
  "ProcedurePath": "../../src/SEZ_AccesDB_Module/procedures.json",
  "OutputPath": "Reports"
}
```
Run without parameters:
```powershell
.\Validate-Procedures.ps1
```

### Interactive Mode
If no JSON file is found or parameters are missing, it will prompt interactively:
```powershell
.\Validate-Procedures.ps1
```
Prompts for Server, Database, and Authentication type.

### Non-Interactive CLI Mode
```powershell
# Windows Authentication
.\Validate-Procedures.ps1 -Server "MATRIX,1434" -Database "RAW_PROCESS_NEW" -WindowsAuth

# SQL Server Authentication (will prompt for user/password)
.\Validate-Procedures.ps1 -Server "MATRIX,1434" -Database "RAW_PROCESS_NEW"

# Custom config or settings paths
.\Validate-Procedures.ps1 -SettingsFile "C:\path\to\custom_settings.json"
.\Validate-Procedures.ps1 -Server "MATRIX" -Database "RAW_PROCESS_NEW" -WindowsAuth -ConfigPath "C:\path\to\procedures.json"
```

## Output

### Console
```
╔══════════════════════════════════════════════════════════════╗
║       procedures.json ↔ Database Validation Utility        ║
╚══════════════════════════════════════════════════════════════╝

  [VALID]    Others_EXP_BTD
  [VALID]    Others_EXP_FULL
  [MISMATCH] Others_EXP_FULL_NEW
             → Threshold MISMATCH: JSON=2,100,000  DB=210,000

  Summary
  Total     : 3
  Valid     : 2
  Missing   : 0
  Mismatch  : 1

  ⚠ Mismatches were detected between the database and procedures.json.
  Do you want to automatically fix procedures.json to match the database? (Y/N): Y
  
  ✓ Successfully auto-fixed 1 stored procedure(s) in configuration.
```

## Interactive Auto-Fix
If any discrepancies are flagged as `[MISMATCH]` (such as incorrect parameter names or desynced threshold amounts), the tool will prompt you before exiting:
- **Y**: Will automatically update the `procedures.json` file in-place, rewriting parameters and counts to perfectly match precisely what is running inside the SQL database so everything aligns.
- **N**: Will skip fixing the configuration and just save the generated report to disk.
 *(Missing procedures will be flagged but are not auto-removed to avoid destructive changes)*

### Report File
A timestamped `.txt` report is saved to:
```
tools/ConfigValidator/Reports/validation_report_YYYYMMDD_HHmmss.txt
```

## Exit Codes

| Code | Meaning |
|---|---|
| `0` | All validations passed |
| `1` | One or more validations failed (missing or mismatch) |

## File Structure

```
tools/ConfigValidator/
├── Validate-Procedures.ps1    # Main validation and auto-fix script
├── Sync-Procedures.ps1        # Master list synchronization script
├── validatorsettings.json     # Configuration file
├── README.md                  # This file
└── Reports/                   # Generated reports (auto-created)
    └── validation_report_*.txt
```

---

# procedures.json Auto-Sync Utility

The `Sync-Procedures.ps1` script is a powerful automation tool designed to make managing your Stored Procedures completely structural. Instead of manually editing the JSON file, you provide a plain `.txt` file containing the master list of Stored Procedures you want exactly in your application.

## What It Does
1. **Validates** every Stored Procedure in your `.txt` file against the Database first.
2. **Adds** missing Stored Procedures to `procedures.json` (auto-fetching their Parameters, Thresholds, and generating reasonable defaults).
3. **Retains** existing Stored Procedures in `procedures.json` even if they are not listed in your text file, ensuring nothing is broken.
4. **Updates** any matching Stored Procedures with correctly refreshed Parameters and Threshold counts from the database to eliminate configuration drift.

## Usage

Create a text file (e.g., `splist.txt`) with one SP name per line:
```text
Others_EXP_BTD
Others_EXP_FULL
Others_IMP_BTD
```

Then execute the sync script, giving it the path to your text file:
```powershell
.\Sync-Procedures.ps1 -InputFile "splist.txt"
```
*(Note: It uses the exact same `validatorsettings.json` file for database connection credentials as the validator script)*

### Important Notice for New SPs
When the sync script injects a brand-new Stored Procedure into your JSON configuration, it cannot reliably guess the custom output `StagingTable` name the stored procedure code targets (e.g. `EXP_OTHERS_FULL`). As a safeguard, it inserts the placeholder string `"TODO_FILL_THIS_IN"`. 
**You must manually open `procedures.json` after running the script to correct these placeholder values!**
