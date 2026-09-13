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
    private const float PinW = 56f;
    private const int PinRows = 1;
    private const float PaddingX = 8f;
    private const float StartBtnW = 128f;    // ancho del botón Inicio (ya no todo el ancho)
    private const float PowerIconW = 24f;    // ancho de cada icono de apagado

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

    public void FocusSearch() { /* no-op: el search se activa SOLO con click */ }

    // enfoca si la y drawer-local cae en la caja (para el click que ABRE)
    internal void FocusBoxIfHit(int y)
    {
        if (y >= 0 && y < SearchH + 2) _searchFocused = true;
    }

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
                _searchFocused = true;   // escribir es activar
                return true;
            }
            if (!char.IsControl(c) && _search.Length < 64)
            {
                _search += c;
                _selIdx = 0; _topRow = 0; _resultsCache = null;
                _searchFocused = true;   // escribir es activar
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

    // scroll con ruedita (misma _topRow que el teclado)
    internal void ScrollBy(int lines)
    {
        int max = Math.Max(0, CurrentResults().Count - VisibleRows);
        int next = Math.Clamp(_topRow + lines, 0, max);
        if (next != _topRow)
        {
            _topRow = next;
            App.Instance?.Invalidate();
        }
    }

    // click dentro del Ã¡rea del drawer (coords locales al drawer).
    // Layout: search box [0..SearchH] | filas desde SearchH+2 (calza con Render).
    // hover del mouse sobre filas (pinta, no toca _selIdx). -1 = limpiar.
    // Devuelve si cambio (la app re-arma el auto-close: hover = uso).
    internal bool HoverRow(int idx)
    {
        if (_hoverIdx == idx) return false;
        _hoverIdx = idx;
        App.Instance?.Invalidate();
        return true;
    }

    // indice de fila para una y drawer-local (misma matematica que OnClick)
    internal int RowAt(int y)
    {
        if (y < SearchH + 2) return -1;
        var results = CurrentResults();
        int idx = (int)((y - SearchH - 2) / RowH) + _topRow;
        return idx >= 0 && idx < results.Count ? idx : -1;
    }

    public void OnClick(int x, int y, int drawerH)
    {
        if (y < SearchH + 2)
        {
            // click en la caja: enfoca (el RowAt la excluye a proposito)
            if (!_searchFocused) { _searchFocused = true; App.Instance?.Invalidate(); }
            return;
        }
        int idx = RowAt(y);
        if (idx >= 0)
        {
            _selIdx = idx;
            Launch(CurrentResults()[idx]);
        }
    }

    internal List<AppCatalog.AppEntry> CurrentResults()
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
    private int _hoverIdx = -1;   // highlight de hover (no pisa _selIdx del teclado)
    private bool _searchFocused;  // click en el box (placeholder se oculta)

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
                _searchFocused = false;
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
        // cuerpo (ancho reducido: deja espacio a los iconos de apagado)
        ctx.FillRect(Skin.StartBtn, 4, by, StartBtnW, StartBtnH);
        // relieve clasico: luz arriba/izq, sombra abajo/der
        ctx.Line(Skin.White, 4, by, 4 + StartBtnW, by);
        ctx.Line(Skin.White, 4, by, 4, by + StartBtnH);
        ctx.Line(Skin.Divider, 4, by + StartBtnH, 4 + StartBtnW, by + StartBtnH);
        ctx.Line(Skin.Divider, 4 + StartBtnW, by, 4 + StartBtnW, by + StartBtnH);
        // ancla del logo (futura start.png del skin)
        float ly = by + (StartBtnH - LogoPx) / 2;
        if (!ctx.DrawSkinLogo(LogoX, ly, LogoPx, LogoPx))
        {
            ctx.FillRect(Skin.SheenTop, LogoX, ly, LogoPx, LogoPx);
            ctx.Line(Skin.Divider, LogoX, ly, LogoX + LogoPx, ly);
            ctx.Line(Skin.Divider, LogoX, ly + LogoPx, LogoX + LogoPx, ly + LogoPx);
            ctx.Line(Skin.Divider, LogoX, ly, LogoX, ly + LogoPx);
            ctx.Line(Skin.Divider, LogoX + LogoPx, ly, LogoX + LogoPx, ly + LogoPx);
        }
        // etiqueta (centrada en el ancho reducido del boton)
        ctx.Text("Inicio", FBig, Skin.White, LogoX + LogoPx + 8, by + (StartBtnH - 18) / 2, StartBtnW - LogoPx - 20, 18);
    }

    // dibuja UN icono de apagado (geometrico, monocromo) segun el indice.
    // 0=apagar, 1=reiniciar, 2=suspender, 3=cerrar sesion, 4=bloquear.
    private void RenderPowerIcon(RenderCtx ctx, float cx, float cy, int idx)
    {
        int c = Skin.Text;   // color de linea del icono
        float r = 8f;        // radio base
        switch (idx)
        {
            case 0: // apagar: circulo + barra vertical
                ctx.FillEllipse(c, cx, cy, r, r);
                ctx.FillEllipse(Skin.Bg, cx, cy, r - 2.5f, r - 2.5f);  // hueco (fondo real)
                ctx.FillRect(c, cx - 1.25f, cy - r - 2f, 2.5f, 5f);       // trazo superior
                break;
            case 1: // reiniciar: flecha (punta + arco sugerido)
                ctx.Line(c, cx - r, cy - 2, cx - r, cy + 2, 2f);          // cola
                ctx.Line(c, cx - r, cy - 2, cx - 1, cy - 2, 2f);          // punta arriba
                ctx.Line(c, cx - r, cy + 2, cx - 1, cy + 2, 2f);          // punta abajo
                ctx.FillEllipse(c, cx + 2, cy - 2, 1.5f, 1.5f);           // flecha (dot)
                break;
            case 2: // suspender: luna menguante
                ctx.FillEllipse(c, cx, cy, r, r);
                ctx.FillEllipse(Skin.Bg, cx + 3.5f, cy - 1f, r, r);      // recorte (fondo real)
                break;
            case 3: // cerrar sesion: puerta + flecha hacia fuera
                ctx.FillRect(c, cx - 3f, cy - r, 3f, 2 * r);              // panel
                ctx.Line(c, cx, cy, cx + 7, cy, 2f);                      // flecha
                ctx.Line(c, cx + 7, cy, cx + 3, cy - 4, 2f);              // punta
                ctx.Line(c, cx + 7, cy, cx + 3, cy + 4, 2f);
                break;
            case 4: // bloquear: candado (arco + cuerpo)
                ctx.FillEllipse(c, cx, cy - 3, 3.5f, 4f);
                ctx.FillEllipse(Skin.Bg, cx, cy - 3, 2f, 2.5f);          // hueco del arco (fondo real)
                ctx.FillRect(c, cx - 4.5f, cy - 3f, 9f, 8f);              // cuerpo
                ctx.FillEllipse(Skin.Bg, cx, cy - 1f, 1.5f, 1.5f);       // ojo de la cerradura (fondo real)
                break;
        }
    }

    // posicion X absoluta (de ventana) del primer icono power, a la derecha del boton Inicio.
    private float PowerIconsX(float x) => x + 4 + StartBtnW + 16;

    private const int PowerIconCount = 3;   // iconos visibles en el drawer colapsado

    private ColorPicker? _picker;
    internal void SetPicker(ColorPicker p) => _picker = p;

    // hit-test de la mini rueda (elemento 4, despues de los 3 iconos power)
    internal bool MiniWheelHitTest(float windowX, float x)
    {
        float ix = PowerIconsX(x) + 3 * (PowerIconW + 3);
        return windowX >= ix && windowX < ix + PowerIconW;
    }

    // hit-test de los iconos power del drawer colapsado. -1 = no hit.
    internal int PowerIconHitTest(float windowX, float x)
    {
        float ix = PowerIconsX(x);
        for (int i = 0; i < PowerIconCount; i++)
        {
            float bx = ix + i * (PowerIconW + 3);
            if (windowX >= bx && windowX < bx + PowerIconW) return i;
        }
        return -1;
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
        _dropGridY = gy - y;   // drawer-LOCAL (el hit-test llega en local)
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
            // × para desapilar (solo quita del stack, el archivo no se toca)
            float xx = x + 4 + col * _dropCellW + _dropCellW - 18;
            ctx.Text("×", FBig, Skin.Muted, xx, cy - 4, 16, 16);
            var name = _dropStack[i];
            int cut = name.LastIndexOf('\\');
            if (cut >= 0) name = name[(cut + 1)..];
            if (name.Length > 14) name = name[..13] + "…";
            ctx.Text(name, F, Skin.Text, cx - _dropCellW / 2 + 4, cy + ThumbPx + 2, _dropCellW - 8, 14);
        }
    }

    // × bajo el punto (coords drawer-local) -> index en el stack, o -1.
    // Se chequea ANTES que el press de drag (el × desapila al instante).
    internal int ThumbRemoveHitTest(int x, int y)
    {
        int idx = ThumbHitTest(x, y);
        if (idx < 0) return -1;
        int p = _dropStack!.Count - 1 - idx;   // posicion de grilla
        int row = p / DropCols, col = p % DropCols;
        float cy = _dropGridY + row * CellH;
        float xx = 4 + col * _dropCellW + _dropCellW - 18;
        if (x >= xx && x < xx + 16 && y >= cy - 4 && y < cy + 12) return idx;
        return -1;
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

    internal void ClearSearchFocus() => _searchFocused = false;

public void Render(RenderCtx ctx, int x, int y, int w, int h, bool open)
    {
        RenderStartButton(ctx, x, y, w, h);

        if (!open)
        {
            // Overlay semitransparente en drawer colapsado (fondo suave que refuerza la sensacion de panel).
            // Debe cubrir la franja inferior del drawer (el boton Inicio), NO la parte superior de la ventana.
            if (Config.Current.GlassOverlayEnabled)
            {
                float by = y + h - StartBtnH - 4;   // misma Y del boton Inicio
                ctx.FillRect(unchecked((int)0x22000000), x, by, w, StartBtnH + 4);
            }
            // ---- iconos de apagado a la derecha del boton Inicio ----
            float icoY = y + h - StartBtnH - 4 + StartBtnH / 2f;   // centro vertical del boton Inicio
            for (int i = 0; i < PowerLabels.Length && i < 3; i++)
            {
                float ix = PowerIconsX(x) + i * (PowerIconW + 3);
                float cx = ix + PowerIconW / 2f;
                RenderPowerIcon(ctx, cx, icoY, i);
            }
            // mini rueda cromática (abre el selector grande al hacer clic)
            if (_picker is not null)
            {
                float wx = PowerIconsX(x) + 3 * (PowerIconW + 3);
                int mr = (int)(PowerIconW / 2f);
                _picker.DrawWheel(ctx, wx + mr, icoY, mr);
            }
            // fila de pins (config.Current.Pins) antes del drop zone
            if (Config.Current.Pins.Any(p => p))
            {
                float py = y;
                float ph = StartBtnH;
                float pw = (w - PaddingX * 2) / 4f; // 4 pins maximo
                for (int i = 0; i < 8; i++)
                {
                    if (!Config.Current.Pins[i]) continue;
                    float cx = PaddingX + (i % 4) * (pw + PaddingX);
                    float cy = py + (i / 4) * (ph + 4);
                    // fondo de la celda pin
                    ctx.FillRect(Skin.Search, cx, cy, pw, ph);
                    // texto (nombre corto)
                    string name = PinName(i);
                    ctx.Text(name, F, Skin.Text, cx + 4, cy + 4, pw - 8, 14);
                    // click = lanzar path correspondiente
                    // (se chequea WM_LBUTTONUP en el message loop de App)
                }
            }
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
        bool empty = string.IsNullOrEmpty(_search);
        // placeholder en Divider (se lee atenuado en temas claros Y oscuros;
        // Muted sobre caja oscura parece texto normal)
        string shown = !empty ? _search + caret : _searchFocused ? "" : "Buscar... ";
        ctx.Text(shown, F, empty && !_searchFocused ? Skin.Divider : Skin.Text, 10, dy + 4);
        if (empty && _searchFocused)
            ctx.FillRect(Skin.Text, 10, dy + 5, 2, 16);   // caret pintado (activacion visible)
        dy += SearchH + 2;

        // filas con scroll + selecciÃ³n
        var results = CurrentResults();
        int visible = VisibleRows;
        int first = Math.Min(_topRow, Math.Max(0, results.Count - 1));
        int last = Math.Min(first + visible, results.Count);
        for (int i = first; i < last; i++)
        {
            float ry = dy + (i - first) * RowH;
            if (i == _selIdx || i == _hoverIdx)
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

    internal static readonly string[] PowerLabels =
        { "Apagar", "Reiniciar", "Suspender", "Cerrar sesión", "Bloquear" };

    public void Dispose() => _catalog.Dispose();

    private static string PinName(int idx)
    {
        return idx switch
        {
            0 => "Escritorio",
            1 => "Documentos",
            2 => "Descargas",
            3 => "Imágenes",
            4 => "Música",
            5 => "Videos",
            _ => "",
        };
    }
}
