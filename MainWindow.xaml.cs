using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FirebirdSql.Data.FirebirdClient;
using FbGateway.Configuration;
using FbGateway.Data;
using FbGateway.Gateway;

namespace FbGateway;

public partial class MainWindow : Window
{
    private readonly SqliteDatabase _database;

    private readonly GatewayServer _gateway;

    private List<DatabaseConfig> _connections = new();

    public MainWindow()
    {
        InitializeComponent();

        _database = new SqliteDatabase();

        _gateway = new GatewayServer(_database);

        GatewayPortTextBox.Text =
            _gateway.Config.Port.ToString();

        LoadConnections();

        UpdateGatewayUi();
    }

    /*
     * Autostart happens after the window is up so a failure can
     * be reported in a dialog the user actually sees.
     *
     * A gateway that fails to start silently is the whole reason
     * a tunnel pointed at this machine returns 502.
     */
    private void Window_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        if (_gateway.Config.AutoStart &&
            !_gateway.IsRunning)
        {
            StartGateway();
        }
    }

    private void Window_Closed(
        object sender,
        EventArgs e)
    {
        _gateway.Dispose();
    }

    private void GatewayToggleButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_gateway.IsRunning)
        {
            _gateway.Stop();

            var stopped = _database.GetGatewayConfig();

            stopped.AutoStart = false;

            _database.SaveGatewayConfig(stopped);

            UpdateGatewayUi();

            return;
        }

        StartGateway();
    }

    private void StartGateway()
    {
        var portText =
            GatewayPortTextBox.Text.Trim();

        if (!int.TryParse(portText, out var port) ||
            port < 1 ||
            port > 65535)
        {
            MessageBox.Show(
                "Please enter a valid port between 1 and 65535.",

                "Easy FB Soft",

                MessageBoxButton.OK,

                MessageBoxImage.Warning);

            return;
        }

        var config = _database.GetGatewayConfig();

        config.Port = port;
        config.AutoStart = true;

        try
        {
            _gateway.Start(config);

            _database.SaveGatewayConfig(config);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"The gateway could not start.\n\n{ex.Message}",

                "Gateway Failed",

                MessageBoxButton.OK,

                MessageBoxImage.Warning);
        }
        finally
        {
            UpdateGatewayUi();
        }
    }

    private void CopyKeyButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_gateway.Config.ApiKey);

            MessageBox.Show(
                "API key copied.\n\n" +
                "Send it on every request as the X-API-Key header.",

                "Easy FB Soft",

                MessageBoxButton.OK,

                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"The key could not be copied.\n\n{ex.Message}",

                "Easy FB Soft",

                MessageBoxButton.OK,

                MessageBoxImage.Warning);
        }
    }

    private void RegenerateKeyButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var confirm =
            MessageBox.Show(
                "Generate a new API key?\n\n" +
                "Every client still using the current key will be " +
                "rejected until it is updated.",

                "New API Key",

                MessageBoxButton.YesNo,

                MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        _gateway.UpdateApiKey(
            _database.RegenerateApiKey());

        MessageBox.Show(
            "A new API key was generated.\n\n" +
            "Use Copy API Key to put it on the clipboard.",

            "Easy FB Soft",

            MessageBoxButton.OK,

            MessageBoxImage.Information);
    }

    private void UpdateGatewayUi()
    {
        var config = _gateway.Config;

        if (_gateway.IsRunning)
        {
            GatewayStatusTextBlock.Text =
                $"● Running — {config.BaseUrl}";

            GatewayStatusTextBlock.Foreground =
                Brushes.Green;

            GatewayHintTextBlock.Text =
                "Point the tunnel here:  " +
                $"cloudflared tunnel --url {config.BaseUrl}";

            GatewayToggleButton.Content = "Stop";
        }
        else
        {
            GatewayStatusTextBlock.Text = "● Stopped";

            GatewayStatusTextBlock.Foreground =
                Brushes.Red;

            GatewayHintTextBlock.Text =
                "Nothing is listening. A Cloudflare tunnel pointed " +
                "at this machine will return 502 until the gateway starts.";

            GatewayToggleButton.Content = "Start";
        }

        GatewayPortTextBox.IsEnabled =
            !_gateway.IsRunning;
    }

    private void LoadConnections()
    {
        _connections =
            _database.GetConnections();

        RenderConnections();
    }

    private void RenderConnections()
    {
        ConnectionsPanel.Children.Clear();

        if (_connections.Count == 0)
        {
            ConnectionsPanel.Children.Add(
                new TextBlock
                {
                    Text =
                        "No databases configured.\n\n" +
                        "Click + Add Data to create a connection.",

                    FontSize = 16,

                    Foreground = Brushes.Gray,

                    TextAlignment =
                        TextAlignment.Center,

                    Margin =
                        new Thickness(20)
                });

            return;
        }

        foreach (var connection in _connections)
        {
            ConnectionsPanel.Children.Add(
                CreateConnectionCard(connection));
        }
    }

    private Border CreateConnectionCard(
        DatabaseConfig connection)
    {
        var border = new Border
        {
            BorderBrush =
                new SolidColorBrush(
                    Color.FromRgb(220, 220, 220)),

            BorderThickness =
                new Thickness(1),

            CornerRadius =
                new CornerRadius(8),

            Padding =
                new Thickness(16),

            Margin =
                new Thickness(0, 0, 0, 12)
        };

        var grid = new Grid();

        grid.ColumnDefinitions.Add(
            new ColumnDefinition
            {
                Width =
                    new GridLength(
                        1,
                        GridUnitType.Star)
            });

        grid.ColumnDefinitions.Add(
            new ColumnDefinition
            {
                Width = GridLength.Auto
            });

        var left = new StackPanel();

        left.Children.Add(
            new TextBlock
            {
                Text = connection.Name,

                FontSize = 18,

                FontWeight =
                    FontWeights.SemiBold
            });

        var status = new TextBlock
        {
            Margin =
                new Thickness(0, 6, 0, 0),

            FontSize = 14
        };

        /*
         * Status logic:
         *
         * Enabled + successful test = Online / green
         * Disabled = Offline / gray
         * Enabled + failed test = Offline / red
         */

        if (!connection.Enabled)
        {
            status.Text = "● Offline";
            status.Foreground = Brushes.Gray;
        }
        else if (connection.LastTestSuccessful)
        {
            status.Text = "● Online";
            status.Foreground = Brushes.Green;
        }
        else
        {
            status.Text = "● Offline";
            status.Foreground = Brushes.Red;
        }

        left.Children.Add(status);

        Grid.SetColumn(left, 0);

        grid.Children.Add(left);

        var buttons = new StackPanel
        {
            Orientation =
                Orientation.Horizontal,

            VerticalAlignment =
                VerticalAlignment.Center
        };

        var toggleButton = new Button
        {
            Content =
                connection.Enabled
                    ? "Offline"
                    : "Online",

            Padding =
                new Thickness(12, 7, 12, 7),

            Margin =
                new Thickness(5, 0, 0, 0)
        };

        toggleButton.Click +=
            async (_, _) =>
                await ToggleConnectionAsync(connection);

        var editButton = new Button
        {
            Content = "Edit",

            Padding =
                new Thickness(12, 7, 12, 7),

            Margin =
                new Thickness(5, 0, 0, 0)
        };

        editButton.Click +=
            (_, _) =>
                EditConnection(connection);

        var deleteButton = new Button
        {
            Content = "Delete",

            Padding =
                new Thickness(12, 7, 12, 7),

            Margin =
                new Thickness(5, 0, 0, 0)
        };

        deleteButton.Click +=
            (_, _) =>
                DeleteConnection(connection);

        buttons.Children.Add(toggleButton);
        buttons.Children.Add(editButton);
        buttons.Children.Add(deleteButton);

        Grid.SetColumn(buttons, 1);

        grid.Children.Add(buttons);

        border.Child = grid;

        return border;
    }

    private void AddButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var window =
            new AddDatabaseWindow
            {
                Owner = this
            };

        if (window.ShowDialog() != true ||
            window.Result == null)
        {
            return;
        }

        try
        {
            _database.AddConnection(
                window.Result);

            LoadConnections();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "Easy FB Soft",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void EditConnection(
        DatabaseConfig connection)
    {
        var window =
            new AddDatabaseWindow(connection)
            {
                Owner = this
            };

        if (window.ShowDialog() != true ||
            window.Result == null)
        {
            return;
        }

        try
        {
            _database.UpdateConnection(
                window.Result);

            LoadConnections();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "Easy FB Soft",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void DeleteConnection(
        DatabaseConfig connection)
    {
        var result =
            MessageBox.Show(
                $"Delete \"{connection.Name}\"?\n\n" +
                "This connection will be permanently removed.",

                "Delete Database",

                MessageBoxButton.YesNo,

                MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _database.DeleteConnection(
            connection.Id);

        LoadConnections();
    }

    private async Task ToggleConnectionAsync(
        DatabaseConfig connection)
    {
        /*
         * Going Online
         */
        if (!connection.Enabled)
        {
            var confirm =
                MessageBox.Show(
                    $"Turn \"{connection.Name}\" online?\n\n" +
                    "Easy FB Soft will test the database connection first.",

                    "Turn Online",

                    MessageBoxButton.YesNo,

                    MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                var successful =
                    await TestConnectionAsync(connection);

                if (!successful)
                {
                    MessageBox.Show(
                        "The database connection failed.\n\n" +
                        "The connection will remain Offline.",

                        "Connection Failed",

                        MessageBoxButton.OK,

                        MessageBoxImage.Warning);

                    _database.SetTestResult(
                        connection.Id,
                        false);

                    LoadConnections();

                    return;
                }

                _database.SetTestResult(
                    connection.Id,
                    true);

                _database.SetEnabled(
                    connection.Id,
                    true);

                LoadConnections();
            }
            catch (Exception ex)
            {
                _database.SetTestResult(
                    connection.Id,
                    false);

                MessageBox.Show(
                    $"The database connection failed.\n\n{ex.Message}",

                    "Connection Failed",

                    MessageBoxButton.OK,

                    MessageBoxImage.Warning);

                LoadConnections();
            }

            return;
        }

        /*
         * Going Offline
         */
        var offlineConfirm =
            MessageBox.Show(
                $"Turn \"{connection.Name}\" offline?",

                "Turn Offline",

                MessageBoxButton.YesNo,

                MessageBoxImage.Question);

        if (offlineConfirm != MessageBoxResult.Yes)
        {
            return;
        }

        _database.SetEnabled(
            connection.Id,
            false);

        LoadConnections();
    }

    private static async Task<bool> TestConnectionAsync(
        DatabaseConfig config)
    {
        try
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

            return true;
        }
        catch
        {
            return false;
        }
    }

    private void DoneButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        Close();
    }
}