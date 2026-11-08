using DusmpLauncher.Models;

namespace DusmpLauncher.Core;

internal static class UpdateNotifierInstaller
{
    internal static async Task InstallAsync(string root, ModpackManifest manifest, string? edition, CancellationToken ct)
    {
        if (!string.Equals(manifest.Loader, "neoforge", StringComparison.OrdinalIgnoreCase) ||
            manifest.Minecraft is not ("1.21.1" or "1.21.11") || string.IsNullOrWhiteSpace(edition))
            return;
        var jar = Path.Combine(root, "mods", "dusmp-update-notifier.jar");
        ExternalLauncherManager.EnsureNoLinks(root, jar);
        ExternalLauncherManager.EnsureNoLinks(root, jar + ".disabled");
        if (File.Exists(jar + ".disabled")) return;
        Directory.CreateDirectory(Path.GetDirectoryName(jar)!);
        var resource = $"DusmpLauncher.Resources.UpdateNotifier.{manifest.Minecraft}.jar";
        await using var input = typeof(UpdateNotifierInstaller).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing update notifier for Minecraft {manifest.Minecraft}.");
        var temp = jar + ".tmp";
        try
        {
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            File.Move(temp, jar, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        var configPath = Path.Combine(root, "config", "dusmp-update-notifier.json");
        ExternalLauncherManager.EnsureNoLinks(root, configPath);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        var json = System.Text.Json.JsonSerializer.Serialize(new { edition = edition, version = manifest.Version });
        await File.WriteAllTextAsync(configPath, json, ct).ConfigureAwait(false);
    }

}
