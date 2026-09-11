using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using FbGateway.Configuration;
using FbGateway.Data;
using FbGateway.Localization;

namespace FbGateway;

public partial class SettingsWindow : Window
{
    private readonly SqliteDatabase _database;
    private readonly Action _onLanguageChanged;

    private const string AutoStartRegistryPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    private const string AutoStartRegistryValueName =
        "ByteBridge";

    public SettingsWindow(
        SqliteDatabase database,
        Action onLanguageChanged)
    {
        InitializeComponent();

        _database = database;
        _onLanguageChanged = onLanguageChanged;

        LoadSettings();
        ApplyLocalization();
    }

    private void LoadSettings()
    {
        // Load current language
        var currentLang = Strings.CurrentLanguage;
        foreach (ComboBoxItem item in LanguageComboBox.Items)
        {
            if (item.Tag?.ToString() == currentLang)
            {
                item.IsSelected = true;
                break;
            }
        }

        // Load auto-start
        AutoStartCheckBox.IsChecked = IsAutoStartEnabled();
    }

    private void ApplyLocalization()
    {
        Title = Strings.Get("Settings");
        SettingsTitleTextBlock.Text = Strings.Get("Settings");
        LanguageTextBlock.Text = Strings.Get("Language");
        LanguageHintTextBlock.Text = "Select the display language";
        AutoStartTextBlock.Text = Strings.Get("AutoStart");
        AutoStartHintTextBlock.Text = Strings.Get("AutoStartHint");
        DoneButton.Content = Strings.Get("Done");
    }

    private void LanguageComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (LanguageComboBox.SelectedItem is ComboBoxItem item)
        {
            var language = item.Tag?.ToString() ?? "en";
            Strings.SetLanguage(language);

            _database.SetSetting("App.Language", language);

            ApplyLocalization();

            _onLanguageChanged?.Invoke();
        }
    }

    private void AutoStartCheckBox_Changed(
        object sender,
        RoutedEventArgs e)
    {
        if (AutoStartCheckBox.IsChecked == true)
        {
            EnableAutoStart();
        }
        else
        {
            DisableAutoStart();
        }
    }

    private void DoneButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        DialogResult = true;
    }

    /*
     * Auto-start management using Windows Registry.
     *
     * Adds/removes an entry in:
     * HKCU\Software\Microsoft\Windows\CurrentVersion\Run
     *
     * This makes the app start automatically when the user
     * logs in to Windows.
     */
    private static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                AutoStartRegistryPath,
                false);

            return key?.GetValue(AutoStartRegistryValueName) != null;
        }
        catch
        {
            return false;
        }
    }

    private static void EnableAutoStart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                AutoStartRegistryPath,
                true);

            if (key == null)
            {
                return;
            }

            // Get the path to the current executable
            var exePath = Environment.ProcessPath;

            if (string.IsNullOrEmpty(exePath))
            {
                return;
            }

            key.SetValue(
                AutoStartRegistryValueName,
                $"\"{exePath}\"");
        }
        catch
        {
            // Silently fail if registry access is denied
        }
    }

    private static void DisableAutoStart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                AutoStartRegistryPath,
                true);

            if (key == null)
            {
                return;
            }

            key.DeleteValue(AutoStartRegistryValueName, false);
        }
        catch
        {
            // Silently fail if registry access is denied
        }
    }
}
