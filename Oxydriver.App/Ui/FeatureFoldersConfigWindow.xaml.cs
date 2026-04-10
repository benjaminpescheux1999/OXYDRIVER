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

    public FeatureFoldersConfigWindow(string featureName, IEnumerable<string> allFolders, IEnumerable<string> selectedFolders)
    {
        InitializeComponent();
        DataContext = this;
        var selected = (selectedFolders ?? [])
            .Select(x => (x ?? string.Empty).Trim().ToUpperInvariant())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(System.StringComparer.OrdinalIgnoreCase);
        foreach (var folder in (allFolders ?? [])
            .Select(x => (x ?? string.Empty).Trim().ToUpperInvariant())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(System.StringComparer.OrdinalIgnoreCase))
        {
            Items.Add(new FolderSelectionItem { Name = folder, IsSelected = selected.Contains(folder) });
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
    public string Name { get; set; } = string.Empty;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
