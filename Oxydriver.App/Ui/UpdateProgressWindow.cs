using System;
using System.Windows;
using System.Windows.Controls;
using WpfProgressBar = System.Windows.Controls.ProgressBar;

namespace Oxydriver.Ui;

public sealed class UpdateProgressWindow : Window
{
    private readonly TextBlock _statusText;
    private readonly WpfProgressBar _progressBar;

    public UpdateProgressWindow()
    {
        Title = "Mise a jour OXYDRIVER";
        Width = 460;
        Height = 150;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.ToolWindow;

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "Telechargement de la mise a jour en cours...",
            FontWeight = FontWeights.SemiBold
        };
        Grid.SetRow(title, 0);
        root.Children.Add(title);

        _progressBar = new WpfProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 18,
            Margin = new Thickness(0, 12, 0, 0)
        };
        Grid.SetRow(_progressBar, 1);
        root.Children.Add(_progressBar);

        _statusText = new TextBlock
        {
            Margin = new Thickness(0, 10, 0, 0),
            Text = "Initialisation..."
        };
        Grid.SetRow(_statusText, 2);
        root.Children.Add(_statusText);

        Content = root;
    }

    public void UpdateProgress(double percent, string status)
    {
        _progressBar.Value = Math.Clamp(percent, 0, 100);
        _statusText.Text = status;
    }
}
