using System.Security.Cryptography;
using System.Text;

namespace QuickAdmin.Shared;

public class QuickAdminConfig
{
    public AuthConfig Auth { get; set; } = new();
    public ServerConfig Server { get; set; } = new();
    public int SessionTimeoutMinutes { get; set; } = 15;
    public List<CustomCommand> CustomCommands { get; set; } = [];
    public List<WolTarget> WolTargets { get; set; } = [];
}

public class AuthConfig
{
    public string PasswordHash { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    public int Iterations { get; set; } = 100000;
}

public class ServerConfig
{
    public string BindAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8600;
    public bool AllowLanAccess { get; set; }
}

public class CustomCommand
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "";
    public string Command { get; set; } = "";
    public string Shell { get; set; } = "cmd";
}

public class WolTarget
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Mac { get; set; } = "";
    public string Host { get; set; } = "255.255.255.255";
    public int Port { get; set; } = 9;
}

public static class PasswordUtil
{
    public static (string hash, string salt) HashPassword(string password, int iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public static bool Verify(string password, AuthConfig auth)
    {
        var salt = Convert.FromBase64String(auth.PasswordSalt);
        var expected = Convert.FromBase64String(auth.PasswordHash);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, auth.Iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
