using System.Text.Json;

namespace QuickAdmin.Shared;

public class ConfigStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();

    public ConfigStore(string path) => _path = path;

    public QuickAdminConfig Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                var config = BuildDefault();
                Save(config);
                return config;
            }
            return JsonSerializer.Deserialize<QuickAdminConfig>(File.ReadAllText(_path)) ?? BuildDefault();
        }
    }

    public void Save(QuickAdminConfig config)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(config, _jsonOptions));
        }
    }

    public static QuickAdminConfig BuildDefault() => new()
    {
        Auth = new AuthConfig
        {
            PasswordHash = "yUAuxbbms7h1S1pU5UHFi59CeIqLNOFDO0EGuIawESQ=",
            PasswordSalt = "VNhPF6aKKVvGmYw0lMco0g==",
            Iterations = 100000
        },
        Server = new ServerConfig { BindAddress = "127.0.0.1", Port = 8600, AllowLanAccess = false },
        SessionTimeoutMinutes = 15,
        CustomCommands = [],
        WolTargets = []
    };
}
