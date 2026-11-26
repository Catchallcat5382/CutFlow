# CutFlow 3.28.0

CutFlow is a local Windows editor for automatically finding and removing silent or non-speech sections from recordings. It uses Silero VAD for voice detection, keeps project and media processing local, and includes timeline editing, Test Cuts, Cut All, undo/redo, and Smart Export.

## What changed in 3.28.0

Cut All no longer sends a giant FFmpeg `select` or `setpts` expression at all. Each removal range is processed as a small filter step, so large projects cannot hit the expression-parser failure that produced `Error while parsing expression` in earlier builds.

The full filter graph is also written to a short-lived UTF-8 filter script instead of being placed on the Windows process command line. This avoids the Windows command-line length limit when a project contains a large number of cuts. The temporary filter script is deleted automatically after the worker finishes or fails.

Video timestamps are collapsed in the same cut order as the removal manifest, while audio continues to use exact kept-range trimming and concatenation. The renderer still validates the shortened file before the editor accepts it.

The main window and error-dialog title now include the running CutFlow version. This makes it easy to confirm that a newly installed build is actually the version being tested.

The 3.26 export-integrity and playback fixes remain included: staged/verified Smart Export, startup timestamp normalization, opening decode checks, audio timestamp continuity, steadier preview audio, and the persistent Export finished screen with Open in Folder and OK.

## Normal workflow

- `setup.bat` — first-time machine/repository setup.
- `build.bat` — build the current Windows installer.
- `push.bat` — push source changes.
- `release.bat` — build and publish the current installer release to GitHub.

A successful build places the installer in:

- `dist\installer\CutFlow-Setup-vX.Y.Z.exe`
- `releases\vX.Y.Z\CutFlow-Setup-vX.Y.Z.exe`
- `releases\latest\CutFlow-Setup.exe`

`build.bat` verifies that the first line of this README matches the version in `CutFlow.csproj`. A version bump with a stale README is treated as a build error so release documentation stays current.

## Runtime data

- Projects: `%LOCALAPPDATA%\CutFlow\Projects`
- Default exports: `%LOCALAPPDATA%\CutFlow\Exports`
- Worker/cache files: `%LOCALAPPDATA%\CutFlow\Cache`
- Downloaded FFmpeg tools: `%LOCALAPPDATA%\CutFlow\Tools`

## Detection and cutting

CutFlow first builds a speech map using Silero VAD, then turns non-speech gaps into editable red regions. Manual region editing, Keep, Cut, Cut All, undo/redo, and Test Cuts all operate from the same project timeline.

Cut All performs its existing safety/refinement pass and freezes one exact merged cut manifest before starting the background worker. The worker probes the current source file, renders the shortened working media, verifies duration and A/V alignment, and only then allows the editor to accept the new working revision.

## Export

Smart Export is optimized for the common case where the already-cut working media is being exported at original resolution/FPS. Fast, Balanced, and Maximum quality modes remain available. Exports run in a separate worker process with progress and ETA reporting.

## Updating CutFlow

Build or install the newest version, close any older CutFlow window that is still open, and launch the new build. The title bar shows the running version so the active executable can be verified immediately.

## Third-party components

See `THIRD_PARTY_NOTICES.md` for FFmpeg, Silero VAD, ONNX Runtime, and related notices.
