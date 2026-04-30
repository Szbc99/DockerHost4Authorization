using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DockerHost
{
    // 新增：SignalR Hub 用于处理长连接
    public class HardwareHub : Hub
    {
        // 使用静态并发字典来跟踪活动的客户端ID和它们的连接ID
        // 键: clientId, 值: ConnectionId
        private static readonly ConcurrentDictionary<string, string> _activeClients = new ConcurrentDictionary<string, string>();

        // 当有新客户端连接时，此方法被调用
        public override Task OnConnectedAsync()
        {
            return base.OnConnectedAsync();
        }

        // 当客户端断开连接时，自动触发此方法
        public override Task OnDisconnectedAsync(Exception? exception)
        {
            // 4. 服务端与客户端连接中断的时候，要及时清理掉_activeClients中对应的连接ID
            // 查找当前断开连接对应的 clientId
            var disconnectedClient = _activeClients.FirstOrDefault(kvp => kvp.Value == Context.ConnectionId);
            
            if (!string.IsNullOrEmpty(disconnectedClient.Key))
            {
                // 只有当字典中存储的 ConnectionId 与当前断开的 ConnectionId 一致时才移除
                // 这样可以防止：客户端重连后更新了 ConnectionId，而旧连接的断开事件随后触发，导致新连接被误删
                if (_activeClients.TryRemove(new KeyValuePair<string, string>(disconnectedClient.Key, Context.ConnectionId)))
                {
                    Console.WriteLine($"客户端断开连接，已清理 ID: {disconnectedClient.Key}");
                }
            }
            return base.OnDisconnectedAsync(exception);
        }

        // 客户端调用此方法以获取加密的硬件ID
        public async Task GetEncryptedHardwareId(string clientId, int salt)
        {
            var currentConnectionId = Context.ConnectionId;

            // 4. 确保相同的客户端ID只能有一个活动的连接。
            // 修改逻辑：如果 clientId 已存在，严格禁止重复连接（除非是同一个连接ID的重复请求）
            if (_activeClients.TryGetValue(clientId, out var existingConnectionId))
            {
                if (existingConnectionId != currentConnectionId)
                {
                    Console.WriteLine($"拒绝重复连接：客户端 {clientId} 已在线 (ConnectionId: {existingConnectionId})。新请求 ConnectionId: {currentConnectionId}");
                    await Clients.Caller.SendAsync("ClientAlreadyConnected", clientId);
                    return;
                }
            }
            else
            {
                // 尝试添加
                if (!_activeClients.TryAdd(clientId, currentConnectionId))
                {
                    // 并发处理：如果添加失败，说明刚刚被其他线程添加了
                    if (_activeClients.TryGetValue(clientId, out existingConnectionId) && existingConnectionId != currentConnectionId)
                    {
                        Console.WriteLine($"拒绝重复连接：客户端 {clientId} 已在线 (ConnectionId: {existingConnectionId})。");
                        await Clients.Caller.SendAsync("ClientAlreadyConnected", clientId);
                        return;
                    }
                }
            }

            // 1. 获取硬件ID（作为明文）
            var hardwareId = HardwareInfoGenerator.GenerateHardwareInfo();
            
            // 2. 使用客户端提供的参数进行加密
            var encryptedId = EncryptionHelper.EncryptString(hardwareId, clientId, salt);
            
            // 3. 将加密后的结果发送回调用方
            await Clients.Caller.SendAsync("ReceiveEncryptedHardwareId", encryptedId);
        }
    }

    public class Program
    {
        // 持有凭证锁文件句柄，进程存活期间独占，崩溃/退出时由内核自动释放
        private static FileStream _licenseLockHandle;

        // 基于硬件注册码抢占文件锁，防止同一物理机运行多个服务端实例
        private static void AcquireLicenseLock(string hardwareId)
        {
            byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(hardwareId));
            string hashHex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();

            string lockDir = OperatingSystem.IsLinux() ? "/var/tmp" : Path.GetTempPath();
            string lockPath = Path.Combine(lockDir, $"gw-auth-{hashHex}.lock");

            try
            {
                if (!Directory.Exists(lockDir))
                    Directory.CreateDirectory(lockDir);

                _licenseLockHandle = new FileStream(lockPath, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                Console.WriteLine($"[凭证锁] 已获取授权锁: {lockPath}");
            }
            catch (IOException)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[凭证锁] 锁文件被占用: {lockPath}");
                Console.WriteLine($"[凭证锁] 同授权服务端已在运行，本实例退出。");
                Console.ResetColor();
                Environment.Exit(1);
            }
        }

        public static void Main(string[] args)
        {
            // 在绑定端口前抢占凭证锁，防止通过修改端口绕过防多开
            var hardwareId = HardwareInfoGenerator.GenerateHardwareInfo();
            AcquireLicenseLock(hardwareId);

            var builder = WebApplication.CreateBuilder(args);

            // 配置 Kestrel 监听方式
            builder.WebHost.ConfigureKestrel((context, serverOptions) =>
            {
                var communicationSection = context.Configuration.GetSection("Communication");
                
                // 根据操作系统环境自动选择通讯方式：
                // Windows：TCP 绑定指定 IP
                // Linux：TCP 绑定 Docker 网桥地址数组（容器内可访问，外部不可达）
                if (OperatingSystem.IsWindows())
                {
                    int port = communicationSection.GetValue<int>("TcpPort", 5000);
                    string ipString = communicationSection.GetValue<string>("IpAddress");

                    if (string.IsNullOrWhiteSpace(ipString) || ipString == "*" || ipString == "0.0.0.0")
                    {
                        serverOptions.ListenAnyIP(port);
                        Console.WriteLine($"[Windows环境] 正在监听 TCP (所有IP) 端口: {port}");
                    }
                    else if (System.Net.IPAddress.TryParse(ipString, out var ipAddress))
                    {
                        serverOptions.Listen(ipAddress, port);
                        Console.WriteLine($"[Windows环境] 正在监听 TCP ({ipAddress}) 端口: {port}");
                    }
                    else
                    {
                        serverOptions.ListenAnyIP(port);
                        Console.WriteLine($"[Windows环境] 配置的 IP 地址无效 ({ipString})，默认监听所有 IP，端口: {port}");
                    }
                }
                else if (OperatingSystem.IsLinux())
                {
                    int port = communicationSection.GetValue<int>("TcpPort", 5000);
                    string[] bridgeIps = communicationSection.GetSection("DockerBridgeIps").Get<string[]>()
                                         ?? new[] { "172.17.0.1" };

                    bool bound = false;
                    foreach (var ipString in bridgeIps)
                    {
                        if (System.Net.IPAddress.TryParse(ipString, out var ipAddress))
                        {
                            serverOptions.Listen(ipAddress, port);
                            Console.WriteLine($"[Linux环境] 监听 Docker 网桥 {ipString}:{port}");
                            bound = true;
                        }
                    }

                    if (!bound)
                    {
                        serverOptions.Listen(System.Net.IPAddress.Loopback, port);
                        Console.WriteLine($"[Linux环境] 配置的网桥 IP 均无效，回退监听 127.0.0.1:{port}");
                    }
                }
                else
                {
                    // 其他环境默认使用 TCP
                    int port = communicationSection.GetValue<int>("TcpPort", 5000);
                    serverOptions.ListenAnyIP(port);
                    Console.WriteLine($"[其他环境] 正在监听 TCP 端口: {port}");
                }
            });

            // 添加对 Systemd 的支持
            builder.Host.UseSystemd();

            // 配置服务
            builder.Services.AddControllers();
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();
            
            // 配置 SignalR 的心跳和超时设置
            builder.Services.AddSignalR(options =>
            {
                // 缩短客户端超时时间，以便服务端更快地清理掉断开的连接 (默认是 30秒)
                // 这样当客户端因网络断开并尝试重连时，服务端能更快地释放旧的 ClientId
                options.ClientTimeoutInterval = TimeSpan.FromSeconds(10);
                
                // 相应地缩短心跳间隔 (默认是 15秒)，通常设置为超时时间的 1/2 左右
                options.KeepAliveInterval = TimeSpan.FromSeconds(5);
            }); 
            
            var app = builder.Build();

            // --- 使用矩形框醒目地输出注册码 ---
            var line1 = $"软件注册码: {hardwareId}";
            var line2 = $"Software registration code: {hardwareId}";

            // 估算显示宽度 (一个中文字符约等于两个英文字符)
            int GetDisplayWidth(string s) => s.Sum(c => c < 128 ? 1 : 2);
            int contentWidth = Math.Max(GetDisplayWidth(line1), GetDisplayWidth(line2));
            int boxWidth = contentWidth + 4; // 左右各留两个空格

            string topBorder = new string('*', boxWidth);
            string emptyLine = $"* {new string(' ', boxWidth - 4)} *";

            // 格式化每一行内容，使其居中
            string FormatLine(string text)
            {
                int textWidth = GetDisplayWidth(text);
                int padding = contentWidth - textWidth;
                int padLeft = padding / 2;
                int padRight = padding - padLeft;
                return $"* {new string(' ', padLeft)}{text}{new string(' ', padRight)} *";
            }

            Console.WriteLine(); // 添加一个空行以增加间距
            var originalColor = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(topBorder);
            Console.WriteLine(emptyLine);
            Console.WriteLine(FormatLine(line1));
            Console.WriteLine(FormatLine(line2));
            Console.WriteLine(emptyLine);
            Console.WriteLine(topBorder);
            Console.ForegroundColor = originalColor;
            Console.WriteLine(); // 添加一个空行以增加间距

            // 配置 HTTP 请求管道
            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            app.UseAuthorization();

            app.MapControllers();

            // 映射 SignalR Hub 端点
            app.MapHub<HardwareHub>("/hardwarehub");


            app.Run();
        }
    }

    // 新增：用于对称加密的帮助类
    public static class EncryptionHelper
    {
        public static string EncryptString(string plainText, string password, int salt)
        {
            byte[] saltBytes = BitConverter.GetBytes(salt);
            // 使用 Pbkdf2 方法替换已过时的 Rfc2898DeriveBytes 构造函数
            byte[] key = Rfc2898DeriveBytes.Pbkdf2(
                password,
                saltBytes,
                10000,
                HashAlgorithmName.SHA256,
                32); // 256-bit key for AES-256

            byte[] iv = Rfc2898DeriveBytes.Pbkdf2(
                password,
                saltBytes,
                10000,
                HashAlgorithmName.SHA256,
                16); // 128-bit IV for AES

            using (var aes = Aes.Create())
            {
                aes.Key = key;
                aes.IV = iv;

                using (var encryptor = aes.CreateEncryptor(aes.Key, aes.IV))
                using (var msEncrypt = new System.IO.MemoryStream())
                {
                    using (var csEncrypt = new CryptoStream(msEncrypt, encryptor, CryptoStreamMode.Write))
                    using (var swEncrypt = new System.IO.StreamWriter(csEncrypt))
                    {
                        swEncrypt.Write(plainText);
                    }
                    return Convert.ToBase64String(msEncrypt.ToArray());
                }
            }
        }
    }

    // 生成基于硬件信息的注册码（V2：5段格式，与 DataCenter.GetSbmV2 保持一致）
    public static class HardwareInfoGenerator
    {
        private static string _cachedHardwareFingerprint = null;

        public static string GenerateHardwareInfo()
        {
            string macAddr = GetPrimaryMacAddress();

            // MAC 衍生部分（与 General.GetString1 一致）
            string macPart = GetString1(macAddr);

            // 硬件指纹部分（SHA256 前4字节 = 8位大写16进制）
            string hwPart = GetHardwareFingerprint();

            // 拼接后整体做 SHA256，取前10字节 = 20位16进制
            string raw = macPart + "|" + hwPart;
            using var sha256 = SHA256.Create();
            byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(raw));

            // 倒序分组格式化为 XXXX-XXXX-XXXX-XXXX-XXXX
            string hex = BitConverter.ToString(hash, 0, 10).Replace("-", "").ToUpper();
            return $"{hex.Substring(16, 4)}-{hex.Substring(12, 4)}-{hex.Substring(8, 4)}-{hex.Substring(4, 4)}-{hex.Substring(0, 4)}";
        }

        // 与 General.GetString1 完全一致
        private static string GetString1(string MacAddr)
        {
            string strID = "AlarmCenter1";
            strID += MacAddr;
            strID = strID.Replace(':', '8');
            strID = strID.Replace(' ', 'F');
            char[] charArray = strID.ToCharArray();
            Array.Reverse(charArray);
            strID = new string(charArray).Substring(0, 8);
            return strID;
        }

        // 与 DataCenter.GetHardwareFingerprint 一致
        private static string GetHardwareFingerprint()
        {
            if (_cachedHardwareFingerprint != null) return _cachedHardwareFingerprint;

            var parts = new List<string>();
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    try
                    {
#pragma warning disable CA1416
                        using var cryptoKey = Microsoft.Win32.Registry.LocalMachine
                            .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                        string machineGuid = cryptoKey?.GetValue("MachineGuid")?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(machineGuid)) parts.Add(machineGuid);

                        using var cpuKey = Microsoft.Win32.Registry.LocalMachine
                            .OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                        string cpuId = cpuKey?.GetValue("Identifier")?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(cpuId)) parts.Add(cpuId);
#pragma warning restore CA1416
                    }
                    catch { }
                }
                else
                {
                    string machineId = TryReadFileLine("/etc/machine-id");
                    if (string.IsNullOrWhiteSpace(machineId))
                        machineId = TryReadFileLine("/var/lib/dbus/machine-id");
                    if (!string.IsNullOrWhiteSpace(machineId)) parts.Add(machineId);

                    try
                    {
                        if (File.Exists("/proc/cpuinfo"))
                        {
                            string cpuModel = File.ReadLines("/proc/cpuinfo")
                                .FirstOrDefault(l => l.StartsWith("model name", StringComparison.OrdinalIgnoreCase))
                                ?.Split(':').LastOrDefault()?.Trim() ?? "";
                            if (!string.IsNullOrEmpty(cpuModel)) parts.Add(cpuModel);
                        }
                    }
                    catch { }
                }
            }
            catch { }

            string combined = string.Join("|", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
            string fingerprint = string.IsNullOrEmpty(combined) ? "NOHW0000" : ComputeSha256Prefix(combined);
            _cachedHardwareFingerprint = fingerprint;
            return fingerprint;
        }

        // SHA256 前4字节 → 8位大写16进制
        private static string ComputeSha256Prefix(string input)
        {
            using var sha256 = SHA256.Create();
            byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
            return BitConverter.ToString(hashBytes, 0, 4).Replace("-", "").ToUpper();
        }

        // 读取文件第一个非空行
        private static string TryReadFileLine(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                foreach (string line in File.ReadLines(path))
                {
                    string l = line.Trim();
                    if (!string.IsNullOrEmpty(l)) return l;
                }
            }
            catch { }
            return null;
        }

        // 判断 MAC 是否为本地管理地址（bit1=1），虚拟网卡多用此类型
        private static bool IsLocallyAdministeredMac(string macHex)
        {
            if (macHex.Length < 2) return false;
            if (!byte.TryParse(macHex.Substring(0, 2),
                System.Globalization.NumberStyles.HexNumber, null, out byte firstByte))
                return false;
            return (firstByte & 0x02) != 0;
        }

        // 网卡类型优先级（与 DataCenter 一致）
        private static int GetAdapterTypePriority(NetworkInterfaceType type)
        {
            switch (type)
            {
                case NetworkInterfaceType.Ethernet: return 0;
                case NetworkInterfaceType.GigabitEthernet: return 1;
                case NetworkInterfaceType.FastEthernetT: return 2;
                case NetworkInterfaceType.FastEthernetFx: return 3;
                case NetworkInterfaceType.Wireless80211: return 4;
                default: return 9;
            }
        }

        // 选取主网卡 MAC（与 DataCenter.EvalSB4Local 中网卡选取逻辑一致）
        private static string GetPrimaryMacAddress()
        {
            string tempaddr = string.Empty;
            string tempaddr1 = "unknow";

            try
            {
                NetworkInterface[] adapters = NetworkInterface.GetAllNetworkInterfaces();

                var sortedAdapters = adapters
                    .Where(a => a.NetworkInterfaceType != NetworkInterfaceType.Loopback
                             && a.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                    .OrderBy(a => GetAdapterTypePriority(a.NetworkInterfaceType))
                    .ThenBy(a => a.GetPhysicalAddress().ToString())
                    .ToList();

                foreach (NetworkInterface adapter in sortedAdapters)
                {
                    string addr = adapter.GetPhysicalAddress().ToString();
                    if (addr.Length >= 6)
                    {
                        if (adapter.OperationalStatus == OperationalStatus.Up)
                        {
                            bool curIsGlobal = !IsLocallyAdministeredMac(addr);
                            bool existingIsLocal = tempaddr != string.Empty && IsLocallyAdministeredMac(tempaddr);
                            bool shouldReplace = tempaddr == string.Empty
                                || (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && existingIsLocal && curIsGlobal);

                            if (shouldReplace)
                                tempaddr = addr;
                        }
                        tempaddr1 = addr;
                    }
                }

                if (tempaddr == string.Empty)
                    tempaddr = tempaddr1;
            }
            catch { }

            return tempaddr ?? "unknow";
        }
    }
}