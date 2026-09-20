using System.Security.Cryptography;
using System.Text;

namespace LoanApp.Core.Application;
public sealed class ServiceAccess
{
    public bool Cloud { get; }
    private readonly string? token;
    public string? Origin { get; }
    public ServiceAccess(string? profile, string environment, string? secret, string? origin = null, bool backend = false)
    {
        profile ??= environment == "Development" ? "LocalDevelopment" : "";
        Cloud = profile == "CloudDemo";
        if (profile is not ("LocalDevelopment" or "CloudDemo") || !Cloud && environment != "Development" || Cloud && environment != "Production")
            throw new InvalidOperationException("Invalid application profile/environment.");
        if (!Cloud) return;
        token = RequireToken(secret);
        if (backend) Origin = RequireOrigin(origin).GetLeftPart(UriPartial.Authority);
    }
    public static string RequireToken(string? value)
    {
        try { if (value is null || value.Length != 44 || Convert.FromBase64String(value).Length != 32 || Convert.ToBase64String(Convert.FromBase64String(value)) != value) throw new FormatException(); }
        catch (FormatException) { throw new InvalidOperationException("A canonical Base64 256-bit service token is required."); }
        return value;
    }
    public static Uri RequireOrigin(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
            throw new InvalidOperationException("An HTTPS origin is required."); return uri;
    }
    public Guid? Authorize(bool loopback, bool health, string? supplied, bool mutation, string? origin, string? session, bool backend)
    {
        if (!Cloud) { if (!loopback) throw new PolicyError(403, "LOCAL_ONLY"); return null; }
        if (health) return null;
        if (supplied is null || supplied.Length > 128 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied)), SHA256.HashData(Encoding.UTF8.GetBytes(token!))))
            throw new PolicyError(401, "SERVICE_UNAUTHORIZED");
        if (!backend) return null;
        if (!Guid.TryParse(session, out var id) || id == Guid.Empty) throw new PolicyError(401, "SESSION_REQUIRED");
        if (mutation && origin != Origin) throw new PolicyError(403, "ORIGIN_NOT_ALLOWED");
        return id;
    }
}
