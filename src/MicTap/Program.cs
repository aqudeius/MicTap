using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Text.Json;

namespace MicTap;

static class Program
{
    [STAThread] static void Main(string[] args)
    {
        PortableLayout.RegisterLocalizations();
        if (args.Contains("--self-test")) { Checks.Run(); return; }
        ApplicationConfiguration.Initialize();
        Application.Run(new MonitorForm());
    }
}

// A per-output delay line, independent of the capture/playback queue.
sealed class DelayLine : ISampleProvider
{
    readonly ISampleProvider source;
    readonly float[] ring;
    int cursor;
    public volatile int Milliseconds;
    public volatile float Gain = .5f;
    public volatile bool Muted;
    public volatile bool MonoMix;
    public Func<bool>? Gate;
    public WaveFormat WaveFormat => source.WaveFormat;
    public DelayLine(ISampleProvider source)
    {
        this.source = source;
        ring = new float[(source.WaveFormat.SampleRate * 5 + 1) * source.WaveFormat.Channels];
    }
    public int Read(float[] buffer, int offset, int count)
    {
        int read = source.Read(buffer, offset, count);
        int channels = WaveFormat.Channels;
        int delay = (int)((long)WaveFormat.SampleRate * Math.Clamp(Milliseconds, 0, 5000) / 1000) * channels;
        float gain = Muted || Gate?.Invoke() == false ? 0 : Gain;
        for (int i = offset; i < offset + read; i++)
        {
            ring[cursor] = buffer[i];
            buffer[i] = ring[(cursor - delay + ring.Length) % ring.Length] * gain;
            cursor = (cursor + 1) % ring.Length;
        }
        if (MonoMix && channels > 1)
        {
            for (int i = offset; i <= offset + read - channels; i += channels)
            {
                var frame = buffer.AsSpan(i, channels);
                float sum = 0;
                foreach (float sample in frame) sum += sample;
                frame.Fill(sum / channels);
            }
        }
        return read;
    }
}

sealed class Route : IDisposable
{
    readonly object gate = new();
    readonly BufferedWaveProvider queue;
    readonly WasapiOut player;
    readonly MMDevice device;
    bool disposed;
    public DelayLine Delay { get; }
    public Route(MMDevice device, WaveFormat format, int latency, Action<Exception> failed)
    {
        this.device = device;
        WasapiOut? opened = null;
        try
        {
            queue = new BufferedWaveProvider(format) { BufferDuration = TimeSpan.FromSeconds(1), DiscardOnBufferOverflow = true, ReadFully = true };
            Delay = new DelayLine(new LockedSource(queue, gate).ToSampleProvider());
            player = opened = new WasapiOut(device, AudioClientShareMode.Shared, true, latency);
            ISampleProvider playback = Delay;
            if (format.Channels == 1 && player.OutputWaveFormat.Channels == 2)
                playback = new MonoToStereoSampleProvider(playback);
            player.Init(new SampleToWaveProvider(playback));
            player.PlaybackStopped += (_, e) => { if (e.Exception != null && !disposed) failed(e.Exception); };
        }
        catch { try { opened?.Dispose(); } finally { device.Dispose(); } throw; }
    }
    public void Start() => player.Play();
    public void Push(byte[] bytes, int count)
    {
        lock (gate)
        {
            if (disposed) return;
            // Bound latency if a device stalls or its sample clock drifts.
            if (queue.BufferedDuration.TotalMilliseconds > 200) queue.ClearBuffer();
            queue.AddSamples(bytes, 0, count);
        }
    }
    public void Dispose()
    {
        lock (gate) disposed = true;
        try { player.Stop(); } finally { player.Dispose(); device.Dispose(); }
    }
    sealed class LockedSource(BufferedWaveProvider source, object gate) : IWaveProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;
        public int Read(byte[] buffer, int offset, int count) { lock (gate) return source.Read(buffer, offset, count); }
    }
}

record DeviceChoice(string Id, string Name, bool Available = true) { public override string ToString() => Available ? Name : Name + " (disconnected)"; }

sealed record OutputSettings(DeviceChoice? Device, int Volume = 50, int Delay = 0, bool Muted = false, ChannelTalkSettings? Talk = null, bool MonoMix = false);
sealed record InputSettings(DeviceChoice? Input, int BufferIndex, OutputSettings[] Outputs);
sealed record AppSettings(InputSettings[] Inputs);
static class SettingsStore
{
    sealed record LegacySettings(DeviceChoice? Input, int BufferIndex, OutputSettings[]? Outputs, TalkSettings? Talk);

    public static readonly string PathName = Path.Combine(PortableLayout.DataDirectory, "settings.json");
    public static AppSettings? Load(string path)
    {
        if (!File.Exists(path)) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Null) return null;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("MicTap settings must be a JSON object.");
        if (root.TryGetProperty(nameof(AppSettings.Inputs), out _)) return root.Deserialize<AppSettings>();

        var old = root.Deserialize<LegacySettings>()!;
        var legacyTalk = old.Talk is { GateMonitors: true } talk
            ? new ChannelTalkSettings(true, talk.Mode, talk.Binding, talk.PassThrough)
            : new ChannelTalkSettings();
        var outputs = (old.Outputs ?? []).OfType<OutputSettings>()
            .Select(output => output with { Talk = output.Talk ?? legacyTalk }).ToArray();
        return new AppSettings([new InputSettings(old.Input, old.BufferIndex, outputs)]);
    }
    public static AppSettings? LoadCurrent()
    {
        if (File.Exists(PathName) || PortableLayout.SystemDirectory is not { } system) return Load(PathName);

        string previousPath = Path.Combine(system, "..", "settings.json");
        AppSettings? settings = Load(previousPath);
        if (settings == null) return null;

        // Load first so an unreadable file stays in place for recovery.
        try { File.Move(previousPath, PathName); }
        catch (IOException) when (File.Exists(PathName)) { return Load(PathName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
        return settings;
    }
    public static void Save(string path, AppSettings settings)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
    public static void SelectDevice(ComboBox box, IEnumerable<DeviceChoice> devices, DeviceChoice? wanted, bool selectFirst = true)
    {
        box.Items.Clear(); box.Items.AddRange(devices.ToArray());
        var match = box.Items.Cast<DeviceChoice>().FirstOrDefault(d => d.Id == wanted?.Id);
        if (match != null) box.SelectedItem = match;
        else if (wanted != null) { var missing = wanted with { Available = false }; box.Items.Add(missing); box.SelectedItem = missing; }
        else box.SelectedIndex = selectFirst && box.Items.Count > 0 ? 0 : -1;
    }
}

sealed class OutputRow : Panel
{
    public readonly ComboBox Device = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 365 };
    public readonly NumericUpDown Delay = new() { Minimum = 0, Maximum = 5000, Increment = 10, Width = 90 };
    public readonly TrackBar Volume = new() { Minimum = 0, Maximum = 100, Value = 50, TickStyle = TickStyle.None, Width = 155, Height = 32 };
    public readonly CheckBox Mute = new() { Text = "Mute", AutoSize = true };
    public readonly Button Remove = new() { Text = "Remove", Width = 85, Height = 30 };
    public readonly CheckBox TalkEnabled = new() { Text = "Talk control", AutoSize = true };
    public readonly ComboBox TalkModeBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    public readonly Button BindKey = new() { Width = 200, Height = 30 };
    public readonly CheckBox PassKey = new() { Text = "Pass key to other apps", Checked = true, AutoSize = true };
    public readonly CheckBox GateBeeps = new() { Text = "On/off beeps", AutoSize = true };
    public readonly CheckBox MonoMix = new() { Text = "Mono mix", AutoSize = true, AccessibleDescription = "Play the same mix in both ears." };
    public readonly Button ToggleTalk = new() { Text = "Toggle talk", Width = 130, Height = 30 };
    public readonly TalkGate TalkGate = new();
    public readonly Guid ChannelId = Guid.NewGuid();
    public Keys Binding = Keys.Control | Keys.Shift | Keys.F8;
    public readonly Action<bool> KeyChanged;
    volatile bool talkEnabled, gateBeepsEnabled;
    readonly Label talkState = new() { AutoSize = true };
    readonly Label percent = new() { Text = "50%", Width = 48 };
    public Route? Active;
    public OutputRow(DeviceChoice[] devices, Action<bool> playTalkCue)
    {
        Height = 166; Width = 825; BackColor = Color.FromArgb(242, 245, 249); Margin = new Padding(0, 0, 0, 12);
        KeyChanged = TalkGate.SetHeld;
        TalkGate.KeyedStateChanged += isOpen =>
        {
            if (talkEnabled && gateBeepsEnabled) playTalkCue(isOpen);
        };
        Controls.Add(new Label { Text = "OUTPUT DEVICE", Location = new Point(16, 12), AutoSize = true, ForeColor = Color.DimGray });
        Device.Location = new Point(16, 36); Device.Items.AddRange(devices); if (devices.Length > 0) Device.SelectedIndex = 0; Controls.Add(Device);
        Controls.Add(new Label { Text = "Added delay (ms)", Location = new Point(400, 12), AutoSize = true });
        Delay.Location = new Point(400, 37); Controls.Add(Delay);
        Controls.Add(new Label { Text = "Volume", Location = new Point(510, 12), AutoSize = true });
        Volume.Location = new Point(505, 34); Controls.Add(Volume); percent.Location = new Point(665, 40); Controls.Add(percent);
        Mute.Location = new Point(725, 40); Controls.Add(Mute);
        TalkEnabled.Location = new Point(16, 83); Controls.Add(TalkEnabled);
        TalkModeBox.Items.AddRange(["Open", "Toggle", "Push to talk"]); TalkModeBox.SelectedIndex = 0;
        TalkModeBox.Location = new Point(155, 79); Controls.Add(TalkModeBox);
        BindKey.Location = new Point(330, 77); Controls.Add(BindKey);
        PassKey.Location = new Point(548, 83); Controls.Add(PassKey);
        GateBeeps.Location = new Point(16, 106); Controls.Add(GateBeeps);
        MonoMix.Location = new Point(155, 106); Controls.Add(MonoMix);
        GateBeeps.CheckedChanged += (_, _) => gateBeepsEnabled = GateBeeps.Checked;
        talkState.Location = new Point(16, 132); Controls.Add(talkState);
        ToggleTalk.Location = new Point(565, 122); Controls.Add(ToggleTalk);
        Remove.Location = new Point(720, 122); Controls.Add(Remove);
        Delay.ValueChanged += (_, _) => UpdateRoute(); Volume.ValueChanged += (_, _) => UpdateRoute(); Mute.CheckedChanged += (_, _) => UpdateRoute();
        MonoMix.CheckedChanged += (_, _) => UpdateRoute();
        ToggleTalk.Click += (_, _) => { TalkGate.Toggle(); UpdateTalkState(); };
        ConfigureTalk();
    }
    public bool IsAudible => !talkEnabled || TalkGate.IsOpen;
    public ChannelTalkSettings ReadTalk() => new(TalkEnabled.Checked, (TalkMode)TalkModeBox.SelectedIndex, (int)Binding, PassKey.Checked, GateBeeps.Checked);
    public void ConfigureTalk()
    {
        talkEnabled = TalkEnabled.Checked;
        TalkGate.Configure((TalkMode)TalkModeBox.SelectedIndex);
        TalkModeBox.Enabled = talkEnabled;
        BindKey.Enabled = PassKey.Enabled = GateBeeps.Enabled = talkEnabled && TalkModeBox.SelectedIndex != 0;
        BindKey.Text = GlobalTalkKey.Display(Binding) + " · assign…";
        UpdateTalkState();
    }
    public void UpdateTalkState()
    {
        bool active = Active != null;
        talkState.Text = !active ? "Stopped" : Mute.Checked ? "MUTED · manual mute" : IsAudible ? "OPEN · audio passing" : "CLOSED · audio silenced";
        talkState.ForeColor = active && IsAudible && !Mute.Checked ? Color.DarkGreen : Color.DimGray;
        ToggleTalk.Enabled = active && talkEnabled && TalkModeBox.SelectedIndex == (int)TalkMode.Toggle;
    }
    public void UpdateRoute()
    {
        percent.Text = $"{Volume.Value}%";
        if (Active == null) return;
        Active.Delay.Milliseconds = (int)Delay.Value; Active.Delay.Gain = Volume.Value / 100f; Active.Delay.Muted = Mute.Checked;
        Active.Delay.MonoMix = MonoMix.Checked;
    }
}

static class Checks
{
    sealed class Impulse(int channels) : ISampleProvider
    {
        int position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, channels);
        public int Read(float[] b, int o, int c) { for (int i = 0; i < c; i++) b[o+i] = position++ < channels ? 1 : 0; return c; }
    }

    static void CheckMonoMix()
    {
        float[] stereo = [.75f, 0, 0, .5f, .25f, .75f];
        var bytes = new byte[stereo.Length * sizeof(float)];
        Buffer.BlockCopy(stereo, 0, bytes, 0, bytes.Length);
        using var source = new RawSourceWaveStream(new MemoryStream(bytes), WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        var route = new DelayLine(source.ToSampleProvider()) { Gain = 1 };
        var output = new float[stereo.Length];
        if (route.Read(output, 0, output.Length) != output.Length || !output.SequenceEqual(stereo))
            throw new Exception("Stereo routing must preserve separate channels with mono mix off");

        source.Position = 0;
        route.MonoMix = true;
        if (route.Read(output, 0, output.Length) != output.Length || !output.SequenceEqual([.375f, .375f, .25f, .25f, .5f, .5f]))
            throw new Exception("Mono mix must center left-only, right-only, and stereo frames");
    }

    public static void Run()
    {
        if (PortableLayout.SystemDirectory is { } system)
        {
            var localizations = Path.Combine(system, "Localizations");
            string? resource = Directory.Exists(localizations)
                ? Directory.EnumerateFiles(localizations, "System.Windows.Forms.resources.dll", SearchOption.AllDirectories).FirstOrDefault()
                : null;
            if (resource != null)
            {
                var culture = System.Globalization.CultureInfo.GetCultureInfo(Path.GetFileName(Path.GetDirectoryName(resource))!);
                var loaded = typeof(Form).Assembly.GetSatelliteAssembly(culture);
                if (!string.Equals(loaded.Location, resource, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("WinForms localizations must load from System/Localizations");
            }
        }
        CheckMonoMix();
        int checks = 0;
        foreach (int channels in new[] { 1, 2 }) foreach (int ms in new[] { 0, 1, 125, 5000 })
        {
            var delay = new DelayLine(new Impulse(channels)) { Milliseconds = ms, Gain = .5f };
            int target = 48 * ms * channels; float[] b = new float[target + channels + 100]; delay.Read(b, 0, b.Length);
            for (int i = 0; i < b.Length; i++) if (b[i] != (i >= target && i < target + channels ? .5f : 0)) throw new Exception("Delay/gain test failed");
            checks++;
        }
        var mute = new DelayLine(new Impulse(1)) { Muted = true }; var silent = new float[100]; mute.Read(silent, 0, 100); if (silent.Any(x => x != 0)) throw new Exception("Mute test failed");
        string path = Path.Combine(AppContext.BaseDirectory, "MicTap-test-" + Guid.NewGuid() + ".json");
        try
        {
            if (SettingsStore.Load(path) != null) throw new Exception("First-launch settings test failed");
            var oldTalk = JsonSerializer.Deserialize<ChannelTalkSettings>("""{"Enabled":true,"Mode":1}""");
            if (oldTalk?.BeepOnChange != false) throw new Exception("Older channel settings must keep talk cues off");
            var first = new InputSettings(new DeviceChoice("test-microphone", "My mic"), 2,
                [new(new DeviceChoice("test-headphones", "Headphones"), 37, 125, true,
                    new ChannelTalkSettings(true, TalkMode.Toggle, BeepOnChange: true), MonoMix: true),
                 new(new DeviceChoice("test-speakers", "Speakers"), 81, 2300)]);
            var saved = new AppSettings([first,
                new(new DeviceChoice("test-line-in", "Line in"), 0,
                    [new(new DeviceChoice("test-cable", "Virtual cable"), Talk: new ChannelTalkSettings(true, TalkMode.PushToTalk, (int)(Keys.Shift | Keys.F9), false))]),
                new(null, 1, [new(null)]),
                new(new DeviceChoice("test-unused", "Unused input"), 1, [])]);
            SettingsStore.Save(path, saved);
            var restored = SettingsStore.Load(path) ?? throw new Exception("Saved settings missing");
            if (JsonSerializer.Serialize(saved) != JsonSerializer.Serialize(restored)) throw new Exception("Settings round-trip failed");
            MonitorForm.CheckSettingsRestore(restored);
            var legacy = new
            {
                first.Input, first.BufferIndex, first.Outputs,
                Talk = new TalkSettings(TalkMode.PushToTalk, (int)(Keys.Control | Keys.F9), false, true, true)
            };
            File.WriteAllText(path, JsonSerializer.Serialize(legacy));
            var migrated = SettingsStore.Load(path) ?? throw new Exception("Legacy settings missing");
            if (migrated.Inputs.Length != 1 || migrated.Inputs[0].Input != first.Input || migrated.Inputs[0].BufferIndex != first.BufferIndex ||
                migrated.Inputs[0].Outputs.Length != 2 || migrated.Inputs[0].Outputs[0] != first.Outputs[0] ||
                migrated.Inputs[0].Outputs[1].Talk != new ChannelTalkSettings(true, TalkMode.PushToTalk, (int)(Keys.Control | Keys.F9), false))
                throw new Exception("Legacy settings must migrate to one group with the same outputs and talk controls");
            MonitorForm.CheckSettingsRestore(migrated);
            File.WriteAllText(path, """{"Input":null,"BufferIndex":1,"Outputs":[]}""");
            MonitorForm.CheckSettingsRestore(SettingsStore.Load(path)!);
            File.WriteAllText(path, "invalid json");
            bool rejected = false; try { SettingsStore.Load(path); } catch (JsonException) { rejected = true; }
            if (!rejected) throw new Exception("Malformed settings test failed");
        }
        finally { File.Delete(path); }
        foreach (var format in new[] { new WaveFormat(44100, 16, 1), WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), new WaveFormatExtensible(48000, 32, 2) })
        {
            string wav = path + ".wav";
            try
            {
                byte[] samples = new byte[format.AverageBytesPerSecond / 10];
                for (int i = 0; i < samples.Length; i++) samples[i] = (byte)(i % 127);
                // Record in memory, then export only after stopping capture.
                for (int take = 0; take < 2; take++)
                {
                    File.Delete(wav);
                    MonitorForm.CheckRecordingFinalization(wav, format, samples);
                    using var reader = new WaveFileReader(wav);
                    if (reader.Length != samples.Length || reader.WaveFormat.SampleRate != format.SampleRate || reader.WaveFormat.Channels != format.Channels)
                        throw new Exception("WAV export header/length mismatch");
                    byte[] actual = new byte[samples.Length];
                    if (reader.Read(actual, 0, actual.Length) != samples.Length || !actual.SequenceEqual(samples))
                        throw new Exception("WAV export changed the captured samples");
                }
            }
            finally { File.Delete(wav); }
        }
        string exportPath = path + ".wav";
        string secondExport = path + "-copy.wav";
        try
        {
            var format = new WaveFormat(48000, 16, 1);
            var retained = new Recording(format, 8);
            byte[] block = [1, 2, 3, 4];
            retained.Append(block, block.Length); block[0] = 9;
            retained.Append([5, 6, 7, 8], 4);
            retained.Append([9, 10], 2);
            if (retained.Bytes != 8 || retained.Error == null) throw new Exception("Memory limit did not preserve prior samples");
            bool rejected = false;
            try { retained.Export(exportPath); } catch (InvalidOperationException) { rejected = true; }
            if (!rejected || File.Exists(exportPath)) throw new Exception("Active recording was exported");
            retained.Complete();
            rejected = false;
            try { retained.Export(Path.Combine(path + "-missing", "take.wav")); } catch (DirectoryNotFoundException) { rejected = true; }
            if (!rejected || retained.Exported || retained.Bytes != 8) throw new Exception("Failed export lost the take");
            File.WriteAllText(exportPath, "old destination contents");
            retained.Export(exportPath); retained.Export(secondExport);
            if (!File.ReadAllBytes(exportPath).SequenceEqual(File.ReadAllBytes(secondExport))) throw new Exception("Repeated export changed the take");
            using var reader = new WaveFileReader(exportPath);
            byte[] actual = new byte[8]; reader.Read(actual, 0, 8);
            if (!actual.SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })) throw new Exception("Retained audio changed after callback buffer reuse");
        }
        finally { File.Delete(exportPath); File.Delete(secondExport); }
        var gate = new TalkGate { Active = true };
        gate.Configure(TalkMode.Open); gate.SetHeld(false); if (!gate.IsOpen) throw new Exception("Open microphone gate failed");
        gate.Configure(TalkMode.Toggle);
        if (gate.IsOpen) throw new Exception("Toggle startup must be muted");
        gate.SetHeld(true); gate.SetHeld(true); if (!gate.IsOpen) throw new Exception("Toggle key repeat was not ignored");
        gate.SetHeld(false); if (!gate.IsOpen) throw new Exception("Toggle release changed the gate");
        gate.SetHeld(true); if (gate.IsOpen) throw new Exception("Toggle second press failed");
        gate.Configure(TalkMode.PushToTalk); gate.SetHeld(true); if (!gate.IsOpen) throw new Exception("PTT press failed");
        gate.SetHeld(false); if (gate.IsOpen) throw new Exception("PTT release failed");
        gate.SetHeld(true); gate.Active = false; if (gate.IsOpen) throw new Exception("Inactive audio must be closed");
        gate.Active = true; gate.Reset(); if (gate.IsOpen) throw new Exception("PTT reset must be muted");
        var gated = new DelayLine(new Impulse(1)) { Gain = 1, Gate = () => gate.IsOpen };
        var gatedAudio = new float[100]; gated.Read(gatedAudio, 0, gatedAudio.Length);
        if (gatedAudio.Any(x => x != 0)) throw new Exception("Closed gate leaked audio");
        if (GlobalTalkKey.Valid(Keys.ControlKey) || GlobalTalkKey.Valid(Keys.LWin) || GlobalTalkKey.Valid(Keys.F12) || !GlobalTalkKey.Valid(Keys.Control | Keys.Shift | Keys.F8)) throw new Exception("Talk key validation failed");
        ChannelKeyChecks.Run();
        File.WriteAllText(Path.Combine(PortableLayout.DataDirectory, "self-test.txt"), $"PASS: {checks} mono/stereo impulse delay/gain cases; mute and mono mix; independent input routing and stop; input-group settings migration and round-trip; selected-input recording and WAV export; memory limit and export retry; independent toggle/PTT gates, shared chords, key-repeat handling, passthrough/suppression, reconfiguration, missing-release handling, and opt-in talk cues.\nUses existing playback endpoints and third-party virtual cables.\n");
    }
}
