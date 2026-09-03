using Microsoft.Win32;

namespace ViennaBar;

// F3/M1 — override de verbs en HKCU (sin admin, reversible).
// Todo recibe el root para test headless con sandbox key. El backup vive
// EN el registry (HKCU\Software\ViennaBar\M1Backup): round-trip de strings
// nativo, sin JSON (System.Text.Json reflection no es AOT-safe).
internal static class Integration
{
    internal static readonly string[] VerbClasses = ["Directory", "Drive", "Folder"];

    internal sealed record VerbBackup(string Cls, bool Existed, string? Command, string? DelegateExecute,
        string? ShellDefault);

    private static string KeyPath(string classesRoot, string cls) =>
        $"{classesRoot}\\{cls}\\shell\\open\\command";

    internal static List<VerbBackup> ReadVerbs(RegistryKey hive, string classesRoot)
    {
        var list = new List<VerbBackup>();
        foreach (var cls in VerbClasses)
        {
            using var key = hive.OpenSubKey(KeyPath(classesRoot, cls), false);
            using var shell = hive.OpenSubKey($"{classesRoot}\\{cls}\\shell", false);
            list.Add(new VerbBackup(cls, key is not null,
                key?.GetValue("") as string, key?.GetValue("DelegateExecute") as string,
                shell?.GetValue("") as string));
        }
        return list;
    }

    // aplica M1: backup + command="exe --open-folder %V" + borra DelegateExecute.
    // El backup es WRITE-ONCE: si ya existe (applys previos), se conserva el
    // original y NO se sobrescribe con el estado ya-overrideado.
    // Devuelve resumen para consola/tests.
    internal static string ApplyM1(RegistryKey hive, string classesRoot, string backupRoot,
        string exePath, string rescueRegPath)
    {
        using var existing = hive.OpenSubKey(backupRoot, false);
        bool haveBackup = existing is not null && existing.SubKeyCount > 0;
        var before = ReadVerbs(hive, classesRoot);
        if (!haveBackup)
        {
            // backup por clase
            foreach (var b in before)
            {
                using var bk = hive.CreateSubKey($"{backupRoot}\\{b.Cls}");
                if (bk is null) continue;
                bk.SetValue("Existed", b.Existed ? 1 : 0, RegistryValueKind.DWord);
                if (b.Command is not null) bk.SetValue("Command", b.Command);
                else try { bk.DeleteValue("Command", false); } catch { }
                if (b.DelegateExecute is not null) bk.SetValue("DelegateExecute", b.DelegateExecute);
                else try { bk.DeleteValue("DelegateExecute", false); } catch { }
                if (b.ShellDefault is not null) bk.SetValue("ShellDefault", b.ShellDefault);
                else try { bk.DeleteValue("ShellDefault", false); } catch { }
            }
            try { System.IO.File.WriteAllText(rescueRegPath, BuildRescueReg("HKEY_CURRENT_USER", classesRoot, before)); }
            catch { }
        }
        string cmd = $"\"{exePath}\" --open-folder \"%V\"";
        foreach (var cls in VerbClasses)
        {
            using var key = hive.CreateSubKey(KeyPath(classesRoot, cls));
            if (key is null) continue;
            key.SetValue("", cmd);
            try { key.DeleteValue("DelegateExecute", false); } catch { }
        }
        // "none" en Directory/Drive salta la resolucion de shell\open: el
        // default debe nombrar el verbo para que el override se consulte
        foreach (var cls in new[] { "Directory", "Drive" })
        {
            using var shell = hive.CreateSubKey($"{classesRoot}\\{cls}\\shell");
            shell?.SetValue("", "open");
        }
        string kept = haveBackup ? " (backup original conservado)" : "";
        return $"M1 aplicado ({VerbClasses.Length} verbs) backup={backupRoot} rescue={rescueRegPath}{kept}";
    }

    internal static string RevertM1(RegistryKey hive, string classesRoot, string backupRoot)
    {
        using var bkRoot = hive.OpenSubKey(backupRoot, false);
        if (bkRoot is null) return "M1: sin backup, nada que revertir";
        var names = new List<string>();
        foreach (var cls in VerbClasses)
        {
            using var bk = hive.OpenSubKey($"{backupRoot}\\{cls}", false);
            if (bk is null) continue;
            bool existed = (bk.GetValue("Existed") as int?) == 1;
            string? cmd = bk.GetValue("Command") as string;
            string? de = bk.GetValue("DelegateExecute") as string;
            string? shDef = bk.GetValue("ShellDefault") as string;
            names.Add(cls);
            // restaurar default de shell (solo Directory/Drive lo tocamos)
            if (cls == "Directory" || cls == "Drive")
            {
                using var shell = hive.CreateSubKey($"{classesRoot}\\{cls}\\shell");
                if (shell is not null)
                {
                    if (shDef is not null) shell.SetValue("", shDef);
                    else try { shell.DeleteValue("", false); } catch { }
                }
            }
            if (!existed)
            {
                // no existia: borrar lo que creamos + podar padres vacios
                try { hive.DeleteSubKeyTree(KeyPath(classesRoot, cls), false); } catch { }
                Prune(hive, $"{classesRoot}\\{cls}\\shell\\open");
                Prune(hive, $"{classesRoot}\\{cls}\\shell");
            }
            else
            {
                using var key = hive.CreateSubKey(KeyPath(classesRoot, cls));
                if (key is null) continue;
                if (cmd is not null) key.SetValue("", cmd);
                else try { key.DeleteValue("", false); } catch { }
                if (de is not null) key.SetValue("DelegateExecute", de);
                else try { key.DeleteValue("DelegateExecute", false); } catch { }
            }
        }
        try { hive.DeleteSubKeyTree(backupRoot, false); } catch { }
        return $"M1 revertido ({names.Count} verbs)";
    }

    private static void Prune(RegistryKey hive, string path)
    {
        try { hive.DeleteSubKey(path, false); } catch { }   // solo borra si esta vacia
    }

    // .reg de rescate manual (doble-click revierte sin la app). Puro: testeable.
    internal static string BuildRescueReg(string hivePrefix, string classesRoot, List<VerbBackup> backups)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Windows Registry Editor Version 5.00");
        sb.AppendLine("; ViennaBar M1 revert — doble-click para restaurar verbs originales");
        foreach (var b in backups)
        {
            sb.AppendLine();
            string shellKey = $"{hivePrefix}\\{classesRoot}\\{b.Cls}\\shell";
            string key = $"{hivePrefix}\\{KeyPath(classesRoot, b.Cls)}";
            if ((b.Cls == "Directory" || b.Cls == "Drive") && b.ShellDefault is not null)
            {
                sb.AppendLine($"[{shellKey}]");
                sb.AppendLine($"@=\"{RegEscape(b.ShellDefault)}\"");
                sb.AppendLine();
            }
            if (!b.Existed)
            {
                sb.AppendLine($"[-{key}]");
                continue;
            }
            sb.AppendLine($"[{key}]");
            sb.AppendLine(b.Command is not null ? $"@=\"{RegEscape(b.Command)}\"" : "@=-");
            sb.AppendLine(b.DelegateExecute is not null
                ? $"\"DelegateExecute\"=\"{RegEscape(b.DelegateExecute)}\"" : "\"DelegateExecute\"=-");
        }
        return sb.ToString();
    }

    private static string RegEscape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

internal static unsafe class SingleInstance
{
    private const uint WM_COPYDATA = 0x004A;

    // layout identico a COPYDATASTRUCT (ULONG_PTR/DWORD/PVOID en x64)
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct CopyDataMsg
    {
        public nuint dwData;
        public uint cbData;
        public void* lpData;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static extern uint SendMessageTimeoutRaw(void* hWnd, uint msg, nuint wParam, void* lParam,
        uint fuFlags, uint timeout, nuint* result);

    private static Mutex? _mutex;

    internal static bool Acquire()
    {
        try
        {
            _mutex = new Mutex(true, "ViennaBar_SingleInstance", out bool owned);
            return owned;
        }
        catch (AbandonedMutexException) { return true; }   // el dueño anterior murio
        catch { return true; }   // sin mutex: arrancar igual
    }

    // reenvia --open-folder a la instancia viva. true = entregado (salir).
    internal static bool ForwardOpenFolder(string path)
    {
        try
        {
            var hwnd = Windows.Win32.PInvoke.FindWindow("ViennaBarSidebar", null);
            if (hwnd.IsNull) return false;
            byte[] bytes = System.Text.Encoding.Unicode.GetBytes(path + '\0');
            fixed (byte* pb = bytes)
            {
                var cds = new CopyDataMsg { dwData = 1, cbData = (uint)bytes.Length, lpData = pb };
                nuint res = 0;
                _ = SendMessageTimeoutRaw((void*)(nint)hwnd.Value, WM_COPYDATA, 0, &cds, 2, 5000, &res);
            }
            return true;
        }
        catch { return false; }
    }
}
