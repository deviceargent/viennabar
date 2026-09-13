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
    public bool StayOpen = false;   // dev: suprime el auto-hide (fotos/tests visuales)
    public bool[] Pins = new bool[8]; // índices: 0=Desktop,1=Docs,2=Downloads,3=Images,4=Music,5=Videos,6=null,7=null; true=visible+clickeable en drawer colapsado
    public bool GlassOverlayEnabled = true; // alternar overlays semitransparentes (false para desactivar)

    // overrides de color (rueda cromática). null = usar el token del skin activo.
    public string? AccentColor;   // listón izquierdo
    public string? BarFillColor;  // barras de CPU/RAM/disco
    public string? StartBtnColor; // botón de inicio
    public string? BgColor;       // fondo del tema

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
            if (r.TryGetProperty("stayOpen", out var so) && so.ValueKind == JsonValueKind.True)
                c.StayOpen = true;
            if (r.TryGetProperty("glassOverlay", out var go) && go.ValueKind == JsonValueKind.True)
                c.GlassOverlayEnabled = true;
            else if (r.TryGetProperty("glassOverlay", out var gf) && gf.ValueKind == JsonValueKind.False)
                c.GlassOverlayEnabled = false;
            c.AccentColor = ReadHex(r, "accent");
            c.BarFillColor = ReadHex(r, "barfill");
            c.StartBtnColor = ReadHex(r, "startbtn");
            c.BgColor = ReadHex(r, "bgoverride");
            if (r.TryGetProperty("pins", out var p_arr) && p_arr.ValueKind == JsonValueKind.Array)
            {
                var arr = p_arr.EnumerateArray();
                int i = 0;
                foreach (var el in arr)
                {
                    if (i >= 8) break;
                    if (el.ValueKind == JsonValueKind.String)
                    {
                        c.Pins[i] = !string.IsNullOrWhiteSpace(el.GetString());
                    }
                    else if (el.ValueKind == JsonValueKind.True)
                    {
                        c.Pins[i] = true;
                    }
                    else if (el.ValueKind == JsonValueKind.False)
                    {
                        c.Pins[i] = false;
                    }
                    i++;
                }
            }
        }
        catch { /* config roto: defaults quedan */ }
        return c;
    }

    private static string? ReadHex(System.Text.Json.JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var el) && el.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var s = el.GetString();
            if (!string.IsNullOrEmpty(s) && s.StartsWith('#') && s.Length == 7) return s;
        }
        return null;
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
