# CutFlow v3.26

CutFlow is a local Windows voice-aware silence editor. v3.26 keeps the v3.25 Smart Export speed path and the stable Silero VAD cut engine, then hardens export startup timestamps, verifies the opening seconds before publishing, improves WPF preview/audio stability, and adds a proper persistent export-finished screen with Open in Folder + OK.

## Normal workflow

- First machine/repository setup: `setup.bat`
- Build current installer: `build.bat`
- Push source changes: `push.bat`
- Build + publish current installer release: `release.bat`

Successful builds place installer packages in `releases\vX.Y.Z` and update `releases\latest\CutFlow-Setup.exe`. Runtime project files live under `%LOCALAPPDATA%\CutFlow\Projects`; exports default to `%LOCALAPPDATA%\CutFlow\Exports`; worker/cache files are kept in the hidden `%LOCALAPPDATA%\CutFlow\Cache` folder.

# CutFlow v3.23

## Install / release workflow
CutFlow is now distributed as an **Inno Setup installer**. Run `setup.bat` once, `build.bat` to build an installer, `push.bat` to push source changes, and `release.bat` to publish the current version installer to GitHub Releases. See `RELEASE_SETUP.md`.

Closing the editor while a project is open now saves it, visually closes the editor, and reopens the Project Home. Closing again from Project Home exits the app.

Settings → App includes installer-managed uninstall with an optional **delete all CutFlow data** checkbox.

# CutFlow v3.22

CutFlow is a local Windows silence/pause editor. v3.22 replaces the old amplitude-heavy pause detector with a voice-activity model so the editor first answers “is there actual speech here?” and then marks the complete non-speech gaps between spoken sections.

## Build

Run `build.bat` on Windows. Keep the existing `.dotnet` directory if you already have one.

On the first v3.22 build, `build.bat` downloads the pinned official Silero VAD ONNX model and embeds it in the single-file CutFlow executable. NuGet restores Microsoft ONNX Runtime automatically. The model is then local/offline at app runtime.

Output remains:

- `dist\CutFlow.exe`
- `releases\vN\CutFlow.exe`
- `releases\latest\CutFlow.exe`

## v3.22 detector behavior

- One fresh import/Rescan = one complete deterministic voice map.
- Same working file + same settings = same automatic regions.
- Red regions cover the full removable gap, not only the lowest-volume sliver inside it.
- Leading/trailing silence can reach the real media boundary.
- Short object noises inside a pause can be swallowed into the same region when they are not sustained speech.
- Protect whispers / soft speech remains a hard safety preference.
- Cut All performs three hidden deterministic refinement levels in memory, merges the final plan, then launches one background render.
- Audio and video use the same final range manifest.

## Working-cut speed

When available, CutFlow uses NVIDIA NVENC for the temporary shortened working video. If NVENC is unavailable or the driver cannot encode, CutFlow automatically falls back to x264 ultrafast. This does not change the final Export quality controls.

See `THIRD_PARTY_NOTICES.md` for the local VAD/ONNX Runtime notices.
