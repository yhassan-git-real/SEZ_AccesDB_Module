using SEZ_AccesDB_Module.Core.Configuration;
using SEZ_AccesDB_Module.Core.Models;

namespace SEZ_AccesDB_Module.Core.Interfaces;

/// <summary>
/// SQL Server data access: execute stored procedures and read staging tables.
/// </summary>
public interface ISqlDataAccess
{
    /// <summary>
    /// Executes a stored procedure. The SP modifies the staging table in-place.
    /// </summary>
    Task ExecuteStoredProcedureAsync(string spName, IEnumerable<SpParameter> parameters, CancellationToken ct = default);

    /// <summary>
    /// Returns an open IDataReader over the given SQL query. Caller must dispose.
    /// </summary>
    Task<System.Data.IDataReader> GetDataReaderAsync(string sql, CancellationToken ct = default);

    /// <summary>
    /// Returns the row count of a staging table after SP execution.
    /// </summary>
    Task<long> GetRowCountAsync(string tableName, CancellationToken ct = default);

    /// <summary>
    /// Tests the SQL Server connection. Returns true if successful.
    /// </summary>
    Task<bool> TestConnectionAsync(CancellationToken ct = default);

    /// <summary>
    /// Ensures the AuditLog table exists, creating it if necessary.
    /// </summary>
    Task EnsureAuditTableExistsAsync(string tableName, CancellationToken ct = default);

    /// <summary>
    /// Inserts an audit log record into the AuditLog table.
    /// </summary>
    Task InsertAuditRecordAsync(string tableName, AuditRecord record, CancellationToken ct = default);
}
