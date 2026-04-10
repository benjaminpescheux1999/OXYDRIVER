using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Oxydriver.Services;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfListBox = System.Windows.Controls.ListBox;
using WpfScrollViewer = System.Windows.Controls.ScrollViewer;

namespace Oxydriver.Ui;

public sealed class LogHistoryWindow : Window
{
    private const int PageSize = 30;
    private readonly AppLogStore _logStore;
    private readonly ObservableCollection<HistoryLine> _items = [];
    private readonly WpfListBox _list;
    private readonly WpfComboBox _categoryFilter;
    private readonly TextBlock _status;
    private WpfScrollViewer? _scrollViewer;
    private bool _loading;
    private bool _hasMore = true;
    private int _offset;

    public LogHistoryWindow(AppLogStore logStore)
    {
        _logStore = logStore;
        Title = "Historique des logs";
        Width = 980;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        top.Children.Add(new TextBlock
        {
            Text = "Catégorie:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        _categoryFilter = new WpfComboBox
        {
            Width = 170,
            ItemsSource = new[] { "Toutes", "systeme", "requete" },
            SelectedIndex = 0
        };
        _categoryFilter.SelectionChanged += async (_, _) => await ResetAndLoadAsync();
        top.Children.Add(_categoryFilter);
        Grid.SetRow(top, 0);
        root.Children.Add(top);

        _list = new WpfListBox { Margin = new Thickness(0, 10, 0, 10), ItemsSource = _items };
        _list.ItemTemplate = BuildTemplate();
        _list.Loaded += (_, _) =>
        {
            _scrollViewer = FindScrollViewer(_list);
            if (_scrollViewer is not null)
                _scrollViewer.ScrollChanged += async (_, e) => await OnScrollChangedAsync(e);
        };
        Grid.SetRow(_list, 1);
        root.Children.Add(_list);

        _status = new TextBlock { Foreground = System.Windows.Media.Brushes.DimGray, Text = "Chargement..." };
        Grid.SetRow(_status, 2);
        root.Children.Add(_status);

        Content = root;
        Loaded += async (_, _) => await ResetAndLoadAsync();
    }

    private async Task ResetAndLoadAsync()
    {
        _offset = 0;
        _hasMore = true;
        _items.Clear();
        await LoadMoreAsync();
    }

    private async Task OnScrollChangedAsync(ScrollChangedEventArgs e)
    {
        if (_scrollViewer is null || _loading || !_hasMore) return;
        if (_scrollViewer.VerticalOffset >= _scrollViewer.ScrollableHeight - 40)
            await LoadMoreAsync();
    }

    private async Task LoadMoreAsync()
    {
        if (_loading || !_hasMore) return;
        _loading = true;
        try
        {
            _status.Text = "Chargement des logs...";
            var category = _categoryFilter.SelectedItem?.ToString();
            if (string.Equals(category, "Toutes", StringComparison.OrdinalIgnoreCase))
                category = null;
            var rows = await _logStore.GetLogsAsync(_offset, PageSize, category);
            foreach (var row in rows)
            {
                var local = row.CreatedAtUtc.ToLocalTime();
                _items.Add(new HistoryLine
                {
                    Header = $"[{local:yyyy-MM-dd HH:mm:ss}] [{row.Category}] {row.Action}",
                    Details = BuildDetails(row)
                });
            }
            _offset += rows.Count;
            _hasMore = rows.Count == PageSize;
            _status.Text = _hasMore
                ? $"Affichés: {_items.Count}. Descendre pour charger 30 de plus."
                : $"Fin de l'historique. Total affiché: {_items.Count}.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Erreur chargement logs: {ex.Message}";
        }
        finally
        {
            _loading = false;
        }
    }

    private static string BuildDetails(PersistedLogEntry row)
    {
        var parts = new[]
        {
            string.IsNullOrWhiteSpace(row.Details) ? null : $"details: {row.Details}",
            string.IsNullOrWhiteSpace(row.RequestContent) ? null : $"envoye: {row.RequestContent}",
            string.IsNullOrWhiteSpace(row.ResponseContent) ? null : $"recu: {row.ResponseContent}",
            string.IsNullOrWhiteSpace(row.ErrorContent) ? null : $"erreur: {row.ErrorContent}"
        }.Where(x => !string.IsNullOrWhiteSpace(x));
        return string.Join(Environment.NewLine, parts);
    }

    private static DataTemplate BuildTemplate()
    {
        var stack = new FrameworkElementFactory(typeof(StackPanel));
        stack.SetValue(StackPanel.MarginProperty, new Thickness(0, 0, 0, 10));

        var header = new FrameworkElementFactory(typeof(TextBlock));
        header.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(HistoryLine.Header)));
        header.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        stack.AppendChild(header);

        var details = new FrameworkElementFactory(typeof(TextBlock));
        details.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(HistoryLine.Details)));
        details.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        details.SetValue(TextBlock.MarginProperty, new Thickness(0, 4, 0, 0));
        details.SetValue(TextBlock.ForegroundProperty, System.Windows.Media.Brushes.DimGray);
        stack.AppendChild(details);

        return new DataTemplate { VisualTree = stack };
    }

    private static WpfScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is WpfScrollViewer sv) return sv;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var result = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (result is not null) return result;
        }
        return null;
    }

    private sealed class HistoryLine
    {
        public string Header { get; init; } = string.Empty;
        public string Details { get; init; } = string.Empty;
    }
}
