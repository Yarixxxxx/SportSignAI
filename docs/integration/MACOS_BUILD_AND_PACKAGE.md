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
  - if the NuGet package does not expose both native libraries, the packaging script can copy them from an installed `VLC.app`

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
- optional fallback for LibVLC packaging: `VLC.app` in `/Applications`

The end user does not need those prerequisites.

## LibVLC runtime check

The packaged app must contain both files:

- `dist/macos/VideoAnalysis.app/Contents/MacOS/lib/libvlc.dylib`
- `dist/macos/VideoAnalysis.app/Contents/MacOS/lib/libvlccore.dylib`
- `dist/macos/VideoAnalysis.app/Contents/MacOS/libvlc.dylib` as a compatibility loader path
- `dist/macos/VideoAnalysis.app/Contents/MacOS/libvlccore.dylib` as a compatibility core path

The packaging script exits with an error if either file is missing. It checks:

1. `LIBVLC_RUNTIME_DIR`, when provided
2. `/Applications/VLC.app/Contents/MacOS/lib`
3. publish output
4. the `VideoLAN.LibVLC.Mac` NuGet cache

If LibVLC cannot be found, install VLC on the packaging Mac and rerun:

```bash
brew install --cask vlc
./scripts/macos/package-app.sh
```

mpv/libmpv is not required on macOS. The macOS build uses LibVLC playback and excludes the mpv-specific playback service from the app binary.
