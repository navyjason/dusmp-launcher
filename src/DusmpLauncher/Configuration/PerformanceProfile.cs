using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DusmpLauncher.Configuration;

/// <summary>Applies the selected low-workload graphics settings without replacing other settings.</summary>
internal static class PerformanceProfile
{
    private static readonly Dictionary<string, string> Options = new()
    {
        ["renderDistance"] = "6", ["simulationDistance"] = "5", ["maxFps"] = "60",
        ["graphicsMode"] = "0", ["renderClouds"] = "\"false\"", ["particles"] = "2",
        ["mipmapLevels"] = "0", ["biomeBlendRadius"] = "0", ["entityDistanceScaling"] = "0.6",
        ["enableVsync"] = "false"
    };

    internal static bool IsProfileFile(string path) => path.Replace('\\', '/').ToLowerInvariant() is
        "options.txt" or "config/gpu_booster_config.txt" or "config/modernfix-mixins.properties" or
        "config/sodium-options.json" or "config/sodium-extra-options.json" or
        "config/roughlyenoughitems/config.json5" or "config/vista-client.toml";

    internal static void Apply(string root, Func<string, bool> shouldApply, Action<string> applied, bool createBackup = true)
    {
        // Backups are kept outside the pack files and never overwritten.
        var backup = Path.Combine(root, ".dusmp-profile-backups", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff"));
        void Change(string relative, Func<string, string> transform, bool create = false)
        {
            if (!shouldApply(relative)) return;
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path) && !create) return;
            var old = File.Exists(path) ? File.ReadAllText(path) : "";
            var updated = transform(old);
            if (updated == old) { applied(relative); return; }
            if (createBackup && File.Exists(path))
            {
                var save = Path.Combine(backup, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(save)!);
                File.Copy(path, save, false);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".dusmp-profile.tmp";
            File.WriteAllText(tmp, updated);
            File.Move(tmp, path, true);
            applied(relative);
        }
        Change("options.txt", text => SetLines(text, Options, ':'), true);
        Change("config/gpu_booster_config.txt", text => SetLines(text, new() { ["renderbuffer_depth"] = "false" }, ':' ));
        Change("config/modernfix-mixins.properties", text => SetLines(text, new() { ["mixin.perf.dynamic_resources"] = "true" }, '='));
        Change("config/sodium-options.json", text => PatchJson(text, """{"quality":{"weather_quality":"FAST","leaves_quality":"FAST","enable_vignette":false},"performance":{"chunk_builder_threads":2},"advanced":{"cpu_render_ahead_limit":2}}"""));
        Change("config/sodium-extra-options.json", text => PatchJson(text, """{"particle_settings":{"rain_splash":false}}"""));
        Change("config/roughlyenoughitems/config.json5", text => PatchJson5Values(text, new() { ["reduceMotion"] = "true", ["hideEntryPanelIfIdle"] = "true", ["horizontalEntriesBoundariesColumns"] = "5", ["verticalEntriesBoundariesRows"] = "8" }));
        Change("config/vista-client.toml", text => PatchToml(text));
    }

    private static string SetLines(string text, Dictionary<string, string> values, char separator)
    {
        foreach (var (key, value) in values)
        {
            var pattern = @"(?m)^\s*" + Regex.Escape(key) + @"\s*" + Regex.Escape(separator.ToString()) + @"[^\r\n]*";
            var line = key + separator + value;
            text = Regex.IsMatch(text, pattern) ? Regex.Replace(text, pattern, _ => line) : text.TrimEnd('\r', '\n') + (text.Length > 0 ? "\n" : "") + line + "\n";
        }
        return text;
    }

    private static string PatchJson(string text, string patch)
    {
        var options = new JsonNodeOptions { PropertyNameCaseInsensitive = false };
        var root = JsonNode.Parse(text, options, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
            ?? throw new InvalidDataException("Invalid mod configuration JSON.");
        Merge(root, JsonNode.Parse(patch)!.AsObject());
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }
    private static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch)
            if (value is JsonObject child)
            {
                if (target[key] is not JsonObject) target[key] = new JsonObject();
                Merge(target[key]!.AsObject(), child);
            }
            else target[key] = value?.DeepClone();
    }
    private static string PatchJson5Values(string text, Dictionary<string, string> values)
    {
        foreach (var (key, value) in values)
        {
            var pattern = @"(?m)([\""']?" + Regex.Escape(key) + @"[\""']?\s*:\s*)(?:true|false|[-+]?\d+(?:\.\d+)?)";
            // Missing keys are left alone rather than guessing where a JSON5 section belongs.
            text = Regex.Replace(text, pattern, match => match.Groups[1].Value + value);
        }
        return text;
    }
    private static string PatchToml(string text)
    {
        var values = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["mirror"] = new() { ["render_distance"] = "24", ["resolution_scale"] = "4" },
            ["mirror.recursion"] = new() { ["mode"] = "\"SHARED\"" },
            ["television"] = new() { ["render_distance"] = "24" },
            ["television.live_feed"] = new() { ["update_fps"] = "5.0", ["min_update_fps"] = "2.0", ["update_distance"] = "12.0", ["resolution_scale"] = "4" },
            ["wave_gate"] = new() { ["resolution_scale"] = "4" }
        };
        var section = "";
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var sectionMatch = Regex.Match(lines[i], @"^\s*\[([^\]]+)\]");
            if (sectionMatch.Success) { section = sectionMatch.Groups[1].Value.Trim(); continue; }
            if (!values.TryGetValue(section, out var fields)) continue;
            var match = Regex.Match(lines[i], @"^(\s*)([A-Za-z0-9_]+)(\s*=\s*)([^#\r\n]*)(.*)$");
            if (match.Success && fields.TryGetValue(match.Groups[2].Value, out var value))
                lines[i] = match.Groups[1].Value + match.Groups[2].Value + match.Groups[3].Value + value + match.Groups[5].Value;
        }
        return string.Join("\n", lines);
    }
}
