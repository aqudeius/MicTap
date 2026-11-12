using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MicTap;

sealed class InputSession : IDisposable
{
    readonly IWaveIn capture;
    readonly MMDevice? device;
    readonly object audioGate = new();
    readonly List<Action<byte[], int>> outputs = [];
    Recording? recording;

    public WaveFormat Format => capture.WaveFormat;
    public float Peak => device?.AudioMeterInformation.MasterPeakValue ?? 0;
    public event Action<Exception>? Failed;

    public InputSession(IWaveIn capture, MMDevice? device = null)
    {
        this.capture = capture;
        this.device = device;
        capture.DataAvailable += DataAvailable;
        capture.RecordingStopped += CaptureStopped;
    }

    public static InputSession Open(string deviceId, int latency)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDevice(deviceId);
        try { return new InputSession(new WasapiCapture(device, true, latency), device); }
        catch { device.Dispose(); throw; }
    }

    public void Start() => capture.StartRecording();

    public void AttachOutput(Action<byte[], int> push)
    {
        lock (audioGate) outputs.Add(push);
    }

    public void DetachOutput(Action<byte[], int> push)
    {
        lock (audioGate) outputs.Remove(push);
    }

    public Recording BeginTake()
    {
        lock (audioGate) return recording = new Recording(Format);
    }

    public void FinishTake()
    {
        lock (audioGate)
        {
            recording?.Complete();
            recording = null;
        }
    }

    void DataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (audioGate)
        {
            foreach (var output in outputs) output(e.Buffer, e.BytesRecorded);
            recording?.Append(e.Buffer, e.BytesRecorded);
        }
    }

    void CaptureStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null) Failed?.Invoke(e.Exception);
    }

    public void Dispose()
    {
        capture.DataAvailable -= DataAvailable;
        capture.RecordingStopped -= CaptureStopped;
        try { capture.Dispose(); }
        finally { device?.Dispose(); }
    }
}
