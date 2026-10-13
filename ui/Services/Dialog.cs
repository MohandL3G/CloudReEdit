using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CloudReEdit.Resources;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
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

    /// <summary>Three-button choice. Primary / Secondary / Close map to
    /// <see cref="MessageBoxResult.Primary"/> / <see cref="MessageBoxResult.Secondary"/> /
    /// <see cref="MessageBoxResult.None"/>.</summary>
    public static async Task<MessageBoxResult> Choice3Async(string title, string message,
        string primaryText, string secondaryText, string closeText)
    {
        var box = new MessageBox
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primaryText,
            SecondaryButtonText = secondaryText,
            CloseButtonText = closeText
        };
        return await box.ShowDialogAsync();
    }

    /// <summary>Three-button choice with an "always do this" checkbox, for a
    /// decision the user should only have to make once.</summary>
    /// <remarks>The checkbox is inert on its own: it only reports whether the user
    /// asked to be remembered. Persisting that choice is the caller's decision, so
    /// this helper stays free of any knowledge about what "always" means.</remarks>
    public static async Task<Choice3WithAlwaysResult> Choice3WithAlwaysAsync(
        string title, string message,
        string primaryText, string secondaryText, string closeText,
        string alwaysText)
    {
        var always = new CheckBox
        {
            Content = alwaysText,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(always);

        var box = new MessageBox
        {
            Title = title,
            Content = panel,
            PrimaryButtonText = primaryText,
            SecondaryButtonText = secondaryText,
            CloseButtonText = closeText,
        };

        var result = await box.ShowDialogAsync();
        return new Choice3WithAlwaysResult(result, always.IsChecked == true);
    }
}

/// <summary>Which button a <see cref="Dialog.Choice3WithAlwaysAsync"/> produced,
/// plus whether the user ticked the "always do this" box.</summary>
public sealed record Choice3WithAlwaysResult(MessageBoxResult Result, bool Always);
