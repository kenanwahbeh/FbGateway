using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FirebirdSql.Data.FirebirdClient;
using ByteBridge.Configuration;

namespace ByteBridge;

public partial class AddDatabaseWindow : Window
{
    private readonly DatabaseConfig? _existing;

    public DatabaseConfig? Result { get; private set; }

    private bool _lastTestSuccessful;

    public AddDatabaseWindow()
    {
        InitializeComponent();

        _lastTestSuccessful = false;
    }

    public AddDatabaseWindow(DatabaseConfig existing)
    {
        InitializeComponent();

        _existing = existing;

        TitleTextBlock.Text = "Edit Data";

        NameTextBox.Text = existing.Name;
        ServerTextBox.Text = existing.Server;
        PortTextBox.Text = existing.Port.ToString();
        UsernameTextBox.Text = existing.Username;
        PasswordBox.Password = existing.Password;
        DatabaseTextBox.Text = existing.Database;

        _lastTestSuccessful = existing.LastTestSuccessful;
    }

    private void TextBox_GotKeyboardFocus(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            Dispatcher.BeginInvoke(
                new Action(textBox.SelectAll));
        }
    }

    private bool TryBuildConfiguration(
        out DatabaseConfig config)
    {
        config = new DatabaseConfig();

        var name = NameTextBox.Text.Trim();
        var server = ServerTextBox.Text.Trim();
        var portText = PortTextBox.Text.Trim();
        var username = UsernameTextBox.Text.Trim();
        var password = PasswordBox.Password;
        var database = DatabaseTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            ShowError("Please enter a connection name.");
            NameTextBox.Focus();
            return false;
        }

        if (string.IsNullOrWhiteSpace(server))
        {
            ShowError("Please enter the Firebird server.");
            ServerTextBox.Focus();
            return false;
        }

        if (!int.TryParse(portText, out var port) ||
            port < 1 ||
            port > 65535)
        {
            ShowError("Please enter a valid port.");
            PortTextBox.Focus();
            return false;
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            ShowError("Please enter the Firebird username.");
            UsernameTextBox.Focus();
            return false;
        }

        if (string.IsNullOrWhiteSpace(database))
        {
            ShowError(
                "Please enter the database path or Firebird alias.");

            DatabaseTextBox.Focus();
            return false;
        }

        config = new DatabaseConfig
        {
            Id = _existing?.Id ?? Guid.NewGuid().ToString(),
            Name = name,
            Server = server,
            Port = port,
            Username = username,
            Password = password,
            Database = database,

            // New/Edit keeps the existing enabled state.
            Enabled = _existing?.Enabled ?? true,

            LastTestSuccessful = _lastTestSuccessful,

            LastTestedAt =
                _lastTestSuccessful
                    ? (_existing?.LastTestedAt ?? DateTime.Now)
                    : _existing?.LastTestedAt
        };

        return true;
    }

    private async void TestButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!TryBuildConfiguration(out var config))
        {
            return;
        }

        TestButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        CancelButton.IsEnabled = false;

        TestResultBorder.Visibility =
            Visibility.Visible;

        TestResultTextBlock.Text =
            "Testing connection...";

        TestResultTextBlock.Foreground =
            Brushes.Gray;

        ScrollToTestResult();

        try
        {
            await TestConnectionAsync(config);

            _lastTestSuccessful = true;

            TestResultTextBlock.Text =
                "✓ Connection successful.";

            TestResultTextBlock.Foreground =
                Brushes.Green;
        }
        catch (Exception ex)
        {
            _lastTestSuccessful = false;

            TestResultTextBlock.Text =
                $"✗ Connection failed.\n\n{ex.Message}";

            TestResultTextBlock.Foreground =
                Brushes.Red;
        }
        finally
        {
            TestButton.IsEnabled = true;
            SaveButton.IsEnabled = true;
            CancelButton.IsEnabled = true;

            ScrollToTestResult();
        }
    }

    private static async Task TestConnectionAsync(
        DatabaseConfig config)
    {
        var builder =
            new FbConnectionStringBuilder
            {
                DataSource = config.Server,
                Port = config.Port,
                Database = config.Database,
                UserID = config.Username,
                Password = config.Password,
                Charset = "UTF8",
                ConnectionTimeout = 10
            };

        await using var connection =
            new FbConnection(builder.ToString());

        await connection.OpenAsync();

        await connection.CloseAsync();
    }

    private void SaveButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!TryBuildConfiguration(out var config))
        {
            return;
        }

        Result = config;

        DialogResult = true;

        Close();
    }

    private void CancelButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        DialogResult = false;

        Close();
    }

    private void ShowError(string message)
    {
        TestResultBorder.Visibility =
            Visibility.Visible;

        TestResultTextBlock.Text =
            message;

        TestResultTextBlock.Foreground =
            Brushes.Red;

        ScrollToTestResult();
    }

    private void ScrollToTestResult()
    {
        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                FormScrollViewer.ScrollToEnd();
            }));
    }
}