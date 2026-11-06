using System.Diagnostics;
using System.Text;

namespace DusmpLauncher.Core;

/// <summary>A snapshot allows detection checks without inspecting or changing the user's desktop.</summary>
public sealed record ExternalLauncherEnvironment(bool IsWindows, string Home, string DataHome, string ConfigHome,
    string LocalAppData, string RoamingAppData, IReadOnlyList<string> ProgramDirectories,
    IReadOnlyList<string> PathDirectories, IReadOnlyList<string> ApplicationDirectories,
    IReadOnlyList<string> FlatpakInstallations, IReadOnlyList<string> IconDirectories)
{
    public static ExternalLauncherEnvironment Current()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var data = AbsoluteEnvironmentPath("XDG_DATA_HOME") ?? Path.Combine(home, ".local", "share");
        var config = AbsoluteEnvironmentPath("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config");
        var dataDirs = (Environment.GetEnvironmentVariable("XDG_DATA_DIRS") ?? "/usr/local/share:/usr/share")
            .Split(':', StringSplitOptions.RemoveEmptyEntries).Where(Path.IsPathFullyQualified);
        var flatpaks = new[] { AbsoluteEnvironmentPath("FLATPAK_USER_DIR") ?? Path.Combine(data, "flatpak"),
            AbsoluteEnvironmentPath("FLATPAK_SYSTEM_DIR") ?? "/var/lib/flatpak" };
        return new(OperatingSystem.IsWindows(), home, data, config,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Where(x => x.Length > 0).ToArray(),
            (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Where(Path.IsPathFullyQualified).ToArray(),
            new[] { Path.Combine(data, "applications") }.Concat(dataDirs.Select(x => Path.Combine(x, "applications")))
                .Concat(flatpaks.Select(x => Path.Combine(x, "exports", "share", "applications"))).Distinct().ToArray(), flatpaks,
            // Same search order as the desktop entries, plus the legacy pixmaps
            // directory. Only hicolor is read: the user's own theme restyles
            // vendor marks, and a restyled logo is worse than the real one.
            new[] { Path.Combine(data, "icons"), Path.Combine(home, ".icons") }
                .Concat(dataDirs.Select(x => Path.Combine(x, "icons")))
                .Concat(flatpaks.Select(x => Path.Combine(x, "exports", "share", "icons")))
                .Concat(dataDirs.Select(x => Path.Combine(x, "pixmaps")))
                .Distinct().ToArray());
    }
    private static string? AbsoluteEnvironmentPath(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } path && Path.IsPathFullyQualified(path) ? path : null;
}

public sealed record ExternalLaunchCommand(string Executable, IReadOnlyList<string> Arguments, string Kind)
{
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } = new Dictionary<string, string>();

    public ProcessStartInfo CreateStartInfo(string root)
    {
        var info = new ProcessStartInfo(Executable) { UseShellExecute = false, WorkingDirectory = root };
        foreach (var argument in Arguments) info.ArgumentList.Add(argument);
        foreach (var (name, value) in EnvironmentVariables) info.Environment[name] = value;
        return info;
    }

    public ProcessStartInfo ForInstance(string root, string instanceId)
    {
        var info = CreateStartInfo(root);
        info.ArgumentList.Add("--dir"); info.ArgumentList.Add(root);
        info.ArgumentList.Add("--launch"); info.ArgumentList.Add(instanceId);
        return info;
    }
}

/// <summary>Reads known install locations and desktop entries; never runs launchers during discovery.</summary>
public sealed class ExternalLauncherDiscovery
{
    private sealed record Definition(string Id, string Name, string DirectoryName, string[] Executables, string? FlatpakId = null)
    {
        /// <summary>
        /// The settings file that marks a portable copy and names a custom
        /// instance directory. Each fork of MultiMC renamed it after itself.
        /// </summary>
        public string? ConfigFile { get; init; }
    }

    /// <summary>
    /// The launchers that keep instances the way MultiMC does: a folder with
    /// <c>instance.cfg</c>, <c>mmc-pack.json</c> and a <c>.minecraft</c> inside.
    /// Prism is a fork of PolyMC, which is a fork of MultiMC, and all three still
    /// read each other's layout.
    /// </summary>
    public static readonly string[] MmcFamily = { "prism", "polymc", "multimc" };

    /// <summary>
    /// What each one calls the loader in <c>mmc-pack.json</c>.
    ///
    /// Read from the metadata servers rather than assumed: Prism and MultiMC both
    /// publish NeoForge as <c>net.neoforged</c>, but PolyMC publishes it as
    /// <c>net.neoforged.neoforge</c>. Writing Prism's name into a PolyMC instance
    /// produces one that simply refuses to start.
    /// </summary>
    public static string? LoaderUid(string launcherId, string loader) => loader.ToLowerInvariant() switch
    {
        "neoforge" => launcherId == "polymc" ? "net.neoforged.neoforge" : "net.neoforged",
        "forge" => "net.minecraftforge",
        "fabric" => "net.fabricmc.fabric-loader",
        "quilt" => "org.quiltmc.quilt-loader",
        _ => null,
    };

    /// <summary>The settings file a launcher of the MultiMC family keeps in its data directory.</summary>
    public static string ConfigFileFor(string launcherId) =>
        Known.FirstOrDefault(x => x.Id == launcherId)?.ConfigFile ?? "prismlauncher.cfg";
    private static readonly Definition[] Known =
    {
        new("prism", "Prism Launcher", "PrismLauncher", new[] { "prismlauncher", "PrismLauncher.exe", "prismlauncher.exe" }, "org.prismlauncher.PrismLauncher") { ConfigFile = "prismlauncher.cfg" },
        new("modrinth", "Modrinth App", "com.modrinth.theseus", new[] { "modrinth-app", "Modrinth App.exe" }, "com.modrinth.ModrinthApp"),
        new("curseforge", "CurseForge", "CurseForge", new[] { "curseforge", "CurseForge.exe" }),
        new("official", "Minecraft Launcher", ".minecraft", new[] { "minecraft-launcher", "MinecraftLauncher.exe" }, "com.mojang.Minecraft"),
        new("sklauncher", "SKLauncher", "sklauncher", new[] { "sklauncher", "SKlauncher.exe" }),
        new("tlauncher", "TLauncher", ".tlauncher", new[] { "tlauncher", "TLauncher.exe" }),
        new("atlauncher", "ATLauncher", "ATLauncher", new[] { "ATLauncher", "atlauncher", "ATLauncher.exe" }, "com.atlauncher.ATLauncher"),
        new("multimc", "MultiMC", "multimc", new[] { "multimc", "MultiMC", "MultiMC.exe" }) { ConfigFile = "multimc.cfg" },
        new("gdlauncher", "GDLauncher", "gdlauncher_carbon", new[] { "gdlauncher", "GDLauncher", "GDLauncher.exe" }),
        new("polymc", "PolyMC", "PolyMC", new[] { "polymc", "PolyMC.exe" }, "org.polymc.PolyMC") { ConfigFile = "polymc.cfg" }
    };
    private readonly ExternalLauncherEnvironment _environment;
    public ExternalLauncherDiscovery(ExternalLauncherEnvironment environment) => _environment = environment;

    public IReadOnlyList<ExternalLauncherInfo> Detect()
    {
        var desktops = ReadDesktopEntries().ToArray();
        return Known.Select(def => Detect(def, desktops)).ToArray();
    }

    public ExternalLaunchCommand? ResolveCommand(string launcherId, string root) =>
        Detect(Known.First(x => x.Id == launcherId), ReadDesktopEntries().ToArray(), root).Command;
    public ExternalLaunchCommand? ResolveOfficialCommand(string root) => Detect(Known.First(x => x.Id == "official"), ReadDesktopEntries().ToArray(), root).Command;

    private ExternalLauncherInfo Detect(Definition def, IReadOnlyList<DesktopEntry> desktops, string? preferredRoot = null)
    {
        var roots = NativeRoots(def).Distinct().ToArray();
        var root = preferredRoot is null ? roots.FirstOrDefault(Directory.Exists) : Path.GetFullPath(preferredRoot);
        var executableRoots = ExecutableRoots(def).ToArray();
        // A manually selected portable installation takes priority over PATH.
        var command = preferredRoot is null ? null : FindExecutable(def.Executables, new[] { root! }, includePath: false);
        var explicitlySelectedExecutable = command is not null;
        command ??= FindExecutable(def.Executables, executableRoots.Concat(roots));
        if (preferredRoot is null && def.ConfigFile is not null && command is not null && IsPortable(def, Path.GetDirectoryName(command.Executable)!))
            root = Path.GetDirectoryName(command.Executable);
        string? iconName = null;
        foreach (var desktop in desktops)
        {
            var tokens = TokenizeDesktopExec(desktop.Exec);
            if (tokens.Count == 0) continue;
            var executableIndex = 0;
            var environment = new Dictionary<string, string>();
            if (Path.GetFileName(tokens[0]) == "env")
            {
                executableIndex = 1;
                while (executableIndex < tokens.Count && tokens[executableIndex].IndexOf('=') > 0)
                {
                    var assignment = tokens[executableIndex++];
                    var split = assignment.IndexOf('=');
                    environment[assignment[..split]] = assignment[(split + 1)..];
                }
            }
            if (executableIndex >= tokens.Count || !MatchesExecutable(def, tokens[executableIndex])) continue;
            var resolved = ResolveExecutable(tokens[executableIndex]);
            if (resolved is null) continue;
            iconName ??= desktop.Icon;
            string? desktopRoot = null;
            var arguments = new List<string>();
            for (var i = executableIndex + 1; i < tokens.Count; i++)
            {
                var argument = tokens[i];
                if (argument is "--dir" or "-d" or "--workDir")
                {
                    if (++i < tokens.Count && Path.IsPathFullyQualified(tokens[i])) desktopRoot = tokens[i];
                    continue;
                }
                if (argument.StartsWith("--dir=", StringComparison.Ordinal) || argument.StartsWith("--workDir=", StringComparison.Ordinal))
                { desktopRoot = argument[(argument.IndexOf('=') + 1)..]; continue; }
                // Do not inherit a desktop shortcut's selected instance/account/import.
                if (argument is "--launch" or "-l" or "--show" or "--import" or "-I" or "--profile" or "-a" or "--server" or "-s")
                { i++; continue; }
                if (argument.StartsWith("--launch=") || argument.StartsWith("--import=") || argument.StartsWith("--show=")) continue;
                arguments.Add(argument);
            }
            var portable = Path.GetDirectoryName(resolved)!;
            if (def.ConfigFile is not null && IsPortable(def, portable)) desktopRoot ??= portable;
            if (desktopRoot is not null && !Path.IsPathFullyQualified(desktopRoot)) desktopRoot = null;
            var matchesRoot = preferredRoot is not null && desktopRoot is not null && SamePath(root!, desktopRoot);
            var sameExecutable = command is not null && SamePath(command.Executable, resolved);
            if (!explicitlySelectedExecutable && (command is null || matchesRoot || preferredRoot is null && desktopRoot is not null || sameExecutable && desktopRoot is null))
            {
                command = new(resolved, arguments, resolved.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase) ? "AppImage" : "Native")
                { EnvironmentVariables = environment };
                if (preferredRoot is null) root = desktopRoot ?? root;
                if (matchesRoot) break;
            }
        }
        if (!_environment.IsWindows && (command is null || preferredRoot is not null))
        {
            var commonDirectories = new[] { Path.Combine(_environment.Home, "Applications"), Path.Combine(_environment.Home, "AppImages"), Path.Combine(_environment.Home, "Downloads") };
            var directories = preferredRoot is null ? commonDirectories : new[] { root! }.Concat(commonDirectories);
            foreach (var directory in directories)
            {
                if (command is not null && (preferredRoot is null || !SamePath(directory, root!))) continue;
                var appImage = FilesIn(directory).FirstOrDefault(file => file.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase) && MatchesExecutable(def, file) && IsExecutable(file));
                if (appImage is null) continue;
                command = new(appImage, Array.Empty<string>(), "AppImage");
                if (preferredRoot is null && IsPortable(def, directory)) root = directory;
                break;
            }
        }
        if (_environment.IsWindows && def.Id == "official")
        {
            var store = FindStoreLauncher();
            if (command is null || root is not null && File.Exists(Path.Combine(root, "launcher_profiles_microsoft_store.json"))) command = store ?? command;
        }
        if (!_environment.IsWindows && def.FlatpakId is { } appId)
        {
            var flatpak = ResolveExecutable("flatpak");
            var installations = _environment.FlatpakInstallations;
            var install = installations.FirstOrDefault(dir => Directory.Exists(Path.Combine(dir, "app", appId)));
            var desktopInstalled = desktops.Any(x => x.Id.Equals(appId, StringComparison.Ordinal) || x.FlatpakId == appId);
            iconName ??= desktops.FirstOrDefault(x => x.Id == appId || x.FlatpakId == appId)?.Icon ?? appId;
            var flatpakRoot = Path.Combine(_environment.Home, ".var", "app", appId, "data", def.DirectoryName);
            if (def.Id == "official") flatpakRoot = Path.Combine(_environment.Home, ".var", "app", appId, ".minecraft");
            var selectedFlatpak = preferredRoot is not null && SamePath(root!, flatpakRoot);
            if (selectedFlatpak) command = null; // Never open the native app on sandbox data.
            if (flatpak is not null && (install is not null || desktopInstalled)
                && (selectedFlatpak || preferredRoot is null && command is null))
            {
                var args = new List<string> { "run" };
                if (install is not null) args.Add(install == installations[0] ? "--user" : "--system");
                args.Add(appId);
                command = new(flatpak, args, "Flatpak"); root = flatpakRoot;
            }
        }
        if (command is not null && root is null) root = roots.FirstOrDefault();
        // A leftover data directory does not mean a launchable application exists.
        var detected = command is not null;
        // Prism gets a full instance; the official launcher gets a profile that
        // points at a game directory. Both are adapters DUSMP actually has.
        // Everything DUSMP has an adapter for: the MultiMC family gets a real
        // instance, the official launcher gets a profile pointed at a game
        // directory.
        var manageable = MmcFamily.Contains(def.Id) || def.Id is "official" or "modrinth" or "curseforge";
        return new(def.Id, def.Name, detected, root, _environment.IsWindows ? "Windows" : "Linux", manageable, null)
        {
            // An installed launcher already has its real logo on disk, which is
            // the user's own file and always beats our stand-in. Only three of
            // the ten marks we can legally bundle are the genuine article, so
            // this is what puts a real icon on most rows.
            IconUrl = InstalledIconUrl(iconName) ?? $"app://launcher/assets/launchers/{def.Id}.svg",
            InstallationKind = command?.Kind,
            ReasonKey = manageable ? null : "error.external.adapterUnavailable",
            Command = command
        };
    }

    /// <summary>Largest icon we will inline. Real app icons are a few KB.</summary>
    private const int MaxIconBytes = 192 * 1024;

    /// <summary>
    /// Freedesktop icon directories, in the order the spec searches them.
    /// Only the sizes a 44px avatar can use, largest first so an SVG wins.
    /// </summary>
    private static readonly string[] IconSubdirectories =
    {
        "hicolor/scalable/apps", "hicolor/512x512/apps", "hicolor/256x256/apps",
        "hicolor/128x128/apps", "hicolor/64x64/apps", "hicolor/48x48/apps", "",
    };

    /// <summary>
    /// The installed icon as a data URL, or null when there is none to show.
    ///
    /// Inlined rather than served: the asset scheme only exposes embedded
    /// resources, and widening it to hand out arbitrary paths from disk is a far
    /// bigger change than base64 for an icon that is a few KB. An &lt;img&gt;
    /// cannot execute a data URL's contents, so an SVG here stays inert.
    /// </summary>
    private string? InstalledIconUrl(string? iconName)
    {
        var file = ResolveIconFile(iconName);
        if (file is null) return null;

        try
        {
            var bytes = File.ReadAllBytes(file);
            if (bytes.Length == 0 || bytes.Length > MaxIconBytes) return null;
            var type = file.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? "image/svg+xml" : "image/png";
            return $"data:{type};base64,{Convert.ToBase64String(bytes)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves a desktop entry's <c>Icon=</c> value to a file the WebView can
    /// render. The value is either an absolute path or a bare theme name.
    /// </summary>
    private string? ResolveIconFile(string? iconName)
    {
        if (_environment.IsWindows || string.IsNullOrWhiteSpace(iconName)) return null;

        if (Path.IsPathFullyQualified(iconName))
            return IsRenderable(iconName) && File.Exists(iconName) ? iconName : null;

        // A theme name is a bare name. Anything with a separator is malformed
        // and must not be joined onto a search directory.
        if (iconName.AsSpan().IndexOfAny('/', '\\', '\0') >= 0) return null;

        foreach (var directory in _environment.IconDirectories)
            foreach (var subdirectory in IconSubdirectories)
                // .svgz is gzipped and .xpm is not a web format: neither renders.
                foreach (var extension in new[] { ".svg", ".png" })
                {
                    var candidate = Path.Combine(directory, subdirectory, iconName + extension);
                    try { if (File.Exists(candidate)) return candidate; }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }

        return null;
    }

    private static bool IsRenderable(string path) =>
        path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

    private IEnumerable<string> NativeRoots(Definition def)
    {
        if (_environment.IsWindows)
        {
            yield return Path.Combine(_environment.RoamingAppData, def.DirectoryName);
            if (def.Id == "official")
            {
                yield return Path.Combine(_environment.LocalAppData, "Packages", "Microsoft.4297127D64EC6_8wekyb3d8bbwe", "LocalCache", "Local", "minecraft");
                yield break;
            }
            yield return Path.Combine(_environment.LocalAppData, def.DirectoryName);
            yield return Path.Combine(_environment.LocalAppData, "Programs", def.DirectoryName);
            foreach (var directory in _environment.ProgramDirectories) yield return Path.Combine(directory, def.DirectoryName);
            if (def.Id == "prism") yield return Path.Combine(_environment.Home, "scoop", "persist", "prismlauncher");
            yield break;
        }
        if (def.DirectoryName.StartsWith('.')) yield return Path.Combine(_environment.Home, def.DirectoryName);
        else yield return Path.Combine(_environment.DataHome, def.DirectoryName);
        yield return Path.Combine(_environment.ConfigHome, def.DirectoryName);
        if (def.Id == "prism") yield return Path.Combine(_environment.Home, "PrismLauncher");
        if (def.Id == "modrinth") yield return Path.Combine(_environment.DataHome, "ModrinthApp");
        if (def.Id == "gdlauncher") yield return Path.Combine(_environment.ConfigHome, "gdlauncher");
        if (def.Id == "multimc") yield return Path.Combine(_environment.Home, "MultiMC");
        if (def.Id == "sklauncher") yield return Path.Combine(_environment.Home, ".minecraft", "sklauncher");
        if (def.Id == "atlauncher") yield return Path.Combine(_environment.Home, ".atl");
    }

    private ExternalLaunchCommand? FindExecutable(IEnumerable<string> executables, IEnumerable<string> roots, bool includePath = true)
    {
        foreach (var directory in roots.Concat(roots.Select(x => Path.Combine(x, "bin"))).Concat(includePath ? _environment.PathDirectories : Array.Empty<string>()))
            foreach (var executable in executables)
            {
                var candidate = Path.Combine(directory, executable);
                if (IsExecutable(candidate)) return new(candidate, Array.Empty<string>(), "Native");
            }
        return null;
    }
    private bool SamePath(string a, string b) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)).Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), _environment.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsPortable(Definition def, string directory) =>
        def.ConfigFile is { } config && File.Exists(Path.Combine(directory, config))
        || File.Exists(Path.Combine(directory, "portable.txt"));

    private static IEnumerable<string> FilesIn(string directory)
    {
        try { return Directory.Exists(directory) ? Directory.GetFiles(directory) : Array.Empty<string>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private IEnumerable<string> ExecutableRoots(Definition def)
    {
        if (!_environment.IsWindows) yield break;
        var name = def.Id == "official" ? "Minecraft Launcher" : def.DirectoryName;
        yield return Path.Combine(_environment.LocalAppData, "Programs", name);
        yield return Path.Combine(_environment.LocalAppData, name);
        foreach (var directory in _environment.ProgramDirectories) yield return Path.Combine(directory, name);
        if (def.Id == "prism") yield return Path.Combine(_environment.Home, "scoop", "apps", "prismlauncher", "current");
    }

    private ExternalLaunchCommand? FindStoreLauncher()
    {
        if (!OperatingSystem.IsWindows()) return null;
        const string family = "Microsoft.4297127D64EC6_8wekyb3d8bbwe";
        const string application = family + "!Minecraft";
        // Read the current user's registration, not protected WindowsApps binaries.
        try
        {
            using var registered = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\ActivatableClasses\Package");
            if (registered?.GetSubKeyNames().Any(name => name.StartsWith("Microsoft.4297127D64EC6_", StringComparison.OrdinalIgnoreCase)) != true)
                return null;
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            return File.Exists(explorer) ? new(explorer, new[] { @"shell:AppsFolder\" + application }, "Microsoft Store") : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    private string? ResolveExecutable(string name) => Path.IsPathFullyQualified(name)
        ? IsExecutable(name) ? name : null
        : _environment.PathDirectories.Select(x => Path.Combine(x, name)).FirstOrDefault(IsExecutable);
    private bool IsExecutable(string path)
    {
        if (!File.Exists(path)) return false;
        if (_environment.IsWindows || OperatingSystem.IsWindows()) return true;
        try { return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    private static bool MatchesExecutable(Definition def, string executable)
    {
        var name = Path.GetFileName(executable);
        if (def.Executables.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
        return name.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase)
            && (name.StartsWith(def.DirectoryName + "-", StringComparison.OrdinalIgnoreCase)
                || name.Equals(def.DirectoryName + ".AppImage", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record DesktopEntry(string Id, string Exec, string? FlatpakId, string? Icon);
    private IEnumerable<DesktopEntry> ReadDesktopEntries()
    {
        if (_environment.IsWindows) yield break;
        foreach (var directory in _environment.ApplicationDirectories.Distinct())
        {
            string[] files;
            try { files = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.desktop") : Array.Empty<string>(); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var file in files)
            {
                DesktopEntry? entry = null;
                try
                {
                    if (new FileInfo(file).Length > 64 * 1024) continue;
                    var fields = new Dictionary<string, string>(); var inEntry = false;
                    foreach (var line in File.ReadLines(file, Encoding.UTF8))
                    {
                        if (line.StartsWith('[')) { inEntry = line == "[Desktop Entry]"; continue; }
                        var separator = line.IndexOf('=');
                        if (inEntry && separator > 0) fields.TryAdd(line[..separator], line[(separator + 1)..]);
                    }
                    if (fields.GetValueOrDefault("Hidden") != "true" && fields.TryGetValue("Exec", out var exec))
                        entry = new(Path.GetFileNameWithoutExtension(file), exec, fields.GetValueOrDefault("X-Flatpak"),
                            fields.GetValueOrDefault("Icon"));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                if (entry is not null) yield return entry;
            }
        }
    }

    // Desktop Exec is not a shell command. Parse quotes and escapes, never evaluate a shell.
    internal static IReadOnlyList<string> TokenizeDesktopExec(string value)
    {
        var result = new List<string>(); var token = new StringBuilder(); var quoted = false; var escaped = false;
        foreach (var c in value)
        {
            if (escaped) { token.Append(c); escaped = false; }
            else if (c == '\\') escaped = true;
            else if (c == '"') quoted = !quoted;
            else if (char.IsWhiteSpace(c) && !quoted) { if (token.Length > 0) { result.Add(token.ToString()); token.Clear(); } }
            else token.Append(c);
        }
        if (quoted || escaped) return Array.Empty<string>();
        if (token.Length > 0) result.Add(token.ToString());
        return result.Where(x => !x.StartsWith('%')).ToArray();
    }
}
