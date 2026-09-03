using System.Text.Json;
using System.Text.Json.Serialization;

namespace ServerLauncher.Core.Storage;

/// <summary>
/// Reads and writes JSON configuration atomically. A crash partway through a save
/// must never leave the user with a truncated servers.json and no way back, so
/// writes go to a temp file and are swapped in, keeping the previous version as .bak.
/// </summary>
public static class JsonStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static T Load<T>(string path, Func<T> createDefault)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var value = JsonSerializer.Deserialize<T>(json, Options);
                if (value is not null)
                {
                    return value;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Fall through to the backup below.
        }

        var backup = path + ".bak";
        try
        {
            if (File.Exists(backup))
            {
                var value = JsonSerializer.Deserialize<T>(File.ReadAllText(backup), Options);
                if (value is not null)
                {
                    return value;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
        }

        return createDefault();
    }

    public static void Save<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(value, Options);
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);

        if (File.Exists(path))
        {
            // Replace keeps a copy of what was there before, in case the new file is bad.
            File.Replace(temp, path, path + ".bak", ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temp, path);
        }
    }
}
