using System.Data;
using Microsoft.Data.SqlClient;
using SEZ_AccesDB_Module.Core.Configuration;
using SEZ_AccesDB_Module.Core.Interfaces;
using SEZ_AccesDB_Module.Core.Models;
using SEZ_AccesDB_Module.Services.Logging;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace SEZ_AccesDB_Module.Services.Etl;

/// <summary>
/// Implements <see cref="IEtlEngine"/>: executes SP, reads data, splits to Access files.
/// Single-pass streaming: one IDataReader shared across all chunks (forward-only).
/// </summary>
public class EtlEngineService : IEtlEngine
{
    private readonly ISqlDataAccess _sql;
    private readonly IAccessDbWriter _accessWriter;
    private readonly IFileManager _fileManager;
    private readonly AppSettings _settings;
    private readonly ILogger<EtlEngineService> _logger;

    public EtlEngineService(
        ISqlDataAccess sql,
        IAccessDbWriter accessWriter,
        IFileManager fileManager,
        AppSettings settings,
        ILogger<EtlEngineService> logger)
    {
        _sql = sql;
        _accessWriter = accessWriter;
        _fileManager = fileManager;
        _settings = settings;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<ExecutionResult> RunAsync(Core.Models.ExecutionContext context, CancellationToken ct = default)
    {
        var result = new ExecutionResult();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var spDef = context.SpDefinition;

            // ── Step 1: Execute Stored Procedure ──────────────────────────────
            AnsiConsole.MarkupLine($"\n[cyan][[1/5]][/] Executing [bold]{spDef.Name}[/] | Parameters: [yellow]{context.ParameterSummary}[/]");
            _logger.LogInformation("[{Pid}] Executing SP: {SP} | Params: {Params}",
                context.ProcessId, spDef.Name, context.ParameterSummary);

            await _sql.ExecuteStoredProcedureAsync(spDef.Name, context.Parameters, ct);
            AnsiConsole.MarkupLine("[green]  ✓ Stored procedure executed successfully.[/]");

            // ── Step 2: Get Row Count from staging table ───────────────────────
            AnsiConsole.MarkupLine($"[cyan][[2/5]][/] Counting rows in [bold]{spDef.StagingTable}[/]...");
            long totalRows = await _sql.GetRowCountAsync(spDef.StagingTable, ct);
            result.TotalRowsRead = totalRows;

            _logger.LogInformation("[{Pid}] Staging table [{Table}] → {Rows:N0} rows", context.ProcessId, spDef.StagingTable, totalRows);
            AnsiConsole.MarkupLine($"[green]  ✓ Total rows: [bold yellow]{totalRows:N0}[/][/]");

            if (totalRows == 0)
            {
                AnsiConsole.MarkupLine("[yellow]  ⚠  Staging table is empty — no output files created.[/]");
                _logger.LogWarning("[{Pid}] Staging table empty — skipping export.", context.ProcessId);
                result.Warnings.Add("Staging table returned 0 rows.");
                result.Success = true;
                return result;
            }

            // ── Step 3: Compute File Splitting ─────────────────────────────────
            bool isImport = spDef.DataType.Equals("Import", StringComparison.OrdinalIgnoreCase);
            long threshold = isImport ? _settings.SplitThresholds.Import : _settings.SplitThresholds.Export;
            long chunkSize = _settings.SplitThresholds.ChunkSize;
            var chunks = _fileManager.ComputeChunks(totalRows, threshold, chunkSize);

            result.TotalFilesCreated = chunks.Count;
            AnsiConsole.MarkupLine($"[cyan][[3/5]][/] File split plan: [bold]{chunks.Count}[/] file(s) " +
                $"[grey](threshold: {threshold:N0} | chunk: {chunkSize:N0})[/]");

            if (chunks.Count > 1)
            {
                _logger.LogWarning("[{Pid}] Splitting into {N} files (rows={Rows:N0} > threshold={T:N0})",
                    context.ProcessId, chunks.Count, totalRows, threshold);
                foreach (var c in chunks)
                    AnsiConsole.MarkupLine($"  [grey]File {c.FileIndex}: rows {c.StartRow + 1:N0} – {c.EndRow + 1:N0} ({c.RowCount:N0} rows)[/]");
            }

            // ── Step 4: Ensure Output Directory ────────────────────────────────
            _fileManager.EnsureOutputDirectory(_settings.FileSettings.OutputPath);

            // ── Step 5: Stream data and write Access files ─────────────────────
            AnsiConsole.MarkupLine($"[cyan][[4/5]][/] Opening data stream from [{spDef.StagingTable}]...");
            var selectSql = $"SELECT * FROM [{spDef.StagingTable}]";

            using var reader = await _sql.GetDataReaderAsync(selectSql, ct);

            // Read schema ONCE before any rows are consumed
            var schema = reader.GetSchemaTable()
                ?? throw new InvalidOperationException($"Could not get schema from [{spDef.StagingTable}].");

            AnsiConsole.MarkupLine($"[cyan][[5/5]][/] Writing {chunks.Count} Access file(s)...\n");

            for (int i = 0; i < chunks.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = chunks[i];

                string filePath = chunks.Count == 1
                    ? _fileManager.GetSingleFilePath(_settings.FileSettings.OutputPath, context.OutputFilePrefix, context.StartTime, _settings.FileSettings.Extension)
                    : _fileManager.GetChunkFilePath(_settings.FileSettings.OutputPath, context.OutputFilePrefix, chunk.FileIndex, context.StartTime, _settings.FileSettings.Extension);

                _logger.LogInformation("[{Pid}] Writing file {N}/{Total}: {File} | Rows: {Rows:N0}",
                    context.ProcessId, i + 1, chunks.Count, filePath, chunk.RowCount);

                long fileRowsWritten = 0;
                long fileRowErrors = 0;

                // Spectre.Console progress for this chunk
                await AnsiConsole.Progress()
                    .AutoClear(false)
                    .HideCompleted(false)
                    .Columns(
                        new TaskDescriptionColumn(),
                        new ProgressBarColumn(),
                        new PercentageColumn(),
                        new SpinnerColumn())
                    .StartAsync(async progressCtx =>
                    {
                        var progressTask = progressCtx.AddTask(
                            $"File [cyan]{chunk.FileIndex}/{chunks.Count}[/] [grey]{Path.GetFileName(filePath)}[/]",
                            maxValue: chunk.RowCount);

                        // NOTE: reader is already positioned at start of this chunk
                        // (chunks are processed sequentially — no skipping needed)
                        fileRowsWritten = await _accessWriter.WriteChunkAsync(
                            filePath: filePath,
                            tableName: spDef.StagingTable,
                            reader: reader,
                            schema: schema,
                            rowCount: chunk.RowCount,
                            onProgress: written => progressTask.Value = written,
                            onRowError: (rowIdx, ex) =>
                            {
                                Interlocked.Increment(ref fileRowErrors);
                                _logger.LogWarning(ex, "[{Pid}] Row error at chunk-relative row {Row}", context.ProcessId, rowIdx);
                            },
                            ct: ct);

                        progressTask.Value = progressTask.MaxValue;
                    });

                result.OutputFilePaths.Add(filePath);
                result.SuccessFileCount++;

                var errNote = fileRowErrors > 0 ? $" [yellow]({fileRowErrors} row errors)[/]" : string.Empty;
                AnsiConsole.MarkupLine($"[green]  ✓ File {chunk.FileIndex}/{chunks.Count} → {Markup.Escape(Path.GetFileName(filePath))} ({fileRowsWritten:N0} rows){errNote}[/]");

                _logger.LogInformation("[{Pid}] File {N} done: {Path} | Written: {W:N0} | Errors: {E}",
                    context.ProcessId, i + 1, filePath, fileRowsWritten, fileRowErrors);
            }

            result.Success = true;
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.ErrorMessage = "Execution was cancelled by user.";
            _logger.LogWarning("ETL execution cancelled by user.");
        }
        catch (SqlException sqlEx)
        {
            // Translate SQL Server error codes into plain English for the console
            var friendly = FriendlySqlMessage(sqlEx);
            result.Success = false;
            result.ErrorMessage = friendly;
            result.ErrorFileCount++;

            // Full trace to log file only — console gets the friendly one-liner
            AppLogger.LogExecutionError(
                spName:       context.SpDefinition?.Name ?? "?",
                parameters:   context.ParameterSummary,
                processId:    context.ProcessId,
                errorMessage: friendly,
                ex:           sqlEx);

            AnsiConsole.WriteLine();
            AnsiConsole.Write(
                new Panel(Markup.Escape(friendly))
                    .Header("[bold red]  ✘ Database Error  [/]")
                    .Border(BoxBorder.Rounded)
                    .BorderColor(Color.Red));
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            result.ErrorFileCount++;

            AppLogger.LogExecutionError(
                spName:       context.SpDefinition?.Name ?? "?",
                parameters:   context.ParameterSummary,
                processId:    context.ProcessId,
                errorMessage: ex.Message,
                ex:           ex);

            AnsiConsole.WriteLine();
            AnsiConsole.Write(
                new Panel(Markup.Escape(ex.Message))
                    .Header("[bold red]  ✘ Unexpected Error  [/]")
                    .Border(BoxBorder.Rounded)
                    .BorderColor(Color.Red));
        }
        finally
        {
            sw.Stop();
            result.Duration = sw.Elapsed;
        }

        return result;
    }
    // ─── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Translates a SqlException into a single plain-English sentence suitable for console display.
    /// The full exception (stack trace, error number) still goes to the error log file.
    /// </summary>
    private static string FriendlySqlMessage(SqlException ex)
    {
        // SQL error numbers: https://learn.microsoft.com/sql/relational-databases/errors-events/database-engine-events-and-errors
        return ex.Number switch
        {
            // Object / table not found
            208 => $"SQL Server could not find the object referenced in the stored procedure. " +
                   $"Details: {ex.Message} — Ensure all lookup tables (e.g. PORTMST) exist in the database.",

            // Permission denied
            229 or 230 => $"Permission denied on database object. Check that the SQL login has sufficient rights. Details: {ex.Message}",

            // Login / auth
            18456 => "SQL Server login failed. Verify the connection string credentials in appsettings.json.",

            // Timeout
            -2 or 258 => "The stored procedure took too long and timed out. Try a narrower date range, or the server is under heavy load.",

            // Deadlock
            1205 => "SQL Server deadlock detected. Please retry the operation.",

            // Null / constraint
            515 => $"A required column value is NULL. The stored procedure tried to insert a NULL into a NOT NULL column. Details: {ex.Message}",
            547 => $"A foreign key or constraint violation occurred. Details: {ex.Message}",

            // Generic fallback — show just the message, never the stack trace
            _ => $"SQL Server error ({ex.Number}): {ex.Message}"
        };
    }
}
