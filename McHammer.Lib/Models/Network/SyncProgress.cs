namespace McHammer.Lib.Services.Network;

public record SyncProgress(
    int    Current,
    int    Total,
    string CurrentItem
);