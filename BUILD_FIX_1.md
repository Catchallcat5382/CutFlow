# Build fix 1

This revision explicitly imports the .NET namespaces used by the WPF temporary build project.

Fixed compiler errors for:
- `Path`, `File`, `Directory`, `FileNotFoundException`, `SearchOption`
- `HttpClient`, `HttpCompletionOption`
- LINQ helpers used by the FFmpeg export builder

Run `build.bat` normally. The existing private `.dotnet` SDK folder may be kept; the script will reuse it.
