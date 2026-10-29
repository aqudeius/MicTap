using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Text.Json;

namespace MicTap;

static class Program
{
    [STAThread] static void Main(string[] args)
    {
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
        int delay = (int)((long)WaveFormat.SampleRate * Math.Clamp(Milliseconds, 0, 5000) / 1000) * WaveFormat.Channels;
        float gain = Muted || Gate?.Invoke() == false ? 0 : Gain;
        for (int i = offset; i < offset + read; i++)
        {
            ring[cursor] = buffer[i];
            buffer[i] = ring[(cursor - delay + ring.Length) % ring.Length] * gain;
            cursor = (cursor + 1) % ring.Length;
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
        queue = new BufferedWaveProvider(format) { BufferDuration = TimeSpan.FromSeconds(1), DiscardOnBufferOverflow = true, ReadFully = true };
        Delay = new DelayLine(new LockedSource(queue, gate).ToSampleProvider());
        player = new WasapiOut(device, AudioClientShareMode.Shared, true, latency);
        try
        {
            player.Init(new SampleToWaveProvider(Delay));
            player.PlaybackStopped += (_, e) => { if (e.Exception != null && !disposed) failed(e.Exception); };
        }
        catch { player.Dispose(); device.Dispose(); throw; }
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

sealed record OutputSettings(DeviceChoice? Device, int Volume = 50, int Delay = 0, bool Muted = false, ChannelTalkSettings? Talk = null);
sealed record AppSettings(DeviceChoice? Input, int BufferIndex, OutputSettings[] Outputs, TalkSettings? Talk = null);
static class SettingsStore
{
    public static ChannelTalkSettings ChannelTalk(OutputSettings output, TalkSettings? legacy) => output.Talk
        ?? (legacy?.GateMonitors == true ? new ChannelTalkSettings(true, legacy.Mode, legacy.Binding, legacy.PassThrough) : new ChannelTalkSettings());
    public static readonly string PathName = Path.Combine(AppContext.BaseDirectory, "settings.json");
    public static AppSettings? Load(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) : null;
    public static void Save(string path, AppSettings settings)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
    public static void SelectDevice(ComboBox box, IEnumerable<DeviceChoice> devices, DeviceChoice? wanted)
    {
        box.Items.Clear(); box.Items.AddRange(devices.ToArray());
        var match = box.Items.Cast<DeviceChoice>().FirstOrDefault(d => d.Id == wanted?.Id);
        if (match != null) box.SelectedItem = match;
        else if (wanted != null) { var missing = wanted with { Available = false }; box.Items.Add(missing); box.SelectedItem = missing; }
        else box.SelectedIndex = box.Items.Count > 0 ? 0 : -1;
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
    public readonly Button ToggleTalk = new() { Text = "Toggle talk", Width = 130, Height = 30 };
    public readonly TalkGate TalkGate = new();
    public readonly Guid ChannelId = Guid.NewGuid();
    public Keys Binding = Keys.Control | Keys.Shift | Keys.F8;
    public readonly Action<bool> KeyChanged;
    volatile bool talkEnabled;
    readonly Label talkState = new() { AutoSize = true };
    readonly Label percent = new() { Text = "50%", Width = 48 };
    public Route? Active;
    public OutputRow(DeviceChoice[] devices)
    {
        Height = 166; Width = 825; BackColor = Color.FromArgb(242, 245, 249); Margin = new Padding(0, 0, 0, 12);
        KeyChanged = TalkGate.SetHeld;
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
        talkState.Location = new Point(16, 132); Controls.Add(talkState);
        ToggleTalk.Location = new Point(565, 122); Controls.Add(ToggleTalk);
        Remove.Location = new Point(720, 122); Controls.Add(Remove);
        Delay.ValueChanged += (_, _) => UpdateRoute(); Volume.ValueChanged += (_, _) => UpdateRoute(); Mute.CheckedChanged += (_, _) => UpdateRoute();
        ToggleTalk.Click += (_, _) => { TalkGate.Toggle(); UpdateTalkState(); };
        ConfigureTalk();
    }
    public bool IsAudible => !talkEnabled || TalkGate.IsOpen;
    public ChannelTalkSettings ReadTalk() => new(TalkEnabled.Checked, (TalkMode)TalkModeBox.SelectedIndex, (int)Binding, PassKey.Checked);
    public void ConfigureTalk()
    {
        talkEnabled = TalkEnabled.Checked;
        TalkGate.Configure((TalkMode)TalkModeBox.SelectedIndex);
        TalkModeBox.Enabled = talkEnabled;
        BindKey.Enabled = PassKey.Enabled = talkEnabled && TalkModeBox.SelectedIndex != 0;
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
    }
}

sealed class MonitorForm : Form
{
    readonly ComboBox input = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 500 };
    readonly ComboBox latency = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 115 };
    readonly Button refresh = new() { Text = "Refresh devices", Width = 140, Height = 32 };
    readonly Button start = new() { Text = "Start listening", Width = 155, Height = 38, BackColor = Color.FromArgb(29, 91, 220), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    readonly Button add = new() { Text = "+ Add output", Width = 130, Height = 34 };
    readonly System.Windows.Forms.Timer keyTimer = new() { Interval = 15 };
    GlobalTalkKeys? talkKeys;
    bool hookFailed, sessionLocked, assigningKey;
    readonly Button record = new() { Text = "Record", Width = 155, Height = 34 };
    readonly Button export = new() { Text = "Save / export…", Width = 155, Height = 34, Enabled = false };
    readonly Button discard = new() { Text = "Discard take", Width = 130, Height = 34, Enabled = false };
    readonly Label recordingStatus = new() { Text = "Record a temporary take, then save/export when ready.", AutoEllipsis = true, Width = 820, Height = 42 };
    readonly object recordingGate = new();
    Recording? recording;
    Recording? take;
    bool recordingOnly;
    bool exporting, closingApproved, closingInProgress;
    readonly Label status = new() { AutoSize = false, Height = 40, Dock = DockStyle.Fill, Text = "Stopped · choose your mic and headphones, then press Start." };
    readonly ProgressBar meter = new() { Maximum = 100, Width = 160, Height = 12 };
    readonly FlowLayoutPanel rows = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 60 };
    readonly object routesGate = new();
    readonly List<Route> routes = [];
    DeviceChoice[] outputs = [];
    WasapiCapture? capture;
    MMDevice? inputDevice;
    bool running, stopping;
    bool loadingSettings = true;
    int generation;
    public MonitorForm()
    {
        Text = "MicTap — live audio monitor"; ClientSize = new Size(900, 790); MinimumSize = new Size(920, 660);
        Font = new Font("Segoe UI", 10); BackColor = Color.White; StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(26), RowCount = 6, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 95)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 95)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        var heading = new Panel { Dock = DockStyle.Fill };
        heading.Controls.Add(new Label { Text = "MicTap", Font = new Font("Segoe UI", 24, FontStyle.Bold), AutoSize = true });
        heading.Controls.Add(new Label { Text = "One input. As many listening routes as you need.", Location = new Point(2, 46), AutoSize = true, ForeColor = Color.DimGray }); layout.Controls.Add(heading, 0, 0);
        var source = new Panel { Dock = DockStyle.Fill };
        source.Controls.Add(new Label { Text = "AUDIO INPUT", AutoSize = true, Location = new Point(0, 12) }); input.Location = new Point(0, 38); source.Controls.Add(input);
        refresh.Location = new Point(515, 36); source.Controls.Add(refresh);
        source.Controls.Add(new Label { Text = "Buffer", Location = new Point(680, 12), AutoSize = true }); latency.Items.AddRange(["20 ms", "50 ms", "100 ms"]); latency.SelectedIndex = 1; latency.Location = new Point(680, 38); source.Controls.Add(latency); layout.Controls.Add(source, 0, 1);
        var toolbar = new Panel { Dock = DockStyle.Fill }; add.Location = new Point(0, 0); toolbar.Controls.Add(add);
        toolbar.Controls.Add(new Label { Text = "Use headphones to avoid microphone feedback.", AutoSize = true, ForeColor = Color.DimGray, Location = new Point(150, 8) });
        toolbar.Controls.Add(new Label { Text = "Talk keys work with other apps active. Shared keys control every matching output.", AutoSize = true, ForeColor = Color.DimGray, Location = new Point(0, 43) });
        layout.Controls.Add(toolbar, 0, 2); layout.Controls.Add(rows, 0, 3);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 }; footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        var recordingPanel = new Panel { Dock = DockStyle.Fill };
        recordingPanel.Controls.Add(record); export.Location = new Point(170, 0); discard.Location = new Point(340, 0);
        recordingPanel.Controls.Add(export); recordingPanel.Controls.Add(discard);
        recordingStatus.Location = new Point(0, 42); recordingPanel.Controls.Add(recordingStatus); layout.Controls.Add(recordingPanel, 0, 4);
        var info = new Panel { Dock = DockStyle.Fill }; info.Controls.Add(status); meter.Location = new Point(0, 47); info.Controls.Add(meter); meter.BringToFront(); footer.Controls.Add(info); footer.Controls.Add(start); layout.Controls.Add(footer, 0, 5); Controls.Add(layout);
        refresh.Click += (_, _) => RefreshDevices(); add.Click += (_, _) => AddRow(); start.Click += (_, _) => { if (running) Stop(); else Start(); };
        timer.Tick += (_, _) => { try { meter.Value = inputDevice == null ? 0 : Math.Clamp((int)(inputDevice.AudioMeterInformation.MasterPeakValue * 100), 0, 100); } catch { meter.Value = 0; } };
        record.Click += async (_, _) => await ToggleRecordingAsync();
        export.Click += async (_, _) => await ExportTakeAsync();
        discard.Click += (_, _) =>
        {
            if (take == null || recording != null || exporting) return;
            if (MessageBox.Show(this, "Discard this temporary take?", "MicTap", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            take = null; UpdateTakeControls(); recordingStatus.Text = "Take discarded. Ready to record.";
        };
        timer.Tick += (_, _) =>
        {
            if (recording == null) return;
            lock (recordingGate)
            {
                if (recording.Error == null)
                {
                    recordingStatus.Text = $"Recording • {TakeSummary(recording)} • temporary in app\nOriginal input; output volume, mute and delay are not recorded.";
                    return;
                }
            }
            bool stopCapture = recordingOnly; FinishRecording(); if (stopCapture) Stop();
        };
        input.SelectedIndexChanged += (_, _) => SaveSettings();
        latency.SelectedIndexChanged += (_, _) => SaveSettings();
        keyTimer.Tick += (_, _) =>
        {
            talkKeys?.CheckRelease();
            foreach (OutputRow row in rows.Controls) row.UpdateTalkState();
        };
        FormClosing += async (_, e) =>
        {
            if (closingApproved) return;
            e.Cancel = true;
            if (exporting || closingInProgress) return;
            closingInProgress = true;
            try
            {
                // Let the first close event return with Cancel set before closing again.
                await Task.Yield();
                SaveSettings(); Stop();
                if (await ConfirmReleaseTakeAsync("closing MicTap")) { closingApproved = true; Close(); }
            }
            finally { closingInProgress = false; }
        };
        FormClosed += (_, _) => { take = null; timer.Dispose(); keyTimer.Dispose(); talkKeys?.Dispose(); Microsoft.Win32.SystemEvents.SessionSwitch -= SessionChanged; };
        Shown += (_, _) =>
        {
            AppSettings? saved = null; string? error = null;
            try { saved = SettingsStore.Load(SettingsStore.PathName); }
            catch (Exception ex) { error = "Could not restore settings: " + ex.Message; }
            RestoreSettings(saved);
            try { talkKeys = new GlobalTalkKeys(); }
            catch (Exception ex) { hookFailed = true; status.Text = "Global talk key unavailable: " + ex.Message; }
            ConfigureChannelKeys(); keyTimer.Start();
            Microsoft.Win32.SystemEvents.SessionSwitch += SessionChanged;
            if (error != null) status.Text = error;
        };
    }
    void SessionChanged(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason != Microsoft.Win32.SessionSwitchReason.SessionLock && e.Reason != Microsoft.Win32.SessionSwitchReason.SessionUnlock) return;
        if (!IsHandleCreated || IsDisposed) return;
        BeginInvoke(() =>
        {
            sessionLocked = e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock;
            foreach (OutputRow row in rows.Controls) { row.TalkGate.Active = row.Active != null && !sessionLocked; row.TalkGate.Reset(); }
            ConfigureChannelKeys();
        });
    }
    void ConfigureChannelKeys()
    {
        var bindings = rows.Controls.Cast<OutputRow>()
            .Where(r => !assigningKey && !sessionLocked && r.Active != null && r.TalkEnabled.Checked && r.TalkModeBox.SelectedIndex != 0)
            .Select(r => new ChannelKeyBinding(r.ChannelId, r.Binding, r.PassKey.Checked, r.KeyChanged)).ToArray();
        talkKeys?.Configure(bindings);
    }
    static string TakeSummary(Recording session)
    {
        var duration = session.Duration;
        return $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00} • {session.Bytes / 1048576d:0.0} MB";
    }
    void UpdateTakeControls()
    {
        record.Enabled = !exporting;
        record.Text = recording != null ? "Stop recording" : take != null ? "Record new take" : "Record";
        record.ForeColor = recording != null ? Color.DarkRed : SystemColors.ControlText;
        export.Enabled = discard.Enabled = !exporting && recording == null && take != null;
    }
    async Task<bool> ConfirmReleaseTakeAsync(string action)
    {
        if (take == null || take.Exported) return true;
        var answer = MessageBox.Show(this, $"Save the temporary take before {action}?\n\nYes: save/export. No: discard it. Cancel: keep it in MicTap.", "Unsaved recording", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.Cancel) return false;
        return answer == DialogResult.No || await ExportTakeAsync();
    }
    async Task<bool> ExportTakeAsync()
    {
        if (take == null || recording != null || exporting) return false;
        var session = take;
        using var dialog = new SaveFileDialog
        {
            Title = "Export recording", Filter = "WAV audio (*.wav)|*.wav",
            DefaultExt = "wav", AddExtension = true, OverwritePrompt = true,
            FileName = "MicTap-" + session.StartedAt.ToString("yyyy-MM-dd-HHmmss") + ".wav"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;
        exporting = true; UpdateTakeControls(); recordingStatus.Text = "Exporting take…";
        try
        {
            await Task.Run(() => session.Export(dialog.FileName));
            recordingStatus.Text = "Exported: " + dialog.FileName + "\nThe take is still available in MicTap until discarded or replaced.";
            return true;
        }
        catch (Exception ex) { recordingStatus.Text = "Export failed: " + ex.Message + "\nYour take is still in MicTap. You can try exporting again."; return false; }
        finally { exporting = false; UpdateTakeControls(); }
    }
    async Task ToggleRecordingAsync()
    {
        if (exporting) return;
        if (recording != null)
        {
            bool stopCapture = recordingOnly; FinishRecording(); if (stopCapture) Stop(); return;
        }
        if (!await ConfirmReleaseTakeAsync("starting a new recording")) return;
        bool startedCapture = !running;
        if (startedCapture) Start(false);
        if (!running) return;
        try
        {
            var session = new Recording(capture!.WaveFormat);
            lock (recordingGate) { recording = session; take = session; }
            recordingOnly = startedCapture;
            UpdateTakeControls();
            recordingStatus.Text = "Recording • temporary in app";
        }
        catch (Exception ex)
        {
            if (startedCapture) Stop();
            recordingStatus.Text = "Could not record: " + ex.Message;
        }
    }
    void FinishRecording()
    {
        Recording? session;
        lock (recordingGate) { session = recording; recording = null; session?.Complete(); }
        if (session == null) return;
        recordingOnly = false;
        UpdateTakeControls();
        recordingStatus.Text = session.Error == null
            ? "Take ready • " + TakeSummary(session) + " • not exported\nKept temporarily in MicTap. Save/export or discard when ready."
            : "Take preserved • " + TakeSummary(session) + "\n" + session.Error.Message;
    }
    AppSettings ReadSettings() => new(input.SelectedItem as DeviceChoice, latency.SelectedIndex,
        rows.Controls.Cast<OutputRow>().Select(r => new OutputSettings(r.Device.SelectedItem as DeviceChoice, r.Volume.Value, (int)r.Delay.Value, r.Mute.Checked, r.ReadTalk())).ToArray());
    void SaveSettings()
    {
        if (loadingSettings) return;
        try { SettingsStore.Save(SettingsStore.PathName, ReadSettings()); }
        catch (Exception ex) { status.Text = "Settings could not be saved: " + ex.Message; }
    }
    void RestoreSettings(AppSettings? saved)
    {
        loadingSettings = true;
        RefreshDevices();
        if (saved == null) AddRow();
        else
        {
            SettingsStore.SelectDevice(input, input.Items.Cast<DeviceChoice>().ToArray(), saved.Input);
            latency.SelectedIndex = Math.Clamp(saved.BufferIndex, 0, 2);
            foreach (var setting in saved.Outputs ?? []) if (setting != null) AddRow(setting with { Talk = SettingsStore.ChannelTalk(setting, saved.Talk) });
        }
        ConfigureChannelKeys();
        loadingSettings = false;
    }
    internal static void CheckSettingsRestore(AppSettings saved)
    {
        using var form = new MonitorForm();
        form.RestoreSettings(saved);
        var actual = form.ReadSettings();
        if (actual.Input?.Id != saved.Input?.Id || actual.BufferIndex != saved.BufferIndex || actual.Outputs.Length != saved.Outputs.Length || form.running)
            throw new Exception("Input, buffer, route count, or stopped-state restoration failed");
        for (int i = 0; i < actual.Outputs.Length; i++)
        {
            var a = actual.Outputs[i]; var b = saved.Outputs[i];
            if (a.Device?.Id != b.Device?.Id || a.Volume != b.Volume || a.Delay != b.Delay || a.Muted != b.Muted)
                throw new Exception("Output settings restoration failed");
            if (a.Talk != SettingsStore.ChannelTalk(b, saved.Talk)) throw new Exception("Channel talk/keybinding restoration failed");
        }
        using var selector = new ComboBox();
        var device = new DeviceChoice("test-endpoint", "Test device");
        SettingsStore.SelectDevice(selector, [], device);
        if (selector.SelectedItem is not DeviceChoice { Available: false }) throw new Exception("Missing-device preservation failed");
        SettingsStore.SelectDevice(selector, [device], selector.SelectedItem as DeviceChoice);
        if (selector.SelectedItem is not DeviceChoice { Available: true }) throw new Exception("Device reconnection failed");
    }
    internal static void CheckRecordingFinalization(string path, WaveFormat format, byte[] samples)
    {
        using var form = new MonitorForm();
        form.recording = form.take = new Recording(format);
        form.recording.Append(samples, samples.Length);
        form.Stop();
        if (form.recording != null || form.take == null || !form.take.IsComplete || form.take.Exported || !form.export.Enabled || !form.discard.Enabled || File.Exists(path))
            throw new Exception("Stopping audio did not retain the temporary take without exporting");
        form.take.Export(path);
        if (!form.take.Exported || form.take.Bytes != samples.Length)
            throw new Exception("Export did not retain the take for additional exports");
    }
    void RefreshDevices()
    {
        bool previousLoading = loadingSettings; loadingSettings = true;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            DeviceChoice[] List(DataFlow flow)
            {
                var collection = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
                return collection.Select(d => { using (d) return new DeviceChoice(d.ID, d.FriendlyName); }).ToArray();
            }
            SettingsStore.SelectDevice(input, List(DataFlow.Capture), input.SelectedItem as DeviceChoice);
            outputs = List(DataFlow.Render);
            foreach (OutputRow row in rows.Controls)
            {
                SettingsStore.SelectDevice(row.Device, outputs, row.Device.SelectedItem as DeviceChoice);
            }
            status.Text = "Stopped · choose your mic and headphones, then press Start.";
        }
        catch (Exception ex) { status.Text = "Cannot list audio devices: " + ex.Message; }
        finally { loadingSettings = previousLoading; }
    }
    void AddRow(OutputSettings? saved = null)
    {
        var row = new OutputRow(outputs); rows.Controls.Add(row);
        if (saved != null)
        {
            SettingsStore.SelectDevice(row.Device, outputs, saved.Device);
            row.Volume.Value = Math.Clamp(saved.Volume, 0, 100);
            row.Delay.Value = Math.Clamp(saved.Delay, 0, 5000); row.Mute.Checked = saved.Muted;
            var talk = saved.Talk ?? new ChannelTalkSettings();
            row.TalkEnabled.Checked = talk.Enabled; row.TalkModeBox.SelectedIndex = Math.Clamp((int)talk.Mode, 0, 2);
            row.Binding = GlobalTalkKey.Valid((Keys)talk.Binding) ? (Keys)talk.Binding : Keys.Control | Keys.Shift | Keys.F8;
            row.PassKey.Checked = talk.PassThrough; row.ConfigureTalk();
        }
        row.Remove.Click += (_, _) =>
        {
            row.TalkGate.Active = false;
            if (row.Active != null) { lock (routesGate) routes.Remove(row.Active); row.Active.Dispose(); row.Active = null; }
            rows.Controls.Remove(row); ConfigureChannelKeys(); row.Dispose(); SaveSettings();
        };
        void TalkChanged() { row.ConfigureTalk(); ConfigureChannelKeys(); SaveSettings(); }
        row.TalkEnabled.CheckedChanged += (_, _) => TalkChanged();
        row.TalkModeBox.SelectedIndexChanged += (_, _) => TalkChanged();
        row.PassKey.CheckedChanged += (_, _) => { ConfigureChannelKeys(); SaveSettings(); };
        row.BindKey.Click += (_, _) =>
        {
            assigningKey = true; ConfigureChannelKeys(); talkKeys?.ReleaseAll();
            using var dialog = new BindKeyDialog(row.Binding);
            if (dialog.ShowDialog(this) == DialogResult.OK) row.Binding = dialog.SelectedBinding;
            assigningKey = false; TalkChanged();
        };
        if (running)
        {
            // Choose a device before activating a newly added route.
            row.Device.SelectedIndex = -1;
            row.Device.SelectedIndexChanged += (_, _) => { if (running && row.Active == null && row.Device.SelectedItem != null) { try { OpenRoute(row); } catch (Exception ex) { status.Text = "Output failed: " + ex.Message; } } };
        }
        row.Device.SelectedIndexChanged += (_, _) => SaveSettings();
        row.Volume.ValueChanged += (_, _) => SaveSettings();
        row.Delay.ValueChanged += (_, _) => SaveSettings();
        row.Mute.CheckedChanged += (_, _) => SaveSettings();
        SaveSettings();
    }
    void OpenRoute(OutputRow row)
    {
        if (row.Device.SelectedItem is not DeviceChoice choice) throw new InvalidOperationException("Choose a device for every output.");
        if (!choice.Available) throw new InvalidOperationException("An output is disconnected. Reconnect it and refresh devices, or choose another output.");
        if (hookFailed && row.TalkEnabled.Checked && row.TalkModeBox.SelectedIndex != 0) throw new InvalidOperationException("Global talk keys are unavailable. Restart MicTap or disable talk control on this output.");
        using var enumerator = new MMDeviceEnumerator();
        int session = generation;
        var route = new Route(enumerator.GetDevice(choice.Id), capture!.WaveFormat, new[] { 20, 50, 100 }[latency.SelectedIndex], ex => ReportFailure(session, ex));
        try
        {
            row.Active = route; row.UpdateRoute(); row.ConfigureTalk(); row.TalkGate.Active = !sessionLocked;
            route.Delay.Gate = () => row.IsAudible;
            lock (routesGate) routes.Add(route); route.Start(); row.Device.Enabled = false;
            ConfigureChannelKeys();
        }
        catch { lock (routesGate) routes.Remove(route); row.Active = null; row.TalkGate.Active = false; ConfigureChannelKeys(); route.Dispose(); throw; }
    }
    void Start(bool monitor = true)
    {
        try
        {
            if (input.SelectedItem is not DeviceChoice choice) throw new InvalidOperationException("No input selected. Connect a microphone and refresh devices.");
            if (!choice.Available) throw new InvalidOperationException("Your saved input is disconnected. Reconnect it and refresh devices, or choose another input.");
            if (monitor && hookFailed && rows.Controls.Cast<OutputRow>().Any(r => r.TalkEnabled.Checked && r.TalkModeBox.SelectedIndex != 0)) throw new InvalidOperationException("The global talk keys could not be installed. Restart MicTap or turn off talk control on keyed outputs.");
            if (monitor && rows.Controls.Count == 0) throw new InvalidOperationException("Add at least one output.");
            if (monitor && rows.Controls.Cast<OutputRow>().Any(r => r.Device.SelectedItem == null)) throw new InvalidOperationException("Choose a device for every output.");
            using var enumerator = new MMDeviceEnumerator(); inputDevice = enumerator.GetDevice(choice.Id);
            capture = new WasapiCapture(inputDevice, true, new[] { 20, 50, 100 }[latency.SelectedIndex]);
            int session = ++generation;
            capture.DataAvailable += (_, e) =>
            {
                lock (routesGate) foreach (var route in routes) route.Push(e.Buffer, e.BytesRecorded);
                lock (recordingGate) recording?.Append(e.Buffer, e.BytesRecorded);
            };
            capture.RecordingStopped += (_, e) => { if (e.Exception != null) ReportFailure(session, e.Exception); };
            if (monitor) foreach (OutputRow row in rows.Controls) OpenRoute(row);
            capture.StartRecording(); running = true; input.Enabled = refresh.Enabled = latency.Enabled = false; start.Text = monitor ? "Stop listening" : "Stop audio";
            ConfigureChannelKeys();
            status.Text = monitor ? "Listening · audio stays on this computer." : "Input active · live listening is off."; timer.Start();
        }
        catch (Exception ex) { Stop(); status.Text = "Could not start: " + ex.Message; MessageBox.Show(this, ex.Message + "\n\nCheck Windows microphone privacy permissions and your audio devices.", "MicTap", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
    void ReportFailure(int session, Exception ex)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(() => { if (session != generation || stopping) return; Stop(); status.Text = "Audio stopped: " + ex.Message + " Reconnect the device, then refresh."; }); } catch (InvalidOperationException) { }
    }
    void Stop()
    {
        if (stopping) return;
        stopping = true; ++generation; timer.Stop();
        foreach (OutputRow row in rows.Controls) { row.TalkGate.Active = false; row.TalkGate.Reset(); }
        talkKeys?.Configure([]); talkKeys?.ReleaseAll();
        try
        {
            try { capture?.StopRecording(); } catch { }
            try { capture?.Dispose(); } catch { }
            capture = null;
            FinishRecording();
            Route[] old; lock (routesGate) { old = routes.ToArray(); routes.Clear(); }
            foreach (var route in old) try { route.Dispose(); } catch { }
            foreach (OutputRow row in rows.Controls) { row.Active = null; row.Device.Enabled = true; }
            inputDevice?.Dispose(); inputDevice = null; running = false; input.Enabled = refresh.Enabled = latency.Enabled = true; start.Text = "Start listening"; meter.Value = 0;
            ConfigureChannelKeys();
            status.Text = "Stopped · microphone released.";
        }
        finally { stopping = false; }
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
    public static void Run()
    {
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
            var saved = new AppSettings(new DeviceChoice("test-microphone", "My mic"), 2,
                [new(new DeviceChoice("test-headphones", "Headphones"), 37, 125, true), new(new DeviceChoice("test-speakers", "Speakers"), 81, 2300)],
                new TalkSettings(TalkMode.PushToTalk, (int)(Keys.Control | Keys.F9), false, true, true));
            SettingsStore.Save(path, saved);
            var restored = SettingsStore.Load(path) ?? throw new Exception("Saved settings missing");
            if (JsonSerializer.Serialize(saved) != JsonSerializer.Serialize(restored)) throw new Exception("Settings round-trip failed");
            MonitorForm.CheckSettingsRestore(restored);
            SettingsStore.Save(path, saved with { Outputs = [] });
            MonitorForm.CheckSettingsRestore(SettingsStore.Load(path)!);
            File.WriteAllText(path, "invalid json");
            bool rejected = false; try { SettingsStore.Load(path); } catch (JsonException) { rejected = true; }
            if (!rejected) throw new Exception("Malformed settings test failed");
        }
        finally { File.Delete(path); }
        MonitorForm.CheckSettingsRestore(new AppSettings(new DeviceChoice("old-mic", "Previous input"), 1,
            [new(new DeviceChoice("old-output", "Previous output"))], new TalkSettings(TalkMode.Toggle, (int)(Keys.Alt | Keys.F9), false, true)));
        MonitorForm.CheckSettingsRestore(new AppSettings(new DeviceChoice("per-channel-mic", "Mic"), 1,
            [new(new DeviceChoice("toggle-output", "Toggle cable"), Talk: new ChannelTalkSettings(true, TalkMode.Toggle, (int)(Keys.Control | Keys.F8), true)),
             new(new DeviceChoice("ptt-output", "PTT cable"), Talk: new ChannelTalkSettings(true, TalkMode.PushToTalk, (int)(Keys.Shift | Keys.F9), false)),
             new(new DeviceChoice("monitor-output", "Headphones"), Talk: new ChannelTalkSettings(false))]));
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
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "self-test.txt"), $"PASS: {checks} mono/stereo impulse delay/gain cases; mute; legacy and per-channel settings restoration; in-memory recording and WAV export; memory limit and export retry; independent toggle/PTT gates, shared chords, key-repeat handling, passthrough/suppression, reconfiguration and missing-release handling.\nUses existing playback endpoints and third-party virtual cables.\n");
    }
}
