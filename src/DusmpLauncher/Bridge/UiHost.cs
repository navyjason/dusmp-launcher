using DusmpLauncher.Core;
using DusmpLauncher.Localization;
using Photino.NET;
using System.Runtime.InteropServices;

namespace DusmpLauncher.Bridge;

/// <summary>
/// Owns the one window. Everything visual happens inside the WebView; this class
/// only creates it, wires the two message primitives Photino gives us, and keeps
/// the bridge alive for the process lifetime.
/// </summary>
public sealed class UiHost
{
    private readonly ILauncherCore _core;
    private readonly ILanguageManager _languages;
    private readonly EmbeddedAssets _assets = new();

    private PhotinoWindow? _window;
    private WebBridge? _bridge;
    private PhotinoWindow? _skinWindow;

    public UiHost(ILauncherCore core, ILanguageManager languages)
    {
        _core = core;
        _languages = languages;
    }

    public WebBridge Bridge =>
        _bridge ?? throw new InvalidOperationException("UiHost.Run has not been called yet.");

    public void Close() => _window?.Invoke(() => _window.Close());

    public event Action? UiInitialized;

    /// <summary>
    /// Blocks until the window closes. Photino must own the main thread, so the
    /// caller starts launcher work from <paramref name="onReady"/> instead of
    /// before this call.
    /// </summary>
    /// <summary>
    /// Filled in once the window exists, so anything needing the toolkit thread
    /// (the embedded sign-in window) can reach it.
    /// </summary>
    public Core.UiThreadInvoker UiThread { get; } = new();

    public void Run(Action<WebBridge> onReady)
    {
        _window = new PhotinoWindow()
            .SetTitle(_languages.Translate("app.windowTitle"))
            .SetUseOsDefaultSize(false)
            .SetSize(1000, 640)
            .SetMinSize(640, 480)
            .SetResizable(true)
            .SetChromeless(false)
            .SetContextMenuEnabled(false)
            .SetDevToolsEnabled(IsDevToolsWanted())
            .SetGrantBrowserPermissions(false)
            .SetMediaAutoplayEnabled(false)
            .Center();

        var iconPath = TryExtractWindowIcon();
        if (iconPath is not null)
            _window.SetIconFile(iconPath);

        // Must be registered before the first navigation, or the entry point 404s.
        _window.RegisterCustomSchemeHandler(EmbeddedAssets.Scheme, _assets.Resolve);

        _bridge = new WebBridge(
            _core,
            _languages,
            send: message => _window!.SendWebMessage(message),
            minimize: () => _window!.Invoke(() => _window!.SetMinimized(true)),
            close: () => _window!.Invoke(() => _window!.Close()),
            chooseFolder: async () =>
            {
                var folders = await _window!.ShowOpenFolderAsync(_languages.Translate("app.chooseDataFolder"), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), true).ConfigureAwait(false);
                return folders?.FirstOrDefault();
            },
            chooseExternalProfile: async id =>
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var start = id == "modrinth"
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ModrinthApp", "profiles")
                    : Path.Combine(home, "curseforge", "minecraft", "Instances");
                if (!Directory.Exists(start)) start = home;
                var folders = await _window!.ShowOpenFolderAsync("Choose an existing DUSMP profile", start, true).ConfigureAwait(false);
                return folders?.FirstOrDefault();
            },
            choosePng: async () =>
            {
                // Filtered to PNG because a skin is always one, and the API's
                // refusal of anything else is not worth putting in front of a
                // player who only picked the wrong file.
                var files = await _window!.ShowOpenFileAsync(
                    _languages.Translate("profile.choose"),
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    multiSelect: false,
                    new[] { ("PNG", new[] { "*.png" }) }).ConfigureAwait(false);
                return files?.FirstOrDefault();
            },
            openSkinWindow: OpenSkinWindow,
            chooseMods: () => _window!.ShowOpenFileAsync(
                _languages.Translate("installedMods.upload"), GetDownloadsDirectory(),
                multiSelect: true, new[] { ("Minecraft mods (.jar)", new[] { "*.jar" }) }));

        _window.RegisterWebMessageReceivedHandler((_, message) => _bridge!.HandleMessage(message));
        _bridge.UiInitialized += () => UiInitialized?.Invoke();

        // Fires once the WebView exists, which is the earliest point at which
        // SendWebMessage will not be dropped.
        _window.RegisterWindowCreatedHandler((_, _) => onReady(_bridge!));

        // Photino owns the GTK/Win32 loop; this is how other code gets onto it.
        UiThread.Bind(action => _window!.Invoke(action));

        _window.Load(EmbeddedAssets.EntryPoint);
        _window.WaitForClose();
    }

    /// <summary>
    /// Opens the skin editor in a window of its own, the way Prism opens its
    /// account editor.
    ///
    /// Photino requires a child window to be created on the thread that owns the
    /// loop, and <c>WaitForClose</c> is what actually shows it, so both happen
    /// inside <c>Invoke</c>. The child gets its own bridge over the same core:
    /// every window has its own <c>window.external</c>, so one page cannot
    /// receive another's replies.
    /// </summary>
    private void OpenSkinWindow()
    {
        if (_window is null) return;

        _window.Invoke(() =>
        {
            // Bringing the existing one forward beats stacking duplicates.
            if (_skinWindow is not null)
            {
                try { _skinWindow.SetTopMost(true).SetTopMost(false); return; }
                catch (Exception) { _skinWindow = null; }
            }

            try
            {
                var child = new PhotinoWindow(_window)
                    .SetTitle(_languages.Translate("profile.title"))
                    .SetUseOsDefaultSize(false)
                    .SetSize(920, 620)
                    .SetResizable(true)
                    // Same rules as the main window: without these the child
                    // offers the WebView's own menu, with Inspect Element on it.
                    .SetContextMenuEnabled(false)
                    .SetDevToolsEnabled(IsDevToolsWanted())
                    .Center();

                var iconPath = TryExtractWindowIcon();
                if (iconPath is not null) child.SetIconFile(iconPath);

                // Without its own handler the child cannot resolve app:// at all.
                child.RegisterCustomSchemeHandler(EmbeddedAssets.Scheme, _assets.Resolve);

                var childBridge = new WebBridge(
                    _core,
                    _languages,
                    send: message => child.SendWebMessage(message),
                    minimize: () => child.SetMinimized(true),
                    close: () => child.Close(),
                    choosePng: async () =>
                    {
                        var files = await child.ShowOpenFileAsync(
                            _languages.Translate("profile.choose"),
                            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            multiSelect: false,
                            new[] { ("PNG", new[] { "*.png" }) }).ConfigureAwait(false);
                        return files?.FirstOrDefault();
                    });

                child.RegisterWebMessageReceivedHandler((_, message) => childBridge.HandleMessage(message));
                child.RegisterWindowClosingHandler((_, _) => { _skinWindow = null; return false; });

                _skinWindow = child;
                child.Load(new Uri(EmbeddedAssets.EntryPoint, "profile.html"));
                child.WaitForClose();
            }
            catch (Exception ex)
            {
                // A window that will not open must not take the launcher with it.
                _skinWindow = null;
                Console.Error.WriteLine($"could not open the skin window: {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Photino wants an icon as a file path, but ours is an embedded resource, so
    /// it gets written to the cache directory once and reused.
    /// </summary>
    private string? TryExtractWindowIcon()
    {
        try
        {
            var paths = LauncherPaths.Resolve();
            Directory.CreateDirectory(paths.Cache);
            var asset = OperatingSystem.IsWindows() ? "app.ico" : "icon.png";
            var target = Path.Combine(paths.Cache, $"window-icon-{LauncherCore.LauncherVersion}-{asset}");

            if (!File.Exists(target))
            {
                using var source = _assets.Resolve(this, EmbeddedAssets.Scheme, new Uri(EmbeddedAssets.EntryPoint, "assets/" + asset).AbsoluteUri, out _);
                if (source is null)
                    return null;

                using var destination = File.Create(target);
                source.CopyTo(destination);
            }

            return target;
        }
        catch (IOException)
        {
            // A missing icon is cosmetic; never block startup over it.
            return null;
        }
    }

    private static bool IsDevToolsWanted() =>
        Environment.GetEnvironmentVariable("DUSMP_DEVTOOLS") is "1" or "true";

    private static string GetDownloadsDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            var downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            if (SHGetKnownFolderPath(ref downloads, 0, IntPtr.Zero, out var pointer) == 0)
            {
                try { if (Marshal.PtrToStringUni(pointer) is { Length: > 0 } path) return path; }
                finally { Marshal.FreeCoTaskMem(pointer); }
            }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, IntPtr token, out IntPtr path);
}
