using System;
using ZedExEss.Spectrum.Abstractions;
using ZedExEss.Spectrum.Memory;
using ZedExEss.Z80CPU;

namespace ZedExEss.Spectrum.Tape
{
    /// <summary>
    /// EAR input port device, tape monitor sink and edge-loader integration point.
    /// </summary>
    /// <remarks>
    /// Ordinary playback only updates _earHigh. Optional accelerators are installed
    /// as CPU hooks and are kept dormant when disabled, so the normal port-read hot
    /// path remains a level read plus edge acknowledgement.
    /// </remarks>
    public sealed class SpectrumEarInputDevice(IEarInputSink? audioSink = null) : IPortDevice, IEarInputSink
    {
        private bool _earHigh;
        private readonly IEarInputSink? _audioSink = audioSink;
        private ITapeEdgeSource? _edgeSource;
        private SemanticTapeEdgeAccelerator? _semanticAccelerator;
        private TapePollingLoopDetector? _pollingLoopDetector;
        private Z80? _cpu;
        private bool _edgeLoadingEnabled;
        private bool _semanticAccelerationEnabled;
        private bool _autoPlayEnabled;

        public event Action? AutoPlayRequested;

        /// <summary>
        /// Diagnostic access for the headless tape verification runner.
        /// </summary>
        public SemanticTapeEdgeAccelerator? SemanticAccelerator => _semanticAccelerator;

        /// <summary>
        /// Gets whether either tape-loader acceleration engine is enabled.
        /// </summary>
        /// <remarks>
        /// Frontends use this aggregate state to select the single
        /// <see cref="Core.TapeFastRunner"/> execution owner. Polling and semantic
        /// acceleration remain separate read algorithms, but must never create
        /// competing execution paths.
        /// </remarks>
        public bool LoaderAccelerationEnabled => _edgeLoadingEnabled || _semanticAccelerationEnabled;

        public bool EdgeLoadingEnabled
        {
            get => _edgeLoadingEnabled;
            set
            {
                if (_edgeLoadingEnabled == value)
                {
                    return;
                }

                _edgeLoadingEnabled = value;
                UpdateCpuTapeHook();
            }
        }

        public bool AutoPlayEnabled
        {
            get => _autoPlayEnabled;
            set
            {
                if (_autoPlayEnabled == value)
                {
                    return;
                }

                _autoPlayEnabled = value;
                UpdateCpuTapeHook();
            }
        }

        /// <summary>
        /// Enables the experimental semantic accelerator independently
        /// of the timing-preserving polling-loop accelerator.
        /// </summary>
        public bool SemanticAccelerationEnabled
        {
            get => _semanticAccelerationEnabled;
            set
            {
                if (_semanticAccelerationEnabled == value)
                {
                    return;
                }

                _semanticAccelerationEnabled = value;
                // Discard any claim prepared before the setting changed. This also
                // resets the delayed pulse classifier at the opt-in boundary.
                _semanticAccelerator?.Configure(_edgeSource, () => _earHigh);
                UpdateCpuTapeHook();
            }
        }
        public void ConfigureEdgeLoading(ITapeEdgeSource? edgeSource)
        {
            _edgeSource = edgeSource;
            _semanticAccelerator?.Configure(edgeSource, () => _earHigh);
            _pollingLoopDetector?.Configure(edgeSource);
        }
        public void ConfigureAcceleration(Z80 cpu, SpectrumMemory memory, Action<ushort, byte>? writePort = null)
        {
            _cpu?.ConfigureTapeAccelerationHook(null);
            _cpu = cpu;
            _ = writePort; // Kept for source compatibility with existing machine setup code.
            _semanticAccelerator = new SemanticTapeEdgeAccelerator(cpu, memory);
            _semanticAccelerator.Configure(_edgeSource, () => _earHigh);
            _pollingLoopDetector = new TapePollingLoopDetector(cpu, memory);
            _pollingLoopDetector.Configure(_edgeSource);
            _pollingLoopDetector.PrimaryReadClaim = TryClaimSemanticRead;
            _pollingLoopDetector.LoaderPollingDetected = () => AutoPlayRequested?.Invoke();
            UpdateCpuTapeHook();
        }
        public bool HandlesPort(ushort port)
        {
            return (port & 0x0001) == 0;
        }
        public byte Read(ushort port)
        {
            return ReadEarBit() ? (byte)0xFF : (byte)0xBF;
        }
        public void Write(ushort port, byte value)
        {
        }
        public bool ReadEarBit()
        {
            if (_semanticAccelerationEnabled && _edgeSource?.IsPlaying == true)
            {
                _semanticAccelerator?.TryAcceleratePreparedRead();
            }

            _edgeSource?.ClearEdgeSeen();
            return _earHigh;
        }
        public void SetEarLevel(bool high)
        {
            _earHigh = high;
            _audioSink?.SetEarLevel(high);
        }
        public string GetAccelerationStatus()
        {
            if (!LoaderAccelerationEnabled)
            {
                return "Accel: off";
            }

            long pollingEvents = _pollingLoopDetector?.SkipEvents ?? 0;
            long pollingLoops = _pollingLoopDetector?.SkippedLoopIterations ?? 0;
            long pollingProfileHits = _pollingLoopDetector?.ProfileHits ?? 0;
            long semanticMatches = _semanticAccelerator?.MatchedReads ?? 0;
            long semanticEvents = _semanticAccelerator?.AccelerationCount ?? 0;
            ulong pollingAt = _pollingLoopDetector?.LastSkipCpuTstate ?? 0;
            ulong semanticAt = _semanticAccelerator?.LastAccelerationCpuTstate ?? 0;

            string active = "waiting";
            if (pollingEvents > 0 || semanticEvents > 0)
            {
                active = pollingAt >= semanticAt ? "polling" : "semantic";
            }

            string semantic = _semanticAccelerationEnabled ? "on (experimental)" : "off";
            string polling = _edgeLoadingEnabled ? "on" : "off";
            return $"Accel: {active} | semantic {semantic}: {semanticEvents} edges/{semanticMatches} matches | polling {polling}: {pollingEvents} skips/{pollingLoops} loops/{pollingProfileHits} profile hits";
        }
        private bool TryClaimSemanticRead(ushort opcodePc, byte portLow)
        {
            return _semanticAccelerationEnabled
                && _semanticAccelerator?.TryClaimRead(opcodePc, portLow) == true;
        }
        private void UpdateCpuTapeHook()
        {
            if (_pollingLoopDetector != null)
            {
                _pollingLoopDetector.SkippingEnabled = _edgeLoadingEnabled;
                _pollingLoopDetector.AutoPlayDetectionEnabled = _autoPlayEnabled;
                _pollingLoopDetector.Enabled = LoaderAccelerationEnabled || _autoPlayEnabled;
            }

            bool hookEnabled = LoaderAccelerationEnabled || _autoPlayEnabled;
            _cpu?.ConfigureTapeAccelerationHook(hookEnabled ? _pollingLoopDetector : null);
        }
    }
}
