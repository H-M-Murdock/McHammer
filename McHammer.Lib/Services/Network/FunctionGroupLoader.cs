using System.Text.Json;
using McHammer.Lib.Models.Network;

namespace McHammer.Lib.Services.Network;

public class FunctionGroupLoader : IFunctionGroupLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<IReadOnlyList<PrtgFunctionGroupFile>> LoadAllAsync(
        string            directoryPath,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(directoryPath))
            throw new DirectoryNotFoundException($"Verzeichnis nicht gefunden: {directoryPath}");

        var files   = Directory.GetFiles(directoryPath, "*_USED.json");
        var results = new List<PrtgFunctionGroupFile>();

        foreach (var file in files.OrderBy(f => f))
        {
            ct.ThrowIfCancellationRequested();

            var json   = await File.ReadAllTextAsync(file, ct);
            var parsed = JsonSerializer.Deserialize<PrtgFunctionGroupFile>(json, JsonOptions);

            if (parsed is not null)
                results.Add(parsed);
        }

        return results;
    }
}