# v3.26.0 validation

- Project version: 3.26.0. Project schema stays 54 and detector schema stays 53; this release intentionally does not force a rescan or alter VAD/cut detection.
- All XAML files parse as XML and every referenced XAML event handler resolves to a C# method.
- Modified C# files have balanced braces in static checks.
- Smart Export uses timestamp-normalized video/audio inputs for stream copy and writes to a same-folder staging file before atomic publish.
- The staged export is probed for zero-ish A/V starts, sane duration, and opening A/V alignment, then the first up-to-2.5 seconds are decoded with FFmpeg `-xerror`.
- Stream-copy verification failures are handled by the existing fast re-encode fallback instead of publishing a damaged file.
- Direct same-container file-copy export is staged + atomically moved.
- Export audio ends with `aresample=async=1:first_pts=0` on the re-encode path to keep timestamps continuous from sample zero.
- Preview no longer calls Play again from the seek-audio recovery timer and no longer reapplies volume/mute every 1.8 seconds during playback.
- Normal playback throttles MediaElement.Position polling to roughly 24 Hz unless TEST-cut boundaries require faster polling.
- Export monitor success remains open and exposes Open in Folder + OK; failure remains visible until OK.
- Linux-side FFmpeg smoke test confirmed separate video/audio timestamp offsets can be normalized to approximately zero while stream-copying video and re-encoding audio, and the first 2.5 seconds decode cleanly with `-xerror`.
- Actual Windows WPF/Inno compilation still needs to be performed by `build.bat` on Windows.
