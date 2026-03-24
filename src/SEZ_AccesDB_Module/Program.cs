
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using SEZ_AccesDB_Module.Core.Configuration;
using SEZ_AccesDB_Module.Core.Interfaces;
using SEZ_AccesDB_Module.Infrastructure.Access;
using SEZ_AccesDB_Module.Infrastructure.SqlServer;
using SEZ_AccesDB_Module.Services.Audit;
using SEZ_AccesDB_Module.Services.Etl;
using SEZ_AccesDB_Module.Services.FileManagement;
using SEZ_AccesDB_Module.Services.Logging;
using SEZ_AccesDB_Module.Services.Orchestration;
using Spectre.Console;

// Enable UTF-8 so emoji (✔ ✘ ✓ ⚠ etc.) render correctly in Windows console/PowerShell
Console.OutputEncoding = System.Text.Encoding.UTF8;

// ── 1. Load Configuration ───────────────────────────────────────────────────
var configBuilder = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json",  optional: false, reloadOnChange: false)
    .AddJsonFile("procedures.json",   optional: false, reloadOnChange: false)
    .Build();

var settings = new AppSettings();
configBuilder.Bind(settings);

// ── 2. Validate Configuration (Fatal Errors) ────────────────────────────────
if (string.IsNullOrWhiteSpace(settings.ConnectionStrings.SqlServer))
{
    AnsiConsole.MarkupLine("[red][[FATAL]][/] SQL Server connection string is missing in appsettings.json.");
    Environment.Exit(1);
}
if (string.IsNullOrWhiteSpace(settings.FileSettings.OutputPath))
{
    AnsiConsole.MarkupLine("[red][[FATAL]][/] FileSettings.OutputPath is missing in appsettings.json.");
    Environment.Exit(1);
}
if (settings.StoredProcedures.Count == 0)
{
    AnsiConsole.MarkupLine("[red][[FATAL]][/] No StoredProcedures found in procedures.json.");
    Environment.Exit(1);
}

// ── 3. Configure Logging (3 separate log files per session) ─────────────────
var logDirectory = string.IsNullOrWhiteSpace(settings.Logging.LogPath)
    ? Path.Combine(AppContext.BaseDirectory, "Logs")
    : settings.Logging.LogPath;

AppLogger.Configure(logDirectory, settings.Logging.MinimumLevel);
Log.Information("Loaded {Count} stored procedures from procedures.json", settings.StoredProcedures.Count);

// ── 4. Configure DI Container ───────────────────────────────────────────────
var services = new ServiceCollection();

services.AddLogging(lb =>
{
    lb.ClearProviders();
    lb.AddSerilog(Log.Logger, dispose: true);
});

services.AddSingleton(settings);
services.AddSingleton<ISqlDataAccess>(_ => new SqlDataAccess(settings.ConnectionStrings.SqlServer));
services.AddSingleton<IAccessDbWriter, AccessDbWriter>();
services.AddSingleton<IFileManager, FileManagerService>();
services.AddSingleton<IAuditService>(sp =>
    new AuditService(
        sp.GetRequiredService<ISqlDataAccess>(),
        settings.Audit.TableName,
        settings.Audit.Enabled,
        sp.GetRequiredService<ILogger<AuditService>>()));
services.AddSingleton<IEtlEngine, EtlEngineService>();
services.AddSingleton<OrchestratorService>();

var serviceProvider = services.BuildServiceProvider();

// ── 5. Run ───────────────────────────────────────────────────────────────────
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    AnsiConsole.MarkupLine("\n[yellow][[WARNING]][/] Ctrl+C detected — cancelling gracefully...");

    // Spectre.Console prompts never observe CancellationToken, so force exit after a grace period
    Task.Run(async () =>
    {
        await Task.Delay(1500);
        AppLogger.CloseAndFlush();
        Environment.Exit(0);
    });
};

try
{
    var orchestrator = serviceProvider.GetRequiredService<OrchestratorService>();
    await orchestrator.RunAsync(cts.Token);
}
catch (OperationCanceledException)
{
    AnsiConsole.MarkupLine("[grey]Execution cancelled.[/]");
}
catch (Exception ex)
{
    Log.Fatal(ex, "Unhandled fatal exception — application terminating. Error: {Message}", ex.Message);
    AnsiConsole.MarkupLine($"[red][[FATAL]][/] {Markup.Escape(ex.Message)}");
    Environment.Exit(1);
}
finally
{
    AppLogger.CloseAndFlush();
}
