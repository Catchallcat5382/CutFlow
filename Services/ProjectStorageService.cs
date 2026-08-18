using System.IO;
using System.IO.Compression;
using System.Text.Json;
using CutFlow.Models;

namespace CutFlow.Services;

public sealed class ProjectStorageService
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    public string RootDirectory { get; }
    public string ProjectsDirectory { get; }
    public string StatePath { get; }

    public ProjectStorageService()
    {
        RootDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutFlow");
        ProjectsDirectory = Path.Combine(RootDirectory, "Projects");
        StatePath = Path.Combine(RootDirectory, "appstate.json");
        Directory.CreateDirectory(ProjectsDirectory);
        MigrateLegacyProjectsBestEffort();
    }

    // Legacy helper kept for old project references. New projects use human-readable folders.
    public string GetProjectDirectory(Guid projectId)
    {
        var known = TryGetKnownAutosavePath(projectId);
        if (!string.IsNullOrWhiteSpace(known))
        {
            var dir = Path.GetDirectoryName(known);
            if (!string.IsNullOrWhiteSpace(dir)) { Directory.CreateDirectory(dir); return dir; }
        }
        var fallback = Path.Combine(ProjectsDirectory, projectId.ToString("N"));
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    public string GetProjectDirectory(CutFlowProject project)
    {
        var known = TryGetKnownAutosavePath(project.Id);
        if (!string.IsNullOrWhiteSpace(known))
        {
            var dir = Path.GetDirectoryName(known);
            if (!string.IsNullOrWhiteSpace(dir)) { Directory.CreateDirectory(dir); return dir; }
        }

        var desired = GetUniqueProjectDirectory(project.Name);
        Directory.CreateDirectory(desired);
        return desired;
    }

    public string GetAutosavePath(Guid projectId)
    {
        var known = TryGetKnownAutosavePath(projectId);
        return !string.IsNullOrWhiteSpace(known) ? known : Path.Combine(GetProjectDirectory(projectId), "autosave.cutflow");
    }

    public string GetAutosavePath(CutFlowProject project)
    {
        var known = TryGetKnownAutosavePath(project.Id);
        if (!string.IsNullOrWhiteSpace(known)) return known;
        var dir = GetProjectDirectory(project);
        return Path.Combine(dir, MakeSafeProjectName(project.Name) + ".cutflow");
    }

    public async Task AutosaveAsync(CutFlowProject project, CancellationToken cancellationToken = default)
    {
        project.SchemaVersion = Math.Max(project.SchemaVersion, 42);
        project.UpdatedUtc = DateTime.UtcNow;
        var path = GetAutosavePath(project);
        path = MigrateToHumanReadablePathIfNeeded(path, project);
        await WriteProjectAtomicAsync(path, project, cancellationToken).ConfigureAwait(false);
        await TouchStateAsync(project, path, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveManualAsync(string path, CutFlowProject project, CancellationToken cancellationToken = default)
    {
        project.SchemaVersion = Math.Max(project.SchemaVersion, 42);
        project.UpdatedUtc = DateTime.UtcNow;
        await WriteProjectAtomicAsync(path, project, cancellationToken).ConfigureAwait(false);
        await AutosaveAsync(project, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CutFlowProject?> LoadProjectAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<CutFlowProject>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CutFlowProject?> LoadRecentProjectAsync(RecentProjectEntry entry, CancellationToken cancellationToken = default)
    {
        var path = !string.IsNullOrWhiteSpace(entry.AutosavePath) ? entry.AutosavePath : GetAutosavePath(entry.Id);
        return await LoadProjectAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RecentProjectEntry>> GetRecentProjectsAsync(CancellationToken cancellationToken = default)
    {
        var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        return state.RecentProjects.OrderByDescending(x => x.UpdatedUtc).ToList();
    }

    public async Task<AppState> LoadStateAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(StatePath)) return new AppState();
        try
        {
            await using var stream = File.OpenRead(StatePath);
            return await JsonSerializer.DeserializeAsync<AppState>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false) ?? new AppState();
        }
        catch { return new AppState(); }
    }

    public async Task RenameProjectAsync(Guid id, string newName, CancellationToken cancellationToken = default)
    {
        newName = newName.Trim();
        if (string.IsNullOrWhiteSpace(newName)) throw new InvalidOperationException("Project name cannot be empty.");

        var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        var entry = state.RecentProjects.FirstOrDefault(x => x.Id == id);
        var oldPath = entry?.AutosavePath;
        if (string.IsNullOrWhiteSpace(oldPath) || !File.Exists(oldPath)) oldPath = GetAutosavePath(id);
        var project = await LoadProjectAsync(oldPath, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The autosaved project could not be found.", oldPath);

        project.Name = newName;
        project.SchemaVersion = Math.Max(project.SchemaVersion, 42);
        project.UpdatedUtc = DateTime.UtcNow;

        var oldDir = Path.GetDirectoryName(oldPath)!;
        var targetDir = GetUniqueProjectDirectory(newName, oldDir);
        if (!PathsEqual(oldDir, targetDir))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
            Directory.Move(oldDir, targetDir);
        }

        var targetPath = Path.Combine(targetDir, MakeSafeProjectName(newName) + ".cutflow");
        var movedOldPath = Path.Combine(targetDir, Path.GetFileName(oldPath));
        if (!PathsEqual(movedOldPath, targetPath) && File.Exists(movedOldPath)) File.Delete(movedOldPath);
        await WriteProjectAtomicAsync(targetPath, project, cancellationToken).ConfigureAwait(false);
        await TouchStateAsync(project, targetPath, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveProjectAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        var entry = state.RecentProjects.FirstOrDefault(x => x.Id == id);
        state.RecentProjects.RemoveAll(x => x.Id == id);
        if (state.LastProjectId == id) state.LastProjectId = state.RecentProjects.FirstOrDefault()?.Id;
        await WriteStateAtomicAsync(state, cancellationToken).ConfigureAwait(false);
        try
        {
            var dir = !string.IsNullOrWhiteSpace(entry?.AutosavePath) ? Path.GetDirectoryName(entry.AutosavePath) : Path.Combine(ProjectsDirectory, id.ToString("N"));
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch { }
    }

    public async Task ExportPackageAsync(string destinationPath, CutFlowProject project, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!project.HasMedia || !File.Exists(project.SourcePath))
            throw new FileNotFoundException("The source media for this project could not be found.", project.SourcePath);

        var portable = CloneProject(project);
        var mediaName = MakeSafeFileName(Path.GetFileName(project.SourcePath));
        portable.SourcePath = "media/" + mediaName;
        portable.OriginalSourcePath = portable.SourcePath;

        var temp = destinationPath + ".tmp";
        try
        {
            if (File.Exists(temp)) File.Delete(temp);
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1024 * 1024, true))
            {
                using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                var projectEntry = archive.CreateEntry("project.json", CompressionLevel.Fastest);
                await using (var projectStream = projectEntry.Open())
                    await JsonSerializer.SerializeAsync(projectStream, portable, _jsonOptions, cancellationToken).ConfigureAwait(false);
                progress?.Report(0.03);

                var mediaEntry = archive.CreateEntry(portable.SourcePath, CompressionLevel.NoCompression);
                await using var entryStream = mediaEntry.Open();
                await using var source = new FileStream(project.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
                var buffer = new byte[1024 * 1024];
                long copied = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read <= 0) break;
                    await entryStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    copied += read;
                    progress?.Report(0.03 + 0.97 * copied / Math.Max(1.0, source.Length));
                }
            }
            File.Move(temp, destinationPath, true);
            progress?.Report(1.0);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    public async Task<CutFlowProject> ImportPackageAsync(string packagePath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var projectEntry = archive.GetEntry("project.json") ?? throw new InvalidDataException("This CutFlow package is missing project.json.");
        CutFlowProject project;
        await using (var stream = projectEntry.Open())
        {
            project = await JsonSerializer.DeserializeAsync<CutFlowProject>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The project data inside this package is invalid.");
        }

        var sourceEntryName = project.SourcePath.Replace('\\', '/').TrimStart('/');
        var mediaEntry = archive.GetEntry(sourceEntryName) ?? archive.Entries.FirstOrDefault(x => x.FullName.StartsWith("media/", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(x.Name));
        if (mediaEntry is null) throw new InvalidDataException("This project package does not contain its source media.");

        project.Id = Guid.NewGuid();
        project.SchemaVersion = 42;
        project.CreatedUtc = DateTime.UtcNow;
        project.UpdatedUtc = DateTime.UtcNow;
        var mediaDir = Path.Combine(GetProjectDirectory(project), "Media");
        Directory.CreateDirectory(mediaDir);
        var destination = Path.Combine(mediaDir, MakeSafeFileName(mediaEntry.Name));

        await using (var source = mediaEntry.Open())
        await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true))
        {
            var buffer = new byte[1024 * 1024];
            long copied = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read <= 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied += read;
                progress?.Report(copied / Math.Max(1.0, mediaEntry.Length));
            }
        }

        project.SourcePath = destination;
        project.OriginalSourcePath = destination;
        await AutosaveAsync(project, cancellationToken).ConfigureAwait(false);
        progress?.Report(1.0);
        return project;
    }


    private void MigrateLegacyProjectsBestEffort()
    {
        try
        {
            if (!File.Exists(StatePath)) return;
            var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(StatePath), _jsonOptions);
            if (state is null || state.RecentProjects.Count == 0) return;
            var changed = false;
            foreach (var entry in state.RecentProjects)
            {
                if (string.IsNullOrWhiteSpace(entry.AutosavePath)) continue;
                var oldPath = entry.AutosavePath;
                var oldDir = Path.GetDirectoryName(oldPath);
                if (string.IsNullOrWhiteSpace(oldDir) || !Directory.Exists(oldDir)) continue;
                var legacyFolder = Path.GetFileName(oldDir).Equals(entry.Id.ToString("N"), StringComparison.OrdinalIgnoreCase);
                var legacyFile = Path.GetFileName(oldPath).Equals("autosave.cutflow", StringComparison.OrdinalIgnoreCase);
                if (!legacyFolder && !legacyFile) continue;

                var targetDir = GetUniqueProjectDirectory(entry.Name, oldDir);
                if (!PathsEqual(oldDir, targetDir)) Directory.Move(oldDir, targetDir);
                var movedPath = Path.Combine(targetDir, Path.GetFileName(oldPath));
                var targetPath = Path.Combine(targetDir, MakeSafeProjectName(entry.Name) + ".cutflow");
                if (File.Exists(movedPath) && !PathsEqual(movedPath, targetPath)) File.Move(movedPath, targetPath, true);
                else if (!File.Exists(targetPath) && File.Exists(oldPath)) File.Move(oldPath, targetPath, true);
                entry.AutosavePath = targetPath;
                changed = true;
            }
            if (changed)
            {
                var temp = StatePath + ".migrate.tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(state, _jsonOptions));
                File.Move(temp, StatePath, true);
            }
        }
        catch
        {
            // Migration is convenience-only. Never prevent CutFlow from starting if an old
            // project folder is locked or was moved outside the app.
        }
    }

    private async Task TouchStateAsync(CutFlowProject project, string autosavePath, CancellationToken cancellationToken)
    {
        var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        state.LastProjectId = project.Id;
        state.RecentProjects.RemoveAll(x => x.Id == project.Id);
        state.RecentProjects.Insert(0, new RecentProjectEntry
        {
            Id = project.Id,
            Name = project.Name,
            SourcePath = string.IsNullOrWhiteSpace(project.OriginalSourcePath) ? project.SourcePath : project.OriginalSourcePath,
            AutosavePath = autosavePath,
            UpdatedUtc = project.UpdatedUtc,
            DurationSeconds = project.Media.DurationSeconds,
            Width = project.Media.Width,
            Height = project.Media.Height,
            HasVideo = project.Media.HasVideo
        });
        if (state.RecentProjects.Count > 24) state.RecentProjects = state.RecentProjects.Take(24).ToList();
        await WriteStateAtomicAsync(state, cancellationToken).ConfigureAwait(false);
    }

    private string? TryGetKnownAutosavePath(Guid projectId)
    {
        try
        {
            if (!File.Exists(StatePath)) return null;
            var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(StatePath), _jsonOptions);
            var path = state?.RecentProjects.FirstOrDefault(x => x.Id == projectId)?.AutosavePath;
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch { return null; }
    }

    private string MigrateToHumanReadablePathIfNeeded(string path, CutFlowProject project)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(dir)) return path;
            var folderName = Path.GetFileName(dir);
            var fileName = Path.GetFileName(path);
            var legacyFolder = folderName.Equals(project.Id.ToString("N"), StringComparison.OrdinalIgnoreCase);
            var legacyFile = fileName.Equals("autosave.cutflow", StringComparison.OrdinalIgnoreCase);
            if (!legacyFolder && !legacyFile) return path;

            var targetDir = GetUniqueProjectDirectory(project.Name, dir);
            if (!PathsEqual(dir, targetDir))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
                Directory.Move(dir, targetDir);
            }
            var targetPath = Path.Combine(targetDir, MakeSafeProjectName(project.Name) + ".cutflow");
            var movedOldPath = Path.Combine(targetDir, fileName);
            if (!PathsEqual(movedOldPath, targetPath) && File.Exists(movedOldPath)) File.Delete(movedOldPath);
            return targetPath;
        }
        catch { return path; }
    }

    private string GetUniqueProjectDirectory(string projectName, string? allowExisting = null)
    {
        var baseName = MakeSafeProjectName(projectName);
        var candidate = Path.Combine(ProjectsDirectory, baseName);
        if (!Directory.Exists(candidate) || PathsEqual(candidate, allowExisting)) return candidate;
        for (var i = 2; i < 10000; i++)
        {
            candidate = Path.Combine(ProjectsDirectory, $"{baseName} ({i})");
            if (!Directory.Exists(candidate) || PathsEqual(candidate, allowExisting)) return candidate;
        }
        return Path.Combine(ProjectsDirectory, $"{baseName} {DateTime.Now:yyyyMMdd-HHmmss}");
    }

    private async Task WriteStateAtomicAsync(AppState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        var temp = StatePath + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(state, _jsonOptions), cancellationToken).ConfigureAwait(false);
        File.Move(temp, StatePath, true);
    }

    private async Task WriteProjectAtomicAsync(string path, CutFlowProject project, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temp = path + ".tmp";
        // Large waveform/analysis arrays can make JSON serialization noticeable if it runs on
        // the WPF dispatcher. Serialize off-thread so autosave/manual save never freezes clicks.
        var json = await Task.Run(() => JsonSerializer.Serialize(project, _jsonOptions), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, true);
    }

    private CutFlowProject CloneProject(CutFlowProject project)
    {
        var json = JsonSerializer.Serialize(project, _jsonOptions);
        return JsonSerializer.Deserialize<CutFlowProject>(json, _jsonOptions) ?? throw new InvalidOperationException("Could not copy project data.");
    }

    private static string MakeSafeProjectName(string name)
    {
        var safe = MakeSafeFileName(name.Trim());
        safe = safe.Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(safe) ? "Untitled Project" : safe;
    }

    private static string MakeSafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "media.bin" : safe;
    }

    private static bool PathsEqual(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try { return Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }
}
