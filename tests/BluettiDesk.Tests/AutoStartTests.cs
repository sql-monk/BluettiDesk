using Microsoft.Win32;

namespace BluettiDesk.Tests;

/// <summary>Works on a scratch key, so running the tests never changes what actually starts with Windows.</summary>
public sealed class AutoStartTests : IDisposable
{
    private const string ValueName = "BluettiDesk";
    private readonly string _keyPath = $@"Software\BluettiDeskTests\{Guid.NewGuid():N}";
    private readonly string _folder = Directory.CreateTempSubdirectory("bluettidesk-tests-").FullName;

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\BluettiDeskTests", throwOnMissingSubKey: false);
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void IsOffUntilTurnedOn()
    {
        Assert.False(AutoStart(@"C:\apps\BluettiDesk.exe").IsEnabled);
    }

    [Fact]
    public void StartsInTheBackgroundEvenFromAPathWithSpaces()
    {
        var autoStart = AutoStart(@"C:\My Apps\BluettiDesk.exe");

        autoStart.SetEnabled(true);

        Assert.True(autoStart.IsEnabled);
        Assert.Equal("\"C:\\My Apps\\BluettiDesk.exe\" --background", Stored());
    }

    [Fact]
    public void TurningOffRemovesTheEntry()
    {
        var autoStart = AutoStart(@"C:\apps\BluettiDesk.exe");
        autoStart.SetEnabled(true);

        autoStart.SetEnabled(false);

        Assert.False(autoStart.IsEnabled);
        Assert.Null(Stored());
    }

    [Fact]
    public void TurningOffTwiceIsHarmless()
    {
        var autoStart = AutoStart(@"C:\apps\BluettiDesk.exe");

        autoStart.SetEnabled(false);
        autoStart.SetEnabled(false);

        Assert.False(autoStart.IsEnabled);
    }

    [Fact]
    public void AnEntryPointingAtAMovedExeIsRepaired()
    {
        AutoStart(Path.Combine(_folder, "gone", "BluettiDesk.exe")).SetEnabled(true);
        var moved = Path.Combine(_folder, "BluettiDesk.exe");
        File.WriteAllText(moved, string.Empty);

        AutoStart(moved).RepairIfMoved();

        Assert.Equal($"\"{moved}\" --background", Stored());
    }

    [Fact]
    public void AnEntryPointingAtAnExistingExeIsLeftAlone()
    {
        var installed = Path.Combine(_folder, "BluettiDesk.exe");
        File.WriteAllText(installed, string.Empty);
        AutoStart(installed).SetEnabled(true);

        AutoStart(@"C:\repos\debug\BluettiDesk.exe").RepairIfMoved();

        Assert.Equal($"\"{installed}\" --background", Stored());
    }

    [Fact]
    public void RepairDoesNotTurnItOn()
    {
        var autoStart = AutoStart(Path.Combine(_folder, "BluettiDesk.exe"));

        autoStart.RepairIfMoved();

        Assert.False(autoStart.IsEnabled);
    }

    private RegistryAutoStart AutoStart(string executable) => new(_keyPath, ValueName, executable);

    private string? Stored()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
        return key?.GetValue(ValueName) as string;
    }
}
