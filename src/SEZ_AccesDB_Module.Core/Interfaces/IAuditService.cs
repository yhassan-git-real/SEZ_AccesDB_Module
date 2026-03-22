using SEZ_AccesDB_Module.Core.Models;

namespace SEZ_AccesDB_Module.Core.Interfaces;

/// <summary>
/// Writes audit records to the SQL Server AuditLog table.
/// </summary>
public interface IAuditService
{
    Task WriteAuditAsync(AuditRecord record, CancellationToken ct = default);
}
