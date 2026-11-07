using System.ComponentModel;
using System.Windows;

namespace BluettiDesk;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    public event Action? HiddenToTray;

    /// <summary>Set when the app is really ending; until then, closing the window only hides it.</summary>
    public bool AllowClose { get; set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke();
        }

        base.OnClosing(e);
    }
}
