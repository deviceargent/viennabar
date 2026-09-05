using System.Text.Json;

namespace ViennaBar;

// Theme engine (F2.4) — tokens desde %APPDATA%\ViennaBar\skin.json con
// hot-reload (FileSystemWatcher). Defaults = Vienna Celeste.
internal sealed class Skin : IDisposable
{
    // ---- tokens activos (int ARGB) — instancia global para los call sites ----
    public static Skin Current { get; private set; } = new();

    // helpers estáticos (los call sites usan Skin.Bg etc.)
    public static int Bg => Current._bg;
    public static int Text => Current._text;
    public static int Muted => Current._muted;
    public static int Divider => Current._divider;
    public static int Sel => Current._sel;
    public static int Btn => Current._btn;
    public static int White => Current._white;
    public static int Search => Current._search;
    public static int SheenTop => Current._sheenTop;

    private int _bg = unchecked((int)0xFFDCE9F5);
    private int _text = unchecked((int)0xFF16324A);
    private int _muted = unchecked((int)0xFF3D6A8A);
    private int _divider = unchecked((int)0xFF2E7CC4);
    private int _sel = unchecked((int)0xFF8ACBEF);
    private int _btn = unchecked((int)0xFF2E7CC4);
    private int _white = unchecked((int)0xFFFFFFFF);
    private int _search = unchecked((int)0xFFD4E9F7);
    private int _sheenTop = unchecked((int)0xFFB8D8EC);

    private static readonly string SkinDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ViennaBar");

    private FileSystemWatcher? _watcher;
    private FileSystemWatcher? _watcherLogo;
    private string _path = "";

    public static Skin LoadDefault()
    {
        SeedBuiltinSkins(SkinDir);
        var old = Current;
        Current = LoadFromPath(ResolveSkinPath(Config.Current.Skin, SkinDir));
        Current.Watch();
        try { old.Dispose(); } catch { }   // watcher del skin anterior (si hubo switch)
        return Current;
    }

    // skins built-in empaquetados (sin copiar nada a mano). Idempotente y
    // testeable: solo escribe si falta.
    internal const string ViennaNightJson = """
        {
          "background": "#16202C",
          "text": "#E8F0F7",
          "muted": "#8AA0B4",
          "divider": "#2A4A64",
          "selection": "#1E4A6B",
          "button": "#2E7CC4",
          "white": "#FFFFFF",
          "search": "#1E2C3A",
          "sheenTop": "#1C303F"
        }
        """;

    internal static void SeedBuiltinSkins(string appDir)
    {
        try
        {
            string dir = Path.Combine(appDir, "skins", "ViennaNight");
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, "skin.json");
            if (!File.Exists(target))
                File.WriteAllText(target, ViennaNightJson);
        }
        catch { }
    }

    // logo del boton Inicio: skins/<activo>/start.png (o legacy). null = placeholder.
    internal static string? LogoPathFor(string skinJsonPath)
    {
        try
        {
            string? dir = Path.GetDirectoryName(skinJsonPath);
            if (string.IsNullOrEmpty(dir)) return null;
            string candidate = Path.Combine(dir, "start.png");
            return File.Exists(candidate) ? candidate : null;
        }
        catch { return null; }
    }

    internal static string? ActiveLogoPath()
    {
        try { return LogoPathFor(Current._path); }
        catch { return null; }
    }

    // packaging: skins/<nombre>/skin.json, fallback al legacy skin.json.
    // Si no existe ninguno, devuelve la ruta empaquetada (crear el archivo
    // despues dispara el hot-reload). Testeable headless.
    internal static string ResolveSkinPath(string skinName, string appDir)
    {
        string packaged = Path.Combine(appDir, "skins", skinName, "skin.json");
        if (File.Exists(packaged)) return packaged;
        string legacy = Path.Combine(appDir, "skin.json");
        if (File.Exists(legacy)) return legacy;
        return packaged;
    }

    // parseo sin watcher: testeable headless (los tests usan un path temporal
    // y no tocan el skin.json real del usuario)
    internal static Skin LoadFromPath(string path)
    {
        var skin = new Skin { _path = path };
        skin.LoadFromDisk(path);
        return skin;
    }

    private void LoadFromDisk(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            _bg = ReadColor(root, "background", _bg);
            _text = ReadColor(root, "text", _text);
            _muted = ReadColor(root, "muted", _muted);
            _divider = ReadColor(root, "divider", _divider);
            _sel = ReadColor(root, "selection", _sel);
            _btn = ReadColor(root, "button", _btn);
            _white = ReadColor(root, "white", _white);
            _search = ReadColor(root, "search", _search);
            _sheenTop = ReadColor(root, "sheenTop", _sheenTop);
        }
        catch { /* skin.json inválido: defaults quedan */ }
    }

    private static int ReadColor(JsonElement root, string name, int fallback)
    {
        if (root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString()!;
            if (s.StartsWith('#') && s.Length == 7)
            {
                byte r = Convert.ToByte(s.Substring(1, 2), 16);
                byte g = Convert.ToByte(s.Substring(3, 2), 16);
                byte b = Convert.ToByte(s.Substring(5, 2), 16);
                return unchecked((int)((uint)0xFF << 24 | (uint)r << 16 | (uint)g << 8 | b));
            }
        }
        return fallback;
    }

    private void Watch()
    {
        string? dir = null;
        try { dir = Path.GetDirectoryName(_path); } catch { }
        if (string.IsNullOrEmpty(dir)) dir = SkinDir;
        try
        {
            Directory.CreateDirectory(dir);
            _watcher = new FileSystemWatcher(dir, "skin.json")
            {
                NotifyFilter = NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) =>
            {
                Thread.Sleep(150);            // el editor escribe en varios pasos
                LoadFromDisk(_path);
                // refresco de brushes + repaint desde el hilo UI
                App.Instance?.ReloadSkin(this);
            };
            _watcherLogo = new FileSystemWatcher(dir, "start.png")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _watcherLogo.Changed += (_, _) => App.Instance?.ReloadSkin(this);
            _watcherLogo.Created += (_, _) => App.Instance?.ReloadSkin(this);
            _watcherLogo.Deleted += (_, _) => App.Instance?.ReloadSkin(this);
            _watcherLogo.Renamed += (_, _) => App.Instance?.ReloadSkin(this);
        }
        catch { /* sin watcher: hot-reload off, skin estático */ }
    }

    // specs de brush derivadas de los tokens actuales
    public (string, int)[] CacheBrushSpec => new[]
    {
        ("bg", _bg),
        ("sheenTop", _sheenTop),
        ("divider", _divider),
        ("text", _text),
        ("muted", _muted),
        ("sel", _sel),
        ("btn", _btn),
        ("white", _white),
        ("search", _search),
    };

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcherLogo?.Dispose();
    }
}
