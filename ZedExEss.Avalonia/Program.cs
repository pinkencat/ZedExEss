using Avalonia;

namespace ZedExEss.AvaloniaHost;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(static argument => argument.Equals("--smoke-test", StringComparison.OrdinalIgnoreCase)))
        {
            return RunSmokeTest();
        }

        if (args.Any(static argument => argument.Equals("--audio-smoke-test", StringComparison.OrdinalIgnoreCase)))
        {
            return RunAudioSmokeTest();
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
    }

    private static int RunSmokeTest()
    {
        string? temporaryTapePath = null;
        string? temporaryDskPath = null;
        string? temporaryTrdPath = null;
        string? temporarySdPath = null;
        try
        {
            var disks = new Spectrum.Core.SpectrumDiskMediaState();
            foreach (Spectrum.Core.SpectrumModel model in Enum.GetValues<Spectrum.Core.SpectrumModel>())
            {
                Spectrum.Core.SpectrumMachine machine = AvaloniaMachineBootstrap.CreateMachine(model, disks);
                ulong before = machine.Cpu.Cyc;
                machine.Emulator.RunFrame(presentFrame: false);
                if (machine.Cpu.Cyc <= before)
                {
                    Console.Error.WriteLine($"Avalonia host smoke test failed: {model} did not advance.");
                    return 1;
                }

                Console.WriteLine($"  {model}: {machine.Cpu.Cyc - before} T-states");
            }

            var gigascreen = new Spectrum.Video.GigascreenFrameBlender(pixelCount: 1);
            int[] firstGigascreenFrame = [unchecked((int)0xFF000000)];
            int[] secondGigascreenFrame = [unchecked((int)0xFFFFFFFF)];
            if (!ReferenceEquals(gigascreen.Compose(firstGigascreenFrame), firstGigascreenFrame)
                || gigascreen.Compose(secondGigascreenFrame)[0] != unchecked((int)0xFF7F7F7F))
            {
                Console.Error.WriteLine("Avalonia host smoke test failed: gigascreen frame blending is incorrect.");
                return 1;
            }

            if (!Spectrum.Memory.SpectrumPokeParser.TryParse(
                    "POKE $8000,$2A,3\n#9000 255 ; comment",
                    out IReadOnlyList<Spectrum.Memory.SpectrumPokeEntry> pokes,
                    out _)
                || pokes.Count != 2
                || pokes[0] != new Spectrum.Memory.SpectrumPokeEntry(0x8000, 0x2A, 3)
                || Spectrum.Memory.SpectrumPokeParser.TryParse("$FFFF 0 2", out _, out _))
            {
                Console.Error.WriteLine("Avalonia host smoke test failed: portable poke parsing is incorrect.");
                return 1;
            }

            var scope = new Spectrum.Audio.AudioScopeCapture(capacity: 4);
            scope.WriteSamples(123, [1, 2], [3, 4], [5, 6], sampleCount: 2);
            short[] scopeBeeper = new short[2];
            short[] scopeA = new short[2];
            short[] scopeB = new short[2];
            short[] scopeC = new short[2];
            if (scope.CopyLatest(scopeBeeper, scopeA, scopeB, scopeC, sampleCount: 2) != 2
                || scopeBeeper[0] != 123 || scopeBeeper[1] != 123
                || scopeA[0] != 1 || scopeA[1] != 2
                || scopeB[0] != 3 || scopeB[1] != 4
                || scopeC[0] != 5 || scopeC[1] != 6)
            {
                Console.Error.WriteLine("Avalonia host smoke test failed: oscilloscope capture channels are incorrect.");
                return 1;
            }

            // Exercise the same portable tape ownership used by the window. A minimal TAP block
            // is sufficient to prove that replacement reconstructs the loader for the new EAR
            // device instead of retaining references to the previous machine.
            temporaryTapePath = Path.Combine(Path.GetTempPath(), $"zedexess-avalonia-{Guid.NewGuid():N}.tap");
            File.WriteAllBytes(temporaryTapePath,
            [
                3, 0, 0xFF, 0x42, 0xBD,
                3, 0, 0xFF, 0x43, 0xBC
            ]);
            var session = new Spectrum.Core.SpectrumSessionController();
            session.ReplaceMachine(
                AvaloniaMachineBootstrap.CreateMachine(Spectrum.Core.SpectrumModel.Spectrum48K, session.Disks),
                preserveTape: false);
            session.LoadTape(temporaryTapePath);

            // Exercise the portable LD_BYTES trap against a real 48K ROM. Besides checking the
            // copied byte and return PC, this guards the important block-position rule: the next
            // playable block must be selected and running after an instantaneous standard block.
            Spectrum.Core.SpectrumMachine flashMachine = session.Machine;
            flashMachine.Cpu.PC = 0x0558;
            flashMachine.Cpu.A_ = 0xFF;
            flashMachine.Cpu.F_ = 0x01;
            flashMachine.Cpu.IX = 0x8000;
            flashMachine.Cpu.D = 0;
            flashMachine.Cpu.E = 1;
            if (!Spectrum.Core.SpectrumRomTapeLoader.TryFlashLoad(flashMachine, session.Tape!)
                || flashMachine.Memory.ReadDirect(0x8000) != 0x42
                || flashMachine.Cpu.PC != 0x05E2
                || session.Tape!.CurrentBlockIndex != 1
                || !session.Tape.IsPlaying)
            {
                Console.Error.WriteLine("Avalonia host smoke test failed: portable ROM flash loading did not advance to the next block.");
                return 1;
            }

            flashMachine.Cpu.PC = 0x10B0;
            flashMachine.Memory.WriteDirect(0x5C3B, 0);
            var injector = new Spectrum.Core.SpectrumAutoLoadKeyboardInjector(
                flashMachine.Cpu,
                flashMachine.Memory,
                readyPc: 0x10B0,
                expectedRomBank: 0,
                command: [0x0D],
                initialDelayTstates: 0,
                keySpacingTstates: 1);
            injector.Tick();
            injector.Tick();
            if (!injector.IsComplete
                || flashMachine.Memory.ReadDirect(0x5C08) != 0x0D
                || (flashMachine.Memory.ReadDirect(0x5C3B) & 0x20) == 0)
            {
                Console.Error.WriteLine("Avalonia host smoke test failed: portable autoload injection did not feed LAST_K.");
                return 1;
            }

            session.ReplaceMachine(
                AvaloniaMachineBootstrap.CreateMachine(Spectrum.Core.SpectrumModel.Spectrum128K, session.Disks),
                preserveTape: true);
            if (session.Tape == null || session.Tape.Blocks.Count != 2)
            {
                Console.Error.WriteLine("Avalonia host smoke test failed: tape was not preserved across model replacement.");
                return 1;
            }

            session.EjectTape();
            if (session.Tape != null || session.TapePath != null)
            {
                Console.Error.WriteLine("Avalonia host smoke test failed: tape ejection left session state attached.");
                return 1;
            }

            // Exercise portable disk lifecycle and prove that the host receives the controller
            // references belonging to each newly built machine rather than stale previous ones.
            temporaryDskPath = Path.Combine(Path.GetTempPath(), $"zedexess-avalonia-{Guid.NewGuid():N}.dsk");
            temporaryTrdPath = Path.Combine(Path.GetTempPath(), $"zedexess-avalonia-{Guid.NewGuid():N}.trd");
            ZedExEss.Spectrum.Disk.Plus3.Plus3DiskImage.CreateBlankPlus3DataDisk(temporaryDskPath);
            File.WriteAllBytes(temporaryTrdPath, new byte[80 * 2 * 16 * 256]);
            disks.LoadPlus3(0, temporaryDskPath);
            disks.LoadTrd(0, temporaryTrdPath);

            _ = AvaloniaMachineBootstrap.CreateMachine(
                Spectrum.Core.SpectrumModel.SpectrumPlus3,
                disks,
                out AvaloniaMachineDevices plus3Devices);
            Spectrum.Core.SpectrumMachine betaMachine = AvaloniaMachineBootstrap.CreateMachine(
                Spectrum.Core.SpectrumModel.Pentagon128,
                disks,
                out AvaloniaMachineDevices betaDevices);
            if (plus3Devices.Plus3DiskController == null
                || betaDevices.Beta128Device == null
                || betaDevices.BetaDiskController == null)
            {
                Console.Error.WriteLine("Avalonia host smoke test failed: optional disk controllers were not exposed.");
                return 1;
            }

            bool betaMatchesPentagonRomZero = true;
            for (ushort address = 0; address < 0x4000; address++)
            {
                if (betaDevices.Beta128Device.ReadMemory(address) != betaMachine.Memory.ReadDirect(address))
                {
                    betaMatchesPentagonRomZero = false;
                    break;
                }
            }

            if (betaMatchesPentagonRomZero)
            {
                Console.Error.WriteLine("Avalonia host smoke test failed: Pentagon machine ROM was selected as TR-DOS ROM.");
                return 1;
            }

            // Pentagon enters TR-DOS by fetching through 3Dxx while its 48K ROM is selected.
            // Verify that the Avalonia-built machine exposes the same automap path as WPF.
            betaMachine.Memory.WritePort7FFD(0x10);
            _ = betaMachine.Memory.FetchOpcode(0x3D00);
            if (!betaDevices.Beta128Device.IsPaged)
            {
                Console.Error.WriteLine("Avalonia host smoke test failed: Pentagon TR-DOS automap did not page the Beta ROM.");
                return 1;
            }

            Spectrum.Core.SpectrumMachine betaMenuMachine = AvaloniaMachineBootstrap.CreateMachine(
                Spectrum.Core.SpectrumModel.Pentagon128,
                new Spectrum.Core.SpectrumDiskMediaState(),
                out AvaloniaMachineDevices betaMenuDevices);
            bool sawBetaRom = false;
            betaMenuMachine.Emulator.ConfigureBeforeCpuStep(() =>
            {
                sawBetaRom |= betaMenuDevices.Beta128Device?.IsPaged == true;
                return false;
            });
            RunFrames(betaMenuMachine, 160);
            for (int i = 0; i < 4; i++)
            {
                PressSpectrumKeys(
                    betaMenuMachine,
                    [Spectrum.Input.SpectrumKey.CapsShift, Spectrum.Input.SpectrumKey.D6],
                    heldFrames: 5,
                    releasedFrames: 10);
            }

            PressSpectrumKeys(
                betaMenuMachine,
                [Spectrum.Input.SpectrumKey.Enter],
                heldFrames: 5,
                releasedFrames: 10);
            RunFrames(betaMenuMachine, 20);
            if (!sawBetaRom || betaMenuMachine.Memory.ReadDirect(0x5C3A) == 0x03)
            {
                Console.Error.WriteLine("Avalonia host smoke test failed: Pentagon menu entered BASIC error 4 instead of TR-DOS.");
                return 1;
            }

            disks.EjectPlus3(0);
            disks.EjectTrd(0);
            if (disks.GetPlus3Image(0) != null || disks.GetTrdImage(0) != null)
            {
                Console.Error.WriteLine("Avalonia host smoke test failed: disk ejection left portable media attached.");
                return 1;
            }

            // DivMMC media belongs to the portable session, not the transient device. Rebuild
            // the machine and prove the same mounted card is reconnected to the replacement.
            temporarySdPath = Path.Combine(Path.GetTempPath(), $"zedexess-avalonia-{Guid.NewGuid():N}.img");
            File.WriteAllBytes(temporarySdPath, new byte[512 * 4]);
            session.DivMmc.Attach(temporarySdPath, folderBacked: false);
            _ = AvaloniaMachineBootstrap.CreateMachine(
                Spectrum.Core.SpectrumModel.Spectrum128K,
                session.Disks,
                session.DivMmc,
                ZedExEss.Spectrum.DivMmc.SpectrumDivExpansionMode.DivMmc,
                out AvaloniaMachineDevices firstDivDevices);
            _ = AvaloniaMachineBootstrap.CreateMachine(
                Spectrum.Core.SpectrumModel.Spectrum48K,
                session.Disks,
                session.DivMmc,
                ZedExEss.Spectrum.DivMmc.SpectrumDivExpansionMode.DivMmc,
                out AvaloniaMachineDevices replacementDivDevices);
            if (firstDivDevices.DivMmcDevice == null || replacementDivDevices.DivMmcDevice == null
                || !session.DivMmc.IsAttached)
            {
                Console.Error.WriteLine("Avalonia host smoke test failed: DivMMC media was not preserved across replacement.");
                return 1;
            }

            session.DivMmc.Eject();
            if (session.DivMmc.IsAttached || session.DivMmc.Path != null)
            {
                Console.Error.WriteLine("Avalonia host smoke test failed: DivMMC ejection left media attached.");
                return 1;
            }

            Console.WriteLine("Avalonia host smoke test passed for all models and portable tape/disk/DivMMC lifecycle.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 2;
        }
        finally
        {
            if (temporaryTapePath != null && File.Exists(temporaryTapePath))
            {
                File.Delete(temporaryTapePath);
            }

            if (temporaryDskPath != null && File.Exists(temporaryDskPath))
            {
                File.Delete(temporaryDskPath);
            }

            if (temporaryTrdPath != null && File.Exists(temporaryTrdPath))
            {
                File.Delete(temporaryTrdPath);
            }

            if (temporarySdPath != null && File.Exists(temporarySdPath))
            {
                File.Delete(temporarySdPath);
            }
        }
    }

    private static void RunFrames(Spectrum.Core.SpectrumMachine machine, int frameCount)
    {
        for (int i = 0; i < frameCount; i++)
        {
            machine.Emulator.RunFrame(presentFrame: false);
        }
    }

    private static void PressSpectrumKeys(
        Spectrum.Core.SpectrumMachine machine,
        ReadOnlySpan<Spectrum.Input.SpectrumKey> keys,
        int heldFrames,
        int releasedFrames)
    {
        foreach (Spectrum.Input.SpectrumKey key in keys)
        {
            machine.Keyboard.SetKeyState(key, pressed: true);
        }

        RunFrames(machine, heldFrames);
        foreach (Spectrum.Input.SpectrumKey key in keys)
        {
            machine.Keyboard.SetKeyState(key, pressed: false);
        }

        RunFrames(machine, releasedFrames);
    }

    private static int RunAudioSmokeTest()
    {
        try
        {
            Spectrum.Core.SpectrumMachine machine = AvaloniaMachineBootstrap.CreateDefaultMachine();
            ulong before = machine.Cpu.Cyc;
            using var output = new SdlAudioOutput(machine.Emulator, machine.SampleRate, 512, 4);
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (machine.Cpu.Cyc == before && output.Failure == null
                && timeout.Elapsed < TimeSpan.FromSeconds(3))
            {
                Thread.Sleep(10);
            }

            if (output.Failure != null)
            {
                throw output.Failure;
            }

            if (machine.Cpu.Cyc == before)
            {
                Console.Error.WriteLine("SDL audio smoke test failed: audio demand did not advance the machine.");
                return 1;
            }

            ulong afterPrime = machine.Cpu.Cyc;
            Thread.Sleep(250);
            if (!output.IsRunning || machine.Cpu.Cyc <= afterPrime)
            {
                Console.Error.WriteLine("SDL audio smoke test failed: playback demand stopped after priming.");
                return 1;
            }

            Console.WriteLine(
                $"SDL audio smoke test passed ({machine.Cpu.Cyc - before} T-states generated while the device consumed PCM).");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 2;
        }
    }
}
