using McHammer.Lib.Configuration;
using McHammer.Lib.Exceptions;
using McHammer.Lib.Models.Network;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Web;

namespace McHammer.Lib.Services.Network;

public class PrtgGroupSyncService : IPrtgGroupSyncService
{
    private readonly PrtgConfig _config;
    private readonly HttpClient _httpV1;   // Lesen  → ApiUrl  (v1 table.json)
    private readonly HttpClient _httpV2;   // Schreiben → ApiUrl2 (v2 REST)

    private readonly int _creationLookupAttempts;
    private readonly TimeSpan _creationLookupDelay;

    private const string TagLiveData     = "LIVE_DATA";
    private const string TagArchivedData = "ARCHIVED_DATA";

    public PrtgGroupSyncService(PrtgConfig config,
        HttpClient? httpV1 = null,
        HttpClient? httpV2 = null,
        int creationLookupAttempts = 30,
        TimeSpan? creationLookupDelay = null)
    {
        _config = config;
        if (creationLookupAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(creationLookupAttempts));
        _creationLookupAttempts = creationLookupAttempts;
        _creationLookupDelay = creationLookupDelay ?? TimeSpan.FromSeconds(2);
        if (_creationLookupDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(creationLookupDelay));

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };

        // V1 – Query-Auth per QueryString
        _httpV1 = httpV1 ?? new HttpClient(handler)
        {
            BaseAddress = new Uri(config.ApiUrl)
        };

        // V2 – Auth per Bearer-Header
        _httpV2 = httpV2 ?? new HttpClient(handler)
        {
            BaseAddress = new Uri(config.ApiUrl2)
        };
        _httpV2.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", config.ApiKey);
        _httpV2.DefaultRequestHeaders.Accept
            .Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // ── Öffentliche Methode ────────────────────────────────────────────────

    public async Task<IReadOnlyList<GroupSyncResult>> SyncAsync(
    IReadOnlyList<PrtgFunctionGroupFile> filesToSync,
    IReadOnlyList<PrtgFunctionGroupFile> allFiles,
    string                               rootGroupName,
    IProgress<SyncProgress>?             progress = null,
    CancellationToken                    ct       = default)
    {
        var results = new List<GroupSyncResult>();

        var rootGroup = await FindGroupAsync(rootGroupName, ct)
                        ?? throw new PrtgApiException(
                            $"Root-Gruppe '{rootGroupName}' wurde in PRTG nicht gefunden.");

        var existingGroups = await GetAllChildGroupsAsync(rootGroup.ObjId, ct);

        var jsonNames = allFiles
            .SelectMany(f => f.Networks)
            .SelectMany(n => new[] { n.Country, n.FunctionGroup, n.Name, n.Type }
                .Where(s => !string.IsNullOrWhiteSpace(s)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Gesamtanzahl vorausberechnen für Progressbar
        var allNetworks = filesToSync.SelectMany(f => f.Networks).ToList();
        var total       = allNetworks.Count;
        var current     = 0;

        foreach (var file in filesToSync)
        {
            var grouped = file.Networks.GroupBy(n => n.Country).ToList();

            foreach (var countryGroup in grouped)
            {
                var country     = countryGroup.Key;
                var countryPrtg = await EnsureGroupAsync(country, rootGroup.ObjId, null, ct);
                var funcGroups  = countryGroup.GroupBy(n => n.FunctionGroup).ToList();

                foreach (var funcGroup in funcGroups)
                {
                    var functionGroupName = funcGroup.Key;
                    var funcPrtg = await EnsureGroupAsync(functionGroupName, countryPrtg.ObjId, null, ct);

                    foreach (var network in funcGroup)
                    {
                        current++;
                        progress?.Report(new SyncProgress(
                            current, total,
                            $"{country} / {functionGroupName} / {network.Name}"));

                        var description = BuildDescription(network);

                        // Level 2
                        var (action2, message2) = await EnsureGroupWithTagAsync(
                            network.Name, funcPrtg.ObjId, description, TagLiveData, ct);

                        results.Add(new GroupSyncResult(
                            action2, country, functionGroupName,
                            network.Name, description, message2));

                        // Level 3: Type
                        if (string.IsNullOrWhiteSpace(network.Type)) continue;

                        var namePrtg = await FindChildGroupAsync(network.Name, funcPrtg.ObjId, ct)
                                       ?? throw new PrtgApiException(
                                           $"Level-2-Gruppe '{network.Name}' nach Sync nicht gefunden.");

                        var (action3, message3) = await EnsureGroupWithTagAsync(
                            network.Type, namePrtg.ObjId, description, TagLiveData, ct);

                        if (action3 == GroupSyncAction.Created)
                        {
                            var typePrtg = await FindChildGroupAsync(network.Type, namePrtg.ObjId, ct);
                            if (typePrtg is not null)
                                await SetDiscoveryPropertiesV1Async(typePrtg.ObjId, network.Network, ct);
                        }

                        results.Add(new GroupSyncResult(
                            action3, country, functionGroupName,
                            $"{network.Name} / {network.Type}", description, message3));
                    }
                }
            }
        }

        foreach (var existing in existingGroups)
        {
            if (!jsonNames.Contains(existing.Name))
            {
                await SetTagV2Async(existing.ObjId, TagArchivedData, ct);
                results.Add(new GroupSyncResult(
                    GroupSyncAction.Archived, "?", "?", existing.Name,
                    "(nicht mehr im JSON)", $"Tag '{TagArchivedData}' gesetzt"));
            }
        }

        return results;
    }

    
    // ── HTTP Helpers ───────────────────────────────────────────────────────

    
    /// <summary>
    /// Setzt IP-Basis und Range für Auto-Discovery, lässt es aber deaktiviert.
    /// discoverytype=0 → keine automatische Suche (Standard)
    /// ipbase         → erste 3 Oktette des Netzwerks (z.B. "10.134.83")
    /// ipstart/ipend  → Bereich 1–254
    /// </summary>
    private async Task SetDiscoveryPropertiesV1Async(
        int objId, string networkIp, CancellationToken ct)
    {
        var ipBase = ExtractIpBase(networkIp);
        if (ipBase is null) return;

        // Alle Properties einzeln setzen – PRTG v1 kennt kein Batch-Set
        await SetPropertyV1Async(objId, "discoverytype", "0",      ct); // deaktiviert
        await SetPropertyV1Async(objId, "ipbase",        ipBase,   ct);
        await SetPropertyV1Async(objId, "ipstart",       "1",      ct);
        await SetPropertyV1Async(objId, "ipend",         "254",    ct);
    }

    private async Task SetPropertyV1Async(
        int objId, string name, string value, CancellationToken ct)
    {
        var url = $"/api/setobjectproperty.htm?id={objId}" +
                  $"&name={name}&value={HttpUtility.UrlEncode(value)}" +
                  $"&{_config.BuildAuthQuery()}";

        await GetV1Async(url, ct);
    }

    /// <summary>"10.134.83.0" → "10.134.83"</summary>
    private static string? ExtractIpBase(string networkIp)
    {
        if (string.IsNullOrWhiteSpace(networkIp)) return null;

        var parts = networkIp.Split('.');
        return parts.Length >= 3
            ? string.Join('.', parts[0], parts[1], parts[2])
            : null;
    }    
    
    /// <summary>V1 GET – wirft PrtgApiException statt roher HttpRequestException</summary>
    private async Task<string> GetV1Async(string url, CancellationToken ct)
    {
        var response = await _httpV1.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            throw new PrtgApiException(
                $"PRTG API v1 Fehler [{(int)response.StatusCode}] auf: {url.Split('?')[0]}");
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>V2 POST mit JSON-Body</summary>
    private async Task<JsonDocument?> PostV2Async(
        string path, object body, CancellationToken ct)
    {
        var requestJson = JsonSerializer.Serialize(body, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        var content  = new StringContent(requestJson, Encoding.UTF8, "application/json");
        var response = await _httpV2.PostAsync(path, content, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new PrtgApiException(
                $"PRTG API v2 POST fehlgeschlagen",
                (int)response.StatusCode,
                $"{_httpV2.BaseAddress}{path.TrimStart('/')}",
                requestJson,
                responseBody);

        // Some PRTG versions acknowledge creation without a JSON body.
        // The caller resolves the created group by its exact parent/name in that case.
        if (string.IsNullOrWhiteSpace(responseBody)) return null;
        try
        {
            return JsonDocument.Parse(responseBody);
        }
        catch (JsonException)
        {
            throw new PrtgApiException(
                "PRTG API v2 POST lieferte keine gültige JSON-Antwort. " +
                "Die Gruppe wurde möglicherweise bereits angelegt; vor Wiederholung prüfen.",
                (int)response.StatusCode, path, requestJson, responseBody);
        }
    }

    private async Task PatchV2Async(string path, object body, CancellationToken ct)
    {
        var requestJson = JsonSerializer.Serialize(body, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        var content  = new StringContent(requestJson, Encoding.UTF8, "application/json");
        var response = await _httpV2.PatchAsync(path, content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            throw new PrtgApiException(
                $"PRTG API v2 PATCH fehlgeschlagen",
                (int)response.StatusCode,
                $"{_httpV2.BaseAddress}{path.TrimStart('/')}",
                requestJson,
                responseBody);
        }
    }

    private static JsonDocument ParseGroupTable(string json, string url)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            // Never include the query string: it contains API credentials.
            throw new PrtgApiException(
                $"PRTG API v1 lieferte {(string.IsNullOrWhiteSpace(json) ? "eine leere" : "keine gültige JSON-")} Antwort bei {url.Split('?')[0]}.");
        }
    }
    // ── PRTG Gruppen-Logik ─────────────────────────────────────────────────

    
    
    private async Task<PrtgGroup?> FindGroupAsync(string name, CancellationToken ct)
    {
        var url = $"/api/table.json?content=groups&columns=objid,name,tags" +
                  $"&filter_name={HttpUtility.UrlEncode(name)}&{_config.BuildAuthQuery()}";

        var json = await GetV1Async(url, ct);
        using var doc = ParseGroupTable(json, url);
        var arr  = doc.RootElement.GetProperty("groups");

        var matches = arr.EnumerateArray()
            .Where(g => string.Equals(g.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 0) return null;
        if (matches.Length > 1)
            throw new PrtgApiException($"Gruppe '{name}' ist in der PRTG-Antwort nicht eindeutig ({matches.Length} Treffer).");

        var g = matches[0];
        return new PrtgGroup(
            g.GetProperty("objid").GetInt32(),
            g.GetProperty("name").GetString() ?? name,
            g.GetProperty("tags").GetString()  ?? "");
    }

    private async Task<IReadOnlyList<PrtgGroup>> GetAllChildGroupsAsync(
        int parentId, CancellationToken ct)
    {
        var url = $"/api/table.json?content=groups&columns=objid,name,tags" +
                  $"&id={parentId}&{_config.BuildAuthQuery()}";

        var json = await GetV1Async(url, ct);
        using var doc = ParseGroupTable(json, url);

        return doc.RootElement
            .GetProperty("groups")
            .EnumerateArray()
            .Select(g => new PrtgGroup(
                g.GetProperty("objid").GetInt32(),
                g.GetProperty("name").GetString() ?? "",
                g.GetProperty("tags").GetString()  ?? ""))
            .ToList();
    }

    private async Task<PrtgGroup?> FindChildGroupAsync(
        string name, int parentId, CancellationToken ct)
    {
        var url = $"/api/table.json?content=groups&columns=objid,name,tags" +
                  $"&id={parentId}&filter_parentid={parentId}&filter_name={HttpUtility.UrlEncode(name)}" +
                  $"&{_config.BuildAuthQuery()}";

        var json = await GetV1Async(url, ct);
        using var doc = ParseGroupTable(json, url);
        var arr  = doc.RootElement.GetProperty("groups");

        var matches = arr.EnumerateArray()
            .Where(g => string.Equals(g.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 0) return null;
        if (matches.Length > 1)
            throw new PrtgApiException($"Gruppe '{name}' ist in der PRTG-Antwort nicht eindeutig ({matches.Length} Treffer).");

        var g = matches[0];
        return new PrtgGroup(
            g.GetProperty("objid").GetInt32(),
            g.GetProperty("name").GetString() ?? name,
            g.GetProperty("tags").GetString()  ?? "");
    }

    private async Task<PrtgGroup> EnsureGroupAsync(
        string name, int parentId, string? description, CancellationToken ct)
    {
        var existing = await FindChildGroupAsync(name, parentId, ct);
        if (existing is not null) return existing;
        return await CreateGroupV2Async(name, parentId, description, ct);
    }


    private async Task<(GroupSyncAction, string?)> EnsureGroupWithTagAsync(
        string name, int parentId, string description,
        string tag, CancellationToken ct)
    {
        var existing = await FindChildGroupAsync(name, parentId, ct);

        if (existing is not null)
        {
            var hasLive     = existing.Tags.Contains(TagLiveData,     StringComparison.OrdinalIgnoreCase);
            var hasArchived = existing.Tags.Contains(TagArchivedData, StringComparison.OrdinalIgnoreCase);

            if (!hasLive && !hasArchived)
                await SetTagV2Async(existing.ObjId, tag, ct);

            return (GroupSyncAction.AlreadyExists, hasLive ? "Tag OK" : "Tag ergänzt");
        }

        var created = await CreateGroupV2Async(name, parentId, description, ct);
        await SetTagV2Async(created.ObjId, tag, ct);

        return (GroupSyncAction.Created, null);
    }

    private async Task SetCommentV1Async(int objId, string comment, CancellationToken ct)
    {
        var url = $"/api/setobjectproperty.htm?id={objId}" +
                  $"&name=comments&value={HttpUtility.UrlEncode(comment)}" +
                  $"&{_config.BuildAuthQuery()}";

        await GetV1Async(url, ct);
    }
    
    private async Task<PrtgGroup> CreateGroupV2Async(
        string name, int parentId, string? description, CancellationToken ct)
    {
        var path = $"/api/v2/experimental/groups/{parentId}/group";
        var body = new { basic = new { name } };

        using var doc = await PostV2Async(path, body, ct);
        var newId = 0;
        if (doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object &&
            doc.RootElement.TryGetProperty("id", out var idElement))
        {
            if (!int.TryParse(idElement.ToString(), out newId) || newId <= 0)
                throw new PrtgApiException($"PRTG lieferte eine ungültige Gruppen-ID bei POST {path}.");
        }

        var newName = name;
        if (newId == 0)
        {
            // POST must never be retried: the group may already have been created.
            // Only repeat read requests while waiting for the v1 table to catch up.
            for (var attempt = 0; attempt < _creationLookupAttempts; attempt++)
            {
                await Task.Delay(_creationLookupDelay, ct);
                var created = await FindChildGroupAsync(name, parentId, ct);
                if (created is null) continue;
                newId = created.ObjId;
                newName = created.Name;
                break;
            }
            if (newId == 0)
                throw new PrtgApiException(
                    $"PRTG bestätigte POST {path} ohne Gruppen-ID. Gruppe '{name}' unter Eltern-ID {parentId} " +
                    "ist nach bis zu {_creationLookupAttempts * _creationLookupDelay.TotalSeconds:0.#} Sekunden noch nicht eindeutig lesbar. " +
                    "Möglicherweise bereits angelegt; vor Wiederholung in PRTG prüfen.");
        }
        else
        {
            await Task.Delay(300, ct);
        }

        // Anmerkung über v1 setzen, falls vorhanden
        if (!string.IsNullOrWhiteSpace(description))
            await SetCommentV1Async(newId, description, ct);

        return new PrtgGroup(newId, newName, "");
    }

    private async Task SetTagV2Async(int objId, string tag, CancellationToken ct)
    {
        await PatchV2Async($"/api/v2/experimental/groups/{objId}",
            new { basic = new { tags = new[] { tag } } }, ct);
    }

    // ── Hilfsmethoden ──────────────────────────────────────────────────────

    private static string BuildDescription(PrtgFunctionGroupNetwork n) =>
        $"VLAN: {n.Vlan} | {n.Network}/{CidrFromNetmask(n.Netmask)} | {n.City} ({n.SiteCode})";

    private static int CidrFromNetmask(string netmask) =>
        netmask.Split('.')
            .Select(int.Parse)
            .Sum(b => Convert.ToString(b, 2).Count(c => c == '1'));

    private record PrtgGroup(int ObjId, string Name, string Tags);
}