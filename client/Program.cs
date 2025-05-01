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
        private static readonly string Version = ""; // TODO: Populate version properly

        private static readonly string[] ExcludeNetInterfaces =
            {"lo", "tun", "docker", "veth", "br-", "vmbr", "vnet", "kube"};

        private static string NetName = "";

        private static Process bashProcess;
        private static StreamReader bashOutput;
        private static StreamWriter bashInput;

        // Variables for calculating CPU usage difference over time
        private static double lastCpuTotalTime = 0;
        private static double lastCpuIdleTime = 0;

        // Cancellation token source to signal application shutdown
        private static CancellationTokenSource _appCts = new CancellationTokenSource();


        private static async Task Main(string[] args)
        {
            Console.WriteLine($"Client starting. Platform: {RuntimeInformation.OSDescription}"); // Detailed startup log

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
                try { Directory.CreateDirectory(Path.GetDirectoryName(configPath)); Console.WriteLine($"Config directory ensured: {Path.GetDirectoryName(configPath)}"); } catch (Exception ex) { Console.Error.WriteLine($"Error creating config directory: {ex.Message}"); }
            }
            // Add logic for OSX config path if needed
            else
            {
                Console.Error.WriteLine($"Unsupported platform: {Platform}"); // Critical error log
                return;
            }


            // Configuration setup logic
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
                    Console.WriteLine($"Configuration saved to {configPath}"); // Detailed log
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error writing config file: {ex.Message}"); // Critical error log
                    return;
                }
                Console.WriteLine("Client configured. Please run without arguments next time."); // User feedback
                return;
            }

            // If no args, try to load config
            Console.WriteLine($"Attempting to load config from {configPath}"); // Detailed log
            if (!File.Exists(configPath))
            {
                Console.Error.WriteLine($"Configuration file not found: {configPath}"); // Critical error log
                Console.WriteLine("Please run with server URL argument to generate config: Client <server_url>"); // User feedback
                return;
            }

            try
            {
                config = JsonConvert.DeserializeObject<Config>(File.ReadAllText(configPath));
                Console.WriteLine($"Configuration loaded: {JsonConvert.SerializeObject(config)}"); // Detailed log
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error loading config file: {ex.Message}"); // Critical error log
                return;
            }

            if (string.IsNullOrWhiteSpace(config.ServerUrl))
            {
                Console.Error.WriteLine("Server URL is not configured."); // Critical error log
                return;
            }
            if (string.IsNullOrWhiteSpace(config.Uuid))
            {
                Console.Error.WriteLine("Client UUID is not configured. Please re-run with server URL argument."); // Critical error log
                return;
            }


            // Ensure ServerUrl ends with /ws/client
            if (!config.ServerUrl.EndsWith("/ws/client", StringComparison.OrdinalIgnoreCase))
            {
                config.ServerUrl = $"{config.ServerUrl.TrimEnd('/')}/ws/client";
                Console.WriteLine($"Adjusted Server URL to: {config.ServerUrl}"); // Detailed log
            }


            if (Platform == "linux")
            {
                InitializeBashProcess(); // Logs initialization status/failure
                if (_appCts.IsCancellationRequested) return; // Exit if bash init failed
            }

            GetHost(); // Logs inside the method

            host.V = Version;
            host.Uuid = config.Uuid;


            // Initialize SignalR Client and start connection process
            Console.WriteLine("Initializing SignalR client."); // Detailed log
            SignalRClient.Instance.InitializeConnection(config.ServerUrl, _appCts.Token);

            // Start tasks for getting status and reporting
            Console.WriteLine("Starting status and report tasks."); // Detailed log
            Task statusTask = Task.Factory.StartNew(GetStatus, TaskCreationOptions.LongRunning);
            Task reportTask = Task.Factory.StartNew(Report, TaskCreationOptions.LongRunning);

            Console.CancelKeyPress += (sender, eventArgs) =>
            {
                eventArgs.Cancel = true;
                Console.WriteLine("Shutdown requested (Ctrl+C)."); // User feedback log
                _appCts.Cancel(); // Signal tasks to cancel
            };

            Console.WriteLine("Client running. Press Ctrl+C to shut down."); // User feedback log

            try
            {
                // Wait for the cancellation token to be signaled
                await Task.Delay(-1, _appCts.Token); // Wait indefinitely until token is cancelled
            }
            catch (TaskCanceledException)
            {
                Console.WriteLine("Shutdown initiated by cancellation token."); // Detailed log
            }
            finally
            {
                Console.WriteLine("Starting shutdown cleanup."); // Detailed log

                // Clean up bash process on exit
                if (Platform == "linux" && bashProcess != null) // Check for null before HasExited
                {
                    if (!bashProcess.HasExited)
                    {
                        Console.WriteLine("Attempting graceful bash process exit."); // Detailed log
                        try { bashInput.WriteLine("exit"); bashInput.Flush(); } catch { Console.Error.WriteLine("Error writing exit to bash input."); }
                        bool exited = bashProcess.WaitForExit(2000); // Wait up to 2 seconds
                        if (!exited)
                        {
                            Console.WriteLine("Bash process did not exit gracefully, force killing."); // Detailed log
                            try { bashProcess.Kill(); } catch (Exception ex) { Console.Error.WriteLine($"Error killing bash process: {ex.Message}"); }
                        }
                        else
                        {
                            Console.WriteLine("Bash process exited gracefully."); // Detailed log
                        }
                    }
                    if (bashProcess != null) try { bashProcess.Dispose(); Console.WriteLine("Bash process disposed."); } catch (Exception ex) { Console.Error.WriteLine($"Error disposing bash process: {ex.Message}"); } // Dispose resources
                }

                Console.WriteLine("Disposing SignalR client."); // Detailed log
                SignalRClient.Instance.Dispose();

                // Optional: Wait for status and report tasks to finish
                // Console.WriteLine("Waiting for status and report tasks to finish...");
                // Task.WhenAll(statusTask, reportTask).Wait(5000); // Wait up to 5 seconds
                // Console.WriteLine("Status and report tasks finished or timed out.");


                Console.WriteLine("Client shutdown complete."); // Final log
            }
        }

        private static void InitializeBashProcess()
        {
            if (bashProcess != null && !bashProcess.HasExited) return;

            try
            {
                if (bashProcess != null) try { bashProcess.Dispose(); Console.WriteLine("Disposed previous bash process during re-init."); } catch { }

                bashProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/bin/bash",
                        RedirectStandardOutput = true,
                        RedirectStandardInput = true,
                        RedirectStandardError = true, // Redirect error stream too
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = "/"
                    }
                };
                bool started = bashProcess.Start();
                if (started)
                {
                    Console.WriteLine($"Bash process started successfully (ID: {bashProcess.Id})."); // Detailed log
                    Thread.Sleep(100); // Give bash a moment to start
                    if (bashProcess.StandardOutput != null) bashProcess.StandardOutput.DiscardBufferedData();
                    // Optional: Read StandardError in a separate thread if needed for debugging
                    // new Thread(() => { try { string err = bashProcess.StandardError.ReadToEnd(); if (!string.IsNullOrEmpty(err)) Console.Error.WriteLine($"Bash STDERR on init: {err}"); } catch { } }).Start();
                }
                else
                {
                    Console.Error.WriteLine("Bash process failed to start."); // Critical error log
                    _appCts.Cancel(); // Signal application shutdown
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to initialize bash process: {ex.Message}"); // Critical error log
                _appCts.Cancel();
            }
        }

        public static string Bash(string cmd)
        {
            // Check for process availability and cancellation
            if (Platform != "linux" || bashProcess == null || bashInput == null || bashOutput == null || bashProcess.HasExited || _appCts.IsCancellationRequested)
            {
                // Console.WriteLine($"Bash command skipped: Process not ready or cancelled. Cmd: {cmd}"); // Very verbose debug
                return "";
            }

            try
            {
                // Clear any previous buffered output before sending a command
                bashOutput.DiscardBufferedData();

                bashInput.WriteLine(cmd);
                string endMarker = $"EndOfCommand_{Guid.NewGuid():N}";
                bashInput.WriteLine($"echo {endMarker}");
                bashInput.Flush();

                string result = "";
                string line;
                // Read until the specific end marker is found or cancellation is requested
                // Use ReadLineAsync with timeout/cancellation in a real-world robust app
                while (!_appCts.IsCancellationRequested && (line = bashOutput.ReadLine()) != null)
                {
                    //Console.WriteLine($"DEBUG: Bash read line: '{line}'"); // Debugging individual lines (very verbose)
                    if (line.Trim() == endMarker)
                        break;
                    result += line + "\n";
                }

                if (_appCts.IsCancellationRequested)
                {
                    Console.WriteLine($"Bash read cancelled during command: {cmd}"); // Log cancellation during read
                    return "";
                }

                // Remove the final newline added after the marker check
                return result.TrimEnd('\n');
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"IOException during bash command '{cmd}': {ex.Message}. Attempting re-initialization."); // Error log
                InitializeBashProcess(); // Attempt to re-initialize bash process
                return "";
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Exception while running bash command '{cmd}': {ex.Message}"); // Error log
                return "";
            }
        }

        private static void GetHost()
        {
            Console.WriteLine("Gathering host information."); // Detailed log
            switch (Platform)
            {
                case "linux":
                    GetHostLinux(); // Logs inside if errors occur
                    break;
                case "windows":
                    GetHostWindows(); // Logs inside if errors occur
                    break;
                case "osx":
                    Console.WriteLine($"GetHost not implemented for platform: {Platform}"); // Log unimplemented
                    break;
                default:
                    Console.WriteLine($"GetHost not implemented for platform: {Platform}"); // Log unimplemented
                    break;
            }
            Console.WriteLine($"Host Info Collected: {JsonConvert.SerializeObject(host)}"); // Detailed log
        }

        private static void GetHostWindows()
        {
            Console.WriteLine("GetHostWindows not fully implemented. Using placeholder values."); // Log incomplete implementation
            host.Platform = "Windows";
            host.Cpu = "Unknown Windows CPU";
            host.MemTotal = 0; // Placeholder
            host.DiskTotal = 0; // Placeholder
            host.SwapTotal = 0; // Placeholder
            host.BootTime = 0; // Placeholder
            host.Arch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "Unknown";
            // IP fetch is generic, happens in GetHostLinux currently. Could move to a separate method.
            host.Ip = "N/A (Placeholder)";
            // Need actual Windows API calls (e.g., WMI/System.Management) for real data
        }

        // This task runs continuously to report status
        private static async void Report() // async void for fire-and-forget task start
        {
            Console.WriteLine("Report task started."); // Task start log
            while (!_appCts.IsCancellationRequested)
            {
                try
                {
                    // Wait for status to be ready initially
                    if (string.IsNullOrEmpty(status.Uuid))
                    {
                        Console.WriteLine("Report task: Waiting for status data..."); // Log waiting
                        await Task.Delay(config.ReportTime, _appCts.Token); // Wait before checking again
                        continue; // Check conditions again
                    }

                    // Only attempt to report if the SignalR client thinks it's connected
                    if (SignalRClient.Instance.IsConnected)
                    {
                        // Console.WriteLine($"Report task: Attempting to report status."); // Very verbose log
                        await SignalRClient.Instance.Report(status); // Internal method logs success/failure
                                                                     // Console.WriteLine("Report task: Status report attempt finished."); // Very verbose log
                    }
                    else
                    {
                        // Console.WriteLine("Report task: SignalR client not connected, skipping report."); // Very verbose log
                    }
                }
                catch (TaskCanceledException)
                {
                    Console.WriteLine("Report task cancelled."); // Log task cancellation
                    break; // Exit loop on cancellation
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"Report task: Unhandled exception in loop: {e.Message}"); // Log unexpected error
                                                                                                       // Continue loop, SignalR client handles its reconnects internally
                }

                // Wait for the next reporting interval
                try
                {
                    await Task.Delay(config.ReportTime, _appCts.Token);
                }
                catch (TaskCanceledException)
                {
                    Console.WriteLine("Report task delay cancelled."); // Log delay cancellation
                    break; // Exit loop on cancellation
                }
            }
            Console.WriteLine("Report task exiting."); // Task exit log
        }


        // This task runs continuously to gather status
        private static async void GetStatus() // async void for fire-and-forget task start
        {
            Console.WriteLine("GetStatus task started."); // Task start log

            // Initial status values are implicitly 0/default
            // Initialize last network transfer values on first run - Keep this to get initial network cumulative bytes
            if (Platform == "linux") // Only needed for Linux Bash method
            {
                Console.WriteLine("GetStatus task: Initializing network stats baseline."); // Detailed log
                string initialNetCmd = $"cat /proc/net/dev | grep \"{NetName}\" | sed 's/:/ /g' | awk '{{print $2,$10}}';";
                var initialNetResult = Bash(initialNetCmd);
                if (!string.IsNullOrEmpty(initialNetResult))
                {
                    try
                    {
                        var net = initialNetResult.Trim().Split(' ');
                        if (net.Length == 2)
                        {
                            if (double.TryParse(net[0], out double initialNetIn)) status.NetInTransfer = initialNetIn; else { Console.Error.WriteLine($"GetStatus task: Failed to parse initial NetInTransfer from '{net[0]}'"); status.NetInTransfer = 0; }
                            if (double.TryParse(net[1], out double initialNetOut)) status.NetOutTransfer = initialNetOut; else { Console.Error.WriteLine($"GetStatus task: Failed to parse initial NetOutTransfer from '{net[1]}'"); status.NetOutTransfer = 0; }
                            Console.WriteLine($"GetStatus task: Initialized network transfers baseline: In={status.NetInTransfer}, Out={status.NetOutTransfer}"); // Detailed log
                        }
                        else
                        {
                            Console.Error.WriteLine($"GetStatus task: Unexpected initial netstat format: '{initialNetResult}'"); // Error log
                            status.NetInTransfer = 0; status.NetOutTransfer = 0;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"GetStatus task: Error parsing initial netstat: {ex.Message}"); // Error log
                        status.NetInTransfer = 0; status.NetOutTransfer = 0;
                    }
                }
                else
                {
                    Console.Error.WriteLine("GetStatus task: Warning: Failed to get initial network stats. Net speed will be 0 initially."); // Warning log
                    status.NetInTransfer = 0; status.NetOutTransfer = 0; // Ensure they are zero if command fails
                }

                // Initial CPU times are 0 by default, first calculation will be 0
            }


            while (!_appCts.IsCancellationRequested)
            {
                DateTime currentDateTime = DateTime.Now.ToUniversalTime();
                TimeSpan diff = currentDateTime - status.UpdateTime; // Use status.UpdateTime for diff base
                double diffSeconds = diff.TotalSeconds;

                try
                {
                    switch (Platform)
                    {
                        case "linux":
                            GetStatusLinux(); // Logs inside if errors occur
                            break;
                        case "windows":
                            GetStatusWindows(); // Logs inside if errors occur
                            break;
                        case "osx":
                            Console.WriteLine($"GetStatus not implemented for platform: {Platform}"); // Log unimplemented
                            break;
                        default:
                            Console.WriteLine($"GetStatus not implemented for platform: {Platform}"); // Log unimplemented
                            break;
                    }
                    // After gathering status (synchronously), update the status object

                    status.Uuid = config.Uuid; // Ensure Uuid is always set in status
                                               // status.UpdateTime is updated *after* the loop's delay
                                               // status.V = Version; // TODO: Populate version

                    // Console.WriteLine($"Status updated."); // Verbose log
                    // Console.WriteLine($"DEBUG Status: CPU={status.CpuUsed:F2}%, MemUsed={status.MemUsed}MB, NetInSpeed={status.NetInSpeed/1024:F2}KB/s, Uptime={status.Uptime:F0}s"); // Debug status values

                }
                catch (TaskCanceledException)
                {
                    Console.WriteLine("GetStatus task cancelled."); // Log task cancellation
                    break; // Exit loop on cancellation
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"GetStatus task: Unhandled exception in loop: {e.Message}"); // Log unexpected error
                    // Continue loop, status object might be stale
                }

                // Wait before the next status gathering
                // Status gathering interval is the same as report time in this code
                try
                {
                    status.UpdateTime = DateTime.Now.ToUniversalTime(); // Update timestamp before delay
                    await Task.Delay(config.ReportTime, _appCts.Token);
                }
                catch (TaskCanceledException)
                {
                    Console.WriteLine("GetStatus task delay cancelled."); // Log delay cancellation
                    break; // Exit loop on cancellation
                }
            }
            Console.WriteLine("GetStatus task exiting."); // Task exit log
        }


        private static void GetStatusLinux() // Synchronous method
        {
            // Note: lastDateTime, NetInTransfer, NetOutTransfer, lastCpuTotalTime, lastCpuIdleTime
            // are static members, maintaining state between calls.

            DateTime currentDateTime = DateTime.Now.ToUniversalTime();
            // Use the time difference between the end of the previous poll and the start of the current poll
            // We updated status.UpdateTime *before* the Task.Delay in the GetStatus async method
            TimeSpan diff = currentDateTime - status.UpdateTime;
            double diffSeconds = diff.TotalSeconds;
            if (diffSeconds <= 0) diffSeconds = 1; // Avoid division by zero or negative time

            // Combine all status commands into one bash call
            var cmd =
                 "grep \"cpu \" /proc/stat | awk '{total=0; for(i=2;i<=NF;i++){total+=$i}; print total, $5}';" + // 0: CPU Total and Idle
                (string.IsNullOrEmpty(NetName) ? "echo '0 0';" : $"cat /proc/net/dev | grep \"{NetName}\" | sed 's/:/ /g' | awk '{{print $2,$10}}';") + // 1: Net RX and TX
                "free -m | awk '/Mem/ {print $3}';" + // 2: Mem Used
                "free -m | awk '/Swap/ {print $3}';" + // 3: Swap Used
                "awk '{print $1}' /proc/uptime;" + // 4: Uptime
                "LANG=C; df -P -t simfs -t ext2 -t ext3 -t ext4 -t btrfs -t xfs -t vfat -t ntfs --total 2>/dev/null | grep '^total' | awk '{ print $3 }';" + // 5: Disk Used (1k blocks)
                "LANG=C; w | head -1 | awk -F'load average:' '{print $2}' | sed 's/^[ \t]*//;s/[ \t]*$//';"; // 6: Load Averages

            var result = Bash(cmd);
            //Console.WriteLine($"DEBUG: GetStatusLinux raw bash result:\n{result}"); // Debugging raw output (very verbose)

            if (!string.IsNullOrEmpty(result))
            {
                var temp = result.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                //Console.WriteLine($"DEBUG: GetStatusLinux parsed {temp.Length} lines from bash result."); // Debugging line count

                try
                {
                    // 0: CPU Stats
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
                                else status.CpuUsed = 0; // Avoid division by zero
                            }
                            else status.CpuUsed = 0; // First iteration or no time passed

                            lastCpuTotalTime = currentCpuTotalTime; // Always update for the next iteration
                            lastCpuIdleTime = currentCpuIdleTime;
                        }
                        else { Console.Error.WriteLine($"GetStatusLinux: Failed to parse CPU stats format: '{cpuStatsLine}'"); status.CpuUsed = 0; } // Parsing error log
                    }
                    else { Console.Error.WriteLine($"GetStatusLinux: CPU stats line missing from bash output."); status.CpuUsed = 0; } // Missing line error log


                    // 1: Network Stats
                    var netStatsLine = temp.ElementAtOrDefault(1)?.Trim();
                    if (!string.IsNullOrEmpty(netStatsLine))
                    {
                        var net = netStatsLine.Split(' ');
                        if (net.Length == 2 && double.TryParse(net[0], out double currentNetInTransfer) && double.TryParse(net[1], out double currentNetOutTransfer))
                        {
                            // Calculate speed ONLY if we have previous data points and time has passed
                            if ((status.NetInTransfer > 0 || status.NetOutTransfer > 0) && diffSeconds > 0) // Check if initial values were set (simple check)
                            {
                                status.NetInSpeed = (currentNetInTransfer - status.NetInTransfer) / diffSeconds;
                                status.NetOutSpeed = (currentNetOutTransfer - status.NetOutTransfer) / diffSeconds;
                            }
                            else
                            {
                                Console.WriteLine($"GetStatusLinux: First network stats or diffSeconds=0, setting speed to 0."); // Log when speed is 0
                                status.NetInSpeed = 0;
                                status.NetOutSpeed = 0;
                            }
                            status.NetInTransfer = currentNetInTransfer; // Always update for the next iteration
                            status.NetOutTransfer = currentNetOutTransfer;
                        }
                        else { Console.Error.WriteLine($"GetStatusLinux: Failed to parse network stats format: '{netStatsLine}' (NetName: {NetName})"); status.NetInSpeed = 0; status.NetOutSpeed = 0; status.NetInTransfer = 0; status.NetOutTransfer = 0; } // Parsing error log
                    }
                    else { Console.Error.WriteLine($"GetStatusLinux: Network stats line missing from bash output (NetName: {NetName})."); status.NetInSpeed = 0; status.NetOutSpeed = 0; status.NetInTransfer = 0; status.NetOutTransfer = 0; } // Missing line error log


                    // 2: Mem Used
                    if (!double.TryParse(temp.ElementAtOrDefault(2), out double memUsed)) { Console.Error.WriteLine($"GetStatusLinux: Failed to parse Mem Used from '{temp.ElementAtOrDefault(2)}'"); status.MemUsed = 0; } else status.MemUsed = memUsed; // Parsing error log

                    // 3: Swap Used
                    if (!double.TryParse(temp.ElementAtOrDefault(3), out double swapUsed)) { Console.Error.WriteLine($"GetStatusLinux: Failed to parse Swap Used from '{temp.ElementAtOrDefault(3)}'"); status.SwapUsed = 0; } else status.SwapUsed = swapUsed; // Parsing error log

                    // 4: Uptime
                    if (!double.TryParse(temp.ElementAtOrDefault(4), out double uptime)) { Console.Error.WriteLine($"GetStatusLinux: Failed to parse Uptime from '{temp.ElementAtOrDefault(4)}'"); status.Uptime = 0; } else status.Uptime = uptime; // Parsing error log

                    // 5: Disk Used (convert 1k blocks to MB)
                    if (!double.TryParse(temp.ElementAtOrDefault(5), out double diskUsedBlocks)) { Console.Error.WriteLine($"GetStatusLinux: Failed to parse Disk Used from '{temp.ElementAtOrDefault(5)}'"); status.DiskUsed = 0; } else status.DiskUsed = diskUsedBlocks / 1024.0; // Parsing error log

                    // 6: Load Averages
                    var loadLine = temp.ElementAtOrDefault(6)?.Trim();
                    if (!string.IsNullOrEmpty(loadLine))
                    {
                        var load = loadLine.Split(',');
                        if (load.Length >= 3)
                        {
                            double tempLoad1, tempLoad5, tempLoad15;
                            if (!double.TryParse(load[0].Trim(), out tempLoad1)) { Console.Error.WriteLine($"GetStatusLinux: Failed to parse Load1 from '{load.ElementAtOrDefault(0)?.Trim()}'"); status.Load1 = 0; } else status.Load1 = tempLoad1; // Parsing error log
                            if (!double.TryParse(load[1].Trim(), out tempLoad5)) { Console.Error.WriteLine($"GetStatusLinux: Failed to parse Load5 from '{load.ElementAtOrDefault(1)?.Trim()}'"); status.Load5 = 0; } else status.Load5 = tempLoad5; // Parsing error log
                            if (!double.TryParse(load[2].Trim(), out tempLoad15)) { Console.Error.WriteLine($"GetStatusLinux: Failed to parse Load15 from '{load.ElementAtOrDefault(2)?.Trim()}'"); status.Load15 = 0; } else status.Load15 = tempLoad15; // Parsing error log
                        }
                        else { Console.Error.WriteLine($"GetStatusLinux: Unexpected load average format (less than 3 values): '{loadLine}'"); status.Load1 = status.Load5 = status.Load15 = 0; } // Format error log
                    }
                    else { Console.Error.WriteLine($"GetStatusLinux: Load average line missing from bash output."); status.Load1 = status.Load5 = status.Load15 = 0; } // Missing line error log

                }
                catch (Exception parseEx)
                {
                    Console.Error.WriteLine($"GetStatusLinux: Unhandled error during parsing bash results: {parseEx.Message}"); // General parsing error log
                    Console.Error.WriteLine($"GetStatusLinux: Raw bash result that caused error:\n{result}"); // Log raw output on error
                                                                                                              // Status object might be partially updated or remain stale
                }
            }
            else
            {
                Console.Error.WriteLine("GetStatusLinux: Bash command returned empty or failed result."); // Bash command failure log
                                                                                                          // If bash command itself failed or returned empty result, set all dynamic stats to 0
                status.CpuUsed = 0;
                status.MemUsed = 0;
                status.SwapUsed = 0;
                status.DiskUsed = 0;
                status.NetInSpeed = 0;
                status.NetOutSpeed = 0;
                status.Load1 = status.Load5 = status.Load15 = 0;
                // Keep existing NetInTransfer/OutTransfer and Uptime as they are cumulative and might retain last valid value
            }
        }

        private static void GetHostLinux()
        {
            Console.WriteLine("GetHostLinux: Starting host info collection."); // Detailed log
            //获取网卡 - Retained original logic
            var tempeth = Bash("cat /proc/net/dev | awk '{if($2>0 && NR > 2) print substr($1, 0, index($1, \":\"))}'");
            if (string.IsNullOrEmpty(tempeth)) Console.Error.WriteLine("GetHostLinux: Bash command for network interfaces returned empty."); // Log empty output

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
            {
                Console.Error.WriteLine("GetHostLinux: Warning: Could not determine main network interface name. Network stats may be inaccurate."); // Warning log
            }
            else
            {
                Console.WriteLine($"GetHostLinux: Detected main network interface: {NetName}"); // Detailed log
            }


            // cmd to get static/semi-static host info
            var cmd =
                "awk -F: '/model name/ {name=$2} END {print name}' /proc/cpuinfo | sed 's/^[ \t]*//;s/[ \t]*$//';" + //cpu型号
                "awk -F: '/processor/ {core++} END {print core}' /proc/cpuinfo | tail -n 1;" + //cpu核心数
                "awk -F: '/cpu MHz/ {freq=$2} END {print freq}' /proc/cpuinfo | sed 's/^[ \t]*//;s/[ \t]*$//';" + //cpu频率
                "free -m | awk '/Mem/ {print $2}';" + //内存大小
                "free -m | awk '/Swap/ {print $3}';" + //swap大小
                "awk '{print $1}' /proc/uptime;" + //开机时间 (seconds)
                "uname -m;" + //arch
                              // Adjusted df command to be more robust and specifically grep 'total' line only
                "LANG=C; df -P -t simfs -t ext2 -t ext3 -t ext4 -t btrfs -t xfs -t vfat -t ntfs --total 2>/dev/null | grep '^total' | awk '{ print $2 }';" + //硬盘大小 (1k blocks)
                                                                                                                                                             // Get OS Name and version
                "([ -f /etc/redhat-release ] && awk '{print ($1,$3~/^[0-9]/?$3:$4)}' /etc/redhat-release)||([ -f /etc/os-release ] && awk -F'[= \"]' '/PRETTY_NAME/{print $3,$4,$5}' /etc/os-release)||([ -f /etc/lsb-release ] && awk -F'[=\"]+' '/DESCRIPTION/{print $2}' /etc/lsb-release);";

            var result = Bash(cmd);
            if (string.IsNullOrEmpty(result)) Console.Error.WriteLine("GetHostLinux: Bash command for host info returned empty or failed."); // Log empty output

            if (!string.IsNullOrEmpty(result))
            {
                var temp = result.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

                // Assign results based on the order of commands in 'cmd' with error handling
                try
                {
                    host.Cpu = (temp.ElementAtOrDefault(0)?.Trim() + " X" + temp.ElementAtOrDefault(1)?.Trim()).Trim(); // CPU Model + Cores
                    if (string.IsNullOrEmpty(temp.ElementAtOrDefault(0)?.Trim())) // handle case where model name is empty
                        host.Cpu = ("X" + temp.ElementAtOrDefault(1)?.Trim()).Trim();
                    // Log if CPU info looks incomplete
                    if (string.IsNullOrEmpty(host.Cpu) || host.Cpu.Trim() == "X") Console.Error.WriteLine($"GetHostLinux: Warning: Could not get full CPU info from bash. Raw: '{temp.ElementAtOrDefault(0)}', '{temp.ElementAtOrDefault(1)}'");


                    // Parse numerical values with error handling
                    if (!double.TryParse(temp.ElementAtOrDefault(3), out double memTotal)) { Console.Error.WriteLine($"GetHostLinux: Failed to parse MemTotal from '{temp.ElementAtOrDefault(3)}'"); host.MemTotal = 0; } else host.MemTotal = memTotal;
                    if (!double.TryParse(temp.ElementAtOrDefault(4), out double swapTotal)) { Console.Error.WriteLine($"GetHostLinux: Failed to parse SwapTotal from '{temp.ElementAtOrDefault(4)}'"); host.SwapTotal = 0; } else host.SwapTotal = swapTotal;
                    if (!double.TryParse(temp.ElementAtOrDefault(5), out double bootTime)) { Console.Error.WriteLine($"GetHostLinux: Failed to parse BootTime from '{temp.ElementAtOrDefault(5)}'"); host.BootTime = 0; } else host.BootTime = bootTime; // Uptime in seconds

                    host.Arch = temp.ElementAtOrDefault(6)?.Trim() ?? "";
                    if (string.IsNullOrEmpty(host.Arch)) Console.Error.WriteLine($"GetHostLinux: Warning: Could not get architecture.");

                    // df reports in 1k blocks, convert to MB for consistency with free -m
                    if (!double.TryParse(temp.ElementAtOrDefault(7), out double diskTotalBlocks)) { Console.Error.WriteLine($"GetHostLinux: Failed to parse DiskTotal from '{temp.ElementAtOrDefault(7)}'"); host.DiskTotal = 0; } else host.DiskTotal = diskTotalBlocks / 1024.0; // Convert 1k blocks to MB

                    host.Platform = temp.ElementAtOrDefault(8)?.Trim() ?? "";
                    if (string.IsNullOrEmpty(host.Platform)) Console.Error.WriteLine($"GetHostLinux: Warning: Could not get OS platform name.");


                    // Attempt to get public IP
                    Console.WriteLine("GetHostLinux: Fetching public IP..."); // Detailed log
                    try
                    {
                        using (var client = new HttpClient())
                        {
                       
                            host.Ip = client.GetStringAsync("https://api-ipv4.ip.sb/ip").GetAwaiter().GetResult().TrimEnd('\n', '\r').Trim();
                            Console.WriteLine($"GetHostLinux: Public IP fetched: {host.Ip}"); // Detailed log
                        }
                    }
                    catch (Exception ipEx)
                    {
                        Console.Error.WriteLine($"GetHostLinux: Could not get public IP: {ipEx.Message}"); // Error log
                        host.Ip = "N/A"; // Indicate failure
                    }

                    Console.WriteLine("GetHostLinux: Host information gathering complete."); // Detailed log
                }
                catch (Exception parseEx)
                {
                    Console.Error.WriteLine($"GetHostLinux: Unhandled error during parsing bash results: {parseEx.Message}"); // General parsing error log
                    Console.Error.WriteLine($"GetHostLinux: Raw bash result that caused error:\n{result}"); // Log raw output on error
                    // Some host info fields might remain default empty/zero
                }
            }
            else
            {
                Console.Error.WriteLine("GetHostLinux: Failed to get host information from bash commands."); // Bash command failure log
                                                                                                             // Set host info fields to default empty/zero on total failure
                host.Cpu = ""; host.MemTotal = 0; host.SwapTotal = 0; host.BootTime = 0; host.Arch = ""; host.DiskTotal = 0; host.Platform = ""; host.Ip = "N/A";
            }
        }


        private static void GetStatusWindows()
        {
            Console.WriteLine("GetStatusWindows not fully implemented. Using placeholder values."); // Log incomplete implementation
            // Implementation for Windows status gathering
            // Needs to gather: CPU usage, Mem Used, Swap Used, Disk Used, Net In/Out Speed/Transfer, Uptime, Load Averages (Win equivalent)
            // This would typically use PerformanceCounter or System.Management

            // Placeholder values
            status.CpuUsed = 0;
            status.MemUsed = 0;
            status.SwapUsed = 0;
            status.DiskUsed = 0;
            status.NetInTransfer = 0;
            status.NetOutTransfer = 0;
            status.NetInSpeed = 0;
            status.NetOutSpeed = 0;
            status.Uptime = 0; // Needs actual fetch
            status.Load1 = status.Load5 = status.Load15 = 0; // Needs Windows equivalent fetch
        }


        // SignalR Client Implementation
        internal class SignalRClient : IDisposable
        {
            private HubConnection _hubConnection;
            private string _serverUrl;
            private CancellationToken _appCancellationToken; // Token from the main application

            internal bool IsConnected => _hubConnection?.State == HubConnectionState.Connected;

            // Singleton instance
            internal static SignalRClient Instance { get; } = new SignalRClient();

            // Private constructor to enforce singleton
            private SignalRClient() { }

            /// <summary>
            /// Initializes the SignalR connection builder and starts the connection process.
            /// </summary>
            /// <param name="serverUrl">The URL of the SignalR hub.</param>
            /// <param name="cancellationToken">Cancellation token to stop the connection process.</param>
            public void InitializeConnection(string serverUrl, CancellationToken cancellationToken)
            {
                _serverUrl = serverUrl;
                _appCancellationToken = cancellationToken;

                // Dispose previous connection if it exists
                DisposeConnection();

                Console.WriteLine($"SignalR: Building connection to {_serverUrl}"); // Detailed log

                _hubConnection = new HubConnectionBuilder()
                    .WithUrl(_serverUrl)
                    // Enable automatic reconnection with default settings
                    // Default delays: 0s, 2s, 10s, 30s, then exponential backoff up to ~3 minutes total before permanent closure
                    .WithAutomaticReconnect()
                    // Optional: Customize reconnect delays
                    // .WithAutomaticReconnect(new TimeSpan[] { TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15) })
                    .Build();

                // Subscribe to connection events
                _hubConnection.Reconnecting += OnReconnecting;
                _hubConnection.Reconnected += OnReconnected;
                _hubConnection.Closed += OnClosed; // Handles permanent closure after retries fail

                // Start the connection process asynchronously
                // Use _ to discard the Task, as this is fire-and-forget initialization
                _ = StartConnectionAsync(_appCancellationToken);

                Console.WriteLine("SignalR: Initialization complete. Starting connection process."); // Detailed log
            }

            /// <summary>
            /// Starts or restarts the SignalR connection process.
            /// Loops indefinitely trying to connect until cancellation is requested or connection succeeds.
            /// </summary>
            /// <param name="cancellationToken">Cancellation token to stop connection attempts.</param>
            private async Task StartConnectionAsync(CancellationToken cancellationToken)
            {
                // The loop continues as long as cancellation is not requested and we are not connected
                while (!cancellationToken.IsCancellationRequested && _hubConnection.State != HubConnectionState.Connected)
                {
                    try
                    {
                        Console.WriteLine("SignalR: Attempting to start connection..."); // Detailed log
                        await _hubConnection.StartAsync(cancellationToken);

                        // If StartAsync completes without exception, we are connected
                        Console.WriteLine($"SignalR: Connection started successfully with ID: {_hubConnection.ConnectionId}"); // Success log

                        // Register the client immediately after connecting (initial or reconnect)
                        await RegisterClientAsync(); // Internal method logs registration status

                        // If we reached here, connection is successful, exit the loop
                        break;
                    }
                    catch (TaskCanceledException)
                    {
                        // Cancellation requested while StartAsync was running
                        Console.WriteLine("SignalR: Connection attempt cancelled by token."); // Log cancellation
                        break; // Exit the loop
                    }
                    catch (Exception ex)
                    {
                        // StartAsync failed (e.g., server unavailable, network issue)
                        Console.Error.WriteLine($"SignalR: Initial connection attempt failed: {ex.Message}"); // Error log
                        // The WithAutomaticReconnect policy handles retries *after* the initial StartAsync succeeds and then drops.
                        // This catch block handles the *initial* StartAsync failure loop.
                        // Add a delay before the next attempt in this initial loop
                        Console.WriteLine($"SignalR: Retrying initial connection attempt in 5 seconds..."); // Log retry delay
                        try
                        {
                            await Task.Delay(5000, cancellationToken); // Wait 5 seconds before next initial attempt
                        }
                        catch (TaskCanceledException)
                        {
                            Console.WriteLine("SignalR: Initial connection retry delay cancelled."); // Log cancellation during delay
                            break; // Exit loop if cancelled during delay
                        }
                    }
                }

                if (_hubConnection.State != HubConnectionState.Connected && !cancellationToken.IsCancellationRequested)
                {
                    Console.WriteLine("SignalR: Exited StartConnectionAsync loop without connecting (not cancelled)."); // Log unexpected exit
                }
            }

            /// <summary>
            /// Event handler for when the connection is lost and auto-reconnect begins.
            /// </summary>
            private Task OnReconnecting(Exception arg)
            {
                Console.WriteLine($"SignalR: Connection lost. Reconnecting... Reason: {arg?.Message}"); // Detailed log
                                                                                                        // You might update UI or state to indicate reconnecting
                return Task.CompletedTask; // Return Task.CompletedTask for async event handlers that don't need awaiting
            }

            /// <summary>
            /// Event handler for when auto-reconnect successfully re-establishes the connection.
            /// </summary>
            private async Task OnReconnected(string connectionId)
            {
                Console.WriteLine($"SignalR: Reconnected successfully with new connection ID: {connectionId}"); // Detailed log
                                                                                                                // It's crucial to re-register the client with the server upon reconnection
                try
                {
                    await RegisterClientAsync(); // Internal method logs registration status
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"SignalR: Error re-registering client after reconnect: {ex.Message}"); // Error log
                                                                                                                    // Depending on your server logic, failure to re-register might require stronger action.
                                                                                                                    // For now, we log the error. The connection is technically up.
                }
            }

            /// <summary>
            /// Event handler for when the connection is permanently closed (auto-reconnect failed or connection stopped).
            /// </summary>
            private Task OnClosed(Exception arg)
            {
                Console.Error.WriteLine($"SignalR: Connection permanently closed. Reason: {arg?.Message}"); // Critical error log
                // This indicates that automatic reconnect attempts have been exhausted or the connection was explicitly stopped.
                // At this point, the SignalR HubConnection object is no longer trying to connect.
                // If you want to attempt to reconnect again after a longer pause, you would call StartConnectionAsync() here.
                // For this example, we just log and let the application continue (though it won't report).
                Console.WriteLine("SignalR: Automatic reconnect failed. Client will stop attempting to report status via SignalR."); // Informational log
                return Task.CompletedTask;
            }


            /// <summary>
            /// Invokes the "Register" method on the SignalR hub.
            /// </summary>
            private async Task RegisterClientAsync()
            {
                // Ensure connection is in the correct state before invoking
                if (_hubConnection == null || _hubConnection.State != HubConnectionState.Connected)
                {
                    Console.WriteLine("SignalR: Skipping registration, connection is not in 'Connected' state."); // Detailed log
                    return;
                }

                try
                {
                    Console.WriteLine($"SignalR: Invoking 'Register' with UUID: {Program.config.Uuid}"); // Detailed log
                    // Pass the client's UUID and host information
                    await _hubConnection.InvokeAsync("Register", Program.config.Uuid, Program.host);
                    Console.WriteLine("SignalR: 'Register' invoked successfully."); // Success log
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"SignalR: Error invoking 'Register': {ex.Message}"); // Error log
                    // This could happen if the connection drops right as we try to send
                    // The automatic reconnect will hopefully pick it up.
                }
            }


            /// <summary>
            /// Invokes the "Report" method on the SignalR hub with current status.
            /// This method is called from the Report task.
            /// </summary>
            /// <param name="status">The current status object.</param>
            internal async Task Report(Status status)
            {
                // Check connection state before attempting to send
                if (_hubConnection == null || _hubConnection.State != HubConnectionState.Connected)
                {
                    // Console.WriteLine("SignalR: Skipping Report, connection not connected."); // Very verbose log
                    return;
                }

                try
                {
                    // Invoke the "Report" method on the server hub
                    await _hubConnection.InvokeAsync("Report", Program.config.Uuid, status);
                    // Console.WriteLine("SignalR: 'Report' invoked successfully."); // Very verbose success log
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"SignalR: Error invoking 'Report': {ex.Message}"); // Error log
                    // This exception indicates a failure during the send operation.
                    // The automatic reconnect mechanism should handle the underlying connection loss.
                }
            }

            /// <summary>
            /// Disposes the SignalR connection.
            /// </summary>
            private async void DisposeConnection() // Use async void if called from non-async context like Dispose()
            {
                if (_hubConnection != null)
                {
                    Console.WriteLine("SignalR: Starting connection disposal."); // Detailed log
                    try
                    {
                        // Unsubscribe from events to prevent memory leaks
                        _hubConnection.Reconnecting -= OnReconnecting;
                        _hubConnection.Reconnected -= OnReconnected;
                        _hubConnection.Closed -= OnClosed;

                        // Stop the connection gracefully
                        Console.WriteLine("SignalR: Stopping connection..."); // Detailed log
                                                                              // Add a timeout for stopping (optional)
                        await _hubConnection.StopAsync(); // StopAsync can take time

                        Console.WriteLine("SignalR: Connection stopped."); // Detailed log
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"SignalR: Error stopping connection: {ex.Message}"); // Error log
                    }
                    finally
                    {
                        try
                        {
                            await _hubConnection.DisposeAsync(); // Use DisposeAsync for async disposal
                            _hubConnection = null;
                            Console.WriteLine("SignalR: Connection disposed."); // Detailed log
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"SignalR: Error disposing connection: {ex.Message}"); // Error log
                        }
                    }
                }
            }

            public void Dispose()
            {
                DisposeConnection(); // Call the async dispose method
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
