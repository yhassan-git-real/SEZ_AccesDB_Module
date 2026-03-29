using System.Data;
using Microsoft.Data.SqlClient;
using SEZ_AccesDB_Module.Core.Configuration;
using SEZ_AccesDB_Module.Core.Interfaces;
using SEZ_AccesDB_Module.Core.Models;
using SEZ_AccesDB_Module.Services.Logging;
using SEZ_AccesDB_Module.Services.UI;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using ExecutionContext = SEZ_AccesDB_Module.Core.Models.ExecutionContext;

namespace SEZ_AccesDB_Module.Services.Etl;

/// <summary>
/// Orchestrates the ETL pipeline: Execute SP → Detect sub-tables → Write one Access file per source table.
/// </summary>
public class EtlEngineService : IEtlEngine
{
    private readonly ISqlDataAccess          _sql;
    private readonly IAccessDbWriter         _accessWriter;
    private readonly IFileManager            _fileManager;
    private readonly IThresholdValidator     _validator;
    private readonly AppSettings             _settings;
    private readonly ILogger<EtlEngineService> _logger;

    public EtlEngineService(
        ISqlDataAccess sql,
        IAccessDbWriter accessWriter,
        IFileManager fileManager,
        IThresholdValidator validator,
        AppSettings settings,
        ILogger<EtlEngineService> logger)
    {
        _sql          = sql;
        _accessWriter = accessWriter;
        _fileManager  = fileManager;
        _validator    = validator;
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

            // ── Step 1: Execute Stored Procedure ───────────────────────────────
            AnsiConsole.MarkupLine($"\n[cyan][[1/5]][/] Executing [bold]{Markup.Escape(spDef.Name)}[/] | Parameters: [yellow]{Markup.Escape(context.ParameterSummary)}[/]");
            _logger.LogInformation("[{Pid}] Executing SP: {SP} | Params: {Params}",
                context.ProcessId, spDef.Name, context.ParameterSummary);

            var phaseWatch = System.Diagnostics.Stopwatch.StartNew();
            await _sql.ExecuteStoredProcedureAsync(spDef.Name, context.Parameters, ct);
            result.SpExecutionTime = phaseWatch.Elapsed;
            AnsiConsole.MarkupLine($"[green]  ✓ Stored procedure executed successfully.[/] [grey]({FormatDuration(result.SpExecutionTime)})[/]");

            // ── Step 2: Verify staging table has rows ────────────────────────────
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

            // ── Step 3: Detect SP-created sub-tables ─────────────────────────────
            // SPs exceeding their row threshold split data into {StagingTable}_1, _2 ...
            AnsiConsole.MarkupLine($"[cyan][[3/6]][/] Detecting SP sub-tables for [bold]{Markup.Escape(spDef.StagingTable)}[/]...");
            var sourceTables = await ResolveSourceTablesAsync(spDef.StagingTable, ct);

            if (sourceTables.Count == 1 && sourceTables[0] == spDef.StagingTable)
            {
                AnsiConsole.MarkupLine($"  [grey]No sub-tables found — writing single file from [[{Markup.Escape(spDef.StagingTable)}]].[/]");

                // Single-table threshold warning: catch cases where the SP split logic didn't fire
                if (spDef.Threshold > 0 && totalRows > spDef.Threshold)
                {
                    AnsiConsole.MarkupLine(
                        $"[yellow]  ⚠  Warning: Staging table [{Markup.Escape(spDef.StagingTable)}] has {totalRows:N0} rows " +
                        $"which exceeds the threshold of {spDef.Threshold:N0}. SP split logic did not trigger.[/]");
                    _logger.LogWarning(
                        "[{Pid}] Single staging table [{Table}] has {Rows:N0} rows exceeding threshold {Threshold:N0} but SP did not split.",
                        context.ProcessId, spDef.StagingTable, totalRows, spDef.Threshold);
                    result.Warnings.Add(
                        $"Staging table [{spDef.StagingTable}] has {totalRows:N0} rows (threshold: {spDef.Threshold:N0}). " +
                        "SP split logic did not trigger — Access file may be oversized.");
                }
            }
            else
            {
                AnsiConsole.MarkupLine($"  [yellow]SP created {sourceTables.Count} sub-table(s) → will write {sourceTables.Count} file(s):[/]");
                foreach (var t in sourceTables)
                    AnsiConsole.MarkupLine($"  [grey]  • {Markup.Escape(t)}[/]");
                _logger.LogInformation("[{Pid}] SP sub-tables detected: {Tables}",
                    context.ProcessId, string.Join(", ", sourceTables));
            }

            // ── Step 4: Threshold Validation ─────────────────────────────────────
            // Validate split table row counts against the configured threshold.
            // Only activates when sub-tables exist AND a threshold is configured.
            if (sourceTables.Count > 1 && spDef.Threshold > 0)
            {
                AnsiConsole.MarkupLine($"[cyan][[4/6]][/] Validating split table row counts against threshold [bold yellow]{spDef.Threshold:N0}[/]...");

                var validation = await _validator.ValidateAsync(spDef, sourceTables, ct);

                if (validation.HasViolations)
                {
                    _logger.LogWarning(
                        "[{Pid}] Threshold violations detected: {V}/{T} table(s) exceed {Threshold:N0}",
                        context.ProcessId, validation.ViolationCount, validation.Tables.Count, spDef.Threshold);

                    var action = ConsoleUiService.PromptThresholdAction(validation);

                    _logger.LogInformation("[{Pid}] User threshold action: {Action}", context.ProcessId, action);

                    switch (action)
                    {
                        case ThresholdAction.CancelOperation:
                            result.Success = false;
                            result.ErrorMessage = "Operation cancelled by user due to threshold violations.";
                            _logger.LogWarning("[{Pid}] ETL cancelled by user — threshold violations.", context.ProcessId);
                            return result;

                        case ThresholdAction.ProcessValidOnly:
                            var skippedTables = validation.Tables
                                .Where(t => t.ExceedsThreshold)
                                .Select(t => t.TableName)
                                .ToList();
                            sourceTables = validation.Tables
                                .Where(t => !t.ExceedsThreshold)
                                .Select(t => t.TableName)
                                .ToList();

                            if (sourceTables.Count == 0)
                            {
                                result.Success = false;
                                result.ErrorMessage = "All split tables exceed the threshold — no tables to process.";
                                _logger.LogWarning("[{Pid}] All tables exceed threshold — nothing to process.", context.ProcessId);
                                return result;
                            }

                            var skippedMsg = $"Skipped {skippedTables.Count} table(s) exceeding threshold: {string.Join(", ", skippedTables)}";
                            result.Warnings.Add(skippedMsg);
                            _logger.LogInformation("[{Pid}] {Msg}", context.ProcessId, skippedMsg);
                            AnsiConsole.MarkupLine($"[yellow]  ⚠  {Markup.Escape(skippedMsg)}[/]");
                            break;

                        case ThresholdAction.ProcessAll:
                            var overrideMsg = "User chose to process all tables despite threshold violations.";
                            result.Warnings.Add(overrideMsg);
                            _logger.LogInformation("[{Pid}] {Msg}", context.ProcessId, overrideMsg);
                            break;
                    }
                }
                else
                {
                    AnsiConsole.MarkupLine("[green]  ✓ All split tables are within the threshold.[/]");
                }
            }

            result.TotalFilesCreated = sourceTables.Count;

            // ── Step 5: Ensure Output Directory ──────────────────────────────────
            _fileManager.EnsureOutputDirectory(_settings.FileSettings.OutputPath);

            // ── Step 6: Stream + write one Access file per source table ───────────
            AnsiConsole.MarkupLine($"[cyan][[5/6]][/] Writing {sourceTables.Count} Access file(s)...\n");
            phaseWatch.Restart();

            for (int i = 0; i < sourceTables.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var sourceTable = sourceTables[i];
                var fileIndex   = i + 1;

                var filePath = sourceTables.Count == 1
                    ? _fileManager.GetSingleFilePath(_settings.FileSettings.OutputPath, context.OutputFilePrefix, context.StartTime, _settings.FileSettings.Extension)
                    : _fileManager.GetChunkFilePath(_settings.FileSettings.OutputPath, context.OutputFilePrefix, fileIndex, context.StartTime, _settings.FileSettings.Extension);

                // Reuse the count already fetched in Step 2 for the base staging table;
                // only query again for SP-created sub-tables (different tables).
                var tableRows = (sourceTables.Count == 1)
                    ? totalRows
                    : await _sql.GetRowCountAsync(sourceTable, ct);

                _logger.LogInformation("[{Pid}] File {N}/{Total}: [{Src}] → {File} ({Rows:N0} rows)",
                    context.ProcessId, fileIndex, sourceTables.Count, sourceTable, filePath, tableRows);

                AnsiConsole.MarkupLine($"[cyan][[6/6 – {fileIndex}/{sourceTables.Count}]][/] " +
                    $"[bold]{Markup.Escape(Path.GetFileName(filePath))}[/] " +
                    $"← [grey]{Markup.Escape(sourceTable)}[/] ({tableRows:N0} rows)");

                long fileRowsWritten = 0;
                long fileRowErrors   = 0;

                var selectSql = $"SELECT * FROM [{sourceTable}]";
                using var reader = await _sql.GetDataReaderAsync(selectSql, ct);

                var schema = reader.GetSchemaTable()
                    ?? throw new InvalidOperationException($"Could not get schema from [{sourceTable}].");

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
                            tableName:  sourceTables.Count == 1
                                ? context.OutputFilePrefix
                                : $"{context.OutputFilePrefix}_{fileIndex}",
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

                var tableSizeBytes = await _sql.GetTableSizeBytesAsync(sourceTable, ct);
                var fileSizeBytes = new FileInfo(filePath).Length;

                result.TotalRowsRead    += fileRowsWritten;
                result.OutputFiles.Add(new OutputFileDetail(filePath, sourceTable, fileRowsWritten, tableSizeBytes, fileSizeBytes));
                result.SuccessFileCount++;

                var errNote = fileRowErrors > 0
                    ? $" [yellow]({fileRowErrors} row errors)[/]"
                    : string.Empty;
                AnsiConsole.MarkupLine(
                    $"[green]  ✓ {Markup.Escape(Path.GetFileName(filePath))} — {fileRowsWritten:N0} rows written{errNote}[/]");

                _logger.LogInformation("[{Pid}] File {N} done: {Path} | Written: {W:N0} | Errors: {E}",
                    context.ProcessId, fileIndex, filePath, fileRowsWritten, fileRowErrors);
            }

            result.WriteTime = phaseWatch.Elapsed;
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
            var friendly = FriendlyGeneralMessage(ex);
            result.Success = false;
            result.ErrorMessage = friendly;
            result.ErrorFileCount++;

            AppLogger.LogExecutionError(
                spName:       context.SpDefinition?.Name ?? "?",
                parameters:   context.ParameterSummary,
                processId:    context.ProcessId,
                errorMessage: friendly,
                ex:           ex);

            AnsiConsole.WriteLine();
            AnsiConsole.Write(
                new Panel(Markup.Escape(friendly))
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

    private static string FormatDuration(TimeSpan ts) =>
        ts.TotalMinutes >= 1
            ? $"{(int)ts.TotalMinutes}m {ts.Seconds:D2}s"
            : $"{ts.TotalSeconds:F1}s";

    /// <summary>
    /// Checks for SP-created sub-tables ({baseTable}_1, _2 ... up to _9).
    /// Returns sub-tables if found, otherwise returns a list with only the base table.
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
                break;
        }

        return subTables.Count > 0 ? subTables : new List<string> { baseTable };
    }

    /// <summary>
    /// Maps SQL error numbers to user-friendly messages for console display.
    /// Full stack trace is still written to the error log file.
    /// </summary>
    private static string FriendlySqlMessage(SqlException ex) =>
        ex.Number switch
        {
            208         => $"SQL Server could not find a referenced database object. " +
                           $"Check that the StagingTable in procedures.json is correct, and verify the procedure structure for missing tables. Details: {ex.Message}",
            229 or 230  => $"Permission denied on database object. Check that the SQL login has sufficient rights. Details: {ex.Message}",
            18456       => "SQL Server login failed. Verify the connection string credentials in appsettings.json.",
            -2 or 258   => "The stored procedure took too long and timed out. Try a narrower date range, or the server is under heavy load.",
            1205        => "SQL Server deadlock detected. Please retry the operation.",
            515         => $"A required column value is NULL. Details: {ex.Message}",
            547         => $"A foreign key or constraint violation occurred. Details: {ex.Message}",
            _           => $"SQL Server error ({ex.Number}): {ex.Message}"
        };

    /// <summary>
    /// Unrolls generic nested exceptions (like AggregateException) to extract readable inner messages.
    /// </summary>
    private static string FriendlyGeneralMessage(Exception ex)
    {
        var messages = new List<string>();
        var current = ex;
        while (current != null)
        {
            var msg = current.Message.Trim();
            if (!messages.Contains(msg) && 
                !msg.Contains("One or more errors occurred") &&
                !msg.Contains("See the inner exception for details"))
            {
                messages.Add(msg);
            }
            current = current.InnerException;
        }
        return messages.Count > 0 ? string.Join(" — ", messages) : "An unknown error occurred.";
    }
}
