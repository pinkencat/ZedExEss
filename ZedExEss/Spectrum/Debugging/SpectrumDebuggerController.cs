using System;
using System.Collections.ObjectModel;
using System.Linq;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.Memory;
using ZedExEss.Spectrum.Ports;
using ZedExEss.Z80CPU;

namespace ZedExEss.Spectrum.Debugging
{
    /// <summary>
    /// Lightweight debugger state machine used by the emulator hot path and the WPF debugger window.
    /// </summary>
    /// <remarks>
    /// Execute breakpoints are evaluated before an opcode fetch. Bus watchpoints only record a
    /// pending hit during the access and stop after the instruction finishes, preserving atomic
    /// CPU timing. <see cref="Enabled"/> is cached so a closed/inactive debugger adds only one
    /// predictable branch to the processor's hot path.
    /// </remarks>
    public sealed class SpectrumDebuggerController : IZ80DebugHook
    {
        private readonly object _sync = new();
        private Z80? _cpu;
        private SpectrumMemory? _memory;
        private SpectrumPortBus? _ports;
        private SpectrumTimingModel _timing;
        private SpectrumModel _model;
        private int _nextBreakpointId = 1;
        private DebuggerBreakHit? _pendingWatchHit;
        private ushort? _temporaryExecuteAddress;
        private volatile bool _hasActiveHooks;
        private volatile DebuggerRunMode _mode = DebuggerRunMode.Running;

        public event Action<DebuggerBreakHit>? BreakHit;
        public event Action? HooksChanged;

        public ObservableCollection<DebuggerBreakpoint> Breakpoints { get; } = [];

        public DebuggerBreakHit? LastHit { get; private set; }

        public DebuggerRunMode Mode => _mode;

        public bool IsPaused => _mode == DebuggerRunMode.Paused;

        public bool Enabled => _hasActiveHooks;

        public bool AccessWatchpointsEnabled { get; private set; }

        public Z80? Cpu => _cpu;

        public SpectrumMemory? Memory => _memory;

        public SpectrumModel Model => _model;

        public SpectrumTimingModel Timing => _timing;
        public void Attach(Z80 cpu, SpectrumMemory memory, SpectrumPortBus ports, SpectrumModel model)
        {
            ArgumentNullException.ThrowIfNull(cpu);
            ArgumentNullException.ThrowIfNull(memory);
            ArgumentNullException.ThrowIfNull(ports);

            _cpu = cpu;
            _memory = memory;
            _ports = ports;
            _model = model;
            _timing = SpectrumTimingModel.ForModel(model);
            cpu.ConfigureDebugHook(this);
            RecomputeActiveHooks();
        }
        public DebuggerBreakpoint AddExecuteBreakpoint(ushort address, bool oneShot = false)
        {
            return AddBreakpoint(new DebuggerBreakpoint
            {
                Type = DebuggerBreakType.Execute,
                Address = address,
                EndAddress = address,
                OneShot = oneShot
            });
        }
        public DebuggerBreakpoint AddMemoryBreakpoint(DebuggerBreakType type, ushort start, ushort end)
        {
            if (type is not (DebuggerBreakType.MemoryRead or DebuggerBreakType.MemoryWrite))
            {
                throw new ArgumentOutOfRangeException(nameof(type));
            }

            return AddBreakpoint(new DebuggerBreakpoint
            {
                Type = type,
                Address = start,
                EndAddress = end
            });
        }
        public DebuggerBreakpoint AddPortBreakpoint(DebuggerBreakType type, ushort port, ushort mask)
        {
            if (type is not (DebuggerBreakType.PortRead or DebuggerBreakType.PortWrite))
            {
                throw new ArgumentOutOfRangeException(nameof(type));
            }

            return AddBreakpoint(new DebuggerBreakpoint
            {
                Type = type,
                Port = port,
                PortMask = mask
            });
        }
        public void RemoveBreakpoint(DebuggerBreakpoint breakpoint)
        {
            if (breakpoint == null)
            {
                return;
            }

            Breakpoints.Remove(breakpoint);
            RecomputeActiveHooks();
        }
        public void ClearTemporaryBreakpoints()
        {
            _temporaryExecuteAddress = null;
        }
        public void RebuildHookState()
        {
            RecomputeActiveHooks();
        }
        public void Run()
        {
            LastHit = null;
            _pendingWatchHit = null;
            _mode = DebuggerRunMode.Running;
            RecomputeActiveHooks();
        }
        public void Pause(string reason = "Paused")
        {
            Hit(new DebuggerBreakHit(DebuggerBreakType.Execute, _cpu?.PC ?? 0, 0, null, _cpu?.Cyc ?? 0, reason, null));
        }
        public void PrepareStepInto()
        {
            LastHit = null;
            _pendingWatchHit = null;
            _mode = DebuggerRunMode.StepInto;
            RecomputeActiveHooks();
        }
        public void PrepareRunTo(ushort address)
        {
            _temporaryExecuteAddress = address;
            Run();
        }
        public void PrepareStepOver(Z80Disassembler disassembler)
        {
            ArgumentNullException.ThrowIfNull(disassembler);
            Z80? cpu = _cpu;
            SpectrumMemory? memory = _memory;
            if (cpu == null || memory == null)
            {
                PrepareStepInto();
                return;
            }

            Z80DisassembledInstruction instruction = disassembler.Disassemble(memory, cpu.PC);
            if (instruction.IsCallLike)
            {
                PrepareRunTo(unchecked((ushort)(cpu.PC + instruction.Length)));
                return;
            }

            PrepareStepInto();
        }
        public bool BeforeCpuStep()
        {
            Z80? cpu = _cpu;
            SpectrumMemory? memory = _memory;
            if (cpu == null || memory == null)
            {
                return false;
            }

            if (_mode == DebuggerRunMode.Paused)
            {
                return true;
            }

            if (_mode == DebuggerRunMode.StepInto)
            {
                return false;
            }

            if (_temporaryExecuteAddress.HasValue && cpu.PC == _temporaryExecuteAddress.Value)
            {
                _temporaryExecuteAddress = null;
                Hit(new DebuggerBreakHit(DebuggerBreakType.Execute, cpu.PC, 0, null, cpu.Cyc, $"Run target {cpu.PC:X4}", null));
                return true;
            }

            SpectrumMemoryMapping mapping = memory.GetMapping(cpu.PC);
            DebuggerBreakpoint? breakpoint = FindAddressBreakpoint(DebuggerBreakType.Execute, cpu.PC, mapping);
            if (breakpoint != null)
            {
                Hit(new DebuggerBreakHit(DebuggerBreakType.Execute, cpu.PC, 0, null, cpu.Cyc, $"Execute {cpu.PC:X4}", breakpoint));
                return true;
            }

            return false;
        }
        public void AfterCpuStep()
        {
            if (_mode == DebuggerRunMode.StepInto)
            {
                Z80? cpu = _cpu;
                Hit(new DebuggerBreakHit(DebuggerBreakType.Execute, cpu?.PC ?? 0, 0, null, cpu?.Cyc ?? 0, "Step complete", null));
                return;
            }

            DebuggerBreakHit? pending = _pendingWatchHit;
            if (pending != null)
            {
                _pendingWatchHit = null;
                Hit(pending);
            }
        }
        public void OnMemoryRead(ushort address, byte value)
        {
            CheckMemoryWatchpoint(DebuggerBreakType.MemoryRead, address, value);
        }
        public void OnMemoryWrite(ushort address, byte value)
        {
            CheckMemoryWatchpoint(DebuggerBreakType.MemoryWrite, address, value);
        }
        public void OnPortRead(ushort port, byte value)
        {
            CheckPortWatchpoint(DebuggerBreakType.PortRead, port, value);
        }
        public void OnPortWrite(ushort port, byte value)
        {
            CheckPortWatchpoint(DebuggerBreakType.PortWrite, port, value);
        }

        public int CurrentFrameTstate
        {
            get
            {
                if (_cpu == null || _timing.TstatesPerFrame <= 0)
                {
                    return 0;
                }

                return (int)(_cpu.Cyc % (ulong)_timing.TstatesPerFrame);
            }
        }

        public int CurrentLine => _timing.TstatesPerLine <= 0 ? 0 : CurrentFrameTstate / _timing.TstatesPerLine;

        public int CurrentLineTstate => _timing.TstatesPerLine <= 0 ? 0 : CurrentFrameTstate % _timing.TstatesPerLine;
        private DebuggerBreakpoint AddBreakpoint(DebuggerBreakpoint breakpoint)
        {
            breakpoint.Id = _nextBreakpointId++;
            Breakpoints.Add(breakpoint);
            RecomputeActiveHooks();
            return breakpoint;
        }
        private void CheckMemoryWatchpoint(DebuggerBreakType type, ushort address, byte value)
        {
            if (_pendingWatchHit != null || _mode != DebuggerRunMode.Running)
            {
                return;
            }

            SpectrumMemory? memory = _memory;
            Z80? cpu = _cpu;
            if (memory == null || cpu == null)
            {
                return;
            }

            DebuggerBreakpoint? breakpoint = FindAddressBreakpoint(type, address, memory.GetMapping(address));
            if (breakpoint == null)
            {
                return;
            }

            _pendingWatchHit = new DebuggerBreakHit(type, address, 0, value, cpu.Cyc, $"{type} {address:X4} = {value:X2}", breakpoint);
        }
        private void CheckPortWatchpoint(DebuggerBreakType type, ushort port, byte value)
        {
            if (_pendingWatchHit != null || _mode != DebuggerRunMode.Running)
            {
                return;
            }

            Z80? cpu = _cpu;
            DebuggerBreakpoint? breakpoint = Breakpoints.FirstOrDefault(bp => bp.Type == type && bp.MatchesPort(port));
            if (breakpoint == null)
            {
                return;
            }

            _pendingWatchHit = new DebuggerBreakHit(type, 0, port, value, cpu?.Cyc ?? 0, $"{type} {port:X4} = {value:X2}", breakpoint);
        }
        private DebuggerBreakpoint? FindAddressBreakpoint(DebuggerBreakType type, ushort address, SpectrumMemoryMapping mapping)
        {
            return Breakpoints.FirstOrDefault(bp => bp.Type == type && bp.MatchesAddress(address, mapping));
        }
        private void Hit(DebuggerBreakHit hit)
        {
            LastHit = hit;
            _mode = DebuggerRunMode.Paused;
            if (hit.Breakpoint?.OneShot == true)
            {
                Breakpoints.Remove(hit.Breakpoint);
            }

            RecomputeActiveHooks();
            BreakHit?.Invoke(hit);
        }
        private void RecomputeActiveHooks()
        {
            AccessWatchpointsEnabled = Breakpoints.Any(bp => bp.Enabled && bp.Type is DebuggerBreakType.MemoryRead or DebuggerBreakType.MemoryWrite or DebuggerBreakType.PortRead or DebuggerBreakType.PortWrite);
            _hasActiveHooks = _mode != DebuggerRunMode.Running
                || _temporaryExecuteAddress.HasValue
                || Breakpoints.Any(bp => bp.Enabled);
            HooksChanged?.Invoke();
        }
    }
}
