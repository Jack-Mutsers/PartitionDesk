using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PartitionDesk.Dialogs;

/// <summary>A small dialog whose fields are added in code, so every operation can share it.</summary>
public sealed class FormDialog : Window
{
    readonly StackPanel _fields = new() { Margin = new Thickness(20, 16, 20, 8) };
    readonly Button _ok;
    readonly List<Func<string?>> _validators = new();

    public FormDialog(Window owner, string title, string okText = "OK", bool danger = false)
    {
        Owner = owner;
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Surface");
        SetResourceReference(ForegroundProperty, "Text");

        _ok = new Button { Content = okText, IsDefault = true, Style = (Style)Application.Current.Resources["DialogButton"] };
        if (danger) { _ok.SetResourceReference(BackgroundProperty, "Danger"); _ok.Foreground = Brushes.White; }
        _ok.Click += (_, _) =>
        {
            foreach (var v in _validators)
            {
                var err = v();
                if (err != null) { MessageBox.Show(this, err, Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            }
            DialogResult = true;
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Style = (Style)Application.Current.Resources["DialogButton"] };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_ok);
        buttons.Children.Add(cancel);
        var footer = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(20, 12, 20, 12),
            Child = buttons,
        };
        footer.SetResourceReference(Border.BackgroundProperty, "Panel");
        footer.SetResourceReference(Border.BorderBrushProperty, "Line");
        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(_fields);
        Content = root;
    }

    public void AddValidator(Func<string?> validator) => _validators.Add(validator);

    public void AddNote(string text, bool warning = false)
    {
        var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        if (warning)
        {
            var note = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            note.SetResourceReference(TextBlock.ForegroundProperty, "DangerText");
            var box = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 12),
                Child = note,
            };
            box.SetResourceReference(Border.BackgroundProperty, "DangerBack");
            box.SetResourceReference(Border.BorderBrushProperty, "DangerBorder");
            _fields.Children.Add(box);
        }
        else _fields.Children.Add(tb);
    }

    void AddRow(string label, FrameworkElement control)
    {
        var caption = new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4) };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _fields.Children.Add(caption);
        control.Margin = new Thickness(0, 0, 0, 12);
        _fields.Children.Add(control);
    }

    public Func<string> AddText(string label, string value = "", int maxLength = 0)
    {
        var tb = new TextBox { Text = value, Padding = new Thickness(4, 3, 4, 3), MaxLength = maxLength };
        AddRow(label, tb);
        return () => tb.Text;
    }

    public Func<bool> AddCheck(string label, bool value)
    {
        var cb = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 0, 0, 10) };
        _fields.Children.Add(cb);
        return () => cb.IsChecked == true;
    }

    public Func<T> AddCombo<T>(string label, IEnumerable<T> items, T selected, Func<T, string>? text = null)
    {
        var list = items.ToList();
        var combo = new ComboBox { Padding = new Thickness(4, 3, 4, 3) };
        foreach (var item in list) combo.Items.Add(new ComboBoxItem { Content = text?.Invoke(item) ?? item?.ToString(), Tag = item });
        combo.SelectedIndex = Math.Max(0, list.IndexOf(selected));
        AddRow(label, combo);
        return () => (T)((ComboBoxItem)combo.SelectedItem).Tag!;
    }

    /// <summary>A size or position in MB with a slider; returns MB.</summary>
    public Func<ulong> AddMegabytes(string label, ulong value, ulong min, ulong max, Action<ulong>? changed = null)
    {
        var slider = new Slider { Minimum = min, Maximum = Math.Max(min, max), Value = value, IsSnapToTickEnabled = false };
        var box = new TextBox { Text = value.ToString(CultureInfo.InvariantCulture), Width = 110, Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(10, 0, 0, 0) };
        var unit = new TextBlock { Text = "MB", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        var range = new TextBlock
        {
            Text = $"{min:N0} – {max:N0} MB",
            FontSize = 11,
        };
        range.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        bool syncing = false;
        slider.ValueChanged += (_, _) =>
        {
            if (syncing) return;
            syncing = true;
            box.Text = ((ulong)Math.Round(slider.Value)).ToString(CultureInfo.InvariantCulture);
            syncing = false;
            changed?.Invoke((ulong)Math.Round(slider.Value));
        };
        box.TextChanged += (_, _) =>
        {
            if (syncing || !ulong.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return;
            syncing = true;
            slider.Value = Math.Clamp(v, min, max);
            syncing = false;
            changed?.Invoke(v);
        };
        var row = new DockPanel();
        DockPanel.SetDock(unit, Dock.Right);
        DockPanel.SetDock(box, Dock.Right);
        row.Children.Add(unit);
        row.Children.Add(box);
        row.Children.Add(slider);
        var panel = new StackPanel();
        panel.Children.Add(row);
        panel.Children.Add(range);
        AddRow(label, panel);

        AddValidator(() => ulong.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= min && v <= max
            ? null : $"{label}: enter a value between {min:N0} and {max:N0} MB.");
        return () => ulong.Parse(box.Text, CultureInfo.InvariantCulture);
    }

    /// <summary>Makes the user type a word before the dangerous action is enabled.</summary>
    public void RequireTyping(string word)
    {
        var tb = new TextBox { Padding = new Thickness(4, 3, 4, 3) };
        AddRow($"Type {word} to confirm", tb);
        _ok.IsEnabled = false;
        tb.TextChanged += (_, _) => _ok.IsEnabled = tb.Text.Trim() == word;
    }

    public bool Run() => ShowDialog() == true;
}
