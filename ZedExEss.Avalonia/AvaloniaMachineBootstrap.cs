using ZedExEss.Hosting;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.Disk.Beta;
using ZedExEss.Spectrum.Disk.Plus3;
using ZedExEss.Spectrum.DivMmc;
using ZedExEss.Spectrum.Input;
using ZedExEss.Spectrum.Memory;

namespace ZedExEss.AvaloniaHost;

/// <summary>
/// Builds portable Spectrum machines while keeping ROM discovery and host preferences in the
/// Avalonia layer. Optional disk controllers are attached before the CPU bus is finalised so a
/// selected model boots with the same hardware graph as the established WPF host.
/// </summary>
internal static class AvaloniaMachineBootstrap
{
    private const int RomBankSize = 16 * 1024;

    public static SpectrumMachine CreateDefaultMachine(SpectrumDiskMediaState? disks = null)
    {
        return CreateMachine(SpectrumModel.Spectrum128K, disks);
    }

    public static SpectrumMachine CreateMachine(SpectrumModel model, SpectrumDiskMediaState? disks = null)
    {
        return CreateMachine(model, disks, out _);
    }

    public static SpectrumMachine CreateMachine(
        SpectrumModel model,
        SpectrumDiskMediaState? disks,
        out AvaloniaMachineDevices devices)
    {
        return CreateMachine(
            model,
            disks,
            null,
            SpectrumDivExpansionMode.Disabled,
            out devices);
    }

    public static SpectrumMachine CreateMachine(
        SpectrumModel model,
        SpectrumDiskMediaState? disks,
        SpectrumDivMmcMediaState? divMmcMedia,
        SpectrumDivExpansionMode divExpansionMode,
        out AvaloniaMachineDevices devices)
    {
        EmulatorHostSettings settings = CreateSettingsStore().Load();
        SpectrumJoystickType joystick = Enum.IsDefined(typeof(SpectrumJoystickType), settings.JoystickType)
            ? settings.JoystickType
            : SpectrumJoystickType.None;
        string romDirectory = Path.Combine(AppContext.BaseDirectory, "ROMs");
        RomSet roms = LoadRoms(model, romDirectory);

        var createdDevices = new AvaloniaMachineDevices();
        SpectrumMachine machine = SpectrumMachineFactory.Create(new SpectrumMachineOptions
        {
            Model = model,
            Roms = roms,
            JoystickType = joystick,
            ConfigureDevices = context => ConfigureOptionalDevices(
                context,
                disks,
                divMmcMedia,
                divExpansionMode,
                romDirectory,
                createdDevices)
        });
        devices = createdDevices;
        return machine;
    }

    private static RomSet LoadRoms(SpectrumModel model, string romDirectory)
    {
        return model switch
        {
            SpectrumModel.Pentagon128 => RomSet.LoadFromCombinedFile(
                Path.Combine(romDirectory, "pentagon.rom"),
                SpectrumModelTraits.RomBankCount(model)),
            SpectrumModel.Scorpion256 => RomSet.LoadFromCombinedFile(
                Path.Combine(romDirectory, "scorpion.rom"),
                SpectrumModelTraits.RomBankCount(model)),
            _ => RomSet.LoadFromFiles(GetRomFileNames(model)
                .Select(name => Path.Combine(romDirectory, name))
                .ToArray())
        };
    }

    private static void ConfigureOptionalDevices(
        SpectrumMachineConfigurationContext context,
        SpectrumDiskMediaState? disks,
        SpectrumDivMmcMediaState? divMmcMedia,
        SpectrumDivExpansionMode divExpansionMode,
        string romDirectory,
        AvaloniaMachineDevices devices)
    {
        ConfigureDivMmc(context, divMmcMedia, divExpansionMode, romDirectory, devices);

        if (SpectrumModelTraits.HasBeta128Disk(context.Model))
        {
            byte[] trDosRom = LoadTrDosRom(context.Model, romDirectory);
            var betaDevice = new SpectrumBeta128Device(trDosRom);
            var betaController = new SpectrumBeta128DiskController(betaDevice);
            devices.BetaDiskController = betaController;
            context.Memory.ConfigureBeta128(betaDevice);

            for (int drive = 0; drive < 2; drive++)
            {
                TrdDiskImage? image = disks?.GetTrdImage(drive);
                if (image != null)
                {
                    betaController.InsertDisk(drive, image);
                }
            }

            context.Ports.AddDevice(betaController);
        }

        if (SpectrumModelTraits.HasPlus3Disk(context.Model))
        {
            var plus3Controller = new SpectrumPlus3DiskController();
            devices.Plus3DiskController = plus3Controller;
            for (int drive = 0; drive < 2; drive++)
            {
                Plus3DiskImage? image = disks?.GetPlus3Image(drive);
                if (image != null)
                {
                    plus3Controller.InsertDisk(drive, image);
                }
            }

            context.Ports.AddDevice(plus3Controller);
        }
    }

    private static void ConfigureDivMmc(
        SpectrumMachineConfigurationContext context,
        SpectrumDivMmcMediaState? media,
        SpectrumDivExpansionMode mode,
        string romDirectory,
        AvaloniaMachineDevices devices)
    {
        if (mode != SpectrumDivExpansionMode.DivMmc)
        {
            media?.ConnectDevice(null);
            return;
        }

        byte[] firmware = File.ReadAllBytes(Path.Combine(romDirectory, "divmmc.rom"));
        var device = new SpectrumDivMmcDevice(SpectrumDivExpansionMode.DivMmc, firmware, ramBankCount: 16)
        {
            // Beta 128 and DivMMC both trap the TR-DOS entry window. Give a model's native
            // Beta interface ownership of that address while retaining all normal DivMMC traps.
            AutomapTrDosEntryEnabled = !SpectrumModelTraits.HasBeta128Disk(context.Model)
        };
        device.PowerOn();
        media?.ConnectDevice(device);
        context.Memory.ConfigureDivExpansion(device);
        context.Ports.AddDevice(device);
        devices.DivMmcDevice = device;
    }

    private static byte[] LoadTrDosRom(SpectrumModel model, string romDirectory)
    {
        if (model == SpectrumModel.Scorpion256)
        {
            string scorpionPath = Path.Combine(romDirectory, "scorpion.rom");
            byte[] combined = File.ReadAllBytes(scorpionPath);
            if (combined.Length >= RomBankSize * 4)
            {
                byte[] embedded = combined.AsSpan(RomBankSize * 3, RomBankSize).ToArray();
                if (LooksLikeTrDosRom(embedded))
                {
                    return embedded;
                }
            }
        }

        string standalonePath = Path.Combine(romDirectory, "trdos.rom");
        byte[] standalone = File.ReadAllBytes(standalonePath);
        if (standalone.Length != RomBankSize)
        {
            throw new InvalidDataException($"TR-DOS ROM must be exactly {RomBankSize} bytes.");
        }

        return standalone;
    }

    private static bool LooksLikeTrDosRom(ReadOnlySpan<byte> rom)
    {
        return ContainsAscii(rom, "TR-DOS") || ContainsAscii(rom, "BETA 128");
    }

    private static bool ContainsAscii(ReadOnlySpan<byte> data, string value)
    {
        ReadOnlySpan<byte> needle = System.Text.Encoding.ASCII.GetBytes(value);
        return data.IndexOf(needle) >= 0;
    }

    private static string[] GetRomFileNames(SpectrumModel model)
    {
        return model switch
        {
            SpectrumModel.Spectrum16K or SpectrumModel.Spectrum48K => ["48.rom"],
            // Preserve the packaged filenames exactly; Linux and macOS use case-sensitive
            // lookups even though the Windows reference host accepts either spelling.
            SpectrumModel.Spectrum128K => ["128_0.ROM", "128_1.ROM"],
            SpectrumModel.SpectrumPlus2 => ["plus2_0.rom", "plus2_1.rom"],
            SpectrumModel.SpectrumPlus2A or SpectrumModel.SpectrumPlus3 =>
                ["plus3-0.rom", "plus3-1.rom", "plus3-2.rom", "plus3-3.rom"],
            _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Unsupported Spectrum model.")
        };
    }

    private static ISettingsStore CreateSettingsStore()
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZedExEss",
            "settings.json");
        return new JsonFileSettingsStore(path);
    }
}
