# Configuration Reference
> **Dynamic and Global Application Settings**

The **SEZ_AccesDB_Module** is designed to be highly configurable across different environments. All operational behavior is driven by the following JSON manifests.

---

## 🌎 1. appsettings.json
This file controls the global application lifecycle, including database connectivity, file storage, and logging.

### 📋 Connection settings
| Field | Type | Description |
| :--- | :---: | :--- |
| `SqlServer` | string | Full ADO.NET connection string (e.g., `Server=...;Database=...`). |
| `Integrated Security` | boolean | Set to `True` to use current Windows credentials. |
| `TrustServerCertificate`| boolean | Required for modern SQL instances using encrypted connections. |

### 📂 File and Path settings
-   **`OutputPath`**: `F:\Trade\SEZ_AccesDB_Module\Output`
    -   The destination folder for all exported `.accdb` files.
-   **`Extension`**: `.accdb`
    -   The file format for output (Fixed to Access).

### 📋 Audit & Logging
> [!NOTE]
> All audits are written synchronously to ensure 100% traceability.

-   **`Audit.TableName`**: The SQL Server table name used for persistent execution records.
-   **`Logging.LogPath`**: The local directory for text-based logs (`Logs/`).
-   **`MinimumLevel`**: Set to `Information` for production; `Debug` for troubleshooting.

---

## 🧩 2. procedures.json
This manifest defines each stored procedure available in the system and its associated metadata.

### 📐 Procedure Schema
| Field | Requirement | Description |
| :--- | :---: | :--- |
| `Name` | **Mandatory** | The database name of the stored procedure. |
| `DisplayName` | Optional | Friendly name shown in the SEZ Menu. |
| `DataType` | **Mandatory** | `Import` or `Export`. |
| `StagingTable` | **Mandatory** | The source table used for data size tracking. |
| `Parameters` | **Mandatory** | List of parameter names (e.g., `["mon", "mon1"]`). |
| `Threshold` | **Mandatory** | Maximum rows before file splitting (e.g., `2000000`). |

---

## 🛠️ 3. validatorsettings.json
Used by the `ConfigValidator` toolset in `tools/ConfigValidator/`.

### 📋 Validator settings
-   **`ProceduresPath`**: The relative path from the validator to the main `procedures.json` file.
-   **`SplistPath`**: Path to the master text file (`splist.txt`) for synchronization.
-   **`ReportPath`**: Output path for the system integrity report.

---

> [!TIP]
> Use the **`Sync-Procedures.ps1`** script to automatically update `procedures.json` based on the latest SQL Server schema changes.
