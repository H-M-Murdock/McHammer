namespace McHammer.Lib.Models;

public record PrtgAuthResult(
    bool   Success,
    string Version,
    string StatusInfo,
    string Message
);