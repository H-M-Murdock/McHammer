using System.Text.Json;
using McHammer.Dev.Commands;
using McHammer.Lib.Configuration;
using McHammer.Lib.Exceptions;
using McHammer.Lib.Models.Network;
using McHammer.Lib.Services.Network;
using Spectre.Console;

namespace McHammer.Dev.Commands.Network;

public class ManageDiscoveryCommand : BaseDevCommand
{
    public override string Name        => "Discovery verwalten";
    public override string Description => "Auto-Discovery für PRTG-Gruppen ein-/ausschalten";
    public override string Category    => "PRTG Struktur";

    public override async Task ExecuteAsync(CancellationToken ct = default)
    {
        PrintHeader("PRTG Auto-Discovery Verwaltung");

        PrtgConfig config;
        try { config = PrtgConfig.FromEnvironment(); }
        catch (InvalidOperationException ex)
        {
            PrintError(ex.Message);
            WaitForKey();
            return;
        }

        var service = new PrtgDiscoveryService(config);

        // ── Schritt 1: TAL-GROUP finden ────────────────────────────────────
        int rootId = 0;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync("Lade Root-Gruppe...", async _ =>
            {
                var root = await FindRootGroupAsync(config, ct);
                rootId = root?.ObjId
                    ?? throw new PrtgApiException("TAL-GROUP nicht gefunden.");
            });

        // ── Schritt 2: Modus wählen ────────────────────────────────────────
        var mode = AnsiConsole.Prompt(
            new SelectionPrompt<DiscoveryMode>()
                .Title("[bold]Discovery-Modus:[/]")
                .HighlightStyle(Style.Parse("cyan bold"))
                .UseConverter(m => m switch
                {
                    DiscoveryMode.Disabled => "[red]✕ Deaktivieren[/]",
                    DiscoveryMode.Template => "[cyan]📋 Suche mit Device Templates[/]",
                    _                      => m.ToString()
                })
                .AddChoices(DiscoveryMode.Disabled, DiscoveryMode.Template));

        AnsiConsole.WriteLine();

        // ── Schritt 2b: Templates wählen ──────────────────────────────────
        List<string> selectedTemplates = [];

        if (mode == DiscoveryMode.Template)
        {
            IReadOnlyList<PrtgDeviceTemplate> availableTemplates = [];

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("cyan"))
                .StartAsync("Lade verfügbare Templates...", async _ =>
                {
                    availableTemplates = await service.GetTemplatesAsync(ct);
                });

            if (availableTemplates.Count == 0)
            {
                PrintWarning("Keine TAL-Templates gefunden.");
                WaitForKey();
                return;
            }

            PrintSuccess($"{availableTemplates.Count} Templates geladen.");
            AnsiConsole.WriteLine();

            var chosen = AnsiConsole.Prompt(
                new MultiSelectionPrompt<PrtgDeviceTemplate>()
                    .Title("[bold]Device Templates wählen [grey](nur TAL-*)[/]:[/]")
                    .PageSize(15)
                    .HighlightStyle(Style.Parse("cyan bold"))
                    .InstructionsText("[grey](Leertaste = auswählen, Enter = bestätigen)[/]")
                    .UseConverter(t =>
                        $"[white]{t.Name.EscapeMarkup()}[/]  " +
                        $"[dim]{t.DisplayName.EscapeMarkup()}[/]")
                    .AddChoices(availableTemplates));

            selectedTemplates = chosen.Select(t => t.Name).ToList();

            if (selectedTemplates.Count == 0)
            {
                PrintWarning("Keine Templates gewählt – Abgebrochen.");
                WaitForKey();
                return;
            }

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[grey]Gewählte Templates:[/]");
            foreach (var t in chosen)
                AnsiConsole.MarkupLine(
                    $"  [cyan]●[/] [white]{t.Name.EscapeMarkup()}[/]" +
                    (string.IsNullOrWhiteSpace(t.DisplayName)
                        ? ""
                        : $" [dim]– {t.DisplayName.EscapeMarkup()}[/]"));
            AnsiConsole.WriteLine();
        }

        // ── Schritt 3: Scope wählen ────────────────────────────────────────
        var scope = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Welche Gruppen betreffen?[/]")
                .HighlightStyle(Style.Parse("cyan bold"))
                .AddChoices(
                    "Alle Level-0 Gruppen",
                    "Level-0 auswählen (Multiselect)",
                    "Level-1 auswählen (nach Level-0)",
                    "Level-2 auswählen (nach Level-1)",
                    "Level-3 auswählen (nach Level-2)"));

        AnsiConsole.WriteLine();

        // ── Schritt 4: Gruppen ermitteln ───────────────────────────────────
        List<PrtgGroupNode> targets = [];

        try
        {
            targets = scope switch
            {
                "Alle Level-0 Gruppen"             => await SelectAllAsync(service, rootId, ct),
                "Level-0 auswählen (Multiselect)"  => await SelectLevel0Async(service, rootId, ct),
                "Level-1 auswählen (nach Level-0)" => await SelectLevel1Async(service, rootId, ct),
                "Level-2 auswählen (nach Level-1)" => await SelectLevel2Async(service, rootId, ct),
                "Level-3 auswählen (nach Level-2)" => await SelectLevel3Async(service, rootId, ct),
                _                                  => []
            };
        }
        catch (PrtgApiException ex)
        {
            PrintError(ex.Message);
            WaitForKey();
            return;
        }

        if (targets.Count == 0)
        {
            PrintWarning("Keine Gruppen ausgewählt.");
            WaitForKey();
            return;
        }

        // ── Schritt 5: Übersicht + Bestätigung ────────────────────────────
        AnsiConsole.WriteLine();
        PrintSelectionSummary(targets, mode);
        AnsiConsole.WriteLine();

        if (!AnsiConsole.Confirm(
            $"Discovery für [cyan]{targets.Count}[/] Gruppe(n) " +
            $"auf [bold]{ModeLabel(mode)}[/] setzen?"))
        {
            PrintWarning("Abgebrochen.");
            WaitForKey();
            return;
        }

        // ── Schritt 6: Ausführen ───────────────────────────────────────────
        AnsiConsole.WriteLine();
        string? error = null;
        var failed    = new List<string>();

        await AnsiConsole.Progress()
            .Columns(
                new TaskDescriptionColumn { Alignment = Justify.Left },
                new ProgressBarColumn()   { Width = 40 },
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("Setze Discovery...", maxValue: targets.Count);

                var progressHandler = new Progress<DiscoveryProgress>(p =>
                {
                    task.Value       = p.Current;
                    task.Description = p.Success == false
                        ? $"[red]✗ {p.GroupName.EscapeMarkup()}[/]"
                        : $"[grey]{p.GroupName.EscapeMarkup()}[/]";

                    if (p.Success == false)
                        failed.Add(p.GroupName);
                });

                try
                {
                    await service.SetDiscoveryAsync(
                        targets, mode,
                        selectedTemplates.Count > 0 ? selectedTemplates : null,
                        progressHandler, ct);
                }
                catch (PrtgApiException ex)
                {
                    error = ex.Message;
                    PrintApiException(ex);
                }
                catch (Exception ex)
                {
                    error = $"Unerwarteter Fehler: {ex.Message}";
                }
                finally
                {
                    task.Value       = targets.Count;
                    task.Description = "Fertig";
                }
            });

        // ── Diagnose (erste Gruppe) ────────────────────────────────────────
        if (targets.Count > 0 && error is null)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[grey]── Diagnose (erste Gruppe) ──[/]");

            var sample    = targets[0];
            var discType  = await service.ReadPropertyAsync(sample.ObjId, "discoverytype", ct);
            var discTempl = await service.ReadPropertyAsync(sample.ObjId, "devicetemplate", ct);

            var diagTable = new Table()
                .Border(TableBorder.Rounded)
                .BorderStyle(Style.Parse("grey"))
                .AddColumn("[grey]Property[/]")
                .AddColumn("[grey]Gesetzter Wert[/]")
                .AddColumn("[grey]Zurückgelesener Wert[/]");

            diagTable.AddRow("discoverytype",
                $"[white]{(int)mode}[/]",
                $"[cyan]{discType}[/]");
            diagTable.AddRow("devicetemplate",
                $"[white]{string.Join(", ", selectedTemplates)}[/]",
                $"[cyan]{discTempl}[/]");

            AnsiConsole.Write(diagTable);
        }

        // ── Ergebnis ───────────────────────────────────────────────────────
        AnsiConsole.WriteLine();

        if (error is not null)
            PrintError(error);

        var succeeded = targets.Count - failed.Count;
        AnsiConsole.MarkupLine(
            $"[green]✓ Erfolgreich: {succeeded}[/]" +
            (failed.Count > 0 ? $"  [red]✗ Fehler: {failed.Count}[/]" : ""));

        if (failed.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[red]Fehlgeschlagene Gruppen:[/]");
            foreach (var f in failed)
                AnsiConsole.MarkupLine($"  [red]–[/] {f.EscapeMarkup()}");
        }

        WaitForKey();
    }

    // ── Auswahl-Helpers ────────────────────────────────────────────────────

    private static async Task<List<PrtgGroupNode>> SelectAllAsync(
        IPrtgDiscoveryService service, int rootId, CancellationToken ct)
    {
        return (await LoadWithSpinner(service, rootId, "Level 0", ct)).ToList();
    }

    private static async Task<List<PrtgGroupNode>> SelectLevel0Async(
        IPrtgDiscoveryService service, int rootId, CancellationToken ct)
    {
        var level0  = await LoadWithSpinner(service, rootId, "Level 0 – Country", ct);
        var unique0 = level0.DistinctBy(g => g.Name).OrderBy(g => g.Name).ToList();

        var selected = AnsiConsole.Prompt(
            new MultiSelectionPrompt<PrtgGroupNode>()
                .Title("[bold]Level 0 – Country wählen:[/]")
                .PageSize(20)
                .HighlightStyle(Style.Parse("cyan bold"))
                .InstructionsText("[grey](Leertaste = auswählen, Enter = bestätigen)[/]")
                .UseConverter(g => g.Name.EscapeMarkup())
                .AddChoices(unique0));

        PrintSelectionBreadcrumb("Country", selected.Select(g => g.Name).ToList());
        return selected;
    }

    private static async Task<List<PrtgGroupNode>> SelectLevel1Async(
        IPrtgDiscoveryService service, int rootId, CancellationToken ct)
    {
        var level0  = await LoadWithSpinner(service, rootId, "Level 0 – Country", ct);
        var unique0 = level0.DistinctBy(g => g.Name).OrderBy(g => g.Name).ToList();

        var selectedL0 = AnsiConsole.Prompt(
            new MultiSelectionPrompt<PrtgGroupNode>()
                .Title("[bold]Level 0 – Country filtern:[/]")
                .PageSize(20)
                .HighlightStyle(Style.Parse("cyan bold"))
                .InstructionsText("[grey](Leertaste = auswählen, Enter = bestätigen)[/]")
                .UseConverter(g => g.Name.EscapeMarkup())
                .AddChoices(unique0));

        PrintSelectionBreadcrumb("Country", selectedL0.Select(g => g.Name).ToList());

        var level1WithPath = new List<(PrtgGroupNode Node, string Path)>();
        foreach (var l0 in selectedL0)
        {
            var children = await LoadWithSpinner(service, l0.ObjId,
                $"Level 1 unter {l0.Name}", ct);
            foreach (var c in children)
            {
                var path = $"[grey]{l0.Name.EscapeMarkup()}.[/][cyan]{c.Name.EscapeMarkup()}[/]";
                if (level1WithPath.All(x => x.Path != path))
                    level1WithPath.Add((c, path));
            }
        }

        if (level1WithPath.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Keine Level-1 Gruppen gefunden.[/]");
            return [];
        }

        return AnsiConsole.Prompt(
                new MultiSelectionPrompt<(PrtgGroupNode Node, string Path)>()
                    .Title("[bold]Level 1 – FunctionGroup wählen:[/]")
                    .PageSize(20)
                    .HighlightStyle(Style.Parse("cyan bold"))
                    .InstructionsText("[grey](Leertaste = auswählen, Enter = bestätigen)[/]")
                    .UseConverter(item => item.Path)
                    .AddChoices(level1WithPath.OrderBy(x => x.Path)))
            .Select(item => item.Node)
            .ToList();
    }

    private static async Task<List<PrtgGroupNode>> SelectLevel2Async(
        IPrtgDiscoveryService service, int rootId, CancellationToken ct)
    {
        var level0  = await LoadWithSpinner(service, rootId, "Level 0 – Country", ct);
        var unique0 = level0.DistinctBy(g => g.Name).OrderBy(g => g.Name).ToList();

        var selectedL0 = AnsiConsole.Prompt(
            new MultiSelectionPrompt<PrtgGroupNode>()
                .Title("[bold]Level 0 – Country filtern:[/]")
                .PageSize(20)
                .HighlightStyle(Style.Parse("cyan bold"))
                .InstructionsText("[grey](Leertaste = auswählen, Enter = bestätigen)[/]")
                .UseConverter(g => g.Name.EscapeMarkup())
                .AddChoices(unique0));

        PrintSelectionBreadcrumb("Country", selectedL0.Select(g => g.Name).ToList());

        var level1WithCtx = new List<(PrtgGroupNode Node, string L0Name, string Path)>();
        foreach (var l0 in selectedL0)
        {
            var children = await LoadWithSpinner(service, l0.ObjId,
                $"Level 1 unter {l0.Name}", ct);
            foreach (var c in children)
            {
                var path = $"[grey]{l0.Name.EscapeMarkup()}.[/][cyan]{c.Name.EscapeMarkup()}[/]";
                if (level1WithCtx.All(x => x.Path != path))
                    level1WithCtx.Add((c, l0.Name, path));
            }
        }

        if (level1WithCtx.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Keine Level-1 Gruppen gefunden.[/]");
            return [];
        }

        var selectedL1 = AnsiConsole.Prompt(
            new MultiSelectionPrompt<(PrtgGroupNode Node, string L0Name, string Path)>()
                .Title("[bold]Level 1 – FunctionGroup filtern:[/]")
                .PageSize(20)
                .HighlightStyle(Style.Parse("cyan bold"))
                .InstructionsText("[grey](Leertaste = auswählen, Enter = bestätigen)[/]")
                .UseConverter(item => item.Path)
                .AddChoices(level1WithCtx.OrderBy(x => x.Path)));

        PrintSelectionBreadcrumb("FunctionGroup",
            selectedL1.Select(x => $"{x.L0Name}.{x.Node.Name}").ToList());

        var level2WithPath = new List<(PrtgGroupNode Node, string Path)>();
        foreach (var (l1Node, l0Name, _) in selectedL1)
        {
            var children = await LoadWithSpinner(service, l1Node.ObjId,
                $"Level 2 unter {l1Node.Name}", ct);
            foreach (var c in children)
            {
                var path =
                    $"[grey]{l0Name.EscapeMarkup()}.{l1Node.Name.EscapeMarkup()}.[/]" +
                    $"[cyan]{c.Name.EscapeMarkup()}[/]";
                if (level2WithPath.All(x => x.Path != path))
                    level2WithPath.Add((c, path));
            }
        }

        if (level2WithPath.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Keine Level-2 Gruppen gefunden.[/]");
            return [];
        }

        return AnsiConsole.Prompt(
                new MultiSelectionPrompt<(PrtgGroupNode Node, string Path)>()
                    .Title("[bold]Level 2 – Network wählen:[/]")
                    .PageSize(20)
                    .HighlightStyle(Style.Parse("cyan bold"))
                    .InstructionsText("[grey](Leertaste = auswählen, Enter = bestätigen)[/]")
                    .UseConverter(item => item.Path)
                    .AddChoices(level2WithPath.OrderBy(x => x.Path)))
            .Select(item => item.Node)
            .ToList();
    }

    private static async Task<List<PrtgGroupNode>> SelectLevel3Async(
        IPrtgDiscoveryService service, int rootId, CancellationToken ct)
    {
        var level0  = await LoadWithSpinner(service, rootId, "Level 0 – Country", ct);
        var unique0 = level0.DistinctBy(g => g.Name).OrderBy(g => g.Name).ToList();

        var selectedL0 = AnsiConsole.Prompt(
            new MultiSelectionPrompt<PrtgGroupNode>()
                .Title("[bold]Level 0 – Country filtern:[/]")
                .PageSize(20)
                .HighlightStyle(Style.Parse("cyan bold"))
                .InstructionsText("[grey](Leertaste = auswählen, Enter = bestätigen)[/]")
                .UseConverter(g => g.Name.EscapeMarkup())
                .AddChoices(unique0));

        PrintSelectionBreadcrumb("Country", selectedL0.Select(g => g.Name).ToList());

        var level1WithCtx = new List<(PrtgGroupNode Node, string L0Name, string Path)>();
        foreach (var l0 in selectedL0)
        {
            var children = await LoadWithSpinner(service, l0.ObjId,
                $"Level 1 unter {l0.Name}", ct);
            foreach (var c in children)
            {
                var path = $"[grey]{l0.Name.EscapeMarkup()}.[/][cyan]{c.Name.EscapeMarkup()}[/]";
                if (level1WithCtx.All(x => x.Path != path))
                    level1WithCtx.Add((c, l0.Name, path));
            }
        }

        if (level1WithCtx.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Keine Level-1 Gruppen gefunden.[/]");
            return [];
        }

        var selectedL1 = AnsiConsole.Prompt(
            new MultiSelectionPrompt<(PrtgGroupNode Node, string L0Name, string Path)>()
                .Title("[bold]Level 1 – FunctionGroup filtern:[/]")
                .PageSize(20)
                .HighlightStyle(Style.Parse("cyan bold"))
                .InstructionsText("[grey](Leertaste = auswählen, Enter = bestätigen)[/]")
                .UseConverter(item => item.Path)
                .AddChoices(level1WithCtx.OrderBy(x => x.Path)));

        PrintSelectionBreadcrumb("FunctionGroup",
            selectedL1.Select(x => $"{x.L0Name}.{x.Node.Name}").ToList());

        var level2WithCtx =
            new List<(PrtgGroupNode Node, string L0Name, string L1Name, string Path)>();
        foreach (var (l1Node, l0Name, _) in selectedL1)
        {
            var children = await LoadWithSpinner(service, l1Node.ObjId,
                $"Level 2 unter {l1Node.Name}", ct);
            foreach (var c in children)
            {
                var path =
                    $"[grey]{l0Name.EscapeMarkup()}.{l1Node.Name.EscapeMarkup()}.[/]" +
                    $"[cyan]{c.Name.EscapeMarkup()}[/]";
                if (level2WithCtx.All(x => x.Path != path))
                    level2WithCtx.Add((c, l0Name, l1Node.Name, path));
            }
        }

        if (level2WithCtx.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Keine Level-2 Gruppen gefunden.[/]");
            return [];
        }

        var selectedL2 = AnsiConsole.Prompt(
            new MultiSelectionPrompt<(PrtgGroupNode Node, string L0Name, string L1Name,
                    string Path)>()
                .Title("[bold]Level 2 – Network filtern:[/]")
                .PageSize(20)
                .HighlightStyle(Style.Parse("cyan bold"))
                .InstructionsText("[grey](Leertaste = auswählen, Enter = bestätigen)[/]")
                .UseConverter(item => item.Path)
                .AddChoices(level2WithCtx.OrderBy(x => x.Path)));

        PrintSelectionBreadcrumb("Network",
            selectedL2.Select(x => $"{x.L0Name}.{x.L1Name}.{x.Node.Name}").ToList());

        var level3WithPath = new List<(PrtgGroupNode Node, string Path)>();
        foreach (var (l2Node, l0Name, l1Name, _) in selectedL2)
        {
            var children = await LoadWithSpinner(service, l2Node.ObjId,
                $"Level 3 unter {l2Node.Name}", ct);
            foreach (var c in children)
            {
                var path =
                    $"[grey]{l0Name.EscapeMarkup()}.{l1Name.EscapeMarkup()}." +
                    $"{l2Node.Name.EscapeMarkup()}.[/][cyan]{c.Name.EscapeMarkup()}[/]";
                if (level3WithPath.All(x => x.Path != path))
                    level3WithPath.Add((c, path));
            }
        }

        if (level3WithPath.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Keine Level-3 Gruppen gefunden.[/]");
            return [];
        }

        return AnsiConsole.Prompt(
                new MultiSelectionPrompt<(PrtgGroupNode Node, string Path)>()
                    .Title("[bold]Level 3 – Type wählen:[/]")
                    .PageSize(20)
                    .HighlightStyle(Style.Parse("cyan bold"))
                    .InstructionsText("[grey](Leertaste = auswählen, Enter = bestätigen)[/]")
                    .UseConverter(item => item.Path)
                    .AddChoices(level3WithPath.OrderBy(x => x.Path)))
            .Select(item => item.Node)
            .ToList();
    }

    // ── Spinner + Breadcrumb ───────────────────────────────────────────────

    private static async Task<IReadOnlyList<PrtgGroupNode>> LoadWithSpinner(
        IPrtgDiscoveryService service, int parentId,
        string label, CancellationToken ct)
    {
        IReadOnlyList<PrtgGroupNode> result = [];
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync($"Lade {label}...", async _ =>
            {
                result = await service.GetChildGroupsAsync(parentId, ct);
            });
        return result;
    }

    private static void PrintSelectionBreadcrumb(string label, List<string> names)
    {
        const int maxItems = 5;
        var display = names.Take(maxItems).ToList();
        var joined  = string.Join(", ", display.Select(n => n.EscapeMarkup()));

        if (names.Count > maxItems)
            joined += $" [dim]+{names.Count - maxItems} weitere[/]";

        AnsiConsole.MarkupLine($"  [grey]✔ {label}:[/] [cyan]{joined}[/]");
        AnsiConsole.WriteLine();
    }

    // ── Darstellung ────────────────────────────────────────────────────────

    private static void PrintSelectionSummary(
        List<PrtgGroupNode> targets, DiscoveryMode mode)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(Style.Parse("cyan"))
            .AddColumn("[grey]#[/]")
            .AddColumn("[grey]Gruppe[/]")
            .AddColumn("[grey]ObjId[/]");

        foreach (var (g, i) in targets.Take(15).Select((g, i) => (g, i + 1)))
            table.AddRow(
                $"[grey]{i}[/]",
                $"[white]{g.Name.EscapeMarkup()}[/]",
                $"[dim]{g.ObjId}[/]");

        if (targets.Count > 15)
            table.AddRow("[dim]…[/]", $"[dim]+ {targets.Count - 15} weitere[/]", "");

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(
            $"\n[bold]Modus:[/] {ModeLabel(mode)}  " +
            $"[bold]Gesamt:[/] [cyan]{targets.Count}[/] Gruppe(n)");
    }

    private static void PrintApiException(PrtgApiException ex)
    {
        AnsiConsole.MarkupLine($"[red]Status:[/] {ex.StatusCode}");
        AnsiConsole.MarkupLine($"[red]Endpoint:[/] {ex.Endpoint?.EscapeMarkup()}");
        if (ex.RequestBody is not null)
            AnsiConsole.Write(new Panel(ex.RequestBody.EscapeMarkup())
                .Header("Request").Border(BoxBorder.Rounded));
        if (ex.ResponseBody is not null)
            AnsiConsole.Write(new Panel(ex.ResponseBody.EscapeMarkup())
                .Header("Response").Border(BoxBorder.Rounded));
    }

    private static string ModeLabel(DiscoveryMode mode) => mode switch
    {
        DiscoveryMode.Disabled => "[red]Deaktiviert[/]",
        DiscoveryMode.Template => "[green]Mit Templates[/]",
        _                      => mode.ToString()
    };

    private static async Task<PrtgGroupNode?> FindRootGroupAsync(
        PrtgConfig config, CancellationToken ct)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri(config.ApiUrl) };

        var url  = $"/api/table.json?content=groups&columns=objid,name,tags" +
                   $"&filter_name=TAL-GROUP&{config.BuildAuthQuery()}";
        var json = await http.GetStringAsync(url, ct);
        var doc  = JsonDocument.Parse(json);
        var arr  = doc.RootElement.GetProperty("groups");
        if (arr.GetArrayLength() == 0) return null;

        var g = arr[0];
        return new PrtgGroupNode(
            g.GetProperty("objid").GetInt32(),
            g.GetProperty("name").GetString() ?? "TAL-GROUP",
            g.GetProperty("tags").GetString()  ?? "");
    }
}