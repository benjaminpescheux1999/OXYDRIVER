using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;

namespace Oxydriver.Ui;

public partial class FeatureFoldersConfigWindow : Window
{
    public ObservableCollection<FolderSelectionItem> Items { get; } = [];
    public string[] SelectedFolders { get; private set; } = [];

    public FeatureFoldersConfigWindow(
        string featureName,
        IEnumerable<string> allFolders,
        IEnumerable<string> selectedFolders,
        IEnumerable<string>? availableFolders = null)
    {
        InitializeComponent();
        DataContext = this;
        var selected = (selectedFolders ?? [])
            .Select(x => (x ?? string.Empty).Trim().ToUpperInvariant())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(System.StringComparer.OrdinalIgnoreCase);
        var available = (availableFolders ?? [])
            .Select(x => (x ?? string.Empty).Trim().ToUpperInvariant())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(System.StringComparer.OrdinalIgnoreCase);
        foreach (var folder in (allFolders ?? [])
            .Select(x => (x ?? string.Empty).Trim().ToUpperInvariant())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(System.StringComparer.OrdinalIgnoreCase))
        {
            var isAvailable = available.Count == 0 || available.Contains(folder);
            Items.Add(new FolderSelectionItem
            {
                Name = folder,
                IsAvailable = isAvailable,
                IsSelected = isAvailable && selected.Contains(folder)
            });
        }
        TitleText.Text = $"Fonctionnalité: {featureName}\nChoisis les dossiers à utiliser pour les requêtes SQL.";
    }

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        var selected = Items.Where(x => x.IsSelected).Select(x => x.Name).ToArray();
        if (selected.Length == 0)
        {
            System.Windows.MessageBox.Show(
                this,
                "Sélectionne au moins un dossier.",
                "Configuration dossiers",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
            return;
        }
        SelectedFolders = selected;
        DialogResult = true;
        Close();
    }

    private void CancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

public sealed class FolderSelectionItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isAvailable = true;
    public string Name { get; set; } = string.Empty;
    public bool IsAvailable
    {
        get => _isAvailable;
        set
        {
            if (_isAvailable == value) return;
            _isAvailable = value;
            if (!_isAvailable) IsSelected = false;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAvailable)));
        }
    }
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!IsAvailable && value) value = false;
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
