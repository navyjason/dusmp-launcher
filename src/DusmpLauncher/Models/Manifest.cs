using System.Text.Json.Serialization;

namespace DusmpLauncher.Models;

/// <summary>
/// One modpack version, exactly as the API serves it at
/// <c>GET /modpack/manifest</c>. This is the single source of truth for what
/// an installation should contain; nothing else may decide that.
/// </summary>
public sealed record ModpackManifest
{
    [JsonPropertyName("version")] public string Version { get; init; } = "0.0.0";
    [JsonPropertyName("edition")] public string? Edition { get; init; }
    [JsonPropertyName("minecraft")] public string Minecraft { get; init; } = "";
    [JsonPropertyName("loader")] public string Loader { get; init; } = "vanilla";

    /// <summary>Exact loader build, e.g. <c>21.1.209</c>. Null means "newest for this Minecraft version".</summary>
    [JsonPropertyName("loaderVersion")] public string? LoaderVersion { get; init; }

    /// <summary>Optional server the Play button can join directly.</summary>
    [JsonPropertyName("server")] public ManifestServer? Server { get; init; }

    /// <summary>Java args and heap the pack wants. Advisory: user settings win.</summary>
    [JsonPropertyName("java")] public ManifestJava? Java { get; init; }

    [JsonPropertyName("files")] public IReadOnlyList<ModFile> Files { get; init; } = Array.Empty<ModFile>();

    /// <summary>Publication timestamp, used only for display.</summary>
    [JsonPropertyName("releasedAt")] public DateTimeOffset? ReleasedAt { get; init; }
}

public sealed class ManifestServer
{
    [JsonPropertyName("host")] public string Host { get; init; } = "";
    [JsonPropertyName("port")] public int Port { get; init; } = 25565;
}

public sealed class ManifestJava
{
    [JsonPropertyName("majorVersion")] public int? MajorVersion { get; init; }
    [JsonPropertyName("recommendedMaxRamMb")] public int? RecommendedMaxRamMb { get; init; }
    [JsonPropertyName("extraJvmArgs")] public IReadOnlyList<string> ExtraJvmArgs { get; init; } = Array.Empty<string>();
}
