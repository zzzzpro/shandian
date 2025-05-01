using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json;

namespace Client
{
    internal class Program
    {
        internal static Host host = new Host();
        internal static Status status = new Status();
        internal static Config config = new Config();

        private static string Platform = "";
        private static readonly string Version = "";

        private static readonly string[] ExcludeNetInterfaces =
            {"lo", "tun", "docker", "veth", "br-", "vmbr", "vnet", "kube"};

        private static string NetName = "";

        private static Process bashProcess;
        private static StreamReader bashOutput;
        private static StreamWriter bashInput;

        private static double lastCpuTotalTime = 0;
        private static double lastCpuIdleTime = 0;

        private static CancellationTokenSource _appCts = new CancellationTokenSource();


        private static async Task Main(string[] args)
        {
            // Console.WriteLine("Starting client..."); // Keep minimal initial output

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) Platform = "linux";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) Platform = "windows";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) Platform = "osx";

            var configPath = "";
            if (Platform == "linux")
            {
                configPath = "/usr/local/shandian_status/config";
            }
            else if (Platform == "windows")
            {
                configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "shandian_status", "config");
                try { Directory.CreateDirectory(Path.GetDirectoryName(configPath)); } catch { }
            }
            else
            {
                Console.WriteLine($"Unsupported platform: {Platform}");
                return;
            }

            if (args.Length != 0)
            {
                if (args.Length < 1 || string.IsNullOrWhiteSpace(args[0]))
                {
                    Console.WriteLine("Usage: Client <server_url>");
                    return;
                }
                config.ServerUrl = args[0];
                config.Uuid = Guid.NewGuid().ToString("N");
                try
                {
                    File.WriteAllText(configPath, JsonConvert.SerializeObject(config, Formatting.Indented));
                    Console.WriteLine($"Config saved."); // Minimal log
                }
                catch
                {
                    Console.WriteLine($"Error writing config file.");
                    return;
                }
                // Console.WriteLine("Client configured. Please run without arguments next time."); // Remove suggestion
                return;
            }

            if (!File.Exists(configPath))
            {
                Console.WriteLine($"Config not found. Run with server URL to create."); // Minimal log
                return;
            }

            try
            {
                config = JsonConvert.DeserializeObject<Config>(File.ReadAllText(configPath));
                // Console.WriteLine("Config loaded."); // Remove message
            }
            catch
            {
                Console.WriteLine($"Error loading config file.");
                return;
            }

            if (string.IsNullOrWhiteSpace(config.ServerUrl) || string.IsNullOrWhiteSpace(config.Uuid))
            {
                Console.WriteLine("Server URL or UUID is not configured.");
                return;
            }

            if (!config.ServerUrl.EndsWith("/ws/client", StringComparison.OrdinalIgnoreCase))
            {
                config.ServerUrl = $"{config.ServerUrl.TrimEnd('/')}/ws/client";
            }
            // Console.WriteLine($"Connecting to SignalR Hub..."); // Remove message

            if (Platform == "linux")
            {
                InitializeBashProcess(); // Only logs failure
                if (_appCts.IsCancellationRequested) return; // Exit if bash init failed
            }

            GetHost(); // No logs inside

            host.V = Version;
            host.Uuid = config.Uuid;

            SignalRClient.Instance.InitializeConnection(config.ServerUrl, _appCts.Token);

            Task statusTask = Task.Factory.StartNew(GetStatus, TaskCreationOptions.LongRunning);
            Task reportTask = Task.Factory.StartNew(Report, TaskCreationOptions.LongRunning);

            Console.CancelKeyPress += (sender, eventArgs) =>
            {
                eventArgs.Cancel = true;
                Console.WriteLine("Shutdown requested."); // Minimal log
                _appCts.Cancel();
            };

            try
            {
                await Task.Delay(-1, _appCts.Token);
            }
            catch (TaskCanceledException)
            {
                // Expected exception on shutdown
            }
            finally
            {
                // Clean up bash process on exit
                if (Platform == "linux" && bashProcess != null) // Check for null before HasExited
                {
                    if (!bashProcess.HasExited)
                    {
                        try { bashInput.WriteLine("exit"); bashInput.Flush(); } catch { } // Try graceful exit
                        bashProcess.WaitForExit(1000); // Wait a bit
                        if (!bashProcess.HasExited)
                        {
                            try { bashProcess.Kill(); } catch { } // Force kill
                        }
                    }
                    try { bashProcess.Dispose(); } catch { } // Dispose resources
                }

                SignalRClient.Instance.Dispose();
                // Console.WriteLine("Shutdown complete."); // Minimal log
            }
        }

        private static void InitializeBashProcess()
        {
            if (bashProcess != null && !bashProcess.HasExited) return;

            try
            {
                if (bashProcess != null) try { bashProcess.Dispose(); } catch { }

                bashProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/bin/bash",
                        RedirectStandardOutput = true,
                        RedirectStandardInput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = "/"
                    }
                };
                bashProcess.Start();
                Thread.Sleep(100);
                if (bashProcess.StandardOutput != null) bashProcess.StandardOutput.DiscardBufferedData();
                // Console.WriteLine("Bash process initialized."); // Remove log
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to initialize bash: {ex.Message}"); // Keep critical failure log
                _appCts.Cancel();
            }
        }

        public static string Bash(string cmd)
        {
            if (Platform != "linux" || bashProcess == null || bashInput == null || bashOutput == null || bashProcess.HasExited || _appCts.IsCancellationRequested)
            {
                return "";
            }

            try
            {
                bashOutput.DiscardBufferedData();
                bashInput.WriteLine(cmd);
                string endMarker = $"EndOfCommand_{Guid.NewGuid():N}";
                bashInput.WriteLine($"echo {endMarker}");
                bashInput.Flush();

                string result = "";
                string line;
                while (!_appCts.IsCancellationRequested && (line = bashOutput.ReadLine()) != null)
                {
                    if (line.Trim() == endMarker)
                        break;
                    result += line + "\n";
                }

                if (_appCts.IsCancellationRequested) return "";

                return result.TrimEnd('\n');
            }
            catch (IOException)
            {
                InitializeBashProcess(); // Attempt re-init silently
                return "";
            }
            catch
            {
                return ""; // Silent failure
            }
        }

        private static void GetHost()
        {
            // Console.WriteLine("Gathering host information..."); // Remove log
            switch (Platform)
            {
                case "linux":
                    GetHostLinux();
                    break;
                case "windows":
                    GetHostWindows();
                    break;
                case "osx":
                    break;
                default:
                    break;
            }
        }

        private static void GetHostWindows()
        {
            host.Platform = "Windows";
            host.Cpu = "";
            host.MemTotal = 0;
            host.DiskTotal = 0;
            host.SwapTotal = 0;
            host.BootTime = 0;
            host.Arch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "";
            host.Ip = "";
        }

        private static async void Report()
        {
            while (!_appCts.IsCancellationRequested)
            {
                try
                {
                    if (!string.IsNullOrEmpty(status.Uuid) && SignalRClient.Instance.IsConnected)
                    {
                        await SignalRClient.Instance.Report(status);
                    }
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch { } // Silent failure
                finally
                {
                    try { await Task.Delay(config.ReportTime, _appCts.Token); } catch (TaskCanceledException) {  }
                }
            }
        }

        private static async void GetStatus()
        {
            while (!_appCts.IsCancellationRequested)
            {
                try
                {
                    switch (Platform)
                    {
                        case "linux":
                            GetStatusLinux();
                            break;
                        case "windows":
                            //GetStatusWindows();
                            break;
                        case "osx":
                            break;
                        default:
                            break;
                    }
                    status.Uuid = config.Uuid;
                    status.UpdateTime = DateTime.Now.ToUniversalTime();
                    // status.V = Version;
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch { } // Silent failure
                finally
                {
                    try { await Task.Delay(config.ReportTime, _appCts.Token); } catch (TaskCanceledException) { ; }
                }
            }
        }

        private static void GetStatusLinux()
        {
            DateTime currentDateTime = DateTime.Now.ToUniversalTime();
            TimeSpan diff = currentDateTime - status.UpdateTime;
            double diffSeconds = diff.TotalSeconds;

            var cmd =
                 "grep \"cpu \" /proc/stat | awk '{total=0; for(i=2;i<=NF;i++){total+=$i}; print total, $5}';" +
                (string.IsNullOrEmpty(NetName) ? "echo '0 0';" : $"cat /proc/net/dev | grep \"{NetName}\" | sed 's/:/ /g' | awk '{{print $2,$10}}';") +
                "free -m | awk '/Mem/ {print $3}';" +
                "free -m | awk '/Swap/ {print $3}';" +
                "awk '{print $1}' /proc/uptime;" +
                "LANG=C; df -P -t simfs -t ext2 -t ext3 -t ext4 -t btrfs -t xfs -t vfat -t ntfs --total 2>/dev/null | grep '^total' | awk '{ print $3 }';" +
                "LANG=C; w | head -1 | awk -F'load average:' '{print $2}' | sed 's/^[ \t]*//;s/[ \t]*$//';";

            var result = Bash(cmd);

            if (!string.IsNullOrEmpty(result))
            {
                var temp = result.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

                var cpuStatsLine = temp.ElementAtOrDefault(0)?.Trim();
                if (!string.IsNullOrEmpty(cpuStatsLine))
                {
                    var cpuTimes = cpuStatsLine.Split(' ');
                    if (cpuTimes.Length == 2 && double.TryParse(cpuTimes[0], out double currentCpuTotalTime) && double.TryParse(cpuTimes[1], out double currentCpuIdleTime))
                    {
                        if (lastCpuTotalTime > 0 && diffSeconds > 0)
                        {
                            double diffTotal = currentCpuTotalTime - lastCpuTotalTime;
                            double diffIdle = currentCpuIdleTime - lastCpuIdleTime;
                            if (diffTotal > 0) status.CpuUsed = ((diffTotal - diffIdle) / diffTotal) * 100.0;
                            else status.CpuUsed = 0;
                        }
                        else status.CpuUsed = 0;

                        lastCpuTotalTime = currentCpuTotalTime;
                        lastCpuIdleTime = currentCpuIdleTime;
                    }
                    else status.CpuUsed = 0;
                }
                else status.CpuUsed = 0;


                var netStatsLine = temp.ElementAtOrDefault(1)?.Trim();
                if (!string.IsNullOrEmpty(netStatsLine))
                {
                    var net = netStatsLine.Split(' ');
                    if (net.Length == 2 && double.TryParse(net[0], out double currentNetInTransfer) && double.TryParse(net[1], out double currentNetOutTransfer))
                    {
                        if ((status.NetInTransfer > 0 || status.NetOutTransfer > 0) && diffSeconds > 0)
                        {
                            status.NetInSpeed = (currentNetInTransfer - status.NetInTransfer) / diffSeconds;
                            status.NetOutSpeed = (currentNetOutTransfer - status.NetOutTransfer) / diffSeconds;
                        }
                        else
                        {
                            status.NetInSpeed = 0;
                            status.NetOutSpeed = 0;
                        }
                        status.NetInTransfer = currentNetInTransfer;
                        status.NetOutTransfer = currentNetOutTransfer;
                    }
                    else
                    {
                        status.NetInSpeed = 0; status.NetOutSpeed = 0;
                        status.NetInTransfer = 0; status.NetOutTransfer = 0;
                    }
                }
                else
                {
                    status.NetInSpeed = 0;
                    status.NetOutSpeed = 0;
                    status.NetInTransfer = 0;
                    status.NetOutTransfer = 0;
                }

                if (!double.TryParse(temp.ElementAtOrDefault(2), out double memUsed)) status.MemUsed = 0; else status.MemUsed = memUsed;
                if (!double.TryParse(temp.ElementAtOrDefault(3), out double swapUsed)) status.SwapUsed = 0; else status.SwapUsed = swapUsed;
                if (!double.TryParse(temp.ElementAtOrDefault(4), out double uptime)) status.Uptime = 0; else status.Uptime = uptime;
                if (!double.TryParse(temp.ElementAtOrDefault(5), out double diskUsedBlocks)) status.DiskUsed = 0; else status.DiskUsed = diskUsedBlocks / 1024.0;

                var loadLine = temp.ElementAtOrDefault(6)?.Trim();
                if (!string.IsNullOrEmpty(loadLine))
                {
                    var load = loadLine.Split(',');
                    if (load.Length >= 3)
                    {
                        double tempLoad1, tempLoad5, tempLoad15;
                        double.TryParse(load[0].Trim(), out tempLoad1);
                        double.TryParse(load[1].Trim(), out tempLoad5);
                        double.TryParse(load[2].Trim(), out tempLoad15);
                        status.Load1 = tempLoad1;
                        status.Load5 = tempLoad5;
                        status.Load15 = tempLoad15;
                    }
                    else status.Load1 = status.Load5 = status.Load15 = 0;
                }
                else status.Load1 = status.Load5 = status.Load15 = 0;
            }
            else
            {
                status.CpuUsed = 0;
                status.MemUsed = 0;
                status.SwapUsed = 0;
                status.DiskUsed = 0;
                status.NetInSpeed = 0;
                status.NetOutSpeed = 0;
                status.Load1 = status.Load5 = status.Load15 = 0;
                // Keep existing NetInTransfer/OutTransfer and Uptime
            }
        }

        private static void GetHostLinux()
        {
            var tempeth = Bash("cat /proc/net/dev | awk '{if($2>0 && NR > 2) print substr($1, 0, index($1, \":\"))}'");
            var eths = tempeth.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            var listRemove = new List<string>();
            foreach (var item in ExcludeNetInterfaces)
                for (var i = 0; i < eths.Count; i++)
                    if (eths[i].StartsWith(item))
                        if (!listRemove.Contains(eths[i]))
                            listRemove.Add(eths[i]);

            foreach (var v in listRemove) eths.Remove(v);
            NetName = eths.FirstOrDefault()?.Trim(':') ?? "";

            var cmd =
                "awk -F: '/model name/ {name=$2} END {print name}' /proc/cpuinfo | sed 's/^[ \t]*//;s/[ \t]*$//';" +
                "awk -F: '/processor/ {core++} END {print core}' /proc/cpuinfo | tail -n 1;" +
                "awk -F: '/cpu MHz/ {freq=$2} END {print freq}' /proc/cpuinfo | sed 's/^[ \t]*//;s/[ \t]*$//';" +
                "free -m | awk '/Mem/ {print $2}';" +
                "free -m | awk '/Swap/ {print $3}';" +
                "awk '{print $1}' /proc/uptime;" +
                "uname -m;" +
                "LANG=C; df -P -t simfs -t ext2 -t ext3 -t ext4 -t btrfs -t xfs -t vfat -t ntfs --total 2>/dev/null | grep '^total' | awk '{ print $2 }';" +
                "([ -f /etc/redhat-release ] && awk '{print ($1,$3~/^[0-9]/?$3:$4)}' /etc/redhat-release)||([ -f /etc/os-release ] && awk -F'[= \"]' '/PRETTY_NAME/{print $3,$4,$5}' /etc/os-release)||([ -f /etc/lsb-release ] && awk -F'[=\"]+' '/DESCRIPTION/{print $2}' /etc/lsb-release);";

            var result = Bash(cmd);

            if (!string.IsNullOrEmpty(result))
            {
                var temp = result.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                host.Cpu = (temp.ElementAtOrDefault(0)?.Trim() + " X" + temp.ElementAtOrDefault(1)?.Trim()).Trim();
                if (string.IsNullOrEmpty(temp.ElementAtOrDefault(0)?.Trim())) host.Cpu = ("X" + temp.ElementAtOrDefault(1)?.Trim()).Trim();

                if (!double.TryParse(temp.ElementAtOrDefault(3), out double memTotal)) host.MemTotal = 0; else host.MemTotal = memTotal;
                if (!double.TryParse(temp.ElementAtOrDefault(4), out double swapTotal)) host.SwapTotal = 0; else host.SwapTotal = swapTotal;
                if (!double.TryParse(temp.ElementAtOrDefault(5), out double bootTime)) host.BootTime = 0; else host.BootTime = bootTime;
                host.Arch = temp.ElementAtOrDefault(6)?.Trim() ?? "";
                if (!double.TryParse(temp.ElementAtOrDefault(7), out double diskTotalBlocks)) host.DiskTotal = 0; else host.DiskTotal = diskTotalBlocks / 1024.0;
                host.Platform = temp.ElementAtOrDefault(8)?.Trim() ?? "";

                try
                {
                    using (var client = new HttpClient())
                    {
                     
                        host.Ip = client.GetStringAsync("https://api-ipv4.ip.sb/ip").GetAwaiter().GetResult().TrimEnd('\n', '\r').Trim();
                    }
                }
                catch
                {
                    host.Ip = ""; // Set to empty string on failure
                }
            }
            else
            {
                host.Cpu = "";
                host.MemTotal = 0;
                host.SwapTotal = 0;
                host.BootTime = 0;
                host.Arch = "";
                host.DiskTotal = 0;
                host.Platform = "";
                host.Ip = "";
            }
        }

        internal class SignalRClient : IDisposable
        {
            private HubConnection _hubConnection;
            private string _serverUrl;
            private CancellationToken _appCancellationToken;

            internal bool IsConnected => _hubConnection?.State == HubConnectionState.Connected;

            internal static SignalRClient Instance { get; } = new SignalRClient();

            private SignalRClient() { }

            public void InitializeConnection(string serverUrl, CancellationToken cancellationToken)
            {
                _serverUrl = serverUrl;
                _appCancellationToken = cancellationToken;
                DisposeConnection();

                _hubConnection = new HubConnectionBuilder()
                    .WithUrl(_serverUrl)
                    .WithAutomaticReconnect()
                    .Build();

                _hubConnection.Reconnecting += OnReconnecting;
                _hubConnection.Reconnected += OnReconnected;
                _hubConnection.Closed += OnClosed;

                _ = StartConnectionAsync(_appCancellationToken);
            }

            private async Task StartConnectionAsync(CancellationToken cancellationToken)
            {
                while (!cancellationToken.IsCancellationRequested && _hubConnection.State != HubConnectionState.Connected)
                {
                    try
                    {
                        // Console.WriteLine("SignalR: Attempting to start connection..."); // Remove message
                        await _hubConnection.StartAsync(cancellationToken);
                        Console.WriteLine($"SignalR: Connected."); // Minimal success log
                        await RegisterClientAsync();
                        break;
                    }
                    catch (TaskCanceledException)
                    {
                        // Expected on shutdown
                        break;
                    }
                    catch
                    {
                        // Console.WriteLine($"SignalR: Connection attempt failed."); // Remove failure log, OnReconnecting/Closed will cover state
                        try { await Task.Delay(5000, cancellationToken); } catch (TaskCanceledException) { break; }
                    }
                }
            }

            private Task OnReconnecting(Exception arg)
            {
                // Console.WriteLine($"SignalR: Reconnecting..."); // Minimal log
                return Task.CompletedTask;
            }

            private async Task OnReconnected(string connectionId)
            {
                Console.WriteLine($"SignalR: Reconnected."); // Minimal success log
                try { await RegisterClientAsync(); } catch { }
            }

            private Task OnClosed(Exception arg)
            {
                Console.WriteLine($"SignalR: Disconnected."); // Minimal log
                return Task.CompletedTask;
            }

            private async Task RegisterClientAsync()
            {
                if (_hubConnection == null || _hubConnection.State != HubConnectionState.Connected) return;
                try
                {
                    await _hubConnection.InvokeAsync("Register", Program.config.Uuid, Program.host);
                }
                catch { } // Silent failure
            }

            internal async Task Report(Status status)
            {
                if (_hubConnection == null || _hubConnection.State != HubConnectionState.Connected) return;
                try
                {
                    await _hubConnection.InvokeAsync("Report", Program.config.Uuid, status);
                }
                catch { } // Silent failure
            }

            private async void DisposeConnection()
            {
                if (_hubConnection != null)
                {
                    try
                    {
                        _hubConnection.Reconnecting -= OnReconnecting;
                        _hubConnection.Reconnected -= OnReconnected;
                        _hubConnection.Closed -= OnClosed;
                        await _hubConnection.StopAsync();
                    }
                    catch { } // Silent failure
                    finally
                    {
                        try { await _hubConnection.DisposeAsync(); } catch { }
                        _hubConnection = null;
                        // Console.WriteLine("SignalR: Connection disposed."); // Remove log
                    }
                }
            }

            public void Dispose()
            {
                DisposeConnection();
                GC.SuppressFinalize(this);
            }
        }

        #region Model

        internal class Host
        {
            public string Platform { get; set; } = "";
            public string Arch { get; set; } = "";
            public string Cpu { get; set; } = "";
            public double BootTime { get; set; }
            public string Ip { get; set; } = "";
            public double MemTotal { get; set; }
            public double DiskTotal { get; set; }
            public double SwapTotal { get; set; }
            public string Uuid { get; set; } = "";
            public string V { get; set; } = "";
        }

        internal class Status
        {
            public double CpuUsed { get; set; }
            public double MemUsed { get; set; }
            public double SwapUsed { get; set; }
            public double DiskUsed { get; set; }
            public double NetInTransfer { get; set; }
            public double NetOutTransfer { get; set; }
            public double NetInSpeed { get; set; }
            public double NetOutSpeed { get; set; }
            public double Uptime { get; set; }
            public double Load1 { get; set; }
            public double Load5 { get; set; }
            public double Load15 { get; set; }
            public string Uuid { get; set; } = "";
            public DateTime UpdateTime { get; set; } = DateTime.Now.ToUniversalTime();
            public string V { get; set; } = "";
        }

        internal class Config
        {
            public string ServerUrl { get; set; } = "";
            public int ReportTime { get; set; } = 3000;
            public string Uuid { get; set; } = "";
        }

        #endregion
    }
}
