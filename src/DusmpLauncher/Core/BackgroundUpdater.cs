using System.Net.Http;
using System.Diagnostics;
using DusmpLauncher.Configuration;
using DusmpLauncher.Downloads;
using DusmpLauncher.Modpack;
using DusmpLauncher.Network;

namespace DusmpLauncher.Core;

/// <summary>
/// Headless DUSMP updater process. It reuses the launcher manifest, file ledger,
/// download verifier and external-instance registry instead of maintaining a
/// second update implementation.
/// </summary>
public sealed class BackgroundUpdater
{
    private readonly LauncherPaths _paths;
    private readonly CancellationToken _stop;
    public BackgroundUpdater(LauncherPaths paths, CancellationToken stop) { _paths=paths; _stop=stop; }

    public async Task RunAsync(bool once)
    {
        var minutes=int.TryParse(Environment.GetEnvironmentVariable("DUSMP_UPDATER_POLL_MINUTES"),out var configured)?Math.Clamp(configured,5,1440):30;
        do
        {
            try { await UpdateAllAsync(_stop).ConfigureAwait(false); }
            catch(OperationCanceledException) when(_stop.IsCancellationRequested) { break; }
            catch(Exception ex) { Console.Error.WriteLine($"Updater: {ex.Message}"); }
            if(once||_stop.IsCancellationRequested)break;
            try { await Task.Delay(TimeSpan.FromMinutes(minutes),_stop).ConfigureAwait(false); }
            catch(OperationCanceledException) when(_stop.IsCancellationRequested) { break; }
        } while(!_stop.IsCancellationRequested);
    }

    private async Task UpdateAllAsync(CancellationToken ct)
    {
        if (GameOrLauncherIsRunning())
        { Console.WriteLine("Update deferred while a launcher or Minecraft process is running."); return; }
        var config = new ConfigStore(_paths);
        await config.LoadAsync(ct).ConfigureAwait(false);
        var registry = new ExternalLauncherManager(_paths);
        var installations = await registry.GetInstallationsAsync(ct).ConfigureAwait(false);
        var editions = installations.Where(x => x.Managed && x.Edition is { Length: > 0 })
            .Select(x => x.Edition!);
        if (!config.Current.FirstLaunch || !string.IsNullOrWhiteSpace(config.Current.Edition))
            editions = editions.Append(config.Current.Edition ?? "");
        var distinctEditions = editions
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        using var http = Program.CreateHttpClient();
        foreach (var edition in distinctEditions)
        {
            ct.ThrowIfCancellationRequested();
            if (GameOrLauncherIsRunning()) return;
            try { await UpdateEditionAsync(edition, config, installations, http, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Console.Error.WriteLine($"Updater: edition {edition}: {ex.Message}"); }
        }
    }

    private async Task UpdateEditionAsync(string edition, ConfigStore config, IReadOnlyList<ManagedInstallation> installations, HttpClient http, CancellationToken ct)
    {
        var active = string.Equals(edition, config.Current.Edition ?? "", StringComparison.OrdinalIgnoreCase);
        var paths = string.IsNullOrWhiteSpace(edition) ? _paths : _paths.ForEdition(edition);
        paths.EnsureCreated();
        var api = new ApiClient(http);
        var mirror = new MirrorManager();
        var download = new DownloadManager(http, config);
        var manifestStore = new ManifestManager(paths);
        var managed = new ModpackManager(paths, Console.WriteLine);
        var optionalStore = new OptionalModsManager(() => config.Current, () => config.SaveAsync());
        using var game = new GameLauncher(paths, http);
        var external = new ExternalLauncherManager(paths);
        var latest = await api.GetModpackLatestAsync(edition, ct).ConfigureAwait(false);
        var manifest = await api.GetModpackManifestAsync(latest.Version, ct).ConfigureAwait(false);
        manifestStore.Validate(manifest);
        if (!string.Equals(latest.Version, manifest.Version, StringComparison.Ordinal))
            throw new InvalidDataException("Latest modpack version and manifest disagree.");
        if (!string.IsNullOrWhiteSpace(edition) && !string.IsNullOrWhiteSpace(manifest.Edition) && !string.Equals(manifest.Edition, edition, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Manifest belongs to a different edition.");
        if (!active && string.IsNullOrWhiteSpace(manifest.Edition))
            throw new InvalidDataException("The server did not identify the manifest edition; another profile cannot safely be updated.");
        await manifestStore.SaveCacheAsync(manifest, ct).ConfigureAwait(false);
        var regions = await api.GetRegionsAsync(ct).ConfigureAwait(false);
        var region = regions.FirstOrDefault(r => r.Available && r.Id == config.Current.PreferredRegionId)
            ?? regions.Where(r => r.Available).OrderBy(r => r.Priority).FirstOrDefault();
        if (region is not null) mirror.SetCdnBases(region.CdnBases);
        var optional = await api.GetOptionalModsAsync(manifest.Version, ct).ConfigureAwait(false);
        var choices = optionalStore.CurrentChoices(optional);
        var plan = await managed.BuildPlanAsync(manifest, choices, force: false, null, ct).ConfigureAwait(false);
        if (GameOrLauncherIsRunning()) return;
        await managed.ApplyDeletionsAsync(plan, ct).ConfigureAwait(false);
        if (active && config.Current.PlayMode != "external") await game.EnsureLoaderInstalledAsync(manifest, null, ct).ConfigureAwait(false);
        var jobs = plan.ToDownload.Select(file => new DownloadJob(file.File.Path, file.AbsolutePath, file.File.Size, file.File.Sha256, mirror.ResolveUrls(file.File))).ToArray();
        if (jobs.Length > 0)
        {
            var result = await download.RunAsync(jobs, ct).ConfigureAwait(false);
            if (!result.Success) throw new IOException(result.Failed.FirstOrDefault()?.Detail ?? "Download was cancelled.");
        }
        await managed.CommitAsync(plan, ct).ConfigureAwait(false);
        foreach (var changed in plan.ToDownload.Where(x => PerformanceProfile.IsProfileFile(x.File.Path)))
            config.Current.PerformanceProfileApplied?.Remove(Path.GetFullPath(paths.Instance) + "|" + changed.File.Path.Replace('\\', '/'));
        if (active) config.Current.InstalledModpackVersion = manifest.Version;
        await config.SaveAsync(ct).ConfigureAwait(false);
        foreach (var installation in installations.Where(x => x.Managed && x.Launcher is ("prism" or "official" or "modrinth" or "curseforge" or "polymc" or "multimc")))
        {
            ct.ThrowIfCancellationRequested();
            var sameEdition = !string.IsNullOrWhiteSpace(installation.Edition)
                ? string.Equals(installation.Edition, edition, StringComparison.OrdinalIgnoreCase)
                : active && installation.Launcher == config.Current.ExternalLauncherId && !string.IsNullOrWhiteSpace(config.Current.ExternalInstancePath)
                  && Path.GetFullPath(installation.InstancePath).Equals(Path.GetFullPath(config.Current.ExternalInstancePath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            if (!sameEdition || !Directory.Exists(installation.InstancePath)) continue;
            if (GameOrLauncherIsRunning()) return;
            try
            {
                var changed = await external.SyncManagedFilesAsync(installation, manifest, choices, ct).ConfigureAwait(false);
                var root = ExternalLauncherManager.ContentRoot(installation);
                foreach (var relative in changed) config.Current.PerformanceProfileApplied?.Remove(Path.GetFullPath(root) + "|" + relative);
                await UpdateNotifierInstaller.InstallAsync(root, manifest, edition, ct).ConfigureAwait(false);
                await config.SaveAsync(ct).ConfigureAwait(false);
                if (installation.Launcher == "official")
                {
                    var host = installation.LauncherRoot ?? Path.GetDirectoryName(installation.InstancePath)!;
                    var version = await game.EnsureLoaderInstalledInAsync(host, manifest, null, ct).ConfigureAwait(false);
                    await external.WriteOfficialProfileAsync(installation, version, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Console.Error.WriteLine($"Updater: {installation.Launcher} profile: {ex.Message}"); }
        }
    }

    private static bool GameOrLauncherIsRunning()
    {
        foreach(var process in Process.GetProcesses())
        {
            using(process)
            {
                try { var name=process.ProcessName; if(new[] { "java", "javaw", "DusmpLauncher", "prismlauncher", "minecraft-launcher", "MinecraftLauncher", "Minecraft", "Modrinth App", "modrinth-app", "theseus", "CurseForge", "PolyMC", "MultiMC" }.Contains(name,StringComparer.OrdinalIgnoreCase))return true; }
                catch(Exception ex) when(ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return false;
    }
}
