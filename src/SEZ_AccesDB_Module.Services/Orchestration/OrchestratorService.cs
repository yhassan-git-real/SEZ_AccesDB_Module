using SEZ_AccesDB_Module.Core.Configuration;
using SEZ_AccesDB_Module.Core.Interfaces;
using SEZ_AccesDB_Module.Core.Models;
using SEZ_AccesDB_Module.Services.Logging;
using SEZ_AccesDB_Module.Services.UI;
using Microsoft.Extensions.Logging;
using Serilog;
using Spectre.Console;

namespace SEZ_AccesDB_Module.Services.Orchestration;

/// <summary>
/// Top-level orchestrator. Drives the full user interaction and ETL execution loop.
/// Execution flow per Requirement.md §4.1:
///   1. Connection test   2. SP selection   3. Parameter input
///   4. ETL execution     5. Logging        6. Audit   7. Summary
/// </summary>
public class OrchestratorService
{
    private readonly AppSettings _settings;
    private readonly ISqlDataAccess _sql;
    private readonly IEtlEngine _etl;
    private readonly IAuditService _audit;
    private readonly ILogger<OrchestratorService> _logger;

    public OrchestratorService(
        AppSettings settings,
        ISqlDataAccess sql,
        IEtlEngine etl,
        IAuditService audit,
        ILogger<OrchestratorService> logger)
    {
        _settings = settings;
        _sql      = sql;
        _etl      = etl;
        _audit    = audit;
        _logger   = logger;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        ConsoleUiService.ShowBanner();

        // ── Connection test ──────────────────────────────────────────────────
        AnsiConsole.MarkupLine("[cyan][[INFO]][/] Testing SQL Server connection to [bold]{0}[/]...",
            ExtractServerName(_settings.ConnectionStrings.SqlServer));

        bool connected = await _sql.TestConnectionAsync(ct);
        if (!connected)
        {
            const string msg = "Cannot connect to SQL Server. Verify the connection string in appsettings.json and ensure the server is reachable.";
            ConsoleUiService.ShowFatalError(msg);
            Log.Fatal("SQL Server connection FAILED. Check appsettings.json → ConnectionStrings.SqlServer");
            return;
        }
        AnsiConsole.MarkupLine("[green][[INFO]][/] SQL Server connection [bold]OK[/].");
        Log.Information("SQL Server connection established successfully.");

        var logDir = string.IsNullOrWhiteSpace(_settings.Logging.LogPath)
            ? Path.Combine(AppContext.BaseDirectory, "Logs")
            : _settings.Logging.LogPath;
        ConsoleUiService.ShowSessionInfo(_settings, logDir);

        // ── Ensure AuditLog table ────────────────────────────────────────────
        if (_settings.Audit.Enabled)
        {
            await _sql.EnsureAuditTableExistsAsync(_settings.Audit.TableName, ct);
            Log.Information("AuditLog table [{Table}] verified / created.", _settings.Audit.TableName);
        }

        // ── Main execution loop ──────────────────────────────────────────────
        bool continueLoop = true;
        while (continueLoop)
        {
            try
            {
                var menu       = new SpSelectionMenu(_settings.StoredProcedures);
                var selectedSp = menu.SelectProcedure();

                var paramHelper = new ParameterInputHelper();
                var parameters  = paramHelper.CollectParameters(selectedSp);
                var filePrefix  = paramHelper.CollectFileNamePrefix(selectedSp.FilePrefix);

                var context = new Core.Models.ExecutionContext
                {
                    SpDefinition     = selectedSp,
                    Parameters       = parameters,
                    OutputFilePrefix = filePrefix,
                    StartTime        = DateTime.Now
                };

                Log.Information(
                    "┌─ New Execution ─────────────────────────────────────────");
                Log.Information(
                    "│  Procedure  : {SP}", selectedSp.Name);
                Log.Information(
                    "│  Parameters : {Params}", context.ParameterSummary);
                Log.Information(
                    "│  Process ID : {PID}", context.ProcessId);
                Log.Information(
                    "│  Prefix     : {Prefix}  |  Output: {Dir}",
                    filePrefix, _settings.FileSettings.OutputPath);
                Log.Information(
                    "└────────────────────────────────────────────────────────");

                // ETL execution
                var result = await _etl.RunAsync(context, ct);

                // Only log success here — errors are already logged inside EtlEngineService catch blocks
                if (result.Success)
                {
                    AppLogger.LogSuccess(
                        spName:       selectedSp.Name,
                        parameters:   context.ParameterSummary,
                        processId:    context.ProcessId.ToString(),
                        rowsWritten:  result.TotalRowsRead,
                        filesCreated: result.TotalFilesCreated,
                        duration:     result.Duration,
                        outputFiles:  result.OutputFiles);
                }

                // Audit record
                await TryWriteAuditAsync(context, result, ct);

                // Console summary
                ConsoleUiService.ShowSummary(context, result);

                Log.Information(
                    "Execution complete — SP: {SP} | Status: {Status} | Rows: {Rows:N0} | Files: {Files} | Duration: {Dur}",
                    selectedSp.Name,
                    result.Success ? "SUCCESS" : "FAILED",
                    result.TotalRowsRead,
                    result.TotalFilesCreated,
                    result.Duration);

                AskContinue(ref continueLoop);
            }
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine("\n[grey]Exiting...[/]");
                Log.Information("Session exited by user.");
                break;
            }
        }
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────

    private async Task TryWriteAuditAsync(Core.Models.ExecutionContext ctx, ExecutionResult result, CancellationToken ct)
    {
        var record = new AuditRecord
        {
            StoreProcedureName = ctx.SpDefinition.Name,
            Parameter          = ctx.ParameterSummary,
            ProcessId          = ctx.ProcessId.ToString(),
            Date               = ctx.StartTime,
            FileNames          = result.FileNamesSummary,
            RowsCount          = result.TotalRowsRead,
            Message            = result.Success ? "Success" : result.ErrorMessage,
            Comment            = $"Duration: {result.DurationFormatted} | Files: {result.TotalFilesCreated} | Total Table Size: {result.TotalTableSizeBytesFormatted} | Total File Size: {result.TotalFileSizeBytesFormatted} | OutputDir: {_settings.FileSettings.OutputPath}"
        };
        await _audit.WriteAuditAsync(record, ct);
    }

    private static void AskContinue(ref bool continueLoop)
    {
        AnsiConsole.WriteLine();
        continueLoop = AnsiConsole.Confirm("[bold]Run another stored procedure?[/]", defaultValue: false);
    }

    private static string ExtractServerName(string connStr)
    {
        // Extract "Server=..." value for display
        foreach (var part in connStr.Split(';'))
        {
            var t = part.Trim();
            if (t.StartsWith("Server=", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
                return t.Split('=', 2)[1];
        }
        return "SQL Server";
    }
}
