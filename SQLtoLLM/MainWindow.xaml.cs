using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SQLtoLLM.Core.Models;
using SQLtoLLM.Infrastructure;
using SQLtoLLM.UI;

namespace SQLtoLLM;

public partial class MainWindow : Window
{
    private readonly MssqlProvider _provider = new();
    private string _connectionString = string.Empty;
    private string _lastResult = string.Empty;

    private readonly ObservableCollection<ObjectRow> _rows = [];

    public MainWindow()
    {
        InitializeComponent();
        GridObjects.ItemsSource = _rows;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Connection mode radio
    // ──────────────────────────────────────────────────────────────────────────

    private void RadioConnMode_Changed(object sender, RoutedEventArgs e)
    {
        if (PanelConnString is null || PanelCredentials is null) return;

        var useConnString = RadioConnString.IsChecked == true;
        PanelConnString.Visibility  = useConnString ? Visibility.Visible   : Visibility.Collapsed;
        PanelCredentials.Visibility = useConnString ? Visibility.Collapsed : Visibility.Visible;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Connect
    // ──────────────────────────────────────────────────────────────────────────

    private async void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        SetStatus(TxtConnStatus, "Connecting…", "#8888AA");
        BtnConnect.IsEnabled = false;

        try
        {
            _connectionString = BuildConnectionString();

            if (string.IsNullOrWhiteSpace(_connectionString))
            {
                SetStatus(TxtConnStatus, "❌ Please fill in all connection fields.", "#F44747");
                return;
            }

            await _provider.TestConnectionAsync(_connectionString);

            SetStatus(TxtConnStatus, "✅ Connected", "#4CAF7D");
            EnableSection(CardObjects, true);
        }
        catch (Exception ex)
        {
            SetStatus(TxtConnStatus, $"❌ {ex.Message}", "#F44747");
            _connectionString = string.Empty;
            EnableSection(CardObjects, false);
            EnableSection(CardExecute, false);
        }
        finally
        {
            BtnConnect.IsEnabled = true;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Resolve Objects
    // ──────────────────────────────────────────────────────────────────────────

    private async void BtnResolve_Click(object sender, RoutedEventArgs e)
    {
        var raw = TxtObjects.Text;
        if (string.IsNullOrWhiteSpace(raw)) return;

        var names = raw.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
                       .Select(n => n.Trim())
                       .Where(n => n.Length > 0)
                       .Select(n => n.Contains('.') ? n : "dbo." + n)
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .ToList();

        BtnResolve.IsEnabled = false;
        EnableSection(CardExecute, false);
        _rows.Clear();
        GridObjects.Visibility = Visibility.Visible;

        try
        {
            var dbObjects = await _provider.ResolveObjectsAsync(names, _connectionString);

            foreach (var obj in dbObjects)
            {
                _rows.Add(new ObjectRow
                {
                    ObjectName   = obj.ObjectName,
                    DetectedType = obj.DetectedType is not null ? TypeLabel(obj.DetectedType.Value) : "—",
                    EditableType = obj.EditableType is not null ? TypeLabel(obj.EditableType.Value) : "TABLE",
                    Status       = obj.StatusDisplay
                });
            }

            RefreshExecuteButton();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error resolving objects:\n\n{ex.Message}",
                            "Resolve Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnResolve.IsEnabled = true;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Combo changed in grid (re-evaluate Execute button)
    // ──────────────────────────────────────────────────────────────────────────

    private void GridCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        RefreshExecuteButton();
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Execute Extraction
    // ──────────────────────────────────────────────────────────────────────────

    private async void BtnExecute_Click(object sender, RoutedEventArgs e)
    {
        BtnExecute.IsEnabled = false;
        TxtResult.Visibility = Visibility.Visible;
        TxtResult.Text = "Running extraction…";
        SetStatus(TxtExecStatus, "Running…", "#8888AA");
        BtnCopy.Visibility = Visibility.Collapsed;
        _lastResult = string.Empty;

        try
        {
            // Map grid rows back to DbObject list
            var objects = _rows.Select(r => new DbObject
            {
                ObjectName   = r.ObjectName,
                EditableType = ParseTypeLabel(r.EditableType),
                Status       = ObjectStatus.Resolved
            }).ToList();

            _lastResult = await _provider.ExtractContextAsync(objects, _connectionString);

            TxtResult.Text = _lastResult;
            SetStatus(TxtExecStatus, $"✅ {objects.Count} object(s) extracted.", "#4CAF7D");
            BtnCopy.Visibility = Visibility.Visible;

            // Auto-copy
            Clipboard.SetText(_lastResult);
            SetStatus(TxtExecStatus, $"✅ {objects.Count} object(s) extracted — copied to clipboard.", "#4CAF7D");
        }
        catch (Exception ex)
        {
            TxtResult.Text = string.Empty;
            SetStatus(TxtExecStatus, $"❌ {ex.Message}", "#F44747");
        }
        finally
        {
            BtnExecute.IsEnabled = true;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Copy to Clipboard
    // ──────────────────────────────────────────────────────────────────────────

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_lastResult)) return;
        Clipboard.SetText(_lastResult);
        SetStatus(TxtExecStatus, "✅ Copied to clipboard.", "#4CAF7D");
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────────────────────

    private string BuildConnectionString()
    {
        if (RadioConnString.IsChecked == true)
        {
            return TxtConnString.Text.Trim();
        }

        var server   = TxtServer.Text.Trim();
        var port     = TxtPort.Text.Trim();
        var database = TxtDatabase.Text.Trim();
        var user     = TxtUser.Text.Trim();
        var password = TxtPassword.Password.Trim();

        if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(database))
            return string.Empty;
            
        var serverString = string.IsNullOrEmpty(port) ? server : $"{server},{port}";

        if (string.IsNullOrEmpty(user))
            // Windows Authentication
            return $"Server={serverString};Database={database};Integrated Security=true;TrustServerCertificate=true;";

        return $"Server={serverString};Database={database};User Id={user};Password={password};TrustServerCertificate=true;";
    }

    private void RefreshExecuteButton()
    {
        if (_rows.Count == 0)
        {
            EnableSection(CardExecute, false);
            return;
        }

        var allResolved = _rows.All(r => r.Status.StartsWith("✅"));
        EnableSection(CardExecute, allResolved);
    }

    private static void EnableSection(Border card, bool enabled)
    {
        card.IsEnabled = enabled;
        card.Opacity   = enabled ? 1.0 : 0.5;
    }

    private static void SetStatus(TextBlock block, string message, string hexColor)
    {
        block.Text = message;
        block.Foreground = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(hexColor));
    }

    private static string TypeLabel(ObjectType t) => t switch
    {
        ObjectType.Table     => "TABLE",
        ObjectType.View      => "VIEW",
        ObjectType.Procedure => "PROCEDURE",
        ObjectType.Index     => "INDEX",
        ObjectType.Trigger   => "TRIGGER",
        _                    => "TABLE"
    };

    private static ObjectType? ParseTypeLabel(string s) => s.ToUpperInvariant() switch
    {
        "TABLE"     => ObjectType.Table,
        "VIEW"      => ObjectType.View,
        "PROCEDURE" => ObjectType.Procedure,
        "INDEX"     => ObjectType.Index,
        "TRIGGER"   => ObjectType.Trigger,
        _           => null
    };
}