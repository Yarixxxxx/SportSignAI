# macOS Build And Packaging

This guide is for preparing a **ready-to-run macOS app** for an end user. The target machine is **Apple Silicon (`osx-arm64`)**.

## Goal

The end user should receive:

- `VideoAnalysis.app`
- or `VideoAnalysis-macos-osx-arm64.zip`

The user should **not** need to:

- install `.NET`
- install `ffmpeg`
- install `VLC`
- run the app from Terminal

## What is already bundled by this repo

- `.NET` is bundled by `dotnet publish --self-contained true`
- `ffmpeg` for Apple Silicon is bundled from:
  - `tools/ffmpeg/macos-arm64/unpacked/ffmpeg`
- macOS LibVLC runtime is referenced through:
  - `VideoLAN.LibVLC.Mac`

## One-command packaging on Mac M1

From the repository root on a Mac:

```bash
chmod +x scripts/macos/package-app.sh
./scripts/macos/package-app.sh
```

The script will:

1. run `dotnet publish` for `osx-arm64`
2. create `dist/macos/VideoAnalysis.app`
3. copy the published output into the app bundle
4. mark the app executable and bundled `ffmpeg` as executable
5. generate `Contents/Info.plist`
6. create `dist/macos/VideoAnalysis-macos-osx-arm64.zip`

## Build without having a Mac

If the team does not have a local Mac, use GitHub Actions.

Workflow file:

- `.github/workflows/macos-package.yml`

How to use it:

1. Push the repository to GitHub
2. Open the `Actions` tab
3. Run `Package macOS App`
4. Download the artifact:
   - `VideoAnalysis-macos-osx-arm64`

The artifact contains:

- `VideoAnalysis-macos-osx-arm64.zip`

## Output paths

- app bundle:
  - `dist/macos/VideoAnalysis.app`
- archive for transfer:
  - `dist/macos/VideoAnalysis-macos-osx-arm64.zip`

## Recommended validation on Mac

After packaging:

1. Open `dist/macos/VideoAnalysis.app`
2. Create or open a project
3. Verify video playback
4. Verify clip export

## Expected prerequisites on the Mac that performs packaging

- `.NET SDK 10`
- standard macOS build tools
- `mpv` installed via Homebrew when `libmpv` is not already bundled into the publish output

Example:

```bash
brew install mpv
```

The end user does not need those prerequisites.

## Current risk to watch

The packaging script warns if LibVLC native files are not present in publish output. In that case:

- the app bundle is still created
- playback on macOS may still fail until LibVLC runtime is fully present in the publish output

That warning is the main go/no-go signal during the first macOS packaging pass.

Playback also depends on `libmpv` at startup. If `libmpv` is missing on the target Mac, the app may reach about `94%` on the splash screen and stop before the main window is shown.

The current startup guard now checks `libmpv` before the main window is created and should show an explicit failure message with the logs path instead of hanging silently.

If you are running the app locally on a Mac and it stops at `94%`, install:

```bash
brew install mpv
```
