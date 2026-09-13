using System.Text.Json;

namespace ViennaBar;

// Theme engine (F2.4) — tokens desde %APPDATA%\ViennaBar\skin.json con
// hot-reload (FileSystemWatcher). Defaults = Vienna Celeste.
internal sealed class Skin : IDisposable
{
    // ---- tokens activos (int ARGB) — instancia global para los call sites ----
    public static Skin Current { get; private set; } = new();

    // helpers estáticos (los call sites usan Skin.Bg etc.)
    public static int Bg => Config.Current.BgColor != null ? ParseHex(Config.Current.BgColor) : Current._bg;
    public static int Text => Current._text;
    public static int Muted => Current._muted;
    public static int Divider => Current._divider;
    public static int Sel => Current._sel;
    public static int Btn => Current._btn;
    public static int White => Current._white;
    public static int Search => Current._search;
    public static int SheenTop => Current._sheenTop;
    public static int Folder => Current._folder;
    public static int Branch => Current._branch;

    // overrides de la rueda cromática (config.json) sobre tokens del skin
    public static int Accent => Config.Current.AccentColor != null ? ParseHex(Config.Current.AccentColor) : Current._folder;
    public static int BarFill => Config.Current.BarFillColor != null ? ParseHex(Config.Current.BarFillColor) : Current._btn;
    public static int StartBtn => Config.Current.StartBtnColor != null ? ParseHex(Config.Current.StartBtnColor) : Current._btn;

    internal static int ParseHex(string hex)
    {
        try
        {
            if (hex.StartsWith('#') && hex.Length == 7)
            {
                byte r = Convert.ToByte(hex.Substring(1, 2), 16);
                byte g = Convert.ToByte(hex.Substring(3, 2), 16);
                byte b = Convert.ToByte(hex.Substring(5, 2), 16);
                return unchecked((int)((uint)0xFF << 24 | (uint)r << 16 | (uint)g << 8 | b));
            }
        }
        catch { }
        return unchecked((int)0xFFCCCCCC);
    }

    private int _bg = unchecked((int)0xFFC6D7E6);
    private int _text = unchecked((int)0xFF16324A);
    private int _muted = unchecked((int)0xFF3D6A8A);
    private int _divider = unchecked((int)0xFF2E7CC4);
    private int _sel = unchecked((int)0xFF8ACBEF);
    private int _btn = unchecked((int)0xFF2E7CC4);
    private int _white = unchecked((int)0xFFFFFFFF);
    private int _search = unchecked((int)0xFFBFD3E4);
    private int _sheenTop = unchecked((int)0xFFB4CBDE);
    private int _folder = unchecked((int)0xFFD9A520);  // amarillo mostaza
    private int _branch = unchecked((int)0xFF5A7A96);  // linea de rama (gris azul p/ default claro)

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
          "sheenTop": "#1C303F",
          "folder": "#D9A520",
          "branch": "#6A8AA8"
        }
        """;

    internal const string ViennaDuskJson = """
        {
          "background": "#2A1D1A",
          "text": "#F8EAE2",
          "muted": "#C09A88",
          "divider": "#8A4A3A",
          "selection": "#6E3526",
          "button": "#C0563A",
          "white": "#FFFFFF",
          "search": "#38241E",
          "sheenTop": "#4A2C24",
          "folder": "#E0A820",
          "branch": "#B89A78"
        }
        """;

    internal static void SeedBuiltinSkins(string appDir)
    {
        try { SeedOne(appDir, "ViennaNight", ViennaNightJson); } catch { }
        try { SeedOne(appDir, "ViennaDusk", ViennaDuskJson); } catch { }
    }

    private static void SeedOne(string appDir, string name, string json)
    {
        string dir = Path.Combine(appDir, "skins", name);
        Directory.CreateDirectory(dir);
        string target = Path.Combine(dir, "skin.json");
        if (!File.Exists(target))
            File.WriteAllText(target, json);
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
            _folder = ReadColor(root, "folder", _folder);
            _branch = ReadColor(root, "branch", _branch);
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
        ("folder", _folder),
        ("branch", _branch),
    };

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcherLogo?.Dispose();
    }
}
