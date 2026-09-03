using System.Text.Json;

namespace ViennaBar;

// Settings (F2) — %APPDATA%\ViennaBar\config.json con hot-reload.
// LoadFromPath sin watcher = testeable headless. Clamps defensivos:
// un config roto nunca rompe la geometría de la ventana.
internal sealed class Config : IDisposable
{
    public static Config Current { get; private set; } = new();

    public int Width = 280;
    public uint RevealMs = 80;
    public uint HideMs = 400;
    public string Skin = "default";

    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ViennaBar");
    private static readonly string ConfigPath = Path.Combine(Dir, "config.json");

    private FileSystemWatcher? _watcher;

    public static Config LoadDefault()
    {
        Current = LoadFromPath(ConfigPath);
        Current.Watch();
        return Current;
    }

    internal static Config LoadFromPath(string path)
    {
        var c = new Config();
        try
        {
            if (!File.Exists(path)) return c;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            if (r.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number)
                c.Width = Math.Clamp(w.GetInt32(), 200, 600);
            if (r.TryGetProperty("revealMs", out var rm) && rm.ValueKind == JsonValueKind.Number)
                c.RevealMs = (uint)Math.Clamp(rm.GetInt32(), 0, 2000);
            if (r.TryGetProperty("hideMs", out var hm) && hm.ValueKind == JsonValueKind.Number)
                c.HideMs = (uint)Math.Clamp(hm.GetInt32(), 0, 2000);
            if (r.TryGetProperty("skin", out var s) && s.ValueKind == JsonValueKind.String)
            {
                var name = Path.GetFileName(s.GetString() ?? "");
                if (name.Length > 0) c.Skin = name;
            }
        }
        catch { /* config roto: defaults quedan */ }
        return c;
    }

    private void Watch()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            _watcher = new FileSystemWatcher(Dir, "config.json")
            {
                NotifyFilter = NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) =>
            {
                Thread.Sleep(150);            // el editor escribe en varios pasos
                Current = LoadFromPath(ConfigPath);
                // aplicar geometría desde el hilo UI
                App.Instance?.RequestConfigApply();
            };
        }
        catch { /* sin watcher: config estático */ }
    }

    public void Dispose() => _watcher?.Dispose();
}
