namespace SEZ_AccesDB_Module.Core.Models;

/// <summary>
/// Audit record to be inserted into the AuditLog SQL Server table.
/// Matches the AuditLog table structure from Requirement.md §5.7.
/// </summary>
public class AuditRecord
{
    public string StoreProcedureName { get; set; } = string.Empty;
    public string? Parameter { get; set; }
    public string ProcessId { get; set; } = string.Empty;
    public DateTime Date { get; set; } = DateTime.Now;
    public string? FileNames { get; set; }
    public long? RowsCount { get; set; }
    public string? Message { get; set; }
    public string? Comment { get; set; }
}
