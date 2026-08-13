using ZedExEss.Zx8x.Core;
using ZedExEss.Zx8x.Input;

namespace ZedExEss.Zx8x.Tape;

/// <summary>
/// Models the ZX80/ZX81 EAR input and the MIC output derived from the video-sync
/// latch. It deliberately does not reuse the Spectrum tape port: the ZX8x ROM
/// creates tape pulses by starting retrace with an even-port read and ending it
/// with an I/O write.
/// </summary>
public sealed class Zx8xCassetteDevice : IZx8xIoCycleObserver
{
    // An ordinary display frame contributes one isolated software-sync pulse.
    // Tape routines instead generate a dense train of edges.  Keeping the
    // detector here lets audio and video monitor that real output without ROM
    // address traps or model-specific SAVE entry points.
    private const ulong MaximumRapidEdgeGapTstates = 8_192;
    private const ulong ActivityHoldTstates = 32_768;
    private const int EdgesRequiredForActivity = 4;

    private readonly Zx8xIoDevice _io;
    private IZx8xCassetteOutputSink? _outputSink;
    private ulong _lastOutputEdgeTstate;
    private int _rapidEdgeCount;

    public Zx8xCassetteDevice(Zx8xIoDevice io)
    {
        _io = io ?? throw new ArgumentNullException(nameof(io));
    }

    /// <summary>Current logic level presented to the ULA cassette input.</summary>
    public bool InputHigh
    {
        get => _io.CassetteInputHigh;
        set => _io.CassetteInputHigh = value;
    }

    /// <summary>
    /// Current MIC level. Idle/non-retrace is high; active vertical retrace is low.
    /// </summary>
    public bool OutputHigh { get; private set; } = true;

    /// <summary>
    /// True while the software sync output contains the dense edge train used
    /// by the ROM LOAD/SAVE routines.  A lone vertical-sync pulse is excluded.
    /// </summary>
    public bool OutputActivityActive { get; private set; }

    /// <summary>Raised only when the logical MIC output changes state.</summary>
    public event Action<ulong, bool>? OutputLevelChanged;

    /// <summary>Raised when dense cassette I/O starts or expires.</summary>
    public event Action<ulong, bool>? OutputActivityChanged;

    public void ConfigureOutputSink(IZx8xCassetteOutputSink? sink, ulong tstate = 0)
    {
        _outputSink = sink;
        sink?.SetMicLevel(tstate, OutputHigh);
    }

    public void OnIoRead(ulong tstate, ushort port, byte value)
    {
        // Odd ports neither select the cassette/keyboard input nor alter retrace.
        if ((port & 0x0001) == 0)
        {
            SetOutputLevel(tstate, !_io.VerticalRetraceActive);
        }
    }

    public void OnIoWrite(ulong tstate, ushort port, byte value)
    {
        // Every I/O write terminates retrace, irrespective of its nominal port.
        SetOutputLevel(tstate, true);
    }

    public void Reset(ulong tstate = 0)
    {
        InputHigh = false;
        _lastOutputEdgeTstate = tstate;
        _rapidEdgeCount = 0;
        SetOutputActivity(tstate, false);
        SetOutputLevel(tstate, true, forceSinkUpdate: true);
    }

    /// <summary>
    /// Expires cassette activity at the exact end of its hold interval.  This is
    /// called from the machine clock even when no further output edge arrives.
    /// </summary>
    public void AdvanceTo(ulong tstate)
    {
        if (OutputActivityActive
            && tstate > _lastOutputEdgeTstate
            && tstate - _lastOutputEdgeTstate > ActivityHoldTstates)
        {
            SetOutputActivity(_lastOutputEdgeTstate + ActivityHoldTstates, false);
            _rapidEdgeCount = 0;
        }
    }

    private void SetOutputLevel(ulong tstate, bool high, bool forceSinkUpdate = false)
    {
        if (!forceSinkUpdate && OutputHigh == high)
        {
            return;
        }

        bool changed = OutputHigh != high;
        OutputHigh = high;
        _outputSink?.SetMicLevel(tstate, high);
        if (changed)
        {
            OutputLevelChanged?.Invoke(tstate, high);
            ObserveOutputEdge(tstate);
        }
    }

    private void ObserveOutputEdge(ulong tstate)
    {
        if (_rapidEdgeCount != 0
            && tstate >= _lastOutputEdgeTstate
            && tstate - _lastOutputEdgeTstate <= MaximumRapidEdgeGapTstates)
        {
            _rapidEdgeCount++;
        }
        else
        {
            _rapidEdgeCount = 1;
        }

        _lastOutputEdgeTstate = tstate;
        if (_rapidEdgeCount >= EdgesRequiredForActivity)
        {
            SetOutputActivity(tstate, true);
        }
    }

    private void SetOutputActivity(ulong tstate, bool active)
    {
        if (OutputActivityActive == active)
        {
            return;
        }

        OutputActivityActive = active;
        OutputActivityChanged?.Invoke(tstate, active);
    }
}
