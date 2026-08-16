using System.Diagnostics;
using System.Globalization;
using System.Text;
using ZedExEss.Hosting;
using ZedExEss.Spectrum.Input;
using ZedExEss.Zx8x.Memory;
using ZedExEss.Zx8x.Video;

namespace ZedExEss.Diagnostics;

/// <summary>Output settings for portable host-settings verification.</summary>
public sealed class SettingsVerificationOptions
{
    public string? OutputPath { get; init; }
}

/// <summary>Verifies JSON settings defaults, round-tripping, overwriting and recovery.</summary>
public static class SettingsVerificationRunner
{
    public static int Run(SettingsVerificationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        string outputPath = Path.GetFullPath(options.OutputPath ?? "settings-verification.log");
        string? outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        string temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ZedExEss-settings-{Guid.NewGuid():N}");
        string settingsPath = Path.Combine(temporaryDirectory, "nested", "settings.json");
        Directory.CreateDirectory(temporaryDirectory);

        using var writer = new StreamWriter(outputPath, false, Encoding.UTF8) { AutoFlush = true };
        int failed = 0;

        try
        {
            writer.WriteLine("Portable settings verification");
            writer.WriteLine($"Output: {outputPath}");
            writer.WriteLine();

            var store = new JsonFileSettingsStore(settingsPath);
            Check("Missing settings return application defaults", () => VerifyDefaults(store), ref failed);
            Check("Settings round-trip with readable enum values", () => VerifyRoundTrip(store, settingsPath), ref failed);
            Check("A subsequent save atomically replaces the document", () => VerifyOverwrite(store), ref failed);
            Check("Malformed JSON falls back to defaults", () => VerifyMalformedRecovery(store, settingsPath), ref failed);

            writer.WriteLine();
            writer.WriteLine(failed == 0
                ? "Result: PASS"
                : $"Result: FAIL ({failed.ToString(CultureInfo.InvariantCulture)} failed checks)");
            return failed == 0 ? 0 : 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            writer.WriteLine($"Error: {ex.Message}");
            Debug.WriteLine(ex.ToString());
            return 3;
        }
        finally
        {
            try
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Cleanup failure does not alter the test result.
            }
            catch (UnauthorizedAccessException)
            {
                // Cleanup failure does not alter the test result.
            }
        }

        void Check(string name, Action check, ref int failureCount)
        {
            try
            {
                check();
                writer.WriteLine($"PASS {name}");
            }
            catch (Exception ex)
            {
                failureCount++;
                writer.WriteLine($"FAIL {name}: {ex.Message}");
            }
        }
    }

    private static void VerifyDefaults(ISettingsStore store)
    {
        EmulatorHostSettings settings = store.Load();
        Require(settings.SchemaVersion == EmulatorHostSettings.CurrentSchemaVersion, "Unexpected settings schema.");
        Require(settings.ScreenZoom == 2.0, "Unexpected default zoom.");
        Require(settings.TapeBrowserVisible, "Tape browser should be visible by default.");
        Require(settings.FlashLoadEnabled && settings.PollingLoaderAccelerationEnabled,
            "Established tape acceleration defaults were not preserved.");
        Require(!settings.SemanticLoaderAccelerationEnabled, "Experimental semantic acceleration must default off.");
        Require(settings.FastForwardSpeed == 4, "Fast-forward should default to 4x.");
        Require(settings.Zx8xRamConfiguration == Zx8xRamConfiguration.Expansion16K,
            "ZX80/ZX81 RAM should default to the common 16 KiB expansion.");
        Require(settings.Zx8xHighResolutionMode == Zx8xHighResolutionMode.Sinclair,
            "Optional WRX hardware must default off.");
    }

    private static void VerifyRoundTrip(ISettingsStore store, string settingsPath)
    {
        var expected = new EmulatorHostSettings
        {
            ScreenZoom = 3.5,
            TapeBrowserVisible = false,
            JoystickType = SpectrumJoystickType.Kempston,
            FlashLoadEnabled = false,
            PollingLoaderAccelerationEnabled = false,
            SemanticLoaderAccelerationEnabled = true,
            RunTapeAccelerationAtMaximumSpeed = false,
            AutoLoadTapeOnAttach = true,
            AutoTapePlayStopEnabled = false,
            DirtyLinePresentationEnabled = false,
            GigascreenBlendEnabled = true,
            FastForwardSpeed = 7,
            Interface1Enabled = true,
            Interface1RomRevision = Spectrum.Interface1.SpectrumInterface1RomRevision.Revision1,
            Zx8xRamConfiguration = Zx8xRamConfiguration.Internal1K,
            Zx8xHighResolutionMode = Zx8xHighResolutionMode.Wrx
        };

        store.Save(expected);
        EmulatorHostSettings actual = store.Load();
        Require(actual == expected, "Round-tripped settings differ from the saved values.");

        string json = File.ReadAllText(settingsPath);
        Require(json.Contains("\"Kempston\"", StringComparison.Ordinal), "Enum values should be human-readable JSON strings.");
    }

    private static void VerifyOverwrite(ISettingsStore store)
    {
        var replacement = new EmulatorHostSettings
        {
            ScreenZoom = 1.5,
            JoystickType = SpectrumJoystickType.Cursor
        };

        store.Save(replacement);
        Require(store.Load() == replacement, "Replacement settings were not loaded.");
    }

    private static void VerifyMalformedRecovery(ISettingsStore store, string settingsPath)
    {
        File.WriteAllText(settingsPath, "{ this is not valid JSON");
        EmulatorHostSettings recovered = store.Load();
        Require(recovered == new EmulatorHostSettings(), "Malformed JSON did not recover to defaults.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
