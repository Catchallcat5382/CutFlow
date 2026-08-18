# v3.23.2 validation

- setup.bat uses an explicit project root for every Git command.
- Git environment overrides are cleared before setup/push/release.
- safe.directory is configured before repository validation.
- unreadable `.git` metadata is backed up outside the project and reinitialized; source files are untouched.
- setup is idempotent and can resume after a failed first run.
- push.bat/release.bat use the same safe-directory hardening.
- GitHub remote creation still avoids `gh repo create --source=.`.

# CutFlow v3.23 validation notes

Static validation completed in the build workspace:

- All XAML files parse as XML, including the new uninstall confirmation and uninstall monitor windows.
- All event-handler names referenced by XAML resolve to code-behind methods.
- `CutFlow.csproj` parses as XML and is versioned 3.23.0.
- Installer source is `installer/CutFlow.iss` and builds the app into `%LOCALAPPDATA%\Programs\CutFlow` with Inno Setup's automatic uninstaller enabled.
- `build.bat` publishes the self-contained x64 app and compiles `CutFlow-Setup-vX.Y.Z.exe` through `ISCC.exe`.
- `setup.bat` initializes Git/GitHub CLI/Inno Setup prerequisites and connects the local folder to the configured GitHub repository.
- `push.bat` commits/pushes source; `release.bat` builds the installer, updates `releases\latest`, and publishes the installer asset to a matching GitHub Release.
- The raw app EXE is not copied to `releases`; installer packages are the release artifact.
- In-app uninstall is available only when an Inno Setup `unins*.exe` is present beside the installed app.
- Optional user-data removal is off by default and targets `%LOCALAPPDATA%\CutFlow` only after installer-managed uninstall completes.
- Project-editor close behavior hides the editor while autosaving and reopens Project Home; a close from Home exits the process.

The Linux workspace cannot run the Microsoft Windows WPF compiler or Inno Setup compiler. `build.bat` on Windows remains the final compile/package validation.
