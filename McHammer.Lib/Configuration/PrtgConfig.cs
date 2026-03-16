namespace McHammer.Lib.Configuration;

public class PrtgConfig
{
    public string ApiUrl       { get; init; } = string.Empty;
    public string ApiUrl2      { get; init; } = string.Empty;
    public string ApiKey       { get; init; } = string.Empty;
    public string PasHash     { get; init; } = string.Empty;
    public string Password     { get; init; } = string.Empty;
    public string User         { get; init; } = string.Empty;

    public string BuildAuthQuery()
    {
        if (!string.IsNullOrWhiteSpace(ApiKey))
            return $"apitoken={Uri.EscapeDataString(ApiKey)}";

        if (!string.IsNullOrWhiteSpace(User) && !string.IsNullOrWhiteSpace(PasHash))
            return $"username={Uri.EscapeDataString(User)}&passhash={Uri.EscapeDataString(PasHash)}";

        if (!string.IsNullOrWhiteSpace(User) && !string.IsNullOrWhiteSpace(Password))
            return $"username={Uri.EscapeDataString(User)}&password={Uri.EscapeDataString(Password)}";

        throw new InvalidOperationException("Keine gültige Auth-Konfiguration gefunden.");
    }    
    
    public static PrtgConfig FromEnvironment() => new()
    {
        ApiUrl   = Env("PRTG_API"),
        ApiUrl2  = Env("PRTG_APIV2"),
        ApiKey   = Env("PRTG_APIKEY"),
        PasHash = Env("PRTG_HASH"),
        Password = Env("PRTG_PASSWORD"),
        User     = Env("PRTG_USER")
    };

    private static string Env(string key) =>
        Environment.GetEnvironmentVariable(key)
        ?? throw new InvalidOperationException($"Umgebungsvariable '{key}' fehlt.");
}