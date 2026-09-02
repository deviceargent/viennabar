using Shell = ViennaBar.ShellNative.ShellNative;

namespace ViennaBarTests;

// Test AOT del pipeline de launch paso a paso â€” bisect del 0xC0000005.
// InternalsVisibleTo llega por transitivity? No: lo agregamos al Shell csproj.
internal static class Program
{
    private static void Log(string s)
    {
        try { Console.Out.Flush(); } catch { }
        Console.WriteLine(s);
        try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vb-tests.log"), $"{DateTime.Now:HH:mm:ss.fff} {s}\n"); }
        catch { }
    }

    private static int Main(string[] args)
    {
        var stage = args.Length > 0 ? args[0] : "all";
        Log($"=== test begin stage={stage}");

        try
        {
            Log("stage1: OpenAppsFolder");
            var apps = Shell.OpenAppsFolder();
            Log($"stage1: apps={(apps != 0 ? "OK" : "FAIL")}");
            if (apps == 0) return 1;

            Log("stage2: EnumChildren");
            var children = Shell.EnumChildren(apps);
            Log($"stage2: {children.Count} children");
            if (children.Count == 0) return 2;

            // item seguro de lanzar: buscamos "Calculator" o el primero con .exe
            var target = children.FirstOrDefault(c => c.Name.Contains("Calculator", StringComparison.OrdinalIgnoreCase))
                ?? children.First(c => c.ParsingName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            Log($"stage3: target = {target?.Name} ({target?.ParsingName})");
            if (target is null) return 3;

            if (stage == "enum") { Log("=== enum-only done"); Shell.ReleaseFolder(apps); return 0; }

            if (stage == "cached")
            {
                // replica EXACTA del path del drawer: PIDLs desde catalog.cache
                var cachePath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ViennaBar", "catalog.cache");
                Log($"stageC: cache exists={System.IO.File.Exists(cachePath)}");
                if (!System.IO.File.Exists(cachePath)) return 21;
                using var br = new BinaryReader(System.IO.File.OpenRead(cachePath));
                int n = br.ReadInt32();
                Log($"stageC: {n} entries");
                var entries = new List<(string name, byte[] pidl)>();
                for (int i = 0; i < n; i++)
                {
                    string nm = br.ReadString();
                    string pr = br.ReadString();
                    int len = br.ReadInt32();
                    byte[] pidl = len > 0 ? br.ReadBytes(len) : Array.Empty<byte>();
                    entries.Add((nm, pidl));
                }
                var t = entries.FirstOrDefault(e => e.name.Contains("Calculator", StringComparison.OrdinalIgnoreCase));
                Log($"stageC: target={t.name} pidlLen={t.pidl?.Length ?? -1}");
                Shell.LaunchByPidl(apps, t.pidl);
                Log("stageC: launch by CACHED pidl returned sin crash");
                Shell.ReleaseFolder(apps);
                return 0;
            }

            // stage 4: pipeline LaunchByPidl completo
            Log("stage4: LaunchByPidl (esto lanza la app de verdad)");
            Shell.LaunchByPidl(apps, target.Pidl);
            Log("stage4: LaunchByPidl returned sin crash");

            Shell.ReleaseFolder(apps);
            Log("=== test done OK");
            return 0;
        }
        catch (Exception ex)
        {
            Log($"EXCEPTION: {ex}");
            return 99;
        }
    }
}
