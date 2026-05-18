using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
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

        // 挑战-响应机制：防止客户端篡改后忽略断开事件继续运行
        private static IHubContext<HardwareHub> _hubContext;
        private static Timer _challengeTimer;
        private static DateTime _lastChallengeTime = DateTime.MinValue;
        // nonce → (clientId, timestamp)
        private static readonly ConcurrentDictionary<string, (string ClientId, DateTime Timestamp)> _pendingChallenges = new();

        public HardwareHub(IHubContext<HardwareHub> hubContext)
        {
            if (_hubContext == null)
            {
                _hubContext = hubContext;
                if (_challengeTimer == null)
                {
                    _challengeTimer = new Timer(OnChallengeTimer, null,
                        TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
                }
            }
        }

        // 定期向所有活跃客户端发送挑战，验证其仍持有硬件注册码
        private static async void OnChallengeTimer(object state)
        {
            try
            {
                var hardwareId = HardwareInfoGenerator.GenerateHardwareInfo();
                var now = DateTime.UtcNow;

                // 检查超时未响应的挑战
                var expiredNonces = new List<string>();
                foreach (var kvp in _pendingChallenges)
                {
                    if ((now - kvp.Value.Timestamp).TotalSeconds > 30)
                    {
                        expiredNonces.Add(kvp.Key);
                        if (_activeClients.TryGetValue(kvp.Value.ClientId, out var connId))
                        {
                            try
                            {
                                await _hubContext.Clients.Client(connId).SendAsync("ForceDisconnect", "挑战响应超时");
                            }
                            catch { }
                            _activeClients.TryRemove(new KeyValuePair<string, string>(kvp.Value.ClientId, connId));
                            Console.WriteLine($"[挑战] 客户端 {kvp.Value.ClientId} 响应超时，已断开");
                        }
                    }
                }
                foreach (var n in expiredNonces)
                    _pendingChallenges.TryRemove(n, out _);

                // 每300秒向所有活跃客户端发送新挑战
                if ((now - _lastChallengeTime).TotalSeconds >= 300)
                {
                    _lastChallengeTime = now;
                    foreach (var kvp in _activeClients)
                    {
                        // 跳过已有待处理挑战的客户端，避免重复
                        if (_pendingChallenges.Values.Any(v => v.ClientId == kvp.Key))
                            continue;
                        var nonce = Guid.NewGuid().ToString("N");
                        _pendingChallenges[nonce] = (kvp.Key, now);
                        try
                        {
                            await _hubContext.Clients.Client(kvp.Value).SendAsync("Challenge", nonce);
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[挑战] 定时器异常: {ex.Message}");
            }
        }

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

            // 清理该客户端的所有待处理挑战
            var clientId = disconnectedClient.Key;
            if (!string.IsNullOrEmpty(clientId))
            {
                foreach (var kvp in _pendingChallenges)
                {
                    if (kvp.Value.ClientId == clientId)
                        _pendingChallenges.TryRemove(kvp.Key, out _);
                }
            }

            return base.OnDisconnectedAsync(exception);
        }

        // 客户端调用此方法以获取加密的硬件ID
        [Obfuscation(Feature = "virtualization", Exclude = false)]
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
                else
                {
                    Console.WriteLine($"客户端已连接 ID: {clientId}");
                }
            }

            // 1. 获取所有候选硬件ID（作为明文）
            var hardwareIds = HardwareInfoGenerator.GenerateAllPhysicalHardwareIds(includeCurrentSelected: true);
            
            // 2. 使用客户端提供的参数进行加密
            var encryptedIds = hardwareIds
                .Select(id => EncryptionHelper.EncryptString(id, clientId, salt))
                .ToArray();
            
            // 3. 将加密后的结果发送回调用方
            await Clients.Caller.SendAsync("ReceiveEncryptedHardwareIds", encryptedIds);
        }

        // 客户端回应挑战：用硬件注册码对 nonce 做 HMAC-SHA256 签名
        [Obfuscation(Feature = "virtualization", Exclude = false)]
        public async Task ChallengeResponse(string clientId, string nonce, string response)
        {
            if (_pendingChallenges.TryRemove(nonce, out var pending))
            {
                if (pending.ClientId != clientId)
                {
                    await Clients.Caller.SendAsync("ForceDisconnect", "挑战验证失败");
                    Context.Abort();
                    return;
                }

                var hardwareIds = HardwareInfoGenerator.GenerateAllPhysicalHardwareIds(includeCurrentSelected: true);
                bool verified = hardwareIds.Any(hardwareId =>
                    string.Equals(ComputeHmac(hardwareId, nonce), response, StringComparison.OrdinalIgnoreCase));

                if (!verified)
                {
                    Console.WriteLine($"[挑战] 客户端 {clientId} 响应验证失败");
                    await Clients.Caller.SendAsync("ForceDisconnect", "挑战验证失败");
                    Context.Abort();
                    _activeClients.TryRemove(new KeyValuePair<string, string>(clientId, Context.ConnectionId));
                }
            }
        }

        [Obfuscation(Feature = "virtualization", Exclude = false)]
        private static string ComputeHmac(string key, string message)
        {
            byte[] hash = HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(key),
                Encoding.UTF8.GetBytes(message));
            return Convert.ToHexString(hash);
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
            var hardwareId = HardwareInfoGenerator.GenerateHardwareInfo(writeStartupComparisonLog: true);
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
                            try
                            {
                                serverOptions.Listen(ipAddress, port);
                                Console.WriteLine($"[Linux环境] 监听 Docker 网桥 {ipString}:{port}");
                                bound = true;
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[Linux环境] 无法绑定 {ipString}:{port} — {ex.Message}");
                            }
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

            // 添加对 Systemd / Windows Service 的支持（各自在对应平台上生效，非目标平台为 no-op）
            builder.Host.UseSystemd();
            builder.Host.UseWindowsService();

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
        [Obfuscation(Feature = "virtualization", Exclude = false)]
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
        private static readonly object _generationLogLock = new object();
        private static HardwareFingerprintInfo? _cachedHardwareFingerprintInfo = null;

        [Obfuscation(Feature = "virtualization", Exclude = false)]
        public static string GenerateHardwareInfo(bool writeStartupComparisonLog = false)
        {
            MacSelectionInfo macInfo = GetPrimaryMacAddressInfo();
            string macAddr = macInfo.SelectedMac;

            // MAC 衍生部分（与 General.GetString1 一致）
            string macPart = GetString1(macAddr);

            // 硬件指纹部分（SHA256 前4字节 = 8位大写16进制）
            HardwareFingerprintInfo fingerprintInfo = GetHardwareFingerprintInfo();
            string hwPart = fingerprintInfo.Fingerprint;

            string hardwareId = BuildHardwareId(macPart, hwPart, out string raw, out string hex);

            Dictionary<string, string> snapshot = BuildGenerationSnapshot(macInfo, macPart, fingerprintInfo, raw, hex, hardwareId);
            WriteGenerationLog(snapshot);
            if (writeStartupComparisonLog)
                WriteStartupComparisonLog(snapshot);

            return hardwareId;
        }

        public static List<PhysicalAdapterHardwareInfo> GenerateAllPhysicalAdapterHardwareInfos()
        {
            MacSelectionInfo macInfo = GetPrimaryMacAddressInfo();
            HardwareFingerprintInfo fingerprintInfo = GetHardwareFingerprintInfo();
            return BuildPhysicalAdapterHardwareInfos(macInfo, fingerprintInfo);
        }

        public static List<string> GenerateAllPhysicalHardwareIds(bool includeCurrentSelected = false)
        {
            MacSelectionInfo macInfo = GetPrimaryMacAddressInfo();
            HardwareFingerprintInfo fingerprintInfo = GetHardwareFingerprintInfo();
            var hardwareIds = BuildPhysicalAdapterHardwareInfos(macInfo, fingerprintInfo)
                .Select(item => item.HardwareId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (includeCurrentSelected && !string.IsNullOrWhiteSpace(macInfo.SelectedMac))
            {
                string currentMacPart = GetString1(macInfo.SelectedMac);
                string currentHardwareId = BuildHardwareId(currentMacPart, fingerprintInfo.Fingerprint, out _, out _);
                hardwareIds.RemoveAll(id => string.Equals(id, currentHardwareId, StringComparison.OrdinalIgnoreCase));
                hardwareIds.Insert(0, currentHardwareId);
            }

            return hardwareIds;
        }

        private static List<PhysicalAdapterHardwareInfo> BuildPhysicalAdapterHardwareInfos(
            MacSelectionInfo macInfo, HardwareFingerprintInfo fingerprintInfo)
        {
            var result = new List<PhysicalAdapterHardwareInfo>();
            var seenMacs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (MacAdapterInfo adapter in macInfo.Adapters
                .Where(a => !a.Excluded && a.PhysicalAddress.Length >= 6)
                .OrderByDescending(a => a.Selected)
                .ThenBy(a => a.Priority)
                .ThenBy(a => a.PhysicalAddress, StringComparer.OrdinalIgnoreCase))
            {
                if (!seenMacs.Add(adapter.PhysicalAddress))
                    continue;

                string macPart = GetString1(adapter.PhysicalAddress);
                string hardwareId = BuildHardwareId(macPart, fingerprintInfo.Fingerprint, out _, out _);
                result.Add(new PhysicalAdapterHardwareInfo
                {
                    Name = adapter.Name,
                    Description = adapter.Description,
                    Status = adapter.Status,
                    MacAddress = adapter.PhysicalAddress,
                    HardwareId = hardwareId,
                    Selected = adapter.Selected
                });
            }

            return result;
        }

        private static string BuildHardwareId(string macPart, string hwPart, out string raw, out string hex)
        {
            // 拼接后整体做 SHA256，取前10字节 = 20位16进制
            raw = macPart + "|" + hwPart;
            using var sha256 = SHA256.Create();
            byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(raw));

            // 倒序分组格式化为 XXXX-XXXX-XXXX-XXXX-XXXX
            hex = BitConverter.ToString(hash, 0, 10).Replace("-", "").ToUpper();
            return $"{hex.Substring(16, 4)}-{hex.Substring(12, 4)}-{hex.Substring(8, 4)}-{hex.Substring(4, 4)}-{hex.Substring(0, 4)}";
        }

        public sealed class PhysicalAdapterHardwareInfo
        {
            public string Name { get; set; } = "";
            public string Description { get; set; } = "";
            public string Status { get; set; } = "";
            public string MacAddress { get; set; } = "";
            public string HardwareId { get; set; } = "";
            public bool Selected { get; set; }
        }

        private sealed class HardwareElement
        {
            public string Name { get; set; } = "";
            public string Value { get; set; } = "";
            public bool Used { get; set; }
            public string Note { get; set; } = "";
        }

        private sealed class HardwareFingerprintInfo
        {
            public string Fingerprint { get; set; } = "";
            public string Combined { get; set; } = "";
            public List<HardwareElement> Elements { get; } = new List<HardwareElement>();
            public string Error { get; set; } = "";
            public bool FromCache { get; set; }
        }

        private sealed class MacAdapterInfo
        {
            public string Name { get; set; } = "";
            public string Description { get; set; } = "";
            public string Type { get; set; } = "";
            public string Status { get; set; } = "";
            public string PhysicalAddress { get; set; } = "";
            public bool IsLocallyAdministered { get; set; }
            public int Priority { get; set; }
            public bool Selected { get; set; }
            public bool Excluded { get; set; }
            public string ExcludeReason { get; set; } = "";
        }

        private sealed class MacSelectionInfo
        {
            public string SelectedMac { get; set; } = "";
            public string FallbackMac { get; set; } = "unknow";
            public List<MacAdapterInfo> Adapters { get; } = new List<MacAdapterInfo>();
            public string Error { get; set; } = "";
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
        [Obfuscation(Feature = "virtualization", Exclude = false)]
        private static string GetHardwareFingerprint()
        {
            return GetHardwareFingerprintInfo().Fingerprint;
        }

        [Obfuscation(Feature = "virtualization", Exclude = false)]
        private static HardwareFingerprintInfo GetHardwareFingerprintInfo()
        {
            if (_cachedHardwareFingerprintInfo != null)
            {
                _cachedHardwareFingerprintInfo.FromCache = true;
                return _cachedHardwareFingerprintInfo;
            }

            var parts = new List<string>();
            var info = new HardwareFingerprintInfo();
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
                        AddFingerprintElement(info, parts, "Windows.Registry.MachineGuid", machineGuid);

                        using var cpuKey = Microsoft.Win32.Registry.LocalMachine
                            .OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                        string cpuId = cpuKey?.GetValue("Identifier")?.ToString() ?? "";
                        AddFingerprintElement(info, parts, "Windows.Registry.CpuIdentifier", cpuId);
#pragma warning restore CA1416
                    }
                    catch (Exception ex)
                    {
                        info.Elements.Add(new HardwareElement
                        {
                            Name = "Windows.Registry",
                            Value = "",
                            Used = false,
                            Note = ex.Message
                        });
                    }
                }
                else
                {
                    string? machineId = TryReadFileLine("/etc/machine-id");
                    bool usedEtcMachineId = !string.IsNullOrWhiteSpace(machineId);
                    AddFingerprintElement(info, parts, "Linux.File./etc/machine-id", machineId, usedEtcMachineId);

                    if (string.IsNullOrWhiteSpace(machineId))
                    {
                        machineId = TryReadFileLine("/var/lib/dbus/machine-id");
                        AddFingerprintElement(info, parts, "Linux.File./var/lib/dbus/machine-id", machineId);
                    }
                    else
                    {
                        info.Elements.Add(new HardwareElement
                        {
                            Name = "Linux.File./var/lib/dbus/machine-id",
                            Value = "",
                            Used = false,
                            Note = "skipped because /etc/machine-id was used"
                        });
                    }

                    try
                    {
                        if (File.Exists("/proc/cpuinfo"))
                        {
                            string cpuModel = File.ReadLines("/proc/cpuinfo")
                                .FirstOrDefault(l => l.StartsWith("model name", StringComparison.OrdinalIgnoreCase))
                                ?.Split(':').LastOrDefault()?.Trim() ?? "";
                            AddFingerprintElement(info, parts, "Linux.File./proc/cpuinfo.model name", cpuModel);
                        }
                        else
                        {
                            info.Elements.Add(new HardwareElement
                            {
                                Name = "Linux.File./proc/cpuinfo.model name",
                                Value = "",
                                Used = false,
                                Note = "file not found"
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        info.Elements.Add(new HardwareElement
                        {
                            Name = "Linux.File./proc/cpuinfo.model name",
                            Value = "",
                            Used = false,
                            Note = ex.Message
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                info.Error = ex.Message;
            }

            string combined = string.Join("|", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
            string fingerprint = string.IsNullOrEmpty(combined) ? "NOHW0000" : ComputeSha256Prefix(combined);
            info.Combined = combined;
            info.Fingerprint = fingerprint;
            _cachedHardwareFingerprintInfo = info;
            return info;
        }

        private static void AddFingerprintElement(HardwareFingerprintInfo info, List<string> parts,
            string name, string? value, bool? usedOverride = null)
        {
            bool used = usedOverride ?? !string.IsNullOrWhiteSpace(value);
            if (used && !string.IsNullOrWhiteSpace(value))
                parts.Add(value);

            info.Elements.Add(new HardwareElement
            {
                Name = name,
                Value = value ?? "",
                Used = used && !string.IsNullOrWhiteSpace(value)
            });
        }

        // SHA256 前4字节 → 8位大写16进制
        private static string ComputeSha256Prefix(string input)
        {
            using var sha256 = SHA256.Create();
            byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
            return BitConverter.ToString(hashBytes, 0, 4).Replace("-", "").ToUpper();
        }

        // 读取文件第一个非空行
        private static string? TryReadFileLine(string path)
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

        private static bool IsStablePhysicalAdapterType(NetworkInterfaceType type)
        {
            switch (type)
            {
                case NetworkInterfaceType.Ethernet:
                case NetworkInterfaceType.GigabitEthernet:
                case NetworkInterfaceType.FastEthernetT:
                case NetworkInterfaceType.FastEthernetFx:
                case NetworkInterfaceType.Wireless80211:
                    return true;
                default:
                    return false;
            }
        }

        private static string GetAdapterExcludeReason(NetworkInterface adapter, string macHex)
        {
            if (string.IsNullOrWhiteSpace(macHex) || macHex.Length < 6)
                return "empty or invalid MAC";

            if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                return "loopback adapter";

            if (adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                return "tunnel adapter";

            if (!IsStablePhysicalAdapterType(adapter.NetworkInterfaceType))
                return $"unsupported adapter type: {adapter.NetworkInterfaceType}";

            string text = $"{adapter.Name} {adapter.Description}".ToLowerInvariant();
            string[] commonVirtualKeywords =
            {
                "virtual",
                "docker",
                "container",
                "hyper-v",
                "wsl",
                "vmware",
                "virtualbox",
                "vboxnet",
                "vmnet",
                "qemu",
                "parallels",
                "bridge",
                "tap",
                "tun",
                "vpn",
                "wireguard",
                "openvpn",
                "zerotier",
                "tailscale",
                "loopback",
                "pseudo"
            };

            foreach (string keyword in commonVirtualKeywords)
            {
                if (text.Contains(keyword))
                    return $"virtual or unstable adapter keyword: {keyword}";
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string[] windowsVirtualKeywords =
                {
                    "vethernet",
                    "lightweight filter",
                    "packet scheduler",
                    "filter driver",
                    "vmswitch extension",
                    "native mac layer",
                    "wfp 802.3",
                    "qos packet",
                    "npcap",
                    "wan miniport",
                    "wi-fi direct",
                    "bluetooth",
                    "kernel debug",
                    "fortinet",
                    "anyconnect",
                    "checkpoint",
                    "check point",
                    "juniper",
                    "sonicwall",
                    "pptp",
                    "l2tp",
                    "sstp"
                };

                foreach (string keyword in windowsVirtualKeywords)
                {
                    if (text.Contains(keyword))
                        return $"virtual or unstable adapter keyword: {keyword}";
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                string interfaceName = adapter.Name.ToLowerInvariant();
                string[] linuxVirtualPrefixes =
                {
                    "docker",
                    "br",
                    "br-",
                    "br_",
                    "veth",
                    "virbr",
                    "bond",
                    "team",
                    "dummy",
                    "macvlan",
                    "ipvlan",
                    "ovs",
                    "cni",
                    "flannel",
                    "cali",
                    "weave",
                    "kube",
                    "vxlan",
                    "gre",
                    "gretap",
                    "sit",
                    "ip6tnl",
                    "tun",
                    "tap",
                    "wg",
                    "zt",
                    "tailscale",
                    "lo"
                };

                foreach (string prefix in linuxVirtualPrefixes)
                {
                    if (interfaceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return $"linux virtual adapter prefix: {prefix}";
                }

                string sysfsDevicePath = Path.Combine("/sys/class/net", adapter.Name, "device");
                if (!Directory.Exists(sysfsDevicePath) && !File.Exists(sysfsDevicePath))
                    return "linux adapter has no physical device in sysfs";
            }

            return "";
        }

        // 选取主网卡 MAC（与 DataCenter.EvalSB4Local 中网卡选取逻辑一致）
        private static string GetPrimaryMacAddress()
        {
            return GetPrimaryMacAddressInfo().SelectedMac;
        }

        private static MacSelectionInfo GetPrimaryMacAddressInfo()
        {
            string tempaddr = string.Empty;
            string tempaddr1 = "unknow";
            var info = new MacSelectionInfo();

            try
            {
                NetworkInterface[] adapters = NetworkInterface.GetAllNetworkInterfaces();

                var sortedAdapters = adapters
                    .OrderBy(a => GetAdapterTypePriority(a.NetworkInterfaceType))
                    .ThenBy(a => a.GetPhysicalAddress().ToString())
                    .ToList();

                foreach (NetworkInterface adapter in sortedAdapters)
                {
                    string addr = adapter.GetPhysicalAddress().ToString();
                    string excludeReason = GetAdapterExcludeReason(adapter, addr);
                    var adapterInfo = new MacAdapterInfo
                    {
                        Name = adapter.Name,
                        Description = adapter.Description,
                        Type = adapter.NetworkInterfaceType.ToString(),
                        Status = adapter.OperationalStatus.ToString(),
                        PhysicalAddress = addr,
                        IsLocallyAdministered = IsLocallyAdministeredMac(addr),
                        Priority = GetAdapterTypePriority(adapter.NetworkInterfaceType),
                        Excluded = !string.IsNullOrWhiteSpace(excludeReason),
                        ExcludeReason = excludeReason
                    };
                    info.Adapters.Add(adapterInfo);

                    if (addr.Length >= 6 && string.IsNullOrWhiteSpace(excludeReason))
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

                        if (tempaddr1 == "unknow")
                            tempaddr1 = addr;
                    }
                }

                if (tempaddr == string.Empty)
                    tempaddr = tempaddr1;

                foreach (var adapter in info.Adapters)
                    adapter.Selected = !adapter.Excluded
                        && string.Equals(adapter.PhysicalAddress, tempaddr, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                info.Error = ex.Message;
            }

            info.SelectedMac = tempaddr ?? "unknow";
            info.FallbackMac = tempaddr1;
            return info;
        }

        private static Dictionary<string, string> BuildGenerationSnapshot(MacSelectionInfo macInfo, string macPart,
            HardwareFingerprintInfo fingerprintInfo, string raw, string hashHex, string hardwareId)
        {
            var snapshot = new Dictionary<string, string>();
            snapshot["time.local"] = DateTime.Now.ToString("O");
            snapshot["time.utc"] = DateTime.UtcNow.ToString("O");
            snapshot["process.id"] = Environment.ProcessId.ToString();
            snapshot["os.description"] = RuntimeInformation.OSDescription;
            snapshot["os.architecture"] = RuntimeInformation.OSArchitecture.ToString();
            snapshot["app.baseDirectory"] = AppContext.BaseDirectory;
            snapshot["result.hardwareId"] = hardwareId;
            snapshot["mac.selected"] = macInfo.SelectedMac;
            snapshot["mac.fallback"] = macInfo.FallbackMac;
            snapshot["mac.part"] = macPart;
            snapshot["fingerprint.value"] = fingerprintInfo.Fingerprint;
            snapshot["fingerprint.fromCache"] = fingerprintInfo.FromCache.ToString();
            snapshot["fingerprint.combined"] = fingerprintInfo.Combined;
            if (!string.IsNullOrWhiteSpace(fingerprintInfo.Error))
                snapshot["fingerprint.error"] = fingerprintInfo.Error;
            snapshot["final.raw"] = raw;
            snapshot["final.sha256.first10bytes"] = hashHex;

            for (int i = 0; i < fingerprintInfo.Elements.Count; i++)
            {
                HardwareElement element = fingerprintInfo.Elements[i];
                string prefix = $"fingerprint.element.{i + 1}";
                snapshot[$"{prefix}.name"] = element.Name;
                snapshot[$"{prefix}.used"] = element.Used.ToString();
                snapshot[$"{prefix}.value"] = element.Value;
                if (!string.IsNullOrWhiteSpace(element.Note))
                    snapshot[$"{prefix}.note"] = element.Note;
            }

            if (!string.IsNullOrWhiteSpace(macInfo.Error))
                snapshot["mac.error"] = macInfo.Error;

            var physicalRegistrations = BuildPhysicalAdapterHardwareInfos(macInfo, fingerprintInfo);
            snapshot["registration.physical.count"] = physicalRegistrations.Count.ToString();
            for (int i = 0; i < physicalRegistrations.Count; i++)
            {
                PhysicalAdapterHardwareInfo item = physicalRegistrations[i];
                string prefix = $"registration.physical.{i + 1}";
                snapshot[$"{prefix}.selected"] = item.Selected.ToString();
                snapshot[$"{prefix}.name"] = item.Name;
                snapshot[$"{prefix}.description"] = item.Description;
                snapshot[$"{prefix}.status"] = item.Status;
                snapshot[$"{prefix}.mac"] = item.MacAddress;
                snapshot[$"{prefix}.hardwareId"] = item.HardwareId;
            }

            for (int i = 0; i < macInfo.Adapters.Count; i++)
            {
                MacAdapterInfo adapter = macInfo.Adapters[i];
                string prefix = $"mac.adapter.{i + 1}";
                snapshot[$"{prefix}.selected"] = adapter.Selected.ToString();
                snapshot[$"{prefix}.name"] = adapter.Name;
                snapshot[$"{prefix}.description"] = adapter.Description;
                snapshot[$"{prefix}.type"] = adapter.Type;
                snapshot[$"{prefix}.status"] = adapter.Status;
                snapshot[$"{prefix}.physicalAddress"] = adapter.PhysicalAddress;
                snapshot[$"{prefix}.isLocallyAdministered"] = adapter.IsLocallyAdministered.ToString();
                snapshot[$"{prefix}.priority"] = adapter.Priority.ToString();
                snapshot[$"{prefix}.excluded"] = adapter.Excluded.ToString();
                if (!string.IsNullOrWhiteSpace(adapter.ExcludeReason))
                    snapshot[$"{prefix}.excludeReason"] = adapter.ExcludeReason;
            }

            return snapshot;
        }

        private static void WriteGenerationLog(Dictionary<string, string> snapshot)
        {
            try
            {
                string logPath = GetGenerationLogPath();
                string? logDir = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrWhiteSpace(logDir))
                    Directory.CreateDirectory(logDir);

                var sb = new StringBuilder();
                sb.AppendLine("============================================================");
                AppendSnapshot(sb, snapshot);

                lock (_generationLogLock)
                {
                    File.AppendAllText(logPath, sb.ToString(), Encoding.UTF8);
                }
            }
            catch
            {
                // 诊断日志不能影响注册码生成和授权流程。
            }
        }

        private static void WriteStartupComparisonLog(Dictionary<string, string> currentSnapshot)
        {
            try
            {
                string diffLogPath = GetStartupComparisonLogPath();
                string? diffLogDir = Path.GetDirectoryName(diffLogPath);
                if (!string.IsNullOrWhiteSpace(diffLogDir))
                    Directory.CreateDirectory(diffLogDir);

                string snapshotPath = GetStartupSnapshotPath();
                Dictionary<string, string> previousSnapshot = File.Exists(snapshotPath)
                    ? ReadSnapshotFile(snapshotPath)
                    : new Dictionary<string, string>();

                var sb = new StringBuilder();
                sb.AppendLine($"compare.time.local={NormalizeLogValue(DateTime.Now.ToString("O"))}");
                sb.AppendLine($"compare.time.utc={NormalizeLogValue(DateTime.UtcNow.ToString("O"))}");
                string currentHardwareId = GetSnapshotValue(currentSnapshot, "result.hardwareId");

                if (previousSnapshot.Count == 0)
                {
                    sb.AppendLine("summary=没有找到上一次启动快照，本次仅保存当前启动快照，下一次启动开始对比。");
                    sb.AppendLine($"current.result.hardwareId={NormalizeLogValue(currentHardwareId)}");
                }
                else
                {
                    string previousHardwareId = GetSnapshotValue(previousSnapshot, "result.hardwareId");
                    sb.AppendLine($"previous.result.hardwareId={NormalizeLogValue(previousHardwareId)}");
                    sb.AppendLine($"current.result.hardwareId={NormalizeLogValue(currentHardwareId)}");

                    if (string.Equals(previousHardwareId, currentHardwareId, StringComparison.Ordinal))
                    {
                        sb.AppendLine("summary=注册码未变化，不展开启动要素对比。");
                        sb.AppendLine("diff=注册码未变化");
                    }
                    else
                    {
                        var changedKeys = GetChangedKeys(previousSnapshot, currentSnapshot).ToList();
                        sb.AppendLine($"summary=注册码发生变化，发现 {changedKeys.Count} 个启动要素差异。");

                        if (changedKeys.Count == 0)
                        {
                            sb.AppendLine("diff=注册码变化，但未发现已记录要素差异。");
                        }
                        else
                        {
                            foreach (string key in changedKeys)
                            {
                                sb.AppendLine($"diff.{key}.previous={NormalizeLogValue(GetSnapshotValue(previousSnapshot, key))}");
                                sb.AppendLine($"diff.{key}.current={NormalizeLogValue(GetSnapshotValue(currentSnapshot, key))}");
                            }
                        }
                    }
                }

                lock (_generationLogLock)
                {
                    File.WriteAllText(diffLogPath, sb.ToString(), Encoding.UTF8);
                    WriteSnapshotFile(snapshotPath, currentSnapshot);
                }
            }
            catch
            {
                // 启动对比日志不能影响注册码生成和授权流程。
            }
        }

        private static IEnumerable<string> GetChangedKeys(Dictionary<string, string> previousSnapshot,
            Dictionary<string, string> currentSnapshot)
        {
            var ignoredKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "time.local",
                "time.utc",
                "process.id",
                "fingerprint.fromCache",
                "app.baseDirectory"
            };

            return previousSnapshot.Keys
                .Union(currentSnapshot.Keys)
                .Where(k => !ignoredKeys.Contains(k))
                .Where(IsRegistrationFactorKey)
                .Where(k => !string.Equals(NormalizeLogValue(GetSnapshotValue(previousSnapshot, k)),
                    NormalizeLogValue(GetSnapshotValue(currentSnapshot, k)), StringComparison.Ordinal))
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsRegistrationFactorKey(string key)
        {
            return key.StartsWith("result.", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("mac.", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("registration.", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("fingerprint.", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("final.", StringComparison.OrdinalIgnoreCase);
        }

        private static void AppendSnapshot(StringBuilder sb, Dictionary<string, string> snapshot)
        {
            foreach (var item in snapshot)
                sb.AppendLine($"{item.Key}={NormalizeLogValue(item.Value)}");
        }

        private static Dictionary<string, string> ReadSnapshotFile(string path)
        {
            var snapshot = new Dictionary<string, string>();
            foreach (string line in File.ReadLines(path, Encoding.UTF8))
            {
                int separatorIndex = line.IndexOf('=');
                if (separatorIndex <= 0) continue;
                string key = line.Substring(0, separatorIndex);
                string value = line.Substring(separatorIndex + 1);
                snapshot[key] = value;
            }

            return snapshot;
        }

        private static void WriteSnapshotFile(string path, Dictionary<string, string> snapshot)
        {
            string? snapshotDir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(snapshotDir))
                Directory.CreateDirectory(snapshotDir);

            var sb = new StringBuilder();
            AppendSnapshot(sb, snapshot);
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        private static string GetSnapshotValue(Dictionary<string, string> snapshot, string key)
        {
            return snapshot.TryGetValue(key, out string? value) ? value ?? "" : "<missing>";
        }

        private static string GetGenerationLogPath()
        {
            string? explicitLogPath = Environment.GetEnvironmentVariable("DOCKERHOST_REGISTRATION_LOG");
            if (!string.IsNullOrWhiteSpace(explicitLogPath))
                return explicitLogPath;

            string? logDir = Environment.GetEnvironmentVariable("DOCKERHOST_REGISTRATION_LOG_DIR");
            if (string.IsNullOrWhiteSpace(logDir))
                logDir = Path.Combine(AppContext.BaseDirectory, "logs");

            return Path.Combine(logDir, "hardware-registration.log");
        }

        private static string GetStartupComparisonLogPath()
        {
            string? explicitLogPath = Environment.GetEnvironmentVariable("DOCKERHOST_REGISTRATION_DIFF_LOG");
            if (!string.IsNullOrWhiteSpace(explicitLogPath))
                return explicitLogPath;

            string? logDir = Environment.GetEnvironmentVariable("DOCKERHOST_REGISTRATION_LOG_DIR");
            if (string.IsNullOrWhiteSpace(logDir))
                logDir = Path.Combine(AppContext.BaseDirectory, "logs");

            return Path.Combine(logDir, "hardware-registration-startup-diff.log");
        }

        private static string GetStartupSnapshotPath()
        {
            string? explicitSnapshotPath = Environment.GetEnvironmentVariable("DOCKERHOST_REGISTRATION_STARTUP_SNAPSHOT");
            if (!string.IsNullOrWhiteSpace(explicitSnapshotPath))
                return explicitSnapshotPath;

            string? logDir = Environment.GetEnvironmentVariable("DOCKERHOST_REGISTRATION_LOG_DIR");
            if (string.IsNullOrWhiteSpace(logDir))
                logDir = Path.Combine(AppContext.BaseDirectory, "logs");

            return Path.Combine(logDir, "hardware-registration-startup-last.snapshot");
        }

        private static string NormalizeLogValue(string? value)
        {
            if (value == null) return "";
            return value.Replace("\r", "\\r").Replace("\n", "\\n");
        }

    }
}
