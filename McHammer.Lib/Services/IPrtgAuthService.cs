using McHammer.Lib.Models;

namespace McHammer.Lib.Services;

public interface IPrtgAuthService
{
    Task<PrtgAuthResult> AuthenticateAsync(CancellationToken ct = default);
}