# DeSign Mage

A vector graphic design program for Windows: draw and edit shapes, live text and placed pictures; combine, weld, cut and outline
them with the effect tools; edit nodes and loops; color with palettes; save and export. Written in C# on .NET 10 with
[Avalonia](https://avaloniaui.net/) (interface) and [SkiaSharp](https://github.com/mono/SkiaSharp) (drawing). 64-bit Windows only.

Documents are saved as `.dsm` (a zip with a JSON design and the embedded pictures). Documents from earlier builds (same format,
older extension) and legacy layout files (`.lyt`) can be opened; they are never overwritten, Save asks for a new `.dsm` name. Export goes to SVG, PDF and PNG.

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

A portable build: `./publish-portable.ps1 -Output ..\build\DeSignMage-portable -Zip` (ReadyToRun, self-contained).

## The window

The parts of the main window have fixed names, used in tooltips, menus, code and these docs:

| Name | Where / what |
|---|---|
| **Main Toolbar** | the icons on the same top row as the menus (New, Open, Save, Undo, Zoom, ...), with Feedback / Bug at the far right |
| **Context Bar** | the band under it that changes with the active tool and the selection: always two rows tall (fields on the first, buttons and options on the second); a row that is too long for the window scrolls sideways |
| **Menu Tabs** | Image, Effects and Tools tabs at the right end of the Context Bar; a tab opens a strip of icon buttons for that menu's commands in the right half of the bar (one at a time; the open tab closes it; the normal menus stay) |
| **Toolbox** | the column of tool buttons on the left |
| **Info Line** | the line of counts and sizes above the ruler |
| **Dock** | the full-height column on the right, under the top row; it holds one panel at a time and is only its **Dock Tabs** when every panel is closed |
| **Dock Tabs** | Color Mixer, Layers and History, at the top of the Dock, in the Context Bar's band; the open one melts into its panel |
| **Color Mixer** | the Dock's Color Mixer: wheel, sliders, the Fill / Stroke target, eyedropper |
| **Palette Strip** | the far-right column of swatches, with the red X (remove all color) on top |
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
| `src/DeSignMage.Formats` | native `.dsm` reader/writer, legacy `.lyt` layout reader, color palette files |
| `src/DeSignMage.Rendering` | drawing the document with Skia; SVG, PDF and PNG export |
| `src/DeSignMage.Text` | fonts, glyph outlines, shaping |
| `src/DeSignMage.Imaging` | pixel operations on placed pictures |
| `src/DeSignMage.Vectorize` (+ `.Cli`) | picture to vector tracing |
| `src/DeSignMage.Feedback` | logging, crash capture, feedback reports |
| `src/DeSignMage.App` | the Avalonia program: windows, menus, panels, the canvas |
| `tests/` | one test project per area (xunit); `tests/Shared` holds the helper that finds the local files below |

## Files that are not in the repository

Two folders next to `DeSignMage.slnx` are ignored by git. The program and the tests work without them.

**`local-assets/`**: files copied next to the exe when they exist.
* `local-assets/palettes/`: color palette files (`.PAL`, `.CLR`). Without them the program starts with a small built-in palette.
  More palettes can be loaded at any time from any folder or file: **File ▸ Load Color Palettes…** and **File ▸ Load Color Palette File…**,
  or the **…** button beside the palette list in the Color Mixer. Folders you add are remembered; palettes in
  `%LOCALAPPDATA%\DeSignMage\palettes` are always loaded.

**`local-testdata/`**: inputs for tests that read real files. Each such test is **skipped** (not failed) when its file is missing.
* `local-testdata/layouts/`: legacy `.lyt` layouts: `sample.lyt` (a mid-size real layout used by most tests), `art-a.lyt`, `art-b.lyt`,
  `art-c.lyt` (larger ones, 1,400 objects for `art-c`), `sample-a.lyt`, `sample-b.lyt`, `sample-c.lyt`, `design2.lyt`, `effects.lyt`,
  `effects2.lyt`, `image2.lyt`, `open.lyt`, `stroke.lyt`, `text.lyt`, `text2.lyt`, `text3.lyt`, `textonpath.lyt`, `vectorize2.lyt`,
  `legacy-old.lyt` (an old-format file the reader must refuse) and `manifest.json`, which maps a file name to its known object count
  (`{ "sample-a.lyt": 5, ... }`).
* `local-testdata/fonts/sample.FNT` and `local-testdata/clipart/sample.LGO`: one legacy font file and one legacy clipart file (the reader must refuse both with a clear message).
* `local-testdata/palettes/`: the palette files plus `palette-golden.json` (what an independent reader gets from each `.PAL`).

Speed tests (`tests/DeSignMage.Perf.Tests`) use `layouts/sample.lyt` too.

## Feedback reports

`secrets/` (also ignored) may hold `feedback-webhook.txt`; when it exists it is built into the program so that "Feedback / Bug" can deliver
reports. Without it reports are kept in `%LOCALAPPDATA%\DeSignMage\feedback\outbox`.

## Where the program keeps its files

`%LOCALAPPDATA%\DeSignMage` (settings, logs, crash files, the feedback outbox, extra palettes) and `%APPDATA%\DeSignMage\recent.json`.
On the first start after the rename from an earlier build, those folders are carried over from their old location.

## License

DeSign Mage is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License,
version 3, as published by the Free Software Foundation. See [LICENSE](LICENSE). It comes with no warranty.

Third-party libraries keep their own licenses: Avalonia, SkiaSharp and HarfBuzzSharp (MIT), Clipper2 (Boost Software License),
the Inter font (SIL Open Font License).
