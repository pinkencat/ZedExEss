using ZedExEss.Zx8x.Core;
using ZedExEss.Zx8x.Input;

namespace ZedExEss.Zx8x.Video;

/// <summary>Fixed horizontal and nominal PAL/NTSC vertical timing of the ZX8x family.</summary>
public readonly record struct Zx8xVideoTiming(
    int TstatesPerLine,
    int DisplayTstates,
    int HorizontalBlankTstates,
    int HorizontalSyncStart,
    int HorizontalSyncTstates,
    int UpperBorderLines,
    int DisplayLines,
    int LowerBorderLines,
    int VerticalSyncLines)
{
    public const int PixelClocksPerTstate = 2;

    public int NominalLinesPerFrame => UpperBorderLines + DisplayLines + LowerBorderLines + VerticalSyncLines;
    public int NominalTstatesPerFrame => NominalLinesPerFrame * TstatesPerLine;

    public static Zx8xVideoTiming ForRegion(bool is50Hz)
    {
        return is50Hz
            ? new Zx8xVideoTiming(207, 128, 64, 192, 15, 56, 192, 56, 6)
            : new Zx8xVideoTiming(207, 128, 64, 192, 15, 32, 192, 32, 6);
    }
}

/// <summary>One display fetch positioned on the software-generated raster.</summary>
public readonly record struct Zx8xRasterFetch(
    Zx8xDisplayFetch Fetch,
    int RasterLine,
    int LineTstate,
    byte CharacterLine);

public interface IZx8xRasterSink
{
    void BeginFrame(long frameNumber);
    void OnRasterFetch(in Zx8xRasterFetch fetch);
}

/// <summary>
/// Tracks the 207-T-state horizontal counter, vertical-retrace resets and ZX81
/// SLOW-mode NMI pulses. The controller is advanced after each complete Z80
/// instruction, while I/O observers retain their exact intra-instruction timestamps.
/// </summary>
public sealed class Zx8xVideoTimingController(
    Zx8xModel model,
    Zx8xIoDevice io,
    Zx8xVideoTiming timing) : IZx8xIoCycleObserver, IZx8xDisplayFetchSink
{
    private readonly Zx8xModel _model = model;
    private readonly Zx8xIoDevice _io = io ?? throw new ArgumentNullException(nameof(io));
    private IZx8xRasterSink? _rasterSink;
    private bool _counterRunning = true;
    private ulong _lineOrigin;
    private ulong _nextLineBoundary = (ulong)timing.TstatesPerLine;
    private ulong _nextNmiPulse = (ulong)timing.HorizontalSyncStart;
    private ulong _verticalRetraceStartedAt;
    private int _rasterLine;
    private byte _characterLine;
    private bool _verticalRetraceActive;
    private long _frameNumber;

    public Zx8xVideoTiming Timing { get; } = timing;
    public bool CounterRunning => _counterRunning;
    public int RasterLine => _rasterLine;
    public byte CharacterLine => _characterLine;
    public long FrameNumber => _frameNumber;
    public long NmiPulseCount { get; private set; }
    public long IoReadCount { get; private set; }
    public long IoWriteCount { get; private set; }
    public ushort LastReadPort { get; private set; }
    public ushort LastWritePort { get; private set; }

    public void ConfigureRasterSink(IZx8xRasterSink? sink)
    {
        _rasterSink = sink;
    }

    public void Reset()
    {
        // The ULA horizontal divider free-runs from power-on. No picture is
        // published until software supplies a valid vertical sync, but FE/FD can
        // enable or disable NMIs against this existing phase at any time.
        _counterRunning = true;
        _lineOrigin = 0;
        _nextLineBoundary = (ulong)Timing.TstatesPerLine;
        _nextNmiPulse = (ulong)Timing.HorizontalSyncStart;
        _verticalRetraceStartedAt = 0;
        _rasterLine = 0;
        _characterLine = 0;
        _verticalRetraceActive = false;
        _frameNumber = 0;
        NmiPulseCount = 0;
        IoReadCount = 0;
        IoWriteCount = 0;
        LastReadPort = 0;
        LastWritePort = 0;
    }

    public void OnIoRead(ulong tstate, ushort port, byte value)
    {
        IoReadCount++;
        LastReadPort = port;
        if ((port & 1) != 0)
        {
            return;
        }

        // A keyboard-port read asserts the software-controlled sync gate on a
        // ZX80, and on a ZX81 only while the horizontal NMI generator is off.
        // Stop exposing the old horizontal phase while sync is asserted. The
        // counter is re-phased when the matching OUT ends the sync pulse.
        if (_model == Zx8xModel.Zx80 || !_io.NmiGeneratorEnabled)
        {
            if (!_verticalRetraceActive)
            {
                _verticalRetraceStartedAt = tstate;
                _verticalRetraceActive = true;
            }

            _counterRunning = false;
            _nextLineBoundary = ulong.MaxValue;
            _nextNmiPulse = ulong.MaxValue;
        }
    }

    public void OnIoWrite(ulong tstate, ushort port, byte value)
    {
        IoWriteCount++;
        LastWritePort = port;
        // An OUT ends a software-generated sync pulse. Crucially, an ordinary
        // OUT while sync is already inactive does *not* restart the horizontal
        // counter. The ZX81 ROM executes one such OUT in every NMI handler; using
        // it as a new line origin lengthens and progressively displaces the raster.
        if (!_verticalRetraceActive)
        {
            return;
        }

        ulong retraceLength = tstate - _verticalRetraceStartedAt;
        bool verticalSync = retraceLength >= (ulong)(Timing.TstatesPerLine * 2);
        if (verticalSync)
        {
            _rasterLine = 0;
            _frameNumber++;
            _rasterSink?.BeginFrame(_frameNumber);
        }
        // Both a vertical pulse and the short IN/OUT pairs used by pseudo-hires
        // software reset the character row counter.
        _characterLine = 0;
        _verticalRetraceActive = false;
        _counterRunning = true;
        _lineOrigin = tstate;
        _nextLineBoundary = tstate + (ulong)Timing.TstatesPerLine;
        _nextNmiPulse = tstate + (ulong)Timing.HorizontalSyncStart;
    }

    public void OnDisplayFetch(in Zx8xDisplayFetch fetch)
    {
        if (!_counterRunning || fetch.TState < _lineOrigin)
        {
            return;
        }

        ulong relative = fetch.TState - _lineOrigin;
        int line = (int)(relative / (ulong)Timing.TstatesPerLine);
        int phase = (int)(relative % (ulong)Timing.TstatesPerLine);
        byte characterLine = (byte)((_characterLine + line) & 0x07);
        var rasterFetch = new Zx8xRasterFetch(fetch, _rasterLine + line, phase, characterLine);
        _rasterSink?.OnRasterFetch(in rasterFetch);
    }

    /// <summary>Advances line/NMI state to the CPU's completed instruction boundary.</summary>
    public void AdvanceAfterInstruction(Zx8xCpu cpu)
    {
        ArgumentNullException.ThrowIfNull(cpu);
        if (!_counterRunning)
        {
            return;
        }

        bool advanced;
        do
        {
            advanced = false;

            while (_nextLineBoundary <= cpu.Cyc)
            {
                _rasterLine++;
                _characterLine = (byte)((_characterLine + 1) & 0x07);
                _lineOrigin = _nextLineBoundary;
                _nextLineBoundary += (ulong)Timing.TstatesPerLine;
                advanced = true;
            }

            if (_nextNmiPulse <= cpu.Cyc)
            {
                ulong pulseStart = _nextNmiPulse;
                _nextNmiPulse += (ulong)Timing.TstatesPerLine;
                advanced = true;

                if (_model == Zx8xModel.Zx81 && _io.NmiGeneratorEnabled)
                {
                    // WAIT holds the CPU until the end of the 15-T-state HSync/NMI
                    // pulse. HALT is released directly and does not receive WAIT.
                    ulong waitEnd = pulseStart + (ulong)(Timing.HorizontalSyncTstates - 1);
                    if (!cpu.IsHalted && cpu.Cyc < waitEnd)
                    {
                        cpu.AddWaitStates((int)(waitEnd - cpu.Cyc));
                    }

                    NmiPulseCount++;
                    cpu.Z80GenNMI();
                    cpu.ServicePendingInterruptsAtBoundary();
                }
            }
        }
        while (advanced && (_nextLineBoundary <= cpu.Cyc || _nextNmiPulse <= cpu.Cyc));
    }
}
