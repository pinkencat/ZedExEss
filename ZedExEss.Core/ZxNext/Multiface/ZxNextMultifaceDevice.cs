using ZedExEss.ZxNext.Core;
using ZedExEss.ZxNext.Memory;

namespace ZedExEss.ZxNext.Multiface;

/// <summary>
/// Multiface NMI and memory-page latches used by the Next's internal MF3.
/// </summary>
/// <remarks>
/// The pinned core asserts NMI first and pages the 8 KiB ROM plus 8 KiB RAM
/// only when the CPU fetches the NMI vector at $0066. RETN releases both the
/// NMI-active and memory-enable latches.
/// </remarks>
public sealed class ZxNextMultifaceDevice
{
    private bool _invisible = true;

    public bool Enabled { get; private set; } = true;
    public bool NmiActive { get; private set; }
    public bool IsMapped { get; private set; }

    public bool BeginNmi()
    {
        if (!Enabled || NmiActive)
        {
            return false;
        }

        NmiActive = true;
        _invisible = false;
        return true;
    }

    public void BeforeOpcodeFetch(ushort address)
    {
        if (Enabled && NmiActive && address == 0x0066)
        {
            IsMapped = true;
        }
    }

    public void CompleteNmi()
    {
        NmiActive = false;
        IsMapped = false;
    }

    /// <summary>
    /// Applies a Multiface control-port read and, for the MF128/MF+3 enable
    /// ports, returns the paging latch driven onto the data bus.
    /// </summary>
    public bool TryReadPort(
        ushort port,
        byte multifaceType,
        ZxNextPagingController paging,
        byte ulaOutputLatch,
        out byte value)
    {
        ArgumentNullException.ThrowIfNull(paging);

        byte mode = (byte)(multifaceType & 0x03);
        byte lowAddress = (byte)port;
        byte enablePort = GetEnablePort(mode);
        byte disablePort = GetDisablePort(mode);
        value = 0xFF;

        if (lowAddress == disablePort)
        {
            IsMapped = false;
            if (mode == 0)
            {
                // MF+3 also releases its asserted NMI on a disable-port read.
                NmiActive = false;
            }

            return false;
        }

        if (lowAddress != enablePort)
        {
            return false;
        }

        bool invisibleEffective = _invisible && mode != 3;
        IsMapped = !invisibleEffective;
        if (invisibleEffective || mode == 3)
        {
            // MF48 never drives a status byte; an invisible MF128/MF+3 leaves
            // the port open even though the read updates its mapping latch.
            return false;
        }

        if (mode != 0)
        {
            value = (byte)(((paging.Port7Ffd & 0x08) != 0 ? 0x80 : 0) | 0x7F);
            return true;
        }

        value = (port >> 12) switch
        {
            0x01 => paging.Port1Ffd,
            0x07 => paging.Port7Ffd,
            0x0D => paging.PortDFfd,
            0x0E => paging.PortEff7,
            _ => (byte)(ulaOutputLatch & 0x07)
        };
        return true;
    }

    /// <summary>Applies the NMI/invisibility side effects of a control-port write.</summary>
    public bool TryWritePort(ushort port, byte multifaceType)
    {
        byte mode = (byte)(multifaceType & 0x03);
        byte lowAddress = (byte)port;
        bool enable = lowAddress == GetEnablePort(mode);
        bool disable = lowAddress == GetDisablePort(mode);
        if (!enable && !disable)
        {
            return false;
        }

        NmiActive = false;
        if ((mode == 0 && enable) || (mode != 0 && disable))
        {
            _invisible = true;
        }

        return true;
    }

    /// <summary>Captures the NMI, mapping and invisibility latches.</summary>
    public ZxNextMultifaceState CaptureState() => new(Enabled, NmiActive, IsMapped, _invisible);

    /// <summary>Puts the latches back.</summary>
    public void RestoreState(ZxNextMultifaceState state)
    {
        Enabled = state.Enabled;
        NmiActive = state.NmiActive;
        IsMapped = state.IsMapped;
        _invisible = state.Invisible;
    }

    public void Reset()
    {
        CompleteNmi();
        _invisible = true;
    }

    public void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        if (!enabled)
        {
            Reset();
        }
    }

    private static byte GetEnablePort(byte mode) => mode switch
    {
        0 => 0x3F,
        3 => 0x9F,
        1 => 0xBF,
        _ => 0x9F
    };

    private static byte GetDisablePort(byte mode) => mode switch
    {
        0 => 0xBF,
        3 => 0x1F,
        1 => 0x3F,
        _ => 0x1F
    };
}
