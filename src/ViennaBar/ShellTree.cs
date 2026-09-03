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

    public void Render(RenderCtx ctx, int x, int y, int w, int h)
    {
        float cy = y + 4;
        const float rowH = 18f;

        foreach (var root in Roots)
        {
            if (cy + rowH > y + h) return;
            if (root.Expanded) ctx.FillRect(Skin.Sel, x + 2, cy - 1, w - 8, rowH);
            ctx.Text((root.Expanded ? "- " : "+ ") + root.Name, AppText.Fmt, Skin.Text, x + 8, cy, w - 20);
            cy += rowH;
            if (root.Expanded)
            {
                foreach (var c in root.Children)
                {
                    if (cy + rowH > y + h) return;
                    ctx.Text("    " + (c.IsFolder ? (c.Expanded ? "- " : "+ ") : "  ") + c.Name, AppText.Fmt, Skin.Text, x + 8, cy, w - 20);
                    cy += rowH;
                }
            }
        }
    }

    private TreeNode? HitTest(int y, int width, int height)
    {
        const float rowH = 18f;
        float cy = 4;
        foreach (var root in Roots)
        {
            if (y < cy + rowH && y >= cy) return root;
            cy += rowH;
            if (root.Expanded)
            {
                foreach (var c in root.Children)
                {
                    if (y < cy + rowH && y >= cy) return c;
                    cy += rowH;
                }
            }
        }
        return null;
    }

    public void Dispose()
    {
        if (_notifyId != 0) _ = SHChangeNotifyDeregister(_notifyId);
        Shell.ReleaseFolder(_desktop);
    }
}
