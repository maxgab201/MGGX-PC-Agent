using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.Json;
using MGGX.PCAgent.Core;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace MGGX.PCAgent.Control;

public partial class App : Application
{
    private Window? _window;
    public App() => InitializeComponent();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var command = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (command.Length > 0)
        {
            await RunAdminActionAsync(command);
            Exit();
            return;
        }
        _window = new MainWindow();
        _window.Activate();
    }

    public static void LaunchElevated(string argument)
    {
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, argument) { UseShellExecute = true, Verb = "runas" });
    }

    private static async Task RunAdminActionAsync(string[] args)
    {
        try
        {
            switch (args[0])
            {
                case "--copy-token":
                    var token = new DpapiTokenStore(AgentConstants.DataDirectory).GetOrCreate();
                    var package = new DataPackage(); package.SetText(token); Clipboard.SetContent(package); Clipboard.Flush();
                    MessageBox(IntPtr.Zero, "Agent token copied to the clipboard.", "MGGX PC Agent", 0x40); break;
                case "--restart-service":
                    using (var sc = new ServiceController(AgentConstants.ServiceName))
                    {
                        if (sc.Status != ServiceControllerStatus.Stopped) { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20)); }
                        sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                    }
                    MessageBox(IntPtr.Zero, "Service restarted successfully.", "MGGX PC Agent", 0x40); break;
                case "--test-agent":
                    var cfg = AgentConfigLoader.Load(AgentConstants.DataDirectory);
                    var key = new DpapiTokenStore(AgentConstants.DataDirectory).GetOrCreate();
                    using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) })
                    {
                        client.DefaultRequestHeaders.Authorization = new("Bearer", key);
                        var health = await client.GetAsync($"http://127.0.0.1:{cfg.Port}/health");
                        var status = await client.GetAsync($"http://127.0.0.1:{cfg.Port}/api/v1/status");
                        if (!health.IsSuccessStatusCode || !status.IsSuccessStatusCode) throw new InvalidOperationException($"Health {(int)health.StatusCode}; Status {(int)status.StatusCode}");
                    }
                    MessageBox(IntPtr.Zero, "Health and authenticated status tests passed.", "MGGX PC Agent", 0x40); break;
                case "--set-config":
                    if (args.Length != 4 || !int.TryParse(args[1], out var port) || port is < 1 or > 65535 || !bool.TryParse(args[2], out var discovery))
                        throw new ArgumentException("Invalid port or discovery value.");
                    var updated = AgentConfigLoader.Load(AgentConstants.DataDirectory);
                    updated.Port = port; updated.DiscoveryEnabled = discovery;
                    updated.LanAdapterId = args[3] == "-" ? null : args[3];
                    File.WriteAllText(Path.Combine(AgentConstants.DataDirectory, "config.json"), JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true }));
                    await RunHiddenAsync("netsh.exe", $"advfirewall firewall set rule name=\"MGGX PC Agent API - Private LAN\" new localport={port}");
                    await RunHiddenAsync("netsh.exe", $"advfirewall firewall set rule name=\"MGGX PC Agent API - Tailscale\" new localport={port}");
                    using (var sc = new ServiceController(AgentConstants.ServiceName))
                    {
                        if (sc.Status != ServiceControllerStatus.Stopped) { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20)); }
                        sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                    }
                    MessageBox(IntPtr.Zero, "Configuration saved and service restarted.", "MGGX PC Agent", 0x40); break;
            }
        }
        catch (Exception ex) { MessageBox(IntPtr.Zero, LogSanitizer.Sanitize(ex.Message), "MGGX PC Agent — Error", 0x10); }
    }

    private static async Task RunHiddenAsync(string file, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false, CreateNoWindow = true }) ?? throw new InvalidOperationException("Could not update Windows Firewall.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException("Windows Firewall update failed.");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}
