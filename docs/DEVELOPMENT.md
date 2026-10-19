# Developer notes

## Build, test, run

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). From the repository root:

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
assemblies are precompiled (ReadyToRun), so every third-party file stays identical to its NuGet package, which release signing relies on.
`-PrecompileAll` precompiles everything for the fastest personal build. Releases: see [RELEASING.md](../RELEASING.md). GitHub Actions
(`.github/workflows`) builds and tests every push and pull request.

## The window

The parts of the main window have fixed names, used in tooltips, menus, code and docs:

| Name | Where / what |
|---|---|
| **Main Toolbar** | the few icons on the top row after the menus (Open, Save, Undo, Redo, the zoom box); the Feedback button is at the far right |
| **Context Bar** | the band under it that changes with the tool and the selection: only actions and the tool's own settings; one line, or two when its controls do not fit side by side |
| **Toolbox** | the column of tool buttons on the left |
| **Info Line** | the bottom bar after the Project Color Bar: the X, Y, W, H fields (or the layout's size), what is selected, the object and group counts, the pointer position, and short messages |
| **Dock** / **Dock Tabs** | the right column (Color Mixer, Layers, History), one panel at a time |
| **Color Mixer** | wheel, sliders, Fill / Stroke target, eyedropper |
| **Palette Strip** | the far-right column of swatches, with the red X (remove all color) on top |
| **Project Color Bar** | the left part of the bottom bar: colors used in the design, Fill and Stroke |
| **Layers Panel** | the Dock's Layers panel |

## Source layout

| Folder | What |
|---|---|
| `src/DeSignMage.Core` | document model, commands and undo, editing tools, effects; no UI |
| `src/DeSignMage.Geometry` | path booleans, offsets and curve fitting (Clipper2) |
| `src/DeSignMage.Formats` | native `.dsm`, legacy `.lyt` layouts, PDF/AI/EPS/SVG import |
| `src/DeSignMage.Rendering` | drawing with Skia; export |
| `src/DeSignMage.Text` | fonts, glyph outlines, shaping |
| `src/DeSignMage.Imaging` | picture operations and the picture editor's processing |
| `src/DeSignMage.Vectorize` | picture to vector tracing |
| `src/DeSignMage.Feedback` | logging, crash capture, feedback reports |
| `src/DeSignMage.App` | the Avalonia program: windows, menus, panels, canvas, updater |
| `tests/` | one test project per area (xunit) |
| `tools/` | release packaging, checks, the feedback relay (`tools/feedback-relay`) |

## Local test files

Some tests read real layout files from `local-testdata/` (ignored by git). Each such test is **skipped**, not failed, when its file is
missing, so the program and the tests work without it.

## Feedback reports

Feedback / Bug and crash reports go to a small relay (a Cloudflare Worker, [`tools/feedback-relay`](../tools/feedback-relay/README.md)),
which checks them and forwards them; no build contains anything that could post to the destination directly. Reports wait in
`%LOCALAPPDATA%\DeSignMage\feedback\outbox` while offline. Crash reports are only sent after the user agrees. Details: [PRIVACY.md](../PRIVACY.md).

## Where the program keeps its files

`%LOCALAPPDATA%\DeSignMage` (settings, logs, crash files, feedback outbox) and `%APPDATA%\DeSignMage\recent.json`. An installed copy lives
in `%LOCALAPPDATA%\DeSignMageApp` (separate on purpose: updating or uninstalling never touches the data). `tools/try-update-locally.ps1`
tries an install and an update on one computer.
