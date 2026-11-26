# v3.28.0 validation

- Project version: 3.28.0. Project schema stays 54 and detector schema stays 53; this release does not force a rescan or change Silero VAD behavior.
- The large-cut video path contains no multi-range `a+b+c+...` FFmpeg expression. Every cut uses its own small `select` predicate and its own small ordered timestamp-collapse step.
- The timestamp-collapse thresholds are based on each cut end after all previously removed durations, preserving the intended compressed source timeline.
- The complete cut graph is written to a temporary UTF-8 filter script and passed with `-filter_complex_script`, avoiding the Windows command-line size limit for large jobs.
- The temporary filter script is removed in a `finally` block on success, FFmpeg failure, cancellation, or timeout.
- Linux-side FFmpeg smoke test: 120 cut ranges on a 30-second A/V file completed successfully with the new filter chain; expected duration 20.400s and output container duration 20.400s.
- Linux-side FFmpeg stress test: 250 cut ranges produced a 44,748-character filter graph and completed successfully through `-filter_complex_script`; expected duration 42.500s and output duration 42.500s.
- The 250-range test demonstrates that a graph larger than the Windows command-line limit is no longer passed through the process command line.
- Existing audio kept-range trimming/concatenation remains unchanged and is still generated from the same merged cut manifest as video.
- Existing worker post-render duration and A/V-alignment validation remains enabled before a cut result can be accepted.
- Main-window and error-dialog titles show the assembly version so a stale installed executable can be identified from a screenshot.
- `build.bat` still refuses to build when the README first line does not match the version in `CutFlow.csproj`.
- Static checks verify balanced braces, valid XAML XML, and matching referenced event-handler names.
- Actual Windows WPF/Inno compilation must be performed by `build.bat` on Windows because this validation environment does not contain the .NET SDK/Windows WPF toolchain.
