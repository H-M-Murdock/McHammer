using System.Net.Http.Headers;
using System.Diagnostics;
using System.Text.Json;
using McHammer.Dev.Commands;
using McHammer.Lib.Configuration;
using Spectre.Console;

namespace McHammer.Dev.Commands.Auth;

public class DiagnosePrtgCommand : BaseDevCommand
{
    public override string Name        => "Diagnose PRTG";
    public override string Description => "API v1 & v2 Endpunkte systematisch prüfen";
    public override string Category    => "Authentifizierung";

    private static readonly (string Path, string Label, bool NeedsAuth)[] V1Endpoints =
    [
        ("/api/getversion.htm",                                       "getversion.htm",      true),
        ("/api/getstatus.htm",                                        "getstatus.htm",       true),
        ("/api/table.json?content=groups&columns=objid,name&count=1", "table.json (groups)", true),
        ("/api/addgroup2.htm",                                        "addgroup2.htm",       false),
        ("/api/editsettings.htm",                                     "editsettings.htm",    false),
    ];

    private static readonly (string Path, string Method, string Label)[] V2Endpoints =
    [
        ("/api/v2/status",                              "GET",  "GET  /api/v2/status"),
        ("/api/v2/objects/count",                       "GET",  "GET  /api/v2/objects/count"),
        ("/api/v2/experimental/objects",                "GET",  "GET  /api/v2/experimental/objects"),
        ("/api/v2/experimental/groups/0/group",         "POST", "POST /experimental/groups/{id}/group"),
    ];

    public override async Task ExecuteAsync(CancellationToken ct = default)
    {
        PrintHeader("PRTG API Diagnose");

        PrtgConfig config;
        try { config = PrtgConfig.FromEnvironment(); }
        catch (InvalidOperationException ex)
        {
            PrintError(ex.Message);
            WaitForKey();
            return;
        }

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };

        // ── V1 ─────────────────────────────────────────────────────────────
        AnsiConsole.MarkupLine($"\n[bold cyan]── API v1[/] [grey]({config.ApiUrl.EscapeMarkup()})[/]");
        using var httpV1 = new HttpClient(handler) { BaseAddress = new Uri(config.ApiUrl) };

        var v1Table = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(Style.Parse("grey"))
            .AddColumn("[grey]Endpunkt[/]")
            .AddColumn("[grey]Status[/]")
            .AddColumn("[grey]Zeit[/]")
            .AddColumn("[grey]Antwort (Vorschau)[/]");

        foreach (var (path, label, needsAuth) in V1Endpoints)
        {
            var url = needsAuth
                ? $"{path}{(path.Contains('?') ? "&" : "?")}{config.BuildAuthQuery()}"
                : path;
            var (status, ms, snippet, color) = await ProbeGetAsync(httpV1, url, ct);

            v1Table.AddRow(
                $"[white]{label.EscapeMarkup()}[/]",
                $"[{color}]{status}[/]",
                $"[grey]{ms}ms[/]",
                $"[dim]{snippet.EscapeMarkup()}[/]");
        }
        AnsiConsole.Write(v1Table);

        // ── V2 ─────────────────────────────────────────────────────────────
        AnsiConsole.MarkupLine($"\n[bold cyan]── API v2[/] [grey]({config.ApiUrl2.EscapeMarkup()})[/]");
        using var httpV2 = new HttpClient(handler) { BaseAddress = new Uri(config.ApiUrl2) };
        httpV2.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", config.ApiKey);
        httpV2.DefaultRequestHeaders.Accept
            .Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var v2Table = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(Style.Parse("grey"))
            .AddColumn("[grey]Methode[/]")
            .AddColumn("[grey]Endpunkt[/]")
            .AddColumn("[grey]Status[/]")
            .AddColumn("[grey]Zeit[/]")
            .AddColumn("[grey]Antwort (Vorschau)[/]");

        foreach (var (path, method, label) in V2Endpoints)
        {
            var (status, ms, snippet, color) = method == "POST"
                ? await ProbePostAsync(httpV2, path,
                    """{"basic":{"name":"__diag_test__"}}""", ct)
                : await ProbeGetAsync(httpV2, path, ct);

            v2Table.AddRow(
                $"[grey]{method}[/]",
                $"[white]{path.EscapeMarkup()}[/]",
                $"[{color}]{status}[/]",
                $"[grey]{ms}ms[/]",
                $"[dim]{snippet.EscapeMarkup()}[/]");
        }
        AnsiConsole.Write(v2Table);

        // ── PATCH Schema für Gruppe (Discovery) ───────────────────────────
        AnsiConsole.MarkupLine("\n[bold cyan]── PATCH Schema – Gruppe (Discovery-Felder)[/]");

        var groupId = AnsiConsole.Ask<int>(
            "[grey]ObjId einer Gruppe für Schema-Abfrage:[/]", 8478);

        var schemaUrl  = $"/api/v2/experimental/schemas/{groupId}/patch?include=all_sections";
        var schemaResp = await httpV2.GetAsync(schemaUrl, ct);
        var schemaBody = await schemaResp.Content.ReadAsStringAsync(ct);

        // JSON pretty-print
        try
        {
            var pretty = JsonSerializer.Serialize(
                JsonDocument.Parse(schemaBody).RootElement,
                new JsonSerializerOptions { WriteIndented = true });

            // Nur die relevanten Sections anzeigen (autodiscovery/discovery)
            var lines = pretty.Split('\n')
                .Where(l =>
                    l.Contains("autodiscovery", StringComparison.OrdinalIgnoreCase) ||
                    l.Contains("discovery",     StringComparison.OrdinalIgnoreCase) ||
                    l.Contains("template",      StringComparison.OrdinalIgnoreCase) ||
                    l.Contains("devicetempl",   StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (lines.Count > 0)
            {
                AnsiConsole.MarkupLine("[grey]Relevante Schema-Felder:[/]");
                AnsiConsole.Write(new Panel(
                        string.Join("\n", lines).EscapeMarkup())
                    .Border(BoxBorder.Rounded)
                    .BorderStyle(Style.Parse("cyan")));
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]Keine Discovery/Template-Felder im Schema gefunden.[/]");
                AnsiConsole.MarkupLine("[grey]Vollständiges Schema:[/]");
                AnsiConsole.Write(new Panel(
                        pretty.EscapeMarkup()[..Math.Min(2000, pretty.Length)])
                    .Border(BoxBorder.Rounded)
                    .BorderStyle(Style.Parse("grey")));
            }
        }
        catch
        {
            AnsiConsole.Write(new Panel(schemaBody.EscapeMarkup())
                .Header($"[grey]{(int)schemaResp.StatusCode}[/]")
                .Border(BoxBorder.Rounded));
        }

        // ── Auswertung ──────────────────────────────────────────────────────
        PrintDiagnosis(config);
        WaitForKey();
    }

    // ── Probe-Helpers ───────────────────────────────────────────────────────

    private static async Task<(string status, long ms, string snippet, string color)>
        ProbeGetAsync(HttpClient http, string url, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var resp    = await http.GetAsync(url, ct);
            sw.Stop();
            var body    = await SafeReadAsync(resp);
            var code    = (int)resp.StatusCode;
            return (code.ToString(), sw.ElapsedMilliseconds, body, StatusColor(code));
        }
        catch (Exception ex)
        {
            sw.Stop();
            return ("ERR", sw.ElapsedMilliseconds,
                ex.Message[..Math.Min(60, ex.Message.Length)], "red");
        }
    }

    private static async Task<(string status, long ms, string snippet, string color)>
        ProbePostAsync(HttpClient http, string url, string jsonBody, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var content = new StringContent(jsonBody,
                System.Text.Encoding.UTF8, "application/json");
            var resp    = await http.PostAsync(url, content, ct);
            sw.Stop();
            var body    = await SafeReadAsync(resp);
            var code    = (int)resp.StatusCode;
            return (code.ToString(), sw.ElapsedMilliseconds, body, StatusColor(code));
        }
        catch (Exception ex)
        {
            sw.Stop();
            return ("ERR", sw.ElapsedMilliseconds,
                ex.Message[..Math.Min(60, ex.Message.Length)], "red");
        }
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp)
    {
        try
        {
            var body  = await resp.Content.ReadAsStringAsync();
            var clean = body.Replace("\n", " ").Replace("\r", "").Trim();
            return clean.Length > 80 ? clean[..80] + "…" : clean;
        }
        catch { return "(kein Body)"; }
    }

    private static string StatusColor(int code) => code switch
    {
        200 or 201 => "bold green",
        400        => "yellow",
        401 or 403 => "yellow",
        404        => "red",
        >= 500     => "bold red",
        _          => "grey"
    };

    private static void PrintDiagnosis(PrtgConfig config)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold yellow]Diagnose-Hinweise[/]")
            .RuleStyle("yellow").LeftJustified());
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine(
            $"[grey]V1 Base-URL:[/]  [white]{config.ApiUrl.EscapeMarkup()}[/]");
        AnsiConsole.MarkupLine(
            $"[grey]V2 Base-URL:[/]  [white]{config.ApiUrl2.EscapeMarkup()}[/]");
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine(
            "[yellow]●[/] Wenn [white]POST /experimental/groups/{id}/group[/] → [yellow]400[/]: " +
            "Endpoint existiert — Body oder Auth falsch.");
        AnsiConsole.MarkupLine(
            "[yellow]●[/] Wenn [white]POST /experimental/groups/{id}/group[/] → [red]404[/]: " +
            "PRTG Application Server ist [bold]nicht aktiviert[/].");
        AnsiConsole.MarkupLine(
            "[grey]  → PRTG Setup → Erweitert → New UI & API v2 aktivieren[/]");
        AnsiConsole.MarkupLine(
            "[yellow]●[/] Schema-Abfrage zeigt welche Felder für PATCH verfügbar sind.");
    }
}