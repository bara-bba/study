using System.Text.Json;
using System.Text.Json.Serialization;

namespace Study.Core.Config;

/// <summary>
/// Reads/writes study's JSON files (settings, PLC list, watches) in one folder —
/// <c>%APPDATA%\study</c> by default. Writes go to a temp file first and are then swapped in,
/// so a crash mid-save can never leave a half-written config behind.
/// </summary>
public sealed class JsonStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public JsonStore(string folder)
    {
        Folder = folder;
    }

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "study");

    public string Folder { get; }

    public string PathFor(string fileName) => Path.Combine(Folder, fileName);

    /// <summary>Loads <paramref name="fileName"/>; returns <paramref name="fallback"/> if missing.</summary>
    /// <exception cref="ConfigException">The file exists but is not valid JSON for the type.</exception>
    public T Load<T>(string fileName, Func<T> fallback)
    {
        var path = PathFor(fileName);
        if (!File.Exists(path))
        {
            return fallback();
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? fallback();
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"{path} is not valid: {ex.Message}", ex);
        }
    }

    public void Save<T>(string fileName, T value) =>
        SaveText(fileName, JsonSerializer.Serialize(value, Options));

    /// <summary>Raw text of <paramref name="fileName"/>, or null when it does not exist.</summary>
    public string? LoadText(string fileName)
    {
        var path = PathFor(fileName);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>Atomic write: temp file first, then swapped in.</summary>
    public void SaveText(string fileName, string text)
    {
        Directory.CreateDirectory(Folder);
        var path = PathFor(fileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }
}

public sealed class ConfigException : Exception
{
    public ConfigException(string message, Exception inner) : base(message, inner)
    {
    }
}
