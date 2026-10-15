# Code signing configuration

`artifact-configuration.xml` tells [SignPath](https://signpath.io) which files of a release to sign. Paste its content into the
project's artifact configuration in SignPath (Project ▸ Artifact configurations ▸ Add, mark it as the default) or keep it as the
project's default and leave `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG` unset.

It signs **only our own files**, wherever they sit in the release ZIP:

* `DeSignMage.exe`
* `DeSignMage.dll` (the program the exe starts)
* `DeSignMage.*.dll` (`DeSignMage.Core.dll`, `DeSignMage.Rendering.dll`, ...)

Everything else in the portable folder (the .NET runtime, Avalonia, SkiaSharp, HarfBuzzSharp, Clipper2, PdfPig, ...) belongs to
other people and is shipped byte-for-byte as their NuGet packages and the .NET runtime pack deliver it. The release publish only
precompiles (ReadyToRun) our own assemblies for this reason, see `PrecompileOnlyOurAssemblies` in
`src/DeSignMage.App/DeSignMage.App.csproj`.

The ZIP root is `<zip-file>` because the release workflow uploads the published folder with `actions/upload-artifact`, which always
packs it as a ZIP. The `max-matches="unbounded"` attributes allow the patterns to match many files; if SignPath rejects the
configuration on upload, that is the first thing to look at.

How the signing request is made, and who approves it: see `../.github/workflows/release.yml`, `../RELEASING.md` and
`../CODE-SIGNING-POLICY.md`.
