using System.Media;
using System.Threading.Channels;
using NAudio.Wave;

namespace MicTap;

sealed class TalkCuePlayer : IDisposable
{
    readonly Channel<bool> pending = Channel.CreateBounded<bool>(new BoundedChannelOptions(4)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropOldest
    });
    readonly object enqueueLock = new();
    bool? lastCue;
    long lastCueTime;
    volatile bool disposed;

    public void Enqueue(bool isOpen)
    {
        lock (enqueueLock)
        {
            long now = Environment.TickCount64;
            // Outputs sharing a hotkey only need one cue for the same transition.
            if (lastCue == isOpen && now - lastCueTime < 25) return;
            if (!pending.Writer.TryWrite(isOpen)) return;
            if (lastCue == null) _ = Task.Run(PlayQueuedAsync);
            lastCue = isOpen;
            lastCueTime = now;
        }
    }

    async Task PlayQueuedAsync()
    {
        using var onStream = new MemoryStream(CreateCue(660), writable: false);
        using var offStream = new MemoryStream(CreateCue(570), writable: false);
        using var on = new SoundPlayer(onStream);
        using var off = new SoundPlayer(offStream);
        await foreach (bool isOpen in pending.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (disposed) break;
            // Finish each pair even when PTT is tapped quickly. Playback is off the hook and UI threads.
            try { (isOpen ? on : off).PlaySync(); }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }
    }

    static byte[] CreateCue(double frequency)
    {
        const int sampleRate = 48000;
        const int pulse = sampleRate * 55 / 1000;
        const int gap = sampleRate * 35 / 1000;
        const double peak = 0.045;
        var samples = new float[2 * pulse + gap];
        for (int i = 0; i < pulse; i++)
        {
            double envelope = Math.Sin(Math.PI * i / (pulse - 1));
            float sample = (float)(peak * envelope * envelope * Math.Sin(Math.Tau * frequency * i / sampleRate));
            samples[i] = samples[pulse + gap + i] = sample;
        }

        using var stream = new MemoryStream();
        using (var writer = new WaveFileWriter(stream, new WaveFormat(sampleRate, 16, 1)))
            writer.WriteSamples(samples, 0, samples.Length);
        return stream.ToArray();
    }

    public void Dispose()
    {
        disposed = true;
        pending.Writer.TryComplete();
    }
}
