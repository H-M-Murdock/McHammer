using McHammer.Dev.Commands;
using McHammer.Lib.Configuration;
using McHammer.Lib.Exceptions;
using McHammer.Lib.Models.Network;
using McHammer.Lib.Services.Network;
using Spectre.Console;

namespace McHammer.Dev.Commands.Network;

public class SyncFunctionGroupsCommand : BaseDevCommand
{
    public override string Name        => "Sync FunctionGroups";
    public override string Description => "JSON-Dateien in PRTG-Gruppen synchronisieren";
    public override string Category    => "PRTG Struktur";

    private const string RootGroup = "TAL-GROUP";

    public override async Task ExecuteAsync(CancellationToken ct = default)
    {
        PrintHeader("PRTG FunctionGroup Sync");

        // Verzeichnis abfragen
        var path = AnsiConsole.Ask<string>(
            "[grey]Pfad zum JSON-Verzeichnis:[/]",
            @"D:\Prtg_FunctionGroups_USED");

        if (!Directory.Exists(path))
        {
            PrintError($"Verzeichnis nicht gefunden: {path}");
            WaitForKey();
            return;
        }

        PrtgConfig config;
        try { config = PrtgConfig.FromEnvironment(); }
        catch (InvalidOperationException ex)
        {
            PrintError(ex.Message);
            WaitForKey();
            return;
        }

        // Dateien laden
        var loader = new FunctionGroupLoader();
        List<PrtgFunctionGroupFile> files = [];

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync("Lade JSON-Dateien...", async _ =>
            {
                var loaded = await loader.LoadAllAsync(path, ct);
                files.AddRange(loaded);
            });

        // Übersicht
        var preTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(Style.Parse("grey"))
            .AddColumn("[grey]Datei[/]")
            .AddColumn("[grey]FunctionGroup[/]")
            .AddColumn("[grey]Netzwerke[/]");

        foreach (var f in files)
            preTable.AddRow(
                $"[white]{f.FunctionGroup.EscapeMarkup()}_USED.json[/]",
                $"[cyan]{f.FunctionGroup.EscapeMarkup()}[/]",
                $"[white]{f.TotalCount}[/]");

        AnsiConsole.Write(preTable);
        AnsiConsole.WriteLine();

        // Bestätigung
        if (!AnsiConsole.Confirm($"Sync gegen [cyan]{RootGroup}[/] starten?"))
        {
            PrintWarning("Abgebrochen.");
            WaitForKey();
            return;
        }

        // ── Debug-Modus ────────────────────────────────────────────────────
        IReadOnlyList<PrtgFunctionGroupFile> filesToSync = files;

        if (AnsiConsole.Confirm("Debug-Modus? (nur eine Struktur synchronisieren)"))
        {
            filesToSync = PromptDebugSelection(files);
        }

        // Sync ausführen
        var syncService = new PrtgGroupSyncService(config);
        List<GroupSyncResult> results = [];
        string? syncError = null;

        await AnsiConsole.Progress()
            .Columns(
                new TaskDescriptionColumn { Alignment = Justify.Left },
                new ProgressBarColumn()   { Width = 40 },
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("Synchronisiere Gruppen...", maxValue: 1);

                var progressHandler = new Progress<SyncProgress>(p =>
                {
                    task.MaxValue   = p.Total;
                    task.Value      = p.Current;
                    task.Description = $"[grey]{p.CurrentItem.EscapeMarkup()}[/]";
                });

                try
                {
                    results.AddRange(await syncService.SyncAsync(
                        filesToSync, files, RootGroup, progressHandler, ct));
                }
                catch (PrtgApiException ex)
                {
                    syncError = ex.Message;
                    PrintApiException(ex);
                }
                catch (Exception ex)
                {
                    syncError = $"Unerwarteter Fehler: {ex.Message}";
                }
                finally
                {
                    task.Value       = task.MaxValue;
                    task.Description = "Synchronisierung abgeschlossen";
                }
            });

        if (syncError is not null)
        {
            AnsiConsole.WriteLine();
            PrintError(syncError);
            WaitForKey();
            return;
        }

        // Ergebnis
        AnsiConsole.WriteLine();
        PrintResultTable(results);
        WaitForKey();
    }

    // ── Debug-Selektion ────────────────────────────────────────────────────

    private static IReadOnlyList<PrtgFunctionGroupFile> PromptDebugSelection(
        List<PrtgFunctionGroupFile> files)
    {
        var allNetworks = files.SelectMany(f => f.Networks).ToList();

        // Level 0: Country
        var country = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Level 0 – [cyan]Country[/] wählen:[/]")
                .PageSize(15)
                .HighlightStyle(Style.Parse("cyan bold"))
                .AddChoices(allNetworks
                    .Select(n => n.Country)
                    .Distinct()
                    .OrderBy(c => c)));

        // Level 1: FunctionGroup
        var functionGroup = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Level 1 – [cyan]FunctionGroup[/] wählen:[/]")
                .PageSize(15)
                .HighlightStyle(Style.Parse("cyan bold"))
                .AddChoices(allNetworks
                    .Where(n => n.Country == country)
                    .Select(n => n.FunctionGroup)
                    .Distinct()
                    .OrderBy(f => f)));

        // Level 2: Network
        var network = AnsiConsole.Prompt(
            new SelectionPrompt<PrtgFunctionGroupNetwork>()
                .Title("[bold]Level 2 – [cyan]Network[/] wählen:[/]")
                .PageSize(15)
                .HighlightStyle(Style.Parse("cyan bold"))
                .UseConverter(n =>
                    $"{n.Name.EscapeMarkup()}  " +
                    $"[dim]{n.Network}/{n.Netmask} | {n.City} ({n.SiteCode})[/]")
                .AddChoices(allNetworks
                    .Where(n => n.Country == country && n.FunctionGroup == functionGroup)
                    .OrderBy(n => n.Name)));

        AnsiConsole.WriteLine();
        PrintDebugSummary(country, functionGroup, network);
        AnsiConsole.WriteLine();

        // Gefilterte Struktur zurückgeben – Record mit-Expression
        return files
            .Select(f => f with
            {
                Networks = f.Networks
                    .Where(n =>
                        n.Country       == country       &&
                        n.FunctionGroup == functionGroup &&
                        n.Name          == network.Name)
                    .ToList()
            })
            .Where(f => f.Networks.Count > 0)
            .ToList();
    }

    private static void PrintDebugSummary(
        string country, string functionGroup, PrtgFunctionGroupNetwork network)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(Style.Parse("yellow"))
            .AddColumn("[yellow]Ebene[/]")
            .AddColumn("[yellow]Auswahl[/]");

        table.AddRow("Level 0 – Country",       $"[cyan]{country.EscapeMarkup()}[/]");
        table.AddRow("Level 1 – FunctionGroup", $"[cyan]{functionGroup.EscapeMarkup()}[/]");
        table.AddRow("Level 2 – Network",       $"[cyan]{network.Name.EscapeMarkup()}[/]");
        table.AddRow("Netzwerk",                $"[white]{network.Network}/{network.Netmask}[/]");
        table.AddRow("Stadt",                   $"[white]{network.City} ({network.SiteCode})[/]");
        table.AddRow("VLAN",                    $"[white]{network.Vlan}[/]");

        AnsiConsole.Write(table);
    }

    private static void PrintApiException(PrtgApiException ex)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[red]PRTG API Fehler[/]").RuleStyle("red").LeftJustified());

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(Style.Parse("red"))
            .AddColumn("[grey]Feld[/]")
            .AddColumn("[grey]Wert[/]");

        table.AddRow("Meldung",    $"[red]{ex.Message.EscapeMarkup()}[/]");

        if (ex.StatusCode is not null)
            table.AddRow("HTTP Status", $"[red]{ex.StatusCode}[/]");

        if (ex.Endpoint is not null)
            table.AddRow("Endpoint",   $"[white]{ex.Endpoint.EscapeMarkup()}[/]");

        AnsiConsole.Write(table);

        if (ex.RequestBody is not null)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[grey]── Request Body ──[/]");
            AnsiConsole.Write(new Panel(ex.RequestBody.EscapeMarkup())
                .Border(BoxBorder.Rounded)
                .BorderStyle(Style.Parse("grey")));
        }

        if (ex.ResponseBody is not null)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[grey]── Response Body ──[/]");
            AnsiConsole.Write(new Panel(ex.ResponseBody.EscapeMarkup())
                .Border(BoxBorder.Rounded)
                .BorderStyle(Style.Parse("red")));
        }
    }
    
    // ── Ergebnis-Tabelle ───────────────────────────────────────────────────

    private static void PrintResultTable(List<GroupSyncResult> results)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(Style.Parse("grey"))
            .AddColumn("[grey]Aktion[/]")
            .AddColumn("[grey]Country[/]")
            .AddColumn("[grey]FunctionGroup[/]")
            .AddColumn("[grey]Name[/]")
            .AddColumn("[grey]Info[/]");

        foreach (var r in results)
        {
            var (actionMarkup, icon) = r.Action switch
            {
                GroupSyncAction.Created       => ("[green]Erstellt[/]",    "✓"),
                GroupSyncAction.AlreadyExists => ("[grey]Vorhanden[/]",    "–"),
                GroupSyncAction.Archived      => ("[yellow]Archiviert[/]", "⚠"),
                _                             => ("[white]?[/]",           "?")
            };

            var info = r.Message ?? r.Description;
            table.AddRow(
                $"{icon} {actionMarkup}",
                r.Country.EscapeMarkup(),
                r.FunctionGroup.EscapeMarkup(),
                r.Name.EscapeMarkup(),
                info.EscapeMarkup()[..Math.Min(50, info.Length)]);
        }

        var created  = results.Count(r => r.Action == GroupSyncAction.Created);
        var existing = results.Count(r => r.Action == GroupSyncAction.AlreadyExists);
        var archived = results.Count(r => r.Action == GroupSyncAction.Archived);

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[green]✓ Erstellt: {created}[/]  " +
            $"[grey]– Vorhanden: {existing}[/]  " +
            $"[yellow]⚠ Archiviert: {archived}[/]");
    }
}