
namespace MicTap;

enum TalkMode { Open, Toggle, PushToTalk }
// Retained only to import settings from older versions, including the unused driver flag.
sealed record TalkSettings(TalkMode Mode = TalkMode.Open, int Binding = (int)(Keys.Control | Keys.Shift | Keys.F8), bool PassThrough = true, bool GateMonitors = false, bool VirtualMicEnabled = false);
sealed record ChannelTalkSettings(bool Enabled = false, TalkMode Mode = TalkMode.Open, int Binding = (int)(Keys.Control | Keys.Shift | Keys.F8), bool PassThrough = true);

sealed class TalkGate
{
    readonly object sync = new();
    int open = 1;
    public volatile bool Active;
    public bool IsOpen => Active && Volatile.Read(ref open) != 0;
    public TalkMode Mode { get; private set; }
    bool held;
    public void Configure(TalkMode mode) { lock (sync) { Mode = mode; held = false; Volatile.Write(ref open, mode == TalkMode.Open ? 1 : 0); } }
    public void SetHeld(bool down)
    {
        lock (sync)
        {
            if (Mode == TalkMode.PushToTalk) Volatile.Write(ref open, down ? 1 : 0);
            else if (Mode == TalkMode.Toggle && down && !held) Volatile.Write(ref open, Volatile.Read(ref open) == 0 ? 1 : 0);
            held = down;
        }
    }
    public void Toggle() { lock (sync) if (Mode == TalkMode.Toggle) Volatile.Write(ref open, Volatile.Read(ref open) == 0 ? 1 : 0); }
    public void Reset() { lock (sync) { held = false; Volatile.Write(ref open, Mode == TalkMode.Open ? 1 : 0); } }
}

static class GlobalTalkKey
{
    public static bool Valid(Keys binding)
    {
        int key = (int)(binding & Keys.KeyCode);
        return key > 0 && key < 256 && key is not 0x10 and not 0x11 and not 0x12 and not 0xA0 and not 0xA1 and not 0xA2 and not 0xA3 and not 0xA4 and not 0xA5 and not 0x5B and not 0x5C and not 0x7B;
    }
    public static string Display(Keys binding) => new KeysConverter().ConvertToString(binding) ?? "Unassigned";
}

sealed class BindKeyDialog : Form
{
    public Keys SelectedBinding { get; private set; }
    public BindKeyDialog(Keys current)
    {
        SelectedBinding = current; Text = "Assign talk key"; ClientSize = new Size(460, 105); FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent; MaximizeBox = MinimizeBox = false; KeyPreview = true;
        Controls.Add(new Label { Text = "Press a key or a Ctrl / Shift / Alt combination.\nEscape cancels. F12 and Windows keys are reserved.", Dock = DockStyle.Fill, Padding = new Padding(18), Font = new Font("Segoe UI", 10) });
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
        if (GlobalTalkKey.Valid(keyData)) { SelectedBinding = keyData; DialogResult = DialogResult.OK; Close(); return true; }
        return true;
    }
}
