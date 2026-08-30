# Slate for Windows

A floating panel at the top of the screen: hover it and it glows, click and it opens onto
now-playing controls, a file shelf, and clipboard history.

A port of the macOS [Slate](../Slate). **Written without a Windows machine to run it on —
see [Untested](#untested) before you judge it.**

---

## Build and run

Requires the .NET 8 SDK. Inside Windows:

```powershell
winget install Microsoft.DotNet.SDK.8
```

Then, from the project folder:

```powershell
dotnet run
```

To produce something you can double-click:

```powershell
dotnet publish -c Release -r win-arm64 --self-contained false
```

Use `-r win-x64` on an Intel/AMD machine. The output lands in
`bin\Release\net8.0-windows10.0.19041.0\<rid>\publish\Slate.exe`.

There is no installer and nothing is written to the registry. Slate lives in the system
tray; quit it from there.

### Testing it from a Mac

This project was written on an Apple Silicon Mac, so the VM has to run **Windows 11
ARM64** — Microsoft publishes that ISO officially and free. Any of these work:

| | Cost | Notes |
|---|---|---|
| **VMware Fusion** | Free for personal use | The sensible default |
| **UTM** | Free | Slower, but genuinely free forever |
| **Parallels** | 14-day trial, then a subscription | Smoothest, best integration |

Give the VM ~64GB of disk and 8GB of RAM. Drag the `SlateWin` folder into the VM, install
the .NET SDK, and `dotnet run`.

## Dependencies

One: **NAudio**, for the volume mixer. That part of Windows is COM — `IMMDeviceEnumerator`,
`IAudioSessionManager2`, `ISimpleAudioVolume` — and hand-written interop means
hand-written vtables, where the wrong order is a crash rather than a compile error. That
is a poor bet in code that cannot be run here, so the mature wrapper wins. Everything
else is the framework.

## Untested

Every line of the macOS build was run and measured on the machine it was written for.
Bugs were found by *measuring*, not by reading: a spurious mouse-exit that closed the
panel the instant it opened, a Chrome tab id that was secretly a string so every cache
lookup silently missed, a tab that sat invisibly behind the camera housing while looking
perfectly correct in a screenshot.

None of that happened here. This code has never been compiled or executed. Expect to be
the one who finds the first round of problems, and expect some of them to be dull —
a missing using, a null on first paint, a layout that is 4pt out.

`SLATE_DEBUG_LOG=1` prints to stderr; add `SLATE_DEBUG_FILE=C:\slate.log` to also write a
file, and `SLATE_DEBUG_OPEN=1` to expand the panel on launch so you are not fighting the
hover to look at something.

## What is different from the Mac version

**There is no notch.** The premise the Mac build rests on — a piece of hardware already
occupying that space — does not exist here. The panel is a slim bar that is always
visible at the top centre of the screen. That also means the concave flares are gone:
on a Mac they marry the panel to the bezel corners, and here there is nothing to marry
it to, so the bar simply has rounded bottom corners.

**Now Playing is dramatically simpler, and better.** Windows publishes one system-wide
API — `GlobalSystemMediaTransportControlsSessionManager` — that reports title, artist,
artwork, position and playback state for *every* app that registers transport controls,
and accepts play/pause/next/previous/seek back. It is public, documented and
event-driven.

The macOS build needs a private framework Apple has since gated shut, per-app AppleScript
for Spotify and Music, and JavaScript evaluated inside browser tabs behind a setting the
user has to switch on by hand — plus a tab cache, a rate-limited sweep and three
different failure modes to tell apart. All of that collapses into one class here.

**Per-app volume is real, and it works for everything.** The panel's volume slider
carries an **APP / SYS** tag saying which level it is moving.

Windows has a per-process mixer built into the OS: every app that makes noise gets an
audio session, and `ISimpleAudioVolume` sets it. That covers Discord, Premiere, a game —
anything — not merely apps Slate knows how to talk to.

macOS has no equivalent. SoundSource and Background Music each ship a virtual audio
driver to fake one, and the macOS build of Slate settles for asking Spotify, Music and
browser tabs to turn *themselves* down through their own scripting APIs. Everything else
there is stuck with the system slider.

System output goes through `IAudioEndpointVolume`, with a change notification so the
slider stays honest when the volume keys are used or the output switches to a headset.

**The clipboard does not poll.** macOS publishes no pasteboard notification, so the Mac
build reads `changeCount` twice a second. Windows has `AddClipboardFormatListener` and
delivers `WM_CLIPBOARDUPDATE`, so the cost while nothing is being copied is zero.

## Now Playing

The panel takes its colour from the record: the artwork is blurred to a wash behind the
content, masked transparent across the top band so the header stays true black. The
accent extracted from the same image drives the glow, the scrubber and the volume fill.

## Layout

```
SlateWin/
  App.xaml(.cs)              Application shell and shared styles
  Notch/
    NotchWindow.xaml(.cs)    The panel: placement, hit region, phases, rendering
    NotchState.cs            closed → hinted → open
    MouseHook.cs             Click-only low-level hook, for dismissing an open panel
    ChipFactory.cs           Shelf and clipboard chips, built in code not templates
  Media/
    MediaModel.cs            The Windows session API, start to finish
    VolumeController.cs      System output, and the per-process mixer
    NowPlaying.cs
  Shelf/
    ShelfStore.cs            Reference-only storage, persisted as paths
    FileIcon.cs              Shell icons
    Launcher.cs              Open, reveal, and raise the playing app
  Clipboard/
    ClipboardStore.cs        Event-driven history, memory only
  Support/
    ScreenMetrics.cs         Where the bar goes
    AccentExtractor.cs       One vivid colour from the artwork
    WindowRaiser.cs          Foreground rules and the AttachThreadInput dance
    Diagnostics.cs
```

## Behaviour carried over from the Mac build

These were all things the macOS version got wrong first and had to be measured to fix, so
they are built in from the start here:

- An open panel **ignores the cursor**. Drifting off it does nothing; only a click
  outside, or the ✕, closes it. Clicking *inside* it does nothing either — a stray click
  on empty space belongs to the panel, not to dismissing it.
- The header is three fixed regions, not a spacer. A spacer only guarantees a minimum
  gap and lets it drift with the width of whatever sits either side.
- Only the selected tab carries its label; three full labels do not fit.
- Transport buttons are large circles inside larger hit targets.
- The hover glow takes its colour from the artwork, and is **black while nothing is
  playing**, so an idle bar glows dark rather than announcing itself in a colour that
  belongs to no track.
- The panel is true black across the top strip; the accent tint only begins below it.

## Not in this build

Calendar, camera mirror, timers, AirDrop equivalents, themes, global hotkey. The bar is
placed on the primary monitor; multi-monitor placement is written but unexercised.
