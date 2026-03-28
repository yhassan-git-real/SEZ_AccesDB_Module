using Serilog;
using Serilog.Events;

namespace SEZ_AccesDB_Module.Services.Logging;

/// <summary>
/// Configures 3 separate Serilog log files per session:
///   executionlog_{yyyyMMdd_HHmmss}.txt  — all INFO+ events (full trace)
///   successlog_{yyyyMMdd_HHmmss}.txt    — only marked success events
///   errorlog_{yyyyMMdd_HHmmss}.txt      — WARNING + ERROR + FATAL only
/// </summary>
public static class AppLogger
{
    private static ILogger? _instance;

    public static ILogger Instance =>
        _instance ?? throw new InvalidOperationException("Logger not configured. Call Configure() first.");

    /// <summary>Property name used to tag a log event as a SUCCESS record.</summary>
    public const string SuccessTagProperty = "IsSuccess";

    public static void Configure(string logDirectory, string minimumLevel = "Information")
    {
        if (!Directory.Exists(logDirectory))
            Directory.CreateDirectory(logDirectory);

        var level = minimumLevel.ToLower() switch
        {
            "debug"   => LogEventLevel.Debug,
            "warning" => LogEventLevel.Warning,
            "error"   => LogEventLevel.Error,
            _         => LogEventLevel.Information
        };

        var sessionStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        var execLogPath    = Path.Combine(logDirectory, $"executionlog_{sessionStamp}.txt");
        var successLogPath = Path.Combine(logDirectory, $"successlog_{sessionStamp}.txt");
        var errorLogPath   = Path.Combine(logDirectory, $"errorlog_{sessionStamp}.txt");

        const string execTemplate =
            "[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{Level,-11}]  {Message:lj}{NewLine}{Exception}";

        const string successTemplate =
            "[{Timestamp:yyyy-MM-dd HH:mm:ss}] [SUCCESS]  {Message:lj}{NewLine}";

        const string errorTemplate =
            "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━{NewLine}" +
            "[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{Level,-11}]  {Message:lj}{NewLine}" +
            "{Exception}" +
            "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━{NewLine}";

        _instance = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .Enrich.FromLogContext()

            // Console: concise friendly output — NO stack traces
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss}] {Message:lj}{NewLine}",
                restrictedToMinimumLevel: LogEventLevel.Warning)

            // executionlog: everything INFO+ (full trace)
            .WriteTo.File(
                execLogPath,
                outputTemplate: execTemplate,
                fileSizeLimitBytes: 200 * 1024 * 1024,
                rollOnFileSizeLimit: true)

            // successlog: only events tagged with IsSuccess=true
            .WriteTo.Logger(lc => lc
                .Filter.ByIncludingOnly(e =>
                    e.Properties.ContainsKey(SuccessTagProperty) &&
                    e.Properties[SuccessTagProperty].ToString() == "True")
                .WriteTo.File(successLogPath, outputTemplate: successTemplate))

            // errorlog: WARNING and above
            .WriteTo.Logger(lc => lc
                .Filter.ByIncludingOnly(e => e.Level >= LogEventLevel.Warning)
                .WriteTo.File(errorLogPath, outputTemplate: errorTemplate))

            .CreateLogger();

        Log.Logger = _instance;

        Log.Information("═══════════════════════════════════════════════════════════");
        Log.Information("  SEZ AccesDB Module — Session started: {Session}", sessionStamp);
        Log.Information("  Execution Log : {Path}", execLogPath);
        Log.Information("  Success  Log  : {Path}", successLogPath);
        Log.Information("  Error    Log  : {Path}", errorLogPath);
        Log.Information("═══════════════════════════════════════════════════════════");
    }

    /// <summary>
    /// Logs a success event that appears in both executionlog and successlog.
    /// NOTE: use \n not {NewLine} — {NewLine} is not a built-in Serilog token.
    /// </summary>
    public static void LogSuccess(
        string spName,
        string parameters,
        string processId,
        long rowsWritten,
        int filesCreated,
        TimeSpan duration,
        IEnumerable<SEZ_AccesDB_Module.Core.Models.OutputFileDetail> outputFiles)
    {
        var fileList = string.Join("\n                     ", outputFiles.Select(f => 
            $"{Path.GetFileName(f.Path)} (Table: {SEZ_AccesDB_Module.Core.Models.ExecutionResult.FormatBytes(f.TableSizeBytes)} | File: {SEZ_AccesDB_Module.Core.Models.ExecutionResult.FormatBytes(f.FileSizeBytes)})"));
        
        long totalTableBytes = outputFiles.Sum(f => f.TableSizeBytes);
        long totalFileBytes = outputFiles.Sum(f => f.FileSizeBytes);

        Log.ForContext(SuccessTagProperty, true)
           .Information(
               "✔  SP Execution SUCCEEDED\n" +
               "     Procedure   : {SP}\n" +
               "     Parameters  : {Params}\n" +
               "     Process ID  : {PID}\n" +
               "     Rows Written: {Rows}\n" +
               "     Table Size  : {TableSize}\n" +
               "     File Size   : {FileSize}\n" +
               "     Files       : {Files}\n" +
               "     Duration    : {Duration}",
               spName, parameters, processId, $"{rowsWritten:N0}", 
               SEZ_AccesDB_Module.Core.Models.ExecutionResult.FormatBytes(totalTableBytes), 
               SEZ_AccesDB_Module.Core.Models.ExecutionResult.FormatBytes(totalFileBytes), 
               fileList, FormatDuration(duration));
    }

    /// <summary>
    /// Logs a structured error event to executionlog and errorlog.
    /// NOTE: use \n not {NewLine} — {NewLine} is not a built-in Serilog token.
    /// </summary>
    public static void LogExecutionError(
        string spName,
        string parameters,
        string processId,
        string errorMessage,
        Exception? ex = null)
    {
        var logger = Log.ForContext("SpName", spName).ForContext("ProcessId", processId);

        if (ex != null)
            logger.Error(ex,
                "✘  SP Execution FAILED\n" +
                "     Procedure  : {SP}\n" +
                "     Parameters : {Params}\n" +
                "     Process ID : {PID}\n" +
                "     Error      : {Error}",
                spName, parameters, processId, errorMessage);
        else
            logger.Error(
                "✘  SP Execution FAILED\n" +
                "     Procedure  : {SP}\n" +
                "     Parameters : {Params}\n" +
                "     Process ID : {PID}\n" +
                "     Error      : {Error}",
                spName, parameters, processId, errorMessage);
    }

    public static void CloseAndFlush()
    {
        Log.Information("═══════════════════════════════════════════════════════════");
        Log.Information("  Session ended: {Time}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        Log.Information("═══════════════════════════════════════════════════════════");
        Log.CloseAndFlush();
    }

    private static string FormatDuration(TimeSpan ts) =>
        ts.TotalSeconds < 60
            ? $"{ts.TotalSeconds:F1}s"
            : $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
}
