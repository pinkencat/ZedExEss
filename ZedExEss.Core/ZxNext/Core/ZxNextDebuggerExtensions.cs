using System;
using System.Collections.Generic;
using ZedExEss.Spectrum.Debugging;
using ZedExEss.ZxNext.Memory;

namespace ZedExEss.ZxNext.Core;

/// <summary>Next-specific debugger views: the eight MMU slots and the NextReg file.</summary>
/// <remarks>
/// Neither is reachable through the shared debugger interfaces. The MMU table is
/// per-slot rather than per-address, and NextReg is a register file the Z80 only
/// reaches through a select/data port pair, so a memory or port view cannot show it.
/// </remarks>
public sealed class ZxNextDebuggerExtensions(ZxNextMachine machine) : IMachineDebuggerExtensions
{
    private readonly ZxNextMachine _machine =
        machine ?? throw new ArgumentNullException(nameof(machine));

    /// <summary>
    /// The NextReg numbers worth showing, with their names. Deliberately a curated list
    /// rather than the whole $00-$FF space: most of that space is unassigned, and a
    /// reader scanning for the paging or video state should not have to find it among
    /// two hundred blank rows.
    /// </summary>
    private static readonly (byte Number, string Name)[] KnownRegisters =
    [
        (0x00, "Machine ID"),
        (0x01, "Core version"),
        (0x02, "Reset"),
        (0x03, "Machine type / timing"),
        (0x05, "Peripheral 1"),
        (0x06, "Peripheral 2"),
        (0x07, "CPU speed"),
        (0x08, "Peripheral 3"),
        (0x09, "Peripheral 4"),
        (0x0A, "Peripheral 5"),
        (0x0E, "Core version (sub)"),
        (0x10, "Anti-brick"),
        (0x11, "Video timing"),
        (0x12, "Layer 2 active bank"),
        (0x13, "Layer 2 shadow bank"),
        (0x14, "Global transparency"),
        (0x15, "Sprite and layers"),
        (0x16, "Layer 2 scroll X"),
        (0x17, "Layer 2 scroll Y"),
        (0x18, "Layer 2 clip window"),
        (0x19, "Sprite clip window"),
        (0x1A, "ULA clip window"),
        (0x1B, "Tilemap clip window"),
        (0x1C, "Clip window control"),
        (0x1E, "Raster line MSB"),
        (0x1F, "Raster line LSB"),
        (0x22, "Line interrupt control"),
        (0x23, "Line interrupt value"),
        (0x26, "ULA scroll X"),
        (0x27, "ULA scroll Y"),
        (0x28, "Stored palette value"),
        (0x2F, "Tilemap scroll X MSB"),
        (0x30, "Tilemap scroll X LSB"),
        (0x31, "Tilemap scroll Y"),
        (0x32, "LoRes scroll X"),
        (0x33, "LoRes scroll Y"),
        (0x40, "Palette index"),
        (0x41, "Palette value (8 bit)"),
        (0x42, "Palette format"),
        (0x43, "Palette control"),
        (0x44, "Palette value (9 bit)"),
        (0x4A, "Fallback colour"),
        (0x4B, "Sprite transparency index"),
        (0x4C, "Tilemap transparency index"),
        (0x50, "MMU slot 0"),
        (0x51, "MMU slot 1"),
        (0x52, "MMU slot 2"),
        (0x53, "MMU slot 3"),
        (0x54, "MMU slot 4"),
        (0x55, "MMU slot 5"),
        (0x56, "MMU slot 6"),
        (0x57, "MMU slot 7"),
        (0x60, "Copper data"),
        (0x61, "Copper address LSB"),
        (0x62, "Copper control"),
        (0x68, "ULA control"),
        (0x69, "Display control"),
        (0x6A, "LoRes control"),
        (0x6B, "Tilemap control"),
        (0x6C, "Tilemap default attribute"),
        (0x6E, "Tilemap base address"),
        (0x6F, "Tile definitions address"),
        (0x75, "Sprite attribute 0"),
        (0x7F, "User register 0")
    ];

    public string MachineName => "ZX Spectrum Next";

    public string DeviceRegisterName => "NextReg";

    public IReadOnlyList<DebuggerMemorySlot> GetMemorySlots()
    {
        var slots = new List<DebuggerMemorySlot>(ZxNextMemoryBus.MmuSlotCount);
        for (int slot = 0; slot < ZxNextMemoryBus.MmuSlotCount; slot++)
        {
            ushort start = (ushort)(slot * ZxNextPhysicalMemory.PageSize);
            ushort end = (ushort)(start + ZxNextPhysicalMemory.PageSize - 1);
            byte page = _machine.MemoryBus.GetMmuPage(slot);

            // Report what the debugger's own per-address view would say, so the table
            // and the disassembly Map column cannot disagree about an overridden slot.
            DebuggerMemoryMapping mapping = _machine.MemoryBus.GetMapping(start);
            slots.Add(new DebuggerMemorySlot(
                slot,
                start,
                end,
                page,
                mapping.DisplayName));
        }

        return slots;
    }

    public IReadOnlyList<DebuggerDeviceRegister> GetDeviceRegisters()
    {
        var registers = new List<DebuggerDeviceRegister>(KnownRegisters.Length);
        foreach ((byte number, string name) in KnownRegisters)
        {
            registers.Add(new DebuggerDeviceRegister(
                number,
                _machine.Registers.Read(number),
                name));
        }

        return registers;
    }
}
