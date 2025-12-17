using Hardware.Info;
using Microsoft.AspNetCore.SignalR; // 添加此 using 指令
using System.Collections.Concurrent; // 新增 using
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
            // 尝试从字典中移除断开连接的客户端
            var disconnectedClientId = _activeClients.FirstOrDefault(kvp => kvp.Value == Context.ConnectionId).Key;
            if (disconnectedClientId != null)
            {
                _activeClients.TryRemove(disconnectedClientId, out _);
            }
            return base.OnDisconnectedAsync(exception);
        }

        // 客户端调用此方法以获取加密的硬件ID
        public async Task GetEncryptedHardwareId(string clientId, int salt)
        {
            var currentConnectionId = Context.ConnectionId;

            // 检查此 clientId 是否已存在于不同的连接上
            if (_activeClients.TryGetValue(clientId, out var existingConnectionId) && existingConnectionId != currentConnectionId)
            {
                // 如果是，则通知新连接的客户端，并终止此操作
                await Clients.Caller.SendAsync("ClientAlreadyConnected", clientId);
                return;
            }

            // 如果 clientId 不存在，则将其与当前连接ID关联
            _activeClients.TryAdd(clientId, currentConnectionId);

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
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // 配置 Kestrel 监听方式
            builder.WebHost.ConfigureKestrel((context, serverOptions) =>
            {
                var communicationSection = context.Configuration.GetSection("Communication");
                string type = communicationSection.GetValue<string>("Type") ?? "UnixSocket";

                if (type.Equals("UnixSocket", StringComparison.OrdinalIgnoreCase))
                {
                    string socketPath = communicationSection.GetValue<string>("UnixSocketPath") ?? "/var/run/dockerhost.sock";
                    
                    // 如果 socket 文件已存在，需要先删除，否则会报错
                    if (File.Exists(socketPath))
                    {
                        File.Delete(socketPath);
                    }
                    
                    serverOptions.ListenUnixSocket(socketPath);
                    Console.WriteLine($"正在监听 Unix Domain Socket: {socketPath}");
                }
                else
                {
                    int port = communicationSection.GetValue<int>("TcpPort", 5000);
                    string ipString = communicationSection.GetValue<string>("IpAddress");

                    if (string.IsNullOrWhiteSpace(ipString) || ipString == "*" || ipString == "0.0.0.0")
                    {
                        serverOptions.ListenAnyIP(port);
                        Console.WriteLine($"正在监听 TCP (所有IP) 端口: {port}");
                    }
                    else if (System.Net.IPAddress.TryParse(ipString, out var ipAddress))
                    {
                        serverOptions.Listen(ipAddress, port);
                        Console.WriteLine($"正在监听 TCP ({ipAddress}) 端口: {port}");
                    }
                    else
                    {
                        serverOptions.ListenAnyIP(port);
                        Console.WriteLine($"配置的 IP 地址无效 ({ipString})，默认监听所有 IP，端口: {port}");
                    }
                }
            });

            // 添加对 Systemd 的支持
            builder.Host.UseSystemd();

            // 配置服务
            builder.Services.AddControllers();
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();
            builder.Services.AddSignalR(); // 添加 SignalR 服务
            
            var app = builder.Build();

            // --- 使用矩形框醒目地输出注册码 ---
            var hardwareId = HardwareInfoGenerator.GenerateHardwareInfo();
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

    // 生成基于硬件信息的GUID
    public static class HardwareInfoGenerator
    {
        public static string GenerateHardwareInfo()
        {
            var sb = new StringBuilder();

            // 获取主板 ID
            sb.Append(GetMotherboardSerialNumber());
            // 再获取第一个非虚拟网卡的MAC地址
            //Console.WriteLine($"第一个标识：{GetMotherboardSerialNumber()}");
            var macAddress = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up &&
                                nic.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                                !nic.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                .Select(nic => nic.GetPhysicalAddress().ToString())
                .FirstOrDefault();
            //Console.WriteLine($"第二个标识：{macAddress}");
            if (!string.IsNullOrEmpty(macAddress.ToString()))
                sb.Append(macAddress.ToString());
            // 如果网卡也获取不到，则获取主机名
            if (sb.Length == 0)
            {
                sb.Append(Environment.MachineName);
                sb.Append("ganweisoft");
            }

           // Console.WriteLine($"第三个标识：{Environment.MachineName}");


            // 生成哈希作为GUID
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
            //把hash转成大写的字符串
            var strResult = BitConverter.ToString(hash).Replace("-", "").ToUpperInvariant();
            //return new Guid(hash.Take(16).ToArray()).ToString();
            return strResult.Substring(12, 4) + "-" + strResult.Substring(8, 4) + "-" + strResult.Substring(4, 4) + "-" + strResult.Substring(0, 4);
        }

        // 获取主板 ID的方法
        public static string GetMotherboardSerialNumber()
        {
            try
            {
                var hardwareInfo = new HardwareInfo();
                hardwareInfo.RefreshMotherboardList();

                foreach (var motherboard in hardwareInfo.MotherboardList)
                {
                    if (!string.IsNullOrWhiteSpace(motherboard.SerialNumber))
                    {
                        return motherboard.SerialNumber.Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                return string.Empty;
            }
            return string.Empty;
        }
    }
}