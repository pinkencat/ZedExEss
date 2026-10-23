using ZedExEss.Machines;
using ZedExEss.Spectrum.Abstractions;
using ZedExEss.Zx8x.Audio;
using ZedExEss.Zx8x.Input;
using ZedExEss.Zx8x.Memory;
using ZedExEss.Zx8x.Tape;
using ZedExEss.Zx8x.Video;

namespace ZedExEss.Zx8x.Core;

/// <summary>Portable object graph for one running ZX80 or ZX81.</summary>
public sealed class Zx8xMachine : IEmulatedMachine, IAudioSource
{
    private long _observedCompletedFrame;
    private Func<bool>? _beforeCpuStep;
    private Action? _afterCpuStep;

    internal Zx8xMachine(
        Zx8xModel model,
        Zx81RomRevision romRevision,
        int sampleRate,
        Zx8xMemory memory,
        Zx8xKeyboard keyboard,
        Zx8xIoDevice io,
        Zx8xCassetteDevice cassette,
        Zx8xTapeSession tape,
        Zx8xAudioRenderer audio,
        Zx8xCpuMemoryBus memoryBus,
        Zx8xCpuPortBus portBus,
        Zx8xCpu cpu,
        Zx8xVideoTimingController videoTiming,
        Zx8xMonochromeRenderer renderer)
    {
        Model = model;
        RomRevision = romRevision;
        SampleRate = sampleRate;
        Memory = memory;
        Keyboard = keyboard;
        Io = io;
        Cassette = cassette;
        Tape = tape;
        Audio = audio;
        MemoryBus = memoryBus;
        PortBus = portBus;
        Cpu = cpu;
        VideoTiming = videoTiming;
        Renderer = renderer;
    }

    public Zx8xModel Model { get; }
    public Zx81RomRevision RomRevision { get; }
    public MachineDescriptor Descriptor => Zx8xModelDescriptors.ForModel(Model);
    public int SampleRate { get; }
    public int CpuClockHz => Zx8xModelDescriptors.CpuClockHz;
    public Zx8xMemory Memory { get; }
    public Zx8xKeyboard Keyboard { get; }
    public Zx8xIoDevice Io { get; }
    public Zx8xCassetteDevice Cassette { get; }
    public Zx8xTapeSession Tape { get; }
    public Zx8xAudioRenderer Audio { get; }
    public Zx8xCpuMemoryBus MemoryBus { get; }
    public Zx8xCpuPortBus PortBus { get; }
    public Zx8xCpu Cpu { get; }
    public Zx8xVideoTimingController VideoTiming { get; }
    public Zx8xMonochromeRenderer Renderer { get; }
    public int FrameWidth => Zx8xMonochromeRenderer.Width;
    public int FrameHeight => Zx8xMonochromeRenderer.Height;
    public int TstatesPerFrame => VideoTiming.Timing.NominalTstatesPerFrame;
    public bool IsPaused { get; private set; }

    public event Action? FrameCompleted;

    /// <summary>
    /// Enables audible monitoring of the cassette MIC output. Dense ROM tape
    /// activity controls this automatically; the property remains available for
    /// diagnostics and explicit host monitoring.
    /// </summary>
    public bool CassetteMonitorEnabled
    {
        get => Audio.MonitorEnabled;
        set => Audio.SetMonitorEnabled(value, Cpu.Cyc);
    }

    /// <summary>Executes one instruction and applies any line-boundary NMI it crossed.</summary>
    public void StepInstruction()
    {
        _ = StepInstruction(publishFrame: true);
    }

    private bool StepInstruction(bool publishFrame)
    {
        if (_beforeCpuStep?.Invoke() == true)
        {
            IsPaused = true;
            return false;
        }

        Cpu.Z80Step();
        Tape.AdvanceTo(Cpu.Cyc);
        VideoTiming.AdvanceAfterInstruction(Cpu);
        MemoryBus.CommitRefreshInterruptAssertion();
        Cassette.AdvanceTo(Cpu.Cyc);
        Renderer.AdvanceCassetteRasterTo(Cpu.Cyc);
        Audio.AdvanceTo(Cpu.Cyc);

        long completedFrame = Renderer.CompletedFrameNumber;
        if (completedFrame != _observedCompletedFrame)
        {
            _observedCompletedFrame = completedFrame;
            if (publishFrame)
            {
                FrameCompleted?.Invoke();
            }
        }

        _afterCpuStep?.Invoke();
        return true;
    }

    /// <summary>
    /// Installs instruction-boundary hooks for host tooling. Null delegates remove
    /// the calls entirely from normal execution except for the predictable null check.
    /// </summary>
    public void ConfigureCpuStepHooks(Func<bool>? beforeCpuStep, Action? afterCpuStep)
    {
        _beforeCpuStep = beforeCpuStep;
        _afterCpuStep = afterCpuStep;
    }

    /// <summary>
    /// Audio-driven execution entry point used by portable desktop audio hosts.
    /// CPU, display, cassette and audio all advance through the normal machine path.
    /// </summary>
    public int ReadSamples(short[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (IsPaused)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        Audio.BeginCapture(Cpu.Cyc);
        int total = Audio.DrainSamples(buffer, offset, count);
        while (total < count && !IsPaused)
        {
            if (!StepInstruction(publishFrame: true))
            {
                break;
            }

            total += Audio.DrainSamples(buffer, offset + total, count - total);
        }

        if (total < count)
        {
            Array.Clear(buffer, offset + total, count - total);
        }

        return count;
    }

    public void RunForTstates(int minimumTstates)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumTstates);
        ulong target = Cpu.Cyc + (ulong)minimumTstates;
        while (!IsPaused && Cpu.Cyc < target)
        {
            if (!StepInstruction(publishFrame: true))
            {
                break;
            }
        }
    }

    public void RunFrame(bool presentFrame = true)
    {
        if (IsPaused)
        {
            return;
        }

        long initialFrame = Renderer.CompletedFrameNumber;
        // ZX8x frames are software generated. FAST mode and early ROM startup can
        // intentionally produce no vertical sync, so a host clock must never wait
        // indefinitely for a picture boundary that the guest has chosen not to emit.
        ulong cycleLimit = Cpu.Cyc + (ulong)TstatesPerFrame;
        while (!IsPaused && Renderer.CompletedFrameNumber == initialFrame && Cpu.Cyc < cycleLimit)
        {
            if (!StepInstruction(presentFrame))
            {
                break;
            }
        }
    }

    public void SetPaused(bool paused)
    {
        IsPaused = paused;
    }

    public bool TryCopyFrame(int[] destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Length != FrameWidth * FrameHeight)
        {
            throw new ArgumentException("Destination buffer size mismatch.", nameof(destination));
        }

        Renderer.CopyBgraFrame(destination);
        return Renderer.CompletedFrameNumber > 0;
    }

    public void Reset()
    {
        Cpu.Z80Init();
        MemoryBus.ResetRefreshInterruptLine();
        Io.Reset();
        Cassette.Reset();
        Tape.Rewind(Cpu.Cyc);
        Audio.Reset();
        VideoTiming.Reset();
        Renderer.Reset(Cpu.Cyc);
        _observedCompletedFrame = 0;
        IsPaused = false;
    }
}
