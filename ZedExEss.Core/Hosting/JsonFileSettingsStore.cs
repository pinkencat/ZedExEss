using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZedExEss.Hosting;

/// <summary>Cross-platform JSON implementation of <see cref="ISettingsStore"/>.</summary>
/// <remarks>
/// Loading is deliberately fault tolerant: a missing, truncated or newer hand-edited settings
/// file must never prevent the emulator from starting. Saving uses a sibling temporary file so
/// an interrupted write cannot leave a partially written settings document.
/// </remarks>
public sealed class JsonFileSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly string _path;

    public JsonFileSettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public EmulatorHostSettings Load()
    {
        if (!File.Exists(_path))
        {
            return new EmulatorHostSettings();
        }

        try
        {
            string json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<EmulatorHostSettings>(json, SerializerOptions)
                ?? new EmulatorHostSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new EmulatorHostSettings();
        }
    }

    public void Save(EmulatorHostSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            string json = JsonSerializer.Serialize(settings, SerializerOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
