using System.Runtime.InteropServices;

namespace TechBench.ServerManager;

internal static class ShortcutManager
{
    public static void Create(AppPaths paths)
    {
        if (!File.Exists(paths.ManagerExecutable))
            throw new FileNotFoundException("The compiled Server Manager executable is missing.", paths.ManagerExecutable);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.ShortcutPath)!);
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Script Host shortcut support is unavailable.");
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType)!;
            shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, [paths.ShortcutPath]);
            var shortcutType = shortcut!.GetType();
            shortcutType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, [paths.ManagerExecutable]);
            shortcutType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, [paths.ManagerDirectory]);
            shortcutType.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, shortcut, [$"{paths.ManagerExecutable},0"]);
            shortcutType.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcut, ["Manage and update the TechBench Sync Service"]);
            shortcutType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }
}
