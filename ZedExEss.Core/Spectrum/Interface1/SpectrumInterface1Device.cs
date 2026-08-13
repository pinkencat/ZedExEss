using ZedExEss.Spectrum.Abstractions;

namespace ZedExEss.Spectrum.Interface1;

/// <summary>
/// Interface 1 ROMCS mapper and ULA port foundation.
/// </summary>
/// <remarks>
/// The Interface 1 owns an 8 KiB ROM which is mirrored across the Spectrum's low
/// 16 KiB while ROMCS is asserted. Mapping changes are tied to opcode fetches:
/// entry fetches page the ROM before the byte is read, whereas the exit fetch at
/// 0700h reads its opcode from the Interface 1 ROM and then releases ROMCS.
///
/// This class also models the control register and eight-drive motor shift
/// register. Cartridge byte transport is intentionally added separately so ROM
/// paging and port decoding can be verified before mutable media is introduced.
/// </remarks>
public sealed class SpectrumInterface1Device : IPortDevice
{
    public const int RomSize = 8 * 1024;

    private readonly byte[] _rom;
    private bool _paged;
    private byte _control;
    private byte _networkOutput;
    private byte _motorMask;

    public SpectrumInterface1Device(ReadOnlySpan<byte> firmwareRom)
    {
        if (firmwareRom.Length != RomSize)
        {
            throw new ArgumentException(
                $"Interface 1 firmware ROM must be {RomSize} bytes.",
                nameof(firmwareRom));
        }

        _rom = firmwareRom.ToArray();
        Reset();
    }

    /// <summary>Whether the Interface 1 currently asserts ROMCS.</summary>
    public bool IsPaged => _paged;

    /// <summary>
    /// Bit mask of running Microdrive motors, with bit zero representing drive 1.
    /// </summary>
    public byte MotorMask => _motorMask;

    /// <summary>The most recent value written to the control/status port group.</summary>
    public byte Control => _control;

    /// <summary>The most recent network/serial output bit.</summary>
    public byte NetworkOutput => _networkOutput;

    public void Reset()
    {
        _paged = false;
        _control = 0;
        _networkOutput = 0;
        _motorMask = 0;
    }

    /// <summary>
    /// Applies the Interface 1 entry traps before the opcode byte is read.
    /// </summary>
    public void BeforeOpcodeFetch(ushort pc)
    {
        if (pc is 0x0008 or 0x1708)
        {
            _paged = true;
        }
    }

    /// <summary>
    /// Releases ROMCS after the exit opcode has been fetched from Interface 1 ROM.
    /// </summary>
    public void AfterOpcodeFetch(ushort pc)
    {
        if (pc == 0x0700)
        {
            _paged = false;
        }
    }

    public byte ReadMemory(ushort address)
    {
        if (!_paged || address >= 0x4000)
        {
            return 0xFF;
        }

        // The same physical 8 KiB ROM responds in both halves of 0000h-3FFFh.
        return _rom[address & 0x1FFF];
    }

    public bool HandlesPort(ushort port)
    {
        // Interface 1 only decodes address lines A3 and A4. Consequently each
        // nominal port has many aliases throughout the 16-bit I/O address space.
        return (port & 0x0018) != 0x0018;
    }

    public byte Read(ushort port)
    {
        return (byte)((port & 0x0018) switch
        {
            0x0000 => 0xFF, // Microdrive data bus: no cartridge selected yet.
            0x0008 => 0xE7, // No drive: BSY and DTR low; GAP/SYNC/WPR inactive.
            0x0010 => 0x7E, // Disconnected serial and network input lines are low.
            _ => 0xFF
        });
    }

    public void Write(ushort port, byte value)
    {
        switch (port & 0x0018)
        {
            case 0x0000:
                // Cartridge data writes are implemented with the rotating media stream.
                break;

            case 0x0008:
                WriteControl(value);
                break;

            case 0x0010:
                _networkOutput = (byte)(value & 0x01);
                break;
        }
    }

    public bool IsMotorRunning(int driveNumber)
    {
        if (driveNumber is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(driveNumber));
        }

        return (_motorMask & (1 << (driveNumber - 1))) != 0;
    }

    private void WriteControl(byte value)
    {
        bool oldClockHigh = (_control & 0x02) != 0;
        bool newClockHigh = (value & 0x02) != 0;
        if (oldClockHigh && !newClockHigh)
        {
            // A falling clock edge moves the previous motor state down the daisy
            // chain. DATA is active-low for drive 1.
            int driveOne = (value & 0x01) == 0 ? 1 : 0;
            _motorMask = (byte)(((_motorMask << 1) | driveOne) & 0xFF);
        }

        _control = value;
    }
}
