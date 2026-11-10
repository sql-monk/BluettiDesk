using System.IO;
using System.Windows;
using BluettiDesk.Ble;
using BluettiDesk.Core.Journal;
using BluettiDesk.Core.Outages;
using BluettiDesk.Core.Station;
using BluettiDesk.Outages;
using BluettiDesk.Ui;
using Microsoft.Win32;

namespace BluettiDesk;

public partial class App : Application
{
    private const string PersonalizeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string InstanceName = @"Local\BluettiDesk";
    private const string ExitArgument = "--exit";
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(3);

    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BluettiDesk");

    private ResourceDictionary? _palette;
    private Mutex? _singleInstance;
    private EventWaitHandle? _showRequest;
    private EventWaitHandle? _exitRequest;
    private StationMonitor? _monitor;
    private StationJournal? _journal;
    private OutageScheduleStore? _outageStore;
    private DtekSiteReader? _dtekSite;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The station accepts one Bluetooth client, so a second copy would only fight the first.
        _singleInstance = new Mutex(true, InstanceName, out var isFirst);
        _showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        _exitRequest = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Exit");
        var wantsExit = e.Args.Contains(ExitArgument);
        if (!isFirst || wantsExit)
        {
            if (!isFirst)
            {
                (wantsExit ? _exitRequest : _showRequest).Set();
            }

            Shutdown();
            return;
        }

        ThreadPool.RegisterWaitForSingleObject(_showRequest, (_, _) => Dispatcher.BeginInvoke(ShowWindow), null, Timeout.Infinite, false);
        ThreadPool.RegisterWaitForSingleObject(_exitRequest, (_, _) => Dispatcher.BeginInvoke(ExitApp), null, Timeout.Infinite, false);

        ApplyPalette();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SessionEnding += (_, _) => ReleaseEverything();

        var autoStart = RegistryAutoStart.ForThisUser(Environment.ProcessPath!);
        autoStart.RepairIfMoved();

        string? journalError = null;
        try
        {
            _journal = new StationJournal(Path.Combine(DataFolder, "journal.db"));
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            journalError = ex.Message;
        }

        _monitor = new StationMonitor(new WinRtLinkFactory());
        if (_journal is { } journal)
        {
            _monitor.SweepCompleted += journal.Record;
        }

        // Outage schedules go into the same file, next to what the station measured.
        if (_journal is not null)
        {
            try
            {
                _outageStore = new OutageScheduleStore(_journal.FilePath);
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
            {
            }
        }

        var outageSettingsPath = Path.Combine(DataFolder, "outages.json");
        _dtekSite = new DtekSiteReader(Path.Combine(DataFolder, "WebView2"));
        var outages = new OutageViewModel(
            OutageSettings.Load(outageSettingsPath), outageSettingsPath, DataFolder, _dtekSite, _outageStore, _journal, Dispatcher);

        var viewModel = new MainViewModel(_monitor, Dispatcher, Confirm, autoStart, _journal, AskExportPath, journalError, outages);
        _window = new MainWindow { DataContext = viewModel };
        _tray = new TrayIcon(viewModel, ShowWindow, ExitApp);
        _window.HiddenToTray += () => _tray.ShowHiddenHint();
        viewModel.Start();
        outages.Start();

        if (!e.Args.Contains(RegistryAutoStart.BackgroundArgument))
        {
            ShowWindow();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _showRequest?.Dispose();
        _exitRequest?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static string? AskExportPath(string suggestedName)
    {
        var dialog = new SaveFileDialog
        {
            FileName = suggestedName,
            Filter = "CSV (*.csv)|*.csv",
            DefaultExt = ".csv",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static bool Confirm(string question) =>
        MessageBox.Show(Current.MainWindow, question, "BluettiDesk", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void ShowWindow()
    {
        if (_window is null || _exiting)
        {
            return;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    private void ExitApp()
    {
        if (_exiting)
        {
            return;
        }

        ReleaseEverything();
        Shutdown();
    }

    /// <summary>Closes the Bluetooth link and the journal cleanly, whether the user chose to exit or Windows is shutting down.</summary>
    private void ReleaseEverything()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        if (_window is not null)
        {
            _window.AllowClose = true;
        }

        _tray?.Dispose();
        if (_monitor is { } monitor)
        {
            Task.Run(monitor.StopAsync).Wait(ExitTimeout);
        }

        _dtekSite?.Dispose();
        _outageStore?.Dispose();
        _journal?.Dispose();
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
        {
            Dispatcher.BeginInvoke(ApplyPalette);
        }
    }

    /// <summary>The Fluent theme follows Windows by itself; the chart and status colors are selected per mode here.</summary>
    private void ApplyPalette()
    {
        var useLight = Registry.GetValue(PersonalizeKey, "AppsUseLightTheme", 1) is not 0;
        var palette = new ResourceDictionary
        {
            Source = new Uri($"Ui/Palette.{(useLight ? "Light" : "Dark")}.xaml", UriKind.Relative),
        };

        if (_palette is not null)
        {
            Resources.MergedDictionaries.Remove(_palette);
        }

        Resources.MergedDictionaries.Add(palette);
        _palette = palette;
    }
}
