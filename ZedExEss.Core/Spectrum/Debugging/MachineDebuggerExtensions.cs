using System.Collections.Generic;

namespace ZedExEss.Spectrum.Debugging
{
    /// <summary>One entry of a machine's memory-mapping table.</summary>
    public readonly record struct DebuggerMemorySlot(
        int Index,
        ushort StartAddress,
        ushort EndAddress,
        int Page,
        string Description)
    {
        public string RangeText => $"{StartAddress:X4}-{EndAddress:X4}";
    }

    /// <summary>One device register a machine exposes for inspection.</summary>
    public readonly record struct DebuggerDeviceRegister(
        byte Number,
        byte Value,
        string Name)
    {
        public string NumberText => $"${Number:X2}";

        public string ValueText => $"${Value:X2}  %{System.Convert.ToString(Value, 2).PadLeft(8, '0')}";
    }

    /// <summary>
    /// Machine-specific debugger views surfaced alongside the shared register,
    /// disassembly and memory panels.
    /// </summary>
    /// <remarks>
    /// The shared debugger already understands paged memory through
    /// <see cref="DebuggerMemoryMapping"/>. This adds what is not per-address: the whole
    /// mapping table at a glance, and a device register file the Z80 reaches only
    /// through ports.
    /// </remarks>
    public interface IMachineDebuggerExtensions
    {
        /// <summary>Shown as the panel heading, for example "ZX Spectrum Next".</summary>
        string MachineName { get; }

        /// <summary>The label for the register file, for example "NextReg".</summary>
        string DeviceRegisterName { get; }

        /// <summary>The current logical-to-physical mapping table.</summary>
        IReadOnlyList<DebuggerMemorySlot> GetMemorySlots();

        /// <summary>The current device register values.</summary>
        IReadOnlyList<DebuggerDeviceRegister> GetDeviceRegisters();
    }
}
