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
    }

    private static string DownloadsPath() =>
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
        var rows = VisibleNodes();
        int visible = Math.Max(1, (int)(h / RowH));
        _topRow = Math.Min(_topRow, Math.Max(0, rows.Count - visible));
        int last = Math.Min(_topRow + visible, rows.Count);
        for (int i = _topRow; i < last; i++)
        {
            var (node, depth) = rows[i];
            float cy = y + 4 + (i - _topRow) * RowH;
            if (ReferenceEquals(node, _selected) || (depth == 0 && node.Expanded))
                ctx.FillRect(Skin.Sel, x + 2, cy - 1, w - 8, RowH);
            string indent = depth == 0 ? "" : "    ";
            string mark = node.IsFolder ? (node.Expanded ? "- " : "+ ") : "  ";
            ctx.Text(indent + mark + node.Name, AppText.Fmt, Skin.Text, x + 8, cy, w - 20);
        }
    }

    // filas visibles planas (raiz + hijos de expandidas) para render/hit/scroll
    private List<(TreeNode node, int depth)> VisibleNodes()
    {
        var list = new List<(TreeNode, int)>();
        foreach (var root in Roots)
        {
            list.Add((root, 0));
            if (root.Expanded)
                foreach (var c in root.Children) list.Add((c, 1));
        }
        return list;
    }

    private int _topRow;
    private int _treeH;
    internal int TopRow => _topRow;
    internal TreeNode? Selected => _selected;
    private TreeNode? _selected;

    internal void ScrollBy(int lines, int height)
    {
        int visible = Math.Max(1, height / 18);
        int max = Math.Max(0, VisibleNodes().Count - visible);
        _topRow = Math.Clamp(_topRow + lines, 0, max);
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

    internal TreeNode? HitTestNode(int y, int width, int height) => HitTest(y, width, height);

    private TreeNode? HitTest(int y, int width, int height)
    {
        var rows = VisibleNodes();
        int idx = (int)(y / RowH) + _topRow;
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
