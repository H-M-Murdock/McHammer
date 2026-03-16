namespace McHammer.Lib.Models.Network;

public enum GroupSyncAction
{
    Created,
    AlreadyExists,
    Archived
}

public record GroupSyncResult(
    GroupSyncAction Action,
    string          Country,
    string          FunctionGroup,
    string          Name,
    string          Description,
    string?         Message = null
);