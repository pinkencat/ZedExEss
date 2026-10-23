using System.Diagnostics;
using System.Globalization;
using System.Text;
using ZedExEss.Spectrum.Abstractions;
using ZedExEss.Spectrum.Audio;

namespace ZedExEss.Diagnostics;

public sealed class FastForwardAudioVerificationOptions
{
    public string? OutputPath { get; init; }
}

/// <summary>Checks fast-forward rate and pitch retention without a desktop audio backend.</summary>
public static class FastForwardAudioVerificationRunner
{
    private const int SampleRate = 48_000;
    private const double TestFrequency = 440.0;

    public static int Run(FastForwardAudioVerificationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string outputPath = Path.GetFullPath(options.OutputPath ?? "fast-forward-audio-verification.log");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

        using var writer = new StreamWriter(outputPath, false, Encoding.UTF8) { AutoFlush = true };
        int failures = 0;
        writer.WriteLine("Fast-forward audio verification");
        writer.WriteLine();

        Check("Reject speeds below 2x", () => VerifyRejectedSpeed(1), ref failures);
        Check("Reject speeds above 10x", () => VerifyRejectedSpeed(11), ref failures);
        foreach (int speed in new[] { 2, 4, 7, 10 })
        {
            Check($"{speed}x advances source at requested rate and retains pitch", () => VerifySpeed(speed, writer), ref failures);
        }

        writer.WriteLine();
        writer.WriteLine(failures == 0 ? "Result: PASS" : $"Result: FAIL ({failures} failed checks)");
        return failures == 0 ? 0 : 1;

        void Check(string name, Action check, ref int failureCount)
        {
            try
            {
                check();
                writer.WriteLine($"PASS {name}");
            }
            catch (Exception ex)
            {
                failureCount++;
                writer.WriteLine($"FAIL {name}: {ex.Message}");
                Debug.WriteLine(ex);
            }
        }
    }

    private static void VerifyRejectedSpeed(int speed)
    {
        try
        {
            _ = new TimeStretchAudioSource(new SineSource(), SampleRate, speed);
            throw new InvalidOperationException($"Speed {speed}x was accepted.");
        }
        catch (ArgumentOutOfRangeException)
        {
            // Expected.
        }
    }

    private static void VerifySpeed(int speed, TextWriter writer)
    {
        var source = new SineSource();
        var stretched = new TimeStretchAudioSource(source, SampleRate, speed);
        var output = new short[SampleRate];
        int read = stretched.ReadSamples(output, 0, output.Length);
        Require(read == output.Length, "The stretcher did not fill the requested host buffer.");

        double sourceRatio = source.SamplesRead / (double)output.Length;
        // WSOLA reads one look-ahead window beyond its analysis cursor.
        Require(Math.Abs(sourceRatio - speed) < 0.08,
            $"Source rate {sourceRatio.ToString("0.000", CultureInfo.InvariantCulture)}x differs from {speed}x.");

        const int settleSamples = 4_096;
        int positiveCrossings = 0;
        for (int i = settleSamples + 1; i < output.Length; i++)
        {
            if (output[i - 1] <= 0 && output[i] > 0)
            {
                positiveCrossings++;
            }
        }

        double measuredFrequency = positiveCrossings * SampleRate / (double)(output.Length - settleSamples);
        Require(Math.Abs(measuredFrequency - TestFrequency) < 55.0,
            $"Measured pitch {measuredFrequency.ToString("0.0", CultureInfo.InvariantCulture)} Hz is not close to {TestFrequency} Hz.");
        writer.WriteLine(
            $"  {speed}x: source={sourceRatio.ToString("0.000", CultureInfo.InvariantCulture)}x, " +
            $"pitch={measuredFrequency.ToString("0.0", CultureInfo.InvariantCulture)} Hz");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class SineSource : IAudioSource
    {
        private long _sample;

        public long SamplesRead => _sample;

        public int ReadSamples(short[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                double phase = 2.0 * Math.PI * TestFrequency * _sample / SampleRate;
                buffer[offset + i] = (short)(Math.Sin(phase) * 12_000.0);
                _sample++;
            }

            return count;
        }
    }
}
