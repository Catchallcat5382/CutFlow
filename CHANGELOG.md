# CutFlow changelog

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
