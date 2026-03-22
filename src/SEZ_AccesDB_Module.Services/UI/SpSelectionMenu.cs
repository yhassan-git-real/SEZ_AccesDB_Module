using SEZ_AccesDB_Module.Core.Configuration;
using SEZ_AccesDB_Module.Core.Models;
using Spectre.Console;

namespace SEZ_AccesDB_Module.Services.UI;

/// <summary>
/// Handles all Spectre.Console user interaction: SP selection, parameter input.
/// </summary>
public class SpSelectionMenu
{
    private readonly List<StoredProcedureDefinition> _procedures;

    public SpSelectionMenu(List<StoredProcedureDefinition> procedures)
    {
        _procedures = procedures;
    }

    /// <summary>
    /// Displays an interactive selection prompt and returns the chosen SP definition.
    /// </summary>
    public StoredProcedureDefinition SelectProcedure()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold cyan]SEZ Access DB Module[/]").RuleStyle("grey").Centered());
        AnsiConsole.WriteLine();

        var choices = _procedures.Select(p => $"[bold]{p.DisplayName}[/] [grey]({p.Name})[/]").ToList();
        choices.Add("[grey]Exit[/]");

        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold yellow]Select a Stored Procedure to execute:[/]")
                .PageSize(15)
                .HighlightStyle(new Style(foreground: Color.Cyan1))
                .AddChoices(choices));

        if (selected == "[grey]Exit[/]")
            throw new OperationCanceledException("User chose to exit.");

        // Match back to the SP definition
        int idx = choices.IndexOf(selected);
        if (idx < 0 || idx >= _procedures.Count)
            throw new InvalidOperationException("Invalid selection.");

        return _procedures[idx];
    }
}
