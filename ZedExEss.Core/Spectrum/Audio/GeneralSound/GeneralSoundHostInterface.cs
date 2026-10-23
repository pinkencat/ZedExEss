using System;

namespace ZedExEss.Spectrum.Audio.GeneralSound
{
    /// <summary>The shared latches and status bits between the host Z80 and the card's.</summary>
    /// <remarks>
    /// One status register is visible to both sides. Bit 0 says the host has left a
    /// command the card has not taken; bit 7 says the same of a data byte. The card
    /// clears each by reading the matching latch, and the host clears the data bit by
    /// reading back the card's output.
    /// </remarks>
    public sealed class GeneralSoundHostInterface
    {
        /// <summary>Host-side command channel; reads return the status register.</summary>
        public const byte CommandPort = 0xBB;

        /// <summary>Host-side data channel; reads return the card's output byte.</summary>
        public const byte DataPort = 0xB3;

        /// <summary>Status bit 0: a command is waiting for the card.</summary>
        public const byte StatusCommandPending = 0x01;

        /// <summary>Status bit 7: a data byte is waiting.</summary>
        public const byte StatusDataPending = 0x80;

        private byte _command;
        private byte _data;
        private byte _output;
        private byte _status;

        /// <summary>Host reads $BB: the status register.</summary>
        public byte ReadStatus() => _status;

        /// <summary>Host reads $B3: the card's output byte, clearing the data bit.</summary>
        public byte ReadData()
        {
            _status &= unchecked((byte)~StatusDataPending);
            return _output;
        }

        /// <summary>Host writes $BB: a command, which also interrupts the card.</summary>
        public void WriteCommand(byte value)
        {
            _command = value;
            _status |= StatusCommandPending;
        }

        /// <summary>Host writes $B3: a data byte.</summary>
        public void WriteData(byte value)
        {
            _data = value;
            _status |= StatusDataPending;
        }

        /// <summary>Card reads its command port. This does not acknowledge it.</summary>
        public byte ReadCommand() => _command;

        /// <summary>Card reads its data port, clearing the data bit.</summary>
        public byte TakeData()
        {
            _status &= unchecked((byte)~StatusDataPending);
            return _data;
        }

        /// <summary>Card publishes a byte for the host and marks it available.</summary>
        public void PublishData(byte value)
        {
            _output = value;
            _status |= StatusDataPending;
        }

        /// <summary>Card acknowledges the command, releasing the host.</summary>
        public void AcknowledgeCommand()
        {
            _status &= unchecked((byte)~StatusCommandPending);
        }

        /// <summary>Drives the data bit directly, which some card ports do.</summary>
        public void SetDataPending(bool pending)
        {
            _status = pending
                ? (byte)(_status | StatusDataPending)
                : (byte)(_status & ~StatusDataPending);
        }

        /// <summary>Drives the command bit directly, which some card ports do.</summary>
        public void SetCommandPending(bool pending)
        {
            _status = pending
                ? (byte)(_status | StatusCommandPending)
                : (byte)(_status & ~StatusCommandPending);
        }

        public void Reset()
        {
            _command = 0;
            _data = 0;
            _output = 0;
            _status = 0;
        }
    }
}
