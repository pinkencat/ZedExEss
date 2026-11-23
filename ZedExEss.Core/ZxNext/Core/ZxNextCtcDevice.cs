namespace ZedExEss.ZxNext.Core;

/// <summary>
/// The Next's Z80 CTC: four channels transcribed from <c>ctc.vhd</c> and <c>ctc_chan.vhd</c>
/// (core 3.02.03, <c>b047011</c>), wired as <c>zxnext.vhd</c> wires them.
/// </summary>
/// <remarks>
/// <para>
/// Every channel runs on the 28 MHz clock, whatever the CPU's speed, and each edge is the
/// clock's edge at that master tick. On an edge, as <c>ctc_chan.vhd</c> does:
/// </para>
/// <list type="bullet">
/// <item>A port write is seen on the first edge after it (<c>iowr</c> is the rising edge of the
/// write strobe): in S_RESET_TC or S_RUN_TC it is the time constant, otherwise bit 0 set is a
/// control word (bits 7-3 stored) and clear a vector, which the Next ignores (its hardware IM2
/// supplies vectors). A control word with bit 1 is a software reset, to S_RESET or, with bit 2,
/// to S_RESET_TC; bit 2 alone asks for a time constant (S_RESET_TC from S_RESET or S_TRIGGER,
/// S_RUN_TC from S_RUN). After the time constant the channel waits in S_TRIGGER for one edge,
/// or for its trigger in timer mode with bit 3.</item>
/// <item>Outside S_RUN and S_RUN_TC the channel is in soft reset: the prescaler is held at 0
/// and the counter at the time constant.</item>
/// <item>The prescaler counts every edge; it fires when its low nibble is $F (divide by 16) or
/// it is $FF (bit 5, divide by 256). A timer counts on the prescaler, a counter (bit 6) on its
/// trigger's edge: falling, or rising with bit 4. A control word that changes bit 4 is an edge
/// itself.</item>
/// <item>The edge that would count 1 to 0 is the zero count (ZC/TO) and reloads the time
/// constant instead, so the counter never reads 0 except with a constant of 0, which counts
/// 256.</item>
/// <item>ZC/TO is registered (<c>zc_to_d</c>): it is high for the clock after the zero-count
/// edge. Channel n's trigger is channel n-1's ZC/TO and channel 0's is channel 3's
/// (<c>ctc_zc_to(2 downto 0) &amp; ctc_zc_to(3)</c>), so a rising-edge counter counts on the
/// edge after its upstream's zero count and a falling-edge one on the edge after that.</item>
/// </list>
/// <para>
/// The interrupt controller sees each ZC/TO as a request on the edge after the zero count
/// (<c>im2_peripheral.vhd</c> latches the rising edge of <c>int_req</c>). The interrupt enable
/// is control bit 7, which a control word or NextReg $C5 writes.
/// </para>
/// <para>
/// Ports <c>$183B</c>-<c>$1F3B</c> (A15-11 = 00011, low byte $3B) under port enable bit 27;
/// A10-8 select the channel and channels 4-7 do not exist (reads $00, writes lost). A read
/// returns the counter as of the read; with writes seen an edge later, that reproduces the
/// counter Klive's simulated-core probes read (its fidelity plan's F3-8, probe group T).
/// </para>
/// <para>
/// Between events the channels are advanced analytically: a running timer's prescaler and
/// counter move by arithmetic, and only zero counts, trigger edges, writes and the edge that
/// leaves S_TRIGGER are visited.
/// </para>
/// </remarks>
public sealed class ZxNextCtcDevice
{
    public const int ChannelCount = 4;

    /// <summary>Control bits as stored (<c>control_reg</c> is bits 7-3 of the control word).</summary>
    public const byte InterruptEnableBit = 0x80;
    public const byte CounterModeBit = 0x40;
    public const byte Prescaler256Bit = 0x20;
    public const byte RisingEdgeBit = 0x10;
    public const byte TriggerStartBit = 0x08;

    private readonly Channel[] _channels = [new(), new(), new(), new()];
    private ulong _tick;
    private int _pendingWriteChannel = -1;
    private byte _pendingWriteValue;
    private ulong _pendingWriteEdge;
    private int _pendingRequests;
    private ulong _pendingRequestEdge;
    private ulong _nextEvent = ulong.MaxValue;

    /// <summary>Raised for each zero count, with the channel and the tick the request is seen at.</summary>
    internal Action<int, ulong>? ZeroCount { get; set; }

    /// <summary>The last edge the channels have been brought to.</summary>
    public ulong Tick => _tick;

    /// <summary>
    /// The earliest edge anything changes that the rest of the machine can see without asking:
    /// a zero count, a write still to be seen, or a trigger. Max when nothing is pending.
    /// </summary>
    public ulong NextEventEdge => _nextEvent;

    /// <summary>The interrupt enables (control bit 7) of channels 0-3, as NextReg $C5 reads them.</summary>
    public byte InterruptEnables
    {
        get
        {
            byte value = 0;
            for (int channel = 0; channel < ChannelCount; channel++)
            {
                value |= (byte)((_channels[channel].Control & InterruptEnableBit) != 0 ? 1 << channel : 0);
            }

            return value;
        }
    }

    public bool InterruptEnabled(int channel) => (_channels[channel].Control & InterruptEnableBit) != 0;

    public ZxNextCtcChannelState GetChannel(int channel)
    {
        Channel c = _channels[channel];
        return new ZxNextCtcChannelState((byte)c.State, c.Control, c.TimeConstant, c.Prescaler, c.Count, c.LastZeroCountEdge);
    }

    /// <summary>The CTC port decode: A15-11 = 00011 and A7-0 = $3B.</summary>
    public static bool IsCtcPort(ushort port) => (port & 0xF8FF) == 0x183B;

    /// <summary>A read of a CTC port at <paramref name="tick"/>: the selected channel's counter.</summary>
    public byte Read(ushort port, ulong tick)
    {
        int channel = (port >> 8) & 0x07;
        if (channel >= ChannelCount)
        {
            return 0x00;
        }

        Advance(tick);
        return _channels[channel].Count;
    }

    /// <summary>A write to a CTC port at <paramref name="tick"/>, seen on the next edge.</summary>
    public void Write(ushort port, byte value, ulong tick)
    {
        int channel = (port >> 8) & 0x07;
        if (channel >= ChannelCount)
        {
            return;
        }

        Advance(tick);
        if (_pendingWriteChannel >= 0)
        {
            // Two writes cannot be a clock apart; the earlier one is seen first.
            Advance(_pendingWriteEdge);
        }

        _pendingWriteChannel = channel;
        _pendingWriteValue = value;
        _pendingWriteEdge = Math.Max(tick, _tick) + 1;
        UpdateNextEvent();
    }

    /// <summary>NextReg $C5: control bit 7 of each channel (<c>i_int_en_wr</c>).</summary>
    public void WriteInterruptEnables(byte value)
    {
        for (int channel = 0; channel < ChannelCount; channel++)
        {
            Channel c = _channels[channel];
            c.Control = (value & (1 << channel)) != 0
                ? (byte)(c.Control | InterruptEnableBit)
                : (byte)(c.Control & ~InterruptEnableBit);
        }
    }

    /// <summary>Brings every channel to <paramref name="tick"/>, raising the zero counts on the way.</summary>
    public void Advance(ulong tick)
    {
        while (_nextEvent <= tick)
        {
            ulong edge = _nextEvent;
            if (_pendingRequests != 0 && _pendingRequestEdge == edge)
            {
                // The requests first; anything else on this edge is the next pass's.
                RaisePendingRequests(edge);
                UpdateNextEvent();
                continue;
            }

            ProcessEdge(edge);
            UpdateNextEvent();
        }

        if (tick > _tick)
        {
            for (int channel = 0; channel < ChannelCount; channel++)
            {
                SkipTo(_channels[channel], tick);
            }

            _tick = tick;
        }
    }

    /// <summary>The machine's reset (zxnext.vhd's reset, soft or hard): every channel to S_RESET.</summary>
    public void Reset(ulong tick)
    {
        foreach (Channel c in _channels)
        {
            c.State = ChannelState.Reset;
            c.Control = 0;
            c.TimeConstant = 0;
            c.Prescaler = 0;
            c.Count = 0;
            c.LastZeroCountEdge = 0;
        }

        _tick = tick;
        _pendingWriteChannel = -1;
        _pendingRequests = 0;
        _nextEvent = ulong.MaxValue;
    }

    public ZxNextCtcState CaptureState()
    {
        var channels = new ZxNextCtcChannelState[ChannelCount];
        for (int channel = 0; channel < ChannelCount; channel++)
        {
            channels[channel] = GetChannel(channel);
        }

        return new ZxNextCtcState(channels, _tick, (sbyte)_pendingWriteChannel, _pendingWriteValue, _pendingWriteEdge)
        {
            PendingRequests = (byte)_pendingRequests,
            PendingRequestEdge = _pendingRequestEdge
        };
    }

    public void RestoreState(ZxNextCtcState state)
    {
        for (int channel = 0; channel < ChannelCount; channel++)
        {
            ZxNextCtcChannelState s = state.Channels[channel];
            Channel c = _channels[channel];
            c.State = (ChannelState)s.State;
            c.Control = s.Control;
            c.TimeConstant = s.TimeConstant;
            c.Prescaler = s.Prescaler;
            c.Count = s.Count;
            c.LastZeroCountEdge = s.LastZeroCountEdge;
        }

        _tick = state.Tick;
        _pendingWriteChannel = state.PendingWriteChannel;
        _pendingWriteValue = state.PendingWriteValue;
        _pendingWriteEdge = state.PendingWriteEdge;
        _pendingRequests = state.PendingRequests;
        _pendingRequestEdge = state.PendingRequestEdge;
        UpdateNextEvent();
    }

    // --- The edge-level model ---------------------------------------------------------------

    private enum ChannelState : byte
    {
        Reset,
        ResetTimeConstant,
        Trigger,
        Run,
        RunTimeConstant
    }

    private sealed class Channel
    {
        public ChannelState State;
        public byte Control;
        public byte TimeConstant;
        public byte Prescaler;
        public byte Count;

        /// <summary>The edge of the last zero count (ZC/TO is high for the clock after it); 0 for none.</summary>
        public ulong LastZeroCountEdge;

        public bool Running => State is ChannelState.Run or ChannelState.RunTimeConstant;
        public bool CounterMode => (Control & CounterModeBit) != 0;
        public int PrescalerMask => (Control & Prescaler256Bit) != 0 ? 0xFF : 0x0F;
    }

    /// <summary>
    /// The trigger edge a channel sees from its upstream's last zero count: on the edge after it
    /// for a rising-edge channel, the edge after that for a falling-edge one.
    /// </summary>
    private ulong TriggerEdgeAfter(int channel, ulong after)
    {
        Channel upstream = _channels[(channel + ChannelCount - 1) % ChannelCount];
        if (upstream.LastZeroCountEdge == 0)
        {
            return ulong.MaxValue;
        }

        ulong edge = upstream.LastZeroCountEdge
            + ((_channels[channel].Control & RisingEdgeBit) != 0 ? 1UL : 2UL);
        return edge > after ? edge : ulong.MaxValue;
    }

    private bool TriggerEdgeAt(int channel, ulong edge) => TriggerEdgeAfter(channel, edge - 1) == edge;

    /// <summary>A running timer's next zero count after edge <paramref name="after"/>.</summary>
    private static ulong TimerZeroCountEdge(Channel c, ulong after)
    {
        int mask = c.PrescalerMask;
        ulong firstFire = after + (ulong)((mask - (c.Prescaler & mask)) & mask) + 1;
        ulong fires = c.Count == 0 ? 256UL : c.Count;
        return firstFire + ((fires - 1) * (ulong)(mask + 1));
    }

    /// <summary>The next edge after the channels' tick at which something happens.</summary>
    private void UpdateNextEvent()
    {
        ulong next = ulong.MaxValue;
        if (_pendingWriteChannel >= 0)
        {
            next = _pendingWriteEdge;
        }

        if (_pendingRequests != 0)
        {
            next = Math.Min(next, _pendingRequestEdge);
        }

        for (int channel = 0; channel < ChannelCount; channel++)
        {
            Channel c = _channels[channel];
            ulong edge = ulong.MaxValue;
            if (c.Running)
            {
                edge = c.CounterMode ? TriggerEdgeAfter(channel, _tick) : TimerZeroCountEdge(c, _tick);
            }
            else if (c.State == ChannelState.Trigger)
            {
                bool waits = !c.CounterMode && (c.Control & TriggerStartBit) != 0;
                edge = waits ? TriggerEdgeAfter(channel, _tick) : _tick + 1;
            }

            next = Math.Min(next, edge);
        }

        _nextEvent = next;
    }

    /// <summary>
    /// Advances a channel through edges with nothing on them up to <paramref name="tick"/>: a
    /// running timer's prescaler and counter move, a counter's prescaler moves, and a channel in
    /// soft reset holds its prescaler at 0 and its counter at the time constant.
    /// </summary>
    private void SkipTo(Channel c, ulong tick)
    {
        if (tick <= _tick)
        {
            return;
        }

        if (!c.Running)
        {
            c.Prescaler = 0;
            c.Count = c.TimeConstant;
            return;
        }

        ulong edges = tick - _tick;
        if (!c.CounterMode)
        {
            int mask = c.PrescalerMask;
            ulong firstFire = (ulong)((mask - (c.Prescaler & mask)) & mask) + 1;
            if (edges >= firstFire)
            {
                ulong fires = 1 + ((edges - firstFire) / (ulong)(mask + 1));
                c.Count = (byte)(c.Count - (int)(fires & 0xFF));
            }
        }

        c.Prescaler = (byte)(c.Prescaler + (int)(edges & 0xFF));
    }

    /// <summary>One clock edge for every channel, as ctc_chan.vhd computes it from the state before.</summary>
    private void ProcessEdge(ulong edge)
    {
        // Every channel up to the edge before, so this edge reads the registers it would.
        for (int channel = 0; channel < ChannelCount; channel++)
        {
            SkipTo(_channels[channel], edge - 1);
        }

        _tick = edge - 1;
        int zeroCounts = 0;
        int writeChannel = _pendingWriteChannel >= 0 && _pendingWriteEdge == edge ? _pendingWriteChannel : -1;
        byte data = _pendingWriteValue;
        for (int channel = 0; channel < ChannelCount; channel++)
        {
            if (ClockChannel(channel, edge, channel == writeChannel, data))
            {
                zeroCounts |= 1 << channel;
            }
        }

        if (writeChannel >= 0)
        {
            _pendingWriteChannel = -1;
        }

        _tick = edge;
        for (int channel = 0; channel < ChannelCount; channel++)
        {
            if ((zeroCounts & (1 << channel)) != 0)
            {
                _channels[channel].LastZeroCountEdge = edge;
            }
        }

        // im2_peripheral.vhd latches the rising edge of ZC/TO on the edge after the zero count.
        if (zeroCounts != 0)
        {
            if (_pendingRequests != 0)
            {
                RaisePendingRequests(_pendingRequestEdge);
            }

            _pendingRequests = zeroCounts;
            _pendingRequestEdge = edge + 1;
        }
    }

    private void RaisePendingRequests(ulong tick)
    {
        int requests = _pendingRequests;
        _pendingRequests = 0;
        for (int channel = 0; channel < ChannelCount; channel++)
        {
            if ((requests & (1 << channel)) != 0)
            {
                ZeroCount?.Invoke(channel, tick);
            }
        }
    }

    /// <summary>ctc_chan.vhd for one edge; true for a zero count.</summary>
    private bool ClockChannel(int channel, ulong edge, bool write, byte data)
    {
        Channel c = _channels[channel];
        bool expectingConstant = c.State is ChannelState.ResetTimeConstant or ChannelState.RunTimeConstant;
        bool writeConstant = write && expectingConstant;
        bool writeControl = write && !expectingConstant && (data & 0x01) != 0;
        bool edgeChange = writeControl && ((data & RisingEdgeBit) != 0) != ((c.Control & RisingEdgeBit) != 0);
        bool triggerEdge = TriggerEdgeAt(channel, edge) || edgeChange;
        int mask = c.PrescalerMask;
        bool prescalerFires = (c.Prescaler & mask) == mask;
        bool countEnable = c.CounterMode ? triggerEdge : prescalerFires;
        bool running = c.Running;
        bool zeroCount = running && countEnable && c.Count == 1;

        ChannelState next;
        if (writeControl && (data & 0x02) != 0)
        {
            next = (data & 0x04) == 0 ? ChannelState.Reset : ChannelState.ResetTimeConstant;
        }
        else
        {
            next = c.State switch
            {
                ChannelState.Reset => writeControl && (data & 0x04) != 0 ? ChannelState.ResetTimeConstant : ChannelState.Reset,
                ChannelState.ResetTimeConstant => write ? ChannelState.Trigger : ChannelState.ResetTimeConstant,
                ChannelState.Trigger => writeControl && (data & 0x04) != 0
                    ? ChannelState.ResetTimeConstant
                    : !c.CounterMode && (c.Control & TriggerStartBit) != 0 && !triggerEdge
                        ? ChannelState.Trigger
                        : ChannelState.Run,
                ChannelState.Run => writeControl && (data & 0x04) != 0 ? ChannelState.RunTimeConstant : ChannelState.Run,
                ChannelState.RunTimeConstant => write ? ChannelState.Run : ChannelState.RunTimeConstant,
                _ => ChannelState.Reset
            };
        }

        byte timeConstant = c.TimeConstant;
        if (!running)
        {
            c.Prescaler = 0;
            c.Count = timeConstant;
        }
        else
        {
            c.Prescaler++;
            if (zeroCount)
            {
                c.Count = timeConstant;
            }
            else if (countEnable)
            {
                c.Count--;
            }
        }

        if (writeControl)
        {
            c.Control = (byte)(data & 0xF8);
        }

        if (writeConstant)
        {
            c.TimeConstant = data;
        }

        c.State = next;
        return zeroCount;
    }
}

/// <summary>One CTC channel: its state, control bits 7-3, time constant, prescaler and counter.</summary>
public readonly record struct ZxNextCtcChannelState(
    byte State,
    byte Control,
    byte TimeConstant,
    byte Prescaler,
    byte Count,
    ulong LastZeroCountEdge);

/// <summary>The CTC: the four channels, the edge they were brought to and a write not yet seen.</summary>
public sealed record ZxNextCtcState(
    ZxNextCtcChannelState[] Channels,
    ulong Tick,
    sbyte PendingWriteChannel,
    byte PendingWriteValue,
    ulong PendingWriteEdge)
{
    /// <summary>Zero counts the interrupt controller sees on the next edge (bit per channel).</summary>
    public byte PendingRequests { get; init; }

    /// <summary>The edge those requests are seen on.</summary>
    public ulong PendingRequestEdge { get; init; }
}
