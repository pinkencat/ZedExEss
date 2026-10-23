using System;
using ZedExEss.Z80CPU;

namespace ZedExEss.Spectrum.Audio.GeneralSound
{
    /// <summary>The card's own I/O space: paging, the host latches and channel volumes.</summary>
    /// <remarks>
    /// Only the low nibble of the port is decoded. Sample data does not arrive here at
    /// all; it is written into the memory-mapped DAC window. These ports set the volume
    /// each channel's samples are scaled by, and carry the host handshake.
    /// </remarks>
    public sealed class GeneralSoundPorts : IZ80PortBus
    {
        private const byte MappingPort = 0x00;
        private const byte CommandPort = 0x01;
        private const byte DataPort = 0x02;
        private const byte OutputPort = 0x03;
        private const byte StatusPort = 0x04;
        private const byte ClearCommandPort = 0x05;
        private const byte FirstVolumePort = 0x06;
        private const byte MappingFlagPort = 0x0A;
        private const byte VolumeFlagPort = 0x0B;

        /// <summary>Channel volume is six bits.</summary>
        public const int MaximumVolume = 0x3F;

        private readonly GeneralSoundMemory _memory;
        private readonly byte[] _volume = new byte[GeneralSoundCard.ChannelCount];
        private readonly byte[] _dac = [0x80, 0x80, 0x80, 0x80];

        internal GeneralSoundPorts(GeneralSoundMemory memory)
        {
            _memory = memory ?? throw new ArgumentNullException(nameof(memory));
            Host = new GeneralSoundHostInterface();
        }

        public GeneralSoundHostInterface Host { get; }

        /// <summary>DAC writes so far, which is how a test sees the mixing loop running.</summary>
        public long DacWriteCount { get; private set; }

        public byte ReadUncontended(ushort port)
        {
            switch ((byte)port & 0x0F)
            {
                case CommandPort:
                    return Host.ReadCommand();
                case DataPort:
                    return Host.TakeData();
                case OutputPort:
                    Host.SetDataPending(true);
                    return 0xFF;
                case StatusPort:
                    return Host.ReadStatus();
                case ClearCommandPort:
                    Host.AcknowledgeCommand();
                    return 0xFF;
                case MappingFlagPort:
                    // The card reports its own paging and volume state back through the
                    // shared status bits; the firmware reads them to test its own setup.
                    Host.SetDataPending((_memory.MappingValue & 0x01) == 0);
                    return 0xFF;
                case VolumeFlagPort:
                    Host.SetCommandPending((_volume[0] & 0x20) != 0);
                    return 0xFF;
                default:
                    return 0xFF;
            }
        }

        public void WriteUncontended(ushort port, byte value)
        {
            int low = (byte)port & 0x0F;
            switch (low)
            {
                case MappingPort:
                    _memory.WriteMappingValue(value);
                    return;
                case DataPort:
                    Host.SetDataPending(false);
                    return;
                case OutputPort:
                    Host.PublishData(value);
                    return;
                case CommandPort:
                    return;
                case ClearCommandPort:
                    Host.AcknowledgeCommand();
                    return;
                case MappingFlagPort:
                    Host.SetDataPending((_memory.MappingValue & 0x01) == 0);
                    return;
                case VolumeFlagPort:
                    Host.SetCommandPending((_volume[0] & 0x20) != 0);
                    return;
            }

            int volumeChannel = low - FirstVolumePort;
            if ((uint)volumeChannel < GeneralSoundCard.ChannelCount)
            {
                _volume[volumeChannel] = (byte)(value & MaximumVolume);
            }
        }

        /// <summary>The card owns its own bus and has no contention.</summary>
        public void ApplyIoContentionBeforeCycle(ushort port, int phase)
        {
        }

        /// <summary>Clocks one sample read from the DAC window into a channel.</summary>
        /// <remarks>
        /// Samples are unsigned with mid-scale as silence, and the channel's volume
        /// register scales them here rather than in the firmware.
        /// </remarks>
        internal void FeedDac(int channel, byte sample)
        {
            if ((uint)channel >= GeneralSoundCard.ChannelCount)
            {
                return;
            }

            int signed = sample - 0x80;
            _dac[channel] = (byte)(0x80 + ((signed * (_volume[channel] & MaximumVolume)) / MaximumVolume));
            DacWriteCount++;
        }

        /// <summary>The card's volume register for one channel.</summary>
        public byte GetChannelVolume(int channel) =>
            (uint)channel < GeneralSoundCard.ChannelCount ? _volume[channel] : (byte)0;

        /// <summary>
        /// Sums all four channels for a mono host mix.
        /// </summary>
        /// <remarks>
        /// The card is stereo - channels 1 and 2 left, 3 and 4 right - but the host path
        /// is mono, so all four are averaged as the hardware's mono mode does.
        /// </remarks>
        public int GetMixedOutput()
        {
            int mixed = 0;
            for (int channel = 0; channel < GeneralSoundCard.ChannelCount; channel++)
            {
                mixed += GetChannelLevel(channel);
            }

            return mixed / GeneralSoundCard.ChannelCount;
        }

        /// <summary>One channel as a host-scaled signed level.</summary>
        public short GetChannelLevel(int channel) =>
            (uint)channel < GeneralSoundCard.ChannelCount
                ? (short)((_dac[channel] - 0x80) * GeneralSoundCard.ChannelGain)
                : (short)0;

        public void Reset()
        {
            Array.Clear(_volume);
            for (int channel = 0; channel < GeneralSoundCard.ChannelCount; channel++)
            {
                _dac[channel] = 0x80;
            }

            DacWriteCount = 0;
            Host.Reset();
        }
    }
}
