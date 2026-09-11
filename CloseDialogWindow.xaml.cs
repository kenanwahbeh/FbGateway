using System;
using System.Windows;
using FbGateway.Localization;

namespace FbGateway;

public partial class CloseDialogWindow : Window
{
    public CloseDialogResult Result { get; private set; } = CloseDialogResult.Cancel;

    public CloseDialogWindow()
    {
        InitializeComponent();

        ApplyLocalization();
    }

    private void ApplyLocalization()
    {
        Title = Strings.Get("CloseTitle");
        MessageTextBlock.Text = Strings.Get("CloseMessage");
        TrayButton.Content = Strings.Get("MinimizeToTray");
        SettingsButton.Content = Strings.Get("Settings");
        ExitButton.Content = Strings.Get("ExitApp");
        CancelButton.Content = Strings.Get("Cancel");
    }

    private void TrayButton_Click(object sender, RoutedEventArgs e)
    {
        Result = CloseDialogResult.MinimizeToTray;
        DialogResult = true;
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        Result = CloseDialogResult.Settings;
        DialogResult = true;
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        Result = CloseDialogResult.Exit;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Result = CloseDialogResult.Cancel;
        DialogResult = false;
    }
}

public enum CloseDialogResult
{
    MinimizeToTray,
    Settings,
    Exit,
    Cancel
}
