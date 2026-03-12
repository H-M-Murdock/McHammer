using Spectre.Console;

namespace McHammer.Dev.Commands;

public abstract class BaseDevCommand : IDevCommand
{
    public abstract string Name        { get; }
    public abstract string Description { get; }
    public virtual  string Category    => "Allgemein";

    public abstract Task ExecuteAsync(CancellationToken ct = default);

    // ── Helpers ────────────────────────────────────────────────────────────

    protected static void PrintHeader(string title)
    {
        AnsiConsole.Write(
            new Rule($"[bold cyan]{title}[/]")
                .RuleStyle("grey")
                .LeftJustified());
        AnsiConsole.WriteLine();
    }

    protected static void PrintSuccess(string message) =>
        AnsiConsole.MarkupLine($"[bold green]✓[/] {message.EscapeMarkup()}");
    protected static void PrintError(string message) =>
        AnsiConsole.MarkupLine($"[bold red]✗[/] {message.EscapeMarkup()}");
    protected static void PrintWarning(string message) =>
        AnsiConsole.MarkupLine($"[bold yellow]⚠[/] {message.EscapeMarkup()}");

    protected static void PrintInfo(string label, string value) =>
        AnsiConsole.MarkupLine($"  [grey]{label.PadRight(12)}:[/] [white]{value}[/]");

    protected static void WaitForKey()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey][[ Drücke Enter um fortzufahren ]][/]");
        Console.ReadLine();
    }


    protected static string MaskSecret(string s) =>
        string.IsNullOrEmpty(s) ? "[red](leer)[/]"
        : s.Length <= 6    ? "[yellow]***[/]"
        : $"[dim]{s[..4]}{"*".PadRight(s.Length - 4, '*')}[/]";

    protected static async Task<T> RunWithSpinner<T>(
        string label,
        Func<Task<T>> action)
    {
        T result = default!;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync(label, async _ =>
            {
                result = await action();
            });
        return result;
    }
}