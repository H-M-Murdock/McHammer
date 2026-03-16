namespace McHammer.Lib.Models.Network;

public record PrtgFunctionGroupNetwork(
    string SiteCode,
    string Country,
    string FunctionGroup,
    string City,
    string Shortname,
    string Type,
    string Name,
    string Network,
    string Netmask,
    int    Vlan,
    string LanId,
    string Status
);