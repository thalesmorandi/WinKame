using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WinKame.Actions;
using WinKame.Core;
using WinKame.Input;
using WinKame.Shell;

namespace WinKame.Settings;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _s;
    private readonly ObservableCollection<CustomAction> _actions;

    public event Action<AppSettings>? Saved;

    private static readonly string[] GlyphChoices =
        ["", "", "", "", "", "", "", "", "", "", "", "", "", "", "⭐", "🔧", "🚀"];

    public SettingsWindow(AppSettings current, string? tab = null)
    {
        InitializeComponent();
        Ui.Themed(this);
        _s = current.Clone();
        _actions = new ObservableCollection<CustomAction>(_s.CustomActions.Select(a => a.Clone()));

        if (TryFindResource("AccentButtonStyle") is Style accent) SaveButton.Style = accent;
        VersionText.Text = T("Versão") + " " + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.1.0");
        PlaceholderHelp.Text = CustomActionRunner.Help;

        // General
        DragEnabled.IsChecked = _s.DragWheelEnabled;
        OutSame.IsChecked = string.IsNullOrWhiteSpace(_s.OutputFolder);
        OutCustom.IsChecked = !OutSame.IsChecked;
        OutFolder.Text = _s.OutputFolder ?? "";
        Reveal.IsChecked = _s.RevealWhenDone;
        LanguageBox.SelectedValue = _s.Language is "pt" or "en" ? _s.Language : "auto";
        LanguageBox.SelectionChanged += (_, _) => LanguageNote.Visibility = Visibility.Visible;

        // Keys
        void SetupKey(ComboBox box, string value)
        {
            box.ItemsSource = DragWatcher.KeyNames.Select(k => new ComboBoxItem { Content = k == "None" ? T("Nenhuma") : DragWatcher.KeyLabel(k), Tag = k }).ToList();
            box.SelectedValuePath = "Tag";
            box.SelectedValue = value;
        }
        SetupKey(KeyConvert, _s.KeyConvert);
        SetupKey(KeyTools, _s.KeyTools);
        SetupKey(KeyActions, _s.KeyActions);
        SetupKey(KeyCustom, _s.KeyCustom);
        TapMs.ValueChanged += (_, e) => TapLabel.Text = T("Duração máxima do toque: {0:0} ms", e.NewValue);
        Threshold.ValueChanged += (_, e) => ThresholdLabel.Text = T("Distância mínima para contar como arrasto: {0:0} px", e.NewValue);
        WheelScale.ValueChanged += (_, e) => ScaleLabel.Text = T("Tamanho da roda: {0:0}%", e.NewValue * 100);
        TapMs.Value = _s.TapMaxMs;
        Threshold.Value = _s.DragThresholdPx;
        WheelScale.Value = _s.WheelScale;
        TapLabel.Text = T("Duração máxima do toque: {0:0} ms", TapMs.Value);
        ThresholdLabel.Text = T("Distância mínima para contar como arrasto: {0:0} px", Threshold.Value);
        ScaleLabel.Text = T("Tamanho da roda: {0:0}%", WheelScale.Value * 100);

        // Tools
        BuildToolRows();

        // Custom actions
        ActionList.ItemsSource = _actions;
        foreach (var g in GlyphChoices)
        {
            var b = new Button { Content = g, FontFamily = Ui.IconFont, FontSize = 14, Width = 32, Height = 30, Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(0) };
            b.Click += (_, _) =>
            {
                if (ActionList.SelectedItem is CustomAction a)
                {
                    a.Glyph = g;
                    ActionEditor.DataContext = null;
                    ActionEditor.DataContext = a;
                }
            };
            GlyphPicker.Children.Add(b);
        }
        if (_actions.Count > 0) ActionList.SelectedIndex = 0;
        UpdateEditorVisibility();

        // Integration
        StartWithWindows.IsChecked = ShellIntegration.StartupInstalled;
        ContextMenuCheck.IsChecked = ShellIntegration.ContextMenuInstalled;
        SendTo.IsChecked = ShellIntegration.SendToInstalled;

        if (tab is not null)
        {
            foreach (TabItem t in Tabs.Items)
            {
                if ((string)t.Tag == tab) Tabs.SelectedItem = t;
            }
        }
    }

    // ---------- tools ----------

    private enum ToolStatus { Included, Available, Optional, Missing }

    private Border ToolCard(string name, string purpose, ToolStatus state, string statusText, string? detail, out StackPanel body)
    {
        name = T(name);
        purpose = T(purpose);
        statusText = T(statusText);
        var card = new Border
        {
            Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 0, 10), CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1), BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x30, 0x80, 0x80, 0x80)),
        };
        body = new StackPanel();
        var head = new DockPanel();
        var status = new TextBlock
        {
            Text = statusText,
            FontWeight = FontWeights.SemiBold,
            Foreground = state switch
            {
                ToolStatus.Included or ToolStatus.Available => System.Windows.Media.Brushes.SeaGreen,
                ToolStatus.Missing => System.Windows.Media.Brushes.IndianRed,
                _ => System.Windows.Media.Brushes.Gray,
            },
        };
        DockPanel.SetDock(status, Dock.Right);
        head.Children.Add(status);
        head.Children.Add(new TextBlock { Text = name, FontSize = 15, FontWeight = FontWeights.SemiBold });
        body.Children.Add(head);
        body.Children.Add(new TextBlock { Text = purpose, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        if (detail is not null)
        {
            body.Children.Add(new TextBlock
            {
                Text = detail, Opacity = 0.6, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 0),
                FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
            });
        }
        card.Child = body;
        return card;
    }

    private static TextBlock Section(string text) =>
        new() { Text = T(text), FontSize = 13, FontWeight = FontWeights.SemiBold, Opacity = 0.8, Margin = new Thickness(0, 10, 0, 8) };

    private void BuildToolRows()
    {
        ToolRows.Children.Clear();

        ToolRows.Children.Add(Section(T("Incluído")));
        ToolRows.Children.Add(ToolCard(T("Motor nativo"),
            T("Imagens, PDF, DOCX, Markdown, HTML, TXT, EPUB, RTF, ODT, CSV/TSV/XLSX, legendas e arquivos compactados, sem programas extras."),
            ToolStatus.Included, T("Incluído"), null, out _));

        var webView = Pdf.WebViewPrinter.RuntimeAvailable();
        ToolRows.Children.Add(ToolCard("WebView2 (Edge)",
            T("Gera PDF de documentos e planilhas. Já vem com o Windows 10 e 11."),
            webView ? ToolStatus.Available : ToolStatus.Missing, webView ? T("Disponível") : T("Não encontrado"), null, out var wvBody));
        if (!webView)
        {
            var get = new Button { Content = T("Baixar o WebView2 Runtime"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
            get.Click += (_, _) => Process.Start(new ProcessStartInfo("https://go.microsoft.com/fwlink/p/?LinkId=2124703") { UseShellExecute = true });
            wvBody.Children.Add(get);
        }

        ToolRows.Children.Add(Section(T("Usado quando estiver instalado")));
        var apps = new[] { (Core.Engines.OfficeApp.Word, "Word"), (Core.Engines.OfficeApp.Excel, "Excel"), (Core.Engines.OfficeApp.PowerPoint, "PowerPoint") }
            .Where(a => Type.GetTypeFromProgID(a.Item1 switch
            {
                Core.Engines.OfficeApp.Word => "Word.Application",
                Core.Engines.OfficeApp.Excel => "Excel.Application",
                _ => "PowerPoint.Application",
            }) is not null)
            .Select(a => a.Item2)
            .ToList();
        ToolRows.Children.Add(ToolCard("Microsoft Office",
            T("Fidelidade máxima em DOCX/XLSX/PPTX para PDF, e abre DOC/XLS/PPT antigos. Se o Office pedir algo (como um rótulo de confidencialidade), o WinKame usa o motor nativo."),
            apps.Count > 0 ? ToolStatus.Available : ToolStatus.Optional, apps.Count > 0 ? string.Join(", ", apps) : T("Não instalado"), null, out var officeBody));
        if (apps.Count > 0)
        {
            var use = new CheckBox { Content = T("Usar o Microsoft Office"), IsChecked = _s.UseOffice, Margin = new Thickness(0, 10, 0, 0) };
            use.Checked += (_, _) => _s.UseOffice = true;
            use.Unchecked += (_, _) => _s.UseOffice = false;
            officeBody.Children.Add(use);
        }

        foreach (var tool in Enum.GetValues<ExternalTool>())
        {
            var found = FindWithOverride(tool);
            var bundled = tool == ExternalTool.FFmpeg && found is not null &&
                string.Equals(System.IO.Path.GetFullPath(found), System.IO.Path.GetFullPath(ToolLocator.BundledFfmpeg), StringComparison.OrdinalIgnoreCase);
            var (state, text) = found is null
                ? (tool == ExternalTool.FFmpeg ? ToolStatus.Missing : ToolStatus.Optional, tool == ExternalTool.FFmpeg ? T("Não encontrado") : T("Opcional"))
                : (bundled ? ToolStatus.Included : ToolStatus.Available, bundled ? T("Incluído") : T("Instalado"));
            if (tool == ExternalTool.FFmpeg)
            {
                // FFmpeg belongs with the included tools.
                ToolRows.Children.Insert(3, ToolCard(ToolLocator.DisplayName(tool), ToolLocator.Purpose(tool), state, text, found, out var ffBody));
                AddToolButtons(tool, found, ffBody);
                continue;
            }
            ToolRows.Children.Add(ToolCard(ToolLocator.DisplayName(tool), ToolLocator.Purpose(tool), state, text, found ?? $"winget install {ToolLocator.WingetId(tool)}", out var body));
            AddToolButtons(tool, found, body);
        }
    }

    private void AddToolButtons(ExternalTool tool, string? found, StackPanel stack)
    {
        {
            var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            if (found is null)
            {
                var install = new Button { Content = T("Instalar com winget"), Margin = new Thickness(0, 0, 8, 0) };
                install.Click += async (_, _) =>
                {
                    install.IsEnabled = false;
                    Status.Text = T("Instalando {0}…", ToolLocator.DisplayName(tool));
                    await ToolGate.InstallAsync(tool);
                    Status.Text = "";
                    BuildToolRows();
                };
                buttons.Children.Add(install);
            }
            var pick = new Button { Content = T("Escolher executável…"), Margin = new Thickness(0, 0, 8, 0) };
            pick.Click += (_, _) =>
            {
                var dlg = new OpenFileDialog { Filter = T("Programas") + " (*.exe;*.com)|*.exe;*.com", Title = ToolLocator.DisplayName(tool) };
                if (dlg.ShowDialog(this) == true)
                {
                    _s.ToolPaths[tool.ToString()] = dlg.FileName;
                    BuildToolRows();
                }
            };
            buttons.Children.Add(pick);
            if (_s.ToolPaths.ContainsKey(tool.ToString()))
            {
                var clear = new Button { Content = T("Usar detecção automática") };
                clear.Click += (_, _) => { _s.ToolPaths.Remove(tool.ToString()); BuildToolRows(); };
                buttons.Children.Add(clear);
            }
            stack.Children.Add(buttons);
        }
    }

    private string? FindWithOverride(ExternalTool tool)
    {
        if (_s.ToolPaths.TryGetValue(tool.ToString(), out var p) && System.IO.File.Exists(p)) return p;
        ToolLocator.Invalidate();
        return ToolLocator.Find(tool);
    }

    // ---------- custom actions ----------

    private void UpdateEditorVisibility()
    {
        var has = ActionList.SelectedItem is CustomAction;
        ActionEditor.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        NoAction.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ActionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ActionEditor.DataContext = ActionList.SelectedItem;
        UpdateEditorVisibility();
    }

    private void ActionName_Changed(object sender, TextChangedEventArgs e)
    {
        // The list shows names; refresh after the binding pushed the new value.
        Dispatcher.BeginInvoke(() =>
        {
            var sel = ActionList.SelectedItem;
            ActionList.Items.Refresh();
            ActionList.SelectedItem = sel;
        });
    }

    private void AddAction_Click(object sender, RoutedEventArgs e)
    {
        var a = new CustomAction { Name = T("Nova ação"), Command = "", Arguments = "\"{path}\"" };
        _actions.Add(a);
        ActionList.SelectedItem = a;
    }

    private void DuplicateAction_Click(object sender, RoutedEventArgs e)
    {
        if (ActionList.SelectedItem is not CustomAction a) return;
        var copy = a.Clone();
        copy.Id = Guid.NewGuid().ToString("N")[..10];
        copy.Name += " (" + T("cópia") + ")";
        _actions.Insert(_actions.IndexOf(a) + 1, copy);
        ActionList.SelectedItem = copy;
    }

    private void RemoveAction_Click(object sender, RoutedEventArgs e)
    {
        if (ActionList.SelectedItem is not CustomAction a) return;
        var i = _actions.IndexOf(a);
        _actions.Remove(a);
        if (_actions.Count > 0) ActionList.SelectedIndex = Math.Min(i, _actions.Count - 1);
        UpdateEditorVisibility();
    }

    private void Examples_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var ex in CustomAction.Examples())
        {
            var item = new MenuItem { Header = ex.Name };
            item.Click += (_, _) =>
            {
                _actions.Add(ex);
                ActionList.SelectedItem = ex;
            };
            menu.Items.Add(item);
        }
        menu.PlacementTarget = ExamplesButton;
        menu.IsOpen = true;
    }

    private void BrowseCommand_Click(object sender, RoutedEventArgs e)
    {
        if (ActionList.SelectedItem is not CustomAction a) return;
        var dlg = new OpenFileDialog { Filter = T("Programas e scripts") + "|*.exe;*.com;*.bat;*.cmd;*.ps1|" + T("Todos") + "|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        if (dlg.FileName.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            a.Command = "powershell.exe";
            a.Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{dlg.FileName}\" \"{{path}}\"";
        }
        else
        {
            a.Command = dlg.FileName;
        }
        ActionEditor.DataContext = null;
        ActionEditor.DataContext = a;
    }

    private void TestAction_Click(object sender, RoutedEventArgs e)
    {
        if (ActionList.SelectedItem is not CustomAction a) return;
        var dlg = new OpenFileDialog { Title = T("Arquivo para testar"), Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;
        if (!a.Matches(dlg.FileNames)) Status.Text = T("Atenção: essa ação não apareceria para esses arquivos (veja \"Para quais arquivos\").");
        _ = CustomActions.RunAsync(a.Clone(), dlg.FileNames);
    }

    // ---------- general ----------

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = T("Pasta para os resultados") };
        if (dlg.ShowDialog(this) == true)
        {
            OutFolder.Text = dlg.FolderName;
            OutCustom.IsChecked = true;
        }
    }

    private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(AppSettings.Folder) { UseShellExecute = true });

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string Key(ComboBox b) => b.SelectedValue as string ?? "None";
        var keys = new[] { Key(KeyConvert), Key(KeyTools), Key(KeyActions), Key(KeyCustom) }.Where(k => k != "None").ToList();
        if (keys.Count != keys.Distinct().Count())
        {
            Tabs.SelectedIndex = 1;
            Status.Text = T("Cada tecla só pode abrir uma roda.");
            return;
        }
        if (OutCustom.IsChecked == true && string.IsNullOrWhiteSpace(OutFolder.Text))
        {
            Tabs.SelectedIndex = 0;
            Status.Text = T("Escolha a pasta para os resultados.");
            return;
        }

        _s.DragWheelEnabled = DragEnabled.IsChecked == true;
        _s.OutputFolder = OutCustom.IsChecked == true ? OutFolder.Text.Trim() : null;
        _s.RevealWhenDone = Reveal.IsChecked == true;
        _s.Language = LanguageBox.SelectedValue as string ?? "auto";
        _s.KeyConvert = Key(KeyConvert);
        _s.KeyTools = Key(KeyTools);
        _s.KeyActions = Key(KeyActions);
        _s.KeyCustom = Key(KeyCustom);
        _s.TapMaxMs = (int)TapMs.Value;
        _s.DragThresholdPx = (int)Threshold.Value;
        _s.WheelScale = Math.Round(WheelScale.Value, 2);
        _s.CustomActions = _actions.ToList();

        try
        {
            if (StartWithWindows.IsChecked != ShellIntegration.StartupInstalled) ShellIntegration.SetStartup(StartWithWindows.IsChecked == true);
            if (ContextMenuCheck.IsChecked != ShellIntegration.ContextMenuInstalled) ShellIntegration.SetContextMenu(ContextMenuCheck.IsChecked == true);
            if (SendTo.IsChecked != ShellIntegration.SendToInstalled) ShellIntegration.SetSendTo(SendTo.IsChecked == true);
        }
        catch (Exception ex)
        {
            Ui.Error(T("Não foi possível alterar a integração com o Windows: {0}", ex.Message));
        }
        _s.StartWithWindows = StartWithWindows.IsChecked == true;
        _s.ExplorerContextMenu = ContextMenuCheck.IsChecked == true;
        _s.SendToMenu = SendTo.IsChecked == true;

        Saved?.Invoke(_s);
        Close();
    }
}
