using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
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

        private static void Main(string[] args)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) Platform = "linux";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) Platform = "windows";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) Platform = "osx";
            var configPath = "";
            if (Platform == "linux")
            {
                configPath = "/usr/local/shandian_status/config";
            }
            if (args.Length != 0)
            {
                config.ServerUrl = args[0];
                config.Uuid = Guid.NewGuid().ToString("N");
                File.WriteAllText(configPath,
                    JsonConvert.SerializeObject(config));
                return;
            }

            if (!File.Exists(configPath)) return;
            config = JsonConvert.DeserializeObject<Config>(
                File.ReadAllText(configPath));
            Console.WriteLine(JsonConvert.SerializeObject(config));
            //Version = Assembly.GetEntryAssembly().GetName().Version.ToString();
            config.ServerUrl = $"{config.ServerUrl}/ws/client";
            InitializeBashProcess();
            GetHost();
            host.V = Version;
            SignalRClient.Instance.InitializeConnection();
            Task.Factory.StartNew(GetStatus);
            //通知线程
            Task.Factory.StartNew(Report);
            new AutoResetEvent(false).WaitOne();
        }

        private static void InitializeBashProcess()
        {
            bashProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    RedirectStandardOutput = true,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            bashProcess.Start();
            bashOutput = bashProcess.StandardOutput;
            bashInput = bashProcess.StandardInput;
        }

        public static string Bash(string cmd)
        {
            try
            {
                bashInput.WriteLine(cmd);
                bashInput.Flush();
                bashInput.WriteLine("echo EndOfCommand");
                bashInput.Flush();

                string result = "";
                string line;
                while ((line = bashOutput.ReadLine()) != null)
                {
                    if (line == "EndOfCommand")
                        break;
                    result += line + "\n";
                }

                return result;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Exception while running bash command: {ex.Message}");
                throw;
            }
        }

        private static void GetHost()
        {
            //host.Platform = Platform;
            switch (Platform)
            {
                case "linux":
                    GetHostLinux();
                    break;
                case "windows":
                    GetHostWindows();
                    break;
            }
        }

        private static void GetHostWindows()
        {
            //获取网卡
        }

        private static void Report()
        {
            while (true)
            {
                try
                {
                    if (status.Uuid == "")
                    {
                        Thread.Sleep(config.ReportTime);
                        continue;
                    }

                    SignalRClient.Instance.Report(status);
                }
                catch (Exception e)
                {
                }

                Thread.Sleep(config.ReportTime);
            }
        }

       private static void GetStatusLinux()
{
    DateTime lastDateTime = DateTime.Now.ToUniversalTime();
    // NetInTransfer and NetOutTransfer are now class members for difference calculation

    // Initialize last transfer values on first run - Keep this to get initial network cumulative bytes
    string initialNetCmd = $"cat /proc/net/dev | grep \"{NetName}\" | sed 's/:/ /g' | awk '{{print $2,$10}}';";
    var initialNetResult = Bash(initialNetCmd);
    if (!string.IsNullOrEmpty(initialNetResult))
    {
         try
         {
             var net = initialNetResult.Trim().Split(' ');
             if (net.Length == 2)
             {
                 if (double.TryParse(net[0], out double initialNetIn)) status.NetInTransfer = initialNetIn;
                 if (double.TryParse(net[1], out double initialNetOut)) status.NetOutTransfer = initialNetOut;
                 // Console.WriteLine($"Initialized network transfers: In={status.NetInTransfer}, Out={status.NetOutTransfer}"); // Optional log
             }
             else
             {
                  Console.Error.WriteLine($"Unexpected initial netstat format: {initialNetResult}");
             }
         }
         catch (Exception ex)
         {
             Console.Error.WriteLine($"Error parsing initial netstat: {ex.Message}");
         }
    }
     else
    {
         Console.Error.WriteLine("Warning: Failed to get initial network stats. Net speed will be 0 initially.");
         status.NetInTransfer = 0; // Ensure they are zero if command fails
         status.NetOutTransfer = 0;
    }

    // lastCpuTotalTime and lastCpuIdleTime are class members, implicitly initialized to 0.
    // We rely on the calculation logic inside the loop to handle the first iteration where they are 0.
    // No need for a separate initial CPU query here.


    // Main status polling loop
    do
    {
        DateTime currentDateTime = DateTime.Now.ToUniversalTime();
        TimeSpan diff = currentDateTime - lastDateTime;
        double diffSeconds = diff.TotalSeconds;

        // Combine all status commands into one bash call
        // CPU stats command is now included directly in the main loop's command string
        var cmd =
             "grep \"cpu \" /proc/stat | awk '{total=0; for(i=2;i<=NF;i++){total+=$i}; print total, $5}';" + // 0: Total CPU time, Idle CPU time
            $"cat /proc/net/dev | grep \"{NetName}\" | sed 's/:/ /g' | awk '{{print $2,$10}}';" + // 1: Net RX bytes, Net TX bytes
            "free -m | awk '/Mem/ {print $3}';" + // 2: Mem Used (MB)
            "free -m | awk '/Swap/ {print $3}';" + // 3: Swap Used (MB)
            "awk '{print $1}' /proc/uptime;" + // 4: Uptime (seconds)
             // Adjusted df command to be more robust and specifically grep 'total' line only
            "LANG=C; df -P -t simfs -t ext2 -t ext3 -t ext4 -t btrfs -t xfs -t vfat -t ntfs --total 2>/dev/null | grep '^total' | awk '{ print $3 }';" + // 5: Disk Used (1k blocks)
            "LANG=C; w | head -1 | awk -F'load average:' '{print $2}' | sed 's/^[ \t]*//;s/[ \t]*$//';"; // 6: Load Averages (1m, 5m, 15m)

        var result = Bash(cmd);
        //Console.WriteLine($"DEBUG: Bash result:\n{result}"); // Debugging result

        if (!string.IsNullOrEmpty(result))
        {
            var temp = result.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries); // Use RemoveEmptyEntries

            try
            {
                 // Parse results based on the order of commands

                 // 0: CPU Stats - Calculation happens here based on current vs last values
                 var cpuStatsLine = temp.ElementAtOrDefault(0)?.Trim();
                 if (!string.IsNullOrEmpty(cpuStatsLine))
                 {
                    var cpuTimes = cpuStatsLine.Split(' ');
                    if (cpuTimes.Length == 2 && double.TryParse(cpuTimes[0], out double currentCpuTotalTime) && double.TryParse(cpuTimes[1], out double currentCpuIdleTime))
                     {
                        // Calculate usage ONLY if we have previous data points and time has passed
                        if (lastCpuTotalTime > 0 && diffSeconds > 0)
                         {
                             double diffTotal = currentCpuTotalTime - lastCpuTotalTime;
                             double diffIdle = currentCpuIdleTime - lastCpuIdleTime;

                             if (diffTotal > 0)
                             {
                                 status.CpuUsed = ((diffTotal - diffIdle) / diffTotal) * 100.0;
                             }
                             else
                             {
                                 status.CpuUsed = 0; // Avoid division by zero if diffTotal is 0
                             }
                         }
                         else
                         {
                              // First iteration or no time passed, CPU usage remains 0 (its default)
                              status.CpuUsed = 0; // Explicitly set to 0 for clarity on first run/no diff
                         }

                         // ALWAYS update last values for the *next* iteration
                         lastCpuTotalTime = currentCpuTotalTime;
                         lastCpuIdleTime = currentCpuIdleTime;
                     }
                     else
                     {
                         Console.Error.WriteLine($"Warning: Could not parse current CPU stats: {cpuStatsLine}");
                         status.CpuUsed = 0; // Set to 0 if parsing fails
                     }
                 }
                 else
                 {
                     Console.Error.WriteLine($"Warning: Could not get CPU stats.");
                     status.CpuUsed = 0; // Set to 0 if command output is empty
                 }


                 // 1: Network Stats - Calculation happens here based on current vs last values
                var netStatsLine = temp.ElementAtOrDefault(1)?.Trim();
                if (!string.IsNullOrEmpty(netStatsLine))
                {
                    var net = netStatsLine.Split(' ');
                    if (net.Length == 2 && double.TryParse(net[0], out double currentNetInTransfer) && double.TryParse(net[1], out double currentNetOutTransfer))
                    {
                        // Calculate speed ONLY if we have previous data points (initialized before loop) and time has passed
                        if ((status.NetInTransfer > 0 || status.NetOutTransfer > 0) && diffSeconds > 0) // Check if initial values were set (simple check)
                        {
                             // Calculate speed in Bytes/second
                            status.NetInSpeed = (currentNetInTransfer - status.NetInTransfer) / diffSeconds;
                            status.NetOutSpeed = (currentNetOutTransfer - status.NetOutTransfer) / diffSeconds;
                        }
                        else
                        {
                             // First iteration or no time passed, speed is 0
                            status.NetInSpeed = 0;
                            status.NetOutSpeed = 0;
                        }
                         // ALWAYS update last transfer values for the *next* iteration
                         status.NetInTransfer = currentNetInTransfer;
                         status.NetOutTransfer = currentNetOutTransfer;
                    }
                    else
                    {
                        Console.Error.WriteLine($"Warning: Could not parse network stats: {netStatsLine}");
                         status.NetInSpeed = 0; status.NetOutSpeed = 0; // Set to 0 if parsing fails
                    }
                }
                else
                {
                     Console.Error.WriteLine($"Warning: Could not get network stats (NetName: {NetName}).");
                     status.NetInSpeed = 0;
                     status.NetOutSpeed = 0;
                }


                 // 2: Mem Used
                 if (double.TryParse(temp.ElementAtOrDefault(2), out double memUsed)) status.MemUsed = memUsed;
                 else { Console.Error.WriteLine($"Warning: Could not parse Mem Used: {temp.ElementAtOrDefault(2)}"); status.MemUsed = 0; }


                 // 3: Swap Used
                 if (double.TryParse(temp.ElementAtOrDefault(3), out double swapUsed)) status.SwapUsed = swapUsed;
                 else { Console.Error.WriteLine($"Warning: Could not parse Swap Used: {temp.ElementAtOrDefault(3)}"); status.SwapUsed = 0; }


                 // 4: Uptime
                 if (double.TryParse(temp.ElementAtOrDefault(4), out double uptime)) status.Uptime = uptime;
                 else { Console.Error.WriteLine($"Warning: Could not parse Uptime: {temp.ElementAtOrDefault(4)}"); status.Uptime = 0; }


                 // 5: Disk Used (convert 1k blocks to MB)
                 if (double.TryParse(temp.ElementAtOrDefault(5), out double diskUsedBlocks)) status.DiskUsed = diskUsedBlocks / 1024.0;
                 else { Console.Error.WriteLine($"Warning: Could not parse Disk Used: {temp.ElementOrDefault(5)}"); status.DiskUsed = 0; }


                 // 6: Load Averages
                var loadLine = temp.ElementAtOrDefault(6)?.Trim();
                 if (!string.IsNullOrEmpty(loadLine))
                 {
                     var load = loadLine.Split(',');
                     if (load.Length >= 3) // Ensure there are at least 3 values
                     {
                         if (!double.TryParse(load[0].Trim(), out status.Load1)) status.Load1 = 0;
                         if (!double.TryParse(load[1].Trim(), out status.Load5)) status.Load5 = 0;
                         if (!double.TryParse(load[2].Trim(), out status.Load15)) status.Load15 = 0;
                     }
                     else
                     {
                         Console.Error.WriteLine($"Warning: Unexpected load average format: {loadLine}");
                          status.Load1 = status.Load5 = status.Load15 = 0; // Set to 0 if format is wrong
                     }
                 }
                 else
                 {
                     Console.Error.WriteLine($"Warning: Could not get load averages.");
                     status.Load1 = status.Load5 = status.Load15 = 0; // Set to 0 if empty
                 }


                 // Update timestamp and UUID in status
                 status.Uuid = config.Uuid;
                 status.UpdateTime = currentDateTime;
                 // status.V = Version; // TODO: Populate version

                 // Console.WriteLine(JsonConvert.SerializeObject(status)); // Debugging status object
            }
            catch (Exception parseEx)
            {
                Console.Error.WriteLine($"Error parsing status results: {parseEx.Message}");
                Console.Error.WriteLine($"Raw status result:\n{result}");
                // Status object might be partially updated or remain stale
            }
        }
        else
        {
            Console.Error.WriteLine("Failed to get status information from bash commands.");
            // Status object will not be updated in this iteration, remains stale
        }

        lastDateTime = currentDateTime; // Update last datetime for speed calculation

        // Sleep until the next reporting interval
        Thread.Sleep(config.ReportTime); // ReportTime is the status update interval
    } while (true);
}

        private static void GetStatus()
        {
            switch (Platform)
            {
                case "linux":
                    GetStatusLinux();
                    break;
                case "windows":
                    GetStatusWindows();
                    break;
            }
        }

        private static void GetStatusWindows()
        {
        }

        private static void GetStatusLinux()
        {
            var lastDateTime = DateTime.Now.ToUniversalTime();
            double NetInTransfer, NetOutTransfer = 0;

            do
            {
                try
                {
                    var
                        cmd = // "cat /proc/stat | grep  \"cpu\b\" | awk -v total=0 '{$1=\"\";for(i=2;i<=NF;i++){total+=$i};used=$2+$3+$4+$7+$8 }END{print total,used}';"
                            "cat /proc/net/dev | grep " + NetName + " | sed 's/:/ /g' | awk '{print $2,$10}';"
                            + "free -m | awk '/Mem/ {print $3}';"
                            + "free -m | awk '/Swap/ {print $3}';"
                            + "awk '{print $1}' /proc/uptime;"
                            + "LANG=C; df -t simfs -t ext2 -t ext3 -t ext4 -t btrfs -t xfs -t vfat -t ntfs -t swap --total 2>/dev/null | grep total | awk '{ print $3 }';"
                            + "LANG=C; w | head -1 | awk -F'load average:' '{print $2}' | sed 's/^[ \t]*//;s/[ \t]*$//';"
                            + "grep \"cpu\" /proc/stat | awk '{usage=($2+$4)*100/($2+$4+$5)} END {print usage}';";
                    var result = Bash(cmd);
                    //Console.WriteLine(result);
                    if (!string.IsNullOrEmpty(result))
                        try
                        {
                            var temp = result.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                            status.DiskUsed = double.Parse(temp[4]);
                            var load = temp[5].Split(',');
                            status.Load1 = double.Parse(load[0].Trim());
                            status.Load5 = double.Parse(load[1].Trim());
                            status.Load15 = double.Parse(load[2].Trim());
                            status.MemUsed = double.Parse(temp[1].Trim());
                            status.SwapUsed = double.Parse(temp[2].Trim());
                            status.CpuUsed = double.Parse(temp[6]);
                            var net = temp[0].Split(' ');
                            NetInTransfer = double.Parse(net[0]);
                            NetOutTransfer = double.Parse(net[1]);
                            status.Uptime = double.Parse(temp[3]);
                            var diff = DateTime.Now.ToUniversalTime() - lastDateTime;
                            if (diff.TotalSeconds > 0)
                            {
                                status.NetOutSpeed = (NetOutTransfer - status.NetOutTransfer) / diff.TotalSeconds;
                                status.NetInSpeed = (NetInTransfer - status.NetInTransfer) / diff.TotalSeconds;
                            }

                            status.NetOutTransfer = NetOutTransfer;
                            status.NetInTransfer = NetInTransfer;
                            lastDateTime = DateTime.Now.ToUniversalTime();
                            status.Uuid = config.Uuid;
                            status.UpdateTime = lastDateTime;
                        }
                        catch (Exception e)
                        {
                        }
                }
                catch (Exception e)
                {
                }

                //status.V = Version;
                // Console.WriteLine(JsonConvert.SerializeObject(status));
                Thread.Sleep(2000);
            } while (true);
        }

        private class SignalRClient
        {
            private HubConnection _hubConnection;

            internal static SignalRClient Instance { get; } = new SignalRClient();

            public void InitializeConnection()
            {
                if (_hubConnection != null)
                {
                    _hubConnection.Closed -= OnDisconnected;
                }
                _hubConnection = new HubConnectionBuilder().WithUrl(config.ServerUrl).WithAutomaticReconnect().Build();
                _hubConnection.Closed += OnDisconnected;
                ConnectWithRetry();
            }

            private async Task OnDisconnected(Exception arg)
            {
                Environment.Exit(1);
            }

            private void ConnectWithRetry()
            {
                var t = _hubConnection.StartAsync();

                t.ContinueWith(task =>
                {
                    if (!task.IsFaulted)
                    {
                        _hubConnection?.InvokeAsync("Register", config.Uuid, host).Wait();
                    }
                }).Wait();
            }

            internal void Report(Status status)
            {
                _hubConnection?.InvokeAsync("Report", config.Uuid, status).Wait();
            }

        }

        #region Model

        internal class Host
        {
            /// <summary>
            ///     操作系统
            /// </summary>
            public string Platform { get; set; } = "";

            public string Arch { get; set; } = "";

            /// <summary>
            ///     cpu型号
            /// </summary>
            public string Cpu { get; set; } = "";

            /// <summary>
            ///     启动时间
            /// </summary>
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
