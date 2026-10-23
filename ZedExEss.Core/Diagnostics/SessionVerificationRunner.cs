using System.Diagnostics;
using System.Globalization;
using System.Text;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.Disk.Beta;
using ZedExEss.Spectrum.Disk.Plus3;
using ZedExEss.Spectrum.Memory;

namespace ZedExEss.Diagnostics;

/// <summary>Output settings for portable machine-session verification.</summary>
public sealed class SessionVerificationOptions
{
    public string? OutputPath { get; init; }
}

/// <summary>
/// Verifies that portable tape and disk state survives machine replacement without relying on a
/// desktop host. This protects the ownership boundary used by both WPF and future Avalonia hosts.
/// </summary>
public static class SessionVerificationRunner
{
    public static int Run(SessionVerificationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        string outputPath = Path.GetFullPath(options.OutputPath ?? "session-verification.log");
        string? outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        string temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ZedExEss-session-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);

        using var writer = new StreamWriter(outputPath, false, Encoding.UTF8) { AutoFlush = true };
        int failed = 0;

        try
        {
            writer.WriteLine("Portable session verification");
            writer.WriteLine($"Output: {outputPath}");
            writer.WriteLine();

            Check("Tape attachment and position survive model replacement", VerifyTapeReplacement, ref failed);
            Check("Tape ejection clears scheduler and session state", VerifyTapeEjection, ref failed);
            Check("+3 and TR-DOS media state is independent per drive", VerifyDiskState, ref failed);
            Check("Silent realtime runner advances and presents frames", VerifyRealtimeFrameRunner, ref failed);

            writer.WriteLine();
            writer.WriteLine(failed == 0
                ? "Result: PASS"
                : $"Result: FAIL ({failed.ToString(CultureInfo.InvariantCulture)} failed checks)");
            return failed == 0 ? 0 : 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            writer.WriteLine($"Error: {ex.Message}");
            Debug.WriteLine(ex.ToString());
            return 3;
        }
        finally
        {
            try
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
            catch (IOException)
            {
                // A failed cleanup must not obscure the verification result.
            }
            catch (UnauthorizedAccessException)
            {
                // A failed cleanup must not obscure the verification result.
            }
        }

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
            }
        }

        void VerifyTapeReplacement()
        {
            string tapePath = Path.Combine(temporaryDirectory, "session.tap");
            File.WriteAllBytes(tapePath, [0x03, 0x00, 0x00, 0x5A, 0x5A]);

            var session = new SpectrumSessionController();
            SpectrumMachine firstMachine = CreateMachine(SpectrumModel.Spectrum48K);
            session.ReplaceMachine(firstMachine, preserveTape: false);
            var firstLoader = session.LoadTape(tapePath);
            firstLoader.JumpToBlockPulse(0, 10, play: true);
            SpectrumTapeSessionState expected = session.CaptureTapeState()
                ?? throw new InvalidOperationException("Attached tape did not produce session state.");

            SpectrumMachine replacement = CreateMachine(SpectrumModel.Spectrum128K);
            session.ReplaceMachine(replacement, preserveTape: true);
            SpectrumTapeSessionState actual = session.CaptureTapeState()
                ?? throw new InvalidOperationException("Tape was lost during machine replacement.");

            Require(ReferenceEquals(session.Machine, replacement), "Replacement machine was not installed.");
            Require(!ReferenceEquals(firstLoader, session.Tape), "Tape loader was not rebuilt for the new EAR device.");
            Require(ReferenceEquals(replacement.Emulator.TapePlayback, session.Tape), "Scheduler tape attachment is inconsistent.");
            Require(actual.Path == expected.Path, "Tape path changed during replacement.");
            Require(actual.BlockIndex == expected.BlockIndex, "Tape block changed during replacement.");
            Require(actual.PulseOffset == expected.PulseOffset, "Tape pulse position changed during replacement.");
            Require(actual.WasPlaying == expected.WasPlaying, "Tape playback state changed during replacement.");
        }

        void VerifyTapeEjection()
        {
            string tapePath = Path.Combine(temporaryDirectory, "eject.tap");
            File.WriteAllBytes(tapePath, [0x02, 0x00, 0x00, 0x00]);

            var session = new SpectrumSessionController();
            SpectrumMachine machine = CreateMachine(SpectrumModel.Spectrum48K);
            session.ReplaceMachine(machine, preserveTape: false);
            session.LoadTape(tapePath);
            session.EjectTape();

            Require(session.Tape == null && session.TapePath == null, "Session retained an ejected tape.");
            Require(machine.Emulator.TapePlayback == null, "Scheduler retained an ejected tape.");
        }

        void VerifyDiskState()
        {
            string plus3Path = Path.Combine(temporaryDirectory, "drive-a.dsk");
            string trdPath = Path.Combine(temporaryDirectory, "drive-b.trd");
            Plus3DiskImage.CreateBlankPlus3DataDisk(plus3Path);
            File.WriteAllBytes(trdPath, new byte[80 * 2 * TrdDiskImage.SectorsPerTrack * TrdDiskImage.SectorSize]);

            var session = new SpectrumSessionController();
            Plus3DiskImage plus3 = session.Disks.LoadPlus3(0, plus3Path);
            TrdDiskImage trd = session.Disks.LoadTrd(1, trdPath);

            Require(ReferenceEquals(session.Disks.GetPlus3Image(0), plus3), "+3 drive A image was not retained.");
            Require(session.Disks.GetPlus3Path(0) == plus3Path, "+3 drive A path was not retained.");
            Require(session.Disks.GetPlus3Image(1) == null, "+3 drive B should be empty.");
            Require(ReferenceEquals(session.Disks.GetTrdImage(1), trd), "TR-DOS drive B image was not retained.");
            Require(session.Disks.GetTrdPath(1) == trdPath, "TR-DOS drive B path was not retained.");

            session.Disks.EjectPlus3(0);
            session.Disks.EjectTrd(1);
            Require(session.Disks.GetPlus3Image(0) == null && session.Disks.GetPlus3Path(0) == null,
                "Ejecting +3 media did not clear its source path.");
            Require(session.Disks.GetTrdImage(1) == null && session.Disks.GetTrdPath(1) == null,
                "Ejecting TR-DOS media did not clear its source path.");
        }

        void VerifyRealtimeFrameRunner()
        {
            SpectrumMachine machine = CreateMachine(SpectrumModel.Spectrum48K);
            using var completed = new ManualResetEventSlim(initialState: false);
            int frameCount = 0;
            machine.Emulator.FrameCompleted += () =>
            {
                if (Interlocked.Increment(ref frameCount) >= 3)
                {
                    completed.Set();
                }
            };

            var stopwatch = Stopwatch.StartNew();
            using var runner = new RealtimeFrameRunner(machine);
            Require(completed.Wait(TimeSpan.FromSeconds(2)), "Realtime runner did not produce three frames.");
            stopwatch.Stop();

            Require(runner.Failure == null, $"Realtime runner faulted: {runner.Failure}");
            Require(machine.Cpu.Cyc > 0, "Realtime runner did not advance the CPU.");
            Require(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(15),
                "Realtime runner appears to be unthrottled.");
        }
    }

    private static SpectrumMachine CreateMachine(SpectrumModel model)
    {
        return SpectrumMachineFactory.Create(new SpectrumMachineOptions
        {
            Model = model,
            Roms = RomSet.CreateBlank(SpectrumModelTraits.RomBankCount(model))
        });
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
