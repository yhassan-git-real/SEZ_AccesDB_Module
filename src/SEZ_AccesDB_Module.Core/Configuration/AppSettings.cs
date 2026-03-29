namespace SEZ_AccesDB_Module.Core.Configuration;

public class AppSettings
{
    public ConnectionStringsConfig ConnectionStrings { get; set; } = new();
    public FileSettingsConfig FileSettings { get; set; } = new();
    public List<StoredProcedureDefinition> StoredProcedures { get; set; } = new();
    public AuditConfig Audit { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();
}

public class ConnectionStringsConfig
{
    public string SqlServer { get; set; } = string.Empty;
}

public class FileSettingsConfig
{
    public string OutputPath { get; set; } = string.Empty;
    public string Extension { get; set; } = ".accdb";
}


public class StoredProcedureDefinition
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty; // "Import" or "Export"
    public string StagingTable { get; set; } = string.Empty;
    public List<string> Parameters { get; set; } = new();
    public string FilePrefix { get; set; } = string.Empty;
    public long Threshold { get; set; }
}

public class AuditConfig
{
    public string TableName { get; set; } = "AuditLog";
    public bool Enabled { get; set; } = true;
}

public class LoggingConfig
{
    public string LogPath { get; set; } = string.Empty;
    public string MinimumLevel { get; set; } = "Information";
}
