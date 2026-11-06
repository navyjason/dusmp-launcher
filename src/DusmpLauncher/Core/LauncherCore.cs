using System.Diagnostics;
using DusmpLauncher.Authentication;
using DusmpLauncher.Bridge;
using DusmpLauncher.Configuration;
using DusmpLauncher.Downloads;
using DusmpLauncher.Localization;
using DusmpLauncher.Models;
using DusmpLauncher.Modpack;
using DusmpLauncher.Network;
using DusmpLauncher.Updates;

namespace DusmpLauncher.Core;

/// <summary>
/// The launcher's state machine. Startup runs once and kicks the modpack download
/// off in the background; onboarding then happens on top of a download already in
/// flight, which is the whole point of the spec's first-run flow.
/// </summary>
public sealed class LauncherCore : ILauncherCore
{
    private readonly LauncherPaths _paths;

    /// <summary>The chosen edition's folders, or the shared ones before a choice.</summary>
    private LauncherPaths _editionPaths;

    /// <summary>Builds the per-edition components. Null leaves the ones handed in.</summary>
    private readonly EditionComponentFactory? _editionComponents;

    private IReadOnlyList<ModpackEdition> _editions = Array.Empty<ModpackEdition>();
    private readonly IConfigStore _config;
    private readonly IApiClient _api;
    private readonly ILanguageManager _languages;
    // Not readonly: these three are rebuilt against the chosen edition's
    // folders, because each edition is its own installation.
    private IModpackManager _modpack;
    private IManifestManager _manifests;
    private readonly IOptionalModsManager _optionalMods;
    private readonly IDownloadManager _downloads;
    private readonly IMirrorManager _mirrors;

    /// <summary>
    /// Finds the public homes of public mods. Null disables the lookup entirely,
    /// which is a supported configuration: everything then comes from DUSMP, as
    /// it did before this existed.
    /// </summary>
    private readonly ExternalMirrorResolver? _externalMirrors;

    private readonly IRegionSelector _regions;
    private readonly IMicrosoftAuth _microsoft;
    private readonly IOfflineAuth _offline;
    private IGameLauncher _game;
    private readonly ConnectivityChecker _connectivity;
    private readonly ILauncherUpdateService? _launcherUpdates;
    private bool _launcherUpdateBlocked;
    private readonly Action? _ensureShortcuts;

    /// <summary>Null disables automatic translation of news.</summary>
    private readonly NewsTranslator? _newsTranslator;
    private readonly ModrinthMetadataResolver? _modMetadata;
    private readonly MinecraftAppearance? _appearance;
    private readonly Accounts.AccountStore _accounts;
    private ExternalLauncherManager _externalLaunchers;

    /// <summary>Serialises multi-step operations that must not interleave.</summary>
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly SemaphoreSlim _installGate = new(1, 1);
    private string? _preparedVersionId;
    private long _lastProgressAt;

    /// <summary>Guards the single <see cref="_state"/> read-modify-write.</summary>
    private readonly object _stateLock = new();

    private LauncherState _state = new();
    private ModpackManifest? _manifest;
    private IReadOnlyList<OptionalMod> _remoteOptionalMods = Array.Empty<OptionalMod>();
    private GameSession? _session;

    /// <summary>The background modpack download, so Play can wait on it instead of restarting it.</summary>
    private Task<DownloadResult>? _backgroundDownload;
    private CancellationTokenSource? _backgroundCts;
    private readonly object _exitRefreshLock = new();
    private Task<bool>? _exitRefresh;
    private int _stopRequested;
    private readonly object _launchCancellationLock = new();
    private CancellationTokenSource? _launchCancellation;


    public LauncherCore(
        LauncherPaths paths,
        IConfigStore config,
        IApiClient api,
        ILanguageManager languages,
        IModpackManager modpack,
        IManifestManager manifests,
        IOptionalModsManager optionalMods,
        IDownloadManager downloads,
        IMirrorManager mirrors,
        IRegionSelector regions,
        IMicrosoftAuth microsoft,
        IOfflineAuth offline,
        IGameLauncher game,
        ConnectivityChecker connectivity,
        ExternalMirrorResolver? externalMirrors = null,
        NewsTranslator? newsTranslator = null,
        ModrinthMetadataResolver? modMetadata = null,
        MinecraftAppearance? appearance = null,
        EditionComponentFactory? editionComponents = null,
        ILauncherUpdateService? launcherUpdates = null,
        Action? ensureShortcuts = null)
    {
        _paths = paths;
        _config = config;
        _api = api;
        _languages = languages;
        _modpack = modpack;
        _manifests = manifests;
        _optionalMods = optionalMods;
        _downloads = downloads;
        _mirrors = mirrors;
        _externalMirrors = externalMirrors;
        _regions = regions;
        _microsoft = microsoft;
        _offline = offline;
        _game = game;
        _connectivity = connectivity;
        _launcherUpdates = launcherUpdates;
        _launcherUpdateBlocked = launcherUpdates is not null;
        _ensureShortcuts = ensureShortcuts;
        _state = _state with { LauncherUpdatePending = _launcherUpdateBlocked };
        _newsTranslator = newsTranslator;
        _modMetadata = modMetadata;
        _appearance = appearance;
        _editionComponents = editionComponents;
        _editionPaths = paths;
        _accounts = new Accounts.AccountStore(paths.AccountsFile);
        _externalLaunchers = new ExternalLauncherManager(paths);

        _downloads.StatsChanged += OnDownloadStats;
        _game.GameExited += OnGameExited;
    }

    public LauncherState State { get { lock (_stateLock) return _state; } }

    public GameSettings CurrentSettings => _config.Current.Game;

    public IReadOnlyList<ExternalLauncherInfo> GetExternalLaunchers()
    {
        var order = new[] { "modrinth", "curseforge", "prism", "multimc", "polymc" };
        var detected = _externalLaunchers.Detect();
        return order.Select(id => detected.FirstOrDefault(item => item.Id == id))
            .Where(item => item is not null).ToArray()!;
    }

    public async Task SetPlayModeAsync(string mode, CancellationToken ct = default)
    {
        if (mode is not ("dusmp" or "external")) throw new LauncherException("error.external.invalidMode", mode);
        await _installGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State.GameState != GameRunState.Idle) throw new LauncherException("error.game.running");
            _config.Current.PlayMode = mode;
            await _config.SaveAsync(ct).ConfigureAwait(false);
            // Only the built-in path needs an account of our own: in external mode the
            // host launcher owns the session, so DUSMP never asks for one.
            Mutate(s => s with
            {
                PlayMode = mode,
                InstancePath = CurrentGameDirectory(),
                Screen = mode == "external"
                    ? _manifest is null && _editions.Count > 0 ? BridgeProtocol.Screens.OnboardingEdition : BridgeProtocol.Screens.OnboardingExternalLauncher
                    : !_config.Current.FirstLaunch && _session is not null ? BridgeProtocol.Screens.Main : BridgeProtocol.Screens.OnboardingAccount,
            });
        }
        finally { _installGate.Release(); }
    }

    public async Task SelectExternalLauncherAsync(string id, string? root, CancellationToken ct = default)
    {
        // Every launcher DUSMP has an adapter for: the MultiMC family, which
        // takes a real instance, and the official launcher, which takes a
        // profile pointed at a game directory.
        if (id is not ("official" or "modrinth" or "curseforge") && !ExternalLauncherDiscovery.MmcFamily.Contains(id))
            throw new LauncherException("error.external.adapterUnavailable", id);
        await _installGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State.GameState != GameRunState.Idle) throw new LauncherException("error.game.running");
            if (_manifest is null) throw new LauncherException("error.manifest.invalid", "Cannot prepare an external instance before the modpack manifest is loaded.");
            var installation = id is "modrinth" or "curseforge"
                ? await _externalLaunchers.SelectExistingProfileAsync(id, root ?? "", _manifest, ct).ConfigureAwait(false)
                : id == "official"
                ? await _externalLaunchers.CreateOrSelectOfficialAsync(root, _manifest, ct).ConfigureAwait(false)
                : await _externalLaunchers.CreateOrSelectMmcAsync(id, root, _manifest, ct).ConfigureAwait(false);
            _config.Current.PlayMode = "external";
            _config.Current.ExternalLauncherId = id;
            _config.Current.ExternalInstancePath = installation.InstancePath;
            await _config.SaveAsync(ct).ConfigureAwait(false);
            Mutate(s => s with { PlayMode = "external", ExternalLauncherId = id, InstancePath = CurrentGameDirectory(),
                Screen = _config.Current.FirstLaunch ? BridgeProtocol.Screens.OnboardingCountry : BridgeProtocol.Screens.Main });
        }
        finally { _installGate.Release(); }
    }

    public event Action<LauncherState>? StateChanged;

    /// <summary>A short, already-translated line for the UI to toast.</summary>
    public event Action<string>? NoticeRaised;
    public event Action<ProgressSnapshot>? ProgressChanged;

    /// <summary>Version reported to the API and shown in settings.</summary>
    public static string LauncherVersion =>
        typeof(LauncherCore).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _installGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopBackgroundWorkAsync().ConfigureAwait(false);
            _preparedVersionId = null;
            Mutate(s => s with { ReadyToPlay = false, FatalErrorKey = null, FatalErrorDetail = null,
                Screen = BridgeProtocol.Screens.Loading });
            await RunStartupAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down; nothing to report.
        }
        catch (LauncherUpdateException ex)
        {
            Console.Error.WriteLine($"launcher update failed: {ex.ErrorKey}");
            Fail(ex.ErrorKey, null);
        }
        catch (LauncherException ex)
        {
            Fail(ex.ErrorKey, ex.Detail);
        }
        catch (Exception ex)
        {
            var error = LauncherErrors.Describe(ex);
            Fail(error.ErrorKey, error.Detail);
        }
        finally { _installGate.Release(); }
    }

    public Task RetryAsync(CancellationToken ct = default)
    {
        Mutate(s => s with { FatalErrorKey = null, FatalErrorDetail = null });
        return StartAsync(ct);
    }

    private async Task RunStartupAsync(CancellationToken ct)
    {
        Mutate(s => s with
        {
            LauncherVersion = LauncherVersion,
            MicrosoftSignInConfigured = Authentication.AuthConstants.HasBrowserClient,
            OfflineFixtures = _api.IsOffline,
            InstancePath = _paths.Instance,
        });

        // 1. Config first: it decides the language everything else is reported in.
        Stage(LauncherStage.Starting);
        _paths.EnsureCreated();
        var config = await _config.LoadAsync(ct).ConfigureAwait(false);
        UseEdition(config.Edition);
        // Use English until the player explicitly selects a language, regardless of the OS locale.
        await _languages.SetLanguageAsync(config.Language ?? LanguageCatalog.DefaultTag, ct)
            .ConfigureAwait(false);

        Mutate(s => s with
        {
            FirstLaunch = config.FirstLaunch,
            JvmPreset = config.Game.JvmPreset,
            Language = _languages.CurrentTag,
            RightToLeft = _languages.IsRightToLeft,
            Country = config.Country,
            PlayMode = config.PlayMode,
            ExternalLauncherId = config.ExternalLauncherId,
            InstancePath = CurrentGameDirectory(),
            InstalledVersion = config.InstalledModpackVersion,
            RegionId = config.PreferredRegionId,
        });

        // 1b. Bring the saved account back before the home screen paints. Without
        //     this the player chip is blank until the first Play, because the
        //     session used to be rebuilt only inside PlayAsync.
        RestoreProfileAtStartup(config);

        // Self-update finishes before any onboarding, account UI, or modpack installation.
        if (_launcherUpdates is not null)
        {
            _launcherUpdateBlocked = true;
            Mutate(s => s with { LauncherUpdatePending = true });
            if (await _launcherUpdates.CheckAsync(new InlineProgress(OnUpdateProgress), ct).ConfigureAwait(false)) return;
            _launcherUpdateBlocked = false;
            Mutate(s => s with { LauncherUpdatePending = false });
        }

        if (!config.FirstLaunch) EnsureShortcuts();

        // 2. Connectivity, kept separate from "the API is down" so the message is honest.
        Stage(LauncherStage.CheckingConnection);
        var online = await _connectivity.IsOnlineAsync(ct).ConfigureAwait(false);

        // 3. The API. An offline start is survivable when we have a cached manifest:
        //    the player can still verify and play what is already installed.
        Stage(LauncherStage.ContactingApi);
        ServiceStatus? status = null;
        LauncherVersionInfo? launcherInfo = null;
        ModpackLatestInfo? latest = null;

        if (online)
        {
            status = await TryApi(() => _api.GetStatusAsync(ct)).ConfigureAwait(false);
            if (status is not null)
                Mutate(s => s with { Status = status });

            Stage(LauncherStage.CheckingLauncherVersion);
            launcherInfo = await TryApi(() => _api.GetLauncherVersionAsync(ct)).ConfigureAwait(false);
            // Adopt the sign-in application id the API serves, so registering a
            // new Azure app does not require shipping a new launcher build.
            Authentication.AuthConstants.UseApiClientId(launcherInfo?.MicrosoftClientId);
            Mutate(st => st with { MicrosoftSignInConfigured = Authentication.AuthConstants.HasBrowserClient });
            if (_launcherUpdates is null && launcherInfo is not null && IsNewer(launcherInfo.Version, LauncherVersion))
                Mutate(s => s with { LauncherUpdate = launcherInfo });

            Stage(LauncherStage.CheckingModpackVersion);
            // As edicoes decidem o que baixar, entao sao lidas antes de tudo.
            var editions = await TryApi(() => _api.GetEditionsAsync(ct)).ConfigureAwait(false);
            if (editions is not null) _editions = editions;
            latest = await TryApi(() => _api.GetModpackLatestAsync(_config.Current.Edition, ct)).ConfigureAwait(false);
            if (latest is not null)
                Mutate(s => s with { LatestVersion = latest.Version });
        }

        // 4. Region selection before any download, so files come from a near mirror.
        if (online)
        {
            Stage(LauncherStage.ChoosingRegion);
            await ChooseRegionAsync(config.Country, ct).ConfigureAwait(false);
        }

        // 5. Without a chosen edition there is nothing to install yet.
        //
        // The launcher used to fetch a manifest and start downloading the moment
        // it opened, so onboarding ran over a download the player had not asked
        // for. With two packs that is worse than wasteful: it would be the wrong
        // one half the time. Nothing is fetched until the edition step answers.
        if (_editions.Count > 0 && string.IsNullOrWhiteSpace(_config.Current.Edition))
        {
            Mutate(st => st with
            {
                Screen = config.FirstLaunch
                    ? !string.IsNullOrWhiteSpace(config.Language) && (config.PerformanceProfileRevision < JvmPresets.ProfileRevision || !JvmPresets.IsKnown(config.Game.JvmPreset))
                        ? BridgeProtocol.Screens.OnboardingDevice
                        : BridgeProtocol.Screens.OnboardingLanguage
                    : (config.PerformanceProfileRevision < JvmPresets.ProfileRevision || !JvmPresets.IsKnown(config.Game.JvmPreset))
                        ? BridgeProtocol.Screens.OnboardingDevice
                        : BridgeProtocol.Screens.OnboardingEdition,
                ReadyToPlay = false,
            });
            return;
        }

        // The rest of this method installs one edition, so the per-edition
        // folders have to be in place before anything reads or writes them.
        UseEdition(_config.Current.Edition);

        // 5b. The manifest: remote when we can reach it, cached otherwise.
        _manifest = null;
        ApiException? manifestError = null;
        if (online)
        {
            try { _manifest = await _api.GetModpackManifestAsync(latest?.Version, ct).ConfigureAwait(false); }
            catch (ApiException ex)
            {
                manifestError = ex;
                Console.Error.WriteLine($"api call failed: {ex.Message}");
            }
        }

        if (_manifest is not null)
        {
            _manifests.Validate(_manifest);
            await _manifests.SaveCacheAsync(_manifest, ct).ConfigureAwait(false);
        }
        else
        {
            // A cached manifest is only a stand-in for a server we could not
            // reach. When the server answered and simply has nothing published,
            // its files are gone too, and installing from the cache just fails
            // later with a confusing 404 on some mod. Say what is actually wrong.
            if (online && manifestError?.ErrorKey is ApiErrorKeys.ManifestMissing or ApiErrorKeys.ModpackLatestMissing)
            {
                throw new LauncherException(
                    ApiErrorKeys.ManifestMissing,
                    manifestError.Detail ?? "The API has no published modpack version.");
            }

            _manifest = await _manifests.LoadCachedAsync(ct).ConfigureAwait(false);
            if (_manifest is null)
            {
                throw new LauncherException(
                    manifestError?.ErrorKey ?? (online ? "error.api.unreachable" : "error.network.offline"),
                    manifestError?.Detail ?? $"No manifest available (api={_api.BaseUrl}, online={online}).");
            }
        }

        Mutate(s => s with { LatestVersion = s.LatestVersion ?? _manifest!.Version, MinecraftVersion = _manifest!.Minecraft });

        // 6. Optional mods, merged with what the player chose last time.
        if (online)
        {
            var remote = await TryApi(() => _api.GetOptionalModsAsync(_manifest!.Version, ct))
                .ConfigureAwait(false);
            if (remote is not null)
                _remoteOptionalMods = remote;
        }
        RefreshOptionalModState();

        // The icon and blurb the API stores are hand-entered and usually empty,
        // which is what made every row look the same. Modrinth can fill them in
        // from the SHA-1 the manifest already carries — in the background, so a
        // slow or unreachable Modrinth never delays onboarding.
        if (online) _ = EnrichOptionalModsAsync(_manifest!);

        // 7. Verify what is on disk against the manifest.
        Stage(LauncherStage.VerifyingInstallation);
        var choices = _optionalMods.CurrentChoices(_remoteOptionalMods);
        var plan = await _modpack
            .BuildPlanAsync(_manifest!, choices, force: false, ReportProgress(), ct)
            .ConfigureAwait(false);

        // 8. Start downloading immediately, in the background, so onboarding runs
        //    over a live download instead of before one.
        Stage(LauncherStage.PreparingModpack);
        Mutate(s => s with
        {
            // A first launch that has already answered the edition step is past
            // the language step, and sending it back there would make the setup
            // start over every time a pack is chosen.
            Screen = (config.PerformanceProfileRevision < JvmPresets.ProfileRevision || !JvmPresets.IsKnown(config.Game.JvmPreset)) ? BridgeProtocol.Screens.OnboardingDevice
                : config.PlayMode == "external" && string.IsNullOrWhiteSpace(config.ExternalLauncherId) ? BridgeProtocol.Screens.OnboardingExternalLauncher
                : !config.FirstLaunch && HasUnseenOptionalMods() ? BridgeProtocol.Screens.OnboardingOptionalMods
                : !config.FirstLaunch ? BridgeProtocol.Screens.Main
                : string.IsNullOrWhiteSpace(config.Edition) ? BridgeProtocol.Screens.OnboardingLanguage
                : _remoteOptionalMods.Count > 0 ? BridgeProtocol.Screens.OnboardingOptionalMods
                : BridgeProtocol.Screens.Main,
            Edition = config.Edition,
            ReadyToPlay = false,
        });
        StartBackgroundWork(plan, ct);

        if (online)
        {
            var news = await TryApi(() => _api.GetNewsAsync(_languages.CurrentTag, ct)).ConfigureAwait(false);
            if (news is not null)
            {
                Mutate(s => s with { News = news });
                // Fill in any language the team did not write, in the background:
                // news is the least important thing on the screen and must never
                // hold up the install.
                _ = TranslateNewsAsync(news, _languages.CurrentTag);
            }

            _ = RefreshServerStatusAsync(ct);
        }

    }

    /// <summary>
    /// Fills in optional-mod icons and descriptions the API left empty.
    ///
    /// Strictly additive: a value the DUSMP team actually published always wins,
    /// so the panel stays the source of truth and this only covers what nobody
    /// filled in. Fire-and-forget — a failure leaves the placeholder in place.
    /// </summary>
    private async Task EnrichOptionalModsAsync(ModpackManifest manifest)
    {
        if (_modMetadata is null || _remoteOptionalMods.Count == 0) return;

        try
        {
            // One jar per optional mod is enough to identify it; the first file
            // with a SHA-1 is the mod's own jar in every pack we publish.
            var sha1ByMod = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in manifest.Files)
            {
                if (file.OptionalId is not { Length: > 0 } id || file.Sha1 is not { Length: > 0 } sha1) continue;
                if (!file.Path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;
                sha1ByMod.TryAdd(id, sha1);
            }
            if (sha1ByMod.Count == 0) return;

            var found = await _modMetadata.ResolveAsync(sha1ByMod).ConfigureAwait(false);
            if (found.Count == 0) return;

            var enriched = new List<OptionalMod>(_remoteOptionalMods.Count);
            var changed = false;
            foreach (var mod in _remoteOptionalMods)
            {
                if (!found.TryGetValue(mod.Id, out var project)) { enriched.Add(mod); continue; }

                var icon = string.IsNullOrWhiteSpace(mod.Icon) ? project.IconUrl : mod.Icon;
                var description = mod.Description;
                if (project.Description is { Length: > 0 } blurb && !HasAnyText(description))
                {
                    // Modrinth writes one English summary; the UI falls back to
                    // "en" for any language, so it lands under that key.
                    description = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = blurb };
                }

                if (ReferenceEquals(icon, mod.Icon) && ReferenceEquals(description, mod.Description))
                {
                    enriched.Add(mod);
                    continue;
                }

                changed = true;
                enriched.Add(mod with { Icon = icon, Description = description });
            }

            if (!changed) return;
            _remoteOptionalMods = enriched;
            RefreshOptionalModState();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"could not enrich optional mods: {ex.GetType().Name}");
        }
    }

    private static bool HasAnyText(IReadOnlyDictionary<string, string> map)
    {
        foreach (var value in map.Values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return true;
        }
        return false;
    }

    // Called only under _installGate. Always join the old worker before reading or
    // writing its files, including when the player changes optional mods mid-download.
    private async Task StopBackgroundWorkAsync()
    {
        _backgroundCts?.Cancel();
        if (_backgroundDownload is { } old) await old.ConfigureAwait(false);
        _backgroundCts?.Dispose();
        _backgroundCts = null;
        _backgroundDownload = null;
    }

    private void StartBackgroundWork(InstallPlan plan, CancellationToken ct)
    {
        _backgroundCts?.Dispose();
        _backgroundCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _backgroundCts.Token;
        Mutate(s => s with { ReadyToPlay = false });

        _backgroundDownload = Task.Run(async () =>
        {
            try
            {
                await _modpack.ApplyDeletionsAsync(plan, token).ConfigureAwait(false);
                var jobs = await BuildDownloadJobsAsync(plan, token).ConfigureAwait(false);
                var result = new DownloadResult(Array.Empty<DownloadJob>(), Array.Empty<DownloadFailure>(), false);
                if (jobs.Count > 0)
                {
                    Stage(LauncherStage.DownloadingFiles);
                    result = await _downloads.RunAsync(jobs, token).ConfigureAwait(false);
                }
                if (!result.Success)
                {
                    if (!result.Cancelled)
                    {
                        var first = result.Failed[0];
                        Fail(first.ErrorKey, $"{first.Job.RelativePath}: {first.Detail}");
                    }
                    return result;
                }

                token.ThrowIfCancellationRequested();
                if (_config.Current.PlayMode != "external")
                {
                    Stage(LauncherStage.InstallingMinecraft);
                    _preparedVersionId = await _game.EnsureLoaderInstalledAsync(plan.Manifest, ReportProgress(), token)
                        .ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                Stage(LauncherStage.Finishing);
                await _modpack.CommitAsync(plan, token).ConfigureAwait(false);
                foreach (var changed in plan.ToDownload.Where(file => PerformanceProfile.IsProfileFile(file.File.Path)))
                    _config.Current.PerformanceProfileApplied?.Remove(Path.GetFullPath(_editionPaths.Instance) + "|" + changed.File.Path.Replace('\\', '/'));
                _config.Current.InstalledModpackVersion = plan.Manifest.Version;
                await _config.SaveAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                Mutate(s => s with { InstalledVersion = plan.Manifest.Version, ReadyToPlay = true });
                Stage(LauncherStage.Ready);
                return result;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return new DownloadResult(Array.Empty<DownloadJob>(), Array.Empty<DownloadFailure>(), true);
            }
            catch (Exception ex)
            {
                var error = LauncherErrors.Describe(ex);
                Fail(error.ErrorKey, error.Detail);
                return new DownloadResult(Array.Empty<DownloadJob>(),
                    new[] { new DownloadFailure(new DownloadJob("installation", _paths.Instance, 0, "", Array.Empty<string>()),
                        error.ErrorKey, error.Detail ?? error.ErrorKey) }, false);
            }
        });
    }

    /// <summary>
    /// Turns the plan into concrete download jobs, after giving the public mod
    /// CDNs a chance to claim the files they already host.
    /// </summary>
    /// <remarks>
    /// Runs on the background worker, never on the startup path, because the
    /// lookup is a network call and the player is meanwhile going through
    /// onboarding. Its result only reorders candidate URLs — the manifest URL
    /// stays in the list and SHA-256 is still verified per file — so a resolver
    /// that fails, times out or answers nothing costs exactly the behaviour we
    /// had before it existed, and never an install.
    /// </remarks>
    private async Task<List<DownloadJob>> BuildDownloadJobsAsync(InstallPlan plan, CancellationToken ct)
    {
        if (_externalMirrors is not null && plan.ToDownload.Count > 0)
        {
            try
            {
                var files = plan.ToDownload.Select(p => p.File).ToList();
                var resolved = await _externalMirrors.ResolveAsync(files, ct).ConfigureAwait(false);

                // Set unconditionally, including when nothing resolved: a new plan
                // must not inherit the previous one's mirrors for files it no
                // longer contains.
                _mirrors.SetExternalMirrors(resolved);
            }
            catch (Exception)
            {
                // Best effort by contract. Whatever went wrong, the manifest URLs
                // below are still a complete way to install the pack.
            }
        }

        return plan.ToDownload
            .OrderBy(p => p.File.IsOptional)
            .Select(p => new DownloadJob(p.File.Path, p.AbsolutePath, p.File.Size,
                p.File.Sha256, _mirrors.ResolveUrls(p.File)))
            .ToList();
    }

    private async Task ChooseRegionAsync(string? country, CancellationToken ct)
    {
        var regions = await TryApi(() => _api.GetRegionsAsync(ct)).ConfigureAwait(false);
        if (regions is null || regions.Count == 0)
            return;

        var measured = await _regions.EvaluateAsync(regions, country, ct).ConfigureAwait(false);
        var best = measured.FirstOrDefault(m => m.Region.Available);
        if (best is null)
            return;

        // Let the API override us: it knows about capacity and cost, we only know latency.
        var hint = await _api.ReportRegionAsync(new RegionHintRequest
        {
            Country = country ?? "",
            Language = _languages.CurrentTag,
            Latencies = measured
                .Where(m => m.LatencyMs is not null)
                .ToDictionary(m => m.Region.Id, m => m.LatencyMs!.Value),
        }, ct).ConfigureAwait(false);

        var chosenId = hint?.RegionId ?? best.Region.Id;
        var bases = hint?.CdnBases.Count > 0
            ? hint.CdnBases
            : measured.FirstOrDefault(m => m.Region.Id == chosenId)?.Region.CdnBases ?? best.Region.CdnBases;

        _mirrors.SetCdnBases(bases);
        _config.Current.PreferredRegionId = chosenId;
        await _config.SaveAsync(ct).ConfigureAwait(false);

        Mutate(s => s with { RegionId = chosenId });
    }

    public async Task SetLanguageAsync(string tag, CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _languages.SetLanguageAsync(tag, ct).ConfigureAwait(false);
            _config.Current.Language = _languages.CurrentTag;
            await _config.SaveAsync(ct).ConfigureAwait(false);

            Mutate(s => s with
            {
                Language = _languages.CurrentTag,
                RightToLeft = _languages.IsRightToLeft,
                // Onboarding advances on selection; later changes stay put.
                // Device choice establishes the heap and collector before play
                // mode or any download. Play mode comes before the account: a player who is
                // going to sign in inside Prism or the Minecraft Launcher must
                // not be stopped by a Microsoft sign-in here first.
                Screen = s.Screen == BridgeProtocol.Screens.OnboardingLanguage
                    ? BridgeProtocol.Screens.OnboardingDevice
                    : s.Screen,
            });
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task SetJvmPresetAsync(string preset, CancellationToken ct = default)
    {
        if (!JvmPresets.IsKnown(preset))
            throw new LauncherException("error.device.unknown", preset);

        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (preset is not (JvmPresets.Low8Gb or JvmPresets.Standard or JvmPresets.Smooth))
                throw new LauncherException("error.device.unknown", preset);
            JvmPresets.Apply(_config.Current.Game, preset);
            _config.Current.PerformanceProfileRevision = JvmPresets.ProfileRevision;
            _config.Current.PerformanceProfileGeneration++;
            await _config.SaveAsync(ct).ConfigureAwait(false);
            Mutate(s => s with
            {
                JvmPreset = _config.Current.Game.JvmPreset,
                Screen = s.Screen == BridgeProtocol.Screens.OnboardingDevice
                    ? _config.Current.FirstLaunch
                        ? BridgeProtocol.Screens.OnboardingPlayMode
                        : _editions.Count > 0 && string.IsNullOrWhiteSpace(_config.Current.Edition)
                            ? BridgeProtocol.Screens.OnboardingEdition
                            : HasUnseenOptionalMods() ? BridgeProtocol.Screens.OnboardingOptionalMods
                            : BridgeProtocol.Screens.Main
                    : s.Screen,
            });
        }
        finally { _mutex.Release(); }
    }

    public async Task SetCountryAsync(string code, CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _config.Current.Country = code;
            await _config.SaveAsync(ct).ConfigureAwait(false);

            // Which pack to install comes next, and until it is answered there
            // is nothing downloaded and so nothing to list as optional.
            var needsEdition = _editions.Count > 0 && string.IsNullOrWhiteSpace(_config.Current.Edition);

            // Showing an empty "Optional mods" screen is a dead step, so when the
            // pack has none the setup ends here instead.
            var hasOptionalMods = _remoteOptionalMods.Count > 0;
            if (!needsEdition && !hasOptionalMods && _config.Current.FirstLaunch
                && JvmPresets.IsKnown(_config.Current.Game.JvmPreset))
            {
                _config.Current.FirstLaunch = false;
                await _config.SaveAsync(ct).ConfigureAwait(false);
            }

            Mutate(s => s with
            {
                Country = code,
                FirstLaunch = _config.Current.FirstLaunch,
                Screen = s.Screen == BridgeProtocol.Screens.OnboardingCountry
                    ? (!JvmPresets.IsKnown(_config.Current.Game.JvmPreset) && _config.Current.FirstLaunch
                        ? BridgeProtocol.Screens.OnboardingDevice
                        : needsEdition
                        ? BridgeProtocol.Screens.OnboardingEdition
                        : hasOptionalMods
                            ? BridgeProtocol.Screens.OnboardingOptionalMods
                            : BridgeProtocol.Screens.Main)
                    : s.Screen,
            });
        }
        finally
        {
            _mutex.Release();
        }

        // Re-score regions with the new country, but never stall the UI on it.
        _ = Task.Run(() => TryApi(async () => { await ChooseRegionAsync(code, CancellationToken.None).ConfigureAwait(false); return new object(); }));
    }

    public async Task<PlayerProfile> SignInMicrosoftAsync(
        Action<DeviceCodePrompt> onPrompt,
        CancellationToken ct = default,
        bool useDeviceCode = false)
    {
        var session = await _microsoft.SignInAsync(onPrompt, ct, useDeviceCode).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await AdoptSessionAsync(session, AccountType.Microsoft, ct).ConfigureAwait(false);
        return session.Profile;
    }

    public async Task<PlayerProfile> SignInOfflineAsync(string username, CancellationToken ct = default)
    {
        if (_offline.ValidateUsername(username) is { } errorKey)
            throw new LauncherException(errorKey, $"Rejected offline username of length {username.Length}.");

        var session = _offline.CreateSession(username);
        _config.Current.OfflineUsername = session.Username;
        await AdoptSessionAsync(session, AccountType.Offline, ct).ConfigureAwait(false);
        return session.Profile;
    }

    private async Task AdoptSessionAsync(GameSession session, AccountType type, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _session = session;
        _config.Current.AccountType = type;
        if (type == AccountType.Microsoft) _config.Current.MicrosoftAccountId = session.Uuid;
        await _config.SaveAsync(ct).ConfigureAwait(false);

        // The drawer lists whatever has signed in, so every session is recorded.
        await _accounts.UpsertAsync(session.Profile, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        Mutate(s => s with
        {
            Profile = session.Profile,
            Screen = s.Screen == BridgeProtocol.Screens.OnboardingAccount
                ? BridgeProtocol.Screens.OnboardingCountry
                : s.Screen,
        });
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        await _microsoft.SignOutAsync(ct).ConfigureAwait(false);
        _session = null;
        _config.Current.AccountType = AccountType.None;
        _config.Current.MicrosoftAccountId = null;
        await _config.SaveAsync(ct).ConfigureAwait(false);

        Mutate(s => s with
        {
            Profile = PlayerProfile.Empty,
            Screen = BridgeProtocol.Screens.OnboardingAccount,
        });
    }

    public async Task ToggleOptionalModAsync(string modId, bool enabled, CancellationToken ct = default)
    {
        await _installGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state.GameState is GameRunState.Preparing or GameRunState.Launching or GameRunState.Running)
                throw new LauncherException("error.game.running");
            await StopBackgroundWorkAsync().ConfigureAwait(false);
            await _optionalMods.SetEnabledAsync(modId, enabled, ct).ConfigureAwait(false);
            RefreshOptionalModState();
            if (_manifest is not null) await PlanBackgroundWorkAsync(false, ct).ConfigureAwait(false);
        }
        finally { _installGate.Release(); }
    }

    public async Task FinishOnboardingAsync(CancellationToken ct = default)
    {
        RequireLauncherUpdateReady();
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireDevicePresetForSetup();
            await _optionalMods.MarkAllSeenAsync(_remoteOptionalMods, ct).ConfigureAwait(false);
            _config.Current.FirstLaunch = false;
            await _config.SaveAsync(ct).ConfigureAwait(false);
            EnsureShortcuts();

            RefreshOptionalModState();
            Mutate(s => s with { FirstLaunch = false, Screen = s.FatalErrorKey is null ? BridgeProtocol.Screens.Main : s.Screen });
        }
        finally
        {
            _mutex.Release();
        }
    }

    private void EnsureShortcuts()
    {
        try { _ensureShortcuts?.Invoke(); }
        catch (Exception ex) { Console.Error.WriteLine($"Could not create launcher shortcuts: {ex.Message}"); }
    }

    public async Task PlayAsync(CancellationToken ct = default)
    {
        RequireLauncherUpdateReady();
        RequireDevicePresetForSetup();
        if (_state.Status?.BlockPlay == true)
            throw new LauncherException("error.api.maintenance", "The API reports maintenance.");

        if (_state.LauncherUpdate?.Mandatory == true)
            throw new LauncherException("error.launcher.updateRequired", _state.LauncherUpdate.Version);

        if (_manifest is null)
            throw new LauncherException("error.manifest.invalid", "No manifest loaded.");

        Task<bool>? exitRefresh;
        lock (_exitRefreshLock) exitRefresh = _exitRefresh is { IsCompleted: false } ? _exitRefresh : null;
        var refreshedOnExit = exitRefresh is not null && await exitRefresh.WaitAsync(ct).ConfigureAwait(false);
        await _installGate.WaitAsync(ct).ConfigureAwait(false);
        if (_state.GameState is GameRunState.Preparing or GameRunState.Launching or GameRunState.Running)
        {
            _installGate.Release();
            return;
        }
        Interlocked.Exchange(ref _stopRequested, 0);
        Mutate(s => s with { GameState = GameRunState.Preparing });
        using var launchCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_launchCancellationLock) _launchCancellation = launchCancellation;

        try
        {
            // Wait for whatever is already downloading rather than racing it.
            if (_backgroundDownload is { } running)
            {
                var finished = await running.WaitAsync(ct).ConfigureAwait(false);
                if (!finished.Success)
                    throw new LauncherException(_state.FatalErrorKey ?? "error.download.failed", _state.FatalErrorDetail);
            }

            if (!refreshedOnExit)
                await RefreshManifestAsync(ct).ConfigureAwait(false);

            if (!_config.Current.FirstLaunch && HasUnseenOptionalMods())
            {
                Mutate(s => s with { GameState = GameRunState.Idle, Screen = BridgeProtocol.Screens.OnboardingOptionalMods });
                return;
            }

            // Re-verify: cheap thanks to the ledger, and the only way to catch a
            // file that changed since startup.
            var choices = _optionalMods.CurrentChoices(_remoteOptionalMods);
            var plan = await _modpack
                .BuildPlanAsync(_manifest, choices, force: false, ReportProgress(), ct)
                .ConfigureAwait(false);

            if (!plan.IsComplete || _config.Current.PlayMode != "external" && _preparedVersionId is null)
            {
                StartBackgroundWork(plan, ct);
                var result = await _backgroundDownload!.ConfigureAwait(false);
                if (!result.Success)
                {
                    throw new LauncherException(
                        result.Cancelled ? "error.download.failed" : "error.download.failed",
                        result.Failed.Count > 0 ? result.Failed[0].Detail : "cancelled");
                }
            }

            Mutate(s => s with { GameState = GameRunState.Launching });

            if (_config.Current.PlayMode == "external")
            {
                var hostId = _config.Current.ExternalLauncherId;
                if (hostId is null || (hostId is not ("official" or "modrinth" or "curseforge") && !ExternalLauncherDiscovery.MmcFamily.Contains(hostId))
                    || string.IsNullOrWhiteSpace(_config.Current.ExternalInstancePath))
                    throw new LauncherException("error.external.notConfigured", "Select a supported external launcher first.");
                var installations = await _externalLaunchers.GetInstallationsAsync(ct).ConfigureAwait(false);
                var instance = installations.FirstOrDefault(x => x.Managed && x.Launcher == hostId && Path.GetFullPath(x.InstancePath).Equals(Path.GetFullPath(_config.Current.ExternalInstancePath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                if (instance is null) throw new LauncherException("error.external.registrationMissing", _config.Current.ExternalInstancePath);
                var updatedProfiles = await _externalLaunchers.SyncManagedFilesAsync(instance, _manifest, choices, ct).ConfigureAwait(false);
                foreach (var relative in updatedProfiles)
                    _config.Current.PerformanceProfileApplied?.Remove(Path.GetFullPath(CurrentGameDirectory()) + "|" + relative);
                await InstallUpdateNotifierAsync(_manifest, ct).ConfigureAwait(false);
                await ApplyPerformanceProfileAsync(ct).ConfigureAwait(false);

                if (hostId == "official")
                {
                    // The MultiMC family resolves the loader itself from
                    // mmc-pack.json; the
                    // official launcher does not, so the version, its libraries
                    // and its assets are installed into its own .minecraft first
                    // and the profile is pointed at the result.
                    var host = instance.LauncherRoot
                        ?? Path.GetDirectoryName(Path.GetFullPath(instance.InstancePath))
                        ?? throw new LauncherException("error.external.registrationMissing", instance.InstancePath);
                    Stage(LauncherStage.InstallingMinecraft);
                    var hostVersion = await _game.EnsureLoaderInstalledInAsync(host, _manifest, ReportProgress(), ct).ConfigureAwait(false);
                    await _externalLaunchers.WriteOfficialProfileAsync(instance, hostVersion, ct).ConfigureAwait(false);
                    await _externalLaunchers.LaunchOfficialAsync(instance, ct).ConfigureAwait(false);
                }
                else if (hostId is "modrinth" or "curseforge")
                {
                    await _externalLaunchers.OpenExistingLauncherAsync(instance, ct).ConfigureAwait(false);
                }
                else
                {
                    await _externalLaunchers.LaunchMmcAsync(instance, ct).ConfigureAwait(false);
                }

                // The other launcher owns the game process and account. A successful
                // handoff must not expose a Stop button that cannot control that game.
                Mutate(s => s with { GameState = GameRunState.Idle });
                Stage(LauncherStage.Ready);
                if (hostId == "official") NoticeRaised?.Invoke(_languages.Translate("main.external.handoff"));
                return;
            }

            await ApplyPerformanceProfileAsync(ct).ConfigureAwait(false);
            var session = _session ?? await RestoreSessionAsync(ct).ConfigureAwait(false);
            var versionId = _preparedVersionId ?? await _game.EnsureLoaderInstalledAsync(_manifest, ReportProgress(), ct).ConfigureAwait(false);

            // A pack whose pinned loader is older than its own mods would open on
            // a red "Error loading mods" screen. Raise the loader to the lowest
            // build every mod accepts instead, and say so.
            versionId = await EnsureLoaderSatisfiesModsAsync(versionId, ct).ConfigureAwait(false);

            Mutate(s => s with { GameState = GameRunState.Running });
            await _game.LaunchAsync(versionId, session, _config.Current.Game, launchCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (Volatile.Read(ref _stopRequested) != 0 && !ct.IsCancellationRequested)
        {
            Mutate(s => s with { GameState = GameRunState.Idle });
        }
        catch
        {
            Mutate(s => s with { GameState = GameRunState.Idle });
            throw;
        }
        finally
        {
            lock (_launchCancellationLock) _launchCancellation = null;
            _installGate.Release();
        }
    }

    /// <summary>
    /// Rebuilds a session for a returning player. Microsoft accounts refresh
    /// silently; a failed refresh sends them back to the account screen rather
    /// than launching with a dead token.
    /// </summary>
    /// <summary>
    /// Shows who is signed in as soon as the launcher opens. Offline accounts are
    /// pure local data so they are rebuilt inline; a Microsoft account needs a
    /// token refresh, which is done in the background so a slow or failed refresh
    /// delays nothing — the home screen simply fills in when it lands.
    /// </summary>
    private void RestoreProfileAtStartup(Configuration.LauncherConfig config)
    {
        switch (config.AccountType)
        {
            case AccountType.Offline when config.OfflineUsername is { Length: > 0 } name:
            {
                try
                {
                    var session = _offline.CreateSession(name);
                    _session = session;
                    Mutate(s => s with { Profile = session.Profile });
                    // Someone who signed in before the drawer existed still has to
                    // see their account in it, so a restore records it too.
                    _ = _accounts.UpsertAsync(session.Profile, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    // A bad saved name must not stop the launcher from opening.
                    Console.Error.WriteLine($"offline profile restore failed: {ex.Message}");
                }
                break;
            }

            case AccountType.Microsoft:
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // The account the player last chose, not simply the last
                        // one to sign in: those differ as soon as they switch.
                        var session = await _microsoft
                            .SignInSilentlyAsync(config.MicrosoftAccountId, CancellationToken.None)
                            .ConfigureAwait(false);
                        if (session is not null)
                        {
                            _session = session;
                            Mutate(s => s with { Profile = session.Profile });
                            await _accounts.UpsertAsync(session.Profile, CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Play re-runs this and surfaces the failure properly there.
                        Console.Error.WriteLine($"silent sign-in at startup failed: {ex.Message}");
                    }
                });
                break;
        }
    }

    private async Task<GameSession> RestoreSessionAsync(CancellationToken ct)
    {
        switch (_config.Current.AccountType)
        {
            case AccountType.Microsoft:
            {
                var session = await _microsoft.SignInSilentlyAsync(_config.Current.MicrosoftAccountId, ct).ConfigureAwait(false);
                if (session is null)
                {
                    Mutate(s => s with { Screen = BridgeProtocol.Screens.OnboardingAccount });
                    throw new LauncherException("error.auth.expired", "Silent refresh was rejected.");
                }
                _session = session;
                Mutate(s => s with { Profile = session.Profile });
                return session;
            }

            case AccountType.Offline when _config.Current.OfflineUsername is { Length: > 0 } name:
            {
                var session = _offline.CreateSession(name);
                _session = session;
                Mutate(s => s with { Profile = session.Profile });
                return session;
            }

            default:
                Mutate(s => s with { Screen = BridgeProtocol.Screens.OnboardingAccount });
                throw new LauncherException("error.auth.failed", "No account configured.");
        }
    }


    public async Task StopGameAsync(CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _stopRequested, 1);
        lock (_launchCancellationLock) _launchCancellation?.Cancel();
        await _installGate.WaitAsync(ct).ConfigureAwait(false);
        try { await _game.StopAsync(ct).ConfigureAwait(false); }
        finally { _installGate.Release(); }
    }

    public async Task RepairAsync(CancellationToken ct = default)
    {
        await _installGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state.GameState is GameRunState.Preparing or GameRunState.Launching or GameRunState.Running)
                throw new LauncherException("error.game.running");
            if (_manifest is null) throw new LauncherException("error.manifest.invalid", "No manifest loaded.");
            await StopBackgroundWorkAsync().ConfigureAwait(false);
            await PlanBackgroundWorkAsync(true, ct).ConfigureAwait(false);
        }
        finally { _installGate.Release(); }
    }

    /// <summary>
    /// Removes everything the launcher installed and starts the modpack over.
    ///
    /// Repair only re-checks files against the current manifest, which cannot
    /// help when the installed loader and mods came from a manifest that no
    /// longer exists — the state the player is left in after a pack is
    /// republished or unpublished. This throws that state away instead.
    ///
    /// Deliberately keeps what belongs to the player: worlds, screenshots,
    /// resource packs, options and logs all live in
    /// <see cref="LauncherPaths.PlayerOwnedDirectories"/> and are never touched.
    /// </summary>
    public async Task ResetInstallationAsync(CancellationToken ct = default)
    {
        await _installGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state.GameState is GameRunState.Preparing or GameRunState.Launching or GameRunState.Running)
                throw new LauncherException("error.game.running");

            await StopBackgroundWorkAsync().ConfigureAwait(false);
            Mutate(s => s with { ReadyToPlay = false, InstalledVersion = null });
            Stage(LauncherStage.VerifyingInstallation);

            var removed = LauncherPaths.ResetInstance(_paths);
            Console.Error.WriteLine($"installation reset: {removed} entries removed under {_paths.Instance}");

            // The caches describe the install that no longer exists.
            foreach (var file in new[] { _paths.ManagedIndexFile, _paths.ManifestCacheFile })
            {
                try { if (File.Exists(file)) File.Delete(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }

            _preparedVersionId = null;

            // Reinstall straight away when there is something to install; when the
            // API has nothing published, leave the player on a clean slate rather
            // than failing here.
            if (_manifest is not null)
                await PlanBackgroundWorkAsync(true, ct).ConfigureAwait(false);
        }
        finally { _installGate.Release(); }
    }

    /// <summary>
    /// Raises the loader to the lowest build the pack's own mods all accept.
    ///
    /// The pinned version is normally authoritative, and everything else in this
    /// launcher goes out of its way to install exactly what the panel published.
    /// This is the one exception, because the alternative is worse: a pack pinned
    /// to a loader its mods reject cannot start at all, and the player is shown a
    /// list of mod ids they have no way to act on. The chosen build is the
    /// minimum that satisfies every mod, so it is the same on every machine.
    ///
    /// Returns the version id to launch, unchanged when nothing needed raising.
    /// </summary>
    private async Task<string> EnsureLoaderSatisfiesModsAsync(string versionId, CancellationToken ct)
    {
        if (_manifest?.LoaderVersion is not { Length: > 0 } pinned) return versionId;

        var tooNew = LoaderCompatibility.FindTooNew(Path.Combine(_paths.Instance, "mods"), pinned);
        if (tooNew.Count == 0) return versionId;

        // Sorted worst-first, so the first entry is the lowest build that works
        // for every one of them.
        var required = tooNew[0].MinimumVersion;
        Console.Error.WriteLine(
            $"loader raised: pack pins {_manifest.Loader} {pinned}, {tooNew.Count} mod(s) need up to {required}");

        string raised;
        try
        {
            raised = await _game
                .EnsureLoaderInstalledAsync(_manifest with { LoaderVersion = required }, ReportProgress(), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Could not get the newer build: launching on the pinned one at least
            // produces the game's own explanation rather than a silent failure.
            Console.Error.WriteLine($"could not raise the loader to {required}: {ex.Message}");
            return versionId;
        }

        Notify(_languages.Translate("main.loader.raised", new Dictionary<string, string>
        {
            ["pinned"] = pinned,
            ["required"] = required,
        }));
        return raised;
    }

    /// <summary>
    /// The signed-in player's skin, skin history and capes.
    ///
    /// Only a Microsoft account has any of this: the skin lives on Mojang's
    /// servers and is read with the Minecraft token. An offline account has no
    /// profile to read, so the screen shows what it knows and stays read-only.
    /// </summary>
    public async Task<AppearanceState?> GetAppearanceAsync(CancellationToken ct = default)
    {
        var token = await RequireMinecraftTokenAsync(ct).ConfigureAwait(false);
        return token is null ? null : await _appearance!.GetAsync(token, ct).ConfigureAwait(false);
    }

    /// <summary>Uploads a PNG as the active skin. The path comes from the host's file picker.</summary>
    public async Task<AppearanceState?> UploadSkinAsync(string filePath, string variant, CancellationToken ct = default)
    {
        var token = await RequireMinecraftTokenAsync(ct).ConfigureAwait(false);
        if (token is null) return null;

        var state = await _appearance!.UploadSkinAsync(token, filePath, variant, ct).ConfigureAwait(false);
        RefreshProfileSkin(state);
        return state;
    }

    /// <summary>Goes back to the default skin.</summary>
    public async Task<AppearanceState?> ResetSkinAsync(CancellationToken ct = default)
    {
        var token = await RequireMinecraftTokenAsync(ct).ConfigureAwait(false);
        if (token is null) return null;

        var state = await _appearance!.ResetSkinAsync(token, ct).ConfigureAwait(false);
        RefreshProfileSkin(state);
        return state;
    }

    /// <summary>Wears a cape, or hides it when <paramref name="capeId"/> is empty.</summary>
    public async Task<AppearanceState?> SetCapeAsync(string? capeId, CancellationToken ct = default)
    {
        var token = await RequireMinecraftTokenAsync(ct).ConfigureAwait(false);
        return token is null ? null : await _appearance!.SetCapeAsync(token, capeId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The Minecraft access token, or null when this account cannot have one.
    /// Restores the session first, so the screen works on a warm start where
    /// nobody has pressed Play yet.
    /// </summary>
    private async Task<string?> RequireMinecraftTokenAsync(CancellationToken ct)
    {
        if (_appearance is null) return null;
        if (_config.Current.AccountType != AccountType.Microsoft) return null;

        var session = _session ?? await RestoreSessionAsync(ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(session.AccessToken) ? null : session.AccessToken;
    }

    /// <summary>
    /// Points the player chip at the new skin. The render service caches by UUID,
    /// so the URL carries the active skin's id to break that cache — otherwise
    /// the player changes their skin and the launcher keeps showing the old one.
    /// </summary>
    private void RefreshProfileSkin(AppearanceState state)
    {
        var active = state.Skins.FirstOrDefault(x => x.Active);
        if (active is null) return;

        Mutate(s => s with
        {
            Profile = s.Profile with { SkinUrl = active.Url },
        });
    }

    /// <summary>
    /// Follows the player back through the setup.
    ///
    /// The host decides which step comes next, but only the page knows when the
    /// player has stepped back to an earlier one. A host still convinced they
    /// were further on would not move them again: having reached the country
    /// step with an offline account, stepping back and signing in with Microsoft
    /// left the setup on a screen it had already passed, with no way forward.
    ///
    /// Only the setup's own steps are accepted, and only while the setup is
    /// running — once it is done the page owns its own navigation.
    /// </summary>
    public void GoToScreen(string screen)
    {
        RequireLauncherUpdateReady();
        if (Array.IndexOf(BridgeProtocol.Screens.Onboarding, screen) < 0)
            throw new LauncherException("error.unknown", $"Refused to move the setup to '{screen}'.");

        Mutate(s => s.FirstLaunch ? s with { Screen = screen } : s);
    }

    /// <summary>Every signed-in account, and which one is active.</summary>
    public async Task<object> ListAccountsAsync(CancellationToken ct = default)
    {
        var accounts = await _accounts.ListAsync(ct).ConfigureAwait(false);
        var active = await _accounts.GetActiveAsync(ct).ConfigureAwait(false);
        return new
        {
            activeId = active?.Id,
            accounts = accounts.Select(x => new
            {
                id = x.Id,
                type = x.Type == AccountType.Microsoft ? "microsoft" : "offline",
                username = x.Username,
                uuid = x.Uuid,
                skinUrl = x.SkinUrl,
            }).ToArray(),
        };
    }

    /// <summary>
    /// Switches to another stored account.
    ///
    /// An offline account is just a name, so it resumes instantly. A Microsoft
    /// account is resumed from its own refresh token; when that token has
    /// expired the player is sent to the sign-in screen rather than left on an
    /// account that cannot actually play.
    /// </summary>
    public async Task SelectAccountAsync(string id, CancellationToken ct = default)
    {
        // Nothing is written until the account is genuinely in hand. Marking it
        // active first and resuming afterwards meant a token that had expired
        // left the launcher reporting the switch as done, on screen and on disk,
        // while also raising the failure — a switch that both worked and did not.
        var accounts = await _accounts.ListAsync(ct).ConfigureAwait(false);
        var account = accounts.FirstOrDefault(x => x.Id == id)
            ?? throw new LauncherException("error.account.missing", id);

        if (account.Type == AccountType.Offline)
        {
            var session = _offline.CreateSession(account.Username);
            _config.Current.OfflineUsername = account.Username;
            await AdoptSessionAsync(session, AccountType.Offline, ct).ConfigureAwait(false);
            return;
        }

        // Asking by id gives back that account or nothing, so a null here only
        // ever means the stored sign-in is no longer good.
        var resumed = await _microsoft.SignInSilentlyAsync(account.Id, ct).ConfigureAwait(false);
        if (resumed is null)
        {
            Mutate(s => s with { Screen = BridgeProtocol.Screens.OnboardingAccount });
            throw new LauncherException("error.account.expired", $"The stored sign-in for {account.Username} has expired.");
        }

        await AdoptSessionAsync(resumed, AccountType.Microsoft, ct).ConfigureAwait(false);
    }

    /// <summary>Forgets an account, along with its stored token.</summary>
    public async Task RemoveAccountAsync(string id, CancellationToken ct = default)
    {
        var accounts = await _accounts.ListAsync(ct).ConfigureAwait(false);
        var removed = accounts.FirstOrDefault(x => x.Id == id);
        if (removed?.Type == AccountType.Microsoft)
            await _microsoft.ForgetAsync(id, ct).ConfigureAwait(false);

        var next = await _accounts.RemoveAsync(id, ct).ConfigureAwait(false);

        // Removing the account in use leaves nothing to play with, so either the
        // next one takes over or the player goes back to signing in.
        if (_session is not null && string.Equals(Accounts.AccountStore.IdFor(_session.Profile), id, StringComparison.Ordinal))
        {
            _session = null;
            if (next is null)
            {
                _config.Current.AccountType = AccountType.None;
                _config.Current.MicrosoftAccountId = null;
                await _config.SaveAsync(ct).ConfigureAwait(false);
                Mutate(s => s with { Profile = PlayerProfile.Empty, Screen = BridgeProtocol.Screens.OnboardingAccount });
                return;
            }
            await SelectAccountAsync(next.Id, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Points the installation at one edition's folders.
    ///
    /// Each edition is a different Minecraft version with a different mod list,
    /// so it gets its own game folder, its own ledger and its own cached
    /// manifest. The three components that read those have to be rebuilt
    /// against them; nothing else in the launcher moves.
    /// </summary>
    private void UseEdition(string? edition)
    {
        var next = string.IsNullOrWhiteSpace(edition) ? _paths : _paths.ForEdition(edition!);
        if (string.Equals(next.Instance, _editionPaths.Instance, StringComparison.Ordinal)) return;

        _editionPaths = next;
        _remoteOptionalMods = Array.Empty<OptionalMod>();
        _externalLaunchers = new ExternalLauncherManager(next);
        if (_editionComponents is null) return;

        var built = _editionComponents(next);
        _modpack = built.Modpack;
        _manifests = built.Manifests;
        _game.GameExited -= OnGameExited;
        _game = built.Game;
        _game.GameExited += OnGameExited;
    }

    /// <summary>The packs the player can choose between, and the one in use.</summary>
    public async Task<object> GetEditionsAsync(CancellationToken ct = default)
    {
        if (_editions.Count == 0)
        {
            var fetched = await TryApi(() => _api.GetEditionsAsync(ct)).ConfigureAwait(false);
            if (fetched is not null) _editions = fetched;
        }

        return new
        {
            current = _config.Current.Edition,
            editions = _editions.Select(e => new
            {
                id = e.Id,
                name = e.Name,
                description = e.Description,
                version = e.Version,
                minecraft = e.Minecraft,
                loader = e.Loader,
                loaderVersion = e.LoaderVersion,
                files = e.Files,
                // An edition with nothing published cannot be installed, and the
                // page greys it out rather than letting the player pick a pack
                // that would fail on the first download.
                available = !string.IsNullOrWhiteSpace(e.Version),
            }).ToArray(),
        };
    }

    /// <summary>
    /// Switches to an edition and installs it.
    ///
    /// This is the only thing that starts a download: nothing is fetched before
    /// the player has said which pack they want.
    /// </summary>
    public async Task SelectEditionAsync(string id, CancellationToken ct = default)
    {
        if (_editions.Count == 0) await GetEditionsAsync(ct).ConfigureAwait(false);
        var edition = _editions.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new LauncherException("error.edition.unknown", id);
        if (string.IsNullOrWhiteSpace(edition.Version))
            throw new LauncherException("error.edition.unpublished", edition.Id);

        await _installGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state.GameState is GameRunState.Preparing or GameRunState.Launching or GameRunState.Running)
                throw new LauncherException("error.game.running");

            // The pack being downloaded belongs to the edition being left.
            await StopBackgroundWorkAsync().ConfigureAwait(false);
            _preparedVersionId = null;

            _config.Current.Edition = edition.Id;
            await _config.SaveAsync(ct).ConfigureAwait(false);
            UseEdition(edition.Id);
            Mutate(s => s with { Edition = edition.Id, ReadyToPlay = false, InstalledVersion = null });
        }
        finally { _installGate.Release(); }

        // Outside the gate: this reruns the whole startup for the new edition,
        // which takes the gate itself.
        await StartAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Sends a one-line message to the UI, if anyone is listening.</summary>
    private void Notify(string message)
    {
        try { NoticeRaised?.Invoke(message); }
        catch (Exception ex) { Console.Error.WriteLine($"notice handler failed: {ex.GetType().Name}"); }
    }

    private async Task PlanBackgroundWorkAsync(bool force, CancellationToken ct)
    {
        Mutate(s => s with { ReadyToPlay = false });
        Stage(LauncherStage.VerifyingFiles);
        var choices = _optionalMods.CurrentChoices(_remoteOptionalMods);
        var plan = await _modpack.BuildPlanAsync(_manifest!, choices, force, ReportProgress(), ct).ConfigureAwait(false);
        StartBackgroundWork(plan, ct);
    }

    public Task<IReadOnlyList<InstalledMod>> GetInstalledModsAsync(CancellationToken ct = default) =>
        new InstalledModManager(Path.Combine(CurrentGameDirectory(), "mods")).ListAsync(ct);

    public async Task<IReadOnlyList<InstalledMod>> SetInstalledModEnabledAsync(string fileName, bool enabled, CancellationToken ct = default)
    {
        await _installGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireModChangesAvailable();
            await StopBackgroundWorkAsync().ConfigureAwait(false);
            var mods = new InstalledModManager(Path.Combine(CurrentGameDirectory(), "mods"));
            await mods.SetEnabledAsync(fileName, enabled, ct).ConfigureAwait(false);
            return await mods.ListAsync(ct).ConfigureAwait(false);
        }
        finally { _installGate.Release(); }
    }

    public async Task<IReadOnlyList<InstalledMod>> ImportModsAsync(IReadOnlyList<string> files, CancellationToken ct = default)
    {
        await _installGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireModChangesAvailable();
            await StopBackgroundWorkAsync().ConfigureAwait(false);
            var mods = new InstalledModManager(Path.Combine(CurrentGameDirectory(), "mods"));
            await mods.ImportAsync(files, ct).ConfigureAwait(false);
            return await mods.ListAsync(ct).ConfigureAwait(false);
        }
        finally { _installGate.Release(); }
    }

    private void RequireModChangesAvailable()
    {
        if (State.GameState is GameRunState.Preparing or GameRunState.Launching or GameRunState.Running || !State.ReadyToPlay
            || _backgroundDownload is { IsCompleted: false })
            throw new LauncherException("error.mods.busy");
    }

    public async Task SaveSettingsAsync(GameSettings settings, CancellationToken ct = default)
    {
        if (settings.JvmPreset is not null && !JvmPresets.IsKnown(settings.JvmPreset))
            throw new LauncherException("error.device.unknown", settings.JvmPreset);
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _config.Current.Game = settings;
            await _config.SaveAsync(ct).ConfigureAwait(false);
            Mutate(s => s with { JvmPreset = settings.JvmPreset });
        }
        finally { _mutex.Release(); }
    }

    private async Task ApplyPerformanceProfileAsync(CancellationToken ct)
    {
        var config = _config.Current;
        if (config.Game.JvmPreset == JvmPresets.Smooth) return;
        var root = Path.GetFullPath(CurrentGameDirectory());
        config.PerformanceProfileApplied ??= new Dictionary<string, int>();
        bool changed = false;
        string Key(string relative) => root + "|" + relative;
        PerformanceProfile.Apply(root,
            relative => !config.PerformanceProfileApplied.TryGetValue(Key(relative), out var generation) || generation != config.PerformanceProfileGeneration,
            relative => { config.PerformanceProfileApplied[Key(relative)] = config.PerformanceProfileGeneration; changed = true; },
            createBackup: config.PlayMode != "external" || config.ExternalLauncherId is not ("modrinth" or "curseforge"));
        if (changed) await _config.SaveAsync(ct).ConfigureAwait(false);
    }

    private async Task InstallUpdateNotifierAsync(ModpackManifest manifest, CancellationToken ct)
    {
        if (!string.Equals(manifest.Loader, "neoforge", StringComparison.OrdinalIgnoreCase) ||
            manifest.Minecraft is not ("1.21.1" or "1.21.11") || string.IsNullOrWhiteSpace(_config.Current.Edition))
            return;
        var root = CurrentGameDirectory();
        var jar = Path.Combine(root, "mods", "dusmp-update-notifier.jar");
        if (File.Exists(jar + ".disabled")) return;
        Directory.CreateDirectory(Path.GetDirectoryName(jar)!);
        var resource = $"DusmpLauncher.Resources.UpdateNotifier.{manifest.Minecraft}.jar";
        await using var input = typeof(LauncherCore).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing update notifier for Minecraft {manifest.Minecraft}.");
        var temp = jar + ".tmp";
        try
        {
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            File.Move(temp, jar, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        var configPath = Path.Combine(root, "config", "dusmp-update-notifier.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        var json = System.Text.Json.JsonSerializer.Serialize(new { edition = _config.Current.Edition, version = manifest.Version });
        await File.WriteAllTextAsync(configPath, json, ct).ConfigureAwait(false);
    }

    private void RequireDevicePresetForSetup()
    {
        if (_config.Current.PerformanceProfileRevision >= JvmPresets.ProfileRevision && JvmPresets.IsKnown(_config.Current.Game.JvmPreset)) return;
        Mutate(s => s with { Screen = BridgeProtocol.Screens.OnboardingDevice });
        throw new LauncherException("error.device.required");
    }

    public string? ValidateOfflineUsername(string username) => _offline.ValidateUsername(username);

    private string CurrentGameDirectory() => _config.Current.PlayMode == "external" && _config.Current.ExternalInstancePath is { Length: > 0 } externalPath
        ? ExternalLauncherDiscovery.MmcFamily.Contains(_config.Current.ExternalLauncherId) ? Path.Combine(externalPath, ".minecraft") : externalPath
        : _editionPaths.Instance;

    public void OpenInstanceFolder()
    {
        Directory.CreateDirectory(_paths.Instances);
        OpenPath(_paths.Instances);
    }

    public void OpenExternalUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new LauncherException("error.unknown", $"Refused to open non-web URL '{url}'.");
        }

        OpenPath(uri.AbsoluteUri);
    }

    /// <summary>
    /// Hands a path or URL to the desktop. Windows needs the shell to resolve the
    /// default handler; Linux has xdg-open.
    /// </summary>
    private static void OpenPath(string target)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", target);
            }
            else
            {
                Process.Start("xdg-open", target);
            }
        }
        catch (Exception ex)
        {
            throw new LauncherException("error.unknown", $"Could not open '{target}': {ex.Message}");
        }
    }

    private async Task RefreshServerStatusAsync(CancellationToken ct)
    {
        var servers = await TryApi(() => _api.GetServersAsync(ct)).ConfigureAwait(false);
        if (servers is null || servers.Count == 0)
            return;

        var statuses = new List<GameServerStatus>(servers.Count);
        foreach (var server in servers)
        {
            var latency = await MinecraftPing.TryPingAsync(server.Host, server.Port, ct).ConfigureAwait(false);
            statuses.Add(new GameServerStatus(
                server.Id,
                server.Name,
                latency is not null,
                latency?.PlayersOnline,
                latency?.PlayersMax,
                latency?.LatencyMs));
        }

        Mutate(s => s with { Servers = statuses });
    }

    /// <summary>
    /// Replaces the shown news with a machine-translated copy when the player's
    /// language is missing. Silent on failure: the untranslated text is already
    /// on screen and is better than an error.
    /// </summary>
    private async Task TranslateNewsAsync(IReadOnlyList<NewsItem> news, string languageTag)
    {
        if (_newsTranslator is null) return;
        try
        {
            var translated = await _newsTranslator.TranslateAsync(news, languageTag).ConfigureAwait(false);
            // Only push when something actually changed, to avoid a pointless
            // state round trip on the common all-languages-present case.
            if (translated.Any(item => item.AutoTranslated))
                Mutate(s => s with { News = translated });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"news translation failed: {ex.Message}");
        }
    }

    private void RefreshOptionalModState()
    {
        var views = _optionalMods.Merge(_remoteOptionalMods);
        Mutate(s => s with { OptionalMods = views });
    }

    private bool HasUnseenOptionalMods() => _optionalMods.Merge(_remoteOptionalMods).Any(mod => mod.IsNew);

    private void OnDownloadStats(DownloadStats stats)
    {
        PublishProgress(new ProgressSnapshot
        {
            Stage = LauncherStage.DownloadingFiles,
            Percent = stats.BytesTotal > 0
                ? Math.Clamp(stats.BytesCompleted * 100.0 / stats.BytesTotal, 0, 100)
                : null,
            CurrentFile = stats.CurrentFile,
            DownloadedBytes = stats.BytesCompleted,
            TotalBytes = stats.BytesTotal,
            BytesPerSecond = stats.BytesPerSecond,
            FilesCompleted = stats.FilesCompleted,
            FilesTotal = stats.FilesTotal,
        });
    }

    private void OnGameExited(int exitCode)
    {
        var stopped = Interlocked.Exchange(ref _stopRequested, 0) != 0;
        var edition = _config.Current.Edition;
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_exitRefreshLock) _exitRefresh = ready.Task;
        Mutate(s => s with
        {
            GameState = exitCode == 0 || stopped ? GameRunState.Idle : GameRunState.Crashed,
        });
        _ = RefreshAfterExitAsync(edition, ready);
    }

    // The caller holds _installGate and has joined any previous download.
    private async Task<bool> RefreshManifestAsync(CancellationToken ct)
    {
        var latest = await TryApi(() => _api.GetModpackLatestAsync(_config.Current.Edition, ct)).ConfigureAwait(false);
        if (latest is null || string.IsNullOrWhiteSpace(latest.Version)) return false;
        Mutate(s => s with { LatestVersion = latest.Version });
        if (_manifest is not null && string.Equals(_manifest.Version, latest.Version, StringComparison.Ordinal))
        {
            var currentOptional = await TryApi(() => _api.GetOptionalModsAsync(_manifest.Version, ct)).ConfigureAwait(false);
            if (currentOptional is not null) { _remoteOptionalMods = currentOptional; RefreshOptionalModState(); PromptForNewOptionalMods(); }
            return currentOptional is not null && HasUnseenOptionalMods();
        }

        // Once the service announces a new version, do not launch a stale pack
        // when fetching or validating that version fails.
        var manifest = await _api.GetModpackManifestAsync(latest.Version, ct).ConfigureAwait(false);
        _manifests.Validate(manifest);
        if (!string.Equals(manifest.Version, latest.Version, StringComparison.Ordinal))
            throw new LauncherException("error.manifest.invalid", "Manifest version does not match the latest version.");
        var optional = await TryApi(() => _api.GetOptionalModsAsync(manifest.Version, ct)).ConfigureAwait(false);
        await _manifests.SaveCacheAsync(manifest, ct).ConfigureAwait(false);
        _manifest = manifest;
        _preparedVersionId = null;
        if (optional is not null) _remoteOptionalMods = optional;
        RefreshOptionalModState();
        PromptForNewOptionalMods();
        Mutate(s => s with { MinecraftVersion = manifest.Minecraft });
        return true;
    }

    private void PromptForNewOptionalMods()
    {
        if (!_config.Current.FirstLaunch && HasUnseenOptionalMods() &&
            State.GameState is not (GameRunState.Preparing or GameRunState.Launching or GameRunState.Running))
            Mutate(s => s with { Screen = BridgeProtocol.Screens.OnboardingOptionalMods });
    }

    private async Task RefreshAfterExitAsync(string? edition, TaskCompletionSource<bool> completion)
    {
        var refreshed = false;
        try
        {
            await _installGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!string.Equals(edition, _config.Current.Edition, StringComparison.Ordinal) ||
                    _state.GameState is GameRunState.Preparing or GameRunState.Launching or GameRunState.Running)
                    return;
                if (_backgroundDownload is { } running) await running.ConfigureAwait(false);
                await ApplyPerformanceProfileAsync(CancellationToken.None).ConfigureAwait(false);
                if (await RefreshManifestAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    await StopBackgroundWorkAsync().ConfigureAwait(false);
                    await PlanBackgroundWorkAsync(false, CancellationToken.None).ConfigureAwait(false);
                    await _backgroundDownload!.ConfigureAwait(false);
                }
                refreshed = true;
            }
            finally { _installGate.Release(); }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"post-game update check failed: {ex.Message}");
        }
        finally { completion.TrySetResult(refreshed); }
    }

    private sealed class InlineProgress(Action<ProgressSnapshot> report) : IProgress<ProgressSnapshot>
    {
        public void Report(ProgressSnapshot value) => report(value);
    }

    private IProgress<ProgressSnapshot> ReportProgress() => new InlineProgress(PublishProgress);

    private void PublishProgress(ProgressSnapshot snapshot)
    {
        bool changedStage;
        bool publish;
        lock (_stateLock)
        {
            changedStage = _state.Progress.Stage != snapshot.Stage;
            _state = _state with { Progress = snapshot };
            var now = Environment.TickCount64;
            publish = changedStage || now - _lastProgressAt >= 200;
            if (publish) _lastProgressAt = now;
        }
        if (publish) ProgressChanged?.Invoke(snapshot);
    }

    private void Stage(LauncherStage stage)
    {
        // A new phase has its own totals; do not carry a download's 100% into an installer.
        var progress = new ProgressSnapshot { Stage = stage };
        Mutate(s => s with { Progress = progress });
        ProgressChanged?.Invoke(progress);
    }

    private void RequireLauncherUpdateReady()
    {
        if (_launcherUpdateBlocked) throw new LauncherException("error.launcher.updateRequired");
    }

    private void OnUpdateProgress(ProgressSnapshot progress)
    {
        Mutate(s => s with { Screen = BridgeProtocol.Screens.Loading, Progress = progress });
        ProgressChanged?.Invoke(progress);
    }

    private void Fail(string errorKey, string? detail)
    {
        Mutate(s => s with
        {
            Screen = BridgeProtocol.Screens.Error,
            FatalErrorKey = errorKey,
            FatalErrorDetail = detail,
            ReadyToPlay = false,
            Progress = s.Progress with
            {
                Stage = LauncherStage.Failed,
                ErrorKey = errorKey,
                ErrorDetail = detail,
            },
        });
    }

    /// <summary>
    /// Applies a state transition. Locked because the bridge, the background
    /// download and the game-exit callback all mutate state concurrently, and a
    /// read-modify-write on <c>_state</c> would otherwise lose one of them.
    /// The listener runs outside the lock so a slow UI push cannot block callers.
    /// </summary>
    private void Mutate(Func<LauncherState, LauncherState> change)
    {
        LauncherState updated;
        lock (_stateLock)
        {
            updated = change(_state);
            _state = updated;
        }
        StateChanged?.Invoke(updated);
    }

    /// <summary>
    /// Runs an API call that the launcher can live without. A failure here degrades
    /// a feature; it must not abort startup, which is why the result is nullable.
    /// </summary>
    private static async Task<T?> TryApi<T>(Func<Task<T>> call) where T : class
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"api call failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Compares dotted numeric versions, treating a malformed value as older.</summary>
    private static bool IsNewer(string candidate, string current)
    {
        static int[] Parse(string value) => value
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part.TrimStart('v'), out var n) ? n : 0)
            .ToArray();

        var a = Parse(candidate);
        var b = Parse(current);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var left = i < a.Length ? a[i] : 0;
            var right = i < b.Length ? b[i] : 0;
            if (left != right)
                return left > right;
        }
        return false;
    }
}
