using Windows.Win32;
using Windows.Win32.Foundation;
using static Windows.Win32.PInvoke;
using Shell = ViennaBar.ShellNative.ShellNative;

namespace ViennaBar;

// Namespace provider â€” Ã¡rbol del namespace shell, 100% API opaca del
// ViennaBar.Shell (handles nint): el core no toca tipos COM (AOT-total,
// sin colisiones de assemblies CsWin32).
internal sealed class ShellTree : IDisposable
{
    private const uint WM_SH_NOTIFY = WM_APP + 1;

    private HWND _hwnd;
    private nint _desktop;
    private uint _notifyId;

    public readonly List<TreeNode> Roots = new();

    public sealed class TreeNode
    {
        public required string Name;
        public required string ParsingName;
        public bool IsFolder;
        public bool Expanded;
        public byte[]? Pidl;
        public readonly List<TreeNode> Children = new();
    }

    // ---- buscador de carpetas (arriba del tree, mismo panel) ----
    private const float FindBoxH = 30f;
    private readonly FolderIndex _index = new();
    private string _findQuery = "";
    private bool _findFocused;
    private List<FolderIndex.DirEntry>? _findCache;
    private int _findHover = -1;

    private List<FolderIndex.DirEntry> FindResults()
    {
        _findCache ??= _index.Search(_findQuery);
        return _findCache;
    }

    internal void ClearFind()
    {
        _findQuery = "";
        _findFocused = false;
        _findCache = null;
        _findHover = -1;
    }

    private void NavigateFind(FolderIndex.DirEntry e)
    {
        ExpandToPath(e.FullPath);
        ClearFind();
        App.Instance?.Invalidate();
    }

    internal bool OnFindKey(uint msg, WPARAM wparam)
    {
        if (msg == WM_CHAR)
        {
            char c = (char)wparam.Value;
            if (c == 27) return false;
            if (c == '\r' || c == '\n')
            {
                var r = FindResults();
                if (r.Count > 0) NavigateFind(r[0]);
                return true;
            }
            if (c == '\b')
            {
                if (_findQuery.Length > 0) { _findQuery = _findQuery[..^1]; _findCache = null; }
                _findFocused = true;
                return true;
            }
            if (!char.IsControl(c) && _findQuery.Length < 64)
            {
                _findQuery += c;
                _findCache = null;
                _findFocused = true;
                return true;
            }
            return false;
        }
        if (msg == WM_KEYDOWN && (int)wparam.Value == 0x1B)
        {
            if (_findQuery.Length > 0 || _findFocused) { ClearFind(); return true; }
            return false;
        }
        return false;
    }

    public void Attach(HWND hwnd)
    {
        _hwnd = hwnd;
        _desktop = Shell.GetDesktopFolder();
        if (_desktop == 0) return;

        foreach (var (name, parse) in new[]
        {
            ("Este equipo", "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"),
            ("Escritorio", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
            ("Descargas", DownloadsPath()),
            ("Documentos", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
        })
        {
            Roots.Add(new TreeNode { Name = name, ParsingName = parse, IsFolder = true });
        }

        RegisterChangeNotify();
        _index.StartBuild();   // indice de carpetas en background (buscador)
    }

    internal static string DownloadsPath() =>
        SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, default, out var p).Succeeded
            ? p.ToString()! : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private unsafe void RegisterChangeNotify()
    {
        // PIDL del desktop = vacÃ­o (2 bytes terminador)
        var pidl = (Windows.Win32.UI.Shell.Common.ITEMIDLIST*)CoTaskMemAlloc(2);
        pidl->mkid.cb = 0;
        var entry = new Windows.Win32.UI.Shell.SHChangeNotifyEntry { pidl = pidl, fRecursive = true };
        _notifyId = SHChangeNotifyRegister(_hwnd,
            Windows.Win32.UI.Shell.SHCNRF_SOURCE.SHCNRF_InterruptLevel | Windows.Win32.UI.Shell.SHCNRF_SOURCE.SHCNRF_ShellLevel,
            0x00000001 | 0x00000002 | 0x00000004 | 0x00002000,
            WM_SH_NOTIFY, 1, in entry);
    }

    public void OnClick(int x, int y, int width, int height)
    {
        if (y < FindBoxH)
        {
            // click en la caja: enfoca (el placeholder se oculta)
            if (!_findFocused) { _findFocused = true; App.Instance?.Invalidate(); }
            return;
        }
        if (_findQuery.Length > 0)
        {
            var r = FindResults();
            int i = (int)((y - FindBoxH) / RowH);
            if (i >= 0 && i < r.Count) NavigateFind(r[i]);
            return;
        }
        var node = HitTest(y, width, height);
        if (node is null) return;
        _selected = node;
        if (node.IsFolder)
        {
            if (!node.Expanded && node.Children.Count == 0) Expand(node);
            else node.Expanded = !node.Expanded;
        }
        App.Instance?.Invalidate();
    }

    public void OnRightClick(int x, int y, int width, int height)
    {
        App.AppLog($"tree: rightclick at {x},{y}");
        var node = HitTest(y, width, height);
        if (node?.Pidl is null || node.Pidl.Length < 4)
        {
            App.AppLog($"tree: rightclick SIN nodo valido (node={node?.Name ?? "null"}, pidlLen={node?.Pidl?.Length ?? -1})");
            return;
        }
        _ = GetCursorPos(out var ptScreen);
        App.AppLog($"tree: rightclick nodo={node.Name} parent bind...");

        var parent = FindParentOf(Roots, node);
        nint folder = _desktop;
        if (parent is not null && !string.IsNullOrEmpty(parent.ParsingName))
        {
            var sf = Shell.OpenFolderByParsingName(parent.ParsingName);
            if (sf != 0) folder = sf;
        }
        App.AppLog("tree: rightclick -> ShowContextMenu");
        Shell.ShowContextMenu(folder, node.Pidl, (nint)_hwnd.Value, ptScreen.X, ptScreen.Y);
        App.AppLog("tree: rightclick done");
    }

    private void Expand(TreeNode node)
    {
        App.AppLog($"tree: expand {node.Name}");
        var folder = Shell.OpenFolderByParsingName(node.ParsingName);
        if (folder == 0)
        {
            App.AppLog($"tree: expand {node.Name} FAIL bind");
            return;
        }
        try
        {
            node.Children.Clear();
            foreach (var c in Shell.EnumChildren(folder))
            {
                node.Children.Add(new TreeNode
                {
                    Name = c.Name,
                    ParsingName = c.ParsingName,
                    IsFolder = c.IsFolder,
                    Pidl = c.Pidl,
                });
            }
            node.Expanded = true;
        }
        finally { Shell.ReleaseFolder(folder); }
        App.AppLog($"tree: expanded {node.Name} -> {node.Children.Count} children");
    }

    private static TreeNode? FindParentOf(List<TreeNode> list, TreeNode target)
    {
        foreach (var n in list)
        {
            foreach (var c in n.Children)
                if (ReferenceEquals(c, target)) return n;
            var deeper = FindParentOf(n.Children, target);
            if (deeper is not null) return deeper;
        }
        return null;
    }

    private const float RowH = 18f;

    public void Render(RenderCtx ctx, int x, int y, int w, int h)
    {
        _treeH = h;
        // caja de busqueda (siempre visible arriba del panel)
        ctx.FillRect(Skin.Search, x + 4, y + 4, w - 8, 22);
        ctx.Line(Skin.Divider, x + 4, y + 4, x + w - 4, y + 4);
        ctx.Line(Skin.Divider, x + 4, y + 26, x + w - 4, y + 26);
        ctx.Line(Skin.Divider, x + 4, y + 4, x + 4, y + 26);
        ctx.Line(Skin.Divider, x + w - 4, y + 4, x + w - 4, y + 26);
        // mini glyph de carpeta
        ctx.FillRect(Skin.Muted, x + 10, y + 14, 12, 9);
        ctx.FillRect(Skin.Muted, x + 10, y + 11, 6, 4);
        bool fqEmpty = string.IsNullOrEmpty(_findQuery);
        string shown = !fqEmpty ? _findQuery : _findFocused ? "" : "carpetas... ";
        ctx.Text(shown, AppText.Fmt, fqEmpty && !_findFocused ? Skin.Divider : Skin.Text, x + 26, y + 7, w - 36, 14);
        if (fqEmpty && _findFocused)
            ctx.FillRect(Skin.Text, x + 26, y + 8, 2, 14);

        if (!fqEmpty)
        {
            RenderFindResults(ctx, x, y, w, h);
            return;
        }
        var rows = VisibleNodes();
        int visible = Math.Max(1, (int)((h - FindBoxH) / RowH));
        _topRow = Math.Min(_topRow, Math.Max(0, rows.Count - visible));
        int last = Math.Min(_topRow + visible, rows.Count);
        for (int i = _topRow; i < last; i++)
        {
            var (node, depth) = rows[i];
            float cy = y + FindBoxH + 4 + (i - _topRow) * RowH;
            if (ReferenceEquals(node, _selected) || ReferenceEquals(node, _hovered))
                ctx.FillRect(Skin.Sel, x + 2, cy - 1, w - 8, RowH);
            string indent = new string(' ', depth * 4);
            string mark = node.IsFolder ? (node.Expanded ? "- " : "+ ") : "  ";
            ctx.Text(indent + mark + node.Name, AppText.Fmt, Skin.Text, x + 8, cy, w - 20);
            // Overlay separador semitransparente entre filas (solo cuando hay多于 una fila visible)
            if (i < last - 1 && Config.Current.GlassOverlayEnabled)
            {
                // alpha ~0.2: 0x33000000
                ctx.FillRect(unchecked((int)0x33000000), x + 2, cy + RowH, w - 8, 1);
            }
        }
    }

    // resultados reemplazan al tree mientras hay query
    private void RenderFindResults(RenderCtx ctx, int x, int y, int w, int h)
    {
        var results = FindResults();
        if (!_index.Ready)
        {
            ctx.Text("Indexando carpetas…", AppText.Fmt, Skin.Muted, x + 10, y + FindBoxH + 4, w - 20, 14);
            _findHover = -1;
            return;
        }
        int visible = Math.Max(1, (int)((h - FindBoxH) / RowH));
        _findTop = Math.Min(_findTop, Math.Max(0, results.Count - visible));
        int last = Math.Min(_findTop + visible, results.Count);
        for (int i = _findTop; i < last; i++)
        {
            var e = results[i];
            float cy = y + FindBoxH + 4 + (i - _findTop) * RowH;
            if (i == _findHover)
                ctx.FillRect(Skin.Sel, x + 2, cy - 1, w - 8, RowH);
            ctx.Text($"{e.Name}  ·  {e.FullPath}", AppText.Fmt, Skin.Text, x + 8, cy, w - 20, 14);
        }
        if (results.Count == 0)
            ctx.Text("Sin carpetas", AppText.Fmt, Skin.Muted, x + 10, y + FindBoxH + 4, w - 20, 14);
    }

    private int _findTop;

    // filas visibles planas (recursivo: hijos de toda expandida) para render/hit/scroll
    private List<(TreeNode node, int depth)> VisibleNodes()
    {
        var list = new List<(TreeNode, int)>();
        foreach (var root in Roots) AddVisible(list, root, 0);
        return list;
    }

    private static void AddVisible(List<(TreeNode, int)> list, TreeNode node, int depth)
    {
        list.Add((node, depth));
        if (node.Expanded)
            foreach (var c in node.Children) AddVisible(list, c, depth + 1);
    }

    internal int VisibleCount => VisibleNodes().Count;

    private int _topRow;
    private int _treeH;
    internal int TopRow => _topRow;
    internal TreeNode? Selected => _selected;
    private TreeNode? _selected;
    private TreeNode? _hovered;   // highlight de hover (no pisa Selected)

    internal void ScrollBy(int lines, int height)
    {
        if (_findQuery.Length > 0)
        {
            int vis = Math.Max(1, (int)((height - FindBoxH) / RowH));
            int m = Math.Max(0, FindResults().Count - vis);
            _findTop = Math.Clamp(_findTop + lines, 0, m);
        }
        else
        {
            int visible = Math.Max(1, (int)((height - FindBoxH) / RowH));
            int max = Math.Max(0, VisibleNodes().Count - visible);
            _topRow = Math.Clamp(_topRow + lines, 0, max);
        }
        App.Instance?.Invalidate();
    }

    // lleva el nodo a la vista (usado tras navegar/invocar)
    internal void EnsureVisible(TreeNode node, int height)
    {
        var rows = VisibleNodes();
        int idx = rows.FindIndex(r => ReferenceEquals(r.node, node));
        if (idx < 0) return;
        int visible = Math.Max(1, height / 18);
        if (idx < _topRow) _topRow = idx;
        else if (idx >= _topRow + visible) _topRow = idx - visible + 1;
        App.Instance?.Invalidate();
    }

    internal void Select(TreeNode node, int height)
    {
        _selected = node;
        EnsureVisible(node, height);
    }

    internal void SetHover(TreeNode? node)
    {
        if (!ReferenceEquals(_hovered, node))
        {
            _hovered = node;
            App.Instance?.Invalidate();
        }
    }

    internal TreeNode? HitTestNode(int y, int width, int height) => HitTest(y, width, height);

    private TreeNode? HitTest(int y, int width, int height)
    {
        var rows = VisibleNodes();
        // El render pinta filas empezando en: y + FindBoxH + 4
        // El hit test debe compensar ese mismo offset para mapear el índice correcto.
        int offset = (int)(FindBoxH + 4);  // 34 píxeles
        int relY = y - offset;
        int idx = (int)Math.Floor((double)relY / RowH) + _topRow;
        if (idx < 0 || idx >= rows.Count) return null;
        return rows[idx].node;
    }

    // M1 --open-folder: expande la cadena hasta la carpeta (lo que exista).
    // 1) raiz FS directa (Escritorio/Descargas/Documentos) 2) via Este equipo.
    internal void ExpandToPath(string fullPath)
    {
        string path;
        try { path = Path.GetFullPath(fullPath).TrimEnd('\\'); }
        catch { return; }
        foreach (var r in Roots)
        {
            if (r.ParsingName.StartsWith("::{")) continue;
            string rd = r.ParsingName.TrimEnd('\\');
            if (path.Equals(rd, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(rd + "\\", StringComparison.OrdinalIgnoreCase))
            {
                WalkDown(r, path.Length > rd.Length ? path[(rd.Length + 1)..] : "");
                return;
            }
        }
        var pc = Roots.Find(r => r.ParsingName.StartsWith("::{20D04FE0"));
        if (pc is null) return;
        if (!pc.Expanded) Expand(pc);
        string drive = (Path.GetPathRoot(path) ?? "").TrimEnd('\\');   // "C:"
        if (drive.Length == 0) return;
        var node = pc.Children.Find(c => c.IsFolder
            && c.ParsingName.TrimEnd('\\').Equals(drive, StringComparison.OrdinalIgnoreCase));
        if (node is null) return;
        string rest = path.Length > drive.Length ? path[(drive.Length + 1)..] : "";
        WalkDown(node, rest);
    }

    // desciende por segmentos expandiendo cada nivel (y el destino final).
    // Match por display Name O por cola del ParsingName (nombres localizados).
    private void WalkDown(TreeNode node, string rest)
    {
        App.AppLog($"nav: walkdown desde {node.Name} rest=[{rest}]");
        foreach (var seg in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!node.Expanded) Expand(node);
            var next = node.Children.Find(c => c.IsFolder
                && (c.Name.Equals(seg, StringComparison.OrdinalIgnoreCase)
                    || c.ParsingName.TrimEnd('\\').EndsWith("\\" + seg, StringComparison.OrdinalIgnoreCase)));
            App.AppLog($"nav: seg=[{seg}] en {node.Name} ({node.Children.Count} hijos) -> {(next is null ? "MISS" : next.Name)}");
            if (next is null) break;
            node = next;
        }
        if (!node.Expanded) Expand(node);
        _selected = node;
        EnsureVisible(node, _treeH);
        App.Instance?.Invalidate();
    }

    public void Dispose()
    {
        if (_notifyId != 0) _ = SHChangeNotifyDeregister(_notifyId);
        Shell.ReleaseFolder(_desktop);
    }
}
