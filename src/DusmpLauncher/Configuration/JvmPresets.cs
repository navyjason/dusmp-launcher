using CmlLib.Core.CommandParser;

namespace DusmpLauncher.Configuration;

/// <summary>
/// Device presets supply collector selection and starting heap values. Applying
/// a preset sets its defaults once; subsequent launches keep the player's RAM.
/// </summary>
public static class JvmPresets
{
    // Keep existing stored IDs and heap budgets when relabeling hardware tiers.
    public const string Low8Gb = "low8gb";
    public const string Default = "default";
    public const string Smooth = "smooth";
    public const string Standard = "standard";
    public const int ProfileRevision = 1;

    private static readonly HashSet<string> CollectorOptions = new(StringComparer.Ordinal)
    {
        "UseG1GC", "UseZGC", "UseSerialGC", "UseParallelGC", "UseParallelOldGC",
        "UseConcMarkSweepGC", "UseParNewGC", "UseShenandoahGC", "UseEpsilonGC",
        "ZGenerational",
    };

    // ZGC-specific knobs must not override or accompany the G1 preset. Keep
    // unrelated flags such as ZeroTLAB intact rather than removing every Z* flag.
    private static readonly HashSet<string> ZgcOptions = new(StringComparer.Ordinal)
    {
        "ZAllocationSpikeTolerance", "ZCollectionInterval", "ZCollectionIntervalMajor",
        "ZCollectionIntervalMinor", "ZFragmentationLimit", "ZMarkStackSpaceLimit",
        "ZProactive", "ZUncommit", "ZUncommitDelay", "ZVerifyRoots", "ZVerifyViews",
        "ZStatisticsInterval", "SoftMaxHeapSize",
    };

    public static bool IsKnown(string? id) => Normalize(id) is Low8Gb or Default or Smooth or Standard;

    /// <summary>Applies a chosen preset without discarding unrelated custom arguments.</summary>
    public static void Apply(GameSettings settings, string id)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var preset = RequireKnown(id);
        settings.JvmPreset = preset;
        settings.MinRamMb = preset is Smooth or Standard ? 2048 : 1024;
        settings.MaxRamMb = preset switch
        {
            Low8Gb => 4096,
            Standard => 8192,
            Smooth => 10240,
            _ => 6144,
        };
    }

    /// <summary>
    /// Builds the effective extra arguments. Heap switches are supplied once by
    /// MLaunchOption's memory fields, and the preset owns collector selection.
    /// Legacy configurations without a preset retain their original arguments.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(GameSettings settings, bool useLowMemoryTuning = true)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var custom = string.IsNullOrWhiteSpace(settings.ExtraJvmArgs)
            ? Array.Empty<string>()
            : Parser.ParseCommandLineToArguments(settings.ExtraJvmArgs)
                .Where(token => !string.IsNullOrWhiteSpace(token)).ToArray();

        if (string.IsNullOrWhiteSpace(settings.JvmPreset))
            return custom;

        var preset = RequireKnown(settings.JvmPreset);
        var arguments = custom.Where(argument => !ConflictsWithPreset(argument, preset, useLowMemoryTuning)).ToList();
        if (preset == Smooth)
        {
            arguments.Add("-XX:+UseZGC");
            arguments.Add("-XX:+ZGenerational");
        }
        else
        {
            arguments.Add("-XX:+UseG1GC");
            if (preset == Low8Gb && useLowMemoryTuning)
            {
                arguments.Add("-XX:-ClassUnloadingWithConcurrentMark");
                arguments.Add("-XX:MinHeapFreeRatio=10");
                arguments.Add("-XX:MaxHeapFreeRatio=20");
                arguments.Add("-XX:G1PeriodicGCInterval=30000");
            }
        }
        return arguments;
    }

    private static bool ConflictsWithPreset(string argument, string preset, bool useLowMemoryTuning)
    {
        if (argument.StartsWith("-Xms", StringComparison.Ordinal)
            || argument.StartsWith("-Xmx", StringComparison.Ordinal))
            return true;

        if (!argument.StartsWith("-XX:", StringComparison.Ordinal))
            return false;

        var name = argument[4..].TrimStart('+', '-');
        var equals = name.IndexOf('=');
        if (equals >= 0) name = name[..equals];

        return (name is "ClassUnloadingWithConcurrentMark" or "MinHeapFreeRatio" or "MaxHeapFreeRatio" or "G1PeriodicGCInterval" or "G1PeriodicGCInvokesConcurrent" && (preset == Standard || preset == Low8Gb && useLowMemoryTuning))
            || CollectorOptions.Contains(name)
            || name is "InitialHeapSize" or "MinHeapSize" or "MaxHeapSize"
            || (preset == Smooth && name.StartsWith("G1", StringComparison.Ordinal))
            || (preset != Smooth && ZgcOptions.Contains(name));
    }

    private static string? Normalize(string? id) => id?.Trim().ToLowerInvariant();

    private static string RequireKnown(string? id)
    {
        var preset = Normalize(id);
        if (preset is not (Low8Gb or Default or Smooth or Standard))
            throw new ArgumentException("Unknown JVM device preset.", nameof(id));
        return preset;
    }
}
