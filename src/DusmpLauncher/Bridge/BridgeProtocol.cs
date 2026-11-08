using System.Text.Json;
using System.Text.Json.Serialization;

namespace DusmpLauncher.Bridge;

/// <summary>
/// Wire format for both directions over Photino's single string channel.
/// The UI calls C# with <see cref="BridgeRequest"/> and gets exactly one
/// <see cref="BridgeResponse"/> back per <see cref="BridgeRequest.Id"/>;
/// C# pushes unsolicited state as <see cref="BridgeEvent"/>.
/// </summary>
public static class BridgeProtocol
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Commands the UI is allowed to invoke. Anything else is rejected.</summary>
    public static class Commands
    {
        public const string Ready = "ready";
        public const string GetState = "getState";
        public const string GetLanguages = "getLanguages";
        public const string SelectLanguage = "selectLanguage";
        public const string SelectJvmPreset = "selectJvmPreset";
        public const string GetCountries = "getCountries";
        public const string SelectCountry = "selectCountry";
        public const string LoginMicrosoft = "loginMicrosoft";
        public const string CancelMicrosoftLogin = "cancelMicrosoftLogin";
        public const string LoginOffline = "loginOffline";
        public const string ValidateOfflineName = "validateOfflineName";
        public const string Logout = "logout";
        public const string GetOptionalMods = "getOptionalMods";
        public const string ToggleOptionalMod = "toggleOptionalMod";
        public const string FinishOnboarding = "finishOnboarding";
        public const string SetPlayMode = "setPlayMode";
        public const string GetExternalLaunchers = "getExternalLaunchers";
        public const string SelectExternalLauncher = "selectExternalLauncher";
        public const string StartGame = "startGame";
        public const string StopGame = "stopGame";
        public const string SyncNow = "syncNow";
        public const string OpenSettings = "openSettings";
        public const string GetSettings = "getSettings";
        public const string SaveSettings = "saveSettings";
        public const string RepairInstallation = "repairInstallation";
        public const string ResetInstallation = "resetInstallation";
        public const string OpenSkinWindow = "openSkinWindow";

        /// <summary>Accounts the player has signed in with, for the account drawer.</summary>
        public const string ListAccounts = "listAccounts";
        public const string SelectAccount = "selectAccount";
        public const string RemoveAccount = "removeAccount";

        /// <summary>The page reporting that the player stepped back through the setup.</summary>
        public const string GoToScreen = "goToScreen";

        /// <summary>The packs the player can choose between, and the choice itself.</summary>
        public const string GetEditions = "getEditions";
        public const string SelectEdition = "selectEdition";
        public const string GetAppearance = "getAppearance";
        public const string UploadSkin = "uploadSkin";
        public const string ResetSkin = "resetSkin";
        public const string SetCape = "setCape";
        public const string OpenInstanceFolder = "openInstanceFolder";
        public const string GetInstalledMods = "getInstalledMods";
        public const string SetInstalledModEnabled = "setInstalledModEnabled";
        public const string ImportMods = "importMods";
        public const string OpenExternal = "openExternal";
        public const string RetryStartup = "retryStartup";
        public const string WindowMinimize = "windowMinimize";
        public const string WindowClose = "windowClose";
    }

    /// <summary>Events C# pushes to the UI.</summary>
    public static class Events
    {
        public const string Progress = "downloadProgress";
        public const string Stage = "stage";
        public const string PlayerProfile = "playerProfile";
        public const string ServerStatus = "serverStatus";
        public const string ModpackVersion = "modpackVersion";
        public const string OptionalMods = "optionalMods";
        public const string Screen = "screen";
        public const string Language = "language";
        public const string DeviceCode = "deviceCode";
        public const string GameState = "gameState";
        public const string Notice = "notice";
        public const string Log = "log";
    }

    /// <summary>Screens the host can ask the UI to show.</summary>
    public static class Screens
    {
        public const string Loading = "loading";
        public const string OnboardingLanguage = "onboarding-language";
        public const string OnboardingDevice = "onboarding-device";
        public const string OnboardingAccount = "onboarding-account";
        public const string OnboardingEdition = "onboarding-edition";
        public const string OnboardingPlayMode = "onboarding-play-mode";
        public const string OnboardingExternalLauncher = "onboarding-external-launcher";
        public const string OnboardingCountry = "onboarding-country";
        public const string OnboardingOptionalMods = "onboarding-optional-mods";
        public const string Main = "main";
        public const string Settings = "settings";
        public const string InstalledMods = "installed-mods";
        public const string Profile = "profile";
        public const string Error = "error";

        /// <summary>The steps of the first-run setup, in the order they are shown.</summary>
        public static readonly string[] Onboarding =
        {
            OnboardingLanguage, OnboardingDevice, OnboardingPlayMode, OnboardingExternalLauncher,
            OnboardingAccount, OnboardingCountry, OnboardingEdition, OnboardingOptionalMods,
        };
    }
}

public sealed class BridgeRequest
{
    /// <summary>Correlation id the UI generates; echoed back on the response.</summary>
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("command")] public string Command { get; init; } = "";

    /// <summary>Command arguments, shape depends on <see cref="Command"/>.</summary>
    [JsonPropertyName("payload")] public JsonElement? Payload { get; init; }
}

public sealed class BridgeResponse
{
    [JsonPropertyName("kind")] public string Kind => "response";
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("result")] public object? Result { get; init; }

    /// <summary>Translation key for the failure, never a raw exception message.</summary>
    [JsonPropertyName("errorKey")] public string? ErrorKey { get; init; }

    /// <summary>Extra detail for the log pane. Safe to show; carries no secrets.</summary>
    [JsonPropertyName("errorDetail")] public string? ErrorDetail { get; init; }

    public static BridgeResponse Success(string id, object? result = null)
        => new() { Id = id, Ok = true, Result = result };

    public static BridgeResponse Failure(string id, string errorKey, string? detail = null)
        => new() { Id = id, Ok = false, ErrorKey = errorKey, ErrorDetail = detail };
}

public sealed class BridgeEvent
{
    [JsonPropertyName("kind")] public string Kind => "event";
    [JsonPropertyName("event")] public string Event { get; init; } = "";
    [JsonPropertyName("payload")] public object? Payload { get; init; }

    public static BridgeEvent Create(string name, object? payload = null)
        => new() { Event = name, Payload = payload };
}
