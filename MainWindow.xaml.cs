using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FirebirdSql.Data.FirebirdClient;
using FbGateway.Configuration;
using FbGateway.Data;

namespace FbGateway;

public partial class MainWindow : Window
{
    private readonly SqliteDatabase _database;

    private List<DatabaseConfig> _connections = new();

    public MainWindow()
    {
        InitializeComponent();

        _database = new SqliteDatabase();

        LoadConnections();
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
                "FbGateway",
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
                "FbGateway",
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
                    "FbGateway will test the database connection first.",

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