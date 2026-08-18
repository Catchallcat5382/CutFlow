# Startup fix 2

The previous build could silently crash while `MainWindow.InitializeComponent()` was creating the controls.
WPF fires `Checked` and `ValueChanged` events while XAML is still being loaded. The settings handlers tried to
update controls that had not been created yet.

Fixes:
- Keeps UI event processing disabled until `InitializeComponent()` and all timers are initialized.
- Settings handlers now return immediately during startup initialization.
- Removed `StartupUri` and explicitly creates the main window inside a guarded `OnStartup` block.
- Added dispatcher/AppDomain/task crash logging under `%LOCALAPPDATA%\\CutFlow\\Logs`.
- Added `run-debug.bat`, which opens the newest crash log if the app exits with an error.
