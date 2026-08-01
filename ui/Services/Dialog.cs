using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CloudReEdit.Resources;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;
using TextBlock = System.Windows.Controls.TextBlock;

namespace CloudReEdit.Services;

public static class Dialog
{
    private static Task ShowAcknowledgeAsync(string title, string message, ControlAppearance appearance)
    {
        var box = new MessageBox
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = S.Get("Dialog_OK"),
            CloseButtonAppearance = appearance,
        };

        box.Loaded += (_, _) => CollapseEmptyFooterButtons(box);
        return box.ShowDialogAsync();
    }

    private static void CollapseEmptyFooterButtons(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button btn)
            {
                var text = (btn.Content as string) ?? (btn.Content as TextBlock)?.Text;
                if (string.IsNullOrWhiteSpace(text) && btn.Icon is null)
                    btn.Visibility = Visibility.Collapsed;
            }
            CollapseEmptyFooterButtons(child);
        }
    }

    public static Task ShowInfoAsync(string title, string message)
        => ShowAcknowledgeAsync(title, message, ControlAppearance.Primary);

    public static Task ShowWarningAsync(string title, string message)
        => ShowAcknowledgeAsync(title, message, ControlAppearance.Caution);

    public static Task ShowErrorAsync(string title, string message)
        => ShowAcknowledgeAsync(title, message, ControlAppearance.Danger);

    public static async Task<bool> ConfirmAsync(string title, string message)
    {
        var box = new MessageBox
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = S.Get("Dialog_Yes"),
            CloseButtonText = S.Get("Dialog_No")
        };
        return await box.ShowDialogAsync() == MessageBoxResult.Primary;
    }

    public static async Task<bool> ConfirmDangerAsync(string title, string message)
    {
        var box = new MessageBox
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = S.Get("Dialog_Yes"),
            PrimaryButtonAppearance = ControlAppearance.Danger,
            CloseButtonText = S.Get("Dialog_No")
        };
        return await box.ShowDialogAsync() == MessageBoxResult.Primary;
    }

    public static async Task<bool> ChoiceAsync(string title, string message,
        string primaryText, string secondaryText)
    {
        var box = new MessageBox
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primaryText,
            CloseButtonText = secondaryText
        };
        return await box.ShowDialogAsync() == MessageBoxResult.Primary;
    }
}
