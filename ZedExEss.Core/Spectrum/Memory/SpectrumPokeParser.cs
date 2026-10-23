using System.Globalization;

namespace ZedExEss.Spectrum.Memory;

/// <summary>One validated contiguous memory patch entered through the poke tool.</summary>
public readonly record struct SpectrumPokeEntry(ushort Address, byte Value, int Count);

/// <summary>
/// Parses the compact address/value syntax shared by the desktop frontends.
/// Keeping this outside either UI prevents the two hosts from accepting subtly
/// different poke files.
/// </summary>
public static class SpectrumPokeParser
{
    public static bool TryParse(
        string? text,
        out IReadOnlyList<SpectrumPokeEntry> pokes,
        out string error)
    {
        var parsed = new List<SpectrumPokeEntry>();
        pokes = parsed;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "No poke entries were provided.";
            return false;
        }

        string[] lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            string line = StripComment(lines[lineIndex]).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            string cleaned = line.Replace(',', ' ').Replace('=', ' ').Replace(':', ' ');
            string[] parts = cleaned.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            int firstOperand = parts.Length > 0
                && string.Equals(parts[0], "poke", StringComparison.OrdinalIgnoreCase)
                    ? 1
                    : 0;
            int operandCount = parts.Length - firstOperand;
            if (operandCount is < 2 or > 3)
            {
                error = $"Invalid poke format on line {lineIndex + 1}. Use: address value [count].";
                return false;
            }

            if (!TryParseNumber(parts[firstOperand], 0xFFFF, out int address))
            {
                error = $"Invalid address on line {lineIndex + 1}.";
                return false;
            }

            if (!TryParseNumber(parts[firstOperand + 1], 0xFF, out int value))
            {
                error = $"Invalid value on line {lineIndex + 1}.";
                return false;
            }

            int count = 1;
            if (operandCount == 3
                && (!TryParseNumber(parts[firstOperand + 2], 0xFFFF, out count) || count <= 0))
            {
                error = $"Invalid count on line {lineIndex + 1}.";
                return false;
            }

            if (address + count - 1 > 0xFFFF)
            {
                error = $"Poke range overruns memory on line {lineIndex + 1}.";
                return false;
            }

            parsed.Add(new SpectrumPokeEntry((ushort)address, (byte)value, count));
        }

        if (parsed.Count != 0)
        {
            return true;
        }

        error = "No valid pokes were found.";
        return false;
    }

    private static string StripComment(string line)
    {
        int semicolon = line.IndexOf(';');
        int slash = line.IndexOf("//", StringComparison.Ordinal);
        int end = semicolon < 0
            ? slash
            : slash < 0 ? semicolon : Math.Min(semicolon, slash);
        return end < 0 ? line : line[..end];
    }

    private static bool TryParseNumber(string token, int maximum, out int value)
    {
        value = 0;
        string text = token.Trim();
        NumberStyles style = NumberStyles.Integer;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
            style = NumberStyles.HexNumber;
        }
        else if (text.StartsWith('$') || text.StartsWith('#'))
        {
            text = text[1..];
            style = NumberStyles.HexNumber;
        }

        return text.Length != 0
            && int.TryParse(text, style, CultureInfo.InvariantCulture, out value)
            && value >= 0
            && value <= maximum;
    }
}
