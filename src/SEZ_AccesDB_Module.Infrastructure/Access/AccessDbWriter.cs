using System.Data;
using System.Data.OleDb;
using System.Runtime.InteropServices;
using SEZ_AccesDB_Module.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace SEZ_AccesDB_Module.Infrastructure.Access;

/// <summary>
/// Creates .accdb files and bulk-inserts data via DAO COM API.
/// DAO bypasses OleDB SQL parsing overhead, giving 3–5× faster bulk write throughput.
/// Rows are pre-buffered from SQL Server in chunks to separate SQL I/O from disk I/O.
/// Requires: Microsoft Access Database Engine 2016 Redistributable (64-bit)
/// </summary>
public class AccessDbWriter : IAccessDbWriter
{
    private readonly ILogger<AccessDbWriter> _logger;

    private const int ReadChunkSize    = 50_000;  // rows buffered from SQL per DAO write pass
    private const int ProgressInterval = 10_000;  // UI progress update frequency

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

        CreateAccessDatabase(filePath);

        using (var schemaConn = new OleDbConnection(BuildConnectionString(filePath)))
        {
            schemaConn.Open();
            CreateTableInAccess(schemaConn, tableName, columns);
        } // OleDB connection must be fully closed before DAO opens the same file

        // ACE engine needs a moment to release its file lock before DAO acquires it
        await Task.Delay(200, ct);

        long totalWritten   = 0;
        long totalAttempted = 0;

        while (totalAttempted < rowCount && !ct.IsCancellationRequested)
        {
            var chunk = new List<object[]>(ReadChunkSize);

            while (chunk.Count < ReadChunkSize && totalAttempted < rowCount)
            {
                ct.ThrowIfCancellationRequested();
                if (!reader.Read()) { totalAttempted = rowCount; break; }
                totalAttempted++;

                var row = new object[columns.Count];
                for (int c = 0; c < columns.Count; c++)
                    row[c] = c < reader.FieldCount ? reader.GetValue(c) : DBNull.Value;
                chunk.Add(row);
            }

            if (chunk.Count == 0) break;

            // DAO COM objects require STA apartment — thread pool threads are MTA
            long chunkStart   = totalAttempted - chunk.Count;
            long chunkWritten = 0;
            Exception? daoEx  = null;

            var staThread = new Thread(() =>
            {
                dynamic? engine = null;
                dynamic? db     = null;
                dynamic? rs     = null;

                try
                {
                    var engineType =
                        Type.GetTypeFromProgID("DAO.DBEngine.120") ??
                        Type.GetTypeFromProgID("DAO.DBEngine.36")  ??
                        throw new InvalidOperationException(
                            "DAO.DBEngine COM object not found. " +
                            "Ensure Microsoft Access Database Engine 2016 Redistributable is installed.");

                    engine = Activator.CreateInstance(engineType)!;
                    db = engine.OpenDatabase(filePath);
                    rs = db.OpenRecordset(tableName, 1); // dbOpenTable — fastest mode for bulk writes

                    for (int i = 0; i < chunk.Count; i++)
                    {
                        try
                        {
                            rs.AddNew();

                            var rowData = chunk[i];
                            for (int col = 0; col < columns.Count; col++)
                            {
                                object val = CoerceValue(rowData[col], columns[col].NetType);
                                if (val != DBNull.Value)
                                    rs.Fields[columns[col].Name].Value = val;
                            }

                            rs.Update();
                            chunkWritten++;

                            if (chunkWritten % ProgressInterval == 0)
                                onProgress?.Invoke(totalWritten + chunkWritten);
                        }
                        catch (Exception rowEx)
                        {
                            onRowError?.Invoke(chunkStart + i + 1, rowEx);
                        }
                    }
                }
                catch (Exception ex)
                {
                    daoEx = ex;
                }
                finally
                {
                    if (rs != null)     try { rs.Close();   } catch { }
                    if (db != null)     try { db.Close();   } catch { }
                    if (rs != null)     try { Marshal.FinalReleaseComObject(rs);     } catch { }
                    if (db != null)     try { Marshal.FinalReleaseComObject(db);     } catch { }
                    if (engine != null) try { Marshal.FinalReleaseComObject(engine); } catch { }
                }
            });

            staThread.SetApartmentState(ApartmentState.STA);
            staThread.IsBackground = true;
            staThread.Name = $"DAO-Writer-chunk-{chunkStart}";
            staThread.Start();

            // Join without blocking the thread pool
            await Task.Run(() => staThread.Join(), ct);

            if (daoEx != null)
            {
                _logger.LogError(daoEx, "DAO write failed at row offset {Offset}", chunkStart);
                throw daoEx;
            }

            totalWritten += chunkWritten;
            onProgress?.Invoke(totalWritten);
        }

        _logger.LogInformation(
            "DAO write complete → {File} | Rows written: {Written:N0} / Attempted: {Attempted:N0}",
            Path.GetFileName(filePath), totalWritten, totalAttempted);

        return totalWritten;
    }

    private static void CreateAccessDatabase(string filePath)
    {
        if (File.Exists(filePath))
            File.Delete(filePath);

        var catalogType = Type.GetTypeFromProgID("ADOX.Catalog")
            ?? throw new InvalidOperationException(
                "ADOX.Catalog COM object not found. " +
                "Please install the Microsoft Access Database Engine 2016 Redistributable (64-bit).");

        dynamic catalog = Activator.CreateInstance(catalogType)!;
        try
        {
            catalog.Create(
                $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={filePath};Jet OLEDB:Engine Type=5;");
        }
        finally
        {
            Marshal.FinalReleaseComObject(catalog);
        }

        if (!File.Exists(filePath))
            throw new InvalidOperationException(
                $"ADOX.Catalog.Create() completed but no file was created at: {filePath}");
    }

    private static string BuildConnectionString(string filePath) =>
        $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={filePath};";

    private static List<ColumnDefinition> BuildColumnDefinitions(DataTable schema)
    {
        var cols = new List<ColumnDefinition>();
        foreach (DataRow row in schema.Rows)
        {
            var colName   = row["ColumnName"]?.ToString() ?? "Col";
            var netType   = row["DataType"] as Type ?? typeof(string);
            var maxLen    = row["ColumnSize"] is int len ? len : 255;
            var allowNull = row["AllowDBNull"] is bool b && b;

            cols.Add(new ColumnDefinition
            {
                Name          = colName,
                NetType       = netType,
                MaxLength     = maxLen,
                AllowNull     = allowNull,
                AccessDdlType = MapToAccessType(netType, maxLen),
                OleDbType     = MapToOleDbType(netType)
            });
        }
        return cols;
    }

    private static string MapToAccessType(Type t, int maxLen)
    {
        if (t == typeof(short))                           return "SHORT";
        if (t == typeof(int))                             return "LONG";
        if (t == typeof(long))                            return "LONG";
        if (t == typeof(double) || t == typeof(float))    return "DOUBLE";
        if (t == typeof(decimal))                         return "DOUBLE";
        if (t == typeof(DateTime))                        return "DATETIME";
        if (t == typeof(bool))                            return "YESNO";
        if (t == typeof(byte[]))                          return "LONGBINARY";
        if (maxLen <= 0 || maxLen > 255)                  return "MEMO";
        return "TEXT(255)";
    }

    private static OleDbType MapToOleDbType(Type t)
    {
        if (t == typeof(short))                            return OleDbType.SmallInt;
        if (t == typeof(int))                              return OleDbType.Integer;
        if (t == typeof(long))                             return OleDbType.BigInt;
        if (t == typeof(double) || t == typeof(float))     return OleDbType.Double;
        if (t == typeof(decimal))                          return OleDbType.Decimal;
        if (t == typeof(DateTime))                         return OleDbType.Date;
        if (t == typeof(bool))                             return OleDbType.Boolean;
        if (t == typeof(byte[]))                           return OleDbType.Binary;
        return OleDbType.VarWChar;
    }

    private static void CreateTableInAccess(OleDbConnection conn, string tableName, List<ColumnDefinition> columns)
    {
        var safeTable = tableName.Replace("]", "]]");
        var colDefs   = string.Join(", ", columns.Select(c =>
            $"[{c.Name.Replace("]", "]]")}] {c.AccessDdlType}"));

        using var cmd = new OleDbCommand($"CREATE TABLE [{safeTable}] ({colDefs})", conn);
        cmd.ExecuteNonQuery();
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
                if (double.TryParse(rawValue.ToString(),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
                return DBNull.Value;
            }

            if (targetType == typeof(decimal))
            {
                if (decimal.TryParse(rawValue.ToString(),
                    System.Globalization.NumberStyles.Any,
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

            return rawValue.ToString() ?? string.Empty;
        }
        catch
        {
            return DBNull.Value;
        }
    }

    private record ColumnDefinition
    {
        public string    Name          { get; init; } = string.Empty;
        public Type      NetType       { get; init; } = typeof(string);
        public int       MaxLength     { get; init; } = 255;
        public bool      AllowNull     { get; init; } = true;
        public string    AccessDdlType { get; init; } = "TEXT(255)";
        public OleDbType OleDbType     { get; init; } = OleDbType.VarWChar;
    }
}
