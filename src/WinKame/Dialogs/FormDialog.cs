// WinKame, a morandi.cloud product. Copyright (C) 2026 Thales de Oliveira Morandi.
// SPDX-License-Identifier: GPL-3.0-or-later. See LICENSE and NOTICE.

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace WinKame.Dialogs;

public abstract record Field(string Key, string Label);

public sealed record ChoiceField(string Key, string Label, string[] Options, int Default = 0) : Field(Key, Label);

public sealed record SliderField(string Key, string Label, double Min, double Max, double Value, double Step = 1, string Suffix = "") : Field(Key, Label);

public sealed record NumberField(string Key, string Label, int Value, int Min = 0, int Max = 1_000_000, string Suffix = "") : Field(Key, Label);

public sealed record TextField(string Key, string Label, string Value = "", string? Hint = null, bool Multiline = false) : Field(Key, Label);

public sealed record CheckField(string Key, string Label, bool Value = false) : Field(Key, Label);

public sealed record ColorField(string Key, string Label, string Value) : Field(Key, Label);

public sealed record NoteField(string Text) : Field("", "");

public sealed class FormValues
{
    private readonly Dictionary<string, object?> _v = new();

    public object? this[string key]
    {
        get => _v.GetValueOrDefault(key);
        set => _v[key] = value;
    }

    public int Choice(string key) => _v.GetValueOrDefault(key) is int i ? i : 0;
    public double Num(string key) => _v.GetValueOrDefault(key) is double d ? d : 0;
    public int Int(string key) => (int)Math.Round(Num(key));
    public string Text(string key) => _v.GetValueOrDefault(key) as string ?? "";
    public bool Check(string key) => _v.GetValueOrDefault(key) is true;

    public FormValues Copy()
    {
        var c = new FormValues();
        foreach (var (k, v) in _v) c[k] = v;
        return c;
    }
}

/// <summary>A small options window built from a list of fields, with an optional live preview.</summary>
public sealed class FormDialog : Window
{
    private readonly FormValues _values = new();
    private readonly Func<FormValues, CancellationToken, Task<byte[]?>>? _preview;
    private readonly Image? _previewImage;
    private readonly TextBlock? _previewStatus;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private CancellationTokenSource? _previewCts;
    private readonly Func<FormValues, string?>? _validate;
    // Created up front: fields report changes (and hide this) while they are still being built.
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 0, 24, 0), Visibility = Visibility.Collapsed };

    private FormDialog(string title, string? subtitle, IReadOnlyList<Field> fields, string ok,
        Func<FormValues, CancellationToken, Task<byte[]?>>? preview, Func<FormValues, string?>? validate)
    {
        Ui.Themed(this);
        title = T(title);
        subtitle = subtitle is null ? null : T(subtitle);
        ok = T(ok);
        Title = title;
        _preview = preview;
        _validate = validate;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        MinWidth = 400;
        MaxHeight = SystemParameters.WorkArea.Height * 0.92;

        var form = new StackPanel { Margin = new Thickness(24, 20, 24, 8), Width = 340 };
        form.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            form.Children.Add(new TextBlock { Text = subtitle, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        }
        foreach (var f in fields) form.Children.Add(Build(f));


        var okButton = new Button { Content = ok, IsDefault = true, MinWidth = 110, Margin = new Thickness(8, 0, 0, 0) };
        if (TryFindResource("AccentButtonStyle") is Style accent) okButton.Style = accent;
        okButton.Click += (_, _) =>
        {
            var problem = _validate?.Invoke(_values);
            if (problem is not null)
            {
                _error.Text = T(problem);
                _error.Visibility = Visibility.Visible;
                return;
            }
            DialogResult = true;
        };
        var cancel = new Button { Content = T("Cancelar"), IsCancel = true, MinWidth = 90 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 12, 24, 20) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(okButton);

        var left = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(_error, Dock.Bottom);
        left.Children.Add(buttons);
        left.Children.Add(_error);
        left.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        if (preview is null)
        {
            Content = left;
        }
        else
        {
            _previewImage = new Image { Stretch = Stretch.Uniform, MaxWidth = 560, MaxHeight = 520 };
            RenderOptions.SetBitmapScalingMode(_previewImage, BitmapScalingMode.HighQuality);
            _previewStatus = new TextBlock { Text = T("Gerando prévia…"), Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var box = new Grid { Width = 580, Height = 540, Margin = new Thickness(0, 20, 20, 20) };
            box.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.FromArgb(0x14, 0x80, 0x80, 0x80)),
            });
            box.Children.Add(_previewStatus);
            box.Children.Add(_previewImage);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(left);
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);
            Content = grid;
            _debounce.Tick += (_, _) => { _debounce.Stop(); _ = RefreshPreview(); };
            Loaded += (_, _) => _ = RefreshPreview();
        }
    }

    private void Changed()
    {
        _error.Visibility = Visibility.Collapsed;
        if (_preview is null) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private async Task RefreshPreview()
    {
        if (_preview is null || _previewImage is null) return;
        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();
        var snapshot = _values.Copy();
        _previewStatus!.Text = T("Gerando prévia…");
        _previewImage.Opacity = 0.55;
        try
        {
            var png = await Task.Run(() => _preview(snapshot, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            _previewImage.Source = Ui.ToBitmap(png);
            _previewImage.Opacity = 1;
            _previewStatus.Text = "";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _previewStatus.Text = T("Prévia indisponível: {0}", ex.Message);
            _previewImage.Opacity = 0.3;
        }
    }

    private static TextBlock LabelFor(string text) =>
        new() { Text = text, Margin = new Thickness(0, 12, 0, 6), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };

    private UIElement Build(Field f)
    {
        var panel = new StackPanel();
        switch (f)
        {
            case NoteField n:
                panel.Children.Add(new TextBlock { Text = T(n.Text), TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = new Thickness(0, 10, 0, 0) });
                break;

            case ChoiceField c:
            {
                panel.Children.Add(LabelFor(T(c.Label)));
                _values[c.Key] = c.Default;
                if (c.Options.Length <= 4 && c.Options.Sum(o => o.Length) < 34)
                {
                    var row = new WrapPanel();
                    var group = "g" + c.Key;
                    for (var i = 0; i < c.Options.Length; i++)
                    {
                        var idx = i;
                        var rb = new RadioButton { Content = T(c.Options[i]), GroupName = group, IsChecked = i == c.Default, Margin = new Thickness(0, 0, 14, 0) };
                        rb.Checked += (_, _) => { _values[c.Key] = idx; Changed(); };
                        row.Children.Add(rb);
                    }
                    panel.Children.Add(row);
                }
                else
                {
                    var cb = new ComboBox { ItemsSource = c.Options.Select(o => T(o)).ToArray(), SelectedIndex = c.Default, HorizontalAlignment = HorizontalAlignment.Stretch };
                    cb.SelectionChanged += (_, _) => { _values[c.Key] = cb.SelectedIndex; Changed(); };
                    panel.Children.Add(cb);
                }
                break;
            }

            case SliderField s:
            {
                var label = LabelFor("");
                string Fmt(double v) => $"{T(s.Label)}: {v.ToString(s.Step < 1 ? "0.0" : "0", CultureInfo.CurrentCulture)}{s.Suffix}";
                label.Text = Fmt(s.Value);
                panel.Children.Add(label);
                _values[s.Key] = s.Value;
                var slider = new Slider
                {
                    Minimum = s.Min, Maximum = s.Max, Value = s.Value, SmallChange = s.Step, LargeChange = s.Step * 5,
                    TickFrequency = s.Step, IsSnapToTickEnabled = true,
                };
                slider.ValueChanged += (_, e) =>
                {
                    _values[s.Key] = e.NewValue;
                    label.Text = Fmt(e.NewValue);
                    Changed();
                };
                panel.Children.Add(slider);
                break;
            }

            case NumberField n:
            {
                panel.Children.Add(LabelFor(T(n.Label) + (string.IsNullOrEmpty(n.Suffix) ? "" : $" ({n.Suffix})")));
                _values[n.Key] = (double)n.Value;
                var tb = new TextBox { Text = n.Value.ToString(CultureInfo.CurrentCulture) };
                tb.TextChanged += (_, _) =>
                {
                    if (int.TryParse(tb.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var v))
                    {
                        _values[n.Key] = (double)Math.Clamp(v, n.Min, n.Max);
                        Changed();
                    }
                    else if (tb.Text.Trim().Length == 0)
                    {
                        _values[n.Key] = (double)n.Min;
                        Changed();
                    }
                };
                panel.Children.Add(tb);
                break;
            }

            case TextField t:
            {
                panel.Children.Add(LabelFor(T(t.Label)));
                _values[t.Key] = t.Value;
                var tb = new TextBox
                {
                    Text = t.Value,
                    AcceptsReturn = t.Multiline,
                    TextWrapping = t.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                    MinHeight = t.Multiline ? 70 : 0,
                };
                tb.TextChanged += (_, _) => { _values[t.Key] = tb.Text; Changed(); };
                panel.Children.Add(tb);
                if (!string.IsNullOrEmpty(t.Hint))
                {
                    panel.Children.Add(new TextBlock { Text = T(t.Hint), Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
                }
                Loaded += (_, _) =>
                {
                    if (FocusManager.GetFocusedElement(this) is null) tb.Focus();
                };
                break;
            }

            case CheckField c:
            {
                _values[c.Key] = c.Value;
                var cb = new CheckBox { Content = T(c.Label), IsChecked = c.Value, Margin = new Thickness(0, 12, 0, 0) };
                cb.Checked += (_, _) => { _values[c.Key] = true; Changed(); };
                cb.Unchecked += (_, _) => { _values[c.Key] = false; Changed(); };
                panel.Children.Add(cb);
                break;
            }

            case ColorField c:
            {
                panel.Children.Add(LabelFor(T(c.Label)));
                _values[c.Key] = c.Value;
                var swatch = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 8, 0), BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray };
                var tb = new TextBox { Text = c.Value, Width = 120 };
                void Apply(string hex)
                {
                    try
                    {
                        swatch.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                        _values[c.Key] = hex;
                        Changed();
                    }
                    catch (FormatException)
                    {
                        // keep the last valid color while typing
                    }
                }
                tb.TextChanged += (_, _) => Apply(tb.Text.Trim());
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(swatch);
                row.Children.Add(tb);
                var palette = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                foreach (var hex in new[] { "#FFFFFF", "#000000", "#1F1F23", "#FF8A1F", "#E5487A", "#7C5CFF", "#2D9CDB", "#27AE60", "#F2C94C", "#F4EDE4" })
                {
                    var chip = new Button
                    {
                        Width = 22, Height = 22, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(0),
                        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)), ToolTip = hex,
                    };
                    chip.Click += (_, _) => tb.Text = hex;
                    palette.Children.Add(chip);
                }
                Apply(c.Value);
                panel.Children.Add(row);
                panel.Children.Add(palette);
                break;
            }
        }
        return panel;
    }

    /// <summary>Builds the window without showing it (used by --snapshot).</summary>
    internal static Window Build(string title, string? subtitle, IReadOnlyList<Field> fields, string ok) =>
        new FormDialog(title, subtitle, fields, ok, null, null);

    /// <summary>Shows the form; returns null when cancelled.</summary>
    public static FormValues? Show(
        string title,
        string? subtitle,
        IReadOnlyList<Field> fields,
        string ok = "Aplicar",
        Func<FormValues, CancellationToken, Task<byte[]?>>? preview = null,
        Func<FormValues, string?>? validate = null)
    {
        var dlg = new FormDialog(title, subtitle, fields, ok, preview, validate);
        return Ui.ShowOnTop(dlg) == true ? dlg._values : null;
    }
}
