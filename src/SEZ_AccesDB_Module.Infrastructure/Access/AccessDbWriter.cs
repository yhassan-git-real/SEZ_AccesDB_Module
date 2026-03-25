using System.Data;
using System.Data.OleDb;
using System.Runtime.InteropServices;
using SEZ_AccesDB_Module.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace SEZ_AccesDB_Module.Infrastructure.Access;

/// <summary>
/// Creates .accdb files and bulk-inserts data via DAO COM API.
/// DAO bypasses OleDB SQL parsing overhead, giving 3–5× faster bulk write throughput.
///
/// Write strategy: one persistent STA thread opens a single DAO session per file.
/// The main thread buffers rows from SQL Server in chunks and hands them to the STA writer,
/// keeping SQL I/O and Access disk I/O on separate threads.
///
/// Requires: Microsoft Access Database Engine 2016 Redistributable (64-bit)
/// </summary>
public class AccessDbWriter : IAccessDbWriter
{
    private readonly ILogger<AccessDbWriter> _logger;

    private const int ReadChunkSize    = 50_000;  // rows buffered from SQL per write pass
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
        bool fileCreated = false;

        try
        {
            CreateAccessDatabase(filePath);
            fileCreated = true;

            using (var conn = new OleDbConnection(BuildConnectionString(filePath)))
            {
                conn.Open();
                CreateTableInAccess(conn, tableName, columns);
            } // OleDB connection must be fully closed before DAO opens the same file

            long totalWritten   = 0;
            long totalAttempted = 0;

            // Shared state (accessed sequentially, never concurrently)
            List<object[]>? currentChunk  = null;
            long currentChunkStart        = 0;
            long currentChunkWritten      = 0;
            Exception? staException       = null;

            // work: main → STA (chunk ready, or null=stop)
            // done: STA → main (chunk written, or error)
            using var work = new SemaphoreSlim(0);
            using var done = new SemaphoreSlim(0);

            var staThread = new Thread(() =>
            {
                dynamic? engine = null, db = null, rs = null;
                try
                {
                    var engineType =
                        Type.GetTypeFromProgID("DAO.DBEngine.120") ??
                        Type.GetTypeFromProgID("DAO.DBEngine.36")  ??
                        throw new InvalidOperationException(
                            "DAO.DBEngine COM object not found. " +
                            "Ensure Microsoft Access Database Engine 2016 Redistributable is installed.");

                    engine = Activator.CreateInstance(engineType)!;

                    // Exponential-backoff retry: waits for OleDB to release the file lock
                    for (int attempt = 1; ; attempt++)
                    {
                        try { db = engine.OpenDatabase(filePath); break; }
                        catch when (attempt < 6) { Thread.Sleep(50 * (1 << attempt)); } // 100,200,400,800,1600ms
                    }

                    rs = db.OpenRecordset(tableName, 1); // dbOpenTable — fastest bulk-write mode

                    while (true)
                    {
                        work.Wait(); // block until chunk or sentinel

                        var chunk = currentChunk;
                        if (chunk == null || ct.IsCancellationRequested)
                        {
                            done.Release();
                            break;
                        }

                        long written = 0;
                        long start   = currentChunkStart;

                        for (int i = 0; i < chunk.Count; i++)
                        {
                            if (ct.IsCancellationRequested) break;
                            try
                            {
                                rs.AddNew();
                                var row = chunk[i];
                                for (int col = 0; col < columns.Count; col++)
                                {
                                    var val = CoerceValue(row[col], columns[col].NetType);
                                    if (val != DBNull.Value)
                                        rs.Fields[columns[col].Name].Value = val;
                                }
                                rs.Update();
                                written++;
                                if (written % ProgressInterval == 0)
                                    onProgress?.Invoke(totalWritten + written);
                            }
                            catch (Exception rowEx)
                            {
                                onRowError?.Invoke(start + i + 1, rowEx);
                            }
                        }

                        currentChunkWritten = written;
                        done.Release(); // signal main: chunk is done
                    }
                }
                catch (Exception ex)
                {
                    staException = ex;
                    done.Release(); // unblock main so it doesn't deadlock
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
            staThread.Start();

            try
            {
                while (totalAttempted < rowCount && !ct.IsCancellationRequested)
                {
                    // Buffer chunk from SQL Server
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

                    // Hand chunk to STA writer thread
                    currentChunk      = chunk;
                    currentChunkStart = totalAttempted - chunk.Count;
                    work.Release();

                    // Wait for STA to finish this chunk (async — doesn't block thread pool)
                    await done.WaitAsync(ct);

                    if (staException != null)
                    {
                        _logger.LogError(staException, "DAO write failed at row offset {Offset}", currentChunkStart);
                        throw staException;
                    }

                    totalWritten += currentChunkWritten;
                    onProgress?.Invoke(totalWritten);
                }
            }
            finally
            {
                // Send null sentinel regardless of success/cancel/exception
                currentChunk = null;
                try { work.Release(); } catch { }
                await Task.Run(() => staThread.Join()); // wait for STA to exit cleanly
            }

            if (staException != null) throw staException;

            _logger.LogInformation(
                "DAO write complete → {File} | Rows: {Written:N0} / Attempted: {Attempted:N0}",
                Path.GetFileName(filePath), totalWritten, totalAttempted);

            return totalWritten;
        }
        catch
        {
            // Don't leave a corrupt or partial .accdb file on disk
            if (fileCreated && File.Exists(filePath))
                try { File.Delete(filePath); } catch { }
            throw;
        }
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
