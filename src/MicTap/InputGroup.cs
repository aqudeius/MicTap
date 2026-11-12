namespace MicTap;

sealed class InputGroup : TabPage
{
    public readonly ComboBox Device = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 500, AccessibleName = "Audio input" };
    public readonly ComboBox Buffer = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 115, AccessibleName = "Audio buffer" };
    public readonly Button AddOutput = new() { Text = "+ Add output", Width = 130, Height = 32 };
    public readonly Button StartStop = new() { Width = 155, Height = 38, BackColor = Color.FromArgb(29, 91, 220), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Anchor = AnchorStyles.Top | AnchorStyles.Right };
    public readonly Label Status = new() { Height = 27, Dock = DockStyle.Top, AutoEllipsis = true, Text = "Stopped · choose an input and its outputs." };
    public readonly FlowLayoutPanel Rows = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
    readonly ProgressBar meter = new() { Maximum = 100, Width = 160, Height = 12, Location = new Point(0, 30), AccessibleName = "Input level" };

    public string GroupName { get; }
    public InputSession? Session { get; set; }
    public bool Monitoring { get; set; }
    public IEnumerable<OutputRow> Outputs => Rows.Controls.Cast<OutputRow>();
    public int BufferMilliseconds => Buffer.SelectedIndex switch { 0 => 20, 2 => 100, _ => 50 };

    public InputGroup(int number)
    {
        GroupName = $"Input {number}";
        BackColor = Color.White;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8), RowCount = 4, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 65));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));

        var source = new Panel { Dock = DockStyle.Fill };
        source.Controls.Add(new Label { Text = "AUDIO INPUT", AutoSize = true });
        Device.Location = new Point(0, 24);
        source.Controls.Add(Device);
        source.Controls.Add(new Label { Text = "Buffer", AutoSize = true, Location = new Point(520, 0) });
        Buffer.Items.AddRange(["20 ms", "50 ms", "100 ms"]);
        Buffer.SelectedIndex = 1;
        Buffer.Location = new Point(520, 24);
        source.Controls.Add(Buffer);
        layout.Controls.Add(source, 0, 0);
        layout.Controls.Add(AddOutput, 0, 1);
        layout.Controls.Add(Rows, 0, 2);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165));
        var info = new Panel { Dock = DockStyle.Fill };
        info.Controls.Add(Status);
        info.Controls.Add(meter);
        footer.Controls.Add(info, 0, 0);
        footer.Controls.Add(StartStop, 1, 0);
        layout.Controls.Add(footer, 0, 3);
        Controls.Add(layout);
        UpdateSessionControls();
    }

    public void UpdateSessionControls()
    {
        bool active = Session != null;
        Text = active ? GroupName + " · active" : GroupName;
        Device.Enabled = Buffer.Enabled = !active;
        StartStop.Text = !active ? "Start listening" : Monitoring ? "Stop listening" : "Stop audio";
        if (!active) meter.Value = 0;
    }

    public void UpdateMeter()
    {
        try { meter.Value = Math.Clamp((int)((Session?.Peak ?? 0) * 100), 0, 100); }
        catch { meter.Value = 0; }
    }
}
