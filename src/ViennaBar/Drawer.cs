using Windows.Win32.Foundation;
using Windows.Win32.Graphics.DirectWrite;

namespace ViennaBar;

// StartDrawer — tercio inferior. Colapsado: botón Inicio + pins.
// Expandido: all-programs (desde AppCatalog cache) + search box + power.
internal sealed class Drawer
{
    private const float StartBtnH = 36f;
    private const float SearchH = 26f;
    private const float RowH = 18f;

    private readonly AppCatalog _catalog = new();
    private HWND _hwnd;
    private string _search = "";
    private int _selIdx = -1;

    public static IDWriteTextFormat F = null!;
    public static IDWriteTextFormat FBig = null!;

    public void Attach(HWND hwnd)
    {
        _hwnd = hwnd;
        _catalog.Attach();
    }

    public void InitText(Renderer r)
    {
        F = r.Text9;
        FBig = r.Text11b;
    }

    // click dentro del área del drawer (coords locales al drawer)
    public void OnClick(int x, int y, int drawerH)
    {
        if (y < SearchH) return;

        var results = CurrentResults();
        int idx = (int)((y - SearchH) / RowH);
        if (idx >= 0 && idx < results.Count)
        {
            Launch(results[idx]);
        }
    }

    private List<AppCatalog.AppEntry> CurrentResults()
    {
        // cacheada: solo recompute al cambiar search o refrescar catálogo
        if (_resultsCache is null || _cacheSearch != _search)
        {
            _resultsCache = (string.IsNullOrEmpty(_search)
                ? _catalog.Apps
                : _catalog.Search(_search)).Take(400).ToList();
            _cacheSearch = _search;
        }
        return _resultsCache;
    }

    private List<AppCatalog.AppEntry>? _resultsCache;
    private string? _cacheSearch;

    private void Launch(AppCatalog.AppEntry entry)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(entry.ParsingName) { UseShellExecute = true };
            _ = System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[drawer] launch error: {ex.Message}");
        }
    }

    public void Render(RenderCtx ctx, int x, int y, int w, int h, bool open)
    {
        // botón Inicio: anclado abajo
        float by = y + h - StartBtnH - 4;
        ctx.FillRect(Skin.Btn, 4, by, w - 8, StartBtnH);
        ctx.Text("Inicio", FBig, Skin.White, w / 2f - 24, by + (StartBtnH - 18) / 2);

        if (!open) return;

        float dy = y + 4;
        ctx.FillRect(Skin.Search, 4, dy, w - 8, SearchH);
        ctx.Line(Skin.Divider, 4, dy, w - 4, dy);
        ctx.Line(Skin.Divider, 4, dy + SearchH, w - 4, dy + SearchH);
        ctx.Line(Skin.Divider, 4, dy, 4, dy + SearchH);
        ctx.Line(Skin.Divider, w - 4, dy, w - 4, dy + SearchH);
        ctx.Text(string.IsNullOrEmpty(_search) ? "Buscar..." : _search, F, Skin.Muted, 10, dy + 4);
        dy += SearchH + 2;

        foreach (var app in CurrentResults())
        {
            if (dy + RowH > y + h - StartBtnH - 8) break;
            ctx.Text(app.Name, F, Skin.Text, 8, dy, w - 20);
            dy += RowH;
        }
    }

    public void Dispose() => _catalog.Dispose();
}
