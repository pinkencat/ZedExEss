using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System;
using ZedExEss.Spectrum.Abstractions;
using ZedExEss.Spectrum.Audio;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.Disk.Beta;
using ZedExEss.Spectrum.Disk.Plus3;
using ZedExEss.Spectrum.Interface1;
using ZedExEss.Spectrum.Video;
using ZedExEss.Z80CPU;

namespace ZedExEss.Spectrum.Ports
{
    /// <summary>
    /// Decodes Spectrum I/O ports and applies model-specific I/O contention before dispatching to devices.
    /// </summary>
    /// <remarks>
    /// Spectrum peripherals are not exclusively selected: several devices may
    /// respond to the same partially decoded port and their values are combined by
    /// wired AND. Built-in devices use typed fields to keep this CPU hot path free of
    /// allocations and repeated type checks.
    /// </remarks>
    public sealed class SpectrumPortBus(
        SpectrumModel model,
        Z80? cpu = null,
        IContentionProfile? contention = null,
        IContendedPageProvider? contendedPages = null,
        IFloatingBus? floatingBus = null) : IPortBus, IZ80PortBus
    {
        // Some models expose writeback effects on a later bus point; queueing
        // here lets reads observe the same ordering as the emulated CPU.
        private readonly Queue<PendingWrite> _pendingWrites = new();
        private readonly List<IPortDevice> _fallbackDevices = [];
        private readonly SpectrumModel _model = model;
        private readonly bool _writebackOnRead = SpectrumModelTraits.HasPagingWritebackOnRead(model);
        private readonly bool _ulaUsesFullPortDecode = SpectrumModelTraits.HasFullyDecodedUlaPort(model);
        private Z80? _cpu = cpu;
        private IContentionProfile? _contention = contention;
        private IContendedPageProvider? _contendedPages = contendedPages;
        private IFloatingBus? _floatingBus = floatingBus;
        private SpectrumUla? _ula;
        private SpectrumPagingDevice? _paging;
        private SpectrumAyDevice? _ay;
        private SpectrumPlus3DiskController? _plus3Disk;
        private SpectrumBeta128DiskController? _beta128Disk;
        private SpectrumInterface1Device? _interface1;
        public void ConfigureTiming(Z80? cpu, IContentionProfile? contention, IContendedPageProvider? contendedPages)
        {
            _cpu = cpu;
            _contention = contention;
            _contendedPages = contendedPages;
            _pendingWrites.Clear();
        }
        public void ConfigureFloatingBus(IFloatingBus? floatingBus)
        {
            _floatingBus = floatingBus;
        }

        /// <summary>Discards delayed port effects belonging to a replaced snapshot state.</summary>
        public void ClearPendingWrites()
        {
            _pendingWrites.Clear();
        }

        /// <summary>
        /// Applies a peripheral-generated Z80 WAIT interval before the next machine
        /// cycle. Returns true when the CPU must remain stopped because the releasing
        /// edge has not yet been produced by the peer station.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool ApplyPeripheralWait()
        {
            Z80? cpu = _cpu;
            SpectrumInterface1Device? interface1 = _interface1;
            if (cpu == null || interface1 == null ||
                !interface1.TryGetNetworkWait(cpu.Cyc, out ulong releaseAt))
            {
                return false;
            }

            if (releaseAt == 0)
            {
                return true;
            }

            ulong remaining = releaseAt - cpu.Cyc;
            while (remaining > 0)
            {
                int step = (int)Math.Min(remaining, int.MaxValue);
                cpu.AddWaitStates(step);
                remaining -= (uint)step;
            }

            return interface1.TryGetNetworkWait(cpu.Cyc, out _);
        }
        public void AddDevice(IPortDevice device)
        {
            ArgumentNullException.ThrowIfNull(device);

            // Keep the common built-in devices in typed fields to avoid walking the fallback list
            // for every ULA, paging, AY or disk access.
            switch (device)
            {
                case SpectrumUla ula:
                    _ula = ula;
                    break;
                case SpectrumPagingDevice paging:
                    _paging = paging;
                    break;
                case SpectrumAyDevice ay:
                    _ay = ay;
                    break;
                case SpectrumPlus3DiskController plus3Disk:
                    _plus3Disk = plus3Disk;
                    break;
                case SpectrumBeta128DiskController beta128Disk:
                    _beta128Disk = beta128Disk;
                    beta128Disk.ConfigureCpuClock(SpectrumModelTraits.CpuClockHz(_model));
                    break;
                case SpectrumInterface1Device interface1:
                    _interface1 = interface1;
                    break;
                default:
                    _fallbackDevices.Add(device);
                    break;
            }
        }
        public byte Read(ushort port)
        {
            // Read timing determines the floating-bus sample point, so compute the post-contention
            // timestamp before dispatching the read.
            ulong readAt = ApplyReadContention(port);
            byte value = ReadUncontended(port, readAt);

            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte ReadUncontended(ushort port)
        {
            ulong readAt = _cpu?.Cyc ?? 0;
            return ReadUncontended(port, readAt);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte ReadUncontended(ushort port, ulong readAt)
        {

            byte value = 0xFF;
            bool handled = false;

            // Spectrum I/O is mostly wired-AND: multiple devices can respond and clear bits.
            if (IsUlaPort(port) && _ula != null)
            {
                handled = true;
                value &= _ula.Read(port);
            }

            SpectrumAyDevice? ay = _ay;
            if (ay != null)
            {
                ushort ayMasked = (ushort)(port & 0xC002);
                if (ayMasked == 0xC000 || ayMasked == 0x8000)
                {
                    handled = true;
                    value &= ay.Read(port);
                }
            }

            SpectrumPagingDevice? paging = _paging;
            if (paging != null && paging.HandlesPort(port))
            {
                handled = true;
                value &= paging.Read(port);
            }

            SpectrumPlus3DiskController? plus3Disk = _plus3Disk;
            if (plus3Disk != null && plus3Disk.HandlesPort(port))
            {
                handled = true;
                value &= plus3Disk.Read(port);
            }

            SpectrumBeta128DiskController? beta128Disk = _beta128Disk;
            if (beta128Disk != null && beta128Disk.HandlesPort(port))
            {
                beta128Disk.SetBusTstate(readAt);
                return (byte)(value & beta128Disk.Read(port));
            }

            SpectrumInterface1Device? interface1 = _interface1;
            if (interface1 != null && interface1.HandlesPort(port))
            {
                handled = true;
                interface1.SetBusTstate(readAt);
                value &= interface1.Read(port);
            }

            for (int i = 0; i < _fallbackDevices.Count; i++)
            {
                IPortDevice device = _fallbackDevices[i];
                if (device.HandlesPort(port))
                {
                    handled = true;
                    value &= device.Read(port);
                }
            }

            if (!handled)
            {
                // Unhandled reads return model-specific floating bus values where available.
                value = _floatingBus?.Read(port, readAt) ?? 0xFF;
            }

            if (_writebackOnRead && (port & 0x8002) == 0)
            {
                // +2A/+3 paging hardware mirrors some reads into the paging latch.
                ApplyWrite(0x7ffd, value);
            }

            return value;
        }
        public void Write(ushort port, byte value)
        {
            if (_cpu == null || _contention == null)
            {
                ApplyWrite(port, value);
                return;
            }

            // The observable write happens during the I/O cycle, before the CPU has consumed
            // the full instruction timing. Queue it so the scheduler exposes it at that point.
            ulong applyAt = ApplyWriteContentionEarly(port, out long tstate, out bool contendedPage);
            _pendingWrites.Enqueue(new PendingWrite(applyAt, port, value));
            ApplyWriteContentionLate(port, ref tstate, contendedPage);
        }
        public void WriteUncontended(ushort port, byte value)
        {
            ApplyWrite(port, value);
        }
        public void ApplyIoContentionBeforeCycle(ushort port, int phase)
        {
            // Used by the CPU core for instructions whose I/O contention is split across
            // documented machine-cycle phases rather than represented as one bus operation.
            if (_cpu == null || _contention == null)
            {
                return;
            }

            bool contendedPage = _contendedPages != null && _contendedPages.IsContendedPage((port >> 14) & 0x3);
            bool ulaPort = _contention.IsUlaPort(port);

            if (phase == 0)
            {
                if (contendedPage)
                {
                    AddNoMreqDelay();
                }

                return;
            }

            if (ulaPort)
            {
                if (phase == 1)
                {
                    AddNoMreqDelay();
                }

                return;
            }

            if (contendedPage)
            {
                AddNoMreqDelay();
            }
        }
        private ulong ApplyReadContention(ushort port)
        {
            Z80? cpu = _cpu;
            if (cpu == null || _contention == null)
            {
                return 0;
            }

            bool contendedPage = _contendedPages != null && _contendedPages.IsContendedPage((port >> 14) & 0x3);
            long tstate = (long)cpu.Cyc;

            // The high address byte during I/O can point at contended RAM, adding no-MREQ delays
            // even though the actual data transfer is through the port bus.
            if (contendedPage)
            {
                AddNoMreqDelay(ref tstate);
            }

            tstate += 1;

            if (_contention.IsUlaPort(port))
            {
                AddNoMreqDelay(ref tstate);
                tstate += 2;
            }
            else
            {
                if (contendedPage)
                {
                    AddNoMreqDelay(ref tstate);
                    tstate += 1;
                    AddNoMreqDelay(ref tstate);
                    tstate += 1;
                    AddNoMreqDelay(ref tstate);
                }
                else
                {
                    tstate += 2;
                }
            }

            return (ulong)tstate;
        }
        private ulong ApplyWriteContentionEarly(ushort port, out long tstate, out bool contendedPage)
        {
            tstate = (long)_cpu!.Cyc;
            contendedPage = _contendedPages != null && _contendedPages.IsContendedPage((port >> 14) & 0x3);

            if (contendedPage)
            {
                AddNoMreqDelay(ref tstate);
            }

            // Device side effects occur after the address has settled but before the
            // remaining no-MREQ waits complete.
            ulong applyAt = (ulong)(tstate + 1);
            tstate += 1;
            return applyAt;
        }
        private void ApplyWriteContentionLate(ushort port, ref long tstate, bool contendedPage)
        {
            if (_cpu == null || _contention == null)
            {
                return;
            }

            if (_contention.IsUlaPort(port))
            {
                AddNoMreqDelay(ref tstate);
                tstate += 2;
            }
            else
            {
                if (contendedPage)
                {
                    AddNoMreqDelay(ref tstate);
                    tstate += 1;
                    AddNoMreqDelay(ref tstate);
                    tstate += 1;
                    AddNoMreqDelay(ref tstate);
                }
                else
                {
                    tstate += 2;
                }
            }
        }
        private void AddNoMreqDelay(ref long tstate)
        {
            Z80? cpu = _cpu;
            if (cpu == null || _contention == null)
            {
                return;
            }

            int delay = _contention.GetNoMreqDelay((ulong)tstate);
            if (delay > 0)
            {
                cpu.AddWaitStates(delay);
                tstate += delay;
            }
        }
        private void AddNoMreqDelay()
        {
            Z80? cpu = _cpu;
            if (cpu == null || _contention == null)
            {
                return;
            }

            int delay = _contention.GetNoMreqDelay(cpu.Cyc);
            if (delay > 0)
            {
                cpu.AddWaitStates(delay);
            }
        }
        public void FlushPendingWrites(ulong tstates)
        {
            // Drained by the central scheduler before any subsystem observes the target T-state.
            while (_pendingWrites.Count > 0 && _pendingWrites.Peek().ApplyAt <= tstates)
            {
                PendingWrite write = _pendingWrites.Dequeue();
                ApplyWrite(write.Port, write.Value, write.ApplyAt);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryPeekPendingWrite(out ulong tstate)
        {
            if (_pendingWrites.Count == 0)
            {
                tstate = 0;
                return false;
            }

            tstate = _pendingWrites.Peek().ApplyAt;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ApplyWrite(ushort port, byte value)
        {
            ApplyWrite(port, value, _cpu?.Cyc ?? 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsUlaPort(ushort port)
        {
            return _ulaUsesFullPortDecode
                ? (port & 0x00FF) == 0x00FE
                : (port & 0x0001) == 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ApplyWrite(ushort port, byte value, ulong applyAt)
        {
            // Order matters when devices share decode ranges. ULA/border updates happen first,
            // then paging/disk/audio/expansion devices see the same bus write.
            if (IsUlaPort(port))
            {
                _ula?.Write(port, value);
            }

            _paging?.Write(port, value);

            SpectrumPlus3DiskController? plus3Disk = _plus3Disk;
            if (plus3Disk != null)
            {
                if (SpectrumPlus3DiskController.IsMotorPort(port))
                {
                    plus3Disk.SetMotorControl(value);
                }

                if (plus3Disk.HandlesPort(port))
                {
                    plus3Disk.Write(port, value);
                }
            }

            SpectrumBeta128DiskController? beta128Disk = _beta128Disk;
            if (beta128Disk != null && beta128Disk.HandlesPort(port))
            {
                beta128Disk.SetBusTstate(applyAt);
                beta128Disk.Write(port, value);
                return;
            }

            SpectrumAyDevice? ay = _ay;
            if (ay != null)
            {
                ushort ayMasked = (ushort)(port & 0xC002);
                if (ayMasked == 0xC000 || ayMasked == 0x8000)
                {
                    ay.Write(port, value);
                }
            }

            SpectrumInterface1Device? interface1 = _interface1;
            if (interface1 != null && interface1.HandlesPort(port))
            {
                interface1.SetBusTstate(applyAt);
                interface1.Write(port, value);
            }

            for (int i = 0; i < _fallbackDevices.Count; i++)
            {
                IPortDevice device = _fallbackDevices[i];
                if (device.HandlesPort(port))
                {
                    device.Write(port, value);
                }
            }
        }
        private readonly struct PendingWrite(ulong applyAt, ushort port, byte value)
        {
            public ulong ApplyAt { get; } = applyAt;
            public ushort Port { get; } = port;
            public byte Value { get; } = value;
        }
    }
}
