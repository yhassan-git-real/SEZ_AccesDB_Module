# SEZ_AccesDB_Module
> **High-Performance SQL Server to Microsoft Access ETL Engine**

<p align="left">
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 8" />
  <img src="https://img.shields.io/badge/SQL_Server-2016+-CC2927?style=for-the-badge&logo=microsoft-sql-server&logoColor=white" alt="SQL Server" />
  <img src="https://img.shields.io/badge/MS_Access-OLEDB-0078D4?style=for-the-badge&logo=microsoft-access&logoColor=white" alt="MS Access" />
  <img src="https://img.shields.io/badge/Platform-Windows-0078D6?style=for-the-badge&logo=windows&logoColor=white" alt="Platform" />
</p>

---

## 💎 Overview
**SEZ_AccesDB_Module** is an enterprise-grade ETL solution designed to automate the extraction of complex datasets from SQL Server into portable Microsoft Access (`.accdb`) databases. Engineered for reliability, it features **intelligent file splitting**, **automated synchronization**, and **full audit traceability**.

### 🌟 Core Value Proposition
-   **Scale**: Handle multi-million row datasets without memory exhaustion.
-   **Integrity**: 100% audit coverage for every data transfer.
-   **Efficiency**: One-click execution with automated configuration syncing.

---

## 🛠️ Documentation Portal
Navigate through the comprehensive technical documentation for the SEZ Suite.

| Guide | Description | Target Audience |
| :--- | :--- | :--- |
| 🚀 **[Operations Guide](docs/Operations_Guide.md)** | Daily execution, monitoring, and troubleshooting. | Operators / Admins |
| ⚙️ **[Configuration Reference](docs/Configuration_Reference.md)** | Deep dive into `appsettings.json` and `procedures.json`. | Config Managers |
| 🏗️ **[Architecture Overview](docs/Architecture_Overview.md)** | System design, data flow, and chunking logic. | Developers / Architects |
| 🛠️ **[Maintenance Guide](docs/Maintenance_Guide.md)** | Adding new procedures and automation sync. | DBAs / Developers |

---

## 🧩 How It Works
The system follows a streamlined pipeline from production SQL databases to local Access storage.

```mermaid
graph LR
    classDef sql fill:#CC2927,stroke:#333,stroke-width:2px,color:#fff;
    classDef engine fill:#003366,stroke:#333,stroke-width:2px,color:#fff;
    classDef target fill:#0078D4,stroke:#333,stroke-width:2px,color:#fff;
    classDef audit fill:#333,stroke:#333,stroke-width:1px,color:#fff;

    DB[(SQL Production)]:::sql -->|Stored Proc| ENG[ETL Engine Service]:::engine
    ENG -->|Data Streaming| SPLIT{File Splitter}:::engine
    SPLIT -->|Chunk 1| ACC1[Access File A]:::target
    SPLIT -->|Chunk N| ACC2[Access File N]:::target
    ENG -.->|Execution Log| AUD[(SQL Audit Table)]:::audit

    style ENG cursor:pointer;
```

---

## ⚡ Features at a Glance

### 🛡️ Intelligent Data Splitting
Never worry about the 2GB limit. The **Data Chunker** automatically monitors row counts and splits data into multiple files, ensuring seamless delivery.

### 🔄 Automated Schema Sync
The `ConfigValidator` toolset keeps your JSON definitions in perfect parity with your SQL Server stored procedures. No more manual entry errors.

### 📊 Real-time Monitoring
Powered by `Spectre.Console`, the interface provides high-fidelity progress bars and deep execution metrics upon completion.

---

## 🚦 Quick Start

> [!TIP]
> Ensure you have the **Microsoft Access Database Engine 2016 Redistributable** installed before running.

1.  **Configure**: Update `appsettings.json` with your connection string.
2.  **Verify**: Run `powershell -File tools/ConfigValidator/Validate-Procedures.ps1` to check integrity.
3.  **Execute**: Run `run-release.bat`.

---

## 📄 License
This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

© 2026 SEZ Trade Division. All Rights Reserved.
