# CutFlow changelog

## 3.26.0 — export startup integrity + playback/audio stability
- Smart Export now normalizes video/audio stream start timestamps before stream-copy remuxing, preventing MP4/MOV edit-list offsets from surfacing as a broken first second.
- Fast exports render to a same-folder staging file, decode-check the opening seconds, verify A/V start alignment/duration, then atomically publish the destination. Unsafe stream-copy results automatically fall back to the fast NVENC/ultrafast encode path.
- Audio export uses a continuous `first_pts=0` resample stage so cleanup filters cannot leave a tiny timestamp hole at the beginning.
- Plain file-copy exports are staged + atomically moved too, so cancellation/crashes do not leave a half-written final export.
- Preview playback no longer re-calls `MediaElement.Play()` after every seek or periodically reassigns the audio device while already playing. This removes a source of random headphone/audio dropouts.
- Normal playback polls `MediaElement.Position` less aggressively while still keeping TEST-cut boundary polling fast.
- Export completion now stays open with a clear **Export finished** state, **Open in Folder**, and **OK**. It no longer disappears automatically after one second.
- Export failures also remain visible until the user presses OK instead of vanishing on a timer.
- Detector/VAD and Cut All behavior are unchanged.

## 3.25.0 — Smart Export / fast ETA

- Replaced v3.24's intentionally throttled export path. Background export no longer forces FFmpeg to Idle priority with only two video threads.
- Added Fast / Balanced / Maximum quality export modes; Fast is the remembered default.
- Added Smart Export: when resolution and FPS stay Original, the already-cut working video stream is copied instead of being decoded/re-encoded again.
- If voice cleanup is enabled, Smart Export processes only the audio while copying video; with cleanup disabled and the same container, export becomes a direct asynchronous file copy.
- Re-encodes use NVIDIA NVENC when available, with fast multi-threaded x264 fallback instead of the old two-thread `fast` preset.
- Fast mode skips MP4/MOV `faststart` relocation so an otherwise-finished export does not spend extra time rewriting the whole output file.
- Export progress now publishes a smoothed worker ETA after roughly the first second of real progress instead of waiting until 2.5%.
- Export speed and the Apply voice cleanup choice are remembered between exports.
- Export worker probes the exact current working media before rendering so duration/progress calculations use the file actually being exported.

## 3.24.0 — Editor polish / stable clipboard / background export

- Hardened WPF preview audio recovery after rapid seeks and Test Cuts jumps so audio state is re-applied without forcing the user to scrub backward.
- Added region clipboard editing: Ctrl+C copy, Ctrl+X cut markers to the internal clipboard, Ctrl+V paste at the playhead, plus a visible Paste button and context-menu actions.
- Added familiar curved-arrow Undo/Redo buttons beside the region tools; Ctrl+Z and Ctrl+Y continue to use the same guarded history.
- Undo/Redo now refuses to restore a media revision whose backing working file is missing instead of breaking the editor.
- + Region and Rescan reliably re-enable after analysis/busy operations finish.
- Replaced the timeline pan slider with a true horizontal scrollbar. Preview/timeline height and silence-panel width can be resized and are remembered.
- Region normalization now merges overlapping/touching regions and microscopic gaps (~18 ms / about one video frame) on scan, manual edits, worker plans, and project open.
- New worker/working files live under hidden `%LOCALAPPDATA%\CutFlow\Cache`; legacy per-project Jobs/Working folders are hidden rather than shown as normal project content.
- Media exports default to `%LOCALAPPDATA%\CutFlow\Exports`; a custom destination folder is remembered for later exports.
- Export encoding now runs in an isolated low-priority CutFlow worker with its own tray icon and independent progress/ETA monitor, so FFmpeg no longer runs inside the editor process.
- Detector/VAD schema remains unchanged at 53: this usability release does not force a needless fresh scan.

## 3.23.3 — Release-folder installer fix

- Every successful `build.bat` now writes the current installer to both `releases\vX.Y.Z` and `releases\latest`.
- `releases\latest` contains both `CutFlow-Setup.exe` and the versioned installer name plus checksums and `VERSION.txt`.
- `release.bat` publishes the exact installer created in the versioned local release folder.
- First-time `setup.bat` now performs a build after GitHub/Inno Setup configuration so a fresh installer exists immediately after setup.

## 3.23.2 — Git metadata auto-repair

- `setup.bat` now repairs an unreadable/incomplete `.git` created by a failed earlier setup instead of stopping.
- Adds the project as a Git `safe.directory` before repository checks to handle secondary-drive/dubious-ownership errors.
- Clears stray `GIT_DIR`, `GIT_WORK_TREE`, and `GIT_INDEX_FILE` environment variables before Git operations.
- Broken Git metadata is moved to `%LOCALAPPDATA%\CutFlow\SetupBackups` before reinitialization; CutFlow source files are never deleted.
- `push.bat` and `release.bat` use the same hardened Git environment/safe-directory handling.

# CutFlow Changelog

## v3.23.1 - Setup/GitHub reliability fix

- Fixed first-run `setup.bat` failing after `git init` with `current directory is not a git repository`.
- Every Git command is now anchored explicitly to the CutFlow project directory with `git -C`.
- GitHub repository creation no longer uses `gh repo create --source=.`; the remote is created by name and then attached as `origin`.
- `setup.bat` is idempotent and repairs a partially completed first setup instead of requiring `.git` to be deleted.
- Added automatic local Git commit identity when none is configured.
- Hardened `push.bat` and `release.bat` so they also operate against the explicit project root.

# CutFlow v3.23.0 — Installer / GitHub / Uninstall wrap-up

- Added Inno Setup 6 installer packaging.
- Added `setup.bat`, `push.bat`, and `release.bat` GitHub workflow.
- Releases now ship the setup installer instead of the raw app EXE.
- Added Settings → App with installer-managed uninstall.
- Optional delete-all-data uninstall is off by default.
- Uninstall runs through an independent temporary monitor with tray icon and progress bar.
- Closing a project editor now hides it while saving and reopens Project Home; closing Home exits.

# CutFlow 3.22.0

## Voice-first pause detection
- Replaced the amplitude/ZCR-first silence detector with the official Silero VAD ONNX voice-activity model.
- CutFlow now decides whether the user is speaking from a real speech-probability map; dB/crest/ZCR are secondary safeguards for very soft speech and transient-noise cleanup.
- Initial scan and Rescan use the same deterministic VAD pipeline. Previous automatic regions are never detector input.
- Full gaps between confirmed speech blocks become regions, including leading/trailing silence.
- Short click/bump/squeak islands are bridged only when the VAD is not confidently seeing sustained speech.

## Cut All refinement
- Cut All still performs three hidden deterministic analysis levels, but all three use the same immutable VAD probability map and produce one final manifest before rendering.
- The final sliver pass merges neighboring pause regions only when the intervening audio has low/non-sustained speech probability.
- Manual regions and explicit Keep choices remain authoritative.

## Faster working cuts
- CutFlow now probes NVIDIA NVENC once and uses it for temporary/working cut renders when the installed FFmpeg + GPU driver can actually encode with it.
- Systems without working NVENC automatically use the existing x264 ultrafast fallback with a larger safe thread budget.
- Final Export keeps its own user-selected quality settings.

## Project cache
- Schema 53 stores the VAD probability map with the current working media so detector settings can be reevaluated deterministically.
- Upgrading from an older detector forces one fresh VAD analysis of the current working file; verified working cuts are not reset to the original source.
