using Avalonia.Input;
using Avalonia.Threading;
using ZedExEss.FileHandlers;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.DivMmc;

namespace ZedExEss.AvaloniaHost;

/// <summary>
/// Composes portable ROM flash loading and autoload injection with Avalonia's execution owner.
/// The hooks run at instruction boundaries; UI and runner changes are always marshalled back to
/// the dispatcher so an audio callback cannot dispose its own execution driver.
/// </summary>
public sealed partial class MainWindow
{
    private const ushort AutoLoad48KReadyPc = 0x10B0;
    private const ushort AutoLoad128ReadyPc = 0x3683;
    private const ushort AutoLoadPlus2ReadyPc = 0x36A9;
    private const ushort AutoLoadPlus3ReadyPc = 0x1875;
    private const int AutoLoadDefaultInitialDelayFrames = 4;
    private const int AutoLoadDefaultKeySpacingFrames = 5;
    private const int AutoLoadPentagonInitialDelayFrames = 40;
    private const int AutoLoadPlus3InitialDelayFrames = 40;
    private static readonly byte[] AutoLoadBasic48Command = [0xEF, 0x22, 0x22, 0x0D];
    private static readonly byte[] AutoLoadCode48Command = [0xEF, 0x22, 0x22, 0xAF, 0x0D];
    private static readonly byte[] AutoLoadEnterCommand = [0x0D];
    private static readonly byte[] AutoLoadCode128Command =
        [0x0A, 0x0D, 0x6C, 0x6F, 0x61, 0x64, 0x20, 0x22, 0x22, 0x20, 0x63, 0x6F, 0x64, 0x65, 0x0D];

    private SpectrumAutoLoadKeyboardInjector? _autoLoadInjector;

    private bool BeforeCpuStep()
    {
        if (BeforeDebuggerCpuStep())
        {
            return true;
        }

        AdvanceAutoLoadInjector();

        SpectrumMachine? machine = _machine;
        TzxLoader? tape = _session.Tape;
        if (_flashLoadEnabled && machine != null && tape != null
            && SpectrumRomTapeLoader.TryFlashLoad(machine, tape))
        {
            QueueTapeRuntimeRefresh();
            return true;
        }

        TryStartTapeForRomLoader();
        return false;
    }

    private void AfterCpuStep()
    {
        if (_autoTapePlayStopEnabled && _machine?.Cpu.IsHalted == true)
        {
            StopTapeMotorFromAutoPlay();
        }

        AfterDebuggerCpuStep();
    }

    private void AdvanceAutoLoadInjector()
    {
        SpectrumAutoLoadKeyboardInjector? injector = _autoLoadInjector;
        if (injector == null)
        {
            return;
        }

        injector.Tick();
        if (!injector.IsComplete)
        {
            return;
        }

        _autoLoadInjector = null;
        Dispatcher.UIThread.Post(UpdateDebuggerHooks, DispatcherPriority.Background);
    }

    private void OnTapeAutoPlayRequested()
    {
        if (_autoTapePlayStopEnabled)
        {
            TryStartTapeMotorFromAutoPlay();
        }
    }

    private bool TryStartTapeForRomLoader()
    {
        SpectrumMachine? machine = _machine;
        if (!_autoTapePlayStopEnabled || machine == null
            || !SpectrumRomTapeLoader.IsLdBytesEntry(machine.Cpu, machine.Memory))
        {
            return false;
        }

        return TryStartTapeMotorFromAutoPlay();
    }

    private bool TryStartTapeMotorFromAutoPlay()
    {
        TzxLoader? tape = _session.Tape;
        if (tape == null || tape.IsPlaying)
        {
            return false;
        }

        tape.Play();
        QueueTapeRuntimeRefresh();
        return true;
    }

    private void StopTapeMotorFromAutoPlay()
    {
        TzxLoader? tape = _session.Tape;
        if (tape == null || !tape.IsPlaying)
        {
            return;
        }

        tape.Stop();
        QueueTapeRuntimeRefresh();
    }

    private void QueueTapeRuntimeRefresh()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_closing)
            {
                return;
            }

            RefreshExecutionOwner();
            UpdateTapeControls();
        }, DispatcherPriority.Background);
    }

    private void TryStartAutoLoadAfterTapeAttach()
    {
        SpectrumMachine? currentMachine = _machine;
        if (!_autoLoadTapeOnAttach || currentMachine == null || _session.Tape == null
            || _session.TapePath == null || IsAutoLoadSuppressedByShift()
            || _divExpansionMode != SpectrumDivExpansionMode.Disabled)
        {
            return;
        }

        SpectrumModel model = currentMachine.Model;
        if (!TryCreateAutoLoadProfile(
                model,
                out byte[] command,
                out ushort readyPc,
                out int? expectedRomBank,
                out int initialDelayFrames,
                out int keySpacingFrames,
                out bool ejectPlus3Disks))
        {
            return;
        }

        if (ejectPlus3Disks)
        {
            EjectPlus3DisksForAutoLoad();
        }

        ReplaceMachine(
            model,
            preserveTape: true,
            rewindTape: true,
            beforeStart: replacement =>
            {
                _autoLoadInjector = new SpectrumAutoLoadKeyboardInjector(
                    replacement.Cpu,
                    replacement.Memory,
                    readyPc,
                    expectedRomBank,
                    command,
                    initialDelayFrames * replacement.TstatesPerFrame,
                    keySpacingFrames * replacement.TstatesPerFrame);
            });
    }

    private bool TryCreateAutoLoadProfile(
        SpectrumModel model,
        out byte[] command,
        out ushort readyPc,
        out int? expectedRomBank,
        out int initialDelayFrames,
        out int keySpacingFrames,
        out bool ejectPlus3Disks)
    {
        bool codeHeader = _session.Tape != null
            && SpectrumRomTapeLoader.TryGetFirstStandardBlock(_session.Tape, out byte[] firstBlock)
            && IsCodeHeader(firstBlock);
        readyPc = AutoLoad48KReadyPc;
        expectedRomBank = 0;
        command = AutoLoadBasic48Command;
        initialDelayFrames = AutoLoadDefaultInitialDelayFrames;
        keySpacingFrames = AutoLoadDefaultKeySpacingFrames;
        ejectPlus3Disks = false;

        switch (model)
        {
            case SpectrumModel.Spectrum16K:
            case SpectrumModel.Spectrum48K:
                command = codeHeader ? AutoLoadCode48Command : AutoLoadBasic48Command;
                return true;

            case SpectrumModel.Spectrum128K:
            case SpectrumModel.Pentagon128:
                readyPc = AutoLoad128ReadyPc;
                command = codeHeader ? AutoLoadCode128Command : AutoLoadEnterCommand;
                initialDelayFrames = model == SpectrumModel.Pentagon128
                    ? AutoLoadPentagonInitialDelayFrames
                    : AutoLoadDefaultInitialDelayFrames;
                return true;

            case SpectrumModel.SpectrumPlus2:
                readyPc = AutoLoadPlus2ReadyPc;
                command = codeHeader ? AutoLoadCode128Command : AutoLoadEnterCommand;
                return true;

            case SpectrumModel.SpectrumPlus2A:
            case SpectrumModel.SpectrumPlus3:
                readyPc = AutoLoadPlus3ReadyPc;
                command = AutoLoadEnterCommand;
                initialDelayFrames = AutoLoadPlus3InitialDelayFrames;
                ejectPlus3Disks = true;
                return true;

            // Scorpion ROM/menu behavior is not compatible with this LAST_K profile.
            case SpectrumModel.Scorpion256:
            default:
                expectedRomBank = null;
                return false;
        }
    }

    private bool IsAutoLoadSuppressedByShift()
    {
        return _pressedHostKeys.Contains(Key.LeftShift) || _pressedHostKeys.Contains(Key.RightShift);
    }

    private void EjectPlus3DisksForAutoLoad()
    {
        for (int drive = 0; drive < 2; drive++)
        {
            if (_session.Disks.GetPlus3Image(drive) == null)
            {
                continue;
            }

            _session.Disks.EjectPlus3(drive);
            _machineDevices?.Plus3DiskController?.EjectDisk(drive);
        }

        UpdateDiskControls();
    }

    private static bool IsCodeHeader(ReadOnlySpan<byte> data)
    {
        return data.Length == 19 && data[0] == 0 && data[1] == 3;
    }
}
