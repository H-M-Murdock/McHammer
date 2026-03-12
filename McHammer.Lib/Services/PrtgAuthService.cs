using McHammer.Lib.Configuration;
using McHammer.Lib.Models;
using System.Text.Json;

namespace McHammer.Lib.Services;

public class PrtgAuthService : IPrtgAuthService
{
    private readonly PrtgConfig _config;
    private readonly HttpClient _http;

    public PrtgAuthService(PrtgConfig config, HttpClient? http = null)
    {
        _config = config;

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };

        _http = http ?? new HttpClient(handler)
        {
            BaseAddress = new Uri(config.ApiUrl)
        };
    }

    public async Task<PrtgAuthResult> AuthenticateAsync(CancellationToken ct = default)
    {
        // getstatus.htm ist der zuverlässige Endpunkt auf Port 443
        var query = BuildAuthQuery();
        var url   = $"/api/getstatus.htm?{query}";

        try
        {
            var response = await _http.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
                return new PrtgAuthResult(
                    false, string.Empty, string.Empty,
                    $"HTTP {(int)response.StatusCode} – {response.ReasonPhrase}");

            var json = await response.Content.ReadAsStringAsync(ct);
            return ParseStatusResponse(json);
        }
        catch (Exception ex)
        {
            return new PrtgAuthResult(false, string.Empty, string.Empty, $"Fehler: {ex.Message}");
        }
    }

    // Für spätere API-Aufrufe (table.xml etc.) – Auth als Query anhängen
    public string BuildAuthQuery()
    {
        if (!string.IsNullOrWhiteSpace(_config.ApiKey))
            return $"apitoken={Uri.EscapeDataString(_config.ApiKey)}";

        if (!string.IsNullOrWhiteSpace(_config.User) && !string.IsNullOrWhiteSpace(_config.PasHash))
            return $"username={Uri.EscapeDataString(_config.User)}&passhash={Uri.EscapeDataString(_config.PasHash)}";

        if (!string.IsNullOrWhiteSpace(_config.User) && !string.IsNullOrWhiteSpace(_config.Password))
            return $"username={Uri.EscapeDataString(_config.User)}&password={Uri.EscapeDataString(_config.Password)}";

        throw new InvalidOperationException("Keine gültige Auth-Konfiguration gefunden.");
    }

    private static PrtgAuthResult ParseStatusResponse(string json)
    {
        try
        {
            var doc      = JsonDocument.Parse(json);
            var root     = doc.RootElement;

            // Version aus dem Status-Response lesen
            var version  = root.TryGetProperty("Version",     out var v) ? v.GetString() ?? "?" : "?";
            var messages = root.TryGetProperty("NewMessages", out var m) ? m.GetString() ?? "0" : "0";
            var alarms   = root.TryGetProperty("NewAlarms",   out var a) ? a.GetString() ?? "0" : "0";

            return new PrtgAuthResult(true, version, $"Meldungen: {messages} | Alarme: {alarms}", string.Empty);
        }
        catch (JsonException)
        {
            // Gültiger 200er, aber kein reines JSON → trotzdem authentifiziert
            return new PrtgAuthResult(true, "?", "Status OK (kein JSON-Parse möglich)", string.Empty);
        }
    }
}
