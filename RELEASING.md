# Releasing

A release is a version tag on `main`. GitHub Actions builds, tests and publishes it; SignPath signs the program's own files after a manual
approval. Background: [CODE-SIGNING-POLICY.md](CODE-SIGNING-POLICY.md).

## Each release

1. Make sure `main` is what you want to ship and [CI](.github/workflows/ci.yml) is green on it.
2. Set the new version in `Directory.Build.props` (`<Version>`, for example `0.3.7`), commit and push to `main`.
3. Tag that commit and push the tag. The tag is `v` + the version, and the workflow refuses a tag that does not match `<Version>`:

   ```
   git tag v0.3.7
   git push origin v0.3.7
   ```

4. The [Release workflow](.github/workflows/release.yml) runs: build with warnings as errors, all tests, the portable win-x64 publish,
   a check that the build contains no webhook address, then the signing request.
5. **Approve the signing request** in SignPath (an email arrives; or Signing requests ▸ the pending one ▸ Approve). The workflow waits up to two hours
   for it. Check that the request is for the commit you tagged.
6. The workflow downloads the signed files, checks them, zips them and creates the GitHub Release
   (`DeSignMage-<version>-win-x64.zip` and its `.sha256`, with generated change notes and a link to the code signing policy).
7. Download the zip from the release page, unzip it somewhere else and check: `DeSignMage.exe` starts, and its Properties ▸ Digital
   Signatures tab shows "SignPath Foundation".

A tag with a dash (`v0.4.0-beta.1`) is marked as a pre-release. If a run fails, fix the problem, delete the tag
(`git push --delete origin v0.3.7`, `git tag -d v0.3.7`) and the draft or release it made, and tag again.

## How a release reaches the people who installed the program

The workflow also makes the installer and the update packages (Velopack, `tools/pack-release.ps1`, run on the signed folder) and puts them on the
release page next to the portable zip: `DeSignMageApp-win-Setup.exe`, `DeSignMageApp-<version>-full.nupkg`, a small
`DeSignMageApp-<version>-delta.nupkg` (from the second release on), and `releases.win.json` / `RELEASES` / `assets.win.json`, the list installed
copies read. Nothing in the program folder is changed or re-signed by packing (the script compares every file with the package).

* **First install** (once per person): send them the link to the release page, or `DeSignMageApp-win-Setup.exe`. They run it; it installs for
  that user only (no administrator), adds a Start menu and Desktop shortcut and does not open the program by itself. Their settings and
  designs are not touched. The installer is Velopack's own and not signed, so Windows may say "unknown publisher" the first time.
* **Every later release** reaches them without any action: an installed copy checks github.com in the background (45 s after it starts, then
  every 4 hours) and downloads a newer *non-pre-release* version quietly. At the **next fresh start** (no other DeSign Mage window open) a window
  "A new version of DeSign Mage is required" appears, with one button that installs the update and reopens the program. If the update is not
  downloaded yet it downloads there with a progress bar. Offline, or any failure, the program just starts and tries again next time.
  So after publishing: wait for the release page to show the files, nothing else to do. To see it yourself, run an installed older copy.
* Releases tagged with a dash (`v0.4.0-beta.1`) are marked pre-release and installed copies ignore them: use that to check a release first.
* **Never change `UpdatePackId`** (Directory.Build.props, `DeSignMageApp`): installed copies find their updates and their install folder by it, and it must
  stay different from the data folder `%LOCALAPPDATA%\DeSignMage`.
* **Try it locally** without publishing anything: `./tools/try-update-locally.ps1` installs an old fake version for you only, packs a newer one into a
  folder, and starts the old one against that folder (`DESIGNMAGE_UPDATE_SOURCE` points the program at a folder or another web address).
  Remove the test copy with `%LOCALAPPDATA%\DeSignMageApp\Update.exe --uninstall`.
* The portable zip is unchanged and never updates itself; Help ▸ About says so.

## Without signing

While the SignPath settings below do not exist, the same workflow still runs and creates a release with the unsigned files; the release notes
say so. Signing starts working the moment the settings are added.

## One-time setup

**SignPath** (<https://signpath.io>, the OSS program of SignPath Foundation):

1. Apply to SignPath Foundation for the open-source project, with the repository link and the policy page above. Turn on two-factor
   authentication for the SignPath account and for the GitHub account.
2. In SignPath: create the project (slug, for example `designmage`); add the artifact configuration from
   [`.signpath/artifact-configuration.xml`](.signpath/artifact-configuration.xml) and make it the project's default; create a signing
   policy (for example `release-signing`) with **manual approval** required and the approver named in the policy.
3. Add the **GitHub.com** trusted build system to the organization and link it to the project (this is how SignPath knows the request
   comes from this repository's workflow); allow the repository. Optionally install the SignPath GitHub App.
4. Create an API token for a CI user that may submit signing requests to this project's policy.

**GitHub** (repository ▸ Settings ▸ Secrets and variables ▸ Actions):

| Kind | Name | Value |
|---|---|---|
| Secret | `SIGNPATH_API_TOKEN` | the API token |
| Variable | `SIGNPATH_ORGANIZATION_ID` | the SignPath organization id |
| Variable | `SIGNPATH_PROJECT_SLUG` | the project slug |
| Variable | `SIGNPATH_SIGNING_POLICY_SLUG` | the signing policy slug |
| Variable (optional) | `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG` | only if the configuration is not the project's default |

Also: Settings ▸ Actions ▸ General: workflows may be read/write (the release job asks for `contents: write` itself), and consider a branch rule
for `main` and a tag rule for `v*` so that only the owner can create release tags.

## Notes

* Only GitHub-hosted runners are used (SignPath requires this for open-source signing).
* No secrets are used by the build. Feedback reports go to a public relay; the build never contains a webhook address
  (`tools/check-no-secrets.ps1` checks the published folder).
* `tools/check-third-party-unchanged.ps1` fails the release if any file that is not ours (not named `DeSignMage*`) differs from the copy in the
  restored NuGet packages / .NET runtime pack, or if a palette file is in the build. It runs before signing and again on the signed result.
* Only `DeSignMage.exe`, `DeSignMage.dll` and `DeSignMage.*.dll` are signed. The publish precompiles only those, so the third-party files
  are unchanged from their NuGet packages (see [.signpath/README.md](.signpath/README.md)).
