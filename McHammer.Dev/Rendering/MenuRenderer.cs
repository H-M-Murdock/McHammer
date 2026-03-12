using McHammer.Dev.Commands;
using Spectre.Console;

namespace McHammer.Dev.Rendering;

public class MenuRenderer
{
    private readonly IReadOnlyList<IDevCommand> _commands;

    public MenuRenderer(IReadOnlyList<IDevCommand> commands)
    {
        _commands = commands;
    }

    public IDevCommand? Prompt()
    {
        const string exitLabel = "[red]✕ Beenden[/]";

        // Gruppierung nach Kategorie
        var grouped = _commands
            .GroupBy(c => c.Category)
            .OrderBy(g => g.Key)
            .ToList();

        var choices = new List<string>();

        foreach (var group in grouped)
        {
            choices.Add($"[bold grey]── {group.Key} ──[/]"); // Trennzeile
            foreach (var cmd in group)
                choices.Add($"[cyan]{cmd.Name}[/][grey] – {cmd.Description}[/]");
        }

        choices.Add(exitLabel);

        var selection = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Was möchtest du tun?[/]")
                .PageSize(15)
                .HighlightStyle(Style.Parse("cyan bold"))
                .AddChoices(choices)
                .UseConverter(s => s));

        if (selection == exitLabel) return null;

        // Trennzeilen sind nicht wählbar (Spectre erlaubt es trotzdem → filtern)
        if (selection.StartsWith("[bold grey]")) return null;

        // Command anhand des Namens zurückgeben
        var cleanName = StripMarkup(selection).Split('–')[0].Trim();
        return _commands.FirstOrDefault(c => c.Name == cleanName);
    }

    private static string StripMarkup(string s)
    {
        // Minimaler Markup-Stripper für den Vergleich
        var result = System.Text.RegularExpressions.Regex.Replace(s, @"\[.*?\]", "");
        return result.Trim();
    }
}