using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace BluettiDesk.Outages;

/// <summary>
/// Holds the DTEK page. It stays hidden while the app reads the schedule, and comes forward only when the site asks a
/// person to pass its check, or when the user opens the site. The page is a WebView2 controller placed over
/// <see cref="Host"/> by <see cref="DtekSiteReader"/>.
/// </summary>
public partial class DtekSiteWindow : Window
{
    public DtekSiteWindow()
    {
        InitializeComponent();
        Host.SizeChanged += (_, _) => PageAreaChanged?.Invoke();
        IsVisibleChanged += (_, _) => PageAreaChanged?.Invoke();
    }

    /// <summary>The user closed or hid the window.</summary>
    public event Action? Dismissed;

    public event Action? ReloadRequested;

    /// <summary>The area for the page moved, resized, appeared or disappeared.</summary>
    public event Action? PageAreaChanged;

    public bool AllowClose { get; set; }

    /// <summary>Where the page goes, in device pixels of the window's client area.</summary>
    public System.Drawing.Rectangle PageArea
    {
        get
        {
            if (!Host.IsLoaded)
            {
                return System.Drawing.Rectangle.Empty;
            }

            var dpi = VisualTreeHelper.GetDpi(this);
            var origin = Host.TranslatePoint(new Point(0, 0), this);
            return new System.Drawing.Rectangle(
                (int)Math.Round(origin.X * dpi.DpiScaleX),
                (int)Math.Round(origin.Y * dpi.DpiScaleY),
                (int)Math.Round(Host.ActualWidth * dpi.DpiScaleX),
                (int)Math.Round(Host.ActualHeight * dpi.DpiScaleY));
        }
    }

    public void ShowWithLabel(string label)
    {
        Label.Text = label;
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        PageAreaChanged?.Invoke();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
            Dismissed?.Invoke();
        }

        base.OnClosing(e);
    }

    private void OnReload(object sender, RoutedEventArgs e) => ReloadRequested?.Invoke();

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Hide();
        Dismissed?.Invoke();
    }
}
