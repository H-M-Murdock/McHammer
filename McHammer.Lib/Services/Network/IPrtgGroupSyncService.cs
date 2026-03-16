using McHammer.Lib.Models.Network;
namespace McHammer.Lib.Services.Network;

public interface IPrtgGroupSyncService
{
    Task<IReadOnlyList<GroupSyncResult>> SyncAsync(
        IReadOnlyList<PrtgFunctionGroupFile> filesToSync,
        IReadOnlyList<PrtgFunctionGroupFile> allFiles,
        string                               rootGroupName,
        IProgress<SyncProgress>?             progress = null,
        CancellationToken                    ct       = default);
}