using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.ServiceProcess;
using MGGX.PCAgent.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace MGGX.PCAgent.Control;

public sealed partial class MainWindow : Window
{
    private readonly AgentConfig _config;
    public MainWindow()
    {
        InitializeComponent();
        _config = AgentConfigLoader.Load(AgentConstants.DataDirectory);
        PortBox.Value = _config.Port; DiscoveryCheck.IsChecked = _config.DiscoveryEnabled;
        ApiText.Text = $"http://{GetLanIp() ?? "PC"}:{_config.Port}";
        UptimeText.Text = FormatUptime(Environment.TickCount64 / 1000);
        TailscaleText.Text = GetTailscaleIp() is { } ip ? $"{ip}  Connected ✓" : "Not connected";
        SunshineText.Text = Process.GetProcessesByName("sunshine").Length > 0 ? "Running ✓" : "Not running";
        SetSize();
        _ = RefreshAsync();
    }

    private void SetSize()
    {
        var id = Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this));
        AppWindow.GetFromWindowId(id).Resize(new Windows.Graphics.SizeInt32(680, 650));
    }

    private async Task RefreshAsync()
    {
        try
        {
            using var sc = new ServiceController(AgentConstants.ServiceName);
            ServiceText.Text = sc.Status == ServiceControllerStatus.Running ? "Running ✓" : sc.Status.ToString();
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var response = await client.GetAsync($"http://127.0.0.1:{_config.Port}/health");
            OnlineText.Text = response.IsSuccessStatusCode ? "● Agent ONLINE" : "● Agent ERROR";
            OnlineText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(response.IsSuccessStatusCode ? Colors.LimeGreen : Colors.OrangeRed);
        }
        catch { ServiceText.Text = "Stopped ✕"; OnlineText.Text = "● Agent OFFLINE"; OnlineText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.OrangeRed); }
    }

    private void CopyToken_Click(object sender, RoutedEventArgs e) => Elevated("--copy-token", "Approve Windows security to copy the protected token.");
    private void TestAgent_Click(object sender, RoutedEventArgs e) => Elevated("--test-agent", "Testing health and authenticated status…");
    private void Restart_Click(object sender, RoutedEventArgs e) => Elevated("--restart-service", "Restarting service…");
    private void OpenLogs_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(Path.Combine(AgentConstants.DataDirectory, "logs")) { UseShellExecute = true });
    private void SaveConfig_Click(object sender, RoutedEventArgs e)
    {
        if (double.IsNaN(PortBox.Value) || PortBox.Value is < 1 or > 65535) { Feedback.Message = "Enter a valid port."; Feedback.Severity = InfoBarSeverity.Error; Feedback.IsOpen = true; return; }
        Elevated($"--set-config {(int)PortBox.Value} {DiscoveryCheck.IsChecked == true}", "Saving configuration and restarting service…");
    }

    private void Elevated(string arg, string message)
    {
        try { Feedback.Message = message; Feedback.Severity = InfoBarSeverity.Informational; Feedback.IsOpen = true; App.LaunchElevated(arg); }
        catch { Feedback.Message = "Administrator approval was cancelled."; Feedback.Severity = InfoBarSeverity.Warning; Feedback.IsOpen = true; }
    }

    private static string FormatUptime(long seconds) => $"{seconds / 3600}h {(seconds % 3600) / 60}m";
    private static string? GetLanIp() => GetIps().FirstOrDefault(ip => { var b = ip.GetAddressBytes(); return b[0] == 192 && b[1] == 168 || b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31; })?.ToString();
    private static string? GetTailscaleIp() => GetIps().FirstOrDefault(ip => { var b = ip.GetAddressBytes(); return b[0] == 100 && b[1] is >= 64 and <= 127; })?.ToString();
    private static IEnumerable<System.Net.IPAddress> GetIps() => NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up).SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(x => x.Address).Where(x => x.AddressFamily == AddressFamily.InterNetwork);
}
