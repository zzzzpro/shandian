using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json;

namespace Client;

internal class Program
{
    internal static Host host = new();
    internal static Status status = new();
    internal static Config config = new();

    private static string Platform = "";
    private static readonly string Version = "";

    private static readonly string[] ExcludeNetInterfaces =
        { "lo", "tun", "docker", "veth", "br-", "vmbr", "vnet", "kube" };

    private static string NetName = "";

    private static Process bashProcess;
    private static StreamReader bashOutput;
    private static StreamWriter bashInput;
    private static Thread bashErrorReaderThread;

    private static double lastCpuTotalTime;
    private static double lastCpuIdleTime;

    private static readonly CancellationTokenSource _appCts = new();


    private static async Task Main(string[] args)
    {
        Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] Client starting.");

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
            configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "shandian_status", "config");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(configPath));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] Error creating config directory: {ex.Message}");
            }
        }
        else
        {
            Console.Error.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] Unsupported platform: {Platform}");
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
                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] Config saved.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] Error writing config file: {ex.Message}");
                return;
            }

            return;
        }

        if (!File.Exists(configPath))
        {
            Console.Error.WriteLine(
                $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] Config not found. Run with server URL to create.");
            return;
        }

        try
        {
            config = JsonConvert.DeserializeObject<Config>(File.ReadAllText(configPath));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] Error loading config file: {ex.Message}");
            return;
        }

        if (string.IsNullOrWhiteSpace(config.ServerUrl) || string.IsNullOrWhiteSpace(config.Uuid))
        {
            Console.Error.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] Server URL or UUID is not configured.");
            return;
        }

        if (!config.ServerUrl.EndsWith("/ws/client", StringComparison.OrdinalIgnoreCase))
            config.ServerUrl = $"{config.ServerUrl.TrimEnd('/')}/ws/client";

        if (Platform == "linux")
        {
            InitializeBashProcess();
            if (_appCts.IsCancellationRequested) return;
        }

        GetHost();

        host.V = Version;
        host.Uuid = config.Uuid;

        SignalRClient.Instance.InitializeConnection(config.ServerUrl, _appCts.Token);

        var statusTask = Task.Factory.StartNew(GetStatus, TaskCreationOptions.LongRunning);
        var reportTask = Task.Factory.StartNew(Report, TaskCreationOptions.LongRunning);

        Console.CancelKeyPress += (sender, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] Shutdown requested.");
            _appCts.Cancel();
        };

        try
        {
            await Task.Delay(-1, _appCts.Token);
        }
        catch (TaskCanceledException)
        {
        }
        finally
        {
            if (Platform == "linux" && bashProcess != null)
            {
                if (!bashProcess.HasExited)
                {
                    try
                    {
                        bashInput.WriteLine("exit");
                        bashInput.Flush();
                    }
                    catch
                    {
                    }

                    bashProcess.WaitForExit(1000);
                    if (!bashProcess.HasExited)
                        try
                        {
                            bashProcess.Kill();
                        }
                        catch
                        {
                        }
                }

                if (bashProcess != null)
                    try
                    {
                        bashProcess.Dispose();
                    }
                    catch
                    {
                    }
            }

            SignalRClient.Instance.Dispose();
        }
    }

    private static void InitializeBashProcess()
    {
        if (bashProcess != null && !bashProcess.HasExited) return;

        try
        {
            if (bashProcess != null)
                try
                {
                    bashProcess.Dispose();
                }
                catch
                {
                }

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
            var started = bashProcess.Start();
            if (started)
            {
                bashOutput = bashProcess.StandardOutput;
                bashInput = bashProcess.StandardInput;
                var bashError = bashProcess.StandardError;

                bashErrorReaderThread = new Thread(() =>
                {
                    try
                    {
                        while (!_appCts.IsCancellationRequested && bashError.ReadLine() is { } errorLine)
                        {
                            if (bashProcess.HasExited)
                            {
                                Console.Error.WriteLine(
                                    $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] Bash STDERR (Process Exited): {errorLine}");
                                break;
                            }

                            Console.Error.WriteLine(
                                $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] Bash STDERR: {errorLine}");
                        }
                    }
                    catch
                    {
                    }
                })
                {
                    IsBackground = true
                };
                bashErrorReaderThread.Start();

                Thread.Sleep(100);
                bashProcess.StandardOutput.DiscardBufferedData();
            }
            else
            {
                Console.Error.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] Bash process failed to start.");
                _appCts.Cancel();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] Failed to initialize bash process: {ex.Message}");
            _appCts.Cancel();
        }
    }

    public static string Bash(string cmd)
    {
        if (Platform != "linux") return "";
        if (bashProcess == null || bashInput == null || bashOutput == null)
        {
            Console.Error.WriteLine(
                $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] Bash command skipped: Bash process streams are null. Cmd: {cmd}");
            return "";
        }

        if (bashProcess.HasExited)
        {
            Console.Error.WriteLine(
                $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] Bash command skipped: Bash process has exited. Attempting re-initialization. Cmd: {cmd}");
            InitializeBashProcess();
            if (bashProcess == null || bashProcess.HasExited) return "";
        }

        if (_appCts.IsCancellationRequested) return "";

        try
        {
            bashOutput.DiscardBufferedData();
            Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] Executing bash command: {cmd}");

            bashInput.WriteLine(cmd);
            var endMarker = $"EndOfCommand_{Guid.NewGuid():N}";
            bashInput.WriteLine($"echo {endMarker}");
            bashInput.Flush();

            var result = "";
            string line;
            while (!_appCts.IsCancellationRequested && (line = bashOutput.ReadLine()) != null)
            {
                if (line.Trim() == endMarker)
                    break;
                result += line + "\n";
            }

            if (_appCts.IsCancellationRequested)
            {
                Console.WriteLine(
                    $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] Bash read cancelled during command: {cmd}");
                return "";
            }

            return result.TrimEnd('\n');
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine(
                $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] IOException during bash command '{cmd}': {ex.Message}. Attempting re-initialization.");
            InitializeBashProcess();
            return "";
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] Exception while running bash command '{cmd}': {ex.Message}");
            return "";
        }
    }

    private static void GetHost()
    {
        switch (Platform)
        {
            case "linux":
                GetHostLinux();
                break;
            case "windows":
                GetHostWindows();
                break;
            case "osx":
                Console.Error.WriteLine(
                    $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] GetHost not implemented for platform: {Platform}");
                break;
            default:
                Console.Error.WriteLine(
                    $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] GetHost not implemented for platform: {Platform}");
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
            try
            {
                if (!string.IsNullOrEmpty(status.Uuid) && SignalRClient.Instance.IsConnected)
                    await SignalRClient.Instance.Report(status);
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch
            {
            }
            finally
            {
                try
                {
                    await Task.Delay(config.ReportTime, _appCts.Token);
                }
                catch (TaskCanceledException)
                {
                    ;
                }
            }
    }

    private static async void GetStatus()
    {
        try
        {
            while (!_appCts.IsCancellationRequested)
            {
                var currentDateTime = DateTime.Now.ToUniversalTime(); // Capture time at the start of the poll attempt

                try
                {
                    switch (Platform)
                    {
                        case "linux":
                            GetStatusLinux(currentDateTime); // Pass current time to Linux getter
                            break;
                        case "windows":
                            GetStatusWindows();
                            break;
                        case "osx":
                            await Console.Error.WriteLineAsync(
                                $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] GetStatus not implemented for platform: {Platform}");
                            break;
                        default:
                            await Console.Error.WriteLineAsync(
                                $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] GetStatus not implemented for platform: {Platform}");
                            break;
                    }

                    status.Uuid = config.Uuid;
                    // status.UpdateTime = DateTime.Now.ToUniversalTime(); // Update moved to after poll, before delay
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] Unhandled exception in GetStatus loop: {ex.Message}");
                }
                finally // Ensure status.UpdateTime is updated and delay happens even if GetStatusLinux/Windows throws
                {
                    status.UpdateTime = DateTime.Now.ToUniversalTime(); // Update time *after* data is collected (or failed)
                    try
                    {
                        await Task.Delay(config.ReportTime, _appCts.Token);
                    }
                    catch (TaskCanceledException)
                    {
                        ;
                    }
                }
            }
        }
        catch (Exception e)
        {
            throw; // TODO handle exception
        }
    }

    // Modified to accept currentDateTime
    private static void GetStatusLinux(DateTime currentDateTime)
    {
        // Use the time difference based on when this specific poll started
        var diff = currentDateTime - status.UpdateTime;
        var diffSeconds = diff.TotalSeconds;
        if (diffSeconds <= 0) diffSeconds = 1; // Prevent division by zero

        // Combine all status commands into one bash call
        var cmd =
            "grep \"cpu \" /proc/stat | awk '{total=0; for(i=2;i<=NF;i++){total+=$i}; print total, $5}';" + // 0: CPU Total and Idle
            (string.IsNullOrEmpty(NetName)
                ? "echo '0 0';"
                : $"cat /proc/net/dev | grep \"{NetName}\" | sed 's/:/ /g' | awk '{{print $2,$10}}';") + // 1: Net RX and TX
            "free -m | awk '/Mem/ {print $3}';" + // 2: Mem Used
            "free -m | awk '/Swap/ {print $3}';" + // 3: Swap Used
            "awk '{print $1}' /proc/uptime;" + // 4: Uptime
            "LANG=C; df -P -t simfs -t ext2 -t ext3 -t ext4 -t btrfs -t xfs -t vfat -t ntfs --total 2>/dev/null | grep '^total' | awk '{ print $3 }';" + // 5: Disk Used (1k blocks)
            "LANG=C; w | head -1 | awk -F'load average:' '{print $2}' | sed 's/^[ 	]*//;s/[ 	]*$//';"; // 6: Load Averages

        var result = Bash(cmd);

        if (!string.IsNullOrEmpty(result))
        {
            var temp = result.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

            try
            {
                // 0: CPU Stats
                var cpuStatsLine = temp.ElementAtOrDefault(0)?.Trim();
                if (!string.IsNullOrEmpty(cpuStatsLine))
                {
                    var cpuTimes = cpuStatsLine.Split(' ');
                    if (cpuTimes.Length == 2 && double.TryParse(cpuTimes[0], out var currentCpuTotalTime) &&
                        double.TryParse(cpuTimes[1], out var currentCpuIdleTime))
                    {
                        if (lastCpuTotalTime > 0 && diffSeconds > 0)
                        {
                            var diffTotal = currentCpuTotalTime - lastCpuTotalTime;
                            var diffIdle = currentCpuIdleTime - lastCpuIdleTime;
                            if (diffTotal > 0)
                            {
                                status.CpuUsed = (diffTotal - diffIdle) / diffTotal * 100.0;
                            }
                            else
                            {
                                Console.Error.WriteLine(
                                    $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] GetStatusLinux: diffTotal was 0 for CPU calculation.");
                                status.CpuUsed = 0;
                            }
                        }
                        else
                        {
                            status.CpuUsed = 0;
                        } // First iteration or diffSeconds=0

                        lastCpuTotalTime = currentCpuTotalTime;
                        lastCpuIdleTime = currentCpuIdleTime;
                    }
                    else
                    {
                        Console.Error.WriteLine(
                            $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] GetStatusLinux: Failed to parse CPU stats format: '{cpuStatsLine}'");
                        status.CpuUsed = 0;
                    }
                }
                else
                {
                    Console.Error.WriteLine(
                        $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] GetStatusLinux: CPU stats line missing from bash output.");
                    status.CpuUsed = 0;
                }

                // 1: Network Stats
                var netStatsLine = temp.ElementAtOrDefault(1)?.Trim();
                if (!string.IsNullOrEmpty(netStatsLine))
                {
                    var net = netStatsLine.Split(' ');
                    if (net.Length == 2 && double.TryParse(net[0], out var currentNetInTransfer) &&
                        double.TryParse(net[1], out var currentNetOutTransfer))
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
                        Console.Error.WriteLine(
                            $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] GetStatusLinux: Failed to parse network stats format: '{netStatsLine}' (NetName: {NetName})");
                        status.NetInSpeed = 0;
                        status.NetOutSpeed = 0;
                        status.NetInTransfer = 0;
                        status.NetOutTransfer = 0;
                    }
                }
                else
                {
                    Console.Error.WriteLine(
                        $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] GetStatusLinux: Network stats line missing from bash output (NetName: {NetName}).");
                    status.NetInSpeed = 0;
                    status.NetOutSpeed = 0;
                    status.NetInTransfer = 0;
                    status.NetOutTransfer = 0;
                }

                // 2: Mem Used
                if (!double.TryParse(temp.ElementAtOrDefault(2), out var memUsed))
                {
                    Console.Error.WriteLine(
                        $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] GetStatusLinux: Failed to parse Mem Used from '{temp.ElementAtOrDefault(2)}'");
                    status.MemUsed = 0;
                }
                else
                {
                    status.MemUsed = memUsed;
                }

                // 3: Swap Used
                if (!double.TryParse(temp.ElementAtOrDefault(3), out var swapUsed))
                {
                    Console.Error.WriteLine(
                        $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] GetStatusLinux: Failed to parse Swap Used from '{temp.ElementAtOrDefault(3)}'");
                    status.SwapUsed = 0;
                }
                else
                {
                    status.SwapUsed = swapUsed;
                }

                // 4: Uptime
                if (!double.TryParse(temp.ElementAtOrDefault(4), out var uptime))
                {
                    Console.Error.WriteLine(
                        $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] GetStatusLinux: Failed to parse Uptime from '{temp.ElementAtOrDefault(4)}'");
                    status.Uptime = 0;
                }
                else
                {
                    status.Uptime = uptime;
                }

                // 5: Disk Used (convert 1k blocks to MB)
                if (!double.TryParse(temp.ElementAtOrDefault(5), out var diskUsedBlocks))
                {
                    Console.Error.WriteLine(
                        $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] GetStatusLinux: Failed to parse Disk Used from '{temp.ElementAtOrDefault(5)}'");
                    status.DiskUsed = 0;
                }
                else
                {
                    status.DiskUsed = diskUsedBlocks;
                }

                // 6: Load Averages
                var loadLine = temp.ElementAtOrDefault(6)?.Trim();
                if (!string.IsNullOrEmpty(loadLine))
                {
                    var load = loadLine.Split(',');
                    if (load.Length >= 3)
                    {
                        double tempLoad1, tempLoad5, tempLoad15;
                        if (!double.TryParse(load[0].Trim(), out tempLoad1))
                        {
                         
                            status.Load1 = 0;
                        }
                        else
                        {
                            status.Load1 = tempLoad1;
                        }

                        if (!double.TryParse(load[1].Trim(), out tempLoad5))
                        {
                            status.Load5 = 0;
                        }
                        else
                        {
                            status.Load5 = tempLoad5;
                        }

                        if (!double.TryParse(load[2].Trim(), out tempLoad15))
                        {
                            
                            status.Load15 = 0;
                        }
                        else
                        {
                            status.Load15 = tempLoad15;
                        }
                    }
                    else
                    {
                      
                        status.Load1 = status.Load5 = status.Load15 = 0;
                    }
                }
                else
                {
                
                    status.Load1 = status.Load5 = status.Load15 = 0;
                }
            }
            catch (Exception parseEx)
            {
              
            }
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
        }
    }

    private static void GetHostLinux()
    {
        Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] GetHostLinux: Starting host info collection.");
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

        if (string.IsNullOrEmpty(NetName))
            Console.Error.WriteLine(
                $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] GetHostLinux: Warning: Could not determine main network interface name. Network stats may be inaccurate.");
        else
            Console.WriteLine(
                $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] GetHostLinux: Detected main network interface: {NetName}");

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
        if (string.IsNullOrEmpty(result))
            Console.Error.WriteLine(
                $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] GetHostLinux: Bash command for host info returned empty or failed.");

        if (!string.IsNullOrEmpty(result))
        {
            var temp = result.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

            try
            {
                host.Cpu = (temp.ElementAtOrDefault(0)?.Trim() + " X" + temp.ElementAtOrDefault(1)?.Trim()).Trim();
                if (string.IsNullOrEmpty(temp.ElementAtOrDefault(0)?.Trim()))
                    host.Cpu = ("X" + temp.ElementAtOrDefault(1)?.Trim()).Trim();
                if (string.IsNullOrEmpty(host.Cpu) || host.Cpu.Trim() == "X")
                    Console.Error.WriteLine(
                        $"[{DateTime.UtcNow:yyyy-MM-dd HH::ssZ}] GetHostLinux: Warning: Could not get full CPU info.");


                if (!double.TryParse(temp.ElementAtOrDefault(3), out var memTotal))
                {
                    Console.Error.WriteLine(
                        $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] GetHostLinux: Failed to parse MemTotal from '{temp.ElementAtOrDefault(3)}'");
                    host.MemTotal = 0;
                }
                else
                {
                    host.MemTotal = memTotal;
                }

                if (!double.TryParse(temp.ElementAtOrDefault(4), out var swapTotal))
                {
                    Console.Error.WriteLine(
                        $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] GetHostLinux: Failed to parse SwapTotal from '{temp.ElementAtOrDefault(4)}'");
                    host.SwapTotal = 0;
                }
                else
                {
                    host.SwapTotal = swapTotal;
                }

                if (!double.TryParse(temp.ElementAtOrDefault(5), out var bootTime))
                {
                    Console.Error.WriteLine(
                        $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] GetHostLinux: Failed to parse BootTime from '{temp.ElementAtOrDefault(5)}'");
                    host.BootTime = 0;
                }
                else
                {
                    host.BootTime = bootTime;
                }

                host.Arch = temp.ElementAtOrDefault(6)?.Trim() ?? "";
                if (string.IsNullOrEmpty(host.Arch))
                    Console.Error.WriteLine(
                        $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] GetHostLinux: Warning: Could not get architecture.");

                if (!double.TryParse(temp.ElementAtOrDefault(7), out var diskTotalBlocks))
                {
                    Console.Error.WriteLine(
                        $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] GetHostLinux: Failed to parse DiskTotal from '{temp.ElementAtOrDefault(7)}'");
                    host.DiskTotal = 0;
                }
                else
                {
                    host.DiskTotal = diskTotalBlocks;
                }

                host.Platform = temp.ElementAtOrDefault(8)?.Trim() ?? "";
                if (string.IsNullOrEmpty(host.Platform))
                    Console.Error.WriteLine(
                        $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] GetHostLinux: Warning: Could not get OS platform name.");


                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] GetHostLinux: Fetching public IP...");
                try
                {
                    using (var client = new HttpClient())
                    {
                        host.Ip = client.GetStringAsync("https://api-ipv4.ip.sb/ip").GetAwaiter().GetResult()
                            .TrimEnd('\n', '\r').Trim();
                        Console.WriteLine(
                            $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] GetHostLinux: Public IP fetched: {host.Ip}");
                    }
                }
                catch (Exception ipEx)
                {
                    Console.Error.WriteLine(
                        $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] GetHostLinux: Could not get public IP: {ipEx.Message}");
                    host.Ip = "N/A";
                }
            }
            catch (Exception parseEx)
            {
                Console.Error.WriteLine(
                    $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] GetHostLinux: Unhandled error during parsing bash results: {parseEx.Message}");
                Console.Error.WriteLine(
                    $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] GetHostLinux: Raw bash result that caused error:\n{result}");
            }
        }
        else
        {
            Console.Error.WriteLine(
                $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] GetHostLinux: Failed to get host information from bash commands. Result was empty.");
            host.Cpu = "";
            host.MemTotal = 0;
            host.SwapTotal = 0;
            host.BootTime = 0;
            host.Arch = "";
            host.DiskTotal = 0;
            host.Platform = "";
            host.Ip = "N/A";
        }
    }

    private static void GetStatusWindows()
    {
        Console.Error.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] GetStatusWindows not fully implemented.");
        status.CpuUsed = 0;
        status.MemUsed = 0;
        status.SwapUsed = 0;
        status.DiskUsed = 0;
        status.NetInTransfer = 0;
        status.NetOutTransfer = 0;
        status.NetInSpeed = 0;
        status.NetOutSpeed = 0;
        status.Uptime = 0;
        status.Load1 = status.Load5 = status.Load15 = 0;
    }


    internal class SignalRClient : IDisposable
    {
        private CancellationToken _appCancellationToken;
        private HubConnection _hubConnection;
        private string _serverUrl;

        private SignalRClient()
        {
        }

        internal bool IsConnected => _hubConnection?.State == HubConnectionState.Connected;

        internal static SignalRClient Instance { get; } = new();

        public void Dispose()
        {
            DisposeConnection();
            GC.SuppressFinalize(this);
        }

        public void InitializeConnection(string serverUrl, CancellationToken cancellationToken)
        {
            _serverUrl = serverUrl;
            _appCancellationToken = cancellationToken;
            DisposeConnection();

            Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] SignalR: Building connection to {_serverUrl}");

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
                try
                {
                    Console.WriteLine(
                        $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] SignalR: Attempting to start connection...");
                    await _hubConnection.StartAsync(cancellationToken);
                    Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] SignalR: Connected.");
                    await RegisterClientAsync();
                    break;
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] SignalR: Initial connection attempt failed: {ex.Message}");
                    try
                    {
                        await Task.Delay(5000, cancellationToken);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                }
        }

        private Task OnReconnecting(Exception arg)
        {
            Console.WriteLine(
                $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] SignalR: Reconnecting... Reason: {arg?.Message}");
            return Task.CompletedTask;
        }

        private async Task OnReconnected(string connectionId)
        {
            Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] SignalR: Reconnected.");
            try
            {
                await RegisterClientAsync();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] SignalR: Error re-registering client after reconnect: {ex.Message}");
            }
        }

        private Task OnClosed(Exception arg)
        {
            Console.Error.WriteLine(
                $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] SignalR: Connection permanently closed. Reason: {arg?.Message}");
            return Task.CompletedTask;
        }

        private async Task RegisterClientAsync()
        {
            if (_hubConnection == null || _hubConnection.State != HubConnectionState.Connected) return;
            try
            {
                await _hubConnection.InvokeAsync("Register", config.Uuid, host);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] SignalR: Error invoking 'Register': {ex.Message}");
            }
        }

        internal async Task Report(Status status)
        {
            if (_hubConnection == null || _hubConnection.State != HubConnectionState.Connected) return;
            try
            {
                await _hubConnection.InvokeAsync("Report", config.Uuid, status);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ssZ}] SignalR: Error invoking 'Report': {ex.Message}");
            }
        }

        private async void DisposeConnection()
        {
            if (_hubConnection != null)
                try
                {
                    _hubConnection.Reconnecting -= OnReconnecting;
                    _hubConnection.Reconnected -= OnReconnected;
                    _hubConnection.Closed -= OnClosed;
                    await _hubConnection.StopAsync();
                }
                catch
                {
                }
                finally
                {
                    try
                    {
                        await _hubConnection.DisposeAsync();
                    }
                    catch
                    {
                    }

                    _hubConnection = null;
                }
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
