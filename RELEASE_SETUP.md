# CutFlow release setup

## One-time setup
Run `setup.bat`. It checks Git, GitHub CLI and Inno Setup 6, signs into GitHub when needed, initializes the local Git repository, creates/connects `Catchallcat5382/CutFlow` as a private repository, commits the project and pushes `main`.

GitHub/release defaults live in `tools\release-settings.cmd`.

## Daily use
- `build.bat` — publishes CutFlow and creates `dist\installer\CutFlow-Setup-vX.Y.Z.exe`.
- `push.bat` — commits and pushes source changes.
- `release.bat` — builds the installer, copies it to `releases\vX.Y.Z` and `releases\latest`, pushes source, and creates/updates the matching GitHub Release.

Release artifacts are installer packages, not the raw application EXE.

## In-app uninstall
Settings → App → Uninstall CutFlow opens a CutFlow confirmation window. “Also delete all CutFlow data” is off by default. The uninstall monitor is copied to `%TEMP%` before the installed app exits, then it runs Inno Setup's `unins*.exe` silently and shows progress from an independent process/tray icon.

## If an older setup.bat failed after git init

Do **not** delete the `.git` folder. Just replace the project files with v3.23.2 or newer and run `setup.bat` again. The setup script detects the existing local repository, creates or reconnects `Catchallcat5382/CutFlow`, repairs `origin`, and pushes `main`.


### Git repair
If a failed setup left an incomplete `.git`, rerun `setup.bat`. v3.23.2 backs up only the broken Git metadata to `%LOCALAPPDATA%\CutFlow\SetupBackups`, reinitializes Git, and keeps all project/source files.
