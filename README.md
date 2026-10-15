# DeSign Mage

A vector graphic design program for Windows: draw and edit shapes, live text and placed pictures; combine, weld, cut and outline
them with the effect tools; edit nodes and loops; color with RGB and CMYK sliders and a color wheel; save and export. Written in C# on .NET 10 with
[Avalonia](https://avaloniaui.net/) (interface) and [SkiaSharp](https://github.com/mono/SkiaSharp) (drawing). 64-bit Windows only.

Documents are saved as `.dsm` (a zip with a JSON design and the embedded pictures). Documents from earlier builds (same format,
older extension) and legacy layout files (`.lyt`) can be opened; they are never overwritten, Save asks for a new `.dsm` name. Export goes to SVG, PDF and PNG.

**Download:** on the [Releases page](https://github.com/dell90wattac/DeSignMage/releases) there is an installer
(`DeSignMageApp-win-Setup.exe`: installs for you only, no administrator needed; a copy installed this way updates itself, asking you to
update when you start it) and the portable build (unzip and run `DeSignMage.exe`, nothing to install, no automatic updates). Free code signing provided by SignPath.io, certificate by SignPath
Foundation. Releases are built by GitHub Actions; see the [code signing policy](CODE-SIGNING-POLICY.md) below.

## Build, test, run

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). From this folder:

```
dotnet build DeSignMage.slnx
dotnet test DeSignMage.slnx
dotnet run --project src/DeSignMage.App -- --demo
```

`DeSignMage.exe` is built to `src/DeSignMage.App/bin/<configuration>/net10.0/`.

Command-line flags (mostly for development): `--demo` (sample artwork), `--stress 5000` (random shapes), `--guides`,
`--screenshot out.png` (render the window, exit), `--window-size 1920x1080`, `--text-scale 1.5`, `--first-frame-exit out.txt`
(startup time to the first drawn frame, exit). A file name opens that file.

A portable build: `./publish-portable.ps1 -Output ..\build\DeSignMage-portable -Zip` (self-contained, win-x64). Only the program's own
assemblies are precompiled (ReadyToRun), so every third-party file in the folder stays identical to its NuGet package, which the release
signing relies on. For the fastest personal build (all assemblies precompiled, about 0.7 s faster to start) add `-PrecompileAll`. Releases are made as described in [RELEASING.md](RELEASING.md); GitHub Actions
(`.github/workflows`) builds and tests every push and pull request.

## The window

The parts of the main window have fixed names, used in tooltips, menus, code and these docs:

| Name | Where / what |
|---|---|
| **Main Toolbar** | the icons on the same top row as the menus (New, Open, Save, Undo, Zoom, ...), with Feedback / Bug at the far right |
| **Context Bar** | the band under it that changes with the active tool and the selection: always two rows tall (fields on the first, buttons and options on the second); a row that is too long for the window scrolls sideways |
| **Menu Tabs** | Image, Effects and Tools tabs at the right end of the Context Bar; a tab opens a strip of buttons that are mainly a short word with a small icon, for that menu's commands right below the tabs (a menu area on the right 38% of the bar, divided from the controls by a line, with the tabs at its left; one strip at a time; the open tab closes it; the normal menus stay) |
| **Toolbox** | the column of tool buttons on the left |
| **Info Line** | the line of counts and sizes above the ruler |
| **Dock** | the full-height column on the right, under the top row; it holds one panel at a time and is only its **Dock Tabs** when every panel is closed |
| **Dock Tabs** | Color Mixer, Layers and History, at the top of the Dock, in the Context Bar's band; the open one melts into its panel |
| **Color Mixer** | the Dock's Color Mixer: wheel, sliders, the Fill / Stroke target, eyedropper |
| **Palette Strip** | the far-right column of swatches (a small built-in set: black, white, greys and basic hues), with the red X (remove all color) on top |
| **Project Color Bar** | the bottom bar: the colors used in the design, then the Fill and Stroke swatches and the stroke width |
| **Layers Panel** | the Dock's Layers panel (History is its own Dock Tab) |

Click a Dock Tab to open its panel across the Dock; click another to switch, or the open one to close it. The program always starts with every panel closed, and with none open the Dock collapses to its tabs and the canvas takes the room. The Palette Strip is always visible. Padding and gaps grow at a third of the rate of the interface text size; text, icons and controls grow with it.

**Dark mode** has two independent switches, both at the right end of the top row (and in the View menu) and both remembered: **Dark Menus** turns all the chrome dark, **Dark Canvas** turns the canvas and the page dark (off shows the design on its real white page). The greys are neutral, swatches always show their true color, and exports, saved files, thumbnails and the clipboard are never affected.

A fresh design starts with no color: new shapes have no fill and no stroke and show as plain outlines until a color is chosen
(Fill and Stroke show the crossed box).

## Layout of the source

| Folder | What |
|---|---|
| `src/DeSignMage.Core` | document model, commands and undo, editing tools, effects; no UI, no drawing |
| `src/DeSignMage.Geometry` | path booleans, offsets and curve fitting (Clipper2) |
| `src/DeSignMage.Formats` | native `.dsm` reader/writer, legacy `.lyt` layout reader |
| `src/DeSignMage.Rendering` | drawing the document with Skia; SVG, PDF and PNG export |
| `src/DeSignMage.Text` | fonts, glyph outlines, shaping |
| `src/DeSignMage.Imaging` | pixel operations on placed pictures |
| `src/DeSignMage.Vectorize` (+ `.Cli`) | picture to vector tracing |
| `src/DeSignMage.Feedback` | logging, crash capture, feedback reports |
| `src/DeSignMage.App` | the Avalonia program: windows, menus, panels, the canvas |
| `tests/` | one test project per area (xunit); `tests/Shared` holds the helper that finds the local files below |

## Files that are not in the repository

The folder `local-testdata/` next to `DeSignMage.slnx` is ignored by git. The program and the tests work without it.

**`local-testdata/`**: inputs for tests that read real files. Each such test is **skipped** (not failed) when its file is missing.
* `local-testdata/layouts/`: legacy `.lyt` layouts: `sample.lyt` (a mid-size real layout used by most tests), `art-a.lyt`, `art-b.lyt`,
  `art-c.lyt` (larger ones, 1,400 objects for `art-c`), `sample-a.lyt`, `sample-b.lyt`, `sample-c.lyt`, `design2.lyt`, `effects.lyt`,
  `effects2.lyt`, `image2.lyt`, `open.lyt`, `stroke.lyt`, `text.lyt`, `text2.lyt`, `text3.lyt`, `textonpath.lyt`, `vectorize2.lyt`,
  `legacy-old.lyt` (an old-format file the reader must refuse) and `manifest.json`, which maps a file name to its known object count
  (`{ "sample-a.lyt": 5, ... }`).
* `local-testdata/fonts/sample.FNT` and `local-testdata/clipart/sample.LGO`: one legacy font file and one legacy clipart file (the reader must refuse both with a clear message).

Speed tests (`tests/DeSignMage.Perf.Tests`) use `layouts/sample.lyt` too.

## Feedback reports

"Feedback / Bug" (and crash capture) posts every report to a small relay, never straight to a chat channel, so no build holds anything
that could post to the channel. The relay is a Cloudflare Worker in [`tools/feedback-relay`](tools/feedback-relay/README.md): it
checks the report (that it is shaped like the program's own reports, size, attachment types, a limit of 10 an hour per computer and 200 a day overall), and passes it on using a secret that only lives in
Cloudflare. Its public address is `RelayConfig.DefaultUrl` (`src/DeSignMage.Feedback/RelayConfig.cs`); the `feedback.relayUrl`
setting in `%LOCALAPPDATA%\DeSignMage\settings.json` points a build at another relay (https, or http to this computer, for testing).

Reports wait in `%LOCALAPPDATA%\DeSignMage\feedback\outbox` until the relay accepts them, so being offline, a limit, or the relay
being switched off loses nothing.

What leaves the computer: the text, a log of recent actions (never the text in a design), the undo list and basic system facts. The
design file and a screenshot of the window are **optional**: both start off, the dialog remembers what the person last chose
(`feedback.includeDesign` / `feedback.includeScreenshot` in the same `settings.json`; set them to `true` there to pre-enable them
for a particular person), and a crash report only carries the recovered design when `feedback.includeDesign` is on.

**Crash reports ask first.** When the program closed unexpectedly, the crash folder (exception, log, system facts, and an emergency copy
of the open design) is written locally under `%LOCALAPPDATA%\DeSignMage\crash`, and nothing is sent. At the next start a prompt asks
"Send the crash report?": **Send** queues and sends it, **Don't send** keeps it on the computer and does not ask about it again, closing
the prompt asks again at the next start. Ticking "Always send crash reports, without asking" together with Send is remembered
(`feedback.alwaysSendCrashes` in `settings.json`; set it to `true` there to pre-enable it for a particular person). Offline, an agreed
report waits in the outbox and goes out later without asking again.

## Where the program keeps its files

`%LOCALAPPDATA%\DeSignMage` (settings, logs, crash files, the feedback outbox) and `%APPDATA%\DeSignMage\recent.json`.
On the first start after the rename from an earlier build, those folders are carried over from their old location.
A copy installed with the installer lives in `%LOCALAPPDATA%\DeSignMageApp` (a different folder on purpose: updating or uninstalling replaces
that folder and never touches the data above). Help ▸ About shows the version and whether the copy updates itself. Releases for installed
copies are made by `tools/pack-release.ps1` (Velopack); `tools/try-update-locally.ps1` tries an install and an update on one computer.

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

| Role | Members |
|---|---|
| Committers and reviewers | [dell90wattac](https://github.com/dell90wattac) |
| Approvers | [dell90wattac](https://github.com/dell90wattac) |

Release binaries are built only by GitHub Actions from tagged commits; signing requests are approved manually. Only DeSign Mage's own
files (`DeSignMage.exe`, `DeSignMage.dll`, `DeSignMage.*.dll`) are signed; third-party files are shipped unchanged. To verify: right-click
`DeSignMage.exe` ▸ Properties ▸ Digital Signatures: the signer is "SignPath Foundation".

**Privacy:** this program will not transfer any information to other networked systems unless specifically requested by the user or the
person installing or operating it. There are two network features. Feedback / Bug: a report is sent when you press Send, and a crash report
only after you agree to it on the next start. Updates: a copy installed with the installer (not the portable zip) looks for new versions
on this project's GitHub Releases page and downloads them; that request carries nothing about you or your designs. Nothing else is ever sent.

The full policy (what is signed, how a release is made, how to verify it, third-party licenses) is in
[CODE-SIGNING-POLICY.md](CODE-SIGNING-POLICY.md); the privacy policy (what a report contains, who receives it, how to have it removed) is
[PRIVACY.md](PRIVACY.md).

## License

DeSign Mage is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License,
version 3, as published by the Free Software Foundation. See [LICENSE](LICENSE). It comes with no warranty.

Third-party libraries keep their own licenses: Avalonia, SkiaSharp and HarfBuzzSharp (MIT), Clipper2 (Boost Software License), PdfPig (Apache License 2.0), Velopack (MIT, installer and updater),
the Inter font (SIL Open Font License).
