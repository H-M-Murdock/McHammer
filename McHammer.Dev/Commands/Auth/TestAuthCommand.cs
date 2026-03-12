using McHammer.Lib.Configuration;
using McHammer.Lib.Services;
using Spectre.Console;

namespace McHammer.Dev.Commands.Auth;

public class TestAuthCommand : BaseDevCommand
{
    public override string Name        => "Test Auth";
    public override string Description => "Verbindung & API-Key gegen PRTG prüfen";
    public override string Category    => "Authentifizierung";

    // Bekannte PRTG Test-Endpunkte der Reihe nach probieren
    private static readonly (string Path, string Label)[] ProbeEndpoints =
    [
        ("/api/getversion.htm",         "getversion.htm"),
        ("/api/getstatus.htm",          "getstatus.htm"),
        ("/api/table.xml?content=sensors&count=1", "table.xml (sensors)"),
        ("/index.htm",                  "index.htm (Basis-Login)"),
        ("/",                           "Root"),
    ];

    public override async Task ExecuteAsync(CancellationToken ct = default)
    {
        PrintHeader("PRTG Authentifizierungstest");

        PrtgConfig config;
        try
        {
            config = PrtgConfig.FromEnvironment();
        }
        catch (InvalidOperationException ex)
        {
            PrintError($"Konfigurationsfehler: {ex.Message}");
            WaitForKey();
            return;
        }

        // Config-Tabelle
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(Style.Parse("grey"))
            .AddColumn("[grey]Variable[/]")
            .AddColumn("[grey]Wert[/]");

        table.AddRow("PRTG_API",    $"[white]{config.ApiUrl.EscapeMarkup()}[/]");
        table.AddRow("PRTG_APIV2", $"[white]{config.ApiUrl2.EscapeMarkup()}[/]");
        table.AddRow("PRTG_USER",  $"[white]{config.User.EscapeMarkup()}[/]");
        table.AddRow("PRTG_APIKEY", MaskSecret(config.ApiKey));
        table.AddRow("PRTG_HASH",   MaskSecret(config.PasHash));
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        // Auth testen
        var service = new PrtgAuthService(config);
        var result  = await RunWithSpinner(
            "Verbinde mit PRTG...",
            () => service.AuthenticateAsync(ct));

        AnsiConsole.WriteLine();

        if (result.Success)
        {
            PrintSuccess("Authentifizierung erfolgreich!");
            AnsiConsole.WriteLine();

            var infoTable = new Table()
                .Border(TableBorder.Rounded)
                .BorderStyle(Style.Parse("green"))
                .AddColumn("[grey]Info[/]")
                .AddColumn("[grey]Wert[/]");

            infoTable.AddRow("PRTG Version", $"[cyan]{result.Version.EscapeMarkup()}[/]");
            infoTable.AddRow("Status",       $"[white]{result.StatusInfo.EscapeMarkup()}[/]");

            AnsiConsole.Write(infoTable);
        }
        else
        {
            PrintError(result.Message);
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[grey]Tipp: Prüfe ob PRTG_APIKEY korrekt gesetzt ist.[/]");
            AnsiConsole.MarkupLine("[grey]      Format: langer Hash aus PRTG -> Setup -> API-Keys[/]");
        }

        WaitForKey();
    }

    private static async Task ProbeUrlAsync(string baseUrl, PrtgConfig config, CancellationToken ct)
    {
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri(baseUrl) };

        // Auth-Query zusammenbauen – beide Varianten
        var authVariants = new Dictionary<string, string>();

        if (!string.IsNullOrWhiteSpace(config.ApiKey))
            authVariants["API-Key"]    = $"apitoken={Uri.EscapeDataString(config.ApiKey)}";

        if (!string.IsNullOrWhiteSpace(config.User) && !string.IsNullOrWhiteSpace(config.PasHash))
            authVariants["User+Hash"]  = $"username={Uri.EscapeDataString(config.User)}&passhash={Uri.EscapeDataString(config.PasHash)}";

        if (!string.IsNullOrWhiteSpace(config.User) && !string.IsNullOrWhiteSpace(config.Password))
            authVariants["User+Pass"]  = $"username={Uri.EscapeDataString(config.User)}&password={Uri.EscapeDataString(config.Password)}";

        var probeTable = new Table()
            .Border(TableBorder.Simple)
            .BorderStyle(Style.Parse("grey"))
            .AddColumn("[grey]Endpunkt[/]")
            .AddColumn("[grey]Auth[/]")
            .AddColumn("[grey]Status[/]")
            .AddColumn("[grey]Antwort[/]");

        foreach (var (endpoint, _) in ProbeEndpoints)
        {
            foreach (var (authLabel, authQuery) in authVariants)
            {
                var url = $"{endpoint}?{authQuery}";
                try
                {
                    var resp    = await http.GetAsync(url, ct);
                    var status  = (int)resp.StatusCode;
                    var snippet = await GetSnippetAsync(resp);

                    var statusMarkup = status switch
                    {
                        200 => $"[bold green]{status} OK[/]",
                        401 => $"[yellow]{status} Unauthorized[/]",
                        403 => $"[yellow]{status} Forbidden[/]",
                        404 => $"[red]{status} Not Found[/]",
                        _   => $"[grey]{status}[/]"
                    };

                    probeTable.AddRow(
                        $"[dim]{endpoint.EscapeMarkup()}[/]",
                        $"[dim]{authLabel}[/]",
                        statusMarkup,
                        $"[dim]{snippet.EscapeMarkup()}[/]");
                }
                catch (Exception ex)
                {
                    probeTable.AddRow(
                        $"[dim]{endpoint.EscapeMarkup()}[/]",
                        $"[dim]{authLabel}[/]",
                        "[red]FEHLER[/]",
                        $"[red]{ex.Message.EscapeMarkup()}[/]");
                }
            }
        }

        AnsiConsole.Write(probeTable);
    }

    private static async Task<string> GetSnippetAsync(HttpResponseMessage resp)
    {
        try
        {
            var body = await resp.Content.ReadAsStringAsync();
            // Ersten 80 Zeichen, Zeilenumbrüche entfernen
            var clean = body.Replace("\n", " ").Replace("\r", "").Trim();
            return clean.Length > 80 ? clean[..80] + "…" : clean;
        }
        catch
        {
            return "(kein Body)";
        }
    }
}
