using Avalonia.Interactivity;
using Avalonia.Threading;
using ZedExEss.Hosting;
using ZedExEss.Spectrum.Basic;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.Debugging;
using ZedExEss.Spectrum.Memory;

namespace ZedExEss.AvaloniaHost;

public sealed partial class MainWindow
{
    private readonly SpectrumDebuggerController _debugger = new();
    private readonly Z80Disassembler _debuggerDisassembler = new();
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
        _oscilloscopeWindow?.AttachAudioRenderer(null);
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
        window.AttachAudioRenderer(_machine?.Audio);
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

    private void OnDebuggerClicked(object? sender, RoutedEventArgs e)
    {
        if (_machine == null && _zx8xMachine == null)
        {
            return;
        }

        if (_debuggerWindow != null)
        {
            _debuggerWindow.Activate();
            _debuggerWindow.RefreshAll(followPc: true);
            return;
        }

        var view = new SpectrumDebuggerViewService(_debugger, _debuggerDisassembler, _debuggerAssembler);
        var window = new DebuggerWindow(view, _fileDialogs);
        window.RunRequested += ResumeFromDebugger;
        window.PauseRequested += () => PauseForDebugger("Paused", notifyController: true);
        window.StepIntoRequested += StepDebuggerInto;
        window.StepOverRequested += StepDebuggerOver;
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

        SpectrumMachine? machine = _machine;
        if (machine == null || _closing)
        {
            return;
        }

        bool before = _debugger.Enabled
            || _flashLoadEnabled
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

    private void StepDebuggerInto()
    {
        SpectrumMachine? machine = _machine;
        Zx8x.Core.Zx8xMachine? zx8x = _zx8xMachine;
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
        if (machine == null && zx8x == null)
        {
            return;
        }

        PauseForDebugger("Step over", notifyController: false);
        _debugger.PrepareStepOver(_debuggerDisassembler);
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
