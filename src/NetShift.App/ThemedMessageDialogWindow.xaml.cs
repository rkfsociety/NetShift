using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;

namespace NetShift.App;

public partial class ThemedMessageDialogWindow : Window
{
    private readonly MessageBoxResult _closeResult;

    public ThemedMessageDialogWindow(string message, string caption, MessageBoxButton buttons, MessageBoxImage image)
    {
        InitializeComponent();
        Title = CaptionText.Text = caption;
        MessageText.Text = message;

        var results = GetResults(buttons);
        _closeResult = results.Contains(MessageBoxResult.Cancel)
            ? MessageBoxResult.Cancel
            : results.Contains(MessageBoxResult.No) ? MessageBoxResult.No : results[0];
        Result = _closeResult;

        SetIcon(image);
        AddButtons(results);
        Loaded += (_, _) => ButtonsPanel.Children.OfType<Button>().FirstOrDefault(button => button.IsDefault)?.Focus();
    }

    public MessageBoxResult Result { get; private set; }

    private static MessageBoxResult[] GetResults(MessageBoxButton buttons) => buttons switch
    {
        MessageBoxButton.OK => [MessageBoxResult.OK],
        MessageBoxButton.OKCancel => [MessageBoxResult.OK, MessageBoxResult.Cancel],
        MessageBoxButton.YesNo => [MessageBoxResult.Yes, MessageBoxResult.No],
        MessageBoxButton.YesNoCancel => [MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel],
        _ => throw new ArgumentOutOfRangeException(nameof(buttons), buttons, "Неподдерживаемый набор кнопок диалога.")
    };

    private void AddButtons(IReadOnlyList<MessageBoxResult> results)
    {
        var defaultResult = results[0];
        foreach (var result in results)
        {
            var primary = result == defaultResult;
            var button = new Button
            {
                Content = result switch
                {
                    MessageBoxResult.OK => "ОК",
                    MessageBoxResult.Cancel => "Отмена",
                    MessageBoxResult.Yes => "Да",
                    MessageBoxResult.No => "Нет",
                    _ => result.ToString()
                },
                MinWidth = 94,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = primary,
                Style = TryFindResource(typeof(Button)) as Style
            };

            if (primary)
            {
                button.Background = TryFindResource("AccentBrush") as Brush;
                button.BorderBrush = TryFindResource("AccentBrush") as Brush;
                button.Foreground = new SolidColorBrush(Color.FromRgb(8, 37, 42));
            }

            button.Click += (_, _) =>
            {
                Result = result;
                Close();
            };
            ButtonsPanel.Children.Add(button);
        }
    }

    private void SetIcon(MessageBoxImage image)
    {
        if (image == MessageBoxImage.None)
        {
            IconContainer.Visibility = Visibility.Collapsed;
            return;
        }

        var (symbol, color) = image == MessageBoxImage.Error
            ? ("×", Color.FromRgb(255, 105, 122))
            : image == MessageBoxImage.Warning
                ? ("!", Color.FromRgb(255, 190, 85))
                : image == MessageBoxImage.Question
                    ? ("?", Color.FromRgb(8, 184, 209))
                    : ("i", Color.FromRgb(8, 184, 209));
        IconGlyph.Text = symbol;
        IconGlyph.Foreground = new SolidColorBrush(color);
        IconContainer.Background = new SolidColorBrush(Color.FromArgb(34, color.R, color.G, color.B));
        IconContainer.BorderBrush = new SolidColorBrush(Color.FromArgb(92, color.R, color.G, color.B));
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Result = _closeResult;
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Result = _closeResult;
        Close();
        e.Handled = true;
    }
}

internal static class ThemedDialog
{
    public static MessageBoxResult Show(Window? owner, string message, string caption,
        MessageBoxButton buttons, MessageBoxImage image)
    {
        var dialog = new ThemedMessageDialogWindow(message, caption, buttons, image);
        if (owner is { IsLoaded: true })
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dialog.ShowDialog();
        return dialog.Result;
    }
}
