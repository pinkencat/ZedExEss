using System;
using ZedExEss.Spectrum.Abstractions;

namespace ZedExEss.Spectrum.Audio.GeneralSound
{
    /// <summary>Host-side view of a General Sound card: two ports and a handshake.</summary>
    /// <remarks>
    /// The card's own Z80 does the work; this only carries bytes between the two
    /// machines. Both ports are decoded on the low byte alone, which is how software
    /// addresses them.
    /// </remarks>
    public sealed class GeneralSoundDevice(GeneralSoundCard card) : IPortDevice
    {
        private readonly GeneralSoundCard _card =
            card ?? throw new ArgumentNullException(nameof(card));

        public GeneralSoundCard Card => _card;

        public bool HandlesPort(ushort port)
        {
            byte low = (byte)port;
            return low is GeneralSoundHostInterface.CommandPort
                or GeneralSoundHostInterface.DataPort;
        }

        public byte Read(ushort port) => (byte)port switch
        {
            GeneralSoundHostInterface.CommandPort => _card.Host.ReadStatus(),
            GeneralSoundHostInterface.DataPort => _card.Host.ReadData(),
            _ => 0xFF
        };

        public void Write(ushort port, byte value)
        {
            switch ((byte)port)
            {
                case GeneralSoundHostInterface.CommandPort:
                    _card.Host.WriteCommand(value);
                    break;
                case GeneralSoundHostInterface.DataPort:
                    _card.Host.WriteData(value);
                    break;
            }
        }
    }
}
