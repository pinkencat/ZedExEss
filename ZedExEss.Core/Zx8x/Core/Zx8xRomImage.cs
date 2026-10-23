namespace ZedExEss.Zx8x.Core;

/// <summary>Immutable, size-validated ZX80 or ZX81 firmware image.</summary>
public sealed class Zx8xRomImage
{
    private readonly byte[] _bytes;

    private Zx8xRomImage(Zx8xRomDescriptor descriptor, byte[] bytes)
    {
        Descriptor = descriptor;
        _bytes = bytes;
    }

    public Zx8xRomDescriptor Descriptor { get; }
    public int Length => _bytes.Length;
    public ReadOnlyMemory<byte> Bytes => _bytes;

    public byte ReadMirrored(ushort address)
    {
        return _bytes[address % _bytes.Length];
    }

    public static Zx8xRomImage Load(string path, Zx8xRomDescriptor descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Load(File.ReadAllBytes(path), descriptor);
    }

    public static Zx8xRomImage Load(ReadOnlySpan<byte> bytes, Zx8xRomDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (bytes.Length != descriptor.SizeBytes)
        {
            throw new InvalidDataException(
                $"ROM {descriptor.FileName} is {bytes.Length} bytes; expected {descriptor.SizeBytes}.");
        }

        return new Zx8xRomImage(descriptor, bytes.ToArray());
    }
}
