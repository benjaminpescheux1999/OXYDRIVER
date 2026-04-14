using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace Oxydriver.Ui.Tabs.Accueil;

public partial class AccueilTabView : System.Windows.Controls.UserControl, INotifyPropertyChanged
{
    private int _statusColumns = 4;
    public int StatusColumns
    {
        get => _statusColumns;
        private set
        {
            if (_statusColumns == value) return;
            _statusColumns = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusColumns)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AccueilTabView()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
        Loaded += (_, _) => RecomputeColumns();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RecomputeColumns();
    }

    private void RecomputeColumns()
    {
        var width = ActualWidth;
        // Responsive simple: 1/2/4 colonnes selon la largeur disponible.
        if (width < 650)
            StatusColumns = 1;
        else if (width < 1100)
            StatusColumns = 2;
        else
            StatusColumns = 4;
    }
}
