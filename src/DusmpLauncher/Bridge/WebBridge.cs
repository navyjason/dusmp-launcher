using System.Text.Json;
using DusmpLauncher.Configuration;
using DusmpLauncher.Core;
using DusmpLauncher.Localization;
using DusmpLauncher.Models;

namespace DusmpLauncher.Bridge;

/// <summary>
/// Translates the UI's JSON messages into <see cref="ILauncherCore"/> calls and
/// pushes state back out. Deliberately thin: it decides nothing, so no security
/// or update logic can end up reachable from the page.
/// </summary>
public sealed class WebBridge
{
    private readonly ILauncherCore _core;
    private readonly ILanguageManager _languages;
    private readonly Action<string> _send;
    private readonly Action _minimize;
    private readonly Action _close;
    private readonly Func<Task<string?>> _chooseFolder;
    private readonly Func<string, Task<string?>> _chooseExternalProfile;
    private readonly Func<Task<string?>> _choosePng;
    private readonly Action _openSkinWindow;
    private readonly Func<Task<string[]?>> _chooseMods;

    /// <summary>Cancels an in-flight device-code sign-in when the player backs out.</summary>
    private CancellationTokenSource? _signInCts;
    private readonly object _signInLock = new();
    private readonly SemaphoreSlim _signInGate = new(1, 1);

    public WebBridge(
        ILauncherCore core,
        ILanguageManager languages,
        Action<string> send,
        Action minimize,
        Action close,
        Func<Task<string?>>? chooseFolder = null,
        Func<Task<string?>>? choosePng = null,
        Action? openSkinWindow = null,
        Func<Task<string[]?>>? chooseMods = null,
        Func<string, Task<string?>>? chooseExternalProfile = null)
    {
        _core = core;
        _languages = languages;
        _send = send;
        _minimize = minimize;
        _close = close;
        _chooseFolder = chooseFolder ?? (() => Task.FromResult<string?>(null));
        _chooseExternalProfile = chooseExternalProfile ?? (_ => _chooseFolder());
        _choosePng = choosePng ?? (() => Task.FromResult<string?>(null));
        _openSkinWindow = openSkinWindow ?? (() => { });
        _chooseMods = chooseMods ?? (() => Task.FromResult<string[]?>(null));

        _core.StateChanged += OnStateChanged;
        _core.ProgressChanged += OnProgressChanged;
        _core.NoticeRaised += OnNotice;
    }

    /// <summary>True once the page has told us it is listening.</summary>
    public bool UiReady { get; private set; }
    public event Action? UiInitialized;

    /// <summary>Entry point for Photino's WebMessageReceived.</summary>
    public void HandleMessage(string raw)
    {
        BridgeRequest? request = null;
        try
        {
            request = JsonSerializer.Deserialize<BridgeRequest>(raw, BridgeProtocol.Json);
        }
        catch (JsonException ex)
        {
            Log($"bridge: unparseable message ({ex.Message})");
            return;
        }

        if (request is null || string.IsNullOrEmpty(request.Command))
            return;

        // Each request runs off the UI thread so a slow command cannot freeze the
        // window, and every failure comes back as a response rather than an
        // unobserved task exception.
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await DispatchAsync(request).ConfigureAwait(false);
                Respond(BridgeResponse.Success(request.Id, result));
            }
            catch (OperationCanceledException)
            {
                Respond(BridgeResponse.Failure(request.Id, "error.auth.cancelled"));
            }
            catch (LauncherException ex)
            {
                Respond(BridgeResponse.Failure(request.Id, ex.ErrorKey, ex.Detail));
            }
            catch (Exception ex)
            {
                Log($"bridge: {request.Command} failed: {ex}");
                Respond(BridgeResponse.Failure(request.Id, "error.unknown", ex.Message));
            }
        });
    }

    private async Task<object?> DispatchAsync(BridgeRequest request)
    {
        switch (request.Command)
        {
            case BridgeProtocol.Commands.Ready:
                UiReady = true;
                UiInitialized?.Invoke();
                return BuildBootstrap();

            case BridgeProtocol.Commands.GetState:
                return _core.State;

            case BridgeProtocol.Commands.GetLanguages:
                return new
                {
                    current = _languages.CurrentTag,
                    languages = _languages.AvailableLanguages,
                };

            case BridgeProtocol.Commands.SelectLanguage:
            {
                var tag = RequireString(request, "tag");
                await _core.SetLanguageAsync(tag).ConfigureAwait(false);
                // The page swaps strings in place from this payload; the spec is
                // explicit that the player must not have to restart anything.
                PushLanguage();
                return new { tag = _languages.CurrentTag };
            }

            case BridgeProtocol.Commands.GetCountries:
                return new
                {
                    current = _core.State.Country,
                    countries = _languages.Countries,
                };

            case BridgeProtocol.Commands.SelectJvmPreset:
                await _core.SetJvmPresetAsync(RequireString(request, "preset")).ConfigureAwait(false);
                return _core.CurrentSettings;

            case BridgeProtocol.Commands.SelectCountry:
            {
                var code = RequireString(request, "code");
                await _core.SetCountryAsync(code).ConfigureAwait(false);
                return new { code };
            }

            case BridgeProtocol.Commands.SyncNow:
                return new { completed = await _core.SyncNowAsync().ConfigureAwait(false) };

            case BridgeProtocol.Commands.SetPlayMode:
                await _core.SetPlayModeAsync(RequireString(request, "mode")).ConfigureAwait(false);
                return null;

            case BridgeProtocol.Commands.GetExternalLaunchers:
                return _core.GetExternalLaunchers();

            case BridgeProtocol.Commands.SelectExternalLauncher:
            {
                var id = RequireString(request, "id");
                var path = OptionalString(request, "root");
                var chooseFolder = request.Payload is { ValueKind: JsonValueKind.Object } selection && selection.TryGetProperty("chooseFolder", out var choose) && choose.ValueKind == JsonValueKind.True;
                if (chooseFolder)
                {
                    path = id is "modrinth" or "curseforge" ? await _chooseExternalProfile(id).ConfigureAwait(false) : await _chooseFolder().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(path)) return new { selected = false };
                }
                if (string.IsNullOrWhiteSpace(path))
                {
                    path = _core.GetExternalLaunchers().FirstOrDefault(x => x.Id == id)?.Root;
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        path = await _chooseFolder().ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(path)) return new { selected = false };
                    }
                }
                await _core.SelectExternalLauncherAsync(id, path).ConfigureAwait(false);
                return new { selected = true };
            }

            case BridgeProtocol.Commands.LoginMicrosoft:
            {
                using var attempt = new CancellationTokenSource();
                lock (_signInLock)
                {
                    _signInCts?.Cancel();
                    _signInCts = attempt;
                }
                var entered = false;
                try
                {
                    await _signInGate.WaitAsync(attempt.Token).ConfigureAwait(false);
                    entered = true;
                    return await _core.SignInMicrosoftAsync(prompt =>
                    {
                        lock (_signInLock)
                        {
                            if (ReferenceEquals(_signInCts, attempt) && !attempt.IsCancellationRequested)
                                Push(BridgeProtocol.Events.DeviceCode, prompt);
                        }
                    }, attempt.Token, OptionalBool(request, "useDeviceCode") ?? false).ConfigureAwait(false);
                }
                finally
                {
                    lock (_signInLock)
                        if (ReferenceEquals(_signInCts, attempt)) _signInCts = null;
                    if (entered) _signInGate.Release();
                }
            }

            case BridgeProtocol.Commands.CancelMicrosoftLogin:
                lock (_signInLock) _signInCts?.Cancel();
                return null;

            case BridgeProtocol.Commands.ValidateOfflineName:
            {
                var name = OptionalString(request, "username") ?? "";
                return new { errorKey = _core.ValidateOfflineUsername(name) };
            }

            case BridgeProtocol.Commands.LoginOffline:
            {
                var name = RequireString(request, "username");
                return await _core.SignInOfflineAsync(name).ConfigureAwait(false);
            }

            case BridgeProtocol.Commands.Logout:
                await _core.SignOutAsync().ConfigureAwait(false);
                return null;

            case BridgeProtocol.Commands.GetOptionalMods:
                return _core.State.OptionalMods;

            case BridgeProtocol.Commands.ToggleOptionalMod:
            {
                var id = RequireString(request, "id");
                var enabled = OptionalBool(request, "enabled") ?? true;
                await _core.ToggleOptionalModAsync(id, enabled).ConfigureAwait(false);
                return null;
            }

            case BridgeProtocol.Commands.FinishOnboarding:
                await _core.FinishOnboardingAsync().ConfigureAwait(false);
                return null;

            case BridgeProtocol.Commands.StopGame:
                await _core.StopGameAsync().ConfigureAwait(false);
                return null;

            case BridgeProtocol.Commands.StartGame:
                await _core.PlayAsync().ConfigureAwait(false);
                return null;

            case BridgeProtocol.Commands.OpenSettings:
            case BridgeProtocol.Commands.GetSettings:
                return _core.CurrentSettings;

            case BridgeProtocol.Commands.SaveSettings:
            {
                var settings = request.Payload is { } payload
                    ? payload.Deserialize<GameSettings>(BridgeProtocol.Json) ?? new GameSettings()
                    : new GameSettings();
                await _core.SaveSettingsAsync(settings).ConfigureAwait(false);
                return _core.CurrentSettings;
            }

            case BridgeProtocol.Commands.OpenSkinWindow:
                _openSkinWindow();
                return null;

            case BridgeProtocol.Commands.GoToScreen:
                _core.GoToScreen(RequireString(request, "screen"));
                return null;

            case BridgeProtocol.Commands.GetEditions:
                return await _core.GetEditionsAsync().ConfigureAwait(false);

            case BridgeProtocol.Commands.SelectEdition:
                await _core.SelectEditionAsync(RequireString(request, "id")).ConfigureAwait(false);
                return await _core.GetEditionsAsync().ConfigureAwait(false);

            case BridgeProtocol.Commands.ListAccounts:
                return await _core.ListAccountsAsync().ConfigureAwait(false);

            case BridgeProtocol.Commands.SelectAccount:
                await _core.SelectAccountAsync(RequireString(request, "id")).ConfigureAwait(false);
                return await _core.ListAccountsAsync().ConfigureAwait(false);

            case BridgeProtocol.Commands.RemoveAccount:
                await _core.RemoveAccountAsync(RequireString(request, "id")).ConfigureAwait(false);
                return await _core.ListAccountsAsync().ConfigureAwait(false);

            case BridgeProtocol.Commands.GetAppearance:
                return await _core.GetAppearanceAsync().ConfigureAwait(false);

            case BridgeProtocol.Commands.UploadSkin:
            {
                // The picker runs on the host side: the page never sees a path it
                // did not get from the player choosing a file.
                var path = await _choosePng().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(path)) return null;
                var variant = request.Payload is { } p && p.TryGetProperty("variant", out var v) ? v.GetString() : null;
                return await _core.UploadSkinAsync(path, variant ?? "classic").ConfigureAwait(false);
            }

            case BridgeProtocol.Commands.ResetSkin:
                return await _core.ResetSkinAsync().ConfigureAwait(false);

            case BridgeProtocol.Commands.SetCape:
            {
                var capeId = request.Payload is { } p && p.TryGetProperty("capeId", out var c) ? c.GetString() : null;
                return await _core.SetCapeAsync(string.IsNullOrWhiteSpace(capeId) ? null : capeId).ConfigureAwait(false);
            }

            case BridgeProtocol.Commands.ResetInstallation:
                await _core.ResetInstallationAsync().ConfigureAwait(false);
                return null;

            case BridgeProtocol.Commands.RepairInstallation:
                await _core.RepairAsync().ConfigureAwait(false);
                return null;

            case BridgeProtocol.Commands.GetInstalledMods:
                return await _core.GetInstalledModsAsync().ConfigureAwait(false);

            case BridgeProtocol.Commands.SetInstalledModEnabled:
            {
                if (request.Payload is not { } modPayload || !modPayload.TryGetProperty("enabled", out var modEnabled)
                    || modEnabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new LauncherException("error.mods.invalidFile");
                return await _core.SetInstalledModEnabledAsync(RequireString(request, "fileName"), modEnabled.GetBoolean()).ConfigureAwait(false);
            }

            case BridgeProtocol.Commands.ImportMods:
            {
                var files = await _chooseMods().ConfigureAwait(false);
                return files is { Length: > 0 }
                    ? await _core.ImportModsAsync(files).ConfigureAwait(false)
                    : await _core.GetInstalledModsAsync().ConfigureAwait(false);
            }

            case BridgeProtocol.Commands.OpenInstanceFolder:
                _core.OpenInstanceFolder();
                return null;

            case BridgeProtocol.Commands.OpenExternal:
                // Only ever a URL the host itself handed the page (a verification
                // URL or a news link); the page cannot talk us into a local path.
                _core.OpenExternalUrl(RequireString(request, "url"));
                return null;

            case BridgeProtocol.Commands.RetryStartup:
                _ = Task.Run(() => _core.RetryAsync());
                return null;

            case BridgeProtocol.Commands.WindowMinimize:
                _minimize();
                return null;

            case BridgeProtocol.Commands.WindowClose:
                _close();
                return null;

            default:
                throw new LauncherException("error.unknown", $"Unknown command '{request.Command}'.");
        }
    }

    /// <summary>Everything the page needs on its very first paint, in one round trip.</summary>
    private object BuildBootstrap() => new
    {
        state = _core.State,
        language = new
        {
            tag = _languages.CurrentTag,
            rtl = _languages.IsRightToLeft,
            table = _languages.CurrentTable,
        },
    };

    private void OnStateChanged(LauncherState state)
    {
        Push(BridgeProtocol.Events.Screen, state);
    }

    private void OnProgressChanged(ProgressSnapshot progress)
    {
        Push(BridgeProtocol.Events.Progress, progress);
    }

    /// <summary>The message is already translated; the page only toasts it.</summary>
    private void OnNotice(string message) => Push(BridgeProtocol.Events.Notice, new { message });

    private void PushLanguage() => Push(BridgeProtocol.Events.Language, new
    {
        tag = _languages.CurrentTag,
        rtl = _languages.IsRightToLeft,
        table = _languages.CurrentTable,
    });

    public void Push(string eventName, object? payload)
    {
        // The ready response contains the latest state, including changes that
        // happened while WebKit was still loading the page.
        if (!UiReady) return;
        Send(BridgeEvent.Create(eventName, payload));
    }

    public void Log(string line)
    {
        Console.Error.WriteLine(line);
        if (UiReady)
            Send(BridgeEvent.Create(BridgeProtocol.Events.Log, new { line }));
    }

    private void Respond(BridgeResponse response) => Send(response);

    private void Send(object envelope)
    {
        try
        {
            _send(EmbeddedAssets.UsePlatformOrigin(JsonSerializer.Serialize(envelope, BridgeProtocol.Json)));
        }
        catch (Exception ex)
        {
            // A failed push must never take the launcher down with it.
            Console.Error.WriteLine($"bridge: send failed: {ex.Message}");
        }
    }

    private static string RequireString(BridgeRequest request, string property)
    {
        var value = OptionalString(request, property);
        if (string.IsNullOrWhiteSpace(value))
            throw new LauncherException("error.unknown", $"'{property}' is required for {request.Command}.");
        return value;
    }

    private static string? OptionalString(BridgeRequest request, string property)
    {
        if (request.Payload is not { } payload || payload.ValueKind != JsonValueKind.Object)
            return null;
        if (!payload.TryGetProperty(property, out var element))
            return null;
        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    private static bool? OptionalBool(BridgeRequest request, string property)
    {
        if (request.Payload is not { } payload || payload.ValueKind != JsonValueKind.Object)
            return null;
        if (!payload.TryGetProperty(property, out var element))
            return null;
        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }
}
