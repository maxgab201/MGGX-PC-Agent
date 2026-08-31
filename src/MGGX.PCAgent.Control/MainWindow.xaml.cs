using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.ServiceProcess;
using MGGX.PCAgent.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace MGGX.PCAgent.Control;

public sealed partial class MainWindow : Window
{
    private readonly AgentConfig _config;
    private readonly INetworkInfoProvider _network = new WindowsNetworkProbe();
    private readonly DispatcherTimer _pairingTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private long? _currentExpiresAtEpochMs;
    private bool _neverPaired = true;

    public MainWindow()
    {
        InitializeComponent();
        _config = AgentConfigLoader.Load(AgentConstants.DataDirectory);
        PortBox.Value = _config.Port; DiscoveryCheck.IsChecked = _config.DiscoveryEnabled;
        AgentVersionText.Text = AgentConstants.Version;
        SetSize();
        PopulateAdapterCombo();
        _pairingTimer.Tick += (_, _) => UpdateExpiryCountdown();
        _ = RefreshAsync();
        _ = RefreshDevicesAsync();
    }

    private void SetSize()
    {
        var id = Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this));
        var appWindow = AppWindow.GetFromWindowId(id);
        appWindow.Resize(new Windows.Graphics.SizeInt32(720, 760));
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath)) appWindow.SetIcon(iconPath);
    }

    private void PopulateAdapterCombo()
    {
        var snapshot = _network.GetSnapshot(null);
        AdapterCombo.Items.Clear();
        AdapterCombo.Items.Add("Automático (recomendado)");
        foreach (var adapter in snapshot.AvailableAdapters)
            AdapterCombo.Items.Add($"{adapter.DisplayName} — {adapter.Ipv4Address}");
        var selectedIndex = 0;
        if (_config.LanAdapterId is { Length: > 0 })
        {
            var index = snapshot.AvailableAdapters.ToList().FindIndex(a => a.Id == _config.LanAdapterId);
            if (index >= 0) selectedIndex = index + 1;
        }
        AdapterCombo.SelectedIndex = selectedIndex;
    }

    private async Task RefreshAsync()
    {
        try
        {
            using var sc = new ServiceController(AgentConstants.ServiceName);
            ServiceText.Text = sc.Status == ServiceControllerStatus.Running ? "Running ✓" : sc.Status.ToString();
        }
        catch { ServiceText.Text = "Stopped ✕"; }

        var snapshot = _network.GetSnapshot(_config.LanAdapterId);
        LanText.Text = snapshot.LanIp ?? "No detectada";
        TailscaleText.Text = snapshot.TailscaleIp is { } ip ? $"{ip}  Connected ✓" : "No conectado";
        SunshineText.Text = Process.GetProcessesByName("sunshine").Length > 0 ? "Running ✓" : "No iniciado";

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var response = await client.GetAsync($"http://127.0.0.1:{_config.Port}/health");
            OnlineText.Text = response.IsSuccessStatusCode ? "● Agent ONLINE" : "● Agent ERROR";
            OnlineText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(response.IsSuccessStatusCode ? Colors.LimeGreen : Colors.OrangeRed);
        }
        catch { OnlineText.Text = "● Agent OFFLINE"; OnlineText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.OrangeRed); }
    }

    private async Task RefreshDevicesAsync()
    {
        var response = await PairingPipeClient.SendAsync(new PairingPipeRequest("listCredentials"));
        DevicesPanel.Children.Clear();
        var devices = response.Credentials ?? [];
        _neverPaired = devices.Count == 0;
        NoDevicesText.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FirstRunGuide.IsOpen = _neverPaired;
        foreach (var device in devices)
        {
            var row = new Grid { Padding = new Thickness(4), ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var texts = new StackPanel();
            texts.Children.Add(new TextBlock { Text = device.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            texts.Children.Add(new TextBlock { Text = $"Vinculado el {device.CreatedAtUtc.LocalDateTime:d MMM yyyy}", Opacity = 0.7, FontSize = 12 });
            Grid.SetColumn(texts, 0);

            var revoke = new Button { Content = "REVOCAR", Tag = device.CredentialId };
            revoke.Click += RevokeDevice_Click;
            Grid.SetColumn(revoke, 1);

            row.Children.Add(texts);
            row.Children.Add(revoke);
            DevicesPanel.Children.Add(row);
        }
    }

    private async void RevokeDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string credentialId }) return;
        var response = await PairingPipeClient.SendAsync(new PairingPipeRequest("revoke", credentialId));
        ShowFeedback(response.Ok ? "Dispositivo revocado." : "No se pudo revocar el dispositivo.", response.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        await RefreshDevicesAsync();
    }

    private async void PairButton_Click(object sender, RoutedEventArgs e) => await GenerateOfferAsync();
    private async void RegenerateOffer_Click(object sender, RoutedEventArgs e) => await GenerateOfferAsync();

    private async Task GenerateOfferAsync()
    {
        var response = await PairingPipeClient.SendAsync(new PairingPipeRequest("generate"));
        if (!response.Ok || response.Offer is null)
        {
            ShowFeedback(response.Error switch
            {
                "no_lan_adapter" => "No se detectó una red LAN válida. Revisá Configuración → Red.",
                "service_unreachable" => "No se pudo conectar con MGGX PC Agent Service.",
                _ => "No se pudo generar el código de vinculación."
            }, InfoBarSeverity.Error);
            return;
        }
        await ShowOfferAsync(response.Offer);
    }

    private async Task ShowOfferAsync(PairingPipeOfferDto offer)
    {
        _currentExpiresAtEpochMs = offer.ExpiresAtEpochMs;
        DisplayCodeText.Text = $"{offer.DisplayCode[..3]} {offer.DisplayCode[3..]}";
        PairingPcText.Text = $"PC: {_config.PcName}";
        PairingNetworkText.Text = $"Red: {offer.Host}";
        QrImage.Source = await BuildQrImageAsync(offer.QrPayload);
        UpdateExpiryCountdown();
        PairingOverlay.Visibility = Visibility.Visible;
        _pairingTimer.Start();
    }

    private static async Task<BitmapImage> BuildQrImageAsync(string payload)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data).GetGraphic(10);
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer());
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    private void UpdateExpiryCountdown()
    {
        if (_currentExpiresAtEpochMs is not { } expiresAt) return;
        var remaining = DateTimeOffset.FromUnixTimeMilliseconds(expiresAt) - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            ExpiryText.Text = "Este código venció. Generá otro.";
            _pairingTimer.Stop();
            return;
        }
        ExpiryText.Text = $"Expira en: {remaining:mm\\:ss}";
    }

    private void CancelPairing_Click(object sender, RoutedEventArgs e)
    {
        _pairingTimer.Stop();
        _currentExpiresAtEpochMs = null;
        PairingOverlay.Visibility = Visibility.Collapsed;
        _ = PairingPipeClient.SendAsync(new PairingPipeRequest("cancel"));
    }

    private async void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage(); package.SetText(DisplayCodeText.Text.Replace(" ", ""));
        Clipboard.SetContent(package); Clipboard.Flush();
        ShowFeedback("Código copiado.", InfoBarSeverity.Success);
        await Task.CompletedTask;
    }

    private void CopyToken_Click(object sender, RoutedEventArgs e) => Elevated("--copy-token", "Approve Windows security to copy the protected token.");
    private void TestAgent_Click(object sender, RoutedEventArgs e) => Elevated("--test-agent", "Testing health and authenticated status…");
    private void Restart_Click(object sender, RoutedEventArgs e) => Elevated("--restart-service", "Restarting service…");
    private void OpenLogs_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(Path.Combine(AgentConstants.DataDirectory, "logs")) { UseShellExecute = true });

    private async void TestPairing_Click(object sender, RoutedEventArgs e)
    {
        var offer = await PairingPipeClient.SendAsync(new PairingPipeRequest("generate"));
        if (!offer.Ok || offer.Offer is null) { ShowFeedback("Test de pairing: no se pudo crear una oferta.", InfoBarSeverity.Error); return; }
        var status = await PairingPipeClient.SendAsync(new PairingPipeRequest("status"));
        var cancelled = await PairingPipeClient.SendAsync(new PairingPipeRequest("cancel"));
        var ok = offer.Ok && status.Ok && status.Offer is not null && cancelled.Ok;
        ShowFeedback(ok ? "Test de pairing: crear/validar/cancelar funcionó correctamente." : "Test de pairing: falló alguno de los pasos.", ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private void SaveConfig_Click(object sender, RoutedEventArgs e)
    {
        if (double.IsNaN(PortBox.Value) || PortBox.Value is < 1 or > 65535) { ShowFeedback("Enter a valid port.", InfoBarSeverity.Error); return; }
        var snapshot = _network.GetSnapshot(null);
        var adapterId = AdapterCombo.SelectedIndex <= 0 ? "" : snapshot.AvailableAdapters[AdapterCombo.SelectedIndex - 1].Id;
        Elevated($"--set-config {(int)PortBox.Value} {DiscoveryCheck.IsChecked == true} {(adapterId.Length == 0 ? "-" : adapterId)}", "Saving configuration and restarting service…");
    }

    private void ShowFeedback(string message, InfoBarSeverity severity)
    {
        Feedback.Message = message; Feedback.Severity = severity; Feedback.IsOpen = true;
    }

    private void Elevated(string arg, string message)
    {
        try { ShowFeedback(message, InfoBarSeverity.Informational); App.LaunchElevated(arg); }
        catch { ShowFeedback("Administrator approval was cancelled.", InfoBarSeverity.Warning); }
    }
}
