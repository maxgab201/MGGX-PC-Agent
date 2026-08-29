using System.Text.Json;

namespace MGGX.PCAgent.Core;

public static class AgentConfigLoader
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static AgentConfig Load(string directory, Action<string>? warning = null)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.json");
        if (File.Exists(path))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path), Options);
                if (parsed?.IsValid() == true) return parsed;
                warning?.Invoke("Configuration was invalid; secure defaults were restored.");
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                warning?.Invoke("Configuration could not be read; secure defaults were restored.");
            }
        }
        var defaults = new AgentConfig();
        File.WriteAllText(path, JsonSerializer.Serialize(defaults, Options));
        return defaults;
    }
}
