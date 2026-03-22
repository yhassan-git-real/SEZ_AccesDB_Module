using SEZ_AccesDB_Module.Core.Interfaces;
using SEZ_AccesDB_Module.Core.Models;
using Microsoft.Extensions.Logging;

namespace SEZ_AccesDB_Module.Services.Audit;

/// <summary>
/// Writes audit records to the SQL Server AuditLog table via <see cref="ISqlDataAccess"/>.
/// </summary>
public class AuditService : IAuditService
{
    private readonly ISqlDataAccess _db;
    private readonly string _tableName;
    private readonly bool _enabled;
    private readonly ILogger<AuditService> _logger;

    public AuditService(ISqlDataAccess db, string tableName, bool enabled, ILogger<AuditService> logger)
    {
        _db = db;
        _tableName = tableName;
        _enabled = enabled;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task WriteAuditAsync(AuditRecord record, CancellationToken ct = default)
    {
        if (!_enabled)
        {
            _logger.LogDebug("Audit is disabled — skipping audit record.");
            return;
        }

        try
        {
            await _db.InsertAuditRecordAsync(_tableName, record, ct);
            _logger.LogInformation("Audit record written for SP: {SP}, ProcessId: {Pid}", 
                record.StoreProcedureName, record.ProcessId);
        }
        catch (Exception ex)
        {
            // Audit failure is non-fatal — log and continue
            _logger.LogError(ex, "Failed to write audit record for SP: {SP}", record.StoreProcedureName);
        }
    }
}
