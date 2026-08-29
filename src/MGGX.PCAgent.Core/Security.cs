using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace MGGX.PCAgent.Core;

public interface ITokenStore { string GetOrCreate(); }

public sealed class DpapiTokenStore(string directory) : ITokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MGGX.PCAgent.Token.v1");
    private readonly string _path = Path.Combine(directory, "agent-token.bin");

    public string GetOrCreate()
    {
        Directory.CreateDirectory(directory);
        if (File.Exists(_path))
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.LocalMachine));

        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.LocalMachine);
        File.WriteAllBytes(_path, protectedBytes);
        RestrictAcl(_path);
        return token;
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void RestrictAcl(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }
}

public static class TokenComparer
{
    public static bool IsValid(string? supplied, string expected)
    {
        if (string.IsNullOrWhiteSpace(supplied)) return false;
        var actualHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied.Trim()));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }
}

public static class LogSanitizer
{
    private static readonly System.Text.RegularExpressions.Regex Bearer = new("(?i)Bearer\\s+[A-Za-z0-9._~+/-]+=*", System.Text.RegularExpressions.RegexOptions.Compiled);
    public static string Sanitize(string text) => Bearer.Replace(text, "Bearer [REDACTED]");
}
