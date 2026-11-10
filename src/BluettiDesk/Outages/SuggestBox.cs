using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Data;
using System.Windows.Input;
using BluettiDesk.Core.Outages;

namespace BluettiDesk.Outages;

/// <summary>
/// A text box that offers matching options as you type, like the address fields on the DTEK site: any part of a name
/// finds it, arrows move through the list, Enter or a click takes an option.
/// </summary>
public sealed class SuggestBox : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(SuggestBox),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((SuggestBox)d).OnTextChanged()));

    public static readonly DependencyProperty OptionsProperty = DependencyProperty.Register(
        nameof(Options), typeof(IEnumerable), typeof(SuggestBox), new PropertyMetadata(null));

    private readonly TextBox _input = new() { Padding = new Thickness(8, 5, 8, 5) };
    private readonly ListBox _list = new() { MaxHeight = 280, MinWidth = 240, BorderThickness = new Thickness(0) };
    private readonly Popup _popup;
    private bool _accepting;

    public SuggestBox()
    {
        _input.SetBinding(TextBox.TextProperty, new Binding(nameof(Text)) { Source = this, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        _input.SetBinding(AutomationProperties.NameProperty, new Binding { Source = this, Path = new PropertyPath(AutomationProperties.NameProperty) });
        _popup = new Popup
        {
            PlacementTarget = _input,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(2),
                Child = _list,
            },
        };
        ((Border)_popup.Child).SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        ((Border)_popup.Child).SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");

        _input.PreviewKeyDown += OnKeyDown;
        _input.GotKeyboardFocus += (_, _) => { if (_input.Text.Length == 0) Suggest(); };
        _list.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(_list, (DependencyObject)e.OriginalSource) is ListBoxItem item)
            {
                Accept((string)item.Content);
            }
        };

        var root = new Grid();
        root.Children.Add(_input);
        root.Children.Add(_popup);
        Content = root;
    }

    /// <summary>An option was taken from the list.</summary>
    public event Action<string>? Accepted;

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    /// <summary>All the options; the box shows those matching what is typed.</summary>
    public IEnumerable? Options { get => (IEnumerable?)GetValue(OptionsProperty); set => SetValue(OptionsProperty, value); }

    public new bool Focus() => _input.Focus();

    private void OnTextChanged()
    {
        if (!_accepting && _input.IsKeyboardFocused)
        {
            Suggest();
        }
    }

    private void Suggest()
    {
        var options = Options?.Cast<string>() ?? [];
        var matches = StreetCatalog.Suggest(options, Text, 60);
        _list.ItemsSource = matches;
        _popup.MinWidth = _input.ActualWidth;
        _popup.IsOpen = matches.Count > 0 && !(matches.Count == 1 && matches[0] == Text);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when _popup.IsOpen:
                _list.SelectedIndex = Math.Min(_list.Items.Count - 1, _list.SelectedIndex + 1);
                _list.ScrollIntoView(_list.SelectedItem);
                e.Handled = true;
                break;
            case Key.Down:
                Suggest();
                e.Handled = true;
                break;
            case Key.Up when _popup.IsOpen:
                _list.SelectedIndex = Math.Max(0, _list.SelectedIndex - 1);
                _list.ScrollIntoView(_list.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter or Key.Tab when _popup.IsOpen && (_list.SelectedItem ?? (_list.Items.Count == 1 ? _list.Items[0] : null)) is string chosen:
                Accept(chosen);
                e.Handled = e.Key == Key.Enter;
                break;
            case Key.Escape when _popup.IsOpen:
                _popup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void Accept(string option)
    {
        _accepting = true;
        Text = option;
        _input.CaretIndex = option.Length;
        _accepting = false;
        _popup.IsOpen = false;
        Accepted?.Invoke(option);
    }
}
