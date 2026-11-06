using System.Text.Json;
using Microsoft.Data.Sqlite;
using DusmpLauncher.Models;

namespace DusmpLauncher.Core;

internal static class ExternalProfileValidator
{
    internal static void Validate(string launcher, string profilePath, ModpackManifest manifest)
    {
        var full = Path.GetFullPath(profilePath);
        if (!Directory.Exists(full) || !string.Equals(manifest.Loader, "neoforge", StringComparison.OrdinalIgnoreCase))
            throw new LauncherException("error.external.profileMismatch", "Select an existing NeoForge profile.");
        string? game = null, loader = null, version = null;
        if (launcher == "curseforge")
        {
            var metadata = Path.Combine(full, "minecraftinstance.json");
            if (!File.Exists(metadata)) throw new LauncherException("error.external.profileMismatch", "Select a CurseForge profile folder containing minecraftinstance.json.");
            using var doc = JsonDocument.Parse(File.ReadAllText(metadata));
            var root = doc.RootElement;
            game = root.GetProperty("gameVersion").GetString();
            var modLoader = root.GetProperty("baseModLoader");
            var name = modLoader.GetProperty("name").GetString();
            if (name?.StartsWith("neoforge-", StringComparison.OrdinalIgnoreCase) == true)
            { loader = "neoforge"; version = name[9..]; }
        }
        else if (launcher == "modrinth")
        {
            var profiles = Directory.GetParent(full);
            if (profiles is null || !profiles.Name.Equals("profiles", StringComparison.OrdinalIgnoreCase))
                throw new LauncherException("error.external.profileMismatch", "Select a folder directly inside ModrinthApp/profiles.");
            var db = Path.Combine(profiles.Parent!.FullName, "app.db");
            if (!File.Exists(db)) throw new LauncherException("error.external.profileMismatch", "Modrinth profile database not found.");
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT s.game_version, s.loader, s.loader_version FROM instances i JOIN instance_content_sets s ON s.id=i.applied_content_set_id WHERE i.path=$path";
            command.Parameters.AddWithValue("$path", Path.GetFileName(full));
            using var reader = command.ExecuteReader();
            if (reader.Read()) { game = reader.GetString(0); loader = reader.GetString(1); version = reader.IsDBNull(2) ? null : reader.GetString(2); }
        }
        else throw new LauncherException("error.external.profileMismatch", "Unsupported profile type.");
        if (!string.Equals(loader, "neoforge", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(game, manifest.Minecraft, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(version, manifest.LoaderVersion, StringComparison.OrdinalIgnoreCase))
            throw new LauncherException("error.external.profileMismatch", $"Expected Minecraft {manifest.Minecraft} and NeoForge {manifest.LoaderVersion}; selected profile has Minecraft {game ?? "unknown"} and {loader ?? "unknown"} {version ?? "unknown"}.");
    }
}
