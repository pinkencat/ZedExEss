using System.Collections.Generic;

namespace ZedExEss.Spectrum.Audio
{
    /// <summary>One captured source, and how a host should label and colour it.</summary>
    public sealed record AudioScopeChannel(int Index, string Name, string ColorHex);

    /// <summary>
    /// A set of channels a host shows together under one collapsible header.
    /// </summary>
    /// <param name="ExpandedByDefault">
    /// False for the TurboSound expansion chips, so a 48K or 128K title looks the way it
    /// always did and the extra six traces only claim height once something drives them.
    /// </param>
    public sealed record AudioScopeGroup(
        string Name,
        IReadOnlyList<AudioScopeChannel> Channels,
        bool ExpandedByDefault);

    /// <summary>
    /// Describes the oscilloscope channel set for one machine family, so both hosts
    /// build the same traces from one definition rather than each hard-coding four.
    /// </summary>
    public sealed class AudioScopeLayout
    {
        private const string BeeperColor = "#F5E663";
        private const string CovoxColor = "#E08A3C";
        private static readonly string[] GeneralSoundColors =
            ["#9BE04A", "#7ABF39", "#5C9E2C", "#3F7D1F"];

        // One hue per PSG channel, darkening by chip so a TurboSound title still reads
        // as three A/B/C triples rather than nine unrelated traces.
        private static readonly string[] ChannelAColors = ["#55D6BE", "#3FA592", "#2C7568"];
        private static readonly string[] ChannelBColors = ["#7AA2FF", "#5A78BF", "#3C5080"];
        private static readonly string[] ChannelCColors = ["#FF77C8", "#BF5996", "#803B64"];

        private AudioScopeLayout(
            int channelCount,
            IReadOnlyList<AudioScopeGroup> groups,
            int covoxChannelIndex,
            int firstGeneralSoundChannelIndex)
        {
            ChannelCount = channelCount;
            Groups = groups;
            CovoxChannelIndex = covoxChannelIndex;
            FirstGeneralSoundChannelIndex = firstGeneralSoundChannelIndex;
        }

        public int ChannelCount { get; }

        public IReadOnlyList<AudioScopeGroup> Groups { get; }

        /// <summary>The Covox capture index, or -1 when none is fitted.</summary>
        public int CovoxChannelIndex { get; }

        /// <summary>The first General Sound capture index, or -1 when no card is fitted.</summary>
        public int FirstGeneralSoundChannelIndex { get; }

        /// <summary>Beeper plus one AY: the 48K/128K/Pentagon channel set.</summary>
        public static AudioScopeLayout Spectrum { get; } = Create(chipCount: 1);

        /// <summary>Beeper, one AY and a fitted Covox DAC.</summary>
        public static AudioScopeLayout SpectrumWithCovox { get; } =
            Create(chipCount: 1, includeCovox: true);

        /// <summary>
        /// Returns the layout for a Spectrum with the given expansions fitted.
        /// </summary>
        /// <remarks>
        /// Cached rather than rebuilt, because the oscilloscope compares layouts by
        /// reference when deciding whether its traces need rebuilding.
        /// </remarks>
        public static AudioScopeLayout ForSpectrum(bool covox, bool generalSound) =>
            SpectrumVariants[(covox ? 1 : 0) | (generalSound ? 2 : 0)];

        private static readonly AudioScopeLayout[] SpectrumVariants =
        [
            Create(chipCount: 1),
            Create(chipCount: 1, includeCovox: true),
            Create(chipCount: 1, includeGeneralSound: true),
            Create(chipCount: 1, includeCovox: true, includeGeneralSound: true)
        ];

        /// <summary>Beeper plus three TurboSound AY chips.</summary>
        public static AudioScopeLayout ZxNext { get; } = Create(chipCount: 3);

        /// <summary>The capture index of the beeper, which is always channel zero.</summary>
        public const int BeeperChannelIndex = 0;

        /// <summary>Returns the capture index of one PSG channel.</summary>
        public static int GetPsgChannelIndex(int chip, int channel) => 1 + (chip * 3) + channel;

        private static AudioScopeLayout Create(
            int chipCount,
            bool includeCovox = false,
            bool includeGeneralSound = false)
        {
            var groups = new List<AudioScopeGroup>(chipCount + 1)
            {
                new(
                    "Beeper",
                    [new AudioScopeChannel(BeeperChannelIndex, "Beeper", BeeperColor)],
                    ExpandedByDefault: true)
            };

            for (int chip = 0; chip < chipCount; chip++)
            {
                string name = chipCount == 1 ? "AY" : $"AY {chip + 1}";
                groups.Add(new AudioScopeGroup(
                    name,
                    [
                        new AudioScopeChannel(
                            GetPsgChannelIndex(chip, 0), $"{name} A", ChannelAColors[chip]),
                        new AudioScopeChannel(
                            GetPsgChannelIndex(chip, 1), $"{name} B", ChannelBColors[chip]),
                        new AudioScopeChannel(
                            GetPsgChannelIndex(chip, 2), $"{name} C", ChannelCColors[chip])
                    ],
                    ExpandedByDefault: chip == 0));
            }

            // Indices are handed out in order, so an expansion that is not fitted
            // never renumbers the channels before it.
            int next = 1 + (chipCount * 3);
            int covoxIndex = -1;
            if (includeCovox)
            {
                covoxIndex = next++;
                groups.Add(new AudioScopeGroup(
                    "Covox",
                    [new AudioScopeChannel(covoxIndex, "Covox", CovoxColor)],
                    ExpandedByDefault: true));
            }

            int firstGeneralSound = -1;
            if (includeGeneralSound)
            {
                firstGeneralSound = next;
                var channels = new List<AudioScopeChannel>(4);
                for (int channel = 0; channel < 4; channel++)
                {
                    channels.Add(new AudioScopeChannel(
                        next++,
                        $"GS {channel + 1}",
                        GeneralSoundColors[channel]));
                }

                // Four sample channels is a lot of height for a card that is silent
                // until something drives it, so the group starts collapsed.
                groups.Add(new AudioScopeGroup("General Sound", channels, ExpandedByDefault: false));
            }

            return new AudioScopeLayout(next, groups, covoxIndex, firstGeneralSound);
        }
    }
}
