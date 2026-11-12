using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MicTap;

sealed class MonitorForm : Form
{
    readonly TabControl tabs = new() { Dock = DockStyle.Fill, AccessibleName = "Input groups" };
    readonly Button addInput = new() { Text = "+ Add input", Width = 120, Height = 34 };
    readonly Button removeInput = new() { Text = "Remove input", Width = 130, Height = 34, Enabled = false };
    readonly Button refresh = new() { Text = "Refresh devices", Width = 140, Height = 34 };
    readonly Button record = new() { Text = "Record", Width = 155, Height = 34 };
    readonly Button export = new() { Text = "Save / export…", Width = 155, Height = 34, Enabled = false };
    readonly Button discard = new() { Text = "Discard take", Width = 130, Height = 34, Enabled = false };
    readonly Label recordingStatus = new() { Text = "Record a temporary take from the selected input.", AutoEllipsis = true, Width = 820, Height = 42 };
    readonly System.Windows.Forms.Timer keyTimer = new() { Interval = 15 };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 60 };
    readonly TalkCuePlayer talkCues = new();
    GlobalTalkKeys? talkKeys;
    bool hookFailed, sessionLocked, assigningKey;
    bool loadingSettings = true;
    DeviceChoice[] inputs = [], outputs = [];
    int nextGroupNumber = 1;

    Recording? recording;
    Recording? take;
    InputGroup? recordingGroup;
    bool recordingOnly;
    bool exporting, closingApproved, closingInProgress;

    IEnumerable<InputGroup> Groups => tabs.TabPages.Cast<InputGroup>();
    InputGroup? SelectedGroup => tabs.SelectedTab as InputGroup;

    public MonitorForm()
    {
        Text = "MicTap — live audio monitor";
        ClientSize = new Size(940, 790);
        MinimumSize = new Size(950, 670);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.White;
        StartPosition = FormStartPosition.CenterScreen;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(26), RowCount = 4, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 95));
        var heading = new Panel { Dock = DockStyle.Fill };
        heading.Controls.Add(new Label { Text = "MicTap", Font = new Font("Segoe UI", 24, FontStyle.Bold), AutoSize = true });
        heading.Controls.Add(new Label { Text = "Route each input to its own outputs.", Location = new Point(2, 46), AutoSize = true, ForeColor = Color.DimGray });
        layout.Controls.Add(heading, 0, 0);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        toolbar.Controls.AddRange([addInput, removeInput, refresh]);
        toolbar.Controls.Add(new Label { Text = "Use headphones to avoid feedback.", AutoSize = true, Margin = new Padding(12, 11, 0, 0) });
        layout.Controls.Add(toolbar, 0, 1);
        layout.Controls.Add(tabs, 0, 2);

        var recordingPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
        record.Location = new Point(0, 8);
        export.Location = new Point(170, 8);
        discard.Location = new Point(340, 8);
        recordingStatus.Location = new Point(0, 49);
        recordingPanel.Controls.AddRange([record, export, discard, recordingStatus]);
        layout.Controls.Add(recordingPanel, 0, 3);
        Controls.Add(layout);

        addInput.Click += (_, _) => AddInput();
        removeInput.Click += (_, _) =>
        {
            if (SelectedGroup is { } group && tabs.TabPages.Count > 1) RemoveInput(group);
        };
        refresh.Click += (_, _) => RefreshDevices();
        tabs.SelectedIndexChanged += (_, _) => removeInput.Enabled = tabs.TabPages.Count > 1;
        record.Click += async (_, _) => await ToggleRecordingAsync();
        export.Click += async (_, _) => await ExportTakeAsync();
        discard.Click += (_, _) =>
        {
            if (take == null || recording != null || exporting) return;
            if (MessageBox.Show(this, "Discard this temporary take?", "MicTap", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            take = null;
            UpdateTakeControls();
            recordingStatus.Text = "Take discarded. Ready to record.";
        };

        timer.Tick += (_, _) =>
        {
            SelectedGroup?.UpdateMeter();
            if (recording is not { } session) return;
            if (session.Error != null) StopTake();
            else recordingStatus.Text = $"Recording {recordingGroup!.GroupName} · {TakeSummary(session)}\n{recordingGroup.Device.SelectedItem}";
        };
        keyTimer.Tick += (_, _) =>
        {
            talkKeys?.CheckRelease();
            if (SelectedGroup is { } group)
                foreach (var row in group.Outputs) row.UpdateTalkState();
        };

        FormClosing += async (_, e) =>
        {
            if (closingApproved) return;
            e.Cancel = true;
            if (exporting || closingInProgress) return;
            closingInProgress = true;
            try
            {
                await Task.Yield();
                SaveSettings();
                foreach (var group in Groups) StopInput(group);
                if (await ConfirmReleaseTakeAsync("closing MicTap")) { closingApproved = true; Close(); }
            }
            finally { closingInProgress = false; }
        };
        FormClosed += (_, _) =>
        {
            take = null;
            timer.Dispose();
            keyTimer.Dispose();
            talkKeys?.Dispose();
            talkCues.Dispose();
            Microsoft.Win32.SystemEvents.SessionSwitch -= SessionChanged;
        };
        Shown += (_, _) =>
        {
            AppSettings? saved = null;
            string? error = null;
            try { saved = SettingsStore.LoadCurrent(); }
            catch (Exception ex) { error = "Could not restore settings: " + ex.Message; }
            RestoreSettings(saved);
            try { talkKeys = new GlobalTalkKeys(); }
            catch (Exception ex) { hookFailed = true; SetStatus("Global talk key unavailable: " + ex.Message); }
            keyTimer.Start();
            timer.Start();
            Microsoft.Win32.SystemEvents.SessionSwitch += SessionChanged;
            if (error != null) SetStatus(error);
        };
    }

    void SetStatus(string message)
    {
        if (SelectedGroup is { } group) group.Status.Text = message;
        else recordingStatus.Text = message;
    }

    void SessionChanged(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason != Microsoft.Win32.SessionSwitchReason.SessionLock && e.Reason != Microsoft.Win32.SessionSwitchReason.SessionUnlock) return;
        if (!IsHandleCreated || IsDisposed) return;
        BeginInvoke(() =>
        {
            sessionLocked = e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock;
            foreach (var row in Groups.SelectMany(group => group.Outputs))
            {
                row.TalkGate.Active = row.Active != null && !sessionLocked;
                row.TalkGate.Reset();
            }
            ConfigureChannelKeys();
        });
    }

    void ConfigureChannelKeys()
    {
        if (talkKeys == null) return;
        var bindings = Groups.SelectMany(group => group.Outputs)
            .Where(row => !assigningKey && !sessionLocked && row.Active != null && row.TalkEnabled.Checked && row.TalkModeBox.SelectedIndex != 0)
            .Select(row => new ChannelKeyBinding(row.ChannelId, row.Binding, row.PassKey.Checked, row.KeyChanged)).ToArray();
        talkKeys.Configure(bindings);
    }

    AppSettings ReadSettings() => new(Groups.Select(group => new InputSettings(
        group.Device.SelectedItem as DeviceChoice, group.Buffer.SelectedIndex,
        group.Outputs.Select(row => new OutputSettings(row.Device.SelectedItem as DeviceChoice,
            row.Volume.Value, (int)row.Delay.Value, row.Mute.Checked, row.ReadTalk(), row.MonoMix.Checked)).ToArray())).ToArray());

    void SaveSettings()
    {
        if (loadingSettings) return;
        try { SettingsStore.Save(SettingsStore.PathName, ReadSettings()); }
        catch (Exception ex) { SetStatus("Settings could not be saved: " + ex.Message); }
    }

    void RestoreSettings(AppSettings? saved)
    {
        loadingSettings = true;
        RefreshDevices();
        foreach (var group in (saved?.Inputs ?? []).OfType<InputSettings>()) AddInput(group);
        if (tabs.TabPages.Count == 0) AddInput(chooseFirstDevice: true);
        tabs.SelectedIndex = 0;
        loadingSettings = false;
    }

    void RefreshDevices()
    {
        bool previousLoading = loadingSettings;
        loadingSettings = true;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            DeviceChoice[] List(DataFlow flow) => enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)
                .Select(device => { using (device) return new DeviceChoice(device.ID, device.FriendlyName); }).ToArray();
            var refreshedInputs = List(DataFlow.Capture);
            var refreshedOutputs = List(DataFlow.Render);
            inputs = refreshedInputs;
            outputs = refreshedOutputs;
            foreach (var group in Groups)
            {
                if (group.Session == null)
                    SettingsStore.SelectDevice(group.Device, inputs, group.Device.SelectedItem as DeviceChoice, selectFirst: false);
                foreach (var row in group.Outputs.Where(row => row.Active == null))
                    SettingsStore.SelectDevice(row.Device, outputs, row.Device.SelectedItem as DeviceChoice, selectFirst: false);
            }
        }
        catch (Exception ex) { SetStatus("Cannot list audio devices: " + ex.Message); }
        finally { loadingSettings = previousLoading; }
    }

    InputGroup AddInput(InputSettings? saved = null, bool chooseFirstDevice = false)
    {
        var group = new InputGroup(nextGroupNumber++);
        SettingsStore.SelectDevice(group.Device, inputs, saved?.Input, selectFirst: chooseFirstDevice);
        if (saved != null) group.Buffer.SelectedIndex = Math.Clamp(saved.BufferIndex, 0, 2);
        if (saved == null) AddOutput(group);
        else foreach (var output in (saved.Outputs ?? []).OfType<OutputSettings>()) AddOutput(group, output);

        group.Device.SelectedIndexChanged += (_, _) => SaveSettings();
        group.Buffer.SelectedIndexChanged += (_, _) => SaveSettings();
        group.AddOutput.Click += (_, _) => { AddOutput(group); SaveSettings(); };
        group.StartStop.Click += (_, _) =>
        {
            if (group.Session != null) StopInput(group);
            else StartInput(group);
        };
        tabs.TabPages.Add(group);
        tabs.SelectedTab = group;
        removeInput.Enabled = tabs.TabPages.Count > 1;
        SaveSettings();
        return group;
    }

    void RemoveInput(InputGroup group)
    {
        StopInput(group);
        tabs.TabPages.Remove(group);
        group.Dispose();
        removeInput.Enabled = tabs.TabPages.Count > 1;
        SaveSettings();
    }

    void AddOutput(InputGroup group, OutputSettings? saved = null)
    {
        var row = new OutputRow(outputs, talkCues.Enqueue);
        group.Rows.Controls.Add(row);
        if (saved != null)
        {
            SettingsStore.SelectDevice(row.Device, outputs, saved.Device, selectFirst: false);
            row.Volume.Value = Math.Clamp(saved.Volume, 0, 100);
            row.Delay.Value = Math.Clamp(saved.Delay, 0, 5000);
            row.Mute.Checked = saved.Muted;
            row.MonoMix.Checked = saved.MonoMix;
            var talk = saved.Talk ?? new ChannelTalkSettings();
            row.TalkEnabled.Checked = talk.Enabled;
            row.TalkModeBox.SelectedIndex = Math.Clamp((int)talk.Mode, 0, 2);
            row.Binding = GlobalTalkKey.Valid((Keys)talk.Binding) ? (Keys)talk.Binding : Keys.Control | Keys.Shift | Keys.F8;
            row.PassKey.Checked = talk.PassThrough;
            row.GateBeeps.Checked = talk.BeepOnChange;
            row.ConfigureTalk();
        }
        else if (group.Monitoring) row.Device.SelectedIndex = -1;

        row.Remove.Click += (_, _) =>
        {
            StopOutput(group, row);
            group.Rows.Controls.Remove(row);
            ConfigureChannelKeys();
            row.Dispose();
            SaveSettings();
        };
        void TalkChanged() { row.ConfigureTalk(); ConfigureChannelKeys(); SaveSettings(); }
        row.TalkEnabled.CheckedChanged += (_, _) => TalkChanged();
        row.TalkModeBox.SelectedIndexChanged += (_, _) => TalkChanged();
        row.PassKey.CheckedChanged += (_, _) => { ConfigureChannelKeys(); SaveSettings(); };
        row.GateBeeps.CheckedChanged += (_, _) => SaveSettings();
        row.BindKey.Click += (_, _) =>
        {
            assigningKey = true;
            ConfigureChannelKeys();
            talkKeys?.ReleaseAll();
            try
            {
                using var dialog = new BindKeyDialog(row.Binding);
                if (dialog.ShowDialog(this) == DialogResult.OK) row.Binding = dialog.SelectedBinding;
            }
            finally { assigningKey = false; TalkChanged(); }
        };
        row.Device.SelectedIndexChanged += (_, _) =>
        {
            if (!loadingSettings && group.Monitoring && row.Active == null && row.Device.SelectedItem != null)
            {
                try { OpenRoute(group, row); ConfigureChannelKeys(); }
                catch (Exception ex) { group.Status.Text = "Output failed: " + ex.Message; }
            }
            SaveSettings();
        };
        row.Volume.ValueChanged += (_, _) => SaveSettings();
        row.Delay.ValueChanged += (_, _) => SaveSettings();
        row.Mute.CheckedChanged += (_, _) => SaveSettings();
        row.MonoMix.CheckedChanged += (_, _) => SaveSettings();
    }

    void OpenRoute(InputGroup group, OutputRow row)
    {
        var session = group.Session!;
        if (row.Device.SelectedItem is not DeviceChoice choice) throw new InvalidOperationException("Choose a device for every output in this group.");
        if (!choice.Available) throw new InvalidOperationException("An output is disconnected. Reconnect it and refresh devices, or choose another output.");
        if (hookFailed && row.TalkEnabled.Checked && row.TalkModeBox.SelectedIndex != 0)
            throw new InvalidOperationException("Global talk keys are unavailable. Restart MicTap or disable talk control on this output.");

        using var enumerator = new MMDeviceEnumerator();
        var route = new Route(enumerator.GetDevice(choice.Id), session.Format, group.BufferMilliseconds, ex => ReportFailure(group, session, ex));
        try
        {
            row.Active = route;
            row.UpdateRoute();
            row.ConfigureTalk();
            row.TalkGate.Active = !sessionLocked;
            route.Delay.Gate = () => row.IsAudible;
            session.AttachOutput(route.Push);
            route.Start();
            row.Device.Enabled = false;
        }
        catch { StopOutput(group, row); throw; }
    }

    bool StartInput(InputGroup group, bool monitor = true)
    {
        try
        {
            if (group.Device.SelectedItem is not DeviceChoice choice) throw new InvalidOperationException("Choose an audio input for this group.");
            if (!choice.Available) throw new InvalidOperationException("This input is disconnected. Reconnect it and refresh devices, or choose another input.");
            if (monitor && !group.Outputs.Any()) throw new InvalidOperationException("Add at least one output to this group.");

            var session = InputSession.Open(choice.Id, group.BufferMilliseconds);
            group.Session = session;
            session.Failed += ex => ReportFailure(group, session, ex);
            if (monitor) foreach (var row in group.Outputs) OpenRoute(group, row);
            session.Start();
            group.Monitoring = monitor;
            group.UpdateSessionControls();
            ConfigureChannelKeys();
            group.Status.Text = monitor ? "Listening · audio stays on this computer." : "Input active for recording.";
            return true;
        }
        catch (Exception ex)
        {
            StopInput(group);
            group.Status.Text = "Could not start: " + ex.Message;
            MessageBox.Show(this, ex.Message, $"MicTap · {group.GroupName}", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    void ReportFailure(InputGroup group, InputSession session, Exception error)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                if (group.Session != session) return;
                StopInput(group);
                group.Status.Text = "Audio stopped: " + error.Message;
            });
        }
        catch (InvalidOperationException) { }
    }

    void StopOutput(InputGroup group, OutputRow row)
    {
        row.TalkGate.Active = false;
        row.TalkGate.Reset();
        if (row.Active is { } route)
        {
            group.Session?.DetachOutput(route.Push);
            row.Active = null;
            try { route.Dispose(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        }
        row.Device.Enabled = true;
        row.UpdateTalkState();
    }

    void StopInput(InputGroup group)
    {
        if (group.Session is not { } session) return;
        if (recordingGroup == group) FinishRecording();
        group.Session = null;
        group.Monitoring = false;
        try { session.Dispose(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        foreach (var row in group.Outputs) StopOutput(group, row);
        ConfigureChannelKeys();
        group.UpdateSessionControls();
        group.Status.Text = "Stopped · input released.";
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
        exporting = true;
        UpdateTakeControls();
        recordingStatus.Text = "Exporting take…";
        try
        {
            await Task.Run(() => session.Export(dialog.FileName));
            recordingStatus.Text = "Exported: " + dialog.FileName + "\nThe take is still available in MicTap until discarded or replaced.";
            return true;
        }
        catch (Exception ex)
        {
            recordingStatus.Text = "Export failed: " + ex.Message + "\nYour take is still in MicTap. You can try exporting again.";
            return false;
        }
        finally { exporting = false; UpdateTakeControls(); }
    }

    async Task ToggleRecordingAsync()
    {
        if (exporting || closingInProgress) return;
        if (recording != null) { StopTake(); return; }
        if (SelectedGroup is not { } group || !await ConfirmReleaseTakeAsync("starting a new recording")) return;
        if (group.IsDisposed || IsDisposed || closingInProgress || recording != null) return;

        bool startedCapture = group.Session == null;
        if (startedCapture && !StartInput(group, monitor: false)) return;
        try { BeginTake(group, startedCapture); }
        catch (Exception ex)
        {
            if (startedCapture) StopInput(group);
            recordingStatus.Text = "Could not record: " + ex.Message;
        }
    }

    void BeginTake(InputGroup group, bool startedCapture)
    {
        recording = take = group.Session!.BeginTake();
        recordingGroup = group;
        recordingOnly = startedCapture;
        UpdateTakeControls();
        recordingStatus.Text = $"Recording {group.GroupName} · temporary in app";
    }

    void StopTake()
    {
        var captureOnlyGroup = recordingOnly ? recordingGroup : null;
        FinishRecording();
        if (captureOnlyGroup != null) StopInput(captureOnlyGroup);
    }

    void FinishRecording()
    {
        if (recording is not { } session) return;
        recordingGroup!.Session!.FinishTake();
        recording = null;
        recordingGroup = null;
        recordingOnly = false;
        UpdateTakeControls();
        recordingStatus.Text = session.Error == null
            ? "Take ready • " + TakeSummary(session) + " • not exported\nKept temporarily in MicTap. Save/export or discard when ready."
            : "Take preserved • " + TakeSummary(session) + "\n" + session.Error.Message;
    }

    internal static void CheckSettingsRestore(AppSettings saved)
    {
        using var form = new MonitorForm();
        form.RestoreSettings(saved);
        var actual = form.ReadSettings();
        if (actual.Inputs.Length != saved.Inputs.Length || form.Groups.Any(group => group.Session != null))
            throw new Exception("Input group count or stopped-state restoration failed");
        for (int i = 0; i < actual.Inputs.Length; i++)
        {
            var a = actual.Inputs[i];
            var b = saved.Inputs[i];
            if (a.Input?.Id != b.Input?.Id || a.BufferIndex != b.BufferIndex || a.Outputs.Length != b.Outputs.Length)
                throw new Exception("Input device, buffer, or output count restoration failed");
            for (int j = 0; j < a.Outputs.Length; j++)
            {
                var x = a.Outputs[j];
                var y = b.Outputs[j];
                if (x.Device?.Id != y.Device?.Id || x.Volume != y.Volume || x.Delay != y.Delay || x.Muted != y.Muted || x.MonoMix != y.MonoMix)
                    throw new Exception("Output settings restoration failed");
                if (x.Talk != (y.Talk ?? new ChannelTalkSettings())) throw new Exception("Channel talk settings restoration failed");
            }
        }

        // Exercise the same add/remove methods as the group and output buttons.
        form.loadingSettings = true;
        var originalCounts = form.Groups.Select(group => group.Outputs.Count()).ToArray();
        var extra = form.AddInput();
        if (extra.Device.SelectedItem != null || extra.Outputs.Count() != 1 || form.SelectedGroup != extra)
            throw new Exception("A new input should be selected, unassigned, and start with one output");
        form.AddOutput(extra);
        if (extra.Outputs.Count() != 2 || !form.Groups.Take(originalCounts.Length).Select(group => group.Outputs.Count()).SequenceEqual(originalCounts))
            throw new Exception("Adding an output changed a different group");
        form.RemoveInput(extra);
        if (!form.Groups.Select(group => group.Outputs.Count()).SequenceEqual(originalCounts))
            throw new Exception("Removing an input changed a different group");

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
        var first = form.AddInput();
        var second = form.AddInput();
        var firstCapture = new TestCapture(format);
        var secondCapture = new TestCapture(format);
        first.Session = new InputSession(firstCapture);
        second.Session = new InputSession(secondCapture);
        var firstOutput = new List<byte>();
        var secondOutput = new List<byte>();
        var extraOutput = new List<byte>();
        first.Session.AttachOutput((buffer, count) => firstOutput.AddRange(buffer.AsSpan(0, count).ToArray()));
        first.Session.AttachOutput((buffer, count) => extraOutput.AddRange(buffer.AsSpan(0, count).ToArray()));
        second.Session.AttachOutput((buffer, count) => secondOutput.AddRange(buffer.AsSpan(0, count).ToArray()));
        first.Session.Start();
        second.Session.Start();
        form.BeginTake(first, startedCapture: false);
        form.tabs.SelectedTab = second;
        byte[] otherSamples = Enumerable.Repeat((byte)127, samples.Length).ToArray();
        secondCapture.Emit(otherSamples);
        form.StopInput(second);
        if (form.recordingGroup != first || !firstCapture.Active || secondCapture.Active)
            throw new Exception("Stopping one input stopped a different group or its recording");
        firstCapture.Emit(samples);
        form.StopInput(first);
        if (!firstOutput.SequenceEqual(samples) || !extraOutput.SequenceEqual(samples) || !secondOutput.SequenceEqual(otherSamples))
            throw new Exception("Audio crossed between groups or failed to reach all outputs");
        if (form.recording != null || form.take == null || !form.take.IsComplete || form.take.Exported || !form.export.Enabled || !form.discard.Enabled || File.Exists(path))
            throw new Exception("Stopping the recorded input did not retain the take without exporting");
        form.take.Export(path);
        if (!form.take.Exported || form.take.Bytes != samples.Length)
            throw new Exception("Export did not retain the selected input's take");
    }

    sealed class TestCapture(WaveFormat format) : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = format;
        public bool Active { get; private set; }
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public void StartRecording() => Active = true;
        public void StopRecording() { Active = false; RecordingStopped?.Invoke(this, new StoppedEventArgs()); }
        public void Emit(byte[] data) { if (Active) DataAvailable?.Invoke(this, new WaveInEventArgs(data, data.Length)); }
        public void Dispose() => StopRecording();
    }
}
