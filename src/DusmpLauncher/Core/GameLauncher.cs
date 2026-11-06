using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Installers;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.Version;
using CmlLib.Core.VersionLoader;
using DusmpLauncher.Authentication;
using DusmpLauncher.Configuration;
using DusmpLauncher.Models;
using ForgeInstallOptions = CmlLib.Core.Installer.Forge.ForgeInstallOptions;
using ForgeInstaller = CmlLib.Core.Installer.Forge.ForgeInstaller;
using NeoForgeInstallOptions = CmlLib.Core.Installer.NeoForge.Installers.NeoForgeInstallOptions;
using NeoForgeInstaller = CmlLib.Core.Installer.NeoForge.NeoForgeInstaller;
using NeoForgeVersion = CmlLib.Core.Installer.NeoForge.Versions.NeoForgeVersion;
using ForgeVersion = CmlLib.Core.Installer.Forge.Versions.ForgeVersion;

namespace DusmpLauncher.Core;

/// <summary>
/// Raised by everything on the launch path. Carries a translation key so the UI
/// can say what went wrong in the player's language, plus a technical detail that
/// belongs in the log pane and is never translated.
/// </summary>
public sealed class GameLaunchException : Exception
{
    public GameLaunchException(string errorKey, string detail, Exception? inner = null)
        : base($"{errorKey}: {detail}", inner)
    {
        ErrorKey = errorKey;
        Detail = detail;
    }

    public string ErrorKey { get; }

    /// <summary>Untranslated technical description: a path, an exit code, a library message.</summary>
    public string Detail { get; }
}

/// <summary>One line the game wrote, as it is handed to the UI log pane.</summary>
/// <param name="Text">The raw line; game output, so never translated.</param>
/// <param name="IsError">True when it came from stderr, so the pane can colour it.</param>
public sealed record GameLogLine(string Text, bool IsError);

/// <summary>
/// How the game run ended. A non-zero exit is reported rather than swallowed,
/// because to the player a window that vanishes silently is the worst outcome.
/// </summary>
public sealed record GameExitInfo(
    int ExitCode,
    bool Crashed,
    string? ErrorKey,
    string? ErrorDetail,
    string? LogFilePath);

/// <summary>
/// Installs the mod loader and starts Minecraft. Everything runs against the
/// launcher's own instance folder, so the pack never mixes with whatever the
/// player already has in their global <c>.minecraft</c>.
/// </summary>
public sealed class GameLauncher : IGameLauncher, IDisposable
{
    /// <summary>Loader ids as the manifest spells them. Protocol values, not display text.</summary>
    private const string LoaderVanilla = "vanilla";
    private const string LoaderNeoForge = "neoforge";
    private const string LoaderForge = "forge";
    private const string LoaderFabric = "fabric";
    private const string LoaderQuilt = "quilt";

    /// <summary>Identifies us in the game's launcher-brand arguments, so the pack can detect its own launcher.</summary>
    private const string LauncherBrand = "DusmpLauncher";

    private const string MicrosoftUserType = "msa";
    private const string OfflineUserType = "legacy";

    /// <summary>
    /// Bytes per log segment. A modded pack with a broken mod can print hundreds
    /// of megabytes of stack traces; keeping one full segment plus its predecessor
    /// preserves the interesting tail without filling the player's disk.
    /// </summary>
    private const long LogSegmentCapBytes = 4L * 1024 * 1024;

    /// <summary>How many past game logs to keep in the logs folder.</summary>
    private const int KeptLogFiles = 10;

    private static readonly Lazy<HttpClient> FallbackHttpClient = new(() => new HttpClient());

    private readonly LauncherPaths _paths;
    private readonly HttpClient _http;
    private readonly MinecraftLauncher _launcher;
    private readonly MinecraftPath _gamePath;
    private readonly int _downloadConcurrency;
    private readonly JavaProvisioner _java;

    private readonly object _logSync = new();
    private StreamWriter? _logWriter;
    private string? _logPath;
    private long _logBytes;

    private Process? _process;
    private string? _versionRefreshError;

    /// <param name="paths">Launcher folders; the game is rooted at <see cref="LauncherPaths.Instance"/>.</param>
    /// <param name="httpClient">
    /// Shared client so the loader installers and the version loader reuse the
    /// launcher's connection pool. A private client is created when none is given.
    /// </param>
    /// <param name="downloadConcurrency">
    /// Parallel download slots for the library's own installer, matching the value
    /// the player set for modpack downloads.
    /// </param>
    /// <param name="javaProvisioner">Override for tests; by default one is built over this launcher.</param>
    /// <param name="instanceOverride">
    /// A different .minecraft to install into. Used when the pack is being
    /// installed for another launcher — the official Minecraft Launcher wants
    /// the version, its libraries and its assets inside its own directory, and
    /// this is what lets the same installers write there instead of here.
    /// Null means the launcher's own instance, which is the normal case.
    /// </param>
    public GameLauncher(
        LauncherPaths paths,
        HttpClient? httpClient = null,
        int downloadConcurrency = 6,
        JavaProvisioner? javaProvisioner = null,
        string? instanceOverride = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _http = httpClient ?? FallbackHttpClient.Value;

        _paths.EnsureCreated();

        // Rooting MinecraftPath at the instance folder is what keeps mods, config,
        // saves, libraries and the managed runtime inside the launcher's own
        // directory instead of the player's global .minecraft.
        _gamePath = new MinecraftPath(string.IsNullOrWhiteSpace(instanceOverride) ? _paths.Instance : instanceOverride);
        _gamePath.CreateDirs();

        var concurrency = Math.Clamp(downloadConcurrency, 1, 16);
        _downloadConcurrency = concurrency;

        var parameters = MinecraftLauncherParameters.CreateDefault(_gamePath, _http);

        // The Mojang manifest loader is asked to fall back to its cached copy, and
        // the local loader is kept alongside it, so a warm start can still resolve
        // an already-installed loader version with no network at all.
        var versionLoaders = new VersionLoaderCollection
        {
            new MojangJsonVersionLoaderV2(_gamePath, _http) { UseLocalManifestWhenError = true },
            new LocalJsonVersionLoader(_gamePath),
        };
        parameters.VersionLoader = versionLoaders;

        parameters.GameInstaller = new ParallelGameInstaller(
            maxChecker: Math.Max(1, Environment.ProcessorCount),
            maxDownloader: concurrency,
            boundedCapacity: concurrency * 4,
            httpClient: _http);

        _launcher = new MinecraftLauncher(parameters);
        var runtimeInstaller = new MojangJavaRuntimeInstaller(_http, _gamePath,
            _launcher.GameInstaller, _launcher.RulesContext);
        _java = javaProvisioner ?? new JavaProvisioner(_gamePath, installRuntime: runtimeInstaller.InstallAsync);
    }

    /// <summary>Fires for every line the game writes to stdout or stderr.</summary>
    public event Action<GameLogLine>? LogLineWritten;

    /// <summary>Fires once the game process ends, crash or not.</summary>
    public event Action<GameExitInfo>? Exited;

    public event Action<string>? GameOutput;
    public event Action<int>? GameExited;

    /// <summary>The underlying CmlLib launcher, for callers that need version metadata.</summary>
    public MinecraftLauncher Launcher => _launcher;

    /// <summary>The instance-rooted game path, so other modules resolve the same folders.</summary>
    public MinecraftPath GamePath => _gamePath;

    public JavaProvisioner Java => _java;

    /// <summary>Path of the log file the current or last run wrote to, for a crash report.</summary>
    public string? CurrentLogFile
    {
        get { lock (_logSync) return _logPath; }
    }

    public bool IsRunning
    {
        get
        {
            var process = _process;
            try
            {
                return process is not null && !process.HasExited;
            }
            catch (InvalidOperationException)
            {
                // Process was never started or has already been disposed.
                return false;
            }
        }
    }

    /// <inheritdoc />
    public async Task<string> EnsureLoaderInstalledInAsync(
        string minecraftRoot,
        ModpackManifest manifest,
        IProgress<ProgressSnapshot>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(minecraftRoot))
            throw new ArgumentException("A .minecraft directory is required.", nameof(minecraftRoot));

        // A second launcher over the same settings, rooted elsewhere. Sharing the
        // HttpClient keeps the connection pool and the download concurrency this
        // one was built with.
        using var into = new GameLauncher(_paths, _http, _downloadConcurrency, instanceOverride: minecraftRoot);
        return await into.EnsureLoaderInstalledAsync(manifest, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Makes sure the version the pack needs exists on disk and returns the id to
    /// launch. Safe to call on every start: the loader installers are told to skip
    /// work that is already done, so a warm start costs a few hash checks.
    /// </summary>
    /// <returns>The launchable version id, e.g. <c>1.21.1-neoforge-21.1.209</c>.</returns>
    public async Task<string> EnsureLoaderInstalledAsync(
        ModpackManifest manifest,
        IProgress<ProgressSnapshot>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (string.IsNullOrWhiteSpace(manifest.Minecraft))
            throw new GameLaunchException("error.manifest.noMinecraftVersion", $"loader={manifest.Loader}");

        ct.ThrowIfCancellationRequested();

        var aggregator = new InstallProgressAggregator(progress, LauncherStage.InstallingMinecraft);
        // Flip the stage straight away: resolving the loader build can take a few
        // seconds on its own and the loading screen must not look stuck.
        aggregator.EmitNow();

        // The loader installers need the vanilla version resolved before they can
        // run, so the metadata is loaded up front rather than relied on lazily.
        await RefreshVersionsAsync(ct).ConfigureAwait(false);

        var loader = (manifest.Loader ?? LoaderVanilla).Trim().ToLowerInvariant();
        var loaderVersion = string.IsNullOrWhiteSpace(manifest.LoaderVersion) ? null : manifest.LoaderVersion!.Trim();

        // A named loader with no pinned build would resolve to whatever is newest
        // today, which is not what the server runs — the player would install a
        // different loader from one day to the next and not be told. The panel
        // already refuses to publish such a manifest; this refuses to act on one.
        if (loaderVersion is null && loader is not ("" or LoaderVanilla))
            throw new GameLaunchException("error.manifest.loaderVersionMissing", $"loader={loader}, minecraft={manifest.Minecraft}");

        string versionId;
        switch (loader)
        {
            case "":
            case LoaderVanilla:
                versionId = manifest.Minecraft;
                break;

            case LoaderNeoForge:
                versionId = await InstallNeoForgeAsync(manifest.Minecraft, loaderVersion, aggregator, ct)
                    .ConfigureAwait(false);
                break;

            case LoaderForge:
                versionId = await InstallForgeAsync(manifest.Minecraft, loaderVersion, aggregator, ct)
                    .ConfigureAwait(false);
                break;

            case LoaderFabric:
            case LoaderQuilt:
                // Neither has a CmlLib installer, but both publish a ready-made
                // version profile, which is all an install actually is.
                versionId = await new FabricFamilyInstaller(_http, _gamePath.Versions)
                    .InstallAsync(loader == LoaderQuilt, manifest.Minecraft, loaderVersion, ct)
                    .ConfigureAwait(false);
                break;

            default:
                throw new GameLaunchException("error.loader.unknown", loader);
        }

        ct.ThrowIfCancellationRequested();

        // A loader install writes a new version json; refresh the metadata so the
        // id below resolves, then let the library complete assets, libraries and
        // the managed runtime for it. Both steps are hash-checked, so this is a
        // no-op on a warm start.
        await RefreshVersionsAsync(ct).ConfigureAwait(false);

        try
        {
            await _launcher
                .InstallAsync(versionId, aggregator.FileProgress, aggregator.ByteProgress, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new GameLaunchException("error.install.minecraftFailed", $"{versionId}: {ex.Message}", ex);
        }

        aggregator.EmitNow();
        return versionId;
    }

    /// <summary>
    /// Starts the game and returns the running process. Output is already being
    /// captured when this returns, so no early crash line is missed.
    /// </summary>
    /// <param name="settings">
    /// Effective settings. The caller is expected to have already folded the
    /// manifest's advisory <c>java</c> block into these, since "pack suggests,
    /// player decides" is a policy the orchestrator owns, not this class.
    /// </param>
    /// <param name="server">The pack's server, joined directly when the player asked for it.</param>
    public async Task<Process> LaunchAsync(
        string versionId,
        GameSession session,
        GameSettings settings,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(settings);

        if (IsRunning)
            throw new GameLaunchException("error.game.alreadyRunning", $"pid={_process?.Id}");

        ct.ThrowIfCancellationRequested();

        var mSession = ToMSession(session);
        var version = await ResolveVersionAsync(versionId, ct).ConfigureAwait(false);
        var javaPath = await _java.ResolveJavaAsync(version, settings, progress: null, ct).ConfigureAwait(false);

        var maxRam = settings.MaxRamMb > 0 ? settings.MaxRamMb : ConfigStore.DetectDefaultMaxRamMb();
        var minRam = Math.Clamp(settings.MinRamMb, 256, maxRam);

        var option = new MLaunchOption
        {
            Path = _gamePath,
            StartVersion = version,
            JavaPath = javaPath,
            Session = mSession,
            MaximumRamMb = maxRam,
            MinimumRamMb = minRam,
            FullScreen = settings.Fullscreen,
            GameLauncherName = LauncherBrand,
            GameLauncherVersion = LauncherVersion,
            ExtraJvmArguments = ParseJvmArguments(settings, JavaProvisioner.RequiredMajorVersion(version) == 21),
        };

        // --width/--height alongside fullscreen only confuses the game, so the
        // window size is passed for windowed mode only.
        if (!settings.Fullscreen)
        {
            option.ScreenWidth = settings.WindowWidth;
            option.ScreenHeight = settings.WindowHeight;
        }

        Process process;
        try
        {
            process = _launcher.BuildProcess(version, option);
        }
        catch (Exception ex)
        {
            throw new GameLaunchException("error.launch.buildFailed", $"{versionId}: {ex.Message}", ex);
        }

        // Last chance to back out: after Start() the game owns its own lifetime and
        // killing it because a token fired would look like a crash to the player.
        ct.ThrowIfCancellationRequested();

        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;

        process.OutputDataReceived += OnOutputDataReceived;
        process.ErrorDataReceived += OnErrorDataReceived;
        process.Exited += OnProcessExited;

        OpenLogFile(versionId);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            process.OutputDataReceived -= OnOutputDataReceived;
            process.ErrorDataReceived -= OnErrorDataReceived;
            process.Exited -= OnProcessExited;
            CloseLogFile();
            process.Dispose();
            throw new GameLaunchException("error.launch.startFailed", $"{javaPath}: {ex.Message}", ex);
        }

        _process = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        // Arm exit notification only once the tracked process and its readers
        // are ready. Process raises Exited even if it ended before this point.
        process.EnableRaisingEvents = true;

        return process;
    }

    /// <summary>
    /// Waits for the running game and returns its exit code. Cancelling stops the
    /// wait, never the game.
    /// </summary>
    public async Task<int> WaitForExitAsync(CancellationToken ct = default)
    {
        var process = _process ?? throw new GameLaunchException("error.game.notRunning", "no process has been started");

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return process.ExitCode;
    }

    /// <summary>
    /// Ends the running game. Asks the process tree to close, then kills what is
    /// left: Minecraft spawns child processes and a plain Kill on the parent
    /// leaves them running and the window on screen.
    /// Safe to call when nothing is running — that is a no-op, not an error.
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        var process = _process;
        if (process is null)
            return;

        try
        {
            if (process.HasExited)
                return;

            AppendLog($"[launcher] stopping game (pid {process.Id})", isError: false);
            process.Kill(entireProcessTree: true);

            // Give it a moment to die so the UI does not flip back to "running"
            // on the next state push.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // It is taking its time; the Exited handler still reports the result.
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                      or System.ComponentModel.Win32Exception)
        {
            // Already gone, or the OS refused; either way there is nothing to stop.
            AppendLog($"[launcher] stop request ignored: {ex.Message}", isError: false);
        }
    }

    public void Dispose()
    {
        var process = _process;
        _process = null;

        if (process is not null)
        {
            process.OutputDataReceived -= OnOutputDataReceived;
            process.ErrorDataReceived -= OnErrorDataReceived;
            process.Exited -= OnProcessExited;

            // The game outlives the launcher on purpose when CloseOnLaunch is set,
            // so the process is released, never killed.
            process.Dispose();
        }

        CloseLogFile();
    }

    /// <summary>
    /// The launcher's own version, reported to the game so server-side code can
    /// tell which build a player is on.
    /// </summary>
    private static string LauncherVersion
    {
        get
        {
            var informational = typeof(GameLauncher).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
            {
                // Strip the "+<commit sha>" suffix the SDK appends; the game only
                // wants something short enough to log.
                var plus = informational.IndexOf('+');
                return plus < 0 ? informational : informational[..plus];
            }

            return typeof(GameLauncher).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        }
    }

    private async Task<string> InstallNeoForgeAsync(
        string minecraftVersion,
        string? loaderVersion,
        InstallProgressAggregator aggregator,
        CancellationToken ct)
    {
        var installer = new NeoForgeInstaller(_launcher);
        var options = new NeoForgeInstallOptions
        {
            // Forge-family installers shell out to java for their processors, so a
            // runtime has to exist before the loader can be built.
            JavaPath = await ResolveInstallerJavaAsync(minecraftVersion, aggregator, ct).ConfigureAwait(false),
            RulesContext = _launcher.RulesContext,
            FileProgress = aggregator.FileProgress,
            ByteProgress = aggregator.ByteProgress,
            CancellationToken = ct,
            SkipIfAlreadyInstalled = true,
        };

        try
        {
            if (loaderVersion is null)
            {
                // No pinned build: the library picks the newest one it knows for
                // this Minecraft version.
                return await installer.Install(minecraftVersion, options).ConfigureAwait(false);
            }

            // A pinned build is installed from a version built here rather than
            // by name. Looking it up by name asks NeoForge's maven-metadata.xml
            // for the version list, and that file has been observed listing only
            // the newest build — which made every pinned version fail with
            // "Cannot find version name" even though its installer jar is still
            // published and downloadable.
            var pinned = new NeoForgeVersion(minecraftVersion, loaderVersion);
            return await installer.Install(pinned, options).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new GameLaunchException(
                "error.install.loaderFailed",
                $"neoforge {minecraftVersion}/{loaderVersion ?? "latest"}: {ex.Message}",
                ex);
        }
    }

    private async Task<string> InstallForgeAsync(
        string minecraftVersion,
        string? loaderVersion,
        InstallProgressAggregator aggregator,
        CancellationToken ct)
    {
        var installer = new ForgeInstaller(_launcher, _http);
        var options = new ForgeInstallOptions
        {
            JavaPath = await ResolveInstallerJavaAsync(minecraftVersion, aggregator, ct).ConfigureAwait(false),
            RulesEvaluator = _launcher.RulesEvaluator,
            RulesContext = _launcher.RulesContext,
            FileProgress = aggregator.FileProgress,
            ByteProgress = aggregator.ByteProgress,
            CancellationToken = ct,
            SkipIfAlreadyInstalled = true,
        };

        try
        {
            if (loaderVersion is not null)
            {
                // Same reasoning as NeoForge: installing a pinned build by name
                // makes the library resolve it through the upstream version
                // index, so an index that stops listing older builds breaks every
                // pinned pack even though the installer jar is still published.
                // Building the version here depends only on the jar existing.
                var pinned = new ForgeVersion(minecraftVersion, loaderVersion);
                return await installer.Install(pinned, options).ConfigureAwait(false);
            }

            // Forge publishes both a "latest" and an older "recommended" build and
            // the manifest asked for neither, so pick the newest explicitly rather
            // than inheriting whichever default the library prefers.
            var available = (await installer.GetForgeVersions(minecraftVersion).ConfigureAwait(false))
                .ToList();

            if (available.Count == 0)
                throw new GameLaunchException("error.loader.noBuildForVersion", $"forge {minecraftVersion}");

            var newest = available.FirstOrDefault(v => v.IsLatestVersion)
                         ?? available.FirstOrDefault(v => v.IsRecommendedVersion)
                         ?? available[0];

            return await installer.Install(newest, options).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (GameLaunchException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new GameLaunchException(
                "error.install.loaderFailed",
                $"forge {minecraftVersion}/{loaderVersion ?? "latest"}: {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// Java for the loader installer's own processors. Deliberately resolved
    /// against the vanilla version and without the player's custom java path: the
    /// runtime Mojang pins is the one the installer is known to work with, and a
    /// mismatched custom JVM here corrupts the produced version.
    /// </summary>
    private async Task<string> ResolveInstallerJavaAsync(
        string minecraftVersion,
        InstallProgressAggregator aggregator,
        CancellationToken ct)
    {
        var vanilla = await ResolveVersionAsync(minecraftVersion, ct).ConfigureAwait(false);
        return await _java
            .ResolveJavaAsync(vanilla, settings: null, aggregator.Sink, ct)
            .ConfigureAwait(false);
    }

    private async Task<IVersion> ResolveVersionAsync(string versionId, CancellationToken ct)
    {
        try
        {
            return await _launcher.GetVersionAsync(versionId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // If the metadata refresh failed earlier, that is almost always the
            // real cause, so carry it into the detail instead of losing it.
            var detail = _versionRefreshError is null
                ? $"{versionId}: {ex.Message}"
                : $"{versionId}: {ex.Message} (version list: {_versionRefreshError})";

            throw new GameLaunchException("error.version.notFound", detail, ex);
        }
    }

    private async Task RefreshVersionsAsync(CancellationToken ct)
    {
        try
        {
            _ = await _launcher.GetAllVersionsAsync(ct).ConfigureAwait(false);
            _versionRefreshError = null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Offline, or Mojang is down. The locally saved version json may still
            // be enough for a warm start, so keep the reason and let the lookup
            // decide whether this was fatal.
            _versionRefreshError = ex.Message;
        }
    }

    /// <summary>
    /// Maps our session onto the one CmlLib wants. Field mapping only — no auth
    /// decisions live here, and the access token is copied, never logged.
    /// </summary>
    private static MSession ToMSession(GameSession session)
    {
        if (string.IsNullOrWhiteSpace(session.Username)
            || string.IsNullOrWhiteSpace(session.Uuid)
            || string.IsNullOrWhiteSpace(session.AccessToken))
        {
            // Deliberately names the empty fields only; never their values.
            var missing = new List<string>(3);
            if (string.IsNullOrWhiteSpace(session.Username)) missing.Add("username");
            if (string.IsNullOrWhiteSpace(session.Uuid)) missing.Add("uuid");
            if (string.IsNullOrWhiteSpace(session.AccessToken)) missing.Add("accessToken");

            throw new GameLaunchException("error.launch.invalidSession", string.Join(", ", missing));
        }

        return new MSession(session.Username, session.AccessToken, session.Uuid)
        {
            UserType = session.Profile.Type == AccountType.Microsoft ? MicrosoftUserType : OfflineUserType,
            Xuid = session.Xuid ?? string.Empty,
        };
    }

    /// <summary>
    /// Keeps quoted custom values together, then applies the selected preset's
    /// collector policy without overriding the adjustable memory controls.
    /// </summary>
    private static IEnumerable<MArgument> ParseJvmArguments(GameSettings settings, bool useLowMemoryTuning)
    {
        try
        {
            return JvmPresets.BuildArguments(settings, useLowMemoryTuning)
                .Select(token => new MArgument(token))
                .ToArray();
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new GameLaunchException("error.launch.badJvmArgs", ex.Message, ex);
        }
    }

    private void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is not null)
            AppendLog(e.Data, isError: false);
    }

    private void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is not null)
            AppendLog(e.Data, isError: true);
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (sender is not Process process)
            return;

        int exitCode;
        try
        {
            // Waiting on an already-exited process is what flushes the redirected
            // readers, so nothing from the final crash is lost.
            process.WaitForExit(2000);
            exitCode = process.ExitCode;
        }
        catch (Exception ex) when (ex is InvalidOperationException or SystemException)
        {
            exitCode = -1;
        }

        AppendLog($"[launcher] process exited with code {exitCode.ToString(CultureInfo.InvariantCulture)}", isError: exitCode != 0);

        string? logPath;
        lock (_logSync)
        {
            logPath = _logPath;
        }

        CloseLogFile();

        var crashed = exitCode != 0;
        var info = new GameExitInfo(
            exitCode,
            crashed,
            crashed ? "error.game.crashed" : null,
            crashed ? $"exit code {exitCode.ToString(CultureInfo.InvariantCulture)}" : null,
            logPath);

        Exited?.Invoke(info);
        GameExited?.Invoke(info.ExitCode);
    }

    private void OpenLogFile(string versionId)
    {
        CloseLogFile();
        PruneOldLogs();

        try
        {
            Directory.CreateDirectory(_paths.Logs);

            var name = $"game-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.log";
            var path = Path.Combine(_paths.Logs, name);
            var writer = new StreamWriter(
                new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read),
                System.Text.Encoding.UTF8)
            {
                AutoFlush = true,
            };

            lock (_logSync)
            {
                _logPath = path;
                _logWriter = writer;
                _logBytes = 0;
            }

            AppendLog($"[launcher] {LauncherBrand} {LauncherVersion} starting {versionId}", isError: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the log file must not stop the game: the UI log pane still
            // gets every line through the event.
            lock (_logSync)
            {
                _logWriter = null;
                _logPath = null;
                _logBytes = 0;
            }
        }
    }

    private void CloseLogFile()
    {
        StreamWriter? writer;
        lock (_logSync)
        {
            writer = _logWriter;
            _logWriter = null;
        }

        try
        {
            writer?.Dispose();
        }
        catch (IOException)
        {
            // Already broken; nothing left to save.
        }
    }

    private void AppendLog(string line, bool isError)
    {
        var text = isError ? "[stderr] " + line : line;

        lock (_logSync)
        {
            if (_logWriter is not null)
            {
                if (_logBytes >= LogSegmentCapBytes)
                    RollLogSegment();

                try
                {
                    _logWriter?.WriteLine(text);
                    _logBytes += text.Length + Environment.NewLine.Length;
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // Disk full or the file went away: stop writing, keep streaming.
                    _logWriter = null;
                }
            }
        }

        // Raised outside the lock: a UI handler that blocks must not be able to
        // stall the process's output reader thread.
        LogLineWritten?.Invoke(new GameLogLine(line, isError));
        GameOutput?.Invoke(line);
    }

    /// <summary>
    /// Rotates the log once a segment fills: the current file becomes
    /// <c>.1</c> and a fresh one starts. Keeps the newest output, which is the
    /// part a crash report needs. Must be called with <c>_logSync</c> held.
    /// </summary>
    private void RollLogSegment()
    {
        var path = _logPath;
        if (path is null)
            return;

        try
        {
            _logWriter?.Dispose();
            _logWriter = null;

            File.Move(path, path + ".1", overwrite: true);

            _logWriter = new StreamWriter(
                new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read),
                System.Text.Encoding.UTF8)
            {
                AutoFlush = true,
            };
            _logBytes = 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logWriter = null;
            _logBytes = 0;
        }
    }

    private void PruneOldLogs()
    {
        try
        {
            if (!Directory.Exists(_paths.Logs))
                return;

            var stale = new DirectoryInfo(_paths.Logs)
                .GetFiles("game-*.log*")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(KeptLogFiles);

            foreach (var file in stale)
            {
                try
                {
                    file.Delete();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A log the player has open in an editor; leave it.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Pruning is housekeeping, never a reason to refuse to launch.
        }
    }
}

/// <summary>
/// Folds CmlLib's two independent progress streams — one counting files, one
/// counting bytes — into the single <see cref="ProgressSnapshot"/> the UI reads.
/// Every number it publishes is one the installer actually reported; nothing is
/// interpolated, and <see cref="ProgressSnapshot.Percent"/> stays null until a
/// real total exists.
/// </summary>
internal sealed class InstallProgressAggregator
{
    /// <summary>Emission floor. The installers report per file and per chunk, far faster than any UI can paint.</summary>
    private static readonly TimeSpan EmitInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Window for the throughput figure; shorter than this and the number jitters uselessly.</summary>
    private static readonly TimeSpan RateWindow = TimeSpan.FromMilliseconds(500);

    private readonly IProgress<ProgressSnapshot>? _sink;
    private readonly LauncherStage _stage;
    private readonly object _sync = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private long _totalBytes;
    private long _doneBytes;
    private long _rateBaselineBytes;
    private TimeSpan _rateBaselineAt;
    private TimeSpan _lastEmitAt = TimeSpan.MinValue;
    private int _totalFiles;
    private int _doneFiles;
    private string? _currentFile;
    private double? _bytesPerSecond;

    public InstallProgressAggregator(IProgress<ProgressSnapshot>? sink, LauncherStage stage)
    {
        _sink = sink;
        _stage = stage;

        // SyncProgress reports inline rather than posting to a captured context,
        // which keeps the counters in the order the installer produced them.
        FileProgress = new SyncProgress<InstallerProgressChangedEventArgs>(ReportFiles);
        ByteProgress = new SyncProgress<ByteProgress>(ReportBytes);
    }

    public IProgress<InstallerProgressChangedEventArgs> FileProgress { get; }

    public IProgress<ByteProgress> ByteProgress { get; }

    /// <summary>The underlying sink, for handing to a nested step that reports snapshots itself.</summary>
    public IProgress<ProgressSnapshot>? Sink => _sink;

    /// <summary>Publishes the current numbers immediately, ignoring the throttle.</summary>
    public void EmitNow()
    {
        ProgressSnapshot snapshot;
        lock (_sync)
        {
            _lastEmitAt = _clock.Elapsed;
            snapshot = BuildLocked();
        }

        _sink?.Report(snapshot);
    }

    private void ReportFiles(InstallerProgressChangedEventArgs e)
    {
        ProgressSnapshot? snapshot;
        lock (_sync)
        {
            _totalFiles = e.TotalTasks;
            _doneFiles = e.ProgressedTasks;

            if (!string.IsNullOrEmpty(e.Name))
                _currentFile = e.Name;

            // A finished file is a visible milestone, so it always goes out.
            snapshot = ShouldEmitLocked(force: e.EventType == InstallerEventType.Done)
                ? BuildLocked()
                : null;
        }

        if (snapshot is not null)
            _sink?.Report(snapshot);
    }

    private void ReportBytes(ByteProgress progress)
    {
        ProgressSnapshot? snapshot;
        lock (_sync)
        {
            // The forge installers run several phases (vanilla jar, libraries,
            // processors) and each restarts its byte counter from zero. Publishing
            // the latest pair as reported keeps the figures honest; pretending they
            // accumulate would invent a total nobody measured.
            var restarted = progress.ProgressedBytes < _doneBytes || progress.TotalBytes != _totalBytes;

            _totalBytes = progress.TotalBytes;
            _doneBytes = progress.ProgressedBytes;

            if (restarted)
            {
                _rateBaselineBytes = progress.ProgressedBytes;
                _rateBaselineAt = _clock.Elapsed;
            }
            else
            {
                var elapsed = _clock.Elapsed - _rateBaselineAt;
                if (elapsed >= RateWindow)
                {
                    var delta = progress.ProgressedBytes - _rateBaselineBytes;
                    _bytesPerSecond = delta > 0 ? delta / elapsed.TotalSeconds : 0d;
                    _rateBaselineBytes = progress.ProgressedBytes;
                    _rateBaselineAt = _clock.Elapsed;
                }
            }

            snapshot = ShouldEmitLocked(force: false) ? BuildLocked() : null;
        }

        if (snapshot is not null)
            _sink?.Report(snapshot);
    }

    private bool ShouldEmitLocked(bool force)
    {
        if (_sink is null)
            return false;

        var now = _clock.Elapsed;
        if (!force && now - _lastEmitAt < EmitInterval)
            return false;

        _lastEmitAt = now;
        return true;
    }

    private ProgressSnapshot BuildLocked()
    {
        double? percent = null;
        if (_totalBytes > 0)
            percent = Math.Clamp(_doneBytes * 100d / _totalBytes, 0d, 100d);
        else if (_totalFiles > 0)
            percent = Math.Clamp(_doneFiles * 100d / _totalFiles, 0d, 100d);

        return new ProgressSnapshot
        {
            Stage = _stage,
            Percent = percent,
            CurrentFile = _currentFile,
            DownloadedBytes = _doneBytes,
            TotalBytes = _totalBytes,
            BytesPerSecond = _bytesPerSecond,
            FilesCompleted = _doneFiles,
            FilesTotal = _totalFiles,
        };
    }
}
