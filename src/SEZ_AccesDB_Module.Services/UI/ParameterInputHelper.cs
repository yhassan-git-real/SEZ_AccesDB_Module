using SEZ_AccesDB_Module.Core.Configuration;
using SEZ_AccesDB_Module.Core.Models;
using Spectre.Console;

namespace SEZ_AccesDB_Module.Services.UI;

/// <summary>
/// Collects SP parameters (@mon and optionally @mon1) from the user via Spectre.Console.
/// </summary>
public class ParameterInputHelper
{
    /// <summary>
    /// Collects all required parameters for a stored procedure.
    /// @mon  → integer in format YYYYMMDD (for BTD/FULL/SEZ_FULL/new) or YYYYMM (for SEZ/Vishal)
    /// @mon1 → end of range (if present in SP definition)
    /// </summary>
    public List<SpParameter> CollectParameters(StoredProcedureDefinition spDef)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[bold cyan]Parameters for: {spDef.Name}[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        bool hasMon1 = spDef.Parameters.Contains("mon1", StringComparer.OrdinalIgnoreCase);

        AnsiConsole.MarkupLine(hasMon1
            ? "[yellow]Enter date range. Format for @mon/@mon1: YYYYMMDD (e.g. 20260101)[/]"
            : "[yellow]Enter month. Format for @mon:  YYYYMM  (e.g. 202601)[/]");
        AnsiConsole.WriteLine();

        var parameters = new List<SpParameter>();

        var mon = AnsiConsole.Prompt(
            new TextPrompt<int>($"[bold]Enter @mon ({(hasMon1 ? "YYYYMMDD start" : "YYYYMM month")}):[/]")
                .ValidationErrorMessage("[red]Please enter a valid integer.[/]")
                .Validate(v =>
                {
                    var s = v.ToString();
                    if (hasMon1) return s.Length == 8 ? ValidationResult.Success() : ValidationResult.Error("[red]@mon must be 8 digits (YYYYMMDD).[/]");
                    return s.Length == 6 ? ValidationResult.Success() : ValidationResult.Error("[red]@mon must be 6 digits (YYYYMM).[/]");
                }));

        parameters.Add(new SpParameter("mon", mon));

        if (hasMon1)
        {
            var mon1 = AnsiConsole.Prompt(
                new TextPrompt<int>("[bold]Enter @mon1 (YYYYMMDD end):[/]")
                    .ValidationErrorMessage("[red]Please enter a valid integer.[/]")
                    .Validate(v =>
                    {
                        var s = v.ToString();
                        if (s.Length != 8) return ValidationResult.Error("[red]@mon1 must be 8 digits (YYYYMMDD).[/]");
                        if (v < mon) return ValidationResult.Error("[red]@mon1 must be >= @mon.[/]");
                        return ValidationResult.Success();
                    }));
            parameters.Add(new SpParameter("mon1", mon1));
        }

        return parameters;
    }

    /// <summary>
    /// Prompts user for the output file name prefix.
    /// </summary>
    public string CollectFileNamePrefix(string defaultPrefix)
    {
        AnsiConsole.WriteLine();
        var prefix = AnsiConsole.Prompt(
            new TextPrompt<string>($"[bold]Output file name prefix[/] [grey](default: {defaultPrefix})[/]:")
                .DefaultValue(defaultPrefix)
                .AllowEmpty());

        return string.IsNullOrWhiteSpace(prefix) ? defaultPrefix : prefix.Trim();
    }
}
