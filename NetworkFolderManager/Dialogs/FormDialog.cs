using NetworkFolderManager.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Control = System.Windows.Controls.Control;
using Rectangle = System.Windows.Shapes.Rectangle;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Cursors = System.Windows.Input.Cursors;
using Orientation = System.Windows.Controls.Orientation;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace NetworkFolderManager.Dialogs;

public enum FieldKind { Text, Password, Check, Choice, Info }

/// <summary>Ein Eingabefeld für <see cref="FormDialog"/>.</summary>
public sealed class FormField
{
    public string Name { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public FieldKind Kind { get; init; } = FieldKind.Text;
    public string Value { get; init; } = string.Empty;
    public bool Checked { get; init; }
    public string? Hint { get; init; }
    public IReadOnlyList<(string Value, string Label)> Options { get; init; } = Array.Empty<(string, string)>();

    public static FormField Text(string name, string label, string value = "", string? hint = null) =>
        new() { Name = name, Label = label, Value = value, Hint = hint };

    public static FormField Password(string name, string label, string? hint = null) =>
        new() { Name = name, Label = label, Kind = FieldKind.Password, Hint = hint };

    public static FormField Check(string name, string label, bool value = false) =>
        new() { Name = name, Label = label, Kind = FieldKind.Check, Checked = value };

    public static FormField Choice(string name, string label, IReadOnlyList<(string, string)> options, string value = "") =>
        new() { Name = name, Label = label, Kind = FieldKind.Choice, Options = options, Value = value };
}

/// <summary>
/// Kleiner Formular-Dialog im noobnet-Design. <c>submit</c> wird beim Bestätigen ausgeführt (z.B. Serveraufruf);
/// wirft er eine Ausnahme, bleibt der Dialog offen und zeigt die Meldung an.
/// </summary>
public sealed class FormDialog : Window
{
    private readonly Dictionary<string, Control> _inputs = new();
    private readonly TextBlock _error;
    private readonly Button _ok;
    private readonly Func<IReadOnlyDictionary<string, string>, Task>? _submit;

    public IReadOnlyDictionary<string, string> Values { get; private set; } = new Dictionary<string, string>();

    private FormDialog(string title, string? subtitle, string? notice, IEnumerable<FormField> fields, string okText, bool danger,
        Func<IReadOnlyDictionary<string, string>, Task>? submit)
    {
        _submit = submit;
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0F));
        Foreground = (Brush)Application.Current.FindResource("Text");
        FontFamily = (FontFamily)Application.Current.FindResource("AppFont");

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        panel.Children.Add(new TextBlock { Text = title, Style = Res<Style>("Headline"), FontSize = 18, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(subtitle))
            panel.Children.Add(new TextBlock { Text = subtitle, Style = Res<Style>("DimText"), Margin = new Thickness(0, 0, 0, 14) });
        if (!string.IsNullOrEmpty(notice))
        {
            panel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0x9F, 0x0A)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 14),
                Child = new TextBlock { Text = notice, TextWrapping = TextWrapping.Wrap, FontSize = 12 }
            });
        }

        foreach (var f in fields)
        {
            switch (f.Kind)
            {
                case FieldKind.Check:
                    var cb = new CheckBox { Content = f.Label, IsChecked = f.Checked, Margin = new Thickness(0, 2, 0, 10) };
                    _inputs[f.Name] = cb;
                    panel.Children.Add(cb);
                    continue;
                case FieldKind.Info:
                    panel.Children.Add(new TextBlock { Text = f.Label, Style = Res<Style>("DimText"), Margin = new Thickness(0, 0, 0, 10) });
                    continue;
            }

            panel.Children.Add(new TextBlock { Text = f.Label, FontWeight = FontWeights.Medium, Margin = new Thickness(0, 0, 0, 4) });
            Control input = f.Kind switch
            {
                FieldKind.Password => new PasswordBox { PasswordChar = '●' },
                FieldKind.Choice => CreateCombo(f),
                _ => new TextBox { Text = f.Value }
            };
            input.Margin = new Thickness(0, 0, 0, f.Hint == null ? 12 : 4);
            _inputs[f.Name] = input;
            panel.Children.Add(input);
            if (f.Hint != null)
                panel.Children.Add(new TextBlock { Text = f.Hint, Style = Res<Style>("DimText"), FontSize = 11, Margin = new Thickness(2, 0, 0, 12) });
        }

        _error = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x61)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed };
        panel.Children.Add(_error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = Loc.T("Abbrechen"), IsCancel = true, Style = Res<Style>("GlassButton") };
        _ok = new Button { Content = okText, IsDefault = true };
        if (danger)
            _ok.Background = (Brush)Application.Current.FindResource("Danger");
        _ok.Click += async (_, _) => await ConfirmAsync();
        buttons.Children.Add(cancel);
        buttons.Children.Add(_ok);
        panel.Children.Add(buttons);

        var root = new Grid();
        root.Children.Add(new Rectangle { Height = 1, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(60, 0, 60, 0), Fill = Res<Brush>("ReflectionLine") });
        root.Children.Add(panel);
        Content = root;

        Loaded += (_, _) =>
        {
            var first = _inputs.Values.FirstOrDefault(c => c is TextBox or PasswordBox);
            first?.Focus();
            if (first is TextBox tb)
                tb.SelectAll();
        };
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
    }

    private static T Res<T>(string key) => (T)Application.Current.FindResource(key);

    private static ComboBox CreateCombo(FormField f)
    {
        var combo = new ComboBox { Height = 36, FontSize = 13 };
        foreach (var (value, label) in f.Options)
        {
            var item = new ComboBoxItem { Content = label, Tag = value };
            combo.Items.Add(item);
            if (value == f.Value)
                combo.SelectedItem = item;
        }
        if (combo.SelectedIndex < 0 && combo.Items.Count > 0)
            combo.SelectedIndex = 0;
        return combo;
    }

    private Dictionary<string, string> Collect() => _inputs.ToDictionary(kv => kv.Key, kv => kv.Value switch
    {
        TextBox t => t.Text.Trim(),
        PasswordBox p => p.Password,
        CheckBox c => c.IsChecked == true ? "true" : "false",
        ComboBox cb => (cb.SelectedItem as ComboBoxItem)?.Tag as string ?? "",
        _ => ""
    });

    private async Task ConfirmAsync()
    {
        var values = Collect();
        _error.Visibility = Visibility.Collapsed;
        _ok.IsEnabled = false;
        Cursor = Cursors.Wait;
        try
        {
            if (_submit != null)
                await _submit(values);
            Values = values;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            _error.Text = ex.Message;
            _error.Visibility = Visibility.Visible;
        }
        finally
        {
            _ok.IsEnabled = true;
            Cursor = null;
        }
    }

    /// <summary>Zeigt den Dialog. Rückgabe: eingegebene Werte oder null (abgebrochen).</summary>
    public static IReadOnlyDictionary<string, string>? Show(Window? owner, string title, string? subtitle, IEnumerable<FormField> fields,
        string okText, Func<IReadOnlyDictionary<string, string>, Task>? submit = null, bool danger = false, string? notice = null)
    {
        var dlg = new FormDialog(title, subtitle, notice, fields, okText, danger, submit)
        {
            Owner = owner ?? Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow
        };
        return dlg.ShowDialog() == true ? dlg.Values : null;
    }

    /// <summary>Ja/Nein-Abfrage im gleichen Design.</summary>
    public static bool Confirm(Window? owner, string title, string text, string okText, bool danger = true) =>
        Show(owner, title, text, Array.Empty<FormField>(), okText, null, danger) != null;

    /// <summary>Prüft neue Passwörter (Länge, Wiederholung) und wirft eine verständliche Meldung.</summary>
    public static void CheckNewPassword(string password, string repeat)
    {
        if (password.Length < 8)
            throw new InvalidOperationException(Loc.T("Das Passwort muss mindestens 8 Zeichen lang sein."));
        if (password != repeat)
            throw new InvalidOperationException(Loc.T("Die Passwörter stimmen nicht überein."));
    }

    /// <summary>Gut lesbares Zufallspasswort (z.B. als Startpasswort).</summary>
    public static string RandomPassword()
    {
        const string a = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = Enumerable.Range(0, 12).Select(_ => a[System.Security.Cryptography.RandomNumberGenerator.GetInt32(a.Length)]).ToArray();
        var s = new string(chars);
        return $"{s[..4]}-{s[4..8]}-{s[8..]}";
    }
}

/// <summary>Dunkle Titelleiste für alle Fenster.</summary>
public static class DarkTitleBar
{
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Apply(Window window)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            var on = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
            var black = 0;
            DwmSetWindowAttribute(hwnd, 35, ref black, sizeof(int));
        }
        catch
        {
            // ältere Windows-Version
        }
    }
}
