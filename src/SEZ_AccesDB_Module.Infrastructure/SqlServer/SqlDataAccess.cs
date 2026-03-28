using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using SEZ_AccesDB_Module.Core.Interfaces;
using SEZ_AccesDB_Module.Core.Models;

namespace SEZ_AccesDB_Module.Infrastructure.SqlServer;

/// <summary>
/// Implements <see cref="ISqlDataAccess"/> using Microsoft.Data.SqlClient + Dapper.
/// All SP executions rely on ADO.NET directly (Dapper used for utility queries).
/// </summary>
public class SqlDataAccess : ISqlDataAccess
{
    private readonly string _connectionString;

    private static readonly int[] TransientSqlErrors = { -2, 258, 1205, 4060, 40197, 40501, 40613 };
    private const int MaxRetries = 3;

    public SqlDataAccess(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("SQL Server connection string cannot be empty.", nameof(connectionString));
        _connectionString = connectionString;
    }

    /// <inheritdoc/>
    public async Task ExecuteStoredProcedureAsync(string spName, IEnumerable<SpParameter> parameters, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(spName, conn)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 3600 // 1 hour — SPs can be long-running
        };

        foreach (var param in parameters)
            cmd.Parameters.AddWithValue($"@{param.Name}", param.Value);

        // SP may return multiple result sets (validation SELECTs); consume them all
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        do
        {
            while (await reader.ReadAsync(ct)) { /* consume without loading into memory */ }
        }
        while (await reader.NextResultAsync(ct));
    }

    /// <inheritdoc/>
    public async Task<IDataReader> GetDataReaderAsync(string sql, CancellationToken ct = default)
    {
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var cmd = new SqlCommand(sql, conn)
        {
            CommandTimeout = 3600
        };

        return await cmd.ExecuteReaderAsync(
            CommandBehavior.CloseConnection | CommandBehavior.SequentialAccess, ct);
    }

    /// <inheritdoc/>
    public async Task<long> GetRowCountAsync(string tableName, CancellationToken ct = default)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync(ct);
                var sql = $"SELECT COUNT_BIG(*) FROM [{tableName}]";
                await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 600 };
                var result = await cmd.ExecuteScalarAsync(ct);
                return Convert.ToInt64(result);
            }
            catch (SqlException ex) when (attempt < MaxRetries && TransientSqlErrors.Contains(ex.Number))
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
            }
        }
    }

    /// <inheritdoc/>
    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            return conn.State == ConnectionState.Open;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task EnsureAuditTableExistsAsync(string tableName, CancellationToken ct = default)
    {
        var ddl = $"""
            IF NOT EXISTS (
                SELECT * FROM sys.objects 
                WHERE object_id = OBJECT_ID(N'[dbo].[{tableName}]') AND type = N'U'
            )
            BEGIN
                CREATE TABLE [dbo].[{tableName}] (
                    [Id]                  INT IDENTITY(1,1) PRIMARY KEY,
                    [StoreProcedureName]  NVARCHAR(200) NOT NULL,
                    [Parameter]           NVARCHAR(500) NULL,
                    [ProcessId]           NVARCHAR(50) NOT NULL,
                    [Date]                DATETIME NOT NULL DEFAULT GETDATE(),
                    [FileNames]           NVARCHAR(MAX) NULL,
                    [RowsCount]           BIGINT NULL,
                    [Message]             NVARCHAR(MAX) NULL,
                    [Comment]             NVARCHAR(MAX) NULL
                )
            END
            """;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(ddl, conn) { CommandTimeout = 60 };
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc/>
    public async Task InsertAuditRecordAsync(string tableName, AuditRecord record, CancellationToken ct = default)
    {
        var sql = $"""
            INSERT INTO [dbo].[{tableName}]
                ([StoreProcedureName],[Parameter],[ProcessId],[Date],[FileNames],[RowsCount],[Message],[Comment])
            VALUES
                (@StoreProcedureName,@Parameter,@ProcessId,@Date,@FileNames,@RowsCount,@Message,@Comment)
            """;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await conn.ExecuteAsync(sql, new
        {
            record.StoreProcedureName,
            record.Parameter,
            record.ProcessId,
            record.Date,
            record.FileNames,
            record.RowsCount,
            record.Message,
            record.Comment
        });
    }

    /// <inheritdoc/>
    public async Task<bool> TableExistsAsync(string tableName, CancellationToken ct = default)
    {
        const string sql = """
            SELECT COUNT(1) FROM sys.objects
            WHERE object_id = OBJECT_ID(@name) AND type = 'U'
            """;

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync(ct);
                await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
                cmd.Parameters.AddWithValue("@name", tableName);
                var result = await cmd.ExecuteScalarAsync(ct);
                return Convert.ToInt32(result) > 0;
            }
            catch (SqlException ex) when (attempt < MaxRetries && TransientSqlErrors.Contains(ex.Number))
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
            }
        }
    }

    /// <inheritdoc/>
    public async Task<long> GetTableSizeBytesAsync(string tableName, CancellationToken ct = default)
    {
        const string sql = """
            SELECT ISNULL(SUM(a.total_pages) * 8192, 0) AS SizeBytes
            FROM sys.tables t
            INNER JOIN sys.indexes i ON t.object_id = i.object_id
            INNER JOIN sys.partitions p ON i.object_id = p.object_id AND i.index_id = p.index_id
            INNER JOIN sys.allocation_units a ON p.partition_id = a.container_id
            WHERE t.name = @tableName
            """;

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync(ct);
                await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
                cmd.Parameters.AddWithValue("@tableName", tableName);
                var result = await cmd.ExecuteScalarAsync(ct);
                return Convert.ToInt64(result);
            }
            catch (SqlException ex) when (attempt < MaxRetries && TransientSqlErrors.Contains(ex.Number))
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
            }
        }
    }
}
