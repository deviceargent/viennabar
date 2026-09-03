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

    private int _bg = unchecked((int)0xFFE8F0F7);
    private int _text = unchecked((int)0xFF1A3A50);
    private int _muted = unchecked((int)0xFF4A6A80);
    private int _divider = unchecked((int)0xFF5A9EC4);
    private int _sel = unchecked((int)0xFF9ED4EE);
    private int _btn = unchecked((int)0xFF3A86C4);
    private int _white = unchecked((int)0xFFFFFFFF);
    private int _search = unchecked((int)0xFFE6F5FC);
    private int _sheenTop = unchecked((int)0xFFC8E0EE);

    private static readonly string SkinDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ViennaBar");
    private static readonly string SkinPath = Path.Combine(SkinDir, "skin.json");

    private FileSystemWatcher? _watcher;

    public static Skin LoadDefault()
    {
        Current = LoadFromPath(SkinPath);
        Current.Watch();
        return Current;
    }

    // parseo sin watcher: testeable headless (los tests usan un path temporal
    // y no tocan el skin.json real del usuario)
    internal static Skin LoadFromPath(string path)
    {
        var skin = new Skin();
        skin.LoadFromDisk(path);
        return skin;
    }

    private void Init()
    {
        LoadFromDisk(SkinPath);
        Watch();
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
        try
        {
            Directory.CreateDirectory(SkinDir);
            _watcher = new FileSystemWatcher(SkinDir, "skin.json")
            {
                NotifyFilter = NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) =>
            {
                Thread.Sleep(150);            // el editor escribe en varios pasos
                LoadFromDisk(SkinPath);
                // refresco de brushes + repaint desde el hilo UI
                App.Instance?.ReloadSkin(this);
            };
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

    public void Dispose() => _watcher?.Dispose();
}
