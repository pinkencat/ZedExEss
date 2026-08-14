using ZedExEss.Spectrum.Core;
using ZedExEss.Z80CPU;

namespace ZedExEss.FileHandlers;

/// <summary>Programmer-visible AY state carried by the standard SZX AY block.</summary>
public sealed record SpectrumAySnapshot(byte SelectedRegister, byte[] Registers)
{
    public SpectrumAySnapshot DeepCopy() => new(SelectedRegister, Registers.ToArray());
}

/// <summary>
/// Host-neutral whole-machine state used between snapshot codecs and a running machine.
/// </summary>
public sealed class SpectrumMachineSnapshot
{
    private readonly byte[][] _ramBanks;

    public SpectrumMachineSnapshot(
        SpectrumModel model,
        Z80SnapshotState cpu,
        IReadOnlyList<byte[]> ramBanks,
        byte port7ffd,
        byte port1ffd,
        byte ulaOutput,
        int frameTstate,
        int frameCounter,
        SpectrumAySnapshot? ay,
        SpectrumInterface1Snapshot? interface1)
    {
        ArgumentNullException.ThrowIfNull(cpu);
        ArgumentNullException.ThrowIfNull(ramBanks);

        int expectedBanks = SpectrumModelTraits.RamBankCount(model);
        if (ramBanks.Count != expectedBanks)
        {
            throw new ArgumentException($"Model {model} requires {expectedBanks} RAM banks.", nameof(ramBanks));
        }

        _ramBanks = new byte[ramBanks.Count][];
        for (int bank = 0; bank < ramBanks.Count; bank++)
        {
            byte[] data = ramBanks[bank] ?? throw new ArgumentException("RAM bank cannot be null.", nameof(ramBanks));
            if (data.Length != 0x4000)
            {
                throw new ArgumentException("Every RAM bank must contain 16384 bytes.", nameof(ramBanks));
            }

            _ramBanks[bank] = data.ToArray();
        }

        int frameLength = Spectrum.Video.SpectrumUlaTiming.ForModel(model).TstatesPerFrame;
        if ((uint)frameTstate >= (uint)frameLength)
        {
            throw new ArgumentOutOfRangeException(nameof(frameTstate));
        }

        Model = model;
        Cpu = cpu;
        Port7FFD = port7ffd;
        Port1FFD = port1ffd;
        UlaOutput = ulaOutput;
        FrameTstate = frameTstate;
        FrameCounter = Math.Max(0, frameCounter);
        Ay = ay?.DeepCopy();
        Interface1 = interface1;
    }

    public SpectrumModel Model { get; }
    public Z80SnapshotState Cpu { get; }
    public byte Port7FFD { get; }
    public byte Port1FFD { get; }
    public byte UlaOutput { get; }
    public int FrameTstate { get; }
    public int FrameCounter { get; }
    public SpectrumAySnapshot? Ay { get; }
    public SpectrumInterface1Snapshot? Interface1 { get; }
    public int RamBankCount => _ramBanks.Length;

    public byte[] CopyRamBank(int bank) => _ramBanks[bank].ToArray();
    internal ReadOnlySpan<byte> GetRamBankSpan(int bank) => _ramBanks[bank];
}
