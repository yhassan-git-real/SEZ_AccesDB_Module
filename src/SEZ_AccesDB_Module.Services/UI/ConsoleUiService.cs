using SEZ_AccesDB_Module.Core.Configuration;
using SEZ_AccesDB_Module.Core.Models;
using Spectre.Console;

namespace SEZ_AccesDB_Module.Services.UI;

/// <summary>
/// Renders all console UI: banner, session info, summary table, error panels.
/// Uses Spectre.Console for word-wrapped, formatted output.
/// </summary>
public static class ConsoleUiService
{
    /// <summary>
    /// Shows the application FigletText banner.
    /// </summary>
    public static void ShowBanner()
    {
        AnsiConsole.Write(
            new FigletText("SEZ Module")
                .Centered()
                .Color(Color.Cyan1));
        AnsiConsole.MarkupLine("[grey]SQL Server → Microsoft Access ETL System   |   .NET 8[/]");
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Displays a session info panel: server, database, user, log path, output path.
    /// </summary>
    public static void ShowSessionInfo(AppSettings settings, string logDirectory)
    {
        ParseConnectionString(settings.ConnectionStrings.SqlServer,
            out var server, out var database, out var user);

        var grid = new Grid().AddColumn().AddColumn();

        grid.AddRow("[grey]SQL Server[/]",   $"[bold white]{Markup.Escape(server)}[/]");
        grid.AddRow("[grey]Database[/]",     $"[bold white]{Markup.Escape(database)}[/]");
        grid.AddRow("[grey]Auth[/]",         string.IsNullOrEmpty(user)
                                                ? "[green]Windows Authentication[/]"
                                                : $"[yellow]SQL Login: {Markup.Escape(user)}[/]");
        grid.AddRow("[grey]Output Path[/]",  $"[dim]{Markup.Escape(settings.FileSettings.OutputPath)}[/]");
        grid.AddRow("[grey]Log Path[/]",     $"[dim]{Markup.Escape(logDirectory)}[/]");
        grid.AddRow("[grey]Procedures[/]",   $"{settings.StoredProcedures.Count} loaded from procedures.json");

        var panel = new Panel(grid)
            .Header("[bold cyan]  Session Info  [/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey);

        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Displays a formatted word-wrapped summary table after execution completes.
    /// </summary>
    public static void ShowSummary(Core.Models.ExecutionContext ctx, ExecutionResult result)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold]Execution Summary[/]").RuleStyle("grey").Centered());
        AnsiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Expand()
            .AddColumn(new TableColumn("[bold grey]Property[/]").RightAligned().Width(18))
            .AddColumn(new TableColumn("[bold white]Value[/]"));          // auto width = word wraps

        table.AddRow("Stored Procedure", $"[cyan]{Markup.Escape(ctx.SpDefinition.Name)}[/]");
        table.AddRow("Parameters",       Markup.Escape(ctx.ParameterSummary));
        table.AddRow("Process ID",       $"[bold]{Markup.Escape(ctx.ProcessId)}[/]");
        table.AddRow("Start Time",       ctx.StartTime.ToString("yyyy-MM-dd HH:mm:ss"));
        table.AddRow("Total Duration",   $"[bold]{FormatDuration(result.Duration)}[/]");

        // ── Phase breakdown ────────────────────────────────────────────────────
        table.AddRow("[grey]  SP Execution[/]", result.SpExecutionTime > TimeSpan.Zero
            ? $"[grey]{FormatDuration(result.SpExecutionTime)}[/]"
            : "[grey]—[/]");
        table.AddRow("[grey]  Data Load & Write[/]", result.WriteTime > TimeSpan.Zero
            ? $"[grey]{FormatDuration(result.WriteTime)}[/]"
            : "[grey]—[/]");
        table.AddRow("[grey]  Overhead (other)[/]", result.SpExecutionTime > TimeSpan.Zero && result.WriteTime > TimeSpan.Zero
            ? $"[grey]{FormatDuration(result.Duration - result.SpExecutionTime - result.WriteTime)}[/]"
            : "[grey]—[/]");

        // ── Throughput ─────────────────────────────────────────────────────────
        table.AddRow("Total Rows Read",  $"[yellow]{result.TotalRowsRead:N0}[/]");
        if (result.RowsPerSecond > 0)
            table.AddRow("[grey]  Write Speed[/]",  $"[grey]{result.RowsPerSecond:N0} rows/sec[/]");

        table.AddRow("Total Files",      result.TotalFilesCreated.ToString());
        table.AddRow("Success Files",    $"[green]{result.SuccessFileCount}[/]");
        table.AddRow("Error Files",      result.ErrorFileCount > 0 ? $"[red]{result.ErrorFileCount}[/]" : "0");
        table.AddRow("Status",           result.Success ? "[bold green]SUCCESS[/]" : "[bold red]FAILED[/]");

        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            // Word-wrap error text to 80 chars with Spectre markup
            var msgLines = WordWrap(result.ErrorMessage, 80);
            table.AddRow("[red]Error[/]", $"[red]{Markup.Escape(msgLines)}[/]");
        }

        AnsiConsole.Write(table);

        // Output files list
        if (result.OutputFilePaths.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Output Files:[/]");
            foreach (var f in result.OutputFilePaths)
                AnsiConsole.MarkupLine($"  [green]✓[/] {Markup.Escape(f)}");
        }

        // Warnings
        if (result.Warnings.Count > 0)
        {
            AnsiConsole.WriteLine();
            foreach (var w in result.Warnings)
                AnsiConsole.MarkupLine($"  [yellow]⚠  {Markup.Escape(w)}[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(result.Success
            ? new Panel("[bold green]Execution completed successfully.[/]")
                .Border(BoxBorder.Rounded).BorderColor(Color.Green)
            : new Panel("[bold red]Execution completed with errors. Check errorlog_*.txt in the Logs folder.[/]")
                .Border(BoxBorder.Rounded).BorderColor(Color.Red));
        AnsiConsole.WriteLine();
    }

    /// <summary>Displays a fatal error panel.</summary>
    public static void ShowFatalError(string message)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(
            new Panel($"[bold red]FATAL:[/] {Markup.Escape(WordWrap(message, 80))}")
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.Red));
        AnsiConsole.WriteLine();
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Formats a TimeSpan as "Xm Ys" (>= 1 min) or "X.Xs" (< 1 min).</summary>
    private static string FormatDuration(TimeSpan ts) =>
        ts.TotalMinutes >= 1
            ? $"{(int)ts.TotalMinutes}m {ts.Seconds:D2}s"
            : $"{ts.TotalSeconds:F1}s";

    /// <summary>Wraps text at the given column width, preserving whole words.</summary>
    private static string WordWrap(string text, int maxWidth)
    {
        if (text.Length <= maxWidth) return text;

        var words  = text.Split(' ');
        var lines  = new List<string>();
        var current = string.Empty;

        foreach (var word in words)
        {
            if (current.Length + word.Length + 1 > maxWidth && current.Length > 0)
            {
                lines.Add(current);
                current = word;
            }
            else
            {
                current = current.Length == 0 ? word : $"{current} {word}";
            }
        }
        if (current.Length > 0) lines.Add(current);

        return string.Join("\n                   ", lines);   // indent continuation lines
    }

    /// <summary>Parses common ADO.NET connection string tokens.</summary>
    private static void ParseConnectionString(string connStr,
        out string server, out string database, out string user)
    {
        server   = "?";
        database = "?";
        user     = string.Empty;

        foreach (var part in connStr.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            var key = kv[0].Trim().ToLowerInvariant();
            var val = kv[1].Trim();

            switch (key)
            {
                case "server":
                case "data source":
                    server = val; break;
                case "database":
                case "initial catalog":
                    database = val; break;
                case "user id":
                case "uid":
                    user = val; break;
            }
        }
    }
}
