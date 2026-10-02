using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Chaos.Worker.Infrastructure;

public sealed class ChaosAdminKey
{
    private readonly byte[] _hash;

    public ChaosAdminKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length < 32)
            throw new InvalidOperationException("Chaos:AdminApiKey must contain an independently generated secret of at least 32 characters");
        _hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
    }

    public bool Matches(string candidate) =>
        CryptographicOperations.FixedTimeEquals(_hash, SHA256.HashData(Encoding.UTF8.GetBytes(candidate)));
}

public sealed class ChaosAdminAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, ChaosAdminKey key) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ChaosApiKey";
    public const string PolicyName = "ChaosAdmin";
    public const string HeaderName = "X-Chaos-Api-Key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values))
            return Task.FromResult(AuthenticateResult.NoResult());
        if (values.Count != 1 || !key.Matches(values[0] ?? ""))
            return Task.FromResult(AuthenticateResult.Fail("Invalid administrative credential"));
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "chaos-admin"), new Claim("permission", "chaos.admin")], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
