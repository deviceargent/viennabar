using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using static Windows.Win32.PInvoke;
using Shell = ViennaBar.ShellNative.ShellNative;

namespace ViennaBar;

// StartDrawer â€” tercio inferior. Colapsado: botÃ³n Inicio + pins.
// Expandido: all-programs (desde AppCatalog cache) + search box + power.
internal sealed class Drawer
{
    private const float StartBtnH = 36f;
    private const float SearchH = 26f;
    private const float RowH = 18f;

    private readonly AppCatalog _catalog = new();
    private HWND _hwnd;
    private string _search = "";
    private int _selIdx;                       // selecciÃ³n de teclado
    private int _topRow;                       // scroll virtual (primera fila visible)
    private float _drawerAreaH = 400f;          // F2: mÃ©trica real del layout

    public static TextFormatHandle F;
    public static TextFormatHandle FBig;

    public void Attach(HWND hwnd)
    {
        _hwnd = hwnd;
        _catalog.Attach();
    }

    public void InitText(Renderer r)
    {
        F = r.Text9Handle;
        FBig = r.Text11bHandle;
    }

    public void SetDrawerArea(float h) => _drawerAreaH = h;

    public void FocusSearch() { /* la ventana ya gana foco al activarse con click */ }

    // teclado: WM_CHAR escribe, KEYDOWN navega/ejecuta. true â†’ repintar.
    public bool OnKey(uint msg, WPARAM wparam)
    {
        if (msg == WM_CHAR)
        {
            char c = (char)wparam.Value;
            if (c == 27) return false;                       // ESC lo maneja KEYDOWN
            if (c == '\r' || c == '\n')
            {
                LaunchSelected();
                return true;
            }
            if (c == '\b')
            {
                if (_search.Length > 0) _search = _search[..^1];
                _selIdx = 0; _topRow = 0; _resultsCache = null;
                return true;
            }
            if (!char.IsControl(c) && _search.Length < 64)
            {
                _search += c;
                _selIdx = 0; _topRow = 0; _resultsCache = null;
                return true;
            }
            return false;
        }

        // WM_KEYDOWN
        int vk = (int)wparam.Value;
        var results = CurrentResults();
        switch (vk)
        {
            case 0x1B: // VK_ESCAPE: limpia búsqueda; si ya está vacía, cierra el drawer
                if (_search.Length > 0) { _search = ""; _selIdx = 0; _topRow = 0; _resultsCache = null; return true; }
                _dismissRequested = true;
                return true;
            case 0x26: // VK_UP
                if (_selIdx > 0) { _selIdx--; ClampScroll(results.Count); return true; }
                return false;
            case 0x28: // VK_DOWN
                if (_selIdx < results.Count - 1) { _selIdx++; ClampScroll(results.Count); return true; }
                return false;
            // VK_RETURN NO va acÃ¡: Enter produce WM_KEYDOWN + WM_CHAR('\r') â€”
            // manejarlo en ambos = doble launch. Solo WM_CHAR lo lanza.
        }
        return false;
    }

    private void LaunchSelected()
    {
        var results = CurrentResults();
        if (_selIdx >= 0 && _selIdx < results.Count)
            Launch(results[_selIdx]);
    }

    private void ClampScroll(int total)
    {
        int visible = VisibleRows;
        if (_topRow > _selIdx) _topRow = _selIdx;
        if (_selIdx >= _topRow + visible) _topRow = _selIdx - visible + 1;
        if (_topRow < 0) _topRow = 0;
    }

    private int VisibleRows => Math.Max(1, (int)((_drawerAreaH - SearchH - StartBtnH - 12) / RowH));

    // click dentro del Ã¡rea del drawer (coords locales al drawer).
    // Layout: search box [0..SearchH] | filas desde SearchH+2 (calza con Render).
    public void OnClick(int x, int y, int drawerH)
    {
        if (y < SearchH + 2) return; // search box: F2 (IME)

        var results = CurrentResults();
        int idx = (int)((y - SearchH - 2) / RowH) + _topRow;
        if (idx >= 0 && idx < results.Count)
        {
            _selIdx = idx;
            Launch(results[idx]);
        }
    }

    private List<AppCatalog.AppEntry> CurrentResults()
    {
        // cacheada: solo recompute al cambiar search o refrescar catÃ¡logo
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

    // BHID_SFUIObject = "GetUIObjectOf" del item â€” ruta canÃ³nica para IContextMenu
    private static readonly Guid SfUiObjectGuid = BHID_SFUIObject;

    // "open\0" ANSI persistente (InvokeCommand lee el LPCSTR mucho despuÃ©s
    // del retorno â€” jamÃ¡s stackalloc)
    private static readonly nint _openVerbPtr = InitOpenVerb();

    private static nint InitOpenVerb()
    {
        var p = Marshal.AllocHGlobal(5);
        Marshal.WriteByte(p, 0, (byte)'o');
        Marshal.WriteByte(p, 1, (byte)'p');
        Marshal.WriteByte(p, 2, (byte)'e');
        Marshal.WriteByte(p, 3, (byte)'n');
        Marshal.WriteByte(p, 4, 0);
        return p;
    }

    private void Launch(AppCatalog.AppEntry entry)
    {
        try
        {
            // AppsFolder (padre de los items del catalogo) — API opaca nint
            var appsFolder = Shell.OpenAppsFolder();
            if (appsFolder == 0) return;
            try
            {
                Shell.LaunchByPidl(appsFolder, entry.Pidl);
                Console.WriteLine($"[drawer] launch OK: {entry.Name}");
                _dismissRequested = true;   // al invocar se cierra el menu
            }
            finally { Shell.ReleaseFolder(appsFolder); }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[drawer] launch error {entry.Name}: {ex.Message}");
        }
    }

    // ---- boton Inicio estilo clasico: rectangulo abajo de todo ----
    // Area definida: (4, by, w-8, StartBtnH), logo en (LogoX, centrado, 24px).
    // Soporte PNG futuro: skins/<nombre>/start.png se pinta DENTRO del rect
    // del logo (DrawBitmap con estas mismas coords); hoy placeholder flat.
    private const float LogoPx = 24f;
    private const float LogoX = 10f;

    private void RenderStartButton(RenderCtx ctx, int x, int y, int w, int h)
    {
        float by = y + h - StartBtnH - 4;
        // cuerpo
        ctx.FillRect(Skin.Btn, 4, by, w - 8, StartBtnH);
        // relieve clasico: luz arriba/izq, sombra abajo/der
        ctx.Line(Skin.White, 4, by, w - 4, by);
        ctx.Line(Skin.White, 4, by, 4, by + StartBtnH);
        ctx.Line(Skin.Divider, 4, by + StartBtnH, w - 4, by + StartBtnH);
        ctx.Line(Skin.Divider, w - 4, by, w - 4, by + StartBtnH);
        // ancla del logo (futura start.png del skin)
        float ly = by + (StartBtnH - LogoPx) / 2;
        ctx.FillRect(Skin.SheenTop, LogoX, ly, LogoPx, LogoPx);
        ctx.Line(Skin.Divider, LogoX, ly, LogoX + LogoPx, ly);
        ctx.Line(Skin.Divider, LogoX, ly + LogoPx, LogoX + LogoPx, ly + LogoPx);
        ctx.Line(Skin.Divider, LogoX, ly, LogoX, ly + LogoPx);
        ctx.Line(Skin.Divider, LogoX + LogoPx, ly, LogoX + LogoPx, ly + LogoPx);
        // etiqueta
        ctx.Text("Inicio", FBig, Skin.White, LogoX + LogoPx + 8, by + (StartBtnH - 18) / 2, w - 60, 18);
    }

    // ---- superficie de drop (drawer colapsado): grid de thumbnails ----
    // El CCW acepta toda la ventana; esta zona visible muestra el stack con
    // thumbs (o solo nombres si el thumb falla). Click-arrastrar = drag-out.
    private const float ThumbPx = 56f;
    private const float CellH = 76f;
    private const int DropCols = 3;

    private List<string>? _dropStack;
    private bool _dragOver;
    private int _pressedItem = -1;
    private float _dropGridY;      // coords drawer-local, del ultimo paint
    private float _dropCellW;
    private int _dropCells;        // celdas visibles en el ultimo paint

    internal void SetDropStack(List<string> stack) => _dropStack = stack;

    internal void SetDragOver(bool over)
    {
        if (_dragOver != over)
        {
            _dragOver = over;
            App.Instance?.Invalidate();
        }
    }

    internal void SetPressedItem(int index)
    {
        if (_pressedItem != index)
        {
            _pressedItem = index;
            App.Instance?.Invalidate();
        }
    }

    private void RenderDropZone(RenderCtx ctx, int x, int y, int w, int h)
    {
        if (h < 60) return;
        _dropCellW = (w - 8) / (float)DropCols;
        int count = _dropStack?.Count ?? 0;
        string header = _dragOver ? "suelta para apilar"
            : count > 0 ? $"Drop ({count})" : "Arrastra archivos";
        ctx.Text(header, FBig, _dragOver ? Skin.Text : Skin.Muted, x + 10, y + 4, w - 20, 18);

        float gy = y + 24;
        _dropGridY = gy;
        int rows = Math.Max(0, (int)((h - 28) / CellH));
        int n = Math.Min(count, rows * DropCols);
        _dropCells = n;
        if (_dropStack is null) return;
        for (int p = 0; p < n; p++)
        {
            int i = count - 1 - p;   // mas nuevos arriba
            int row = p / DropCols, col = p % DropCols;
            float cx = x + 4 + col * _dropCellW + _dropCellW / 2;
            float cy = gy + row * CellH;
            if (i == _pressedItem)
                ctx.FillRect(Skin.Sel, x + 4 + col * _dropCellW + 2, cy - 2, _dropCellW - 4, CellH - 2);
            nint bmp = ctx.GetThumb(_dropStack[i]);
            if (bmp != 0) ctx.DrawBitmap(bmp, cx - ThumbPx / 2, cy, ThumbPx, ThumbPx);
            else ctx.FillRect(Skin.Search, cx - ThumbPx / 2, cy, ThumbPx, ThumbPx);
            var name = _dropStack[i];
            int cut = name.LastIndexOf('\\');
            if (cut >= 0) name = name[(cut + 1)..];
            if (name.Length > 14) name = name[..13] + "…";
            ctx.Text(name, F, Skin.Text, cx - _dropCellW / 2 + 4, cy + ThumbPx + 2, _dropCellW - 8, 14);
        }
    }

    // celda bajo el punto (coords drawer-local) -> index en el stack, o -1
    internal int ThumbHitTest(int x, int y)
    {
        if (_dropStack is null || _dropStack.Count == 0 || _dropCells <= 0) return -1;
        if (y < _dropGridY) return -1;
        int col = (int)((x - 4) / _dropCellW);
        int row = (int)((y - _dropGridY) / CellH);
        if (col < 0 || col >= DropCols || row < 0) return -1;
        int p = row * DropCols + col;
        if (p < 0 || p >= _dropCells) return -1;
        return _dropStack.Count - 1 - p;
    }

    // dismiss del drawer: la app lo cierra al lanzar o con ESC (ver App)
    private bool _dismissRequested;

    internal bool ConsumeDismiss()
    {
        if (!_dismissRequested) return false;
        _dismissRequested = false;
        return true;
    }

    public void Render(RenderCtx ctx, int x, int y, int w, int h, bool open)
    {
        RenderStartButton(ctx, x, y, w, h);

        if (!open)
        {
            RenderDropZone(ctx, x, y, w, (int)(y + h - StartBtnH - 4 - y - 4));
            return;
        }

        float dy = y + 4;
        // search box con el texto real + caret
        ctx.FillRect(Skin.Search, 4, dy, w - 8, SearchH);
        ctx.Line(Skin.Divider, 4, dy, w - 4, dy);
        ctx.Line(Skin.Divider, 4, dy + SearchH, w - 4, dy + SearchH);
        ctx.Line(Skin.Divider, 4, dy, 4, dy + SearchH);
        ctx.Line(Skin.Divider, w - 4, dy, w - 4, dy + SearchH);
        string caret = "|";
        ctx.Text(string.IsNullOrEmpty(_search) ? "Buscar... " + caret : _search + caret, F, Skin.Text, 10, dy + 4);
        dy += SearchH + 2;

        // filas con scroll + selecciÃ³n
        var results = CurrentResults();
        int visible = VisibleRows;
        int first = Math.Min(_topRow, Math.Max(0, results.Count - 1));
        int last = Math.Min(first + visible, results.Count);
        for (int i = first; i < last; i++)
        {
            float ry = dy + (i - first) * RowH;
            if (i == _selIdx)
                ctx.FillRect(Skin.Sel, 6, ry - 1, w - 12, RowH);
            ctx.Text(results[i].Name, F, i == _selIdx ? Skin.Text : Skin.Text, 8, ry, w - 20);
        }

        // scrollbar minimal si hay overflow
        if (results.Count > visible)
        {
            float trackH = visible * RowH;
            float thumbH = Math.Max(12f, trackH * visible / results.Count);
            float thumbY = dy + (trackH - thumbH) * first / Math.Max(1, results.Count - visible);
            ctx.FillRect(Skin.Divider, w - 6, dy, 2, trackH);
            ctx.FillRect(Skin.Text, w - 6, thumbY, 2, thumbH);
        }
    }

    public void Dispose() => _catalog.Dispose();
}
