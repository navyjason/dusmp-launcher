using System.Diagnostics;
using System.Runtime.CompilerServices;
using DusmpLauncher.Core;
using DusmpLauncher.Configuration;
using DusmpLauncher.Models;

namespace DusmpLauncher.Modpack;

/// <summary>
/// Decides what the installation is missing and what may be removed from it.
/// <para>
/// Two rules shape everything here. First, the manifest plus the managed-file
/// ledger are the only sources of truth about which files belong to the launcher:
/// the instance folder is never walked looking for strangers, because the
/// "strangers" are the player's own mods, configs and worlds. Second, hashing is
/// the expensive part of a start, so a file is only re-read when its size or
/// mtime stopped matching what the ledger recorded at the last commit.
/// </para>
/// </summary>
public sealed class ModpackManager : IModpackManager
{
    /// <summary>
    /// Progress is coalesced to this interval. Reporting per file would push tens of
    /// thousands of events through the bridge for a pass that takes a second.
    /// </summary>
    private const int ProgressIntervalMs = 100;

    private readonly LauncherPaths _paths;
    private readonly Action<string>? _trace;

    /// <summary>
    /// What each planned file looked like on disk while the plan was being built,
    /// remembered per plan instance. <see cref="CommitAsync"/> uses it to tell a file
    /// that was really rewritten from one whose download failed, so the ledger never
    /// vouches for content that is still corrupt. Weak keys mean an abandoned plan
    /// costs nothing.
    /// </summary>
    private readonly ConditionalWeakTable<InstallPlan, PlanObservations> _observations = new();

    /// <param name="paths">Resolved launcher paths; also the authority on what may be deleted.</param>
    /// <param name="trace">
    /// Optional sink for technical, non-player-facing notes about work that was
    /// skipped — a locked file, a rejected deletion. Skipping is normal and must not
    /// fail an update, but it still has to be visible in the log pane.
    /// </param>
    public ModpackManager(LauncherPaths paths, Action<string>? trace = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _trace = trace;
    }

    /// <inheritdoc />
    public async Task<InstallPlan> BuildPlanAsync(
        ModpackManifest manifest,
        IReadOnlyDictionary<string, bool> optionalChoices,
        bool force = false,
        IProgress<ProgressSnapshot>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ct.ThrowIfCancellationRequested();

        var choices = optionalChoices ?? new Dictionary<string, bool>(0, StringComparer.Ordinal);
        var files = manifest.Files ?? Array.Empty<ModFile>();
        var ledger = await ManagedFileIndex.LoadAsync(_paths.ManagedIndexFile, ct).ConfigureAwait(false);

        var toDownload = new List<PlannedFile>();
        var toDelete = new List<PlannedFile>();
        var upToDate = new List<PlannedFile>();
        var preserved = new List<PlannedFile>();
        var observations = new PlanObservations();

        // Every manifest path that got a verdict. Doubles as the duplicate guard and
        // as the "still listed" set for the obsolete pass below.
        var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var total = files.Count;
        var examined = 0;
        var clock = Stopwatch.StartNew();
        var lastReportMs = long.MinValue;

        Report(progress, 0, total, null);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var relative = file is null ? "" : ManagedFileIndex.NormalizePath(file.Path);
            if (file is not null && relative.Length > 0 && handled.Add(relative))
                await ClassifyAsync(file, relative).ConfigureAwait(false);

            // Counted even when skipped, so the bar reflects the pass and always ends at 100.
            examined++;

            var elapsed = clock.ElapsedMilliseconds;
            if (progress is not null && (examined == total || elapsed - lastReportMs >= ProgressIntervalMs))
            {
                lastReportMs = elapsed;
                Report(progress, examined, total, relative.Length > 0 ? relative : null);
            }
        }

        // Obsolete pass: ledger entries the new manifest no longer lists. This is the
        // only place a file becomes deletable without the manifest naming it, and it
        // is bounded by what the launcher itself wrote.
        foreach (var entry in ledger.Entries.OrderBy(e => e.Path, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();

            if (handled.Contains(entry.Path))
                continue;

            // A write-once file was handed to the player on purpose; dropping it from
            // the manifest means the launcher stops managing it, not that the player's
            // edited config should be destroyed. It simply leaves the ledger at commit.
            if (entry.WriteOnce || PerformanceProfile.IsProfileFile(entry.Path))
            {
                _trace?.Invoke($"modpack: releasing write-once file to the player: {entry.Path}");
                continue;
            }

            string absolute;
            try
            {
                absolute = _paths.ResolveInstanceFile(entry.Path);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // A ledger written by a future or tampered build: refuse to touch it.
                _trace?.Invoke($"modpack: ignoring unresolvable ledger path: {entry.Path} ({ex.GetType().Name})");
                continue;
            }

            // Renaming a mod to .jar.disabled is a player choice, even when a
            // later manifest drops or changes that mod. Do not adopt or remove
            // the disabled file as part of normal managed-file maintenance.
            if (IsDisabledModPath(entry.Path) || HasDisabledCounterpart(entry.Path, absolute))
                continue;

            if (TryStat(absolute) is null)
                continue;

            toDelete.Add(new PlannedFile(FileFromLedger(entry), FileAction.Obsolete, absolute));
        }

        var plan = new InstallPlan
        {
            Manifest = manifest,
            ToDownload = toDownload,
            ToDelete = toDelete,
            UpToDate = upToDate,
            Preserved = preserved,
        };

        _observations.Remove(plan);
        _observations.Add(plan, observations);

        Report(progress, total, total, null);
        return plan;

        async Task ClassifyAsync(ModFile file, string relative)
        {
            string absolute;
            try
            {
                absolute = _paths.ResolveInstanceFile(relative);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // The manifest tried to write outside the instance. Refusing the whole
                // plan is the only safe answer; a single skipped file would leave the
                // install silently incomplete.
                throw new ManifestValidationException(ManifestErrorKeys.PathTraversal, relative);
            }

            // Disabled mods deliberately do not match the active manifest.
            // Honour this before optional selection and hash/repair checks:
            // neither a launch nor a forced repair may restore an enabled JAR.
            if (HasDisabledCounterpart(relative, absolute))
            {
                preserved.Add(new PlannedFile(file, FileAction.PreservedByUser, absolute + ".disabled"));
                return;
            }
            if (IsDisabledModPath(relative) && TryStat(absolute) is not null)
            {
                preserved.Add(new PlannedFile(file, FileAction.PreservedByUser, absolute));
                return;
            }

            var stat = TryStat(absolute);
            if (stat is not null)
                observations.Record(relative, stat.Value);

            // An optional mod the player turned off: never downloaded, and removed if a
            // previous choice left it behind. The toggle is explicit consent to delete.
            if (!IsSelected(file, choices))
            {
                if (stat is not null)
                    toDelete.Add(new PlannedFile(file, FileAction.Deselected, absolute));
                return;
            }

            if (stat is null)
            {
                toDownload.Add(new PlannedFile(file, FileAction.Missing, absolute));
                return;
            }

            // The publisher can explicitly hand configuration ownership to the player.
            if (file.WriteOnce)
            {
                preserved.Add(new PlannedFile(file, FileAction.PreservedByUser, absolute));
                return;
            }

            if (PerformanceProfile.IsProfileFile(relative))
            {
                // Never silently discard a newly published config. If the old file
                // is still the publisher's previous version, it is safe to update.
                // Otherwise require a decision rather than overwrite player edits.
                if (ledger.TryGet(relative, out var previous) && !previous.WriteOnce &&
                    !ManagedFileIndex.HashEquals(previous.Sha256, file.Sha256))
                {
                    var profileHash = await ManagedFileIndex.ComputeSha256Async(absolute, ct: ct).ConfigureAwait(false);
                    if (!ManagedFileIndex.HashEquals(profileHash, previous.Sha256))
                        throw new LauncherException("error.modpack.configConflict", $"The pack updated {relative}, but the existing file was changed. Move or remove that file, then retry; the launcher will download the new version.");
                    toDownload.Add(new PlannedFile(file, FileAction.Corrupt, absolute));
                    return;
                }
                preserved.Add(new PlannedFile(file, FileAction.PreservedByUser, absolute));
                return;
            }

            // Ledger hit. Size and mtime unchanged since the last commit means the
            // content is what we recorded, so the recorded hash answers the question
            // without touching the disk. Entries recorded for a write-once file are
            // excluded: their hash was never verified against the content.
            if (!force
                && ledger.TryGet(relative, out var recorded)
                && !recorded.WriteOnce
                && ManagedFileIndex.MatchesDisk(recorded, stat.Value.Size, stat.Value.LastWriteTicksUtc))
            {
                if (ManagedFileIndex.HashEquals(recorded.Sha256, file.Sha256))
                    upToDate.Add(new PlannedFile(file, FileAction.UpToDate, absolute));
                else
                    toDownload.Add(new PlannedFile(file, FileAction.Corrupt, absolute));
                return;
            }

            // A wrong length cannot hash correctly, so this saves a full read on the
            // common half-downloaded case. Size 0 in the manifest means "unknown".
            if (file.Size > 0 && stat.Value.Size != file.Size)
            {
                toDownload.Add(new PlannedFile(file, FileAction.Corrupt, absolute));
                return;
            }

            string? actual = null;
            try
            {
                actual = await ManagedFileIndex.ComputeSha256Async(absolute, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable is indistinguishable from wrong: re-fetch it, and let the
                // downloader surface a real error if the file is genuinely locked.
                _trace?.Invoke($"modpack: cannot hash {relative}: {ex.GetType().Name}");
            }

            if (actual is not null && ManagedFileIndex.HashEquals(actual, file.Sha256))
                upToDate.Add(new PlannedFile(file, FileAction.UpToDate, absolute));
            else
                toDownload.Add(new PlannedFile(file, FileAction.Corrupt, absolute));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ApplyDeletionsAsync(InstallPlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // Deleting thousands of files is synchronous I/O; keeping it off the caller's
        // thread keeps the WebView responsive while an update is being applied.
        return Task.Run<IReadOnlyList<string>>(() => DeleteAll(plan, ct), ct);
    }

    /// <inheritdoc />
    public async Task CommitAsync(InstallPlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ct.ThrowIfCancellationRequested();

        _observations.TryGetValue(plan, out var observed);

        var index = new ManagedFileIndex { ModpackVersion = plan.Manifest?.Version };

        foreach (var planned in Ledgerable(plan))
        {
            ct.ThrowIfCancellationRequested();

            if (planned?.File is null)
                continue;

            var relative = ManagedFileIndex.NormalizePath(planned.File.Path);
            if (relative.Length == 0)
                continue;

            string absolute;
            try
            {
                absolute = _paths.ResolveInstanceFile(relative);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                continue;
            }

            // A disabled JAR is kept outside the canonical managed ledger. Its
            // bytes have not been verified against this manifest, and recording
            // them under .jar would claim ownership of a different file.
            if (IsDisabledModPath(relative)
                || HasDisabledCounterpart(relative, absolute)
                || (IsModJarPath(relative) && planned.Action == FileAction.PreservedByUser
                    && planned.AbsolutePath.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)))
                continue;

            var stat = TryStat(absolute);

            // Not on disk: a download that never landed, or a preserved file the player
            // deleted. Either way it is not a file the launcher currently owns.
            if (stat is null)
                continue;

            // Planned for download and byte-identical to what we saw before the run
            // means nothing was written: recording it would make the next start trust a
            // file that is still missing content, and hide the damage from Verify.
            if (planned.Action is FileAction.Missing or FileAction.Corrupt
                && observed is not null
                && observed.TryGet(relative, out var before)
                && before.Size == stat.Value.Size
                && before.LastWriteTicksUtc == stat.Value.LastWriteTicksUtc)
            {
                _trace?.Invoke($"modpack: not recording unwritten file: {relative}");
                continue;
            }

            index.Set(new ManagedFileEntry
            {
                Path = relative,
                // The manifest hash, not a fresh one: up-to-date files were just
                // verified and downloaded files were verified by the downloader, so
                // re-reading every byte here would double the cost of an install.
                Sha256 = (planned.File.Sha256 ?? "").ToLowerInvariant(),
                Size = stat.Value.Size,
                LastWriteTicksUtc = stat.Value.LastWriteTicksUtc,
                WriteOnce = planned.File.WriteOnce,
                OptionalId = planned.File.OptionalId,
            });
        }

        await index.SaveAsync(_paths.ManagedIndexFile, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Everything that belongs in the ledger after a successful run: files already
    /// correct, files the downloader wrote, and write-once files left to the player.
    /// Obsolete and deselected paths are absent, which is how they stop being managed.
    /// </summary>
    private static IEnumerable<PlannedFile> Ledgerable(InstallPlan plan)
    {
        foreach (var planned in plan.UpToDate ?? Array.Empty<PlannedFile>())
            yield return planned;
        foreach (var planned in plan.Preserved ?? Array.Empty<PlannedFile>())
            yield return planned;
        foreach (var planned in plan.ToDownload ?? Array.Empty<PlannedFile>())
            yield return planned;
    }

    private IReadOnlyList<string> DeleteAll(InstallPlan plan, CancellationToken ct)
    {
        var removed = new List<string>();
        var emptiedDirectories = new HashSet<string>(StringComparer.Ordinal);

        foreach (var planned in plan.ToDelete ?? Array.Empty<PlannedFile>())
        {
            // Cancelling here is safe: files already deleted stay deleted, and the
            // ledger is untouched until CommitAsync, so the next plan re-derives the
            // remaining work from disk rather than from a half-applied state.
            ct.ThrowIfCancellationRequested();

            if (planned?.File is null)
                continue;

            var relative = ManagedFileIndex.NormalizePath(planned.File.Path);
            if (relative.Length == 0)
                continue;

            // The one authority on this. Player-owned paths are skipped, never thrown
            // on, because one protected path must not abort the rest of an update.
            if (!LauncherPaths.IsDeletable(relative))
            {
                _trace?.Invoke($"modpack: refusing to delete player-owned path: {relative}");
                continue;
            }

            string absolute;
            try
            {
                // Re-derived rather than taken from the plan: the absolute path is the
                // part that decides whether we write outside the instance folder, so it
                // is recomputed from the path authority every time.
                absolute = _paths.ResolveInstanceFile(relative);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                _trace?.Invoke($"modpack: refusing to delete outside the instance: {relative}");
                continue;
            }

            if (IsDisabledModPath(relative) || HasDisabledCounterpart(relative, absolute))
                continue;

            try
            {
                if (!File.Exists(absolute))
                    continue;

                File.Delete(absolute);
                removed.Add(relative);

                var directory = Path.GetDirectoryName(absolute);
                if (!string.IsNullOrEmpty(directory))
                    emptiedDirectories.Add(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Locked by a running game or an antivirus scan. The next verification
                // pass will plan it again; failing the update would be worse.
                _trace?.Invoke($"modpack: could not delete {relative}: {ex.GetType().Name}");
            }
        }

        PruneEmptyDirectories(emptiedDirectories, ct);
        return removed;
    }

    /// <summary>
    /// Removes directories that the deletions just emptied, walking upward while the
    /// folder stays empty. Player-owned trees are never entered, and the instance root
    /// itself is never removed, so an update cannot leave the install headless.
    /// </summary>
    private void PruneEmptyDirectories(IEnumerable<string> startingPoints, CancellationToken ct)
    {
        var instanceRoot = Path.GetFullPath(_paths.Instance);
        var rootWithSeparator = instanceRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        foreach (var start in startingPoints)
        {
            var current = start;

            while (!string.IsNullOrEmpty(current))
            {
                ct.ThrowIfCancellationRequested();

                string full;
                try
                {
                    full = Path.GetFullPath(current);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    break;
                }

                // Stops at the instance root, which is not inside rootWithSeparator.
                if (!full.StartsWith(rootWithSeparator, StringComparison.Ordinal))
                    break;

                var relative = ManagedFileIndex.NormalizePath(Path.GetRelativePath(instanceRoot, full));
                if (!IsPrunableDirectory(relative))
                    break;

                try
                {
                    if (Directory.Exists(full))
                    {
                        if (Directory.EnumerateFileSystemEntries(full).Any())
                            break;

                        Directory.Delete(full, recursive: false);
                        _trace?.Invoke($"modpack: removed empty directory: {relative}");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    break;
                }

                current = Path.GetDirectoryName(full);
            }
        }
    }

    /// <summary>
    /// A directory may only be pruned when it sits inside the instance and is not a
    /// player-owned tree. The trailing slash makes
    /// <see cref="LauncherPaths.IsDeletable"/> match its directory prefixes, which is
    /// how <c>saves</c> and friends are excluded here as well as for files.
    /// </summary>
    private static bool IsPrunableDirectory(string relativeDirectory)
    {
        if (relativeDirectory.Length == 0 || relativeDirectory is "." or "..")
            return false;

        return LauncherPaths.IsDeletable(relativeDirectory.TrimEnd('/') + "/");
    }

    /// <summary>
    /// A mandatory file is always selected. An optional one follows the player's
    /// answer, and an id the caller did not mention counts as enabled — the choices
    /// map only ever says "off" deliberately.
    /// </summary>
    private static bool IsSelected(ModFile file, IReadOnlyDictionary<string, bool> choices)
    {
        if (file.OptionalId is not { Length: > 0 } optionalId)
            return true;

        return !choices.TryGetValue(optionalId, out var enabled) || enabled;
    }

    private static bool IsModJarPath(string relative) =>
        relative.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)
        && relative.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);

    private static bool IsDisabledModPath(string relative) =>
        relative.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)
        && relative.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase);

    private static bool HasDisabledCounterpart(string relative, string absolute) =>
        IsModJarPath(relative) && File.Exists(absolute + ".disabled");

    /// <summary>
    /// Size and mtime of a file, or null when it is absent, is actually a directory,
    /// or cannot be stat'ed. Treating all three as "not there" is right: each one
    /// means the launcher does not currently have usable content at that path.
    /// </summary>
    private static DiskStat? TryStat(string absolutePath)
    {
        try
        {
            var info = new FileInfo(absolutePath);
            if (!info.Exists)
                return null;

            return new DiskStat(info.Length, info.LastWriteTimeUtc.Ticks);
        }
        catch (Exception ex) when (ex is IOException
                                     or UnauthorizedAccessException
                                     or NotSupportedException
                                     or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Rebuilds a <see cref="ModFile"/> for a ledger-only path, because
    /// <see cref="PlannedFile"/> is expressed in manifest terms and an obsolete file
    /// has no manifest entry left. It carries no URL: nothing will ever download it.
    /// </summary>
    private static ModFile FileFromLedger(ManagedFileEntry entry) => new()
    {
        Path = entry.Path,
        Size = entry.Size,
        Sha256 = entry.Sha256,
        Url = "",
        Mirrors = Array.Empty<string>(),
        OptionalId = entry.OptionalId,
        WriteOnce = entry.WriteOnce,
    };

    private static void Report(IProgress<ProgressSnapshot>? progress, int done, int total, string? currentFile)
    {
        progress?.Report(new ProgressSnapshot
        {
            Stage = LauncherStage.VerifyingFiles,
            // Measured, never synthetic: files verified over files in the manifest.
            Percent = total > 0 ? Math.Clamp(done * 100.0 / total, 0d, 100d) : 100d,
            CurrentFile = currentFile,
            FilesCompleted = done,
            FilesTotal = total,
        });
    }

    /// <summary>Size and last-write time as read in a single stat.</summary>
    private readonly record struct DiskStat(long Size, long LastWriteTicksUtc);

    /// <summary>
    /// Per-plan memory of what was on disk at planning time. A class because
    /// <see cref="ConditionalWeakTable{TKey,TValue}"/> needs a reference value.
    /// </summary>
    private sealed class PlanObservations
    {
        private readonly Dictionary<string, DiskStat> _stats = new(StringComparer.OrdinalIgnoreCase);

        public void Record(string relativePath, DiskStat stat) => _stats[relativePath] = stat;

        public bool TryGet(string relativePath, out DiskStat stat) => _stats.TryGetValue(relativePath, out stat);
    }
}
