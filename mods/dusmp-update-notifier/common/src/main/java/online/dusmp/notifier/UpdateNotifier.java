package online.dusmp.notifier;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import java.awt.EventQueue;
import java.net.URI;
import java.net.URLEncoder;
import java.net.http.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.time.Duration;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.atomic.AtomicBoolean;
import javax.swing.JOptionPane;
import net.minecraft.client.Minecraft;
import net.neoforged.api.distmarker.Dist;
import net.neoforged.fml.common.Mod;
import net.neoforged.fml.loading.FMLPaths;
import net.neoforged.neoforge.client.event.ClientTickEvent;
import net.neoforged.neoforge.common.NeoForge;

@Mod(value="dusmp_update_notifier", dist=Dist.CLIENT)
public final class UpdateNotifier {
    private final AtomicBoolean started = new AtomicBoolean();
    public UpdateNotifier() { NeoForge.EVENT_BUS.addListener(this::tick); }
    private void tick(ClientTickEvent.Post event) {
        if (Minecraft.getInstance().screen == null || !started.compareAndSet(false, true)) return;
        CompletableFuture.runAsync(this::check);
    }
    private void check() {
        try {
            Path path = FMLPaths.GAMEDIR.get().resolve("config/dusmp-update-notifier.json");
            if (!Files.isRegularFile(path)) return;
            JsonObject config = JsonParser.parseString(Files.readString(path)).getAsJsonObject();
            String edition = config.get("edition").getAsString(), installed = config.get("version").getAsString();
            if (edition.isBlank() || installed.isBlank()) return;
            URI uri = URI.create("https://api.dusmp.online/modpack/latest?edition=" + URLEncoder.encode(edition, StandardCharsets.UTF_8));
            HttpClient client = HttpClient.newBuilder().connectTimeout(Duration.ofSeconds(8)).build();
            HttpRequest request = HttpRequest.newBuilder(uri).timeout(Duration.ofSeconds(12)).GET().build();
            HttpResponse<String> response = client.send(request, HttpResponse.BodyHandlers.ofString());
            if (response.statusCode() != 200 || response.body().length() > 65536) throw new IllegalStateException("Update service unavailable");
            String latest = JsonParser.parseString(response.body()).getAsJsonObject().get("version").getAsString();
            if (latest.isBlank() || latest.equals(installed)) return;
            EventQueue.invokeLater(() -> {
                int choice = JOptionPane.showOptionDialog(null, "A DUSMP modpack update is available (" + latest + ").\nChoose OK to open the launcher and close Minecraft.", "DUSMP update", JOptionPane.DEFAULT_OPTION, JOptionPane.INFORMATION_MESSAGE, null, new String[]{"OK", "Later"}, "Later");
                if (choice == 0) {
                    openLauncher();
                    Minecraft.getInstance().execute(() -> Minecraft.getInstance().stop());
                }
            });
        } catch (Exception ex) {
            EventQueue.invokeLater(() -> JOptionPane.showMessageDialog(null, "DUSMP could not check for modpack updates. You can continue playing.", "DUSMP update check", JOptionPane.WARNING_MESSAGE));
        }
    }
    private static void openLauncher() {
        try {
            String local = System.getenv("LOCALAPPDATA");
            if (local == null) throw new IllegalStateException("Launcher location unavailable");
            Path executable = Path.of(local, "Programs", "DUSMP Launcher", "DusmpLauncher.exe");
            if (!Files.isRegularFile(executable)) throw new IllegalStateException("Launcher is not installed");
            new ProcessBuilder(executable.toString()).start();
        } catch (Exception ex) {
            JOptionPane.showMessageDialog(null, "Open DUSMP Launcher manually after quitting Minecraft.", "DUSMP update", JOptionPane.INFORMATION_MESSAGE);
        }
    }
}
