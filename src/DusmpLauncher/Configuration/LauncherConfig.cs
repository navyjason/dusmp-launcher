using System.Text.Json.Serialization;

namespace DusmpLauncher.Configuration;

/// <summary>
/// Everything the launcher remembers between runs, minus secrets. This is the
/// file described in the spec; tokens live in <c>Authentication.SecureTokenStore</c>
/// instead, never here.
/// </summary>
public sealed class LauncherConfig
{
    [JsonPropertyName("firstLaunch")] public bool FirstLaunch { get; set; } = true;

    /// <summary>BCP-47 tag, e.g. <c>pt-BR</c>. Null until the player chooses.</summary>
    [JsonPropertyName("language")] public string? Language { get; set; }

    /// <summary>ISO 3166-1 alpha-2, e.g. <c>BR</c>. Null until the player chooses.</summary>
    [JsonPropertyName("country")] public string? Country { get; set; }

    [JsonPropertyName("accountType")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Models.AccountType AccountType { get; set; } = Models.AccountType.None;

    /// <summary>
    /// Which pack is installed and played. Null until the player has chosen, and
    /// null is also what an install from before editions carries — both mean
    /// "nothing has been downloaded for an edition yet".
    /// </summary>
    [JsonPropertyName("edition")] public string? Edition { get; set; }

    [JsonPropertyName("playMode")] public string PlayMode { get; set; } = "dusmp";
    [JsonPropertyName("externalLauncherId")] public string? ExternalLauncherId { get; set; }
    [JsonPropertyName("externalInstancePath")] public string? ExternalInstancePath { get; set; }

    /// <summary>Last offline username, so the field comes pre-filled.</summary>
    [JsonPropertyName("offlineUsername")] public string? OfflineUsername { get; set; }

    /// <summary>Opaque id of the saved Microsoft account in the account store.</summary>
    [JsonPropertyName("microsoftAccountId")] public string? MicrosoftAccountId { get; set; }

    /// <summary>Player choices per optional mod id. Absent id means "never asked".</summary>
    [JsonPropertyName("optionalMods")] public Dictionary<string, bool> OptionalMods { get; set; } = new();

    /// <summary>Optional mod ids the player has already been shown, so new ones can be flagged.</summary>
    [JsonPropertyName("seenOptionalMods")] public List<string> SeenOptionalMods { get; set; } = new();

    /// <summary>Region the selector settled on last time; re-validated each start.</summary>
    [JsonPropertyName("preferredRegionId")] public string? PreferredRegionId { get; set; }

    /// <summary>Modpack version currently installed on disk, or null for a fresh install.</summary>
    [JsonPropertyName("installedModpackVersion")] public string? InstalledModpackVersion { get; set; }

    [JsonPropertyName("performanceProfileRevision")] public int PerformanceProfileRevision { get; set; }

    [JsonPropertyName("performanceProfileGeneration")] public int PerformanceProfileGeneration { get; set; }
    [JsonPropertyName("performanceProfileApplied")] public Dictionary<string, int> PerformanceProfileApplied { get; set; } = new();

    [JsonPropertyName("game")] public GameSettings Game { get; set; } = new();

    /// <summary>Config schema version, so future migrations have something to branch on.</summary>
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
}

public sealed class GameSettings
{
    /// <summary>Selected device preset. Null preserves pre-preset custom JVM settings.</summary>
    [JsonPropertyName("jvmPreset")] public string? JvmPreset { get; set; }

    /// <summary>Max heap in MB. 0 means "let the launcher decide from installed RAM".</summary>
    [JsonPropertyName("maxRamMb")] public int MaxRamMb { get; set; }

    [JsonPropertyName("minRamMb")] public int MinRamMb { get; set; } = 512;

    /// <summary>Extra JVM args, split on spaces. Empty by default.</summary>
    [JsonPropertyName("extraJvmArgs")] public string ExtraJvmArgs { get; set; } = "";

    /// <summary>Absolute path to a java binary, or null to use the managed runtime.</summary>
    [JsonPropertyName("javaPath")] public string? JavaPath { get; set; }

    [JsonPropertyName("fullscreen")] public bool Fullscreen { get; set; }
    [JsonPropertyName("windowWidth")] public int WindowWidth { get; set; } = 854;
    [JsonPropertyName("windowHeight")] public int WindowHeight { get; set; } = 480;

    /// <summary>Close the launcher once the game window appears.</summary>
    [JsonPropertyName("closeOnLaunch")] public bool CloseOnLaunch { get; set; }

    /// <summary>Join the pack's server straight from the Play button.</summary>

    /// <summary>Parallel download slots. Clamped to 1..16 on load.</summary>
    [JsonPropertyName("downloadConcurrency")] public int DownloadConcurrency { get; set; } = 6;
}
