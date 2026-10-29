using NAudio.Wave;

namespace MicTap;

// A take stays in memory until the user explicitly exports it. Once completed,
// its blocks are immutable, so exporting can run while live monitoring continues.
sealed class Recording
{
    readonly List<byte[]> blocks = [];
    readonly WaveFormat format;
    readonly long maxBytes;
    long written;
    public DateTime StartedAt { get; } = DateTime.Now;
    public Exception? Error { get; private set; }
    public bool IsComplete { get; private set; }
    public bool Exported { get; private set; }
    public long Bytes => Interlocked.Read(ref written);
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Bytes / format.AverageBytesPerSecond);
    public Recording(WaveFormat format, long maxBytes = 512L * 1024 * 1024)
    {
        this.format = format; this.maxBytes = maxBytes;
    }
    // The owner serializes Append and Complete with the audio callback.
    public void Append(byte[] bytes, int count)
    {
        if (IsComplete || Error != null || count == 0) return;
        if (written + count > maxBytes)
        {
            Error = new IOException("The temporary recording reached its memory limit (512 MB). Export or discard this take before recording another.");
            return;
        }
        var block = new byte[count]; Buffer.BlockCopy(bytes, 0, block, 0, count);
        blocks.Add(block); Interlocked.Add(ref written, count);
    }
    public void Complete() => IsComplete = true;
    public void Export(string path)
    {
        if (!IsComplete) throw new InvalidOperationException("Stop recording before exporting the take.");
        // Replace an existing destination only after the new WAV is finalized.
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var writer = new WaveFileWriter(temporary, format))
                foreach (var block in blocks) writer.Write(block, 0, block.Length);
            File.Move(temporary, path, true);
            Exported = true;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
