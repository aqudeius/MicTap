# MicTap

MicTap is a small Windows microphone monitor and audio splitter. Choose one input, send it to multiple playback devices or installed virtual audio cables, and control each output independently.

## Features

- Independent output volume, mute, and added delay from 0 to 5 seconds.
- Optional open, toggle, or push-to-talk mode on each output.
- Assignable global keyboard bindings with configurable key pass-through.
- Saved input, outputs, volume, delay, and talk settings.
- Temporary recordings kept in memory, with optional WAV export.

## Requirements

- Windows with the .NET 8 Desktop Runtime to run a framework-dependent build.
- The .NET 8 SDK or a later SDK capable of targeting .NET 8 to build from source.
- A microphone or other Windows recording endpoint.
- An installed third-party virtual audio cable if you want other apps to receive a gated audio feed. Headphone monitoring works with ordinary playback devices.

MicTap uses existing Windows audio endpoints. It does not install or expose its own virtual microphone driver.

## Build and run

From the repository root on Windows:

```powershell
dotnet restore MicTap.sln
dotnet build MicTap.sln -c Release --no-restore
dotnet run --project src/MicTap/MicTap.csproj -c Release --no-build
```

You can also open `MicTap.sln` in Visual Studio with the .NET desktop development workload installed.

To publish a folder that uses the installed .NET Desktop Runtime:

```powershell
dotnet publish src/MicTap/MicTap.csproj -c Release --self-contained false -o artifacts/publish
```

Keep the published files and license notices together. `artifacts/`, `bin/`, and `obj/` are ignored by Git. Compiled releases should be distributed separately from source, for example through GitHub Releases.

## Monitor and route audio

1. Choose the original microphone under **Audio input**.
2. Choose headphones under **Output device** and leave added delay at **0** for live monitoring.
3. Use **Add output** to create more routes.
4. Press **Start listening**. Stop listening or close MicTap to release the input.

Use headphones to avoid acoustic feedback. Zero added delay still includes Windows and device latency. If audio crackles, try a larger buffer.

For a virtual microphone feed, choose the cable's **playback endpoint** as a MicTap output. In the receiving app, choose the same cable's **recording endpoint** as its input. A typical setup is:

```text
Microphone -> MicTap -> headphones, continuously open
                    -> virtual cable, controlled by push-to-talk -> receiving app
```

If MicTap's input is already a virtual cable, use a different cable for its outgoing feed. Reading and feeding the same cable can create a loop. MicTap gates only its own output stream; other applications feeding that cable bypass MicTap's controls.

Stop audio before changing the input, an existing output device, or the buffer size. Use **Refresh devices** after reconnecting hardware. A saved device that is unavailable remains selected and is marked disconnected.

## Per-output talk controls

Enable **Talk control** on an output, then choose its mode:

| Mode | Behavior |
| --- | --- |
| Open | Audio passes continuously while the output is active. |
| Toggle | Starts closed. Press the assigned binding, or click **Toggle talk**, to switch between open and closed. |
| Push to talk | Starts closed. Hold the assigned binding to pass audio; release it to close the gate. |

Leave **Talk control** off for outputs that should remain continuously audible. **Mute** overrides the talk mode.

Click the key button to assign a keyboard key with optional Ctrl, Shift, or Alt. The default is Ctrl+Shift+F8. Escape cancels assignment; F12 and Windows keys are reserved. Keys control active routes and do not start audio capture.

Bindings work while another normal desktop app is active or MicTap is minimized. **Pass key to other apps** controls whether the foreground app also receives the trigger key; it does not enable or disable background handling. When pass-through is off, the trigger key's down/up events are consumed, while modifier keys can still reach the foreground app.

Use different bindings for independent control. Shared bindings control every matching active channel. A shared trigger reaches the foreground app only if every matching active channel allows pass-through. Changing one output does not reset other outputs' toggle states.

Toggle and push-to-talk gates reset closed when audio restarts, their mode or binding changes, or the Windows session locks/unlocks. Hotkeys may be unavailable on secure desktops, in protected games, or across elevation boundaries.

## Record and export

Click **Record** to begin a temporary take without choosing a file. Click **Stop recording** to keep it in MicTap, then choose **Save / export** for a WAV file or **Discard take** to release it.

Recording works with or without live listening. It captures the original input before output volume, mute, delay, and talk gates. Muting an output does not mute the recording.

Only one take is retained at a time, up to 512 MB of audio in memory. At the limit, recording stops and preserves the take for export. MicTap prompts before replacing or closing an unsaved take. Temporary takes do not survive app exit, a crash, or power loss; export anything you want to keep.

## Settings and local data

MicTap writes `settings.json` beside its executable. That folder must be writable. Device selections, output settings, and channel bindings are restored on the next launch; audio capture does not start automatically.

The ignore file excludes local settings, WAV recordings, build output, logs, and signing keys. Audio stays on the computer and is written to a recording file only when you export it.

## Checks

After a Release build on Windows:

```powershell
dotnet src/MicTap/bin/Release/net8.0-windows/MicTap.dll --self-test
Get-Content src/MicTap/bin/Release/net8.0-windows/self-test.txt
```

The checks cover delay timing, gain and mute, settings migration, in-memory recording and WAV export, independent and shared bindings, repeat suppression, key pass-through, and missing key releases. They do not activate audio capture.

Background key delivery has also been checked with an isolated native test window. Hardware latency, protected applications, and long-running multi-device routing still need testing on the intended setup.

## Repository layout

```text
MicTap.sln
src/MicTap/             Application and built-in checks
licenses/              Retained dependency license text
LICENSE                MicTap MIT license
THIRD_PARTY_NOTICES.md  Dependency attribution
```

## License

MicTap is available under the [MIT license](LICENSE), copyright 2026 MicTap contributors. NAudio has its own retained MIT notice; see [third-party notices](THIRD_PARTY_NOTICES.md).
