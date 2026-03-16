namespace McHammer.Lib.Models.Network;

public record PrtgDeviceTemplate(string Name, string DisplayName, string Description);

public static class PrtgDeviceTemplates
{
    public static readonly IReadOnlyList<PrtgDeviceTemplate> All =
    [
        new("TAL-Template-BasicDevice", "TAL Basic Device", "Basis-Template"),
    ];
}