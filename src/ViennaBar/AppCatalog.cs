using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;
using static Windows.Win32.PInvoke;

namespace ViennaBar;

// AppCatalog con cache persistente (hallazgo S4: el primer Next() del enum
// COM construye todo el catálogo ~1.9 s → cache en LOCALAPPDATA + refresh
// en background; el primer frame sale del cache).
internal sealed unsafe class AppCatalog : IDisposable
{
    public sealed record AppEntry(string Name, string ParsingName);

    private List<AppEntry> _apps = new();
    private string _cachePath = null!;
    private Thread? _refreshThread;

    public IReadOnlyList<AppEntry> Apps => _apps;

    public void Attach()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ViennaBar");
        Directory.CreateDirectory(dir);
        _cachePath = Path.Combine(dir, "catalog.cache");

        // primer frame: desde cache (si existe)
        LoadCache();

        // refresh en background
        _refreshThread = new Thread(RefreshFromShell) { IsBackground = true };
        _refreshThread.Start();
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            var lines = File.ReadAllLines(_cachePath);
            var list = new List<AppEntry>(lines.Length);
            foreach (var line in lines)
            {
                var idx = line.IndexOf('\t');
                if (idx > 0)
                    list.Add(new AppEntry(line[..idx], line[(idx + 1)..]));
            }
            if (list.Count > 0)
            {
                _apps = list;
            }
        }
        catch { /* cache corrupta: se regenera */ }
    }

    private void SaveCache()
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            foreach (var a in _apps)
                sb.AppendLine(a.Name.Replace('\t', ' ') + "\t" + a.ParsingName);
            File.WriteAllText(_cachePath, sb.ToString());
        }
        catch { /* sin permisos: seguimos sin cache */ }
    }

    // enum COM completo (el S4 validado)
    private void RefreshFromShell()
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var list = new List<AppEntry>();

            Guid iidItem = typeof(IShellItem).GUID;
            HRESULT hr = SHGetKnownFolderItem(FOLDERID_AppsFolder, KNOWN_FOLDER_FLAG.KF_FLAG_DEFAULT, default, in iidItem, out var itemObj);
            if (hr.Failed || itemObj is not IShellItem item) return;

            Guid iidFolder = typeof(IShellFolder).GUID;
            Guid bhid = BHID_SFObject;
            item.BindToHandler(default, &bhid, &iidFolder, out var psfObj);
            if (psfObj is not IShellFolder apps) return;

            var hrEnum = apps.EnumObjects(default, 0x20 | 0x40 | 0x80, out var en);
            if (hrEnum.Failed) return;

            while (true)
            {
                ITEMIDLIST* child = null;
                uint fetched = 0;
                hr = en.Next(1, &child, &fetched);
                if (hr != 0 || fetched == 0) break;
                try
                {
                    var sr = default(STRRET);
                    apps.GetDisplayNameOf(child, SHGDNF.SHGDN_NORMAL, &sr);
                    string name = sr.uType == 0 && sr.Anonymous.pOleStr.Value != null
                        ? new string(sr.Anonymous.pOleStr.Value) : string.Empty;
                    var sr2 = default(STRRET);
                    apps.GetDisplayNameOf(child, SHGDNF.SHGDN_FORPARSING, &sr2);
                    string parsing = sr2.uType == 0 && sr2.Anonymous.pOleStr.Value != null
                        ? new string(sr2.Anonymous.pOleStr.Value) : string.Empty;
                    if (name.Length > 0 && parsing.Length > 0)
                        list.Add(new AppEntry(name, parsing));
                }
                finally { ILFree(child); }
            }
            Marshal.ReleaseComObject(en);
            Marshal.ReleaseComObject(psfObj);

            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            _apps = list;
            SaveCache();
            Console.WriteLine($"[catalog] refresh background: {list.Count} apps en {sw.ElapsedMilliseconds} ms");
            App.Instance?.Invalidate();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[catalog] refresh error: {ex.Message}");
        }
    }

    public IEnumerable<AppEntry> Search(string query) =>
        _apps.Where(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

    public void Dispose() => _refreshThread?.Join(500);
}
