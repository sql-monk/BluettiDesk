using System.IO;
using Microsoft.Win32;

namespace BluettiDesk;

public interface IAutoStart
{
    bool IsEnabled { get; }

    void SetEnabled(bool enabled);
}

/// <summary>A value under the user's "Run" key: per-user, needs no admin rights, and shows up in Task Manager's Startup tab.</summary>
public sealed class RegistryAutoStart(string keyPath, string valueName, string executablePath) : IAutoStart
{
    public const string BackgroundArgument = "--background";

    public static RegistryAutoStart ForThisUser(string executablePath) =>
        new(@"Software\Microsoft\Windows\CurrentVersion\Run", "BluettiDesk", executablePath);

    private string Command => $"\"{executablePath}\" {BackgroundArgument}";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(valueName) is not null;
        }
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        if (enabled)
        {
            key.SetValue(valueName, Command);
        }
        else
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }

    /// <summary>After the exe has been moved, the entry would point at nothing. Run from the new place, it heals itself.</summary>
    public void RepairIfMoved()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        if (key?.GetValue(valueName) is string command && !File.Exists(ExecutableIn(command)))
        {
            SetEnabled(true);
        }
    }

    private static string ExecutableIn(string command)
    {
        var parts = command.Split('"', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[0] : string.Empty;
    }
}
