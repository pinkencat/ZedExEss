using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ZedExEss.Hosting;
using ZedExEss.Spectrum.Audio;
using ZedExEss.Spectrum.Basic;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.Debugging;
using ZedExEss.Spectrum.Memory;
using ZedExEss.Spectrum.DivMmc;
using ZedExEss.Spectrum.Multiface;
using ZedExEss.ZxNext.Core;

namespace ZedExEss.AvaloniaHost;

public sealed partial class MainWindow
{
    private readonly SpectrumDebuggerController _debugger = new();
    private readonly Z80Disassembler _debuggerDisassembler = new();
    private readonly Z80Disassembler _z80nDisassembler = new(enableZ80N: true);
    private readonly Z80InlineAssembler _debuggerAssembler = new();
    private DebuggerWindow? _debuggerWindow;
    private AudioOscilloscopeWindow? _oscilloscopeWindow;

    private void InitializeTooling()
    {
        _debugger.BreakHit += OnDebuggerBreakHit;
        _debugger.HooksChanged += OnDebuggerHooksChanged;
    }

    private void ShutdownTooling()
    {
        _debugger.BreakHit -= OnDebuggerBreakHit;
        _debugger.HooksChanged -= OnDebuggerHooksChanged;
        _debuggerWindow?.Close();
        _debuggerWindow = null;
        _oscilloscopeWindow?.AttachAudioSource(null);
        _oscilloscopeWindow?.Close();
        _oscilloscopeWindow = null;
    }

    private void AttachDebuggerToMachine(SpectrumMachine machine)
    {
        _debugger.Attach(machine.Cpu, machine.Memory, machine.Ports, machine.Model);
        UpdateDebuggerHooks();
        _debuggerWindow?.RefreshAll(followPc: true);
    }

    private async void OnBasicEditorClicked(object? sender, RoutedEventArgs e)
    {
        if (_zxNextMachine != null)
        {
            _statusText.Text = "The BASIC editor does not yet support ZX Spectrum Next state.";
            return;
        }

        if (_zx8xMachine != null)
        {
            _statusText.Text = "The BASIC editor currently supports Sinclair Spectrum BASIC only.";
            return;
        }

        SpectrumMachine? machine = _machine;
        if (machine == null)
        {
            return;
        }

        ToolRunState runState = SuspendMachineForTool();
        try
        {
            var service = new SpectrumBasicMemoryService(machine.Memory, machine.Model);
            var editor = new SpectrumBasicEditorSession(service);
            var window = new BasicProgramWindow(editor);
            await window.ShowDialog(this);
        }
        finally
        {
            RestoreMachineAfterTool(runState);
            Focus();
        }
    }

    private async void OnPokesClicked(object? sender, RoutedEventArgs e)
    {
        if (_zxNextMachine != null)
        {
            _statusText.Text = "ZX Spectrum Next MMU-aware memory editing is not implemented yet.";
            return;
        }

        if (_zx8xMachine != null)
        {
            _statusText.Text = "ZX80/ZX81 memory editing will be exposed through the portable debugger path.";
            return;
        }

        SpectrumMachine? machine = _machine;
        if (machine == null)
        {
            return;
        }

        ToolRunState runState = SuspendMachineForTool();
        string? successStatus = null;
        try
        {
            var window = new PokeWindow();
            bool accepted = await window.ShowDialog<bool>(this);
            if (accepted)
            {
                int written = 0;
                foreach (SpectrumPokeEntry poke in window.Pokes)
                {
                    for (int offset = 0; offset < poke.Count; offset++)
                    {
                        machine.Memory.WriteDirect((ushort)(poke.Address + offset), poke.Value);
                        written++;
                    }
                }

                successStatus = $"Applied {window.Pokes.Count} poke entr{(window.Pokes.Count == 1 ? "y" : "ies")} ({written} byte{(written == 1 ? string.Empty : "s")}).";
            }
        }
        finally
        {
            RestoreMachineAfterTool(runState);
            Focus();
        }

        if (successStatus != null)
        {
            _statusText.Text = successStatus;
        }
    }

    private void OnAudioOscilloscopeClicked(object? sender, RoutedEventArgs e)
    {
        if (_zx8xMachine != null)
        {
            _statusText.Text = "The current oscilloscope displays Spectrum beeper and AY channels.";
            return;
        }

        if (_oscilloscopeWindow != null)
        {
            _oscilloscopeWindow.Activate();
            return;
        }

        var window = new AudioOscilloscopeWindow();
        window.AttachAudioSource(GetAudioScopeSource());
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_oscilloscopeWindow, window))
            {
                _oscilloscopeWindow = null;
            }
        };
        _oscilloscopeWindow = window;
        window.Show(this);
    }

    private void OnScorpionTurboClicked(object? sender, RoutedEventArgs e)
    {
        if (_updatingCommandChecks || _scorpionTurboMenuItem == null)
        {
            return;
        }

        SpectrumMachine? machine = _machine;
        if (machine == null || !machine.SupportsTurbo)
        {
            _scorpionTurboMenuItem.IsChecked = false;
            return;
        }

        // This is the front-panel toggle: a hardware latch the running program does not
        // have to be interrupted for.
        machine.TurboEnabled = _scorpionTurboMenuItem.IsChecked;
    }

    private void SelectMultiface(SpectrumMultifaceModel model)
    {
        if (model == _multifaceModel)
        {
            return;
        }

        // Only the variant built for the running machine can be fitted. Nothing else
        // about the machine's configuration matters: a Multiface coexists with Interface
        // 1, a disk interface, or any of the sound expansions.
        if (model != SpectrumMultifaceModel.None
            && _machine != null
            && model != SpectrumModelTraits.SupportedMultiface(_machine.Model))
        {
            UpdateMultifaceMenuState();
            return;
        }

        if (model != SpectrumMultifaceModel.None
            && AvaloniaMachineBootstrap.LoadMultifaceRom(model).IsEmpty)
        {
            _statusText.Text =
                $"Multiface needs {SpectrumModelTraits.GetMultifaceRomFileName(model)} in the ROMs folder.";
            UpdateMultifaceMenuState();
            return;
        }

        _multifaceModel = model;
        if (model != SpectrumMultifaceModel.None)
        {
            // A Multiface and DivMMC overlay the same low memory and their control ports
            // overlap, so fitting one unfits the other.
            _divExpansionMode = SpectrumDivExpansionMode.Disabled;
        }

        if (_machine != null)
        {
            ReplaceMachine(_machine.Model, preserveTape: true, rewindTape: false);
        }
    }

    private void UpdateMultifaceMenuState()
    {
        if (_multifaceDisabledMenuItem == null)
        {
            return;
        }

        SpectrumMultifaceModel supported = SpectrumModelTraits.SupportedMultiface(
            _machine?.Model ?? SpectrumModel.Spectrum48K);
        bool available = _zxNextMachine == null && _zx8xMachine == null;

        _multifaceDisabledMenuItem.IsChecked = _multifaceModel == SpectrumMultifaceModel.None;
        _multifaceDisabledMenuItem.IsEnabled = available;
        SetMultifaceItem(_multifaceOneMenuItem, SpectrumMultifaceModel.One, supported, available);
        SetMultifaceItem(_multiface128MenuItem, SpectrumMultifaceModel.OneTwentyEight, supported, available);
        SetMultifaceItem(_multifaceThreeMenuItem, SpectrumMultifaceModel.Three, supported, available);
        // The magic button is never gated: pressing it is the whole point of the
        // interface, and a press with nothing fitted is a harmless no-op.
    }

    private void SetMultifaceItem(
        MenuItem? item,
        SpectrumMultifaceModel model,
        SpectrumMultifaceModel supported,
        bool available)
    {
        if (item == null)
        {
            return;
        }

        item.IsChecked = _multifaceModel == model;

        // The other variants stay visible but disabled, so it is clear which one this
        // machine takes.
        item.IsEnabled = available && model == supported;
    }

    private void OnGeneralSoundEnabledClicked(object? sender, RoutedEventArgs e)
    {
        if (_updatingCommandChecks || _generalSoundEnabledMenuItem == null)
        {
            return;
        }

        // Fitting the card changes the port map, so the machine is rebuilt.
        _generalSoundEnabled = _generalSoundEnabledMenuItem.IsChecked;
        if (_machine != null)
        {
            ReplaceMachine(_machine.Model, preserveTape: true, rewindTape: false);
        }
    }

    private void OnCovoxEnabledClicked(object? sender, RoutedEventArgs e)
    {
        if (_updatingCommandChecks || _covoxEnabledMenuItem == null)
        {
            return;
        }

        // Fitting the DAC changes the port map, so the machine is rebuilt the same way
        // an Interface 1 or DivMMC change rebuilds it.
        _covoxEnabled = _covoxEnabledMenuItem.IsChecked;
        if (_machine != null)
        {
            ReplaceMachine(_machine.Model, preserveTape: true, rewindTape: false);
        }
    }

    /// <summary>The audio block of whichever machine family is currently running.</summary>
    private IAudioScopeSource? GetAudioScopeSource() =>
        _zxNextMachine != null ? _zxNextMachine.Audio : _machine?.Audio;

    private void OnDebuggerClicked(object? sender, RoutedEventArgs e)
    {
        if (_machine == null && _zx8xMachine == null && _zxNextMachine == null)
        {
            return;
        }

        if (_debuggerWindow != null)
        {
            _debuggerWindow.Activate();
            _debuggerWindow.RefreshAll(followPc: true);
            return;
        }

        // The Next needs the Z80N table and contributes its own MMU and NextReg views.
        var view = new SpectrumDebuggerViewService(
            _debugger,
            _zxNextMachine != null ? _z80nDisassembler : _debuggerDisassembler,
            _debuggerAssembler)
        {
            MachineExtensions = _zxNextMachine != null
                ? new ZxNextDebuggerExtensions(_zxNextMachine)
                : null
        };
        var window = new DebuggerWindow(view, _fileDialogs);
        window.RunRequested += ResumeFromDebugger;
        window.PauseRequested += () => PauseForDebugger("Paused", notifyController: true);
        window.StepIntoRequested += StepDebuggerInto;
        window.StepOverRequested += StepDebuggerOver;
        window.StepSourceLineRequested += StepDebuggerSourceLine;
        window.StepSourceLineOverRequested += StepDebuggerSourceLineOver;
        window.RunToAddressRequested += RunDebuggerToAddress;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_debuggerWindow, window))
            {
                _debuggerWindow = null;
            }

            UpdateDebuggerHooks();
        };
        _debuggerWindow = window;
        window.Show(this);
        UpdateDebuggerHooks();
    }

    private bool BeforeDebuggerCpuStep()
    {
        if (!_debugger.Enabled || !_debugger.BeforeCpuStep())
        {
            return false;
        }

        _machine?.Emulator.SetPaused(true);
        _zx8xMachine?.SetPaused(true);
        _zxNextMachine?.SetPaused(true);
        RequestDebuggerPause();
        return true;
    }

    private void AfterDebuggerCpuStep()
    {
        if (!_debugger.Enabled)
        {
            return;
        }

        _debugger.AfterCpuStep();
        if (_debugger.IsPaused)
        {
            _machine?.Emulator.SetPaused(true);
            _zx8xMachine?.SetPaused(true);
            _zxNextMachine?.SetPaused(true);
            RequestDebuggerPause();
        }
    }

    private void OnDebuggerHooksChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            UpdateDebuggerHooks();
        }
        else
        {
            Dispatcher.UIThread.Post(UpdateDebuggerHooks);
        }
    }

    private void UpdateDebuggerHooks()
    {
        Zx8x.Core.Zx8xMachine? zx8x = _zx8xMachine;
        if (zx8x != null)
        {
            if (_closing)
            {
                return;
            }

            zx8x.ConfigureCpuStepHooks(
                _debugger.Enabled ? BeforeDebuggerCpuStep : null,
                _debugger.Enabled ? AfterDebuggerCpuStep : null);
            zx8x.Cpu.ConfigureDebugHook(_debugger.AccessWatchpointsEnabled ? _debugger : null);
            return;
        }

        ZxNextMachine? next = _zxNextMachine;
        if (next != null)
        {
            next.ConfigureCpuStepHooks(
                _debugger.Enabled ? BeforeDebuggerCpuStep : null,
                _debugger.Enabled ? AfterDebuggerCpuStep : null);
            next.Cpu.ConfigureDebugHook(_debugger.AccessWatchpointsEnabled ? _debugger : null);
            return;
        }

        SpectrumMachine? machine = _machine;
        if (machine == null || _closing)
        {
            return;
        }

        bool before = _debugger.Enabled
            || _flashLoadEnabled
            || _flashSaveEnabled
            || _autoTapePlayStopEnabled
            || _autoLoadInjector != null;
        bool after = _debugger.Enabled || _autoTapePlayStopEnabled;
        machine.Emulator.ConfigureCpuStepHooks(
            before ? BeforeCpuStep : null,
            after ? AfterCpuStep : null);
        machine.Cpu.ConfigureDebugHook(_debugger.AccessWatchpointsEnabled ? _debugger : null);
    }

    private void OnDebuggerBreakHit(DebuggerBreakHit hit) => RequestDebuggerPause();

    private void RequestDebuggerPause()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_closing)
            {
                PauseForDebugger(_debugger.LastHit?.Reason ?? "Debugger break", notifyController: false);
            }
        });
    }

    private void PauseForDebugger(string reason, bool notifyController)
    {
        SpectrumMachine? machine = _machine;
        Zx8x.Core.Zx8xMachine? zx8x = _zx8xMachine;
        ZxNextMachine? next = _zxNextMachine;
        if (next != null)
        {
            // The Next owns its own execution thread, so stop that before pausing: the
            // runner must not still be inside an instruction when we refresh.
            StopExecutionOwner();
            next.SetPaused(true);
            if (notifyController)
            {
                _debugger.Pause(reason);
            }

            UpdateDebuggerHooks();
            UpdatePauseCommandState(paused: true);
            _statusText.Text = $"ZX Spectrum Next — {reason}";
            _debuggerWindow?.RefreshAll(followPc: true);
            return;
        }

        if (machine == null && zx8x == null)
        {
            return;
        }

        StopExecutionOwner();
        machine?.Emulator.SetPaused(true);
        zx8x?.SetPaused(true);
        if (notifyController)
        {
            _debugger.Pause(reason);
        }

        UpdateDebuggerHooks();
        UpdatePauseCommandState(paused: true);
        _statusText.Text = machine != null
            ? $"{FormatModel(machine.Model)} — {reason}"
            : $"{FormatZx8xModel(zx8x!.Model)} — {reason}";
        _debuggerWindow?.RefreshAll(followPc: true);
    }

    private void ResumeFromDebugger()
    {
        SpectrumMachine? machine = _machine;
        Zx8x.Core.Zx8xMachine? zx8x = _zx8xMachine;
        ZxNextMachine? next = _zxNextMachine;
        if (next != null)
        {
            _debugger.Run();
            next.SetPaused(false);
            UpdateDebuggerHooks();
            if (!HasExecutionOwner())
            {
                _ = StartSelectedZxNextExecution(next);
            }

            UpdatePauseCommandState(paused: false);
            _statusText.Text = $"ZX Spectrum Next — {GetExecutionModeText()} — keyboard active";
            _debuggerWindow?.RefreshAll(followPc: true);
            return;
        }

        if (machine == null && zx8x == null)
        {
            return;
        }

        _debugger.Run();
        machine?.Emulator.SetPaused(false);
        zx8x?.SetPaused(false);
        UpdateDebuggerHooks();
        if (!HasExecutionOwner())
        {
            if (machine != null)
            {
                _ = StartSelectedExecution(machine);
            }
            else
            {
                _ = StartSelectedZx8xExecution(zx8x!);
            }
        }

        UpdatePauseCommandState(paused: false);
        _statusText.Text = machine != null
            ? $"{FormatModel(machine.Model)} — {GetExecutionModeText()} — keyboard active"
            : $"{FormatZx8xModel(zx8x!.Model)} — {GetExecutionModeText()} — keyboard active";
        _debuggerWindow?.RefreshAll(followPc: true);
    }

    /// <summary>
    /// Runs until execution reaches a different line of the loaded source.
    /// </summary>
    /// <remarks>
    /// Built on the single-instruction step rather than beside it, so it inherits the
    /// per-machine handling the instruction step already does. The refresh is left until the
    /// end: a source line can be hundreds of instructions, and redrawing the whole debugger
    /// for each of them would cost far more than the stepping.
    /// </remarks>
    private void StepDebuggerSourceLine() => RunSourceStep(stepOverCalls: false);

    /// <summary>
    /// The same, but calls made along the way are run to completion rather than entered.
    /// </summary>
    /// <remarks>
    /// The only difference is which primitive the loop is given, which is why both share one
    /// implementation: stepping over is stepping, with a different idea of one step.
    /// </remarks>
    private void StepDebuggerSourceLineOver() => RunSourceStep(stepOverCalls: true);

    private void RunSourceStep(bool stepOverCalls)
    {
        DebuggerWindow? window = _debuggerWindow;
        if (window == null)
        {
            return;
        }

        Func<bool> step = stepOverCalls
            ? () => SpectrumSourceStepper.StepOverInstruction(
                ReadDebuggerPc,
                ReadDebuggerStackPointer,
                MeasureDebuggerInstruction,
                StepDebuggerOneInstruction)
            : StepDebuggerOneInstruction;

        SourceStepOutcome outcome = SpectrumSourceStepper.StepLine(
            window.Symbols,
            ReadDebuggerPc,
            step);

        window.RefreshAll(followPc: true);
        string what = stepOverCalls ? "Source step over" : "Source step";
        _statusText.Text = outcome.ReachedNewLine
            ? $"Stepped to {outcome.Summary}"
            : $"{what}: {outcome.Summary}";
    }

    private ushort ReadDebuggerStackPointer() => _debugger.Cpu?.SP ?? 0;

    /// <summary>
    /// How wide the instruction at an address is, and whether it calls somewhere.
    /// </summary>
    /// <remarks>
    /// A Next has to be measured with the extended decoder, because Z80N instructions are
    /// longer than the plain table reports and a step over one would otherwise resume in the
    /// middle of it.
    /// </remarks>
    private SpectrumInstructionShape MeasureDebuggerInstruction(ushort address)
    {
        IZ80DebuggerMemory? memory = _debugger.Memory;
        if (memory == null)
        {
            return new SpectrumInstructionShape(1, IsCallLike: false);
        }

        Z80Disassembler disassembler = _zxNextMachine != null ? _z80nDisassembler : _debuggerDisassembler;
        Z80DisassembledInstruction instruction = disassembler.Disassemble(memory, address);
        return new SpectrumInstructionShape(instruction.Length, instruction.IsCallLike);
    }

    private ushort ReadDebuggerPc() => _debugger.Cpu?.PC ?? 0;

    /// <summary>One instruction, without redrawing anything.</summary>
    /// <remarks>
    /// The same sequence the instruction step performs, minus the refresh, so a loop over it
    /// behaves identically to pressing step repeatedly.
    /// </remarks>
    private bool StepDebuggerOneInstruction()
    {
        if (_zxNextMachine != null)
        {
            _zxNextMachine.SetPaused(false);
            _zxNextMachine.StepInstruction();
            _zxNextMachine.SetPaused(true);
            return true;
        }

        if (_zx8xMachine != null)
        {
            _zx8xMachine.SetPaused(false);
            _zx8xMachine.StepInstruction();
            _zx8xMachine.SetPaused(true);
            return true;
        }

        if (_machine == null)
        {
            return false;
        }

        _machine.Emulator.SetPaused(false);
        _machine.Emulator.StepInstruction();
        _machine.Emulator.SetPaused(true);
        return true;
    }

    private void StepDebuggerInto()
    {
        SpectrumMachine? machine = _machine;
        Zx8x.Core.Zx8xMachine? zx8x = _zx8xMachine;
        ZxNextMachine? next = _zxNextMachine;
        if (next != null)
        {
            PauseForDebugger("Step", notifyController: false);
            _debugger.PrepareStepInto();
            UpdateDebuggerHooks();
            next.SetPaused(false);
            next.StepInstruction();
            next.SetPaused(true);
            UpdateDebuggerHooks();
            _debuggerWindow?.RefreshAll(followPc: true);
            return;
        }

        if (machine == null && zx8x == null)
        {
            return;
        }

        PauseForDebugger("Step", notifyController: false);
        _debugger.PrepareStepInto();
        UpdateDebuggerHooks();
        if (machine != null)
        {
            machine.Emulator.StepInstruction();
            machine.Emulator.SetPaused(true);
        }
        else
        {
            zx8x!.SetPaused(false);
            zx8x.StepInstruction();
            zx8x.SetPaused(true);
        }
        UpdateDebuggerHooks();
        _debuggerWindow?.RefreshAll(followPc: true);
    }

    private void StepDebuggerOver()
    {
        SpectrumMachine? machine = _machine;
        Zx8x.Core.Zx8xMachine? zx8x = _zx8xMachine;
        ZxNextMachine? next = _zxNextMachine;
        if (next != null)
        {
            PauseForDebugger("Step over", notifyController: false);
            // Z80N instructions are longer than the plain table would report, so a step
            // over one has to be measured with the extended decoder.
            _debugger.PrepareStepOver(_z80nDisassembler);
            UpdateDebuggerHooks();
            if (_debugger.Mode == DebuggerRunMode.StepInto)
            {
                next.SetPaused(false);
                next.StepInstruction();
                next.SetPaused(true);
                UpdateDebuggerHooks();
                _debuggerWindow?.RefreshAll(followPc: true);
                return;
            }

            ResumeFromDebugger();
            return;
        }

        if (machine == null && zx8x == null)
        {
            return;
        }

        PauseForDebugger("Step over", notifyController: false);
        _debugger.PrepareStepOver(_zxNextMachine != null ? _z80nDisassembler : _debuggerDisassembler);
        UpdateDebuggerHooks();
        if (_debugger.Mode == DebuggerRunMode.StepInto)
        {
            if (machine != null)
            {
                machine.Emulator.StepInstruction();
                machine.Emulator.SetPaused(true);
            }
            else
            {
                zx8x!.SetPaused(false);
                zx8x.StepInstruction();
                zx8x.SetPaused(true);
            }
            UpdateDebuggerHooks();
            _debuggerWindow?.RefreshAll(followPc: true);
            return;
        }

        ResumeFromDebugger();
    }

    private void RunDebuggerToAddress(ushort address)
    {
        _debugger.PrepareRunTo(address);
        ResumeFromDebugger();
    }

    private ToolRunState SuspendMachineForTool()
    {
        SpectrumMachine machine = _machine
            ?? throw new InvalidOperationException("No machine is available.");
        var state = new ToolRunState(
            machine.Emulator.IsPaused,
            HasExecutionOwner());
        StopExecutionOwner();
        machine.Emulator.SetPaused(true);
        UpdatePauseCommandState(paused: true);
        return state;
    }

    private void RestoreMachineAfterTool(ToolRunState state)
    {
        SpectrumMachine? machine = _machine;
        if (machine == null || _closing || _replacingMachine)
        {
            return;
        }

        machine.Emulator.SetPaused(state.WasPaused);
        if (state.HadExecutionOwner && !HasExecutionOwner())
        {
            _ = StartSelectedExecution(machine);
        }

        UpdatePauseCommandState(state.WasPaused);
        _statusText.Text = state.WasPaused
            ? $"{FormatModel(machine.Model)} — paused"
            : $"{FormatModel(machine.Model)} — {GetExecutionModeText()} — keyboard active";
    }

    private readonly record struct ToolRunState(bool WasPaused, bool HadExecutionOwner);
}
