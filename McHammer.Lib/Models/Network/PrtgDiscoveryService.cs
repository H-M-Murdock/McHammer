using McHammer.Lib.Configuration;
using McHammer.Lib.Exceptions;
using McHammer.Lib.Models.Network;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Web;

namespace McHammer.Lib.Services.Network;

public enum DiscoveryMode
{
    Disabled         = 0,
    Automatic        = 1,
    Template = 2,   // ← korrekt für "Mit Gerätevorlagen"
    AutomaticDetailed = 3   
}

public record PrtgGroupNode(int ObjId, string Name, string Tags);

public record DiscoveryProgress(
    int    Current,
    int    Total,
    string GroupName,
    bool?  Success = null);

public interface IPrtgDiscoveryService
{
    Task<IReadOnlyList<PrtgGroupNode>> GetChildGroupsAsync(
        int parentId, CancellationToken ct = default);

    Task<IReadOnlyList<PrtgDeviceTemplate>> GetTemplatesAsync(
        CancellationToken ct = default);

    Task SetDiscoveryAsync(
        IReadOnlyList<PrtgGroupNode>  groups,
        DiscoveryMode                 mode,
        IReadOnlyList<string>?        templateNames = null,
        IProgress<DiscoveryProgress>? progress      = null,
        CancellationToken             ct            = default);

    Task<string> ReadPropertyAsync(
        int objId, string name, CancellationToken ct = default);
}

public class PrtgDiscoveryService : IPrtgDiscoveryService
{
    private readonly PrtgConfig _config;
    private readonly HttpClient _http;    // V1 – Query-Auth
    private readonly HttpClient _httpV2;  // V2 – Bearer-Auth

    public PrtgDiscoveryService(PrtgConfig config,
        HttpClient? http   = null,
        HttpClient? httpV2 = null)
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

        _httpV2 = httpV2 ?? new HttpClient(handler)
        {
            BaseAddress = new Uri(config.ApiUrl2)
        };
        _httpV2.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", config.ApiKey);
        _httpV2.DefaultRequestHeaders.Accept
            .Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // ── Gruppen laden ──────────────────────────────────────────────────────

    public async Task<IReadOnlyList<PrtgGroupNode>> GetChildGroupsAsync(
        int parentId, CancellationToken ct = default)
    {
        // filter_parentid stellt sicher dass nur direkte Kinder zurückkommen
        var url = $"/api/table.json?content=groups" +
                  $"&columns=objid,name,tags,parentid" +
                  $"&id={parentId}" +
                  $"&filter_parentid={parentId}" +
                  $"&{_config.BuildAuthQuery()}";

        var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            throw new PrtgApiException(
                "Fehler beim Laden der Gruppen",
                (int)response.StatusCode,
                url.Split('?')[0]);

        var json = await response.Content.ReadAsStringAsync(ct);
        var doc  = JsonDocument.Parse(json);

        return doc.RootElement
            .GetProperty("groups")
            .EnumerateArray()
            .Select(g => new PrtgGroupNode(
                g.GetProperty("objid").GetInt32(),
                g.GetProperty("name").GetString() ?? "",
                g.GetProperty("tags").GetString()  ?? ""))
            .OrderBy(g => g.Name)
            .ToList();
    }

    // ── Templates laden ────────────────────────────────────────────────────

    public async Task<IReadOnlyList<PrtgDeviceTemplate>> GetTemplatesAsync(
        CancellationToken ct = default)
    {
        try
        {
            var response = await _httpV2.GetAsync(
                "/api/v2/experimental/devices/templates", ct);

            if (!response.IsSuccessStatusCode)
                return PrtgDeviceTemplates.All;

            var json = await response.Content.ReadAsStringAsync(ct);
            var doc  = JsonDocument.Parse(json);

            var templates = doc.RootElement
                .EnumerateArray()
                .Select(t => new PrtgDeviceTemplate(
                    t.TryGetProperty("name",        out var n)    ? n.GetString()    ?? "" : "",
                    t.TryGetProperty("displayName", out var d)    ? d.GetString()    ?? "" : "",
                    t.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : ""))
                .Where(t =>
                    !string.IsNullOrWhiteSpace(t.Name) &&
                    t.Name.StartsWith("TAL-", StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t.Name)
                .ToList();

            return templates.Count > 0 ? templates : PrtgDeviceTemplates.All;
        }
        catch
        {
            return PrtgDeviceTemplates.All;
        }
    }

    // ── Discovery setzen ───────────────────────────────────────────────────

    public async Task SetDiscoveryAsync(
        IReadOnlyList<PrtgGroupNode>  groups,
        DiscoveryMode                 mode,
        IReadOnlyList<string>?        templateNames = null,
        IProgress<DiscoveryProgress>? progress      = null,
        CancellationToken             ct            = default)
    {
        var total   = groups.Count;
        var current = 0;

        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            current++;

            try
            {
                if (mode == DiscoveryMode.Disabled)
                {
                    await SetPropertyAsync(group.ObjId, "discoverytype", "0", ct);
                }
                else if (mode == DiscoveryMode.Template && templateNames?.Count > 0)
                {
                    await SetDiscoveryWithTemplatesAsync(group.ObjId, templateNames, ct);
                }

                progress?.Report(new DiscoveryProgress(
                    current, total, group.Name, Success: true));
            }
            catch (Exception)
            {
                progress?.Report(new DiscoveryProgress(
                    current, total, group.Name, Success: false));
                throw;
            }
        }
    }

    private async Task SetDiscoveryWithTemplatesAsync(
        int objId, IReadOnlyList<string> templateNames, CancellationToken ct)
    {
        var templates = templateNames
            .Select(t => t.EndsWith(".odt", StringComparison.OrdinalIgnoreCase)
                ? t : $"{t}.odt")
            .ToArray();

        var body = new
        {
            discoverytypegroup = new
            {
                discoverytype  = "2",
                devicetemplate = templates
            }
        };

        await PatchV2Async($"/api/v2/experimental/groups/{objId}", body, ct);
    }

    // ── Property lesen/schreiben ───────────────────────────────────────────

    public async Task<string> ReadPropertyAsync(
        int objId, string name, CancellationToken ct = default)
    {
        var url = $"/api/getobjectproperty.htm?id={objId}" +
                  $"&name={name}&show=nohtmlencode" +
                  $"&{_config.BuildAuthQuery()}";

        var response = await _http.GetAsync(url, ct);
        var body     = await response.Content.ReadAsStringAsync(ct);

        var match = System.Text.RegularExpressions.Regex
            .Match(body, @"<result>(.*?)</result>");
        return match.Success ? match.Groups[1].Value : "";
    }

    private async Task SetPropertyAsync(
        int objId, string name, string value, CancellationToken ct)
    {
        var url = $"/api/setobjectproperty.htm?id={objId}" +
                  $"&name={name}&value={HttpUtility.UrlEncode(value)}" +
                  $"&{_config.BuildAuthQuery()}";

        var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            throw new PrtgApiException(
                $"Property '{name}' konnte nicht gesetzt werden",
                (int)response.StatusCode,
                url.Split('?')[0]);
    }

    private async Task PatchV2Async(string path, object body, CancellationToken ct)
    {
        var json    = JsonSerializer.Serialize(body);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _httpV2.PatchAsync(path, content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            throw new PrtgApiException(
                $"PRTG API v2 PATCH fehlgeschlagen",
                (int)response.StatusCode,
                $"{_httpV2.BaseAddress}{path.TrimStart('/')}",
                json,
                responseBody);
        }
    }
}