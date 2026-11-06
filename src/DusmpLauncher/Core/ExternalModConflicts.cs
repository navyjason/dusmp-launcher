using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DusmpLauncher.Core;

internal static class ExternalModConflicts
{
    internal static IReadOnlySet<string> ReadIds(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var json = zip.GetEntry("fabric.mod.json") ?? zip.GetEntry("quilt.mod.json");
            if (json is not null && json.Length <= 262144)
            {
                using var doc = JsonDocument.Parse(json.Open());
                var root = doc.RootElement;
                if (root.TryGetProperty("quilt_loader", out var quilt)) root = quilt;
                if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } value)
                    return new HashSet<string>(StringComparer.OrdinalIgnoreCase) { value };
            }
            var toml = zip.GetEntry("META-INF/neoforge.mods.toml") ?? zip.GetEntry("META-INF/mods.toml");
            if (toml is null || toml.Length > 262144) return new HashSet<string>();
            using var stream = new StreamReader(toml.Open());
            var text = stream.ReadToEnd();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match block in Regex.Matches(text, @"(?ms)^\s*\[\[mods\]\]\s*(.*?)(?=^\s*\[|\z)"))
            {
                var id = Regex.Match(block.Groups[1].Value, "(?m)^\\s*modId\\s*=\\s*[\\\"']([a-zA-Z0-9_.-]+)[\\\"']");
                if (id.Success) ids.Add(id.Groups[1].Value);
            }
            return ids;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { return new HashSet<string>(); }
    }
}
