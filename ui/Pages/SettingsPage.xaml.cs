using System.Windows;
using System.Windows.Controls;
using CloudReEdit.Services;

namespace CloudReEdit.Pages;

public partial class SettingsPage : Page
{
    /// <summary>True while the initial selection is being applied, so loading a
    /// stored preference never immediately rewrites the file it came from.</summary>
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();

        _loading = true;
        Select(AppSettings.LoadSaveSteamBehaviour());
        _loading = false;
    }

    private void Select(SaveSteamBehaviour behaviour)
    {
        switch (behaviour)
        {
            case SaveSteamBehaviour.AlwaysOpen:
                AlwaysOpenRadio.IsChecked = true;
                break;
            case SaveSteamBehaviour.NeverOpen:
                NeverOpenRadio.IsChecked = true;
                break;
            case SaveSteamBehaviour.LastState:
                LastStateRadio.IsChecked = true;
                break;
            default:
                AskRadio.IsChecked = true;
                break;
        }
    }

    private void Behaviour_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is not RadioButton radio) return;
        if (radio.IsChecked != true) return;

        var behaviour = radio.Name switch
        {
            nameof(AlwaysOpenRadio) => SaveSteamBehaviour.AlwaysOpen,
            nameof(NeverOpenRadio) => SaveSteamBehaviour.NeverOpen,
            nameof(LastStateRadio) => SaveSteamBehaviour.LastState,
            _ => SaveSteamBehaviour.Ask,
        };

        AppSettings.SetSaveSteamBehaviour(behaviour);
        AppLog.Info("settings",
            $"save/steam behaviour set to \"{AppSettings.SaveSteamBehaviourToken(behaviour)}\"");
    }
}
