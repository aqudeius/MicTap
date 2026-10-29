using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MicTap;

sealed record ChannelKeyBinding(Guid Id, Keys Key, bool PassThrough, Action<bool> Changed);

// One router dispatches a chord to every matching channel before deciding whether
// the foreground app receives it. Reconfiguring one channel leaves others intact.
sealed class ChannelKeyRouter
{
    sealed class Entry(ChannelKeyBinding binding) { public ChannelKeyBinding Binding = binding; public bool Held; }
    readonly Dictionary<Guid, Entry> entries = [];
    readonly HashSet<int> pressed = [], suppressed = [];
    public void Configure(IEnumerable<ChannelKeyBinding> bindings)
    {
        var next = bindings.ToDictionary(b => b.Id);
        foreach (var id in entries.Keys.ToArray())
        {
            var entry = entries[id];
            if (!next.TryGetValue(id, out var updated) || updated.Key != entry.Binding.Key || updated.Changed != entry.Binding.Changed)
            {
                if (entry.Held) entry.Binding.Changed(false);
                entries.Remove(id);
            }
        }
        foreach (var binding in next.Values)
        {
            if (entries.TryGetValue(binding.Id, out var entry)) entry.Binding = binding;
            else entries.Add(binding.Id, new Entry(binding));
        }
    }
    public void ReleaseAll()
    {
        foreach (var entry in entries.Values) if (entry.Held) { entry.Held = false; entry.Binding.Changed(false); }
        // Keep pressed/suppressed until physical key-up, so a held key cannot
        // re-trigger after a mode change or leak an unmatched key-up to an app.
    }
    bool Any(params int[] keys) => keys.Any(pressed.Contains);
    bool Matches(Keys key) => pressed.Contains((int)(key & Keys.KeyCode))
        && Any(0x11, 0xA2, 0xA3) == key.HasFlag(Keys.Control)
        && Any(0x10, 0xA0, 0xA1) == key.HasFlag(Keys.Shift)
        && Any(0x12, 0xA4, 0xA5) == key.HasFlag(Keys.Alt)
        && !Any(0x5B, 0x5C);
    public bool Handle(int key, bool down)
    {
        bool first = down && !pressed.Contains(key);
        if (down) pressed.Add(key); else pressed.Remove(key);
        bool consume = suppressed.Contains(key);
        foreach (var entry in entries.Values)
        {
            var binding = entry.Binding;
            bool chord = Matches(binding.Key);
            if (first && key == (int)(binding.Key & Keys.KeyCode) && chord)
            {
                entry.Held = true; binding.Changed(true);
            }
            else if (entry.Held && !chord) { entry.Held = false; binding.Changed(false); }
            if (!binding.PassThrough && down && key == (int)(binding.Key & Keys.KeyCode) && chord)
            { suppressed.Add(key); consume = true; }
        }
        if (!down) suppressed.Remove(key);
        return consume;
    }
    public void CheckRelease(Func<int, bool> isDown)
    {
        // Use current state only for missing releases, never as a repeat trigger.
        foreach (int key in pressed.ToArray()) if (!isDown(key)) pressed.Remove(key);
        foreach (var entry in entries.Values)
            if (entry.Held && !Matches(entry.Binding.Key)) { entry.Held = false; entry.Binding.Changed(false); }
    }
}

sealed class GlobalTalkKeys : IDisposable
{
    readonly ChannelKeyRouter router = new();
    readonly Thread thread;
    readonly HookProc callback;
    Control? dispatcher;
    ApplicationContext? context;
    IntPtr hook;
    public GlobalTalkKeys()
    {
        callback = OnKey;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        thread = new Thread(() =>
        {
            try
            {
                dispatcher = new Control(); _ = dispatcher.Handle; context = new ApplicationContext();
                hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
                if (hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                ready.SetResult(); Application.Run(context);
            }
            catch (Exception ex) { ready.TrySetException(ex); }
            finally { if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook); dispatcher?.Dispose(); }
        }) { IsBackground = true, Name = "MicTap channel talk keys" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); ready.Task.GetAwaiter().GetResult();
    }
    void Post(Action action)
    {
        try { if (dispatcher?.IsHandleCreated == true) dispatcher.BeginInvoke(action); } catch (InvalidOperationException) { }
    }
    public void Configure(ChannelKeyBinding[] bindings) => Post(() => router.Configure(bindings));
    public void ReleaseAll() => Post(router.ReleaseAll);
    public void CheckRelease() => Post(() => router.CheckRelease(k => (GetAsyncKeyState(k) & 0x8000) != 0));
    IntPtr OnKey(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            bool down = message == (IntPtr)0x100 || message == (IntPtr)0x104;
            bool up = message == (IntPtr)0x101 || message == (IntPtr)0x105;
            if ((down || up) && router.Handle(Marshal.ReadInt32(data), down)) return (IntPtr)1;
        }
        return CallNextHookEx(hook, code, message, data);
    }
    public void Dispose() { Post(() => context?.ExitThread()); thread.Join(2000); }
    delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
}
