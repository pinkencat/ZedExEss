using System;using ZedExEss.Spectrum.Core;

namespace ZedExEss.Spectrum.Audio
{
    /// <summary>
    /// Audio clock constants derived from each Spectrum model's CPU and AY clock.
    /// </summary>
    public static class SpectrumAudioTiming
    {
        public const int DefaultSampleRate = 44100;
        public static int CpuClockHz(SpectrumModel model)
        {
            return SpectrumModelTraits.CpuClockHz(model);
        }
        public static bool HasAy(SpectrumModel model)
        {
            return SpectrumModelTraits.HasAy(model);
        }
        public static int AyClockHz(SpectrumModel model)
        {
            return CpuClockHz(model) / 2;
        }
    }
}
