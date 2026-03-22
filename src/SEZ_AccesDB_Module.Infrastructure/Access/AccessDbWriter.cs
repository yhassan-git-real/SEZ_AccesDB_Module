using System.Data;
using System.Data.OleDb;
using SEZ_AccesDB_Module.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace SEZ_AccesDB_Module.Infrastructure.Access;

/// <summary>
/// Creates Microsoft Access .accdb files and bulk-inserts data using ACE OLE DB.
/// Requires: Microsoft Access Database Engine 2016 Redistributable (64-bit).
/// </summary>
public class AccessDbWriter : IAccessDbWriter
{
    private readonly ILogger<AccessDbWriter> _logger;
    private const int BatchSize = 1000;

    public AccessDbWriter(ILogger<AccessDbWriter> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<long> WriteChunkAsync(
        string filePath,
        string tableName,
        IDataReader reader,
        DataTable schema,
        long rowCount,
        Action<long>? onProgress = null,
        Action<long, Exception>? onRowError = null,
        CancellationToken ct = default)
    {
        var columns = BuildColumnDefinitions(schema);

        // Create a fresh .accdb file
        CreateAccessDatabase(filePath);

        long rowsWritten = 0;
        long rowsAttempted = 0;

        await using var conn = new OleDbConnection(BuildConnectionString(filePath));
        await Task.Run(() => conn.Open(), ct);

        // Build the table structure
        CreateTableInAccess(conn, tableName, columns);

        // Prepare the parameterised INSERT command (no transaction assigned yet)
        using var insertCmd = BuildInsertCommand(conn, tableName, columns);

        OleDbTransaction? tx = null;
        long batchCount = 0;

        try
        {
            tx = conn.BeginTransaction();
            insertCmd.Transaction = tx;

            while (rowsAttempted < rowCount)
            {
                ct.ThrowIfCancellationRequested();

                if (!reader.Read())
                    break; // source exhausted before rowCount reached

                rowsAttempted++;

                try
                {
                    PopulateInsertParameters(insertCmd, reader, columns);
                    await Task.Run(() => insertCmd.ExecuteNonQuery(), ct);
                    rowsWritten++;
                    batchCount++;
                }
                catch (Exception ex)
                {
                    // Non-fatal row error — log and continue
                    _logger.LogWarning(ex, "Row-level insert error at row {Row}", rowsAttempted);
                    onRowError?.Invoke(rowsAttempted, ex);
                }

                // Commit batch and open a new transaction
                if (batchCount >= BatchSize)
                {
                    await Task.Run(() => tx.Commit(), ct);
                    tx.Dispose();
                    tx = conn.BeginTransaction();
                    insertCmd.Transaction = tx;
                    batchCount = 0;
                    onProgress?.Invoke(rowsWritten);
                }
            }

            // Commit any remaining rows
            if (batchCount > 0)
            {
                await Task.Run(() => tx.Commit(), ct);
                tx.Dispose();
                tx = null;
                onProgress?.Invoke(rowsWritten);
            }
        }
        catch (OperationCanceledException)
        {
            SafeRollback(tx);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error writing Access chunk: {FilePath}", filePath);
            SafeRollback(tx);
            throw;
        }
        finally
        {
            tx?.Dispose();
        }

        _logger.LogInformation("Access chunk written → {File} | Rows written: {Written:N0} / Attempted: {Attempted:N0}",
            Path.GetFileName(filePath), rowsWritten, rowsAttempted);

        return rowsWritten;
    }

    // ─── Database / Schema Helpers ──────────────────────────────────────────────

    private static void CreateAccessDatabase(string filePath)
    {
        if (File.Exists(filePath))
            File.Delete(filePath);

        // ACE OLE DB creates an empty .accdb when the file doesn't exist and Engine Type=5
        var connStr = $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={filePath};Jet OLEDB:Engine Type=5;";
        using var conn = new OleDbConnection(connStr);
        conn.Open();
    }

    private static string BuildConnectionString(string filePath) =>
        $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={filePath};";

    // ─── Column Definition & Type Mapping ──────────────────────────────────────

    private static List<ColumnDefinition> BuildColumnDefinitions(DataTable schema)
    {
        var cols = new List<ColumnDefinition>();
        foreach (DataRow row in schema.Rows)
        {
            var colName  = row["ColumnName"]?.ToString() ?? "Col";
            var netType  = row["DataType"] as Type ?? typeof(string);
            var maxLen   = row["ColumnSize"] is int len ? len : 255;
            var allowNull = row["AllowDBNull"] is bool b && b;

            cols.Add(new ColumnDefinition
            {
                Name         = colName,
                NetType      = netType,
                MaxLength    = maxLen,
                AllowNull    = allowNull,
                AccessDdlType = MapToAccessType(netType, maxLen),
                OleDbType    = MapToOleDbType(netType)
            });
        }
        return cols;
    }

    private static string MapToAccessType(Type t, int maxLen)
    {
        if (t == typeof(short))                            return "SHORT";
        if (t == typeof(int))                              return "LONG";
        if (t == typeof(long))                             return "LONG";
        if (t == typeof(double) || t == typeof(float))     return "DOUBLE";
        if (t == typeof(decimal))                          return "DOUBLE";
        if (t == typeof(DateTime))                         return "DATETIME";
        if (t == typeof(bool))                             return "YESNO";
        if (t == typeof(byte[]))                           return "LONGBINARY";
        // Strings
        if (maxLen <= 0 || maxLen > 255)                   return "MEMO";
        return "TEXT(255)";
    }

    private static OleDbType MapToOleDbType(Type t)
    {
        if (t == typeof(short))                             return OleDbType.SmallInt;
        if (t == typeof(int))                               return OleDbType.Integer;
        if (t == typeof(long))                              return OleDbType.BigInt;
        if (t == typeof(double) || t == typeof(float))      return OleDbType.Double;
        if (t == typeof(decimal))                           return OleDbType.Decimal;
        if (t == typeof(DateTime))                          return OleDbType.Date;
        if (t == typeof(bool))                              return OleDbType.Boolean;
        if (t == typeof(byte[]))                            return OleDbType.Binary;
        return OleDbType.VarWChar;
    }

    // ─── DDL & DML Builders ────────────────────────────────────────────────────

    private static void CreateTableInAccess(OleDbConnection conn, string tableName, List<ColumnDefinition> columns)
    {
        var safeTable = tableName.Replace("]", "]]");
        var colDefs   = string.Join(", ", columns.Select(c =>
            $"[{c.Name.Replace("]", "]]")}] {c.AccessDdlType}"));

        using var cmd = new OleDbCommand($"CREATE TABLE [{safeTable}] ({colDefs})", conn);
        cmd.ExecuteNonQuery();
    }

    private static OleDbCommand BuildInsertCommand(OleDbConnection conn, string tableName, List<ColumnDefinition> columns)
    {
        var safeTable = tableName.Replace("]", "]]");
        var colNames  = string.Join(", ", columns.Select(c => $"[{c.Name.Replace("]", "]]")}]"));
        var placeholders = string.Join(", ", columns.Select(_ => "?"));
        var sql = $"INSERT INTO [{safeTable}] ({colNames}) VALUES ({placeholders})";

        var cmd = new OleDbCommand(sql, conn);
        foreach (var col in columns)
            cmd.Parameters.Add(col.Name, col.OleDbType);

        return cmd;
    }

    private static void PopulateInsertParameters(OleDbCommand cmd, IDataReader reader, List<ColumnDefinition> columns)
    {
        for (int i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            object rawValue = i < reader.FieldCount ? reader.GetValue(i) : DBNull.Value;
            cmd.Parameters[i].Value = CoerceValue(rawValue, col.NetType);
        }
    }

    private static object CoerceValue(object rawValue, Type targetType)
    {
        if (rawValue == null || rawValue is DBNull)
            return DBNull.Value;

        try
        {
            if (targetType == typeof(double) || targetType == typeof(float))
            {
                if (rawValue is double or float or decimal) return Convert.ToDouble(rawValue);
                if (double.TryParse(rawValue.ToString(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
                return DBNull.Value;
            }

            if (targetType == typeof(decimal))
            {
                if (decimal.TryParse(rawValue.ToString(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var dec)) return dec;
                return DBNull.Value;
            }

            if (targetType == typeof(int) || targetType == typeof(long) || targetType == typeof(short))
            {
                if (long.TryParse(rawValue.ToString(), out var l)) return l;
                return DBNull.Value;
            }

            if (targetType == typeof(DateTime))
            {
                if (rawValue is DateTime dt) return dt;
                if (DateTime.TryParse(rawValue.ToString(), out var dt2)) return dt2;
                return DBNull.Value;
            }

            if (targetType == typeof(bool))
            {
                if (rawValue is bool bv) return bv;
                if (bool.TryParse(rawValue.ToString(), out var bv2)) return bv2;
                return DBNull.Value;
            }

            // String — truncate to 255 chars for TEXT fields to avoid Access truncation errors
            var str = rawValue.ToString() ?? string.Empty;
            return str;
        }
        catch
        {
            return DBNull.Value;
        }
    }

    private static void SafeRollback(OleDbTransaction? tx)
    {
        try { tx?.Rollback(); } catch { /* best-effort */ }
    }

    // ─── Column definition record ──────────────────────────────────────────────

    private record ColumnDefinition
    {
        public string Name { get; init; } = string.Empty;
        public Type NetType { get; init; } = typeof(string);
        public int MaxLength { get; init; } = 255;
        public bool AllowNull { get; init; } = true;
        public string AccessDdlType { get; init; } = "TEXT(255)";
        public OleDbType OleDbType { get; init; } = OleDbType.VarWChar;
    }
}
