using McHammer.Lib.Configuration;
using Spectre.Console;

namespace McHammer.Dev.Commands.Info;

public class ShowConfigCommand : BaseDevCommand
{
    public override string Name        => "Config anzeigen";
    public override string Description => "Umgebungsvariablen & Konfiguration prüfen";
    public override string Category    => "System";

    public override Task ExecuteAsync(CancellationToken ct = default)
    {
        PrintHeader("Aktuelle Konfiguration");

        var vars = new[]
        {
            "PRTG_API", "PRTG_APIV2", "PRTG_APIKEY",
            "PRTG_HASH", "PRTG_PASSWORD", "PRTG_USER"
        };

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(Style.Parse("grey"))
            .AddColumn("[bold]Variable[/]")
            .AddColumn("[bold]Status[/]")
            .AddColumn("[bold]Wert[/]");

        foreach (var key in vars)
        {
            var val = Environment.GetEnvironmentVariable(key);
            var isSensitive = key.Contains("KEY") || key.Contains("HASH") || key.Contains("PASSWORD");

            if (val is null)
            {
                table.AddRow(
                    $"[white]{key}[/]",
                    "[red]✗ FEHLT[/]",
                    "[dim](nicht gesetzt)[/]");
            }
            else if (isSensitive)
            {
                table.AddRow(
                    $"[white]{key}[/]",
                    "[green]✓ OK[/]",
                    MaskSecret(val));
            }
            else
            {
                table.AddRow(
                    $"[white]{key}[/]",
                    "[green]✓ OK[/]",
                    $"[cyan]{val}[/]");
            }
        }

        AnsiConsole.Write(table);
        WaitForKey();
        return Task.CompletedTask;
    }
}