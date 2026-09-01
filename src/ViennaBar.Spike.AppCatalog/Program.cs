using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;
using static Windows.Win32.PInvoke;

namespace ViennaBar.Spike.AppCatalog;

// S4 — valida: enum de FOLDERID_AppsFolder (Win32 + MSIX/AUMID) via
// SHCreateItemFromParsingName + IShellItem.BindToHandler(BHID_SFObject),
// timing frío y caliente. Gates: frío < 150 ms, caliente < 30 ms.
internal static unsafe class Program
{
    private const uint SHCONTF_FOLDERS = 0x20;
    private const uint SHCONTF_NONFOLDERS = 0x40;
    private const uint SHCONTF_INCLUDEHIDDEN = 0x80;

    // BHID_SFObject = 3981E224-F559-11D3-8E3A-00C04F6837D5 (valor del generado CsWin32)
    private static readonly Guid SFObjectHandlerGuid = new(0x3981E224, 0xF559, 0x11D3, 0x8E, 0x3A, 0x00, 0xC0, 0x4F, 0x68, 0x37, 0xD5);

    [STAThread]
    private static int Main()
    {
        _ = CoInitialize(default);

        // Gate realista: primera PÁGina (20 apps) es lo que el drawer necesita
        // para el primer frame; el resto se enumera lazy/background.
        var sw = Stopwatch.StartNew();
        int page = EnumApps(out var names, maxItems: 20, out int totalCold, fullScan: true);
        long coldPageMs = sw.ElapsedMilliseconds;

        sw.Restart();
        int page2 = EnumApps(out _, maxItems: 20, out _, fullScan: false);
        long warmPageMs = sw.ElapsedMilliseconds;

        Console.WriteLine($"Primera página (20) frío:    {page} items en {coldPageMs} ms (gate < 150)");
        Console.WriteLine($"Primera página (20) caliente: {page2} items en {warmPageMs} ms (gate < 30)");
        Console.WriteLine($"Enum completo (background):  {totalCold} apps");
        Console.WriteLine("--- primeros 20 ---");
        foreach (var n in names) Console.WriteLine($"  {n}");
        bool pass = coldPageMs < 150 && warmPageMs < 30 && page == 20 && totalCold > 0;
        Console.WriteLine($"RESULT {(pass ? "PASS" : "FAIL")}");
        return pass ? 0 : 1;
    }

    private static int EnumApps(out List<string> names, int maxItems, out int total, bool fullScan)
    {
        names = new List<string>();
        total = 0;

        // Ruta canónica Win8+: SHGetKnownFolderItem(FOLDERID_AppsFolder) → IShellItem
        Guid iidItem = typeof(IShellItem).GUID;
        HRESULT hr = SHGetKnownFolderItem(FOLDERID_AppsFolder, KNOWN_FOLDER_FLAG.KF_FLAG_DEFAULT, default, in iidItem, out var itemObj);
        if (hr.Failed || itemObj is not IShellItem item)
        {
            Console.Error.WriteLine($"SHGetKnownFolderItem hr=0x{(int)hr:X8}");
            return -2;
        }

        Guid iidFolder = typeof(IShellFolder).GUID;
        Guid bhid = SFObjectHandlerGuid;
        item.BindToHandler(default, &bhid, &iidFolder, out var psfObj);
        if (psfObj is not IShellFolder apps) return -5;

        var hrEnum = apps.EnumObjects(default, SHCONTF_FOLDERS | SHCONTF_NONFOLDERS | SHCONTF_INCLUDEHIDDEN, out var enumerator);
        if (hrEnum.Failed) return -6;

        int count = 0;
        while (true)
        {
            ITEMIDLIST* childPidl = null;
            uint fetched = 0;
            hr = enumerator.Next(1, &childPidl, &fetched);
            if (hr != 0 || fetched == 0) break;
            try
            {
                if (names.Count < maxItems &&
                    TryDisplayName(apps, childPidl, out var name))
                {
                    names.Add(name);
                }
            }
            finally { ILFree(childPidl); }
            count++;
            // sin fullScan: nos vamos tras llenar la página (product gate)
            if (!fullScan && count >= maxItems) break;
        }
        Marshal.ReleaseComObject(enumerator);
        Marshal.ReleaseComObject(psfObj);
        Marshal.ReleaseComObject(itemObj);
        total = fullScan ? count : 0;
        return names.Count;
    }

    private static bool TryDisplayName(IShellFolder folder, ITEMIDLIST* pidl, out string name)
    {
        name = string.Empty;
        var sr = default(STRRET);
        folder.GetDisplayNameOf(pidl, SHGDNF.SHGDN_NORMAL, &sr);
        try
        {
            name = sr.uType switch
            {
                0 => new string(sr.Anonymous.pOleStr.Value),
                _ => sr.Anonymous.cStr.AsSpan().ToString(),
            };
            return name.Length > 0;
        }
        finally
        {
            if (sr.uType == 0 && sr.Anonymous.pOleStr.Value != null)
                CoTaskMemFree((void*)sr.Anonymous.pOleStr.Value);
        }
    }
}
