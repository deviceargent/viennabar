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

    // =============== M2 IFEO (solo codigo: el apply vivo requiere admin) ===============

    internal static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    internal static string IfeoKeyPath(string ifeoRoot) => $"{ifeoRoot}\\explorer.exe";

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string link, string target, nint reserved);

    // Aplica el redirector IFEO. El passthrough usa un hardlink (mismo nombre
    // distinto = IFEO no dispara) creado al lado del launcher: sin el, un
    // switch desconocido entraria en loop, asi que sin link NO se escribe IFEO.
    // backupRoot vive en HKCU (write-once, leccion M1). Devuelve resumen.
    internal static string ApplyM2(RegistryKey hklm, string ifeoRoot, RegistryKey hkcu, string backupRoot,
        string launcherPath, string linkPath, string linkTarget, string rescueRegPath, bool requireAdmin = true)
    {
        if (requireAdmin && !IsElevated()) return "M2: se necesita terminal elevada (admin)";
        if (!System.IO.File.Exists(launcherPath))
            return $"M2: launcher no encontrado en {launcherPath} (sin cambios)";
        using var existing = hkcu.OpenSubKey(backupRoot, false);
        bool haveBackup = existing?.GetValue("HadKey") is not null;   // backup plano (sin subclaves)
        using var cur = hklm.OpenSubKey(IfeoKeyPath(ifeoRoot), false);
        bool hadKey = cur is not null;
        string? oldDebugger = cur?.GetValue("Debugger") as string;

        // hardlink idempotente (si falta se crea y se registra propiedad)
        bool createdLink = false;
        if (!System.IO.File.Exists(linkPath))
        {
            int linkErr = 0;
            try
            {
                createdLink = CreateHardLinkW(linkPath, linkTarget, 0);
                if (!createdLink) linkErr = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            }
            catch { createdLink = false; linkErr = -1; }
            if (!createdLink) return $"M2: no se pudo crear el hardlink {linkPath} (win32={linkErr}, sin cambios)";
        }

        if (!haveBackup)
        {
            using var bk = hkcu.CreateSubKey(backupRoot);
            if (bk is not null)
            {
                bk.SetValue("HadKey", hadKey ? 1 : 0, RegistryValueKind.DWord);
                if (oldDebugger is not null) bk.SetValue("Debugger", oldDebugger);
                else try { bk.DeleteValue("Debugger", false); } catch { }
                bk.SetValue("LinkCreated", createdLink ? 1 : 0, RegistryValueKind.DWord);
                bk.SetValue("LinkPath", linkPath);
            }
            try
            {
                System.IO.File.WriteAllText(rescueRegPath,
                    BuildRescueRegM2("HKEY_LOCAL_MACHINE", ifeoRoot, hadKey, oldDebugger));
            }
            catch { }
        }
        else if (createdLink)
        {
            // re-apply que si creo el link: actualizar solo propiedad
            try
            {
                using var bk = hkcu.OpenSubKey(backupRoot, true);
                bk?.SetValue("LinkCreated", 1, RegistryValueKind.DWord);
                bk?.SetValue("LinkPath", linkPath);
            }
            catch { }
        }

        using var key = hklm.CreateSubKey(IfeoKeyPath(ifeoRoot));
        if (key is null) return "M2: no se pudo escribir IFEO (sin cambios)";
        key.SetValue("Debugger", $"\"{launcherPath}\"");
        string kept = haveBackup ? " (backup original conservado)" : "";
        return $"M2 aplicado (IFEO explorer.exe -> launcher){kept}";
    }

    internal static string RevertM2(RegistryKey hklm, string ifeoRoot, RegistryKey hkcu, string backupRoot)
    {
        using var bk = hkcu.OpenSubKey(backupRoot, false);
        if (bk is null) return "M2: sin backup, nada que revertir";
        bool hadKey = (bk.GetValue("HadKey") as int?) == 1;
        string? oldDebugger = bk.GetValue("Debugger") as string;
        bool linkCreated = (bk.GetValue("LinkCreated") as int?) == 1;
        string? linkPath = bk.GetValue("LinkPath") as string;
        if (hadKey)
        {
            using var key = hklm.CreateSubKey(IfeoKeyPath(ifeoRoot));
            if (key is not null)
            {
                if (oldDebugger is not null) key.SetValue("Debugger", oldDebugger);
                else try { key.DeleteValue("Debugger", false); } catch { }
            }
        }
        else
        {
            using var key = hklm.OpenSubKey(IfeoKeyPath(ifeoRoot), true);
            if (key is not null)
            {
                try { key.DeleteValue("Debugger", false); } catch { }
                if (key.SubKeyCount == 0 && key.ValueCount == 0)
                    try { hklm.DeleteSubKey(IfeoKeyPath(ifeoRoot), false); } catch { }
            }
        }
        if (linkCreated && linkPath is not null)
            try { System.IO.File.Delete(linkPath); } catch { }
        try { hkcu.DeleteSubKeyTree(backupRoot, false); } catch { }
        return "M2 revertido (IFEO restaurado)";
    }

    internal static string BuildRescueRegM2(string hivePrefix, string ifeoRoot, bool hadKey, string? oldDebugger)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Windows Registry Editor Version 5.00");
        sb.AppendLine("; ViennaBar M2 revert — aplicar como ADMIN para restaurar IFEO");
        sb.AppendLine();
        string key = $"{hivePrefix}\\{IfeoKeyPath(ifeoRoot)}";
        if (!hadKey) { sb.AppendLine($"[-{key}]"); return sb.ToString(); }
        sb.AppendLine($"[{key}]");
        sb.AppendLine(oldDebugger is not null
            ? $"\"Debugger\"=\"{RegEscape(oldDebugger)}\"" : "\"Debugger\"=-");
        return sb.ToString();
    }
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
