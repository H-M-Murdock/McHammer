namespace McHammer.Lib.Models.Network;

public record PrtgFunctionGroupFile(
    string                            FunctionGroup,
    string                            Filter,
    int                               TotalCount,
    DateTime                          GeneratedAtUtc,
    IReadOnlyList<PrtgFunctionGroupNetwork> Networks
);