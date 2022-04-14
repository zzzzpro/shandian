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

        private static void Main(string[] args)
        {
            if (args.Length != 0)
            {
                config.ServerUrl = args[0];
                config.Uuid = Guid.NewGuid().ToString("N");
                File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), "config"),
                    JsonConvert.SerializeObject(config));
                return;
            }

            if (!File.Exists("config")) return;

            config = JsonConvert.DeserializeObject<Config>(
                File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "config")));
            Console.WriteLine(JsonConvert.SerializeObject(config));
            //Version = Assembly.GetEntryAssembly().GetName().Version.ToString();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) Platform = "linux";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) Platform = "windows";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) Platform = "osx";
            GetHost();
            host.V = Version;

            SignalRClient.Instance.Init();
            Task.Factory.StartNew(GetStatus);
            //通知线程
            Task.Factory.StartNew(Report);
            new AutoResetEvent(false).WaitOne();
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


        private static void GetHostLinux()
        {
            //获取网卡
            var tempeth =
                Bash("cat /proc/net/dev | awk '{if($2>0 && NR > 2) print substr($1, 0, index($1, \":\"))}'");

            //Console.WriteLine(tempeth);
            var eths = tempeth.Split(new[] {'\n'}, StringSplitOptions.RemoveEmptyEntries).ToList();
            var listRemove = new List<string>();
            foreach (var item in ExcludeNetInterfaces)
                for (var i = 0; i < eths.Count; i++)
                    if (eths[i].StartsWith(item))
                        if (!listRemove.Contains(eths[i]))
                            listRemove.Add(eths[i]);

            foreach (var v in listRemove) eths.Remove(v);
            NetName = eths.FirstOrDefault().Trim(':'); //netName;

            // Console.WriteLine(config.NetName);
            var cmd =
                "awk -F: '/model name/ {name=$2} END {print name}' /proc/cpuinfo | sed 's/^[ \t]*//;s/[ \t]*$//';" //cpu型号
                + "awk -F: '/processor/ {core++} END {print core}' /proc/cpuinfo;" //cpu核心数
                + "awk -F: '/cpu MHz/ {freq=$2} END {print freq}' /proc/cpuinfo | sed 's/^[ \t]*//;s/[ \t]*$//';" //cpu频率
                + "free -m | awk '/Mem/ {print $2}';" //内存大小
                + "free -m | awk '/Swap/ {print $2}';" //swap大小
                + "awk '{print $1}' /proc/uptime;" //开机时间
                + "uname -m;" //arch
                + "LANG=C; df -t simfs -t ext2 -t ext3 -t ext4 -t btrfs -t xfs -t vfat -t ntfs -t swap --total 2>/dev/null | grep total | awk '{ print $2 }';" //硬盘大小";
                + "([ -f /etc/redhat-release ] && awk '{print ($1,$3~/^[0-9]/?$3:$4)}' /etc/redhat-release)||([ -f /etc/os-release ] && awk -F'[= \"]' '/PRETTY_NAME/{print $3,$4,$5}' /etc/os-release)||([ -f /etc/lsb-release ] && awk -F'[=\"]+' '/DESCRIPTION/{print $2}' /etc/lsb-release);";
            var result = Bash(cmd);
            if (!string.IsNullOrEmpty(result))
            {
                var temp = result.Split(new[] {'\n'});
                host.Cpu = temp[0].Trim() + " X" + temp[1];
                if (string.IsNullOrEmpty(temp[0].Trim())) //可能取不到cpu信息
                    host.Cpu = "X" + temp[1];
                host.MemTotal = double.Parse(temp[3]);
                host.SwapTotal = double.Parse(temp[4]);
                host.BootTime = double.Parse(temp[5]);
                host.Arch = temp[6];
                host.DiskTotal = double.Parse(temp[7]);
                host.Platform = temp[8];
                //不上报ip了。 由服务端获取。
                try
                {
                    host.Ip = new WebClient().DownloadString("https://api.ip.sb/ip").TrimEnd('\n');
                }
                catch (Exception e)
                {
                }
            }
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
                            var temp = result.Split(new[] {'\n'}, StringSplitOptions.RemoveEmptyEntries);
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

        private static string Bash(string cmd)
        {
            var escapedArgs = cmd.Replace("\"", "\\\"");

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = $"-c \"{escapedArgs}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var result = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return result;
        }

        private class SignalRClient
        {
            private HubConnection Conn;

            private bool isconnect;
            internal static SignalRClient Instance { get; } = new SignalRClient();

            internal void Init()
            {
                Conn = new HubConnectionBuilder().WithUrl(config.ServerUrl).WithAutomaticReconnect().Build();
                Connect();
                //连接断开的监控
                Conn.Closed += Conn_Closed;
            }

            internal void Report(Status status)
            {
                Conn?.InvokeAsync("Report", config.Uuid, status).Wait();
            }

            private Task Conn_Closed(Exception arg)
            {
                Connect();
                return Task.CompletedTask;
            }

            private void Connect()
            {
                if (isconnect) return;
                while (true)
                    try
                    {
                        if (Conn.State == HubConnectionState.Connected) break;
                        isconnect = true;
                        Conn.StartAsync().Wait();
                        Conn.InvokeAsync("Register", config.Uuid, host).Wait();
                        isconnect = false;
                        break;
                    }
                    catch (Exception ex)
                    {
                        while (ex.InnerException != null) ex = ex.InnerException;
                        Thread.Sleep(5000);
                    }
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