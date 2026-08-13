using System.Runtime.CompilerServices;
using ZedExEss.Zx8x.Core;

namespace ZedExEss.Zx8x.Input;

/// <summary>
/// Models the ZX80/ZX81 keyboard, cassette input and the I/O transitions which
/// control vertical retrace and the ZX81 NMI generator.
/// </summary>
public sealed class Zx8xIoDevice
{
    public Zx8xIoDevice(Zx8xModel model, Zx8xKeyboard? keyboard = null)
    {
        Model = model;
        Keyboard = keyboard ?? new Zx8xKeyboard();
    }

    public Zx8xModel Model { get; }
    public Zx8xKeyboard Keyboard { get; }

    /// <summary>True for the UK 50 Hz link; false selects the US 60 Hz link.</summary>
    public bool Is50Hz { get; set; } = true;

    /// <summary>Current digital cassette input presented on data bit 7.</summary>
    public bool CassetteInputHigh { get; set; }

    /// <summary>Whether ZX81 SLOW-mode horizontal blanking NMIs are enabled.</summary>
    public bool NmiGeneratorEnabled { get; private set; }

    /// <summary>Tracks the retrace latch controlled by I/O read/write cycles.</summary>
    public bool VerticalRetraceActive { get; private set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadPort(ushort port)
    {
        // The keyboard/cassette buffer is selected by A0=0, not by a fully
        // decoded FEh low byte. Odd ports therefore leave the data bus high.
        if ((port & 0x0001) != 0)
        {
            return 0xFF;
        }

        if (Model == Zx8xModel.Zx80 || !NmiGeneratorEnabled)
        {
            VerticalRetraceActive = true;
        }

        byte value = (byte)(0x20 | Keyboard.ReadRows((byte)(port >> 8)));
        if (Is50Hz)
        {
            value |= 0x40;
        }

        if (CassetteInputHigh)
        {
            value |= 0x80;
        }

        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WritePort(ushort port, byte value)
    {
        // Any I/O write releases the software sync gate. Horizontal counter
        // re-phasing is decided by the video timing device from the preceding
        // pulse length; ordinary writes must not move the ZX81 line clock.
        VerticalRetraceActive = false;
        if (Model != Zx8xModel.Zx81)
        {
            return;
        }

        switch ((byte)port)
        {
            case 0xFE:
                NmiGeneratorEnabled = true;
                break;
            case 0xFD:
                NmiGeneratorEnabled = false;
                break;
        }
    }

    public void Reset()
    {
        NmiGeneratorEnabled = false;
        VerticalRetraceActive = false;
        CassetteInputHigh = false;
        Keyboard.ReleaseAll();
    }
}
