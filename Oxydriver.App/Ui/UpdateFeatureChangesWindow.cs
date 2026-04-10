using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using WpfButton = System.Windows.Controls.Button;
using WpfListBox = System.Windows.Controls.ListBox;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace Oxydriver.Ui;

public sealed class UpdateFeatureChangesWindow : Window
{
    public UpdateFeatureChangesWindow(string targetVersion, IEnumerable<string> changes)
    {
        Title = $"Mise a jour {targetVersion}";
        Width = 920;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanMinimize;

        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = $"Version {targetVersion} disponible",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold
        };
        Grid.SetRow(title, 0);
        root.Children.Add(title);

        var subtitle = new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 10),
            Text = "Vérifie les évolutions fonctionnelles avant téléchargement.",
            Foreground = System.Windows.Media.Brushes.DimGray
        };
        Grid.SetRow(subtitle, 1);
        root.Children.Add(subtitle);

        var items = (changes ?? [])
            .Select(BuildLine)
            .ToArray();
        var list = new WpfListBox
        {
            ItemsSource = items,
            Margin = new Thickness(0, 0, 0, 12)
        };
        var factory = new FrameworkElementFactory(typeof(TextBlock));
        factory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(UpdateChangeLine.Text)));
        factory.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(UpdateChangeLine.Foreground)));
        factory.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        factory.SetValue(TextBlock.MarginProperty, new Thickness(0, 0, 0, 4));
        list.ItemTemplate = new DataTemplate { VisualTree = factory };
        Grid.SetRow(list, 2);
        root.Children.Add(list);

        var actions = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        var cancel = new WpfButton
        {
            Content = "Annuler",
            Width = 120,
            Margin = new Thickness(0, 0, 8, 0)
        };
        cancel.Click += (_, _) =>
        {
            DialogResult = false;
            Close();
        };
        var validate = new WpfButton
        {
            Content = "Valider et télécharger",
            Width = 190
        };
        validate.Click += (_, _) =>
        {
            DialogResult = true;
            Close();
        };
        actions.Children.Add(cancel);
        actions.Children.Add(validate);
        Grid.SetRow(actions, 3);
        root.Children.Add(actions);

        Content = root;
    }

    private static UpdateChangeLine BuildLine(string raw)
    {
        var text = raw ?? string.Empty;
        var brush = System.Windows.Media.Brushes.Black;
        if (text.Contains("[AJOUT]", System.StringComparison.OrdinalIgnoreCase))
            brush = System.Windows.Media.Brushes.ForestGreen;
        else if (text.Contains("[RETRAIT]", System.StringComparison.OrdinalIgnoreCase))
            brush = System.Windows.Media.Brushes.Firebrick;
        else if (text.Contains("[DROITS MODIFIES]", System.StringComparison.OrdinalIgnoreCase))
            brush = System.Windows.Media.Brushes.DarkOrange;
        else if (text.StartsWith("===", System.StringComparison.OrdinalIgnoreCase))
            brush = System.Windows.Media.Brushes.MidnightBlue;
        return new UpdateChangeLine { Text = text, Foreground = brush };
    }
}

public sealed class UpdateChangeLine
{
    public string Text { get; set; } = string.Empty;
    public System.Windows.Media.Brush Foreground { get; set; } = System.Windows.Media.Brushes.Black;
}
