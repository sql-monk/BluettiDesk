using System.Windows;
using System.Windows.Forms;
using BluettiDesk.Ui;
using DrawingIcon = System.Drawing.Icon;

namespace BluettiDesk;

/// <summary>The app's presence while the window is closed: it keeps logging, and this is the way back in or out.</summary>
internal sealed class TrayIcon : IDisposable
{
    private const int MaxTooltipLength = 120;

    private readonly MainViewModel _viewModel;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _connection;
    private bool _hintShown;

    public TrayIcon(MainViewModel viewModel, Action open, Action exit)
    {
        _viewModel = viewModel;
        _connection = new ToolStripMenuItem(viewModel.ConnectionButtonText, null, (_, _) => viewModel.ToggleConnectionCommand.Execute(null));

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Відкрити", null, (_, _) => open()));
        menu.Items.Add(_connection);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Вихід", null, (_, _) => exit()));

        var resource = System.Windows.Application.GetResourceStream(new Uri("Assets/app.ico", UriKind.Relative));
        _icon = new NotifyIcon
        {
            Icon = new DrawingIcon(resource.Stream, SystemInformation.SmallIconSize),
            Text = "BluettiDesk",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => open();
        _icon.BalloonTipClicked += (_, _) => open();
        viewModel.PropertyChanged += (_, _) => Refresh();
        viewModel.AlertRaised += alert => _icon.ShowBalloonTip(10000, alert.Title, alert.Detail.Length > 0 ? alert.Detail : " ", ToolTipIcon.Warning);
        if (viewModel.Outages is { } outages)
        {
            outages.CheckRequired += () => _icon.ShowBalloonTip(
                10000,
                "Графік відключень не оновлюється",
                "Сайт ДТЕК просить пройти перевірку. Відкрийте вкладку «Відключення» і натисніть «Пройти перевірку».",
                ToolTipIcon.Info);
        }
        Refresh();
    }

    /// <summary>Explains once per run why closing the window did not end the app.</summary>
    public void ShowHiddenHint()
    {
        if (!_hintShown)
        {
            _hintShown = true;
            _icon.ShowBalloonTip(5000, "BluettiDesk працює далі", "Журнал ведеться у фоні. Вийти можна через меню цього значка.", ToolTipIcon.Info);
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    private void Refresh()
    {
        _connection.Text = _viewModel.ConnectionButtonText;
        var text = _viewModel.TrayText;
        _icon.Text = text.Length <= MaxTooltipLength ? text : text[..MaxTooltipLength];
    }
}
