using McHammer.Lib.Models.Network;

namespace McHammer.Lib.Services.Network;

public interface IFunctionGroupLoader
{
    Task<IReadOnlyList<PrtgFunctionGroupFile>> LoadAllAsync(
        string directoryPath,
        CancellationToken ct = default);
}