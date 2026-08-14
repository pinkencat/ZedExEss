using System;using ZedExEss.Spectrum.Ports;

namespace ZedExEss.Spectrum.Core
{
    /// <summary>
    /// Central capability table for model-specific clocks, ROM/RAM counts, paging and optional hardware.
    /// </summary>
    public static class SpectrumModelTraits
    {
        public static int CpuClockHz(SpectrumModel model)
        {
            return model switch
            {
                SpectrumModel.Spectrum16K => 3500000,
                SpectrumModel.Spectrum48K => 3500000,
                SpectrumModel.Spectrum128K => 3546900,
                SpectrumModel.SpectrumPlus2 => 3546900,
                SpectrumModel.SpectrumPlus2A => 3546900,
                SpectrumModel.SpectrumPlus3 => 3546900,
                SpectrumModel.Pentagon128 => 3584000,
                SpectrumModel.Scorpion256 => 3500000,
                _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Unsupported Spectrum model.")
            };
        }
        public static int RomBankCount(SpectrumModel model)
        {
            return model switch
            {
                SpectrumModel.Spectrum16K => 1,
                SpectrumModel.Spectrum48K => 1,
                SpectrumModel.Spectrum128K => 2,
                SpectrumModel.SpectrumPlus2 => 2,
                SpectrumModel.SpectrumPlus2A => 4,
                SpectrumModel.SpectrumPlus3 => 4,
                SpectrumModel.Pentagon128 => 2,
                SpectrumModel.Scorpion256 => 4,
                _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Unsupported Spectrum model.")
            };
        }
        public static int RamBankCount(SpectrumModel model)
        {
            return model switch
            {
                SpectrumModel.Spectrum16K => 1,
                SpectrumModel.Spectrum48K => 3,
                SpectrumModel.Spectrum128K => 8,
                SpectrumModel.SpectrumPlus2 => 8,
                SpectrumModel.SpectrumPlus2A => 8,
                SpectrumModel.SpectrumPlus3 => 8,
                SpectrumModel.Pentagon128 => 8,
                SpectrumModel.Scorpion256 => 16,
                _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Unsupported Spectrum model.")
            };
        }
        public static bool HasAy(SpectrumModel model)
        {
            return model switch
            {
                SpectrumModel.Spectrum128K => true,
                SpectrumModel.SpectrumPlus2 => true,
                SpectrumModel.SpectrumPlus2A => true,
                SpectrumModel.SpectrumPlus3 => true,
                SpectrumModel.Pentagon128 => true,
                SpectrumModel.Scorpion256 => true,
                _ => false
            };
        }
        public static bool SupportsPaging(SpectrumModel model)
        {
            return Supports128Paging(model) || SupportsPlus3Paging(model);
        }
        public static bool Supports128Paging(SpectrumModel model)
        {
            return model == SpectrumModel.Spectrum128K
                || model == SpectrumModel.SpectrumPlus2
                || model == SpectrumModel.Pentagon128
                || model == SpectrumModel.Scorpion256;
        }
        public static bool SupportsPlus3Paging(SpectrumModel model)
        {
            return model == SpectrumModel.SpectrumPlus2A
                || model == SpectrumModel.SpectrumPlus3;
        }
        public static bool SupportsSecondaryPagingPort(SpectrumModel model)
        {
            return SupportsPlus3Paging(model) || model == SpectrumModel.Scorpion256;
        }
        public static SpectrumPagingPortMode PagingPortMode(SpectrumModel model)
        {
            return model switch
            {
                SpectrumModel.SpectrumPlus2A => SpectrumPagingPortMode.Plus3,
                SpectrumModel.SpectrumPlus3 => SpectrumPagingPortMode.Plus3,
                SpectrumModel.Scorpion256 => SpectrumPagingPortMode.Scorpion,
                _ => SpectrumPagingPortMode.Standard128
            };
        }
        public static bool HasPlus3Disk(SpectrumModel model)
        {
            return model == SpectrumModel.SpectrumPlus3;
        }
        public static bool HasBeta128Disk(SpectrumModel model)
        {
            return model == SpectrumModel.Pentagon128
                || model == SpectrumModel.Scorpion256;
        }
        public static bool HasPagingWritebackOnRead(SpectrumModel model)
        {
            return model == SpectrumModel.Spectrum128K
                || model == SpectrumModel.SpectrumPlus2;
        }
        public static bool HasFloatingBus(SpectrumModel model)
        {
            return model != SpectrumModel.SpectrumPlus2A
                && model != SpectrumModel.SpectrumPlus3
                && model != SpectrumModel.Pentagon128
                && model != SpectrumModel.Scorpion256;
        }
        public static bool HasUlaPortContention(SpectrumModel model)
        {
            return model == SpectrumModel.Spectrum16K
                || model == SpectrumModel.Spectrum48K
                || model == SpectrumModel.Spectrum128K
                || model == SpectrumModel.SpectrumPlus2;
        }
        /// <summary>
        /// Returns whether the machine decodes all eight low address bits for its ULA port.
        /// </summary>
        /// <remarks>
        /// Sinclair machines decode only A0, exposing the ULA on every even port. Pentagon
        /// and Scorpion clones use a 74LS138-style full low-byte decode and respond only when
        /// the low byte is FEh. This describes device selection, independently of contention.
        /// </remarks>
        public static bool HasFullyDecodedUlaPort(SpectrumModel model)
        {
            return model == SpectrumModel.Pentagon128
                || model == SpectrumModel.Scorpion256;
        }
        public static bool IsContendedRamBank(SpectrumModel model, int bankIndex)
        {
            return model switch
            {
                SpectrumModel.Spectrum16K => bankIndex == 0,
                SpectrumModel.Spectrum48K => bankIndex == 0,
                SpectrumModel.Spectrum128K => (bankIndex & 0x01) != 0,
                SpectrumModel.SpectrumPlus2 => (bankIndex & 0x01) != 0,
                SpectrumModel.SpectrumPlus2A => bankIndex >= 4,
                SpectrumModel.SpectrumPlus3 => bankIndex >= 4,
                SpectrumModel.Pentagon128 => false,
                SpectrumModel.Scorpion256 => false,
                _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Unsupported Spectrum model.")
            };
        }

    }
}
