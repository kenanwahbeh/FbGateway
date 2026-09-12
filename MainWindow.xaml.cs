using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FirebirdSql.Data.FirebirdClient;
using ByteBridge.Configuration;
using ByteBridge.Data;
using ByteBridge.Gateway;
using ByteBridge.Localization;

namespace ByteBridge;

public partial class MainWindow : Window
{
    private readonly SqliteDatabase _database;

    private readonly GatewayServiceControl _service = new();

    /*
     * The window no longer holds the gateway; the service does. This
     * refreshes what the window shows about it, because the service
     * reacts to a settings change on its own schedule rather than when
     * a button is clicked.
     */
    private readonly DispatcherTimer _refresh = new()
    {
        Interval = TimeSpan.FromSeconds(2)
    };

    private List<DatabaseConfig> _connections = new();

    private bool _isClosing = false;

    public MainWindow()
    {
        InitializeComponent();

        _database = new SqliteDatabase();

        // Load saved language
        var savedLanguage = _database.GetSetting("App.Language");
        if (!string.IsNullOrEmpty(savedLanguage))
        {
            Strings.SetLanguage(savedLanguage);
        }

        ApplyLocalization();

        GatewayPortTextBox.Text =
            _database.GetGatewayConfig().Port.ToString();

        _refresh.Tick += async (_, _) => await UpdateGatewayUi();

        LoadConnections();

        LoadOAuthConfig();

        _ = UpdateGatewayUi();

        _refresh.Start();
    }

    /*
     * The gateway starts with the machine now, so there is nothing to
     * start here. What is worth doing is telling someone when the
     * service that should be hosting it is not installed or not
     * running, since the window otherwise looks the same either way.
     */
    private async void Window_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        await UpdateGatewayUi();
    }

    private void Window_Closing(
        object sender,
        System.ComponentModel.CancelEventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        // Show the close dialog
        var dialog = new CloseDialogWindow
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            // User cancelled
            e.Cancel = true;
            return;
        }

        switch (dialog.Result)
        {
            case CloseDialogResult.MinimizeToTray:
                // Minimize to tray instead of closing
                e.Cancel = true;
                WindowState = WindowState.Minimized;
                ShowInTaskbar = false;
                break;

            case CloseDialogResult.Settings:
                // Open settings window
                e.Cancel = true;
                OpenSettings();
                break;

            case CloseDialogResult.Exit:
                // Actually close
                _isClosing = true;
                _refresh.Stop();
                _service.Dispose();
                break;

            case CloseDialogResult.Cancel:
                e.Cancel = true;
                break;
        }
    }

    private void Window_Closed(
        object sender,
        EventArgs e)
    {
        /*
         * Deliberately does not stop the gateway. Closing this window
         * used to kill it, which is the whole reason a tunnel went dead
         * when someone tidied up their desktop.
         */
        _refresh.Stop();

        _service.Dispose();
    }

    private void OpenSettings()
    {
        var settingsWindow = new SettingsWindow(
            _database,
            () =>
            {
                ApplyLocalization();
                LoadConnections();
                LoadOAuthConfig();
            })
        {
            Owner = this
        };

        settingsWindow.ShowDialog();

        // Refresh UI after settings change
        _ = UpdateGatewayUi();
    }

    /*
     * Records whether the gateway should be listening and leaves the
     * service to act on it, rather than starting or stopping a listener
     * in this process. The service notices within a few seconds; the
     * refresh timer is what makes the window catch up.
     */
    private async void GatewayToggleButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var config = _database.GetGatewayConfig();

        if (config.AutoStart)
        {
            config.AutoStart = false;

            _database.SaveGatewayConfig(config);

            await UpdateGatewayUi();

            return;
        }

        var portText = GatewayPortTextBox.Text.Trim();

        if (!int.TryParse(portText, out var port)
            || port < 1
            || port > 65535)
        {
            MessageBox.Show(
                "Please enter a valid port between 1 and 65535.",
                "ByteBridge",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            return;
        }

        config.Port = port;
        config.AutoStart = true;

        _database.SaveGatewayConfig(config);

        await UpdateGatewayUi();
    }

    private async void StartServiceButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            _service.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "The ByteBridge service could not be started.\n\n"
                + ex.Message,
                "Service",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        await UpdateGatewayUi();
    }

    private void CopyKeyButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_database.GetGatewayConfig().ApiKey);

            MessageBox.Show(
                "API key copied.\n\n" +
                "Send it on every request as the X-API-Key header.",

                "ByteBridge",

                MessageBoxButton.OK,

                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"The key could not be copied.\n\n{ex.Message}",

                "ByteBridge",

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

        /*
         * The service compares the key on every request and reloads it
         * without rebinding, so a rotation takes effect within seconds
         * and no restart is needed.
         */
        _database.RegenerateApiKey();

        MessageBox.Show(
            "A new API key was generated.\n\n" +
            "Use Copy API Key to put it on the clipboard.",

            "ByteBridge",

            MessageBoxButton.OK,

            MessageBoxImage.Information);
    }

    /*
     * Shows two facts that are easy to confuse, and keeps them apart:
     * whether Windows is running the service, and whether the gateway
     * inside it is actually answering. A running service with a gateway
     * that could not bind is precisely the state that makes a tunnel
     * return 502, so collapsing the two would hide it.
     */
    private async Task UpdateGatewayUi()
    {
        var config = _database.GetGatewayConfig();
        var state = _service.State();

        /*
         * Everything that can be known without asking the gateway is
         * painted before anything is asked of it, so opening the window
         * never waits on a network call to show something. The gateway
         * line below keeps whatever it last said until the answer comes
         * back, which is why this does not flicker on the timer.
         */
        RenderServiceState(state);

        var answering =
            state == ServiceState.Running
            && await _service.IsAnsweringAsync(config.BaseUrl);

        RenderGatewayState(config, state, answering);
    }

    private void RenderServiceState(ServiceState state)
    {
        StartServiceButton.Visibility =
            state == ServiceState.Stopped
                ? Visibility.Visible
                : Visibility.Collapsed;

        ServiceStatusTextBlock.Text = state switch
        {
            ServiceState.Running => "Service: running",
            ServiceState.Stopped => "Service: stopped",
            ServiceState.Pending => "Service: starting or stopping",
            _ => "Service: not installed — reinstall ByteBridge to add it"
        };
    }

    private void RenderGatewayState(
        GatewayConfig config,
        ServiceState state,
        bool answering)
    {
        if (answering)
        {
            GatewayStatusTextBlock.Text =
                $"● Answering — {config.BaseUrl}";

            GatewayStatusTextBlock.Foreground = Brushes.Green;

            GatewayHintTextBlock.Text =
                "Point the tunnel here:  "
                + $"cloudflared tunnel --url {config.BaseUrl}";
        }
        else if (!config.AutoStart)
        {
            GatewayStatusTextBlock.Text = "● Turned off";

            GatewayStatusTextBlock.Foreground = Brushes.Gray;

            GatewayHintTextBlock.Text =
                "The gateway is set not to listen. A tunnel pointed at "
                + "this machine will return 502 until it is turned on.";
        }
        else if (state == ServiceState.Running)
        {
            /*
             * Wanted, and the service is up, but nothing answers. Either
             * it is still within a poll of noticing, or the bind failed
             * and it is retrying. The event log carries the reason.
             */
            GatewayStatusTextBlock.Text = "● Starting, or unable to bind";

            GatewayStatusTextBlock.Foreground = Brushes.DarkOrange;

            GatewayHintTextBlock.Text =
                $"The service is running but nothing answers on {config.BaseUrl}. "
                + "Give it a few seconds; if it stays this way the port is in use "
                + "or the reservation was refused. See Event Viewer, Application, "
                 + "source ByteBridge.";
        }
        else
        {
            GatewayStatusTextBlock.Text = "● Not running";

            GatewayStatusTextBlock.Foreground = Brushes.Red;

            GatewayHintTextBlock.Text =
                "The service that hosts the gateway is not running, so a "
                + "tunnel pointed at this machine will return 502.";
        }

        GatewayToggleButton.Content =
            config.AutoStart ? "Turn Off" : "Turn On";

        /*
         * The port is only editable while the gateway is meant to be
         * off, so a change cannot half-apply under a live tunnel.
         */
        GatewayPortTextBox.IsEnabled = !config.AutoStart;
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
                "ByteBridge",
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
                "ByteBridge",
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
                    "ByteBridge will test the database connection first.",

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
        // Trigger the closing event which shows the close dialog
        Close();
    }

    /*
     * OAuth configuration and event handlers.
     */

    private void LoadOAuthConfig()
    {
        var config = _database.GetGatewayConfig();
        var oauthConfig = _database.GetOAuthConfig();

        OAuthBorder.Visibility = Visibility.Visible;

        TeamDomainTextBox.Text = oauthConfig.TeamDomain;
        AudienceTextBox.Text = oauthConfig.Audience;

        if (oauthConfig.Enabled)
        {
            OAuthStatusTextBlock.Text = "Enabled";
            OAuthStatusTextBlock.Foreground = Brushes.Green;
            OAuthToggleButton.Content = "Disable";
            TeamDomainTextBox.IsEnabled = false;
            AudienceTextBox.IsEnabled = false;
        }
        else
        {
            OAuthStatusTextBlock.Text = "Not configured";
            OAuthStatusTextBlock.Foreground = Brushes.Gray;
            OAuthToggleButton.Content = "Enable";
            TeamDomainTextBox.IsEnabled = true;
            AudienceTextBox.IsEnabled = true;
        }
    }

    private void OAuthToggleButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var config = _database.GetOAuthConfig();

        if (config.Enabled)
        {
            // Disable OAuth
            config.Enabled = false;
            _database.SaveOAuthConfig(config);

            MessageBox.Show(
                Strings.Get("OAuthDisabled"),
                Strings.Get("AppTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        else
        {
            // Enable OAuth
            var teamDomain = TeamDomainTextBox.Text.Trim();
            var audience = AudienceTextBox.Text.Trim();

            if (string.IsNullOrEmpty(teamDomain))
            {
                MessageBox.Show(
                    Strings.Get("OAuthTeamDomainRequired"),
                    Strings.Get("AppTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                return;
            }

            if (string.IsNullOrEmpty(audience))
            {
                MessageBox.Show(
                    Strings.Get("OAuthAudienceRequired"),
                    Strings.Get("AppTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                return;
            }

            config.Enabled = true;
            config.TeamDomain = teamDomain;
            config.Audience = audience;
            config.JwksUri =
                $"https://{teamDomain}/cdn-cgi/access/certs";

            _database.SaveOAuthConfig(config);

            MessageBox.Show(
                Strings.Get("OAuthEnabled"),
                Strings.Get("AppTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        LoadOAuthConfig();
    }

    /*
     * Applies localized strings to all UI elements.
     */
    private void ApplyLocalization()
    {
        Title = Strings.Get("AppTitle");
        AddDataButton.Content = Strings.Get("AddData");
        DoneButton.Content = Strings.Get("Done");

        // Gateway
        GatewayApiTextBlock.Text = Strings.Get("GatewayApi");
        PortTextBlock.Text = Strings.Get("Port");
        CopyKeyButton.Content = Strings.Get("CopyApiKey");
        RegenerateKeyButton.Content = Strings.Get("NewKey");
        StartServiceButton.Content = Strings.Get("StartService");

        // OAuth
        CloudflareLoginTextBlock.Text = Strings.Get("CloudflareLogin");
        TeamDomainTextBlock.Text = Strings.Get("TeamDomain");
        AudienceTextBlock.Text = Strings.Get("Audience");

        // Refresh dynamic text
        _ = UpdateGatewayUi();
    }
}