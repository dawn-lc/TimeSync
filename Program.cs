using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Runtime.InteropServices;
using System.Text;
using System.Configuration.Install;
using System.Threading;
namespace TimeSync
{
    public class Win32API
    {
        public static class INI
        {
            [DllImport("kernel32")]
            private static extern long WritePrivateProfileString(string section, string key, string val, string filePath);
            [DllImport("kernel32")]
            private static extern int GetPrivateProfileString(string section, string key, string def, StringBuilder retVal, int size, string filePath);
            /// <summary>
            /// 设置配置项
            /// </summary>
            /// <param name="section">配置节点</param>
            /// <param name="key">键名</param>
            /// <param name="value">值</param>
            /// <param name="path">路径</param>
            public static void Set(string path, string section, string key, string value)
            {
                WritePrivateProfileString(section, key, value, path);
            }
            /// <summary>
            /// 获取配置项
            /// </summary>
            /// <param name="section">配置节点</param>
            /// <param name="key">键名</param>
            /// <param name="path">路径</param>
            /// <param name="def">默认值</param>
            /// <returns>配置值</returns>
            public static string Get(string path, string section, string key, string def = "")
            {
                StringBuilder temp = new StringBuilder(65535);
                GetPrivateProfileString(section, key, def, temp, 65535, path);
                return temp.ToString();
            }
        }
        [StructLayoutAttribute(LayoutKind.Sequential)]
        private struct SYSTEMTIME
        {
            public short year;
            public short month;
            public short dayOfWeek;
            public short day;
            public short hour;
            public short minute;
            public short second;
            public short milliseconds;
        }

        [DllImport("kernel32")]
        static extern bool SetLocalTime(ref SYSTEMTIME time);
        public static bool SetTime(DateTime trts)
        {
            SYSTEMTIME st = new SYSTEMTIME
            {
                year = (short)trts.Year,
                month = (short)trts.Month,
                dayOfWeek = (short)trts.DayOfWeek,
                day = (short)trts.Day,
                hour = (short)trts.Hour,
                minute = (short)trts.Minute,
                second = (short)trts.Second,
                milliseconds = (short)trts.Millisecond
            };
            return SetLocalTime(ref st);
        }
    }
    public class Config
    {
        private readonly string path;

        public Config(string filePath)
        {
            path = Path.GetFullPath(filePath);
            if (!File.Exists(path))
            {
                Console.WriteLine($"配置文件 {path} 不存在。");
                File.Create(path);
            }
        }
        public T Get<T>(string section, string key, string def = "null")
        {
            return (T)Convert.ChangeType(Win32API.INI.Get(path, section, key, def), typeof(T));
        }
        public void Set<T>(string section, string key, T value)
        {
            Win32API.INI.Set(path, section, key, value.ToString());
        }
    }

    partial class TimeSync : ServiceBase
    {
        public Config Config { get; set; }
        public string[] NTPServers { get; set; }
        private Timer Timer { get; set; }

        public TimeSync(string serviceName)
        {
            ServiceName = serviceName;
            Config = new Config(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "TimeSync.ini"));
            NTPServers = Config.Get<string>("TimeSync", "NTPServerList", "ntp1.nim.ac.cn|ntp2.nim.ac.cn|ntp.ntsc.ac.cn|cn.pool.ntp.org|ntp.aliyun.com|ntp1.aliyun.com|ntp2.aliyun.com|ntp.tencent.com|ntp1.tencent.com|ntp2.tencent.com|pool.ntp.org|time.windows.com").Split('|');
        }

        protected override void OnStart(string[] args)
        {
            TimerCallback timerCallback = new TimerCallback(_ => Sync(args));
            Timer = new Timer(timerCallback, null, TimeSpan.Zero, TimeSpan.FromMinutes(15));
        }

        protected override void OnStop()
        {
            Timer?.Dispose();
            Config.Set("TimeSync", "NTPServerList", string.Join("|", NTPServers));
        }

        public void Sync(string[] args)
        {
            string[] ntpServers = args.Length < 1 ? NTPServers : args;

            DateTime netTime = DateTime.MinValue;
            foreach (var ntpServer in ntpServers)
            {
                try
                {
                    using (NTPClient client = new NTPClient(ntpServer))
                    {
                        Console.WriteLine($"正在从NTP服务器[{ntpServer}]获取当前时间...");
                        netTime = client.Query();
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"无法从NTP服务器[{ntpServer}]获取当前时间！");
                    Console.WriteLine(ex.ToString());
                    if (ntpServer == ntpServers[ntpServers.Length - 1])
                    {
                        Console.WriteLine("无法从服务器列表中的服务器获取当前时间！");
                        return;
                    }
                    continue;
                }
            }

            if (netTime != DateTime.MinValue)
            {
                Console.WriteLine($"本机时间：{DateTime.Now}");
                Console.WriteLine($"服务器时间：{netTime}");

                if (Math.Abs((DateTime.Now - netTime).TotalMilliseconds) <= 500)
                {
                    Console.WriteLine("无需校时");
                }
                else if (Win32API.SetTime(netTime))
                {
                    Console.WriteLine("校时成功");
                }
                else
                {
                    Console.WriteLine("校时失败");
                }
            }
            return;
        }
    }

    internal class Program
    {
        static string ServiceName { get; set; } = "TimeSync";
        static string Description { get; set; } = "根据配置进行时间同步";
        private static bool IsServiceExisted(string serviceName)
        {
            ServiceController[] services = ServiceController.GetServices();
            foreach (ServiceController sc in services)
            {
                if (sc.ServiceName.ToLower() == serviceName.ToLower())
                {
                    return true;
                }
            }
            return false;
        }
        public static ServiceControllerStatus GetServiceStatus(string serviceName)
        {
            ServiceController service = new ServiceController(serviceName);
            return service.Status;
        }
        public static void UninstallService(string serviceName)
        {
            TransactedInstaller ti = new TransactedInstaller();
            ti.Installers.Add(new ServiceInstaller
            {
                ServiceName = serviceName
            });
            ti.Uninstall(null);
        }
        public static void InstallService(string serviceFilePath, string serviceName, ServiceStartMode startType, string description = null, string[] servicesDependedOn = null)
        {
            TransactedInstaller ti = new TransactedInstaller
            {
                Context = new InstallContext()
            };
            ti.Context.Parameters["assemblypath"] = serviceFilePath;
            ti.Installers.Add(new ServiceProcessInstaller
            {
                Account = ServiceAccount.LocalSystem
            });
            ti.Installers.Add(new ServiceInstaller
            {
                DisplayName = serviceName,
                ServiceName = serviceName,
                StartType = startType,
                Description = description,
                ServicesDependedOn = servicesDependedOn
            });
            ti.Install(new Hashtable());
        }
        private static void ServiceStart(string serviceName)
        {
            using (ServiceController control = new ServiceController(serviceName))
            {
                if (control.Status == ServiceControllerStatus.Stopped)
                {
                    control.Start();
                }
            }
        }
        private static void ServiceStop(string serviceName)
        {
            using (ServiceController control = new ServiceController(serviceName))
            {
                if (control.Status == ServiceControllerStatus.Running)
                {
                    control.Stop();
                }
            }
        }

        static void Main(string[] args)
        {
            List<string> StartupParameters = args.ToList();

            if (StartupParameters.Any(i=> i.ToLower() == "service"))
            {
                ServiceBase.Run(new ServiceBase[] { new TimeSync(ServiceName) });
                return;
            }

            if (StartupParameters.Any(i => i.ToLower() == "install"))
            {
                if (IsServiceExisted(ServiceName)) UninstallService(ServiceName);
                InstallService($"\"{Process.GetCurrentProcess().MainModule.FileName}\" service", ServiceName, ServiceStartMode.Automatic, Description);
                if (IsServiceExisted(ServiceName)) ServiceStart(ServiceName);
                return;
            }

            if (StartupParameters.Any(i => i.ToLower() == "uninstall"))
            {
                if (IsServiceExisted(ServiceName))
                {
                    ServiceStop(ServiceName);
                    UninstallService(ServiceName);
                }
                return;
            }

            new TimeSync(ServiceName).Sync(args);
            return;
        }
    }
}
