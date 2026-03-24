using System.Data;
using Microsoft.Data.SqlClient;
using SEZ_AccesDB_Module.Core.Configuration;
using SEZ_AccesDB_Module.Core.Interfaces;
using SEZ_AccesDB_Module.Core.Models;
using SEZ_AccesDB_Module.Services.Logging;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using ExecutionContext = SEZ_AccesDB_Module.Core.Models.ExecutionContext;

namespace SEZ_AccesDB_Module.Services.Etl;

/// <summary>
/// Orchestrates the ETL pipeline for a single stored procedure execution.
/// Flow: Execute SP → Detect sub-tables → Write one Access file per source table.
/// </summary>
public class EtlEngineService : IEtlEngine
{
    private readonly ISqlDataAccess          _sql;
    private readonly IAccessDbWriter         _accessWriter;
    private readonly IFileManager            _fileManager;
    private readonly AppSettings             _settings;
    private readonly ILogger<EtlEngineService> _logger;

    public EtlEngineService(
        ISqlDataAccess sql,
        IAccessDbWriter accessWriter,
        IFileManager fileManager,
        AppSettings settings,
        ILogger<EtlEngineService> logger)
    {
        _sql          = sql;
        _accessWriter = accessWriter;
        _fileManager  = fileManager;
        _settings     = settings;
        _logger       = logger;
    }

    /// <inheritdoc/>
    public async Task<ExecutionResult> RunAsync(ExecutionContext context, CancellationToken ct = default)
    {
        var result = new ExecutionResult();
        var sw     = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var spDef = context.SpDefinition;

            // ── Step 1: Execute Stored Procedure ──────────────────────────────
            AnsiConsole.MarkupLine($"\n[cyan][[1/5]][/] Executing [bold]{Markup.Escape(spDef.Name)}[/] | Parameters: [yellow]{Markup.Escape(context.ParameterSummary)}[/]");
            _logger.LogInformation("[{Pid}] Executing SP: {SP} | Params: {Params}",
                context.ProcessId, spDef.Name, context.ParameterSummary);

            await _sql.ExecuteStoredProcedureAsync(spDef.Name, context.Parameters, ct);
            AnsiConsole.MarkupLine("[green]  ✓ Stored procedure executed successfully.[/]");

            // ── Step 2: Verify staging table has rows ──────────────────────────
            AnsiConsole.MarkupLine($"[cyan][[2/5]][/] Counting rows in [bold]{Markup.Escape(spDef.StagingTable)}[/]...");
            long totalRows = await _sql.GetRowCountAsync(spDef.StagingTable, ct);

            _logger.LogInformation("[{Pid}] Staging table [{Table}] → {Rows:N0} rows",
                context.ProcessId, spDef.StagingTable, totalRows);
            AnsiConsole.MarkupLine($"[green]  ✓ Total rows: [bold yellow]{totalRows:N0}[/][/]");

            if (totalRows == 0)
            {
                AnsiConsole.MarkupLine("[yellow]  ⚠  Staging table is empty — no output files created.[/]");
                _logger.LogWarning("[{Pid}] Staging table empty — skipping export.", context.ProcessId);
                result.Warnings.Add("Staging table returned 0 rows.");
                result.Success = true;
                return result;
            }

            // ── Step 3: Detect SP-created sub-tables (dynamic, no hardcoded names) ──
            //   The SP may produce {StagingTable}_1, {StagingTable}_2 ... when row count
            //   exceeds the SP's internal threshold. We detect these at runtime.
            AnsiConsole.MarkupLine($"[cyan][[3/5]][/] Detecting SP sub-tables for [bold]{Markup.Escape(spDef.StagingTable)}[/]...");
            var sourceTables = await ResolveSourceTablesAsync(spDef.StagingTable, ct);

            result.TotalFilesCreated = sourceTables.Count;

            if (sourceTables.Count == 1 && sourceTables[0] == spDef.StagingTable)
            {
                AnsiConsole.MarkupLine($"  [grey]No sub-tables found — writing single file from [[{Markup.Escape(spDef.StagingTable)}]].[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"  [yellow]SP created {sourceTables.Count} sub-table(s) → will write {sourceTables.Count} file(s):[/]");
                foreach (var t in sourceTables)
                    AnsiConsole.MarkupLine($"  [grey]  • {Markup.Escape(t)}[/]");
                _logger.LogInformation("[{Pid}] SP sub-tables detected: {Tables}",
                    context.ProcessId, string.Join(", ", sourceTables));
            }

            // ── Step 4: Ensure Output Directory ────────────────────────────────
            _fileManager.EnsureOutputDirectory(_settings.FileSettings.OutputPath);

            // ── Step 5: For each source table → open fresh reader → write one Access file ─
            AnsiConsole.MarkupLine($"[cyan][[4/5]][/] Writing {sourceTables.Count} Access file(s)...\n");

            for (int i = 0; i < sourceTables.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var sourceTable = sourceTables[i];
                var fileIndex   = i + 1;

                // File naming follows the defined convention:
                //   1 file  → {prefix}_{ddMMyyyy}.accdb
                //   N files → {prefix}_{N}_{ddMMyyyy}.accdb
                var filePath = sourceTables.Count == 1
                    ? _fileManager.GetSingleFilePath(_settings.FileSettings.OutputPath, context.OutputFilePrefix, context.StartTime, _settings.FileSettings.Extension)
                    : _fileManager.GetChunkFilePath(_settings.FileSettings.OutputPath, context.OutputFilePrefix, fileIndex, context.StartTime, _settings.FileSettings.Extension);

                // Row count for this specific source table
                var tableRows = await _sql.GetRowCountAsync(sourceTable, ct);

                _logger.LogInformation("[{Pid}] File {N}/{Total}: [{Src}] → {File} ({Rows:N0} rows)",
                    context.ProcessId, fileIndex, sourceTables.Count, sourceTable, filePath, tableRows);

                AnsiConsole.MarkupLine($"[cyan][[5/5 – {fileIndex}/{sourceTables.Count}]][/] " +
                    $"[bold]{Markup.Escape(Path.GetFileName(filePath))}[/] " +
                    $"← [grey]{Markup.Escape(sourceTable)}[/] ({tableRows:N0} rows)");

                long fileRowsWritten = 0;
                long fileRowErrors   = 0;

                // Fresh reader opened per file — avoids complex row-skipping
                var selectSql = $"SELECT * FROM [{sourceTable}]";
                using var reader = await _sql.GetDataReaderAsync(selectSql, ct);

                var schema = reader.GetSchemaTable()
                    ?? throw new InvalidOperationException(
                        $"Could not get schema from [{sourceTable}].");

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
                            $"File [cyan]{fileIndex}/{sourceTables.Count}[/] [grey]{Markup.Escape(Path.GetFileName(filePath))}[/]",
                            maxValue: tableRows > 0 ? tableRows : 1);

                        fileRowsWritten = await _accessWriter.WriteChunkAsync(
                            filePath:   filePath,
                            tableName:  spDef.StagingTable,   // Access table name = base table (consistent naming)
                            reader:     reader,
                            schema:     schema,
                            rowCount:   tableRows,
                            onProgress: written => progressTask.Value = written,
                            onRowError: (rowIdx, ex) =>
                            {
                                Interlocked.Increment(ref fileRowErrors);
                                _logger.LogWarning(ex, "[{Pid}] Row error at row {Row} in [{Src}]",
                                    context.ProcessId, rowIdx, sourceTable);
                            },
                            ct: ct);

                        progressTask.Value = progressTask.MaxValue;
                    });

                result.TotalRowsRead    += fileRowsWritten;
                result.OutputFilePaths.Add(filePath);
                result.SuccessFileCount++;

                var errNote = fileRowErrors > 0
                    ? $" [yellow]({fileRowErrors} row errors)[/]"
                    : string.Empty;
                AnsiConsole.MarkupLine(
                    $"[green]  ✓ {Markup.Escape(Path.GetFileName(filePath))} — {fileRowsWritten:N0} rows written{errNote}[/]");

                _logger.LogInformation("[{Pid}] File {N} done: {Path} | Written: {W:N0} | Errors: {E}",
                    context.ProcessId, fileIndex, filePath, fileRowsWritten, fileRowErrors);
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
            var friendly = FriendlySqlMessage(sqlEx);
            result.Success = false;
            result.ErrorMessage = friendly;
            result.ErrorFileCount++;

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
    /// Detects SP-created sub-tables dynamically by checking for
    /// {baseTable}_1, {baseTable}_2 ... up to _9.
    /// Returns the list of sub-tables if any exist, otherwise returns
    /// a list containing only the base staging table.
    /// No table names are hardcoded — names are always derived from StagingTable.
    /// </summary>
    private async Task<List<string>> ResolveSourceTablesAsync(string baseTable, CancellationToken ct)
    {
        var subTables = new List<string>();

        for (int i = 1; i <= 9; i++)
        {
            var candidate = $"{baseTable}_{i}";
            if (await _sql.TableExistsAsync(candidate, ct))
                subTables.Add(candidate);
            else
                break; // stop at first missing index — no gaps expected
        }

        // If no sub-tables found, fall back to the main staging table
        return subTables.Count > 0 ? subTables : new List<string> { baseTable };
    }

    /// <summary>
    /// Translates a SqlException into a plain-English sentence for console display.
    /// Full stack trace still goes to errorlog file.
    /// </summary>
    private static string FriendlySqlMessage(SqlException ex) =>
        ex.Number switch
        {
            208         => $"SQL Server could not find the object referenced in the stored procedure. " +
                           $"Details: {ex.Message} — Ensure all lookup tables exist in the database.",
            229 or 230  => $"Permission denied on database object. Check that the SQL login has sufficient rights. Details: {ex.Message}",
            18456       => "SQL Server login failed. Verify the connection string credentials in appsettings.json.",
            -2 or 258   => "The stored procedure took too long and timed out. Try a narrower date range, or the server is under heavy load.",
            1205        => "SQL Server deadlock detected. Please retry the operation.",
            515         => $"A required column value is NULL. Details: {ex.Message}",
            547         => $"A foreign key or constraint violation occurred. Details: {ex.Message}",
            _           => $"SQL Server error ({ex.Number}): {ex.Message}"
        };
}
