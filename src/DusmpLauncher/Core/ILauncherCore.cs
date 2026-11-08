using DusmpLauncher.Models;

namespace DusmpLauncher.Core;

/// <summary>
/// Orchestrates startup, onboarding and play. The bridge calls only into here;
/// it holds no logic of its own beyond translating JSON to these methods.
/// </summary>
public interface ILauncherCore
{
    LauncherState State { get; }

    /// <summary>Fires whenever <see cref="State"/> is replaced.</summary>
    event Action<LauncherState>? StateChanged;

    /// <summary>A short, already-translated line for the UI to toast.</summary>
    event Action<string>? NoticeRaised;

    /// <summary>Fires on download and verification progress, faster than state changes.</summary>
    event Action<ProgressSnapshot>? ProgressChanged;

    /// <summary>
    /// Runs the whole startup sequence: connectivity, API, versions, region,
    /// verification, and the background modpack download. Returns once the
    /// launcher knows which screen to show — the download keeps running after.
    /// </summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Re-runs startup after a failure, reusing anything already downloaded.</summary>
    Task RetryAsync(CancellationToken ct = default);

    Task SetLanguageAsync(string tag, CancellationToken ct = default);
    Task SetJvmPresetAsync(string preset, CancellationToken ct = default);
    Task SetCountryAsync(string code, CancellationToken ct = default);
    Task SetPlayModeAsync(string mode, CancellationToken ct = default);
    IReadOnlyList<ExternalLauncherInfo> GetExternalLaunchers();
    Task SelectExternalLauncherAsync(string id, string? root, CancellationToken ct = default);
    Task<PlayerProfile> SignInMicrosoftAsync(Action<Bridge.DeviceCodePrompt> onPrompt, CancellationToken ct = default, bool useDeviceCode = false);
    Task<PlayerProfile> SignInOfflineAsync(string username, CancellationToken ct = default);
    Task SignOutAsync(CancellationToken ct = default);
    Task ToggleOptionalModAsync(string modId, bool enabled, CancellationToken ct = default);

    /// <summary>Closes onboarding and moves to the main screen, download or not.</summary>
    Task FinishOnboardingAsync(CancellationToken ct = default);

    /// <summary>
    /// Verifies, updates if needed, then starts Minecraft. Throws with a
    /// translation key when something blocks the launch.
    /// </summary>
    Task PlayAsync(CancellationToken ct = default);

    /// <summary>
    /// Hands the pasted sign-in address to a pending Microsoft login. False when
    /// no sign-in is waiting for one.
    /// </summary>

    /// <summary>Stops the running game, so Play can double as a Stop button.</summary>
    Task StopGameAsync(CancellationToken ct = default);
    Task<bool> SyncNowAsync(CancellationToken ct = default);

    /// <summary>Full rehash and repair of the installation.</summary>
    Task RepairAsync(CancellationToken ct = default);

    /// <summary>
    /// Removes every launcher-installed file and installs the modpack again.
    /// Worlds, screenshots and settings are kept.
    /// </summary>
    Task ResetInstallationAsync(CancellationToken ct = default);

    /// <summary>Skin, skin history and capes, or null for an offline account.</summary>
    /// <summary>The packs the player can choose between, and the one in use.</summary>
    Task<object> GetEditionsAsync(CancellationToken ct = default);

    /// <summary>Switches to an edition and installs it. The only thing that starts a download.</summary>
    Task SelectEditionAsync(string id, CancellationToken ct = default);

    /// <summary>Follows the player back through the setup, so the host and the page agree on the current step.</summary>
    void GoToScreen(string screen);

    /// <summary>Every signed-in account, and which one is active.</summary>
    Task<object> ListAccountsAsync(CancellationToken ct = default);

    /// <summary>Switches to another stored account.</summary>
    Task SelectAccountAsync(string id, CancellationToken ct = default);

    /// <summary>Forgets an account, along with its stored token.</summary>
    Task RemoveAccountAsync(string id, CancellationToken ct = default);

    Task<Network.AppearanceState?> GetAppearanceAsync(CancellationToken ct = default);

    Task<Network.AppearanceState?> UploadSkinAsync(string filePath, string variant, CancellationToken ct = default);

    Task<Network.AppearanceState?> ResetSkinAsync(CancellationToken ct = default);

    Task<Network.AppearanceState?> SetCapeAsync(string? capeId, CancellationToken ct = default);

    Task SaveSettingsAsync(Configuration.GameSettings settings, CancellationToken ct = default);

    Task<IReadOnlyList<InstalledMod>> GetInstalledModsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<InstalledMod>> SetInstalledModEnabledAsync(string fileName, bool enabled, CancellationToken ct = default);
    Task<IReadOnlyList<InstalledMod>> ImportModsAsync(IReadOnlyList<string> files, CancellationToken ct = default);

    /// <summary>Current game settings, for the settings screen.</summary>
    Configuration.GameSettings CurrentSettings { get; }

    /// <summary>
    /// Null when the name is usable, otherwise a translation key. Exposed here so
    /// the UI can validate as the player types without a round trip through auth.
    /// </summary>
    string? ValidateOfflineUsername(string username);

    /// <summary>Reveals the shared instances directory in the desktop file manager.</summary>
    void OpenInstanceFolder();

    /// <summary>
    /// Opens a URL in the player's real browser. Only accepts http/https, because
    /// the UI is allowed to ask for this and must not be able to launch a local
    /// program through it.
    /// </summary>
    void OpenExternalUrl(string url);
}
