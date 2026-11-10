using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Interop;
using BluettiDesk.Core.Outages;
using Microsoft.Web.WebView2.Core;

namespace BluettiDesk.Outages;

public enum SiteState
{
    Idle,
    Working,
    Offline,
    CheckRequired,
    Error,
}

/// <summary>
/// Asks the DTEK site, one question at a time, through its real page in WebView2. The site is behind Imperva, which
/// lets only a browser in; when it wants a person to pass a check, this says so and waits for the user to do it in the
/// site window. It never tries to pass the check itself.
/// </summary>
public sealed class DtekSiteReader : IDisposable
{
    private const int TicksBeforeCheck = 16;
    private const string ReadyScript =
        "(typeof DisconSchedule !== 'undefined' && !!DisconSchedule.fact && typeof jQuery !== 'undefined' && document.readyState === 'complete') ? 'yes' : 'no'";
    private const string StreetsScript =
        "(typeof DisconSchedule !== 'undefined' && DisconSchedule.streets) ? JSON.stringify(DisconSchedule.streets) : null";

    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan InitTimeout = TimeSpan.FromSeconds(30);

    private readonly string _profileFolder;
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly string _extractScript = ReadResource("extract.js");
    private readonly string _housesScript = ReadResource("houses.js");
    private DtekSiteWindow? _window;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _core;
    private Task<bool>? _init;
    private string? _readyUrl;
    private bool _checkShown;
    private bool _disposed;

    public DtekSiteReader(string profileFolder) => _profileFolder = profileFolder;

    public event Action? StateChanged;

    public SiteState State { get; private set; }

    public string? Message { get; private set; }

    /// <summary>Reloads the page and asks for the schedule of <paramref name="address"/>.</summary>
    /// <returns>The result of <c>extract.js</c> in its "done" state, or null with <see cref="State"/> saying why.</returns>
    public Task<string?> ReadScheduleAsync(DtekSite site, OutageAddress address) => Run(async () =>
    {
        if (!await LoadAsync(site.Url, reload: true))
        {
            return null;
        }

        var script = _extractScript.Replace(
            "/*ADDRESS*/",
            JsonSerializer.Serialize(new { city = address.City, street = address.Street, house = address.House }),
            StringComparison.Ordinal);
        return await AwaitAnswerAsync(script, "Сайт ДТЕК не дав графіка вчасно.");
    });

    /// <summary>The settlements and their streets that the site's address form offers.</summary>
    public Task<string?> ReadStreetsAsync(DtekSite site) => Run(async () =>
    {
        if (!await LoadAsync(site.Url, reload: false))
        {
            return null;
        }

        var streets = await RunScriptAsync(StreetsScript);
        SetState(streets is null ? SiteState.Error : SiteState.Idle, streets is null ? "На сторінці ДТЕК немає довідника адрес." : null);
        return streets;
    });

    /// <summary>The house numbers the site knows on one street. "-" means the whole street is in one queue.</summary>
    public Task<IReadOnlyList<string>?> ReadHousesAsync(DtekSite site, string city, string street) => Run(async () =>
    {
        if (!await LoadAsync(site.Url, reload: false))
        {
            return null;
        }

        var script = _housesScript
            .Replace("/*CITY*/", JsonSerializer.Serialize(city), StringComparison.Ordinal)
            .Replace("/*STREET*/", JsonSerializer.Serialize(street), StringComparison.Ordinal);
        var answer = await AwaitAnswerAsync(script, "Сайт ДТЕК не дав номерів будинків вчасно.");
        if (answer is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(answer);
        return (IReadOnlyList<string>?)document.RootElement.GetProperty("houses").EnumerateArray()
            .Select(h => h.GetString() ?? string.Empty)
            .Where(h => h.Length > 0)
            .ToList();
    });

    /// <summary>
    /// Brings the site forward so that the user can pass its check. The check is not drawn on a hidden page, so the
    /// page is loaded again once it shows. Completes when the schedule page is there or the user closes the window.
    /// </summary>
    public async Task<bool> ShowCheckAsync(DtekSite site)
    {
        if (!await EnsureInitAsync())
        {
            return false;
        }

        _checkShown = true;
        _window!.ShowWithLabel("Сайт ДТЕК просить пройти перевірку. Виконайте її тут, далі вікно закриється саме.");
        return await Run(() => LoadAsync(site.Url, reload: true));
    }

    /// <summary>Shows the site as it is, for the user to look at.</summary>
    public async Task OpenSiteAsync(DtekSite site)
    {
        if (!await EnsureInitAsync())
        {
            return;
        }

        _window!.ShowWithLabel(site.Name);
        if (!_core!.Source.StartsWith("http", StringComparison.Ordinal))
        {
            _core.Navigate(site.Url);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _controller?.Close();
        if (_window is { } window)
        {
            window.AllowClose = true;
            window.Close();
        }
    }

    private static string ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private async Task<T> Run<T>(Func<Task<T>> work)
    {
        await _turn.WaitAsync();
        try
        {
            return await work();
        }
        finally
        {
            _turn.Release();
        }
    }

    private Task<bool> EnsureInitAsync() => _init ??= InitAsync();

    private async Task<bool> InitAsync()
    {
        try
        {
            _window = new DtekSiteWindow();
            _window.Dismissed += () => _checkShown = false;
            _window.ReloadRequested += () => _core?.Reload();
            _window.PageAreaChanged += PlacePage;

            // The page lives in a window that is never shown unless needed. A controller on its handle works while it
            // is hidden, unlike the WebView2 control, which waits for the window to appear.
            var handle = new WindowInteropHelper(_window).EnsureHandle();
            var environment = await CoreWebView2Environment.CreateAsync(null, _profileFolder);
            var created = environment.CreateCoreWebView2ControllerAsync(handle);
            if (await Task.WhenAny(created, Task.Delay(InitTimeout)) != created)
            {
                SetState(SiteState.Error, "Вбудований браузер WebView2 не запустився.");
                return false;
            }

            _controller = await created;
            _controller.IsVisible = false;
            _core = _controller.CoreWebView2;
            _core.Settings.AreDevToolsEnabled = false;
            _core.Settings.IsStatusBarEnabled = false;
            return true;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            SetState(SiteState.Error, "Для графіка відключень потрібен Microsoft Edge WebView2 Runtime. Встановіть його із сайту Microsoft.");
            return false;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            SetState(SiteState.Error, $"Вбудований браузер WebView2 не запустився: {ex.Message}");
            return false;
        }
    }

    /// <summary>Makes sure the schedule page of <paramref name="url"/> is loaded and its scripts are ready.</summary>
    private async Task<bool> LoadAsync(string url, bool reload)
    {
        if (_disposed || !await EnsureInitAsync())
        {
            return false;
        }

        if (!reload && _readyUrl == url && await IsReadyAsync())
        {
            return true;
        }

        _readyUrl = null;
        SetState(SiteState.Working, null);
        var core = _core!;
        var navigation = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e) => navigation.TrySetResult(e);
        core.NavigationCompleted += OnCompleted;
        try
        {
            core.Navigate(url);
            if (await Task.WhenAny(navigation.Task, Task.Delay(NavigationTimeout)) != navigation.Task)
            {
                SetState(SiteState.Offline, "Сайт ДТЕК не відповідає.");
                return false;
            }
        }
        finally
        {
            core.NavigationCompleted -= OnCompleted;
        }

        // Status 0 means no answer from a server at all. A page with an error code may be the check, so it is watched on.
        var result = navigation.Task.Result;
        if (!result.IsSuccess && result.HttpStatusCode == 0 && result.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled && !_checkShown)
        {
            SetState(SiteState.Offline, "Немає зв'язку із сайтом ДТЕК.");
            return false;
        }

        for (var tick = 0; !_disposed; tick++)
        {
            if (await IsReadyAsync())
            {
                _readyUrl = url;
                if (_checkShown)
                {
                    _checkShown = false;
                    _window!.Hide();
                }

                return true;
            }

            if (!_checkShown && tick >= TicksBeforeCheck)
            {
                SetState(SiteState.CheckRequired, "Сайт ДТЕК просить пройти перевірку, що відвідувач не робот. Її проходить людина.");
                return false;
            }

            await Task.Delay(Tick);
        }

        return false;
    }

    private async Task<string?> AwaitAnswerAsync(string script, string timeoutMessage)
    {
        var deadline = DateTime.UtcNow + AnswerTimeout;
        while (DateTime.UtcNow < deadline && !_disposed)
        {
            if (await RunScriptAsync(script) is { } answer)
            {
                using var document = JsonDocument.Parse(answer);
                var state = document.RootElement.TryGetProperty("state", out var s) ? s.GetString() : null;
                if (state == "done")
                {
                    SetState(SiteState.Idle, null);
                    return answer;
                }

                if (state == "error")
                {
                    // The page keeps its answer; the next question starts from a fresh one.
                    _readyUrl = null;
                    SetState(SiteState.Error, document.RootElement.TryGetProperty("message", out var m) ? m.GetString() : "Сайт ДТЕК повернув помилку.");
                    return null;
                }
            }

            await Task.Delay(Tick);
        }

        _readyUrl = null;
        SetState(SiteState.Error, timeoutMessage);
        return null;
    }

    private async Task<bool> IsReadyAsync() => await RunScriptAsync(ReadyScript) == "yes";

    /// <summary>Runs a script whose last value is a string; null when there is none or the page is changing.</summary>
    private async Task<string?> RunScriptAsync(string script)
    {
        try
        {
            var raw = await _core!.ExecuteScriptAsync(script);
            var value = JsonSerializer.Deserialize<string?>(raw);
            return string.IsNullOrEmpty(value) || value == "null" ? null : value;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Puts the page over the window's page area while the window shows, and hides it otherwise.</summary>
    private void PlacePage()
    {
        if (_controller is null || _window is null)
        {
            return;
        }

        var visible = _window.IsVisible;
        if (visible)
        {
            _controller.Bounds = _window.PageArea;
        }

        _controller.IsVisible = visible;
    }

    private void SetState(SiteState state, string? message)
    {
        if (State != state || Message != message)
        {
            State = state;
            Message = message;
            StateChanged?.Invoke();
        }
    }
}
