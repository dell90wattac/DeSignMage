# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

This is the signing policy of **DeSign Mage** (<https://github.com/dell90wattac/DeSignMage>). It describes which files are signed,
who is responsible for the signed releases, how a release is made, and what the program does with your data.

## What is signed

Release builds of DeSign Mage's own programs are digitally signed (Authenticode):

* `DeSignMage.exe`
* `DeSignMage.dll` and `DeSignMage.*.dll` (the program's own libraries)

Nothing else is signed by this project. The installer `DeSignMageApp-win-Setup.exe` and the `Update.exe` it installs are built by the
Velopack tool from its own prebuilt files, wrapped around the signed program files without changing them; they are **not** signed (the
program files inside them are the signed ones). The .NET runtime, Avalonia, SkiaSharp, HarfBuzzSharp, Clipper2, PdfPig and the other
third-party files in the download are included exactly as their publishers ship them; they are not modified and not re-signed
(see [Third-party software](#third-party-software)).

## How releases are built and signed

* Release binaries are built **only by GitHub Actions**, on GitHub-hosted runners, **from tagged commits** of the `main` branch
  ([`.github/workflows/release.yml`](.github/workflows/release.yml)). The same workflow builds with warnings as errors and runs the
  full test suite before anything is published.
* A signing request is submitted to SignPath from that workflow, for the files listed above only
  ([`.signpath/artifact-configuration.xml`](.signpath/artifact-configuration.xml)).
* **Every signing request is approved manually** in SignPath by an approver named below. A release is published only after the
  approval; without it the workflow stops.
* The signed files are downloaded back into the workflow, checked (right files, valid signature, third-party files unchanged), zipped and
  attached to the GitHub Release together with a SHA-256 checksum.

The steps are written down in [RELEASING.md](RELEASING.md).

## Team roles

| Role | Members |
|---|---|
| **Committers and reviewers** | [dell90wattac](https://github.com/dell90wattac) |
| **Approvers** | [dell90wattac](https://github.com/dell90wattac) |

The approver reviews the commit and the build that the signing request refers to, and approves only builds of this project's own
source. The account that owns the project's GitHub repository and SignPath organization uses two-factor authentication.

## How to verify a signature

1. Right-click `DeSignMage.exe` in the unzipped download and choose **Properties**.
2. Open the **Digital Signatures** tab. The signer is **SignPath Foundation**.
3. Select the signature, click **Details** and check that the dialog says the digital signature is OK.

Or in PowerShell:

```powershell
Get-AuthenticodeSignature .\DeSignMage.exe | Format-List Status, SignerCertificate
```

To verify the download itself, compare the SHA-256 of the zip with the `.sha256` file on the release page:

```powershell
Get-FileHash .\DeSignMage-<version>-win-x64.zip -Algorithm SHA256
```

A build you make yourself from the source (`dotnet build`) is not signed.

## Privacy policy

The complete privacy policy is in [PRIVACY.md](PRIVACY.md). In short:

**This program will not transfer any information to other networked systems unless specifically requested by the user or the person
installing or operating it.**

In detail:

* DeSign Mage does not collect usage data and does not contact any server by itself, with one exception that the person installing it
  chooses: a copy installed with the installer (Setup) looks for new versions on this project's public GitHub Releases page and downloads
  them from there (the portable zip never does). That request contains nothing about you or your designs. Your designs, pictures
  and fonts stay on your computer; opening, editing, saving and exporting work fully offline.
* The other network feature is **Feedback / Bug**. Nothing is sent until you press **Send** in that dialog. A report contains what you
  typed, the program version, the Windows version and a short hardware summary (processor count, memory, screen sizes), a log of
  recent actions and the undo list (never the text in a design), and, only if you tick the boxes, a screenshot of the window and the open design. Reports are delivered over
  HTTPS to a small relay service run by the maintainer, which forwards them to the maintainer's private channel. They are used only to
  fix problems and are not shared with anyone else.
* After a crash the program saves a crash report and a recovery copy of your open designs on your computer. The next time it starts it
  **asks** whether the report may be sent; it is sent only if you agree (you may also choose to always send them). If you decline, the report is only kept on your computer.
* The program keeps its settings, logs and unsent reports in `%LOCALAPPDATA%\DeSignMage` and its recent-file list in
  `%APPDATA%\DeSignMage`. Delete those folders to remove everything.

## Third-party software

DeSign Mage is free software under the [GNU General Public License, version 3](LICENSE). It uses the following third-party
components, which keep their own licenses:

| Component | License |
|---|---|
| [Avalonia](https://avaloniaui.net/) | MIT |
| [SkiaSharp](https://github.com/mono/SkiaSharp) and [HarfBuzzSharp](https://github.com/mono/SkiaSharp) | MIT |
| [Clipper2](https://github.com/AngusJohnson/Clipper2) | Boost Software License 1.0 |
| [PdfPig](https://github.com/UglyToad/PdfPig) | Apache License 2.0 |
| [Velopack](https://velopack.io/) (installer and updater; its own `Setup.exe` and `Update.exe` are not signed by this project) | MIT |
| [Inter](https://rsms.me/inter/) (font) | SIL Open Font License 1.1 |
| [.NET runtime](https://github.com/dotnet/runtime) (included in the portable download) | MIT |

The full license texts are in each component's own repository or NuGet package.
