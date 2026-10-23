namespace ZedExEss.Hosting;

/// <summary>Loads and saves host preferences without tying a frontend to a storage format.</summary>
public interface ISettingsStore
{
    EmulatorHostSettings Load();

    void Save(EmulatorHostSettings settings);
}
