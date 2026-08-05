using System;
using ZedExEss.Spectrum.Core;

namespace ZedExEss.Spectrum.Debugging
{
    /// <summary>The bus event that can stop debugger execution.</summary>
    public enum DebuggerBreakType
    {
        Execute,
        MemoryRead,
        MemoryWrite,
        PortRead,
        PortWrite
    }
    /// <summary>Debugger-owned execution state consulted at instruction boundaries.</summary>
    public enum DebuggerRunMode
    {
        Paused,
        Running,
        StepInto
    }
    /// <summary>Physical ROM/RAM mapping behind one logical Z80 address.</summary>
    public readonly struct SpectrumMemoryMapping(
        ushort address,
        int page,
        bool isRam,
        bool isRom,
        bool isOpenBus,
        bool isReadOnly,
        bool isContended,
        int bankIndex,
        int offset,
        int romBank,
        SpectrumModel model)
    {
        public ushort Address { get; } = address;
        public int Page { get; } = page;
        public bool IsRam { get; } = isRam;
        public bool IsRom { get; } = isRom;
        public bool IsOpenBus { get; } = isOpenBus;
        public bool IsReadOnly { get; } = isReadOnly;
        public bool IsContended { get; } = isContended;
        public int BankIndex { get; } = bankIndex;
        public int Offset { get; } = offset;
        public int RomBank { get; } = romBank;
        public SpectrumModel Model { get; } = model;

        public string DisplayName
        {
            get
            {
                if (IsOpenBus)
                {
                    return $"Open bus page {Page}";
                }

                if (IsRom)
                {
                    return $"ROM {RomBank}, +{Offset:X4}";
                }

                return $"RAM {BankIndex}, +{Offset:X4}";
            }
        }
    }
    /// <summary>
    /// Logical address or masked-port breakpoint, optionally qualified by a physical RAM bank.
    /// </summary>
    public sealed class DebuggerBreakpoint
    {
        public int Id { get; set; }
        public DebuggerBreakType Type { get; set; }
        public bool Enabled { get; set; } = true;
        public ushort Address { get; set; }
        public ushort EndAddress { get; set; }
        public ushort Port { get; set; }
        public ushort PortMask { get; set; } = 0xFFFF;
        public bool OneShot { get; set; }
        public bool BankQualified { get; set; }
        public bool MatchRam { get; set; }
        public int BankIndex { get; set; } = -1;

        public bool IsMemoryType =>
            Type is DebuggerBreakType.Execute or DebuggerBreakType.MemoryRead or DebuggerBreakType.MemoryWrite;

        public string AddressText => IsMemoryType
            ? EndAddress != Address ? $"{Address:X4}-{EndAddress:X4}" : $"{Address:X4}"
            : $"{Port:X4}/{PortMask:X4}";

        public string Summary => $"{Id}: {Type} {AddressText}{(Enabled ? string.Empty : " (disabled)")}";
        public bool MatchesAddress(ushort address, SpectrumMemoryMapping mapping)
        {
            if (!Enabled || !IsMemoryType)
            {
                return false;
            }

            bool inRange = Address <= EndAddress
                ? address >= Address && address <= EndAddress
                : address >= Address || address <= EndAddress;
            if (!inRange)
            {
                return false;
            }

            if (!BankQualified)
            {
                return true;
            }

            return mapping.IsRam == MatchRam && mapping.BankIndex == BankIndex;
        }
        public bool MatchesPort(ushort port)
        {
            return Enabled && !IsMemoryType && (port & PortMask) == (Port & PortMask);
        }
    }
    /// <summary>Immutable description of the access that most recently stopped execution.</summary>
    public sealed class DebuggerBreakHit(
        DebuggerBreakType type,
        ushort address,
        ushort port,
        byte? value,
        ulong tstates,
        string reason,
        DebuggerBreakpoint? breakpoint)
    {
        public DebuggerBreakType Type { get; } = type;
        public ushort Address { get; } = address;
        public ushort Port { get; } = port;
        public byte? Value { get; } = value;
        public ulong Tstates { get; } = tstates;
        public string Reason { get; } = reason;
        public DebuggerBreakpoint? Breakpoint { get; } = breakpoint;
    }
    /// <summary>
    /// Optional CPU bus observer; implementations must keep disabled checks extremely cheap.
    /// </summary>
    /// <remarks>Access watchpoints request a stop after the instruction, never mid-cycle.</remarks>
    public interface IZ80DebugHook
    {
        bool Enabled { get; }
        bool AccessWatchpointsEnabled { get; }
        void OnMemoryRead(ushort address, byte value);
        void OnMemoryWrite(ushort address, byte value);
        void OnPortRead(ushort port, byte value);
        void OnPortWrite(ushort port, byte value);
    }
    /// <summary>Presentation model for one debugger disassembly row.</summary>
    public sealed class Z80DisassemblyLine(
        ushort address,
        byte[] bytes,
        string mnemonic,
        int length,
        bool isCurrent,
        bool hasBreakpoint,
        SpectrumMemoryMapping mapping)
    {
        public ushort Address { get; } = address;
        public byte[] Bytes { get; } = bytes;
        public string Mnemonic { get; } = mnemonic;
        public int Length { get; } = length;
        public bool IsCurrent { get; } = isCurrent;
        public bool HasBreakpoint { get; } = hasBreakpoint;
        public SpectrumMemoryMapping Mapping { get; } = mapping;
        public string AddressText => $"{Address:X4}";
        public string BytesText => BitConverter.ToString(Bytes).Replace("-", " ");
        public string EditableBytesText { get; set; } = BitConverter.ToString(bytes).Replace("-", " ");
    }
}
