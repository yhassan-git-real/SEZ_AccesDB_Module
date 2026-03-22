TECHNICAL DESIGN DOCUMENT (TDD)
Project: SEZ_AccesDB_Module

---

1. OVERVIEW

---

1.1 Purpose
This document defines the technical design for a console-based ETL automation system that executes SQL Server stored procedures and exports the resulting data into Microsoft Access databases with full auditing, logging, and configurability.

You can read the stored procedure logic and table DDL for application development and processing workflow clarity.

1.2 Scope
The application will:

* Execute user-selected stored procedures
* Process result sets
* Export data into Microsoft Access (.accdb) files
* Handle file size constraints via data splitting
* Maintain audit logs and execution tracking

---

2. SYSTEM ARCHITECTURE

---

High-Level Components:

* Console UI Layer (Spectre.Console)
* Application Layer (Orchestration)
* ETL Engine
* Configuration Manager
* Data Access Layer (SQL + Access)
* Logging & Auditing

---

3. TECHNOLOGY STACK

---

* Language: C# (.NET 8)
* UI Library: Spectre.Console
* Source Database: SQL Server
* Target Database: Microsoft Access (.accdb)
* Configuration: JSON (System.Text.Json)
* Logging: Custom / Serilog (recommended)
* Data Access: ADO.NET / Dapper

---

4. FUNCTIONAL DESIGN

---

4.1 Execution Flow

1. Load configuration (JSON)
2. Display available stored procedures
3. Accept user input:

   * Stored procedure selection
   * Parameters
   * Output file name
4. Execute stored procedure
5. Fetch result set
6. Determine row count
7. Apply file splitting rules
8. Create Access database file(s)
9. Insert data into Access tables
10. Log execution details
11. Write audit record
12. Display summary

---

## 4.2 FILE SPLITTING LOGIC

Threshold Rules:

* Import Data:
  If row count <= 1,500,000 → Single file
  If row count > 1,500,000 → Multiple files

* Export Data:
  If row count <= 2,000,000 → Single file
  If row count > 2,000,000 → Multiple files

Algorithm:

IF rowCount <= threshold
create single file
ELSE
split data into chunks
create multiple files
END IF

File Naming:

Single File:
Export_File_23032026.accdb

Multiple Files:
Export_File_1_23032026.accdb
Export_File_2_23032026.accdb

---

5. MODULE DESIGN

---

5.1 Console UI Module
Responsibilities:

* User interaction
* Input collection
* Progress display
* Final summary

5.2 Configuration Module
Responsibilities:

* Load and validate JSON configuration
* Provide strongly typed configuration objects

Sample Config:

{
"ConnectionStrings": {
"SqlServer": "",
"AccessTemplatePath": ""
},
"FileSettings": {
"OutputPath": "",
"Extension": ".accdb"
},
"StoredProcedures": [
{
"Name": "sp_GetData",
"Parameters": ["StartDate", "EndDate"]
}
],
"Audit": {
"TableName": "AuditLog"
}
}

5.3 ETL Engine
Responsibilities:

* Execute stored procedures
* Process datasets
* Manage file splitting

Subcomponents:

* SP Executor
* Data Processor
* Chunk Manager

5.4 Data Access Layer

SQL Server:

* Execute stored procedures
* Retrieve data using DataReader / DataTable

Access DB:

* Create .accdb files
* Insert data using OLE DB

5.5 File Manager
Responsibilities:

* Generate file names
* Handle multi-file logic
* Track file metadata

5.6 Logging Module

Log Types:

1. Success Log
2. Error Log
3. Summary Log

Log Format:
[Timestamp] [Level] [SP Name] [Parameters] [FileName] [Rows] [Duration] [Message]

5.7 Audit Module

Responsibilities:

* Insert execution records into audit table

Audit Table Structure:

* StoreProcedureName
* Parameter
* ProcessId
* Date
* FileNames
* RowsCount
* Message
* Comment

---

6. DATA VALIDATION

---

* Validate column count
* Validate data types
* Ensure schema compatibility with Access tables

---

7. ERROR HANDLING

---

Non-Fatal Errors:

* Row-level failures
* Data mismatches
  Action: Log and continue

Fatal Errors:

* Missing configuration
* Database connectivity failure
  Action: Stop execution

---

8. PERFORMANCE CONSIDERATIONS

---

* Use batch inserts
* Stream data instead of loading fully into memory
* Use IDataReader where possible
* Optimize chunk sizes

---

9. SECURITY CONSIDERATIONS

---

* Secure connection strings
* Avoid logging sensitive data
* Validate user inputs

---

10. UI / UX DESIGN

---

* Use Spectre.Console
* Color-coded output:

  * Success: Green
  * Warning: Yellow
  * Error: Red

Progress Example:
Processing file 45 of 1000

Execution Example:
[INFO] Selected SP: sp_GetData
[INFO] Fetching data...
[INFO] Total Rows: 2,300,000
[WARNING] Data exceeds threshold. Splitting required.
[PROGRESS] Processing file 1 of 2...
[PROGRESS] Processing file 2 of 2...
[SUCCESS] Execution completed.

---

11. SUMMARY OUTPUT

---

At the end of execution:

* Total Files
* Success Count
* Error Count

---

12. EXTENSIBILITY

---

Future Enhancements:

* Support CSV/Excel output
* Parallel processing
* Retry mechanism
* Scheduler integration (Task Scheduler / Hangfire)

---

13. DEPLOYMENT

---

* Publish as self-contained .NET 8 console app
* Include:

  * JSON config file
  * Access template (if required)
* Run via CLI or scheduler

---

14. ASSUMPTIONS

---

* Stored procedures return structured tabular data
* Access schema is predefined or dynamically created
* Row count can be determined before processing

---

## END OF DOCUMENT
