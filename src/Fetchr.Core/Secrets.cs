using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fetchr.Core;

/// <summary>
/// Windows DPAPI, current-user scope: only this Windows account on this machine can decrypt.
/// Used for archive passwords and the browser cookies a download needs to resume.
/// </summary>
public static class Secrets
{
    private static readonly byte[] Entropy = "Fetchr.v1"u8.ToArray();

    public static byte[] Protect(string plain) =>
        OperatingSystem.IsWindows()
            ? ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser)
            : throw new PlatformNotSupportedException("Secrets need Windows DPAPI.");

    public static string? Unprotect(byte[]? data)
    {
        if (data is null || !OperatingSystem.IsWindows()) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException) { return null; } // another user's or a damaged value: treat as absent
    }

    public static byte[]? ProtectHeaders(IReadOnlyDictionary<string, string>? headers) =>
        headers is { Count: > 0 } ? Protect(JsonSerializer.Serialize(headers)) : null;

    public static Dictionary<string, string> UnprotectHeaders(byte[]? data) =>
        Unprotect(data) is { } json ? JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [] : [];
}

/// <summary>Only these request headers are ever replayed from the browser.</summary>
public static class RequestHeaders
{
    public static readonly string[] Allowed = ["Cookie", "User-Agent", "Authorization"];

    public static Dictionary<string, string> Filter(IReadOnlyDictionary<string, string>? headers) =>
        headers?.Where(h => Allowed.Contains(h.Key, StringComparer.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(h.Value)
                            && !h.Value.Contains('\r') && !h.Value.Contains('\n'))
            .ToDictionary(h => Allowed.First(a => a.Equals(h.Key, StringComparison.OrdinalIgnoreCase)), h => h.Value)
        ?? [];

    public static void Apply(HttpRequestMessage req, IReadOnlyDictionary<string, string> headers)
    {
        foreach (var (k, v) in headers)
        {
            req.Headers.Remove(k);
            req.Headers.TryAddWithoutValidation(k, v);
        }
    }
}
