using System.Runtime.InteropServices;
using System.Threading;
using SDL3;
using ZedExEss.Hosting;
using ZedExEss.Spectrum.Abstractions;

namespace ZedExEss.AvaloniaHost;

/// <summary>
/// Low-latency SDL3 PCM output whose queue demand advances the emulated machine.
/// </summary>
/// <remarks>
/// SDL copies each submitted block into its audio stream. The producer maintains a deliberately
/// small queue and asks the emulator for another block only as the device consumes it, making the
/// physical audio clock the realtime master without doing emulator work in an SDL callback.
/// </remarks>
internal sealed class SdlAudioOutput : IAudioOutput
{
    private readonly IAudioSource _source;
    private readonly ManualResetEventSlim _stop = new(initialState: false);
    private readonly Thread _producerThread;
    private readonly short[] _sampleBuffer;
    private readonly int _targetQueuedBytes;
    private IntPtr _stream;
    private volatile bool _running;
    private bool _disposed;

    public SdlAudioOutput(IAudioSource source, int sampleRate, int bufferSamples, int bufferCount)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSamples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferCount);

        _sampleBuffer = new short[bufferSamples];
        _targetQueuedBytes = checked(bufferSamples * bufferCount * sizeof(short));
        SdlAudioRuntime.Acquire();

        try
        {
            var specification = new SDL.AudioSpec
            {
                // SDL_AUDIO_S16LE (0x8010). All currently packaged desktop RIDs are
                // little-endian; spelling this constant explicitly also avoids binding releases
                // that expose only the canonical SDL macro aliases.
                Format = (SDL.AudioFormat)0x8010,
                Channels = 1,
                Freq = sampleRate
            };
            _stream = SDL.OpenAudioDeviceStream(
                SDL.AudioDeviceDefaultPlayback,
                in specification,
                null,
                IntPtr.Zero);
            if (_stream == IntPtr.Zero)
            {
                throw CreateSdlException("SDL_OpenAudioDeviceStream");
            }

            _running = true;
            _producerThread = new Thread(ProducerLoop)
            {
                IsBackground = true,
                Name = "SdlAudioProducer",
                Priority = ThreadPriority.AboveNormal
            };
            _producerThread.Start();
        }
        catch
        {
            if (_stream != IntPtr.Zero)
            {
                SDL.DestroyAudioStream(_stream);
                _stream = IntPtr.Zero;
            }

            SdlAudioRuntime.Release();
            _stop.Dispose();
            throw;
        }
    }

    public bool IsRunning => _running && _producerThread.IsAlive;
    public Exception? Failure { get; private set; }
    public event Action<Exception>? Faulted;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _running = false;
        _stop.Set();
        if (Thread.CurrentThread != _producerThread)
        {
            _producerThread.Join();
        }

        if (_stream != IntPtr.Zero)
        {
            SDL.DestroyAudioStream(_stream);
            _stream = IntPtr.Zero;
        }

        SdlAudioRuntime.Release();
        _stop.Dispose();
    }

    private void ProducerLoop()
    {
        try
        {
            // Prime the short queue before unpausing the device. This prevents an immediate
            // underrun without adding the long latency of the old six-buffer WinMM arrangement.
            while (_running)
            {
                int queued = SDL.GetAudioStreamQueued(_stream);
                if (queued < 0)
                {
                    throw CreateSdlException("SDL_GetAudioStreamQueued");
                }

                if (queued >= _targetQueuedBytes)
                {
                    break;
                }

                SubmitOneBuffer();
            }

            if (_running && !SDL.ResumeAudioStreamDevice(_stream))
            {
                throw CreateSdlException("SDL_ResumeAudioStreamDevice");
            }

            int bufferBytes = _sampleBuffer.Length * sizeof(short);
            while (_running)
            {
                int queued = SDL.GetAudioStreamQueued(_stream);
                if (queued < 0)
                {
                    throw CreateSdlException("SDL_GetAudioStreamQueued");
                }

                if (queued <= _targetQueuedBytes - bufferBytes)
                {
                    SubmitOneBuffer();
                }
                else
                {
                    _stop.Wait(TimeSpan.FromMilliseconds(1));
                }
            }
        }
        catch (Exception ex)
        {
            if (_running)
            {
                Failure = ex;
                Faulted?.Invoke(ex);
            }
        }
        finally
        {
            _running = false;
        }
    }

    private void SubmitOneBuffer()
    {
        int samples = _source.ReadSamples(_sampleBuffer, 0, _sampleBuffer.Length);
        if (samples <= 0)
        {
            return;
        }

        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(_sampleBuffer.AsSpan(0, samples));
        if (!SDL.PutAudioStreamData(_stream, bytes, bytes.Length))
        {
            throw CreateSdlException("SDL_PutAudioStreamData");
        }
    }

    private static InvalidOperationException CreateSdlException(string operation)
    {
        return new InvalidOperationException($"{operation} failed: {SDL.GetError()}");
    }

    /// <summary>Reference-counts SDL's process-wide audio subsystem across output replacement.</summary>
    private static class SdlAudioRuntime
    {
        private static readonly object Sync = new();
        private static int _users;

        public static void Acquire()
        {
            lock (Sync)
            {
                if (_users == 0 && !SDL.InitSubSystem(SDL.InitFlags.Audio))
                {
                    throw CreateSdlException("SDL_InitSubSystem(SDL_INIT_AUDIO)");
                }

                _users++;
            }
        }

        public static void Release()
        {
            lock (Sync)
            {
                if (_users <= 0)
                {
                    return;
                }

                _users--;
                if (_users == 0)
                {
                    SDL.QuitSubSystem(SDL.InitFlags.Audio);
                }
            }
        }
    }
}
