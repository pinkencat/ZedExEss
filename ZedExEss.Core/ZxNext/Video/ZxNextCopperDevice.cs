using ZedExEss.ZxNext.Core;

namespace ZedExEss.ZxNext.Video;

/// <summary>
/// The Copper: a 1K x 16 instruction list run on the 28 MHz clock against the beam, writing
/// NextRegs $01-$7F. A transcription of the pinned core's <c>device/copper.vhd</c> and its
/// wiring in <c>zxnext.vhd</c>.
/// </summary>
/// <remarks>
/// <para>
/// One clock edge per master tick. Edge <c>e</c> is the one the clock reaches at tick
/// <c>e</c>, and it sees hc_ula and cvc as they were during tick <c>e - 1</c>. On each edge,
/// in this order:
/// </para>
/// <list type="bullet">
/// <item>A change of mode (NextReg $62 bits 7:6) takes the whole edge: modes 01 and 11 go
/// back to instruction 0. Modes 00 and 10 keep the address, and so does rewriting the same
/// mode.</item>
/// <item>Mode 11 also goes back to 0 at every edge where cvc and hc_ula are both 0.</item>
/// <item>The edge after a MOVE only clears its write pulse.</item>
/// <item>A WAIT (bit 15 set) moves on at the first edge where cvc equals its bits 8:0 and
/// hc_ula has reached its bits 14:9 x 8 + 12, a 9-bit sum (so h = 63 means hc_ula &gt;= 4).</item>
/// <item>Otherwise a MOVE: bits 14:8 are the register, 7:0 the value. A MOVE to register 0 is a
/// NOP and writes nothing. Either way the next instruction follows, wrapping at 1024.</item>
/// </list>
/// <para>
/// So a NOP takes one clock and a MOVE two. Its write reaches the register two clocks after
/// the MOVE (<c>copper_requester</c> registers it, then <c>nr_wr_en</c>), after that edge's own
/// instruction, so a MOVE to $62 is followed by one more instruction. The list RAM is read on
/// the falling edge, so every instruction sees the address set on the edge before.
/// </para>
/// <para>
/// The list is written through NextRegs $60 (a byte at a time: the high byte at an even
/// address, the low at an odd one) and $63 (an even address only stores the byte; the odd one
/// writes it with the low byte), at the 11-bit address $61/$62 hold, which moves on after
/// each write. A reset stops the Copper and keeps the list; only loading the FPGA, which a
/// hard reset does, clears it.
/// </para>
/// </remarks>
public sealed class ZxNextCopperDevice
{
    private const int ListLength = 1024;

    private readonly ushort[] _list = new ushort[ListLength];
    private ZxNextUlaTiming? _beam;

    // NextReg state.
    private int _writeAddress;
    private byte _storedByte;
    private byte _mode;

    // Execution state: the edge to run next and where it starts from.
    private int _pc;
    private byte _lastMode;
    private bool _clearingMove;
    private ulong _nextEdge;

    // Writes in flight: a MOVE's write lands two edges later. The next MOVE can be queued
    // on the edge the previous write lands on, so two can be held for a moment.
    private ulong _writeTick0;
    private byte _writeRegister0;
    private byte _writeValue0;
    private bool _hasWrite0;
    private ulong _writeTick1;
    private byte _writeRegister1;
    private byte _writeValue1;
    private bool _hasWrite1;

    // A beam search answers the same for any edge between where it started and what it
    // found, while the beam (cvc's offset, the timing) stays as it was.
    private bool _waitCacheValid;
    private ushort _waitCacheInstruction;
    private int _waitCacheGeneration;
    private ulong _waitCacheFrom;
    private ulong _waitCacheEdge;
    private bool _restartCacheValid;
    private int _restartCacheGeneration;
    private ulong _restartCacheFrom;
    private ulong _restartCacheEdge;

    // The run of NOPs found last, until the list is written.
    private int _nopRunStart = -1;
    private int _nopRunLength;

    public ZxNextCopperDevice()
    {
        PowerOn();
    }

    internal void ConfigureBeam(ZxNextUlaTiming beam)
    {
        _beam = beam ?? throw new ArgumentNullException(nameof(beam));
        InvalidateBeamCaches();
    }

    /// <summary>NextReg $62 bits 7:6.</summary>
    public byte Mode => _mode;

    /// <summary>The instruction the Copper will execute next.</summary>
    public int ProgramCounter => _pc;

    /// <summary>The 11-bit byte address the next $60/$63 write goes to.</summary>
    public int WriteAddress => _writeAddress;

    /// <summary>True while the Copper runs or a write of its is still to land.</summary>
    public bool IsActive => _mode != 0 || _lastMode != 0 || _hasWrite0;

    /// <summary>One instruction of the list.</summary>
    public ushort ReadInstruction(int index) => _list[index & (ListLength - 1)];

    // --- NextRegs ----------------------------------------------------------------------------

    internal byte ReadRegister(byte register) => register switch
    {
        0x61 => (byte)_writeAddress,
        0x62 => (byte)((_mode << 6) | ((_writeAddress >> 8) & 0x07)),
        _ => 0x00
    };

    /// <summary>A write to NextReg $60-$63, once the Copper has run up to it.</summary>
    internal void WriteRegister(byte register, byte value)
    {
        switch (register)
        {
            case 0x60:
            {
                int index = _writeAddress >> 1;
                if ((_writeAddress & 1) == 0)
                {
                    _list[index] = (ushort)((_list[index] & 0x00FF) | (value << 8));
                    _storedByte = value;
                }
                else
                {
                    _list[index] = (ushort)((_list[index] & 0xFF00) | value);
                }

                _nopRunStart = -1;
                _writeAddress = (_writeAddress + 1) & 0x7FF;
                break;
            }

            case 0x61:
                _writeAddress = (_writeAddress & 0x700) | value;
                break;
            case 0x62:
                _mode = (byte)(value >> 6);
                _writeAddress = ((value & 0x07) << 8) | (_writeAddress & 0xFF);
                break;
            case 0x63:
            {
                if ((_writeAddress & 1) == 0)
                {
                    _storedByte = value;
                }
                else
                {
                    _list[_writeAddress >> 1] = (ushort)((_storedByte << 8) | value);
                    _nopRunStart = -1;
                }

                _writeAddress = (_writeAddress + 1) & 0x7FF;
                break;
            }
        }
    }

    // --- Running -------------------------------------------------------------------------------

    /// <summary>
    /// Runs the Copper's edges up to <paramref name="endTick"/>, stopping on the first edge at
    /// which one of its writes lands. True with that write, which belongs to the machine as it
    /// is at <paramref name="writeTick"/>; false when none lands by the end.
    /// </summary>
    internal bool TryRunToNextWrite(ulong endTick, out ulong writeTick, out byte register, out byte value)
    {
        while (true)
        {
            if (_hasWrite0 && _nextEdge > _writeTick0)
            {
                writeTick = _writeTick0;
                register = _writeRegister0;
                value = _writeValue0;
                _writeTick0 = _writeTick1;
                _writeRegister0 = _writeRegister1;
                _writeValue0 = _writeValue1;
                _hasWrite0 = _hasWrite1;
                _hasWrite1 = false;
                return true;
            }

            if (_nextEdge > endTick)
            {
                writeTick = 0;
                register = 0;
                value = 0;
                return false;
            }

            RunEdges(_hasWrite0 ? Math.Min(endTick, _writeTick0) : endTick);
        }
    }

    /// <summary>
    /// The earliest tick at which a write of the Copper's could land, as things stand: the
    /// CPU publishes batched time by then. <see cref="ulong.MaxValue"/> when none can.
    /// </summary>
    internal ulong NextWriteLowerBound()
    {
        if (_hasWrite0)
        {
            return _writeTick0;
        }

        if (_mode == 0 && _lastMode == 0)
        {
            return ulong.MaxValue;
        }

        if (_lastMode != _mode || _clearingMove || _mode == 0)
        {
            return _nextEdge;
        }

        ushort instruction = _list[_pc];
        ulong bound;
        if ((instruction & 0x8000) != 0)
        {
            bound = WaitEdge(_nextEdge, instruction);
        }
        else if ((instruction & 0x7F00) == 0)
        {
            int run = NopRunFrom(_pc);
            bound = run == ListLength ? ulong.MaxValue : _nextEdge + (ulong)run;
        }
        else
        {
            bound = _nextEdge;
        }

        return _mode == 0x03 ? Math.Min(bound, RestartEdge(_nextEdge)) : bound;
    }

    /// <summary>
    /// Executes every edge up to and including <paramref name="limit"/>, or up to the edge a
    /// write queued on the way lands on, so the machine can apply it there.
    /// </summary>
    private void RunEdges(ulong limit)
    {
        while (_nextEdge <= limit)
        {
            ulong edge = _nextEdge;
            if (_lastMode != _mode)
            {
                _lastMode = _mode;
                if (_mode is 0x01 or 0x03)
                {
                    _pc = 0;
                }

                _clearingMove = false;
                _nextEdge = edge + 1;
                continue;
            }

            if (_mode == 0)
            {
                // Stopped: nothing happens until the mode changes.
                _clearingMove = false;
                _nextEdge = limit + 1;
                return;
            }

            // Mode 11 goes back to the start while cvc and hc_ula are both 0.
            ulong restart = _mode == 0x03 ? RestartEdge(edge) : ulong.MaxValue;
            if (restart == edge)
            {
                _pc = 0;
                _clearingMove = false;
                _nextEdge = edge + 1;
                continue;
            }

            ulong stop = Math.Min(limit, restart - 1);
            if (_clearingMove)
            {
                _clearingMove = false;
                _nextEdge = edge + 1;
                continue;
            }

            ushort instruction = _list[_pc];
            if ((instruction & 0x8000) != 0)
            {
                ulong satisfied = WaitEdge(edge, instruction);
                if (satisfied > stop)
                {
                    _nextEdge = stop + 1;
                    continue;
                }

                _pc = (_pc + 1) & (ListLength - 1);
                _nextEdge = satisfied + 1;
                continue;
            }

            if ((instruction & 0x7F00) == 0)
            {
                // A run of NOPs: one edge each and nothing else happens.
                int run = NopRunFrom(_pc);
                ulong edges = run == ListLength ? stop - edge + 1 : Math.Min((ulong)run, stop - edge + 1);
                _pc = (int)((_pc + (long)(edges % ListLength)) & (ListLength - 1));
                _nextEdge = edge + edges;
                continue;
            }

            QueueWrite(edge + 2, (byte)((instruction >> 8) & 0x7F), (byte)instruction);
            limit = Math.Min(limit, _writeTick0);
            _clearingMove = true;
            _pc = (_pc + 1) & (ListLength - 1);
            _nextEdge = edge + 1;
        }
    }

    private void QueueWrite(ulong tick, byte register, byte value)
    {
        if (!_hasWrite0)
        {
            _writeTick0 = tick;
            _writeRegister0 = register;
            _writeValue0 = value;
            _hasWrite0 = true;
        }
        else
        {
            _writeTick1 = tick;
            _writeRegister1 = register;
            _writeValue1 = value;
            _hasWrite1 = true;
        }
    }

    /// <summary>How many NOPs run from <paramref name="pc"/>, all 1024 when the list is nothing else.</summary>
    private int NopRunFrom(int pc)
    {
        if (_nopRunStart >= 0)
        {
            int into = (pc - _nopRunStart) & (ListLength - 1);
            if (_nopRunLength == ListLength)
            {
                return ListLength;
            }

            if (into < _nopRunLength)
            {
                return _nopRunLength - into;
            }
        }

        int run = 0;
        while (run < ListLength && (_list[(pc + run) & (ListLength - 1)] & 0xFF00) == 0)
        {
            run++;
        }

        _nopRunStart = pc;
        _nopRunLength = run;
        return run;
    }

    /// <summary>The first edge at or after <paramref name="edge"/> on which a WAIT is satisfied.</summary>
    private ulong WaitEdge(ulong edge, ushort instruction)
    {
        if (_beam is null)
        {
            return ulong.MaxValue;
        }

        int generation = _beam.CopperBeamGeneration;
        if (_waitCacheValid
            && _waitCacheInstruction == instruction
            && _waitCacheGeneration == generation
            && edge >= _waitCacheFrom
            && edge <= _waitCacheEdge)
        {
            return _waitCacheEdge;
        }

        int line = instruction & 0x01FF;
        int hcThreshold = ((((instruction >> 9) & 0x3F) << 3) + 12) & 0x1FF;
        ulong tick = _beam.FindCopperBeamTick(edge - 1, line, hcThreshold, int.MaxValue);
        ulong found = tick == ulong.MaxValue ? ulong.MaxValue : tick + 1;
        _waitCacheValid = true;
        _waitCacheInstruction = instruction;
        _waitCacheGeneration = generation;
        _waitCacheFrom = edge;
        _waitCacheEdge = found;
        return found;
    }

    /// <summary>Mode 11's restart: the first edge at or after <paramref name="edge"/> seeing cvc = hc_ula = 0.</summary>
    private ulong RestartEdge(ulong edge)
    {
        if (_beam is null)
        {
            return ulong.MaxValue;
        }

        int generation = _beam.CopperBeamGeneration;
        if (_restartCacheValid
            && _restartCacheGeneration == generation
            && edge >= _restartCacheFrom
            && edge <= _restartCacheEdge)
        {
            return _restartCacheEdge;
        }

        ulong tick = _beam.FindCopperBeamTick(edge - 1, 0, 0, 0);
        ulong found = tick == ulong.MaxValue ? ulong.MaxValue : tick + 1;
        _restartCacheValid = true;
        _restartCacheGeneration = generation;
        _restartCacheFrom = edge;
        _restartCacheEdge = found;
        return found;
    }

    private void InvalidateBeamCaches()
    {
        _waitCacheValid = false;
        _restartCacheValid = false;
    }

    // --- Reset and state ---------------------------------------------------------------------

    /// <summary>The core's reset: stopped at instruction 0, address 0; the list stays.</summary>
    internal void Reset(ulong nowTick)
    {
        _writeAddress = 0;
        _storedByte = 0;
        _mode = 0;
        _lastMode = 0;
        _pc = 0;
        _clearingMove = false;
        _hasWrite0 = false;
        _hasWrite1 = false;
        _nextEdge = nowTick + 1;
        InvalidateBeamCaches();
    }

    /// <summary>Loading the FPGA: the list RAM starts as zeros, which are NOPs.</summary>
    internal void PowerOn()
    {
        Array.Clear(_list);
        _nopRunStart = -1;
        Reset(0);
    }

    /// <summary>
    /// Brings a stopped Copper's clock to <paramref name="tick"/>, before a write that may
    /// start it: a running one is always there already.
    /// </summary>
    internal void Synchronize(ulong tick)
    {
        if (!IsActive && _nextEdge <= tick)
        {
            _nextEdge = tick + 1;
        }
    }

    public ZxNextCopperState CaptureState() => new(
        _list.ToArray(),
        (ushort)_writeAddress,
        _storedByte,
        _mode,
        (ushort)_pc,
        _lastMode,
        _clearingMove,
        _nextEdge,
        _hasWrite0,
        _writeTick0,
        _writeRegister0,
        _writeValue0,
        _hasWrite1,
        _writeTick1,
        _writeRegister1,
        _writeValue1,
        _beam?.CopperLineOffset ?? 0,
        _beam?.ActiveCopperLineOffset ?? 0,
        _beam?.NextCopperLineOffsetReloadTick ?? 0);

    /// <summary>Puts the Copper back, after the ULA timing it runs against.</summary>
    public void RestoreState(ZxNextCopperState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.List.Length != ListLength)
        {
            throw new InvalidDataException($"A copper list holds {ListLength} instructions, not {state.List.Length}.");
        }

        state.List.AsSpan().CopyTo(_list);
        _nopRunStart = -1;
        _writeAddress = state.WriteAddress & 0x7FF;
        _storedByte = state.StoredByte;
        _mode = (byte)(state.Mode & 0x03);
        _pc = state.ProgramCounter & (ListLength - 1);
        _lastMode = (byte)(state.LastMode & 0x03);
        _clearingMove = state.ClearingMove;
        _nextEdge = state.NextEdge;
        _hasWrite0 = state.HasWrite0;
        _writeTick0 = state.WriteTick0;
        _writeRegister0 = state.WriteRegister0;
        _writeValue0 = state.WriteValue0;
        _hasWrite1 = state.HasWrite1;
        _writeTick1 = state.WriteTick1;
        _writeRegister1 = state.WriteRegister1;
        _writeValue1 = state.WriteValue1;
        _beam?.RestoreCopperLineOffset(state.LineOffset, state.ActiveLineOffset, state.NextLineOffsetReloadTick);
        InvalidateBeamCaches();
    }
}

/// <summary>The Copper's list, registers and position, with the cvc offset it runs against.</summary>
public sealed record ZxNextCopperState(
    ushort[] List,
    ushort WriteAddress,
    byte StoredByte,
    byte Mode,
    ushort ProgramCounter,
    byte LastMode,
    bool ClearingMove,
    ulong NextEdge,
    bool HasWrite0,
    ulong WriteTick0,
    byte WriteRegister0,
    byte WriteValue0,
    bool HasWrite1,
    ulong WriteTick1,
    byte WriteRegister1,
    byte WriteValue1,
    byte LineOffset,
    byte ActiveLineOffset,
    ulong NextLineOffsetReloadTick);
