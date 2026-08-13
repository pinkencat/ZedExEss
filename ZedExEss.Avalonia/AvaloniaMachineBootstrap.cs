using ZedExEss.Hosting;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.Disk.Beta;
using ZedExEss.Spectrum.Disk.Plus3;
using ZedExEss.Spectrum.DivMmc;
using ZedExEss.Spectrum.Input;
using ZedExEss.Spectrum.Interface1;
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
        return CreateMachine(
            model,
            disks,
            divMmcMedia,
            divExpansionMode,
            interface1Enabled: false,
            SpectrumInterface1RomRevision.Revision2,
            out devices);
    }

    public static SpectrumMachine CreateMachine(
        SpectrumModel model,
        SpectrumDiskMediaState? disks,
        SpectrumDivMmcMediaState? divMmcMedia,
        SpectrumDivExpansionMode divExpansionMode,
        bool interface1Enabled,
        SpectrumInterface1RomRevision interface1RomRevision,
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
                interface1Enabled,
                interface1RomRevision,
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
        bool interface1Enabled,
        SpectrumInterface1RomRevision interface1RomRevision,
        string romDirectory,
        AvaloniaMachineDevices devices)
    {
        ConfigureInterface1(context, interface1Enabled, interface1RomRevision, romDirectory, devices);
        ConfigureDivMmc(context, divMmcMedia, divExpansionMode, romDirectory, devices);

        if (SpectrumModelTraits.HasBeta128Disk(context.Model))
        {
            byte[] trDosRom = LoadTrDosRom(context.Model, romDirectory);
            var betaDevice = new SpectrumBeta128Device(trDosRom);
            var betaController = new SpectrumBeta128DiskController(betaDevice);
            devices.Beta128Device = betaDevice;
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

    private static void ConfigureInterface1(
        SpectrumMachineConfigurationContext context,
        bool enabled,
        SpectrumInterface1RomRevision revision,
        string romDirectory,
        AvaloniaMachineDevices devices)
    {
        if (!enabled || !SpectrumInterface1Compatibility.IsSupported(context.Model))
        {
            return;
        }

        string path = Path.Combine(romDirectory, SpectrumInterface1Compatibility.GetRomFileName(revision));
        var device = new SpectrumInterface1Device(File.ReadAllBytes(path));
        context.Memory.ConfigureInterface1(device);
        context.Ports.AddDevice(device);
        devices.Interface1Device = device;
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
        if (model == SpectrumModel.Scorpion256
            && TryLoadScorpionTrDosRom(romDirectory, out byte[] trDosRom))
        {
            return trDosRom;
        }

        string standalonePath = Path.Combine(romDirectory, "trdos.rom");
        if (TryLoadStandaloneTrDosRom(model, standalonePath, romDirectory, out trDosRom))
        {
            return trDosRom;
        }

        // Some ROM sets bundle a valid TR-DOS image as Scorpion bank 3. The WPF host uses
        // this as its fallback when trdos.rom is actually a duplicate machine ROM bank.
        if (TryLoadScorpionTrDosRom(romDirectory, out trDosRom))
        {
            return trDosRom;
        }

        throw new FileNotFoundException(
            "TR-DOS ROM not found. Expected a valid trdos.rom or TR-DOS bank in scorpion.rom.");
    }

    private static bool TryLoadStandaloneTrDosRom(
        SpectrumModel model,
        string path,
        string romDirectory,
        out byte[] rom)
    {
        rom = [];
        if (!File.Exists(path))
        {
            return false;
        }

        byte[] candidate = File.ReadAllBytes(path);
        if (candidate.Length != RomBankSize
            || IsSameAsModelRomBankZero(model, candidate, romDirectory))
        {
            return false;
        }

        rom = candidate;
        return true;
    }

    private static bool TryLoadScorpionTrDosRom(string romDirectory, out byte[] rom)
    {
        rom = [];
        string path = Path.Combine(romDirectory, "scorpion.rom");
        if (!File.Exists(path))
        {
            return false;
        }

        byte[] combined = File.ReadAllBytes(path);
        if (combined.Length < RomBankSize * 4)
        {
            return false;
        }

        rom = combined.AsSpan(RomBankSize * 3, RomBankSize).ToArray();
        return LooksLikeTrDosRom(rom);
    }

    private static bool IsSameAsModelRomBankZero(
        SpectrumModel model,
        ReadOnlySpan<byte> candidate,
        string romDirectory)
    {
        string? fileName = model switch
        {
            SpectrumModel.Pentagon128 => "pentagon.rom",
            SpectrumModel.Scorpion256 => "scorpion.rom",
            _ => null
        };
        if (fileName == null)
        {
            return false;
        }

        string path = Path.Combine(romDirectory, fileName);
        if (!File.Exists(path))
        {
            return false;
        }

        byte[] combined = File.ReadAllBytes(path);
        return combined.Length >= RomBankSize
            && candidate.SequenceEqual(combined.AsSpan(0, RomBankSize));
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
