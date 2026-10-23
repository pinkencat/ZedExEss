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
        /// <summary>
        /// True for clones with a switchable 7 MHz CPU clock.
        /// </summary>
        /// <remarks>
        /// Turbo needs the ULA to be independent of the CPU's T-state grid. Sinclair
        /// models tie contention and the floating bus to that grid, so a faster CPU
        /// there would walk both at the wrong rate; these clones have neither.
        /// </remarks>
        public static bool SupportsTurboClock(SpectrumModel model)
        {
            return model == SpectrumModel.Scorpion256;
        }

        /// <summary>
        /// The Multiface variant built for this machine, or None where there is not one.
        /// </summary>
        /// <remarks>
        /// Each variant decodes different ports and expects its own machine's ROM, so a
        /// model accepts exactly one of them. The clones are excluded: they have their
        /// own built-in facilities and were never a Multiface target.
        /// </remarks>
        public static Multiface.SpectrumMultifaceModel SupportedMultiface(SpectrumModel model)
        {
            return model switch
            {
                SpectrumModel.Spectrum16K or SpectrumModel.Spectrum48K =>
                    Multiface.SpectrumMultifaceModel.One,
                SpectrumModel.Spectrum128K or SpectrumModel.SpectrumPlus2 =>
                    Multiface.SpectrumMultifaceModel.OneTwentyEight,
                SpectrumModel.SpectrumPlus2A or SpectrumModel.SpectrumPlus3 =>
                    Multiface.SpectrumMultifaceModel.Three,
                _ => Multiface.SpectrumMultifaceModel.None
            };
        }

        /// <summary>The ROM file name for one Multiface variant.</summary>
        public static string GetMultifaceRomFileName(Multiface.SpectrumMultifaceModel model)
        {
            return model switch
            {
                Multiface.SpectrumMultifaceModel.One => "mf1.rom",
                Multiface.SpectrumMultifaceModel.OneTwentyEight => "mf128.rom",
                Multiface.SpectrumMultifaceModel.Three => "mf3.rom",
                _ => string.Empty
            };
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
        /// Returns whether machine glue logic delays opcode M1 cycles onto even
        /// absolute T-state boundaries.
        /// </summary>
        /// <remarks>
        /// This Scorpion-specific rule is separate from ULA contention. 
        /// </remarks>
        public static bool AlignsM1ToEvenTstates(SpectrumModel model)
        {
            return model == SpectrumModel.Scorpion256;
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
