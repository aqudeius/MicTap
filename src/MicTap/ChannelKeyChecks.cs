using NAudio.Wave;

namespace MicTap;

static class ChannelKeyChecks
{
    sealed class ConstantSource : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        public int Read(float[] buffer, int offset, int count) { Array.Fill(buffer, 1f, offset, count); return count; }
    }
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run()
    {
        var a = new TalkGate { Active = true }; a.Configure(TalkMode.Toggle);
        var b = new TalkGate { Active = true }; b.Configure(TalkMode.PushToTalk);
        var open = new TalkGate { Active = true }; open.Configure(TalkMode.Open);
        var router = new ChannelKeyRouter();
        var ka = new ChannelKeyBinding(Guid.NewGuid(), Keys.Control | Keys.F8, true, a.SetHeld);
        var kb = new ChannelKeyBinding(Guid.NewGuid(), Keys.Control | Keys.F9, false, b.SetHeld);
        router.Configure([ka, kb]);
        Require(!router.Handle((int)Keys.ControlKey, true), "Modifier should pass through");
        Require(!router.Handle((int)Keys.F8, true) && a.IsOpen && !b.IsOpen && open.IsOpen, "Independent toggle or passthrough failed");
        router.Handle((int)Keys.F8, true); Require(a.IsOpen, "Repeat toggled a channel twice");
        router.Handle((int)Keys.F8, false); Require(a.IsOpen, "Release changed toggle state");
        Require(router.Handle((int)Keys.F9, true) && b.IsOpen && a.IsOpen, "PTT or key suppression failed");
        Require(router.Handle((int)Keys.F9, false) && !b.IsOpen && a.IsOpen, "PTT release or suppression failed");
        router.Configure([ka, kb with { PassThrough = true }]);
        Require(a.IsOpen, "Editing another channel reset a toggle");
        router.Handle((int)Keys.F9, true); Require(b.IsOpen, "Reconfigured PTT failed");
        router.Configure([ka]); Require(!b.IsOpen && a.IsOpen, "Removing a held channel changed another channel");
        router.Handle((int)Keys.F9, false);
        // A shared chord must reach both channels even when one consumes it.
        router.Configure([ka, kb with { Key = ka.Key }]);
        Require(router.Handle((int)Keys.F8, true) && !a.IsOpen && b.IsOpen, "Shared chord did not reach all channels");
        router.Handle((int)Keys.ControlKey, false); Require(!b.IsOpen, "Releasing a required modifier left PTT open");
        Require(router.Handle((int)Keys.F8, false), "Suppressed key-up leaked after modifier release");
        // A missing key-up closes PTT, but the eventual real key-up stays suppressed.
        router.Handle((int)Keys.ControlKey, true); router.Handle((int)Keys.F8, true);
        Require(b.IsOpen, "PTT did not reopen on a new chord");
        router.CheckRelease(_ => false); Require(!b.IsOpen, "Missing-release watchdog left PTT open");
        Require(router.Handle((int)Keys.F8, false), "Watchdog lost suppressed key-up tracking");
        // Suppression remains paired even if an output is removed mid-press.
        router.Handle((int)Keys.ControlKey, true); router.Handle((int)Keys.F8, true);
        router.Configure([]); Require(router.Handle((int)Keys.F8, false), "Removing a channel leaked an unmatched key-up");
        router.Handle((int)Keys.ControlKey, false);
        Require(!router.Handle((int)Keys.A, true) && !router.Handle((int)Keys.A, false), "Unrelated key was consumed");
        // Closing one channel must not silence another output's audio samples.
        a.Configure(TalkMode.PushToTalk); b.Configure(TalkMode.Open);
        Require(!a.IsOpen && b.IsOpen && open.IsOpen, "Channel gate isolation failed");
        var cable = new DelayLine(new ConstantSource()) { Gain = 1, Gate = () => a.IsOpen };
        var monitor = new DelayLine(new ConstantSource()) { Gain = 1, Gate = () => b.IsOpen };
        float[] cableAudio = new float[960], monitorAudio = new float[960];
        cable.Read(cableAudio, 0, 960); monitor.Read(monitorAudio, 0, 960);
        Require(cableAudio.All(x => x == 0) && monitorAudio.All(x => x == 1), "Closing a cable channel changed the monitor audio");
        a.SetHeld(true); cable.Read(cableAudio, 0, 960);
        Require(cableAudio.All(x => x == 1), "Opening a channel did not restore its audio");
    }
}
