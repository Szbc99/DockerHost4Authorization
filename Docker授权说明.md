# Docker 授权机制说明

## 1. 架构概述

```
+--------------------------------------------------+
| 宿主机 Linux                                        |
|                                                    |
|  DockerHost4Authorization (服务端)                    |
|  ├─ 监听 Docker 网桥 172.17.0.1:5000                 |
|  ├─ 凭证锁 /var/tmp/gw-auth-{MD5}.lock              |
|  └─ 生成硬件注册码                                    |
|                       ▲                              |
|                       │ TCP (SignalR / WebSocket)     |
|                       │ 仅容器可达 (RFC 1918)          |
|  +--------------------+---------------------+        |
|  | Docker 容器 (普通网络模式)                  |        |
|  |  GWDataCenter (客户端)                      |        |
|  |  └─ 连接 host.docker.internal:5000         |        |
|  +------------------------------------------+        |
+--------------------------------------------------+
```

服务端运行在宿主机，客户端运行在 Docker 容器内。两者通过 SignalR（WebSocket，TCP）长连接完成基于宿主机硬件指纹的授权验证。

**核心设计目标**：

| 目标 | 机制 |
|---|---|
| 授权绑定宿主机硬件 | 服务端采集 MAC / Machine-ID / CPU 生成注册码，客户端解密后校验 |
| 容器防多开 | 同一 clientId (GUID) 同时仅允许一个 SignalR 连接 |
| 服务端防多开 | 硬件注册码 MD5 → 文件锁，同机第二个实例无法启动 |
| 外部机器不可达 | 服务端绑定 Docker 网桥 IP（RFC 1918 私有地址） |

---

## 2. 服务端：DockerHost4Authorization

### 2.1 项目结构

.NET 10 ASP.NET Core 应用，单文件 `Program.cs`，包含四个类，附加 Swagger（仅开发环境）、Systemd 和 Windows Service 集成：

| 类名 | 职责 |
|---|---|
| `HardwareHub` | SignalR Hub，管理客户端长连接与防多开 |
| `Program` | 应用入口，启动时抢占凭证锁，配置 Kestrel 监听 |
| `EncryptionHelper` | AES-256-CBC 对称加密 |
| `HardwareInfoGenerator` | 采集宿主机硬件指纹生成注册码 |

### 2.2 凭证锁（防服务端多开）

在绑定 TCP 端口之前，基于硬件注册码的 MD5 抢占全局文件锁：

```
Main()
  ├─ GenerateHardwareInfo()        ← 生成硬件注册码
  ├─ AcquireLicenseLock()          ← 抢占文件锁（在 Kestrel 之前）
  ├─ ConfigureKestrel()            ← 锁成功后才能绑定端口
  └─ Build → Run
```

**锁文件路径**：`/var/tmp/gw-auth-{MD5(硬件注册码，小写)}.lock`（Windows 下使用 `%TEMP%`）

**互斥原理**：`FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)`

操作系统内核保证同一文件同一时刻只有一个进程持有写句柄。第二个实例启动时触发 `IOException`，打印错误并退出。

**崩溃恢复**：进程意外终止（`kill -9` / OOM）时内核自动回收文件句柄，锁立即释放，守护进程可安全拉起。

**场景推演**：

| 场景 | 结果 |
|---|---|
| 同机复制部署目录，改端口启动第二实例 | 硬件码相同 → 锁文件相同 → IOException → 退出 |
| 不同物理机各运行一个实例 | 硬件码不同 → 锁文件不同 → 正常运行 |
| 进程被 kill -9 | 内核回收句柄 → 锁自动释放 |

### 2.3 通讯方式

Linux 下绑定 Docker 网桥地址数组，Windows 下绑定指定 IP：

| 操作系统 | 通讯方式 | 配置项 |
|---|---|---|
| Linux | TCP，绑定 Docker 网桥 | `DockerBridgeIps`（默认 `["172.17.0.1"]`），`TcpPort`（默认 5000） |
| Windows | TCP，绑定指定 IP 或所有 IP | `IpAddress`，`TcpPort`（默认 5000） |
| 其他 | TCP，监听所有 IP | `TcpPort`（默认 5000） |

Linux 下遍历 `DockerBridgeIps` 数组依次 `Listen`，支持同时监听多个网桥地址（覆盖默认 docker0 和 Docker Compose 自定义网络）。所有配置的 IP 均无效时回退到 `127.0.0.1`。

Windows 下 `IpAddress` 为空、`*` 或 `0.0.0.0` 时监听所有 IP；无效 IP 也会回退到所有 IP。非 Windows/Linux 环境统一监听所有 IP。

### 2.4 SignalR Hub

端点路径：`/hardwarehub`

**Hub 方法**：

| 方法 | 方向 | 说明 |
|---|---|---|
| `GetEncryptedHardwareId(clientId, salt)` | 客户端 → 服务端 | 请求加密后的硬件注册码 |
| `ReceiveEncryptedHardwareId(encryptedId)` | 服务端 → 客户端 | 返回 Base64 编码的 AES 加密注册码 |
| `ClientAlreadyConnected(clientId)` | 服务端 → 客户端 | 拒绝重复连接 |
| `Challenge(nonce)` | 服务端 → 客户端 | 定期挑战，验证客户端仍持有硬件注册码 |
| `ChallengeResponse(clientId, nonce, response)` | 客户端 → 服务端 | 客户端用 HMAC-SHA256 签名 nonce 作为回应 |
| `ForceDisconnect(reason)` | 服务端 → 客户端 | 挑战超时或验证失败，强制客户端退出 |

### 2.5 防客户端多开

服务端维护 `ConcurrentDictionary<string, string> _activeClients`，映射 `clientId → ConnectionId`：

```
客户端调用 GetEncryptedHardwareId(clientId, salt)
  │
  ├─ _activeClients 已存在该 clientId 且 ConnectionId 相同
  │     → 同一连接的重复请求，允许继续（重连场景）
  │
  ├─ _activeClients 已存在该 clientId 且 ConnectionId 不同
  │     → 发送 ClientAlreadyConnected，拒绝连接
  │
  ├─ _activeClients 不存在该 clientId
  │     → TryAdd 到字典
  │     ├─ 成功 → 生成并返回加密注册码
  │     └─ 失败（并发竞争）→ 再次检查，拒绝重复连接
  │
  └─ 生成加密注册码 → 返回 ReceiveEncryptedHardwareId
```

`OnDisconnectedAsync` 时仅当 `ConnectionId` 与字典中记录一致才清理条目，防止重连后的新连接被旧连接的断开事件误删。

### 2.5.1 周期性挑战-响应（防客户端篡改）

针对客户端可能被反编译篡改、移除 `Environment.Exit` 的风险，服务端每 30 秒发起一轮挑战：

```
服务端 (每30s定时器)                          客户端
  │                                            │
  │── Challenge(nonce) ──────────────────────►│ HMAC-SHA256(注册码, nonce)
  │                                            │
  │◄── ChallengeResponse(clientId, nonce, rsp) ─│
  │                                            │
  │  验证 rsp == expected ?                    │
  │  ├─ 一致 → 继续                            │
  │  └─ 不一致 → ForceDisconnect → 断开连接      │
  │                                            │
  │  10s 内未收到响应 → ForceDisconnect → 断开    │
```

**关键设计点**：

- 挑战响应 = `HMAC-SHA256(硬件注册码, nonce)`，只有持有正确注册码的客户端才能通过验证
- 篡改客户端即使忽略了 `Closed` 事件，也无法伪造挑战响应（不知道注册码）
- 服务端主动探测比被动等待心跳超时更可靠，超时检测从 10s 缩短为主动验证
- 挑战失败或超时 → 服务端发送 `ForceDisconnect` → 客户端 `Environment.Exit(1)`
- 客户端正常断开时，`OnDisconnectedAsync` 同时清理该 clientId 的所有待处理挑战

### 2.6 连接超时配置

```csharp
options.ClientTimeoutInterval = TimeSpan.FromSeconds(10);
options.KeepAliveInterval = TimeSpan.FromSeconds(5);
```

同机部署下延迟接近零，短超时可快速检测客户端离线，及时释放 `_activeClients` 条目。

### 2.7 注册码生成算法

注册码格式：`XXXX-XXXX-XXXX-XXXX-XXXX`（5 段 4 位大写十六进制字符）

```
输入：宿主机 MAC 地址 + 硬件指纹
  │
  ├─ MAC 衍生部分（GetString1）
  │     取主网卡物理地址 → 拼接 "AlarmCenter1" → 替换冒号为8/空格为F → 反转 → 截取前 8 字符
  │
  ├─ 硬件指纹部分（GetHardwareFingerprint，首次计算后缓存）
  │     Windows: MachineGuid + CPU Identifier (注册表)
  │     Linux:   /etc/machine-id + CPU model name (/proc/cpuinfo)
  │     → SHA256 → 取前 4 字节 → 8 位十六进制
  │
  └─ 最终合成
       "macPart|hwPart" → SHA256 → 取前 10 字节 → 20 位十六进制
       → 按 4 字符分组倒序排列
```

### 2.8 加密算法

| 参数 | 值 |
|---|---|
| 算法 | AES-256-CBC |
| 密钥派生 | PBKDF2 (RFC 2898) |
| Hash 算法 | SHA-256 |
| 迭代次数 | 10,000 |
| 密钥长度 | 256 bit |
| IV 长度 | 128 bit |
| 密文编码 | Base64 |

密钥派生参数：
- `password` = 客户端提供的 `clientId`（来自授权 XML 的 `<guid>` 节点）
- `salt` = 客户端随机生成 6 位整数（100,000 ~ 999,999）

---

## 3. 客户端：GWDataCenter.DataCenter

### 3.1 授权启动流程

客户端在 `StationItem.init()` 初始化阶段调用 `DataCenter.EvalSB4Docker(null)`，该公开方法读取授权 XML 中的 `<mode>` 节点，仅当 `mode=4` 时才进入 Docker 授权分支。

```
StationItem.init()
  └─ DataCenter.EvalSB4Docker(null)               [公开方法，检查 mode=4]
       ├─ 读取 <mode>：仅当 mode=4 时继续
       ├─ 检查 <limitdate>：是否超过有效期
       └─ EvalSB4Docker()                         [私有方法，核心授权逻辑]
            ├─ _ = ConnectDockerHost()             [fire-and-forget，建立 SignalR 长连接]
            ├─ dockerSmbReceived.Task.Wait(5s)     [阻塞等待，最多 5 秒]
            │    ├─ 收到 → strSMB4Docker 被赋值
            │    └─ 超时 → strSMB4Docker = "超时未获取"
            └─ CheckSMB(strSMB4Docker)             [验证解密后的注册码是否在 <shibie> 列表中]
                 ├─ 通过 → bSiBOK=true → 正常运行
                 └─ 不通过 → bTemper4QX=true → 临时授权模式，运行一段时间后自动退出
```

**关键变量**：

| 变量 | 类型 | 说明 |
|---|---|---|
| `strSMB4Docker` | `static string` | 缓存解密后的 Docker 注册码，初始值 `"未获取"` |
| `bStartDocker` | `static bool` | 防止重复启动 SignalR 连接，整个进程生命周期仅启动一次 |
| `iSalt4Docker` | `static int` | AES 加密盐值，首次使用时随机生成 100000~999999，进程内不变 |
| `dockerSmbReceived` | `TaskCompletionSource<bool>` | 用于 `EvalSB4Docker` 阻塞等待 `ConnectDockerHost` 异步结果的信号量 |
| `connectionBuilder` | `static HubConnectionBuilder` | SignalR 连接构造器，缓存复用，避免重复创建 |

### 3.2 连接配置

#### 3.2.1 配置文件位置

客户端配置通过 `PropertyService` 从 XML 文件读取，文件路径为：

```
{应用程序根目录}/data/AlarmCenter/AlarmCenterProperties.xml
```

`GetPropertyFromPropertyService("HostServer", "DockerHostPort", "5000")` 方法在 XML 中查找 `<Properties name="HostServer">` 节点，读取子节点值，找不到时返回传入的默认值。

#### 3.2.2 配置项

| 配置项 | XML 路径 | 默认值 | 说明 |
|---|---|---|---|
| `DockerHostIP` | `HostServer.DockerHostIP` | `host.docker.internal` | 服务端地址 |
| `DockerHostPort` | `HostServer.DockerHostPort` | `5000` | 服务端 TCP 端口 |

#### 3.2.3 IP 地址回退逻辑

```csharp
if (string.IsNullOrWhiteSpace(dockerHostIP) || dockerHostIP == "*" || dockerHostIP == "0.0.0.0")
    dockerHostIP = "host.docker.internal";
```

| 配置值 | 行为 |
|---|---|
| 正常 IP（如 `172.17.0.1`） | 直接使用 |
| 空字符串 / `*` / `0.0.0.0` | 回退到 `host.docker.internal` |
| `host.docker.internal` | Docker 桌面版自动解析为宿主机网关；Linux 下需 `--add-host` 注入 |

#### 3.2.4 clientId 与盐值

| 参数 | 来源 | 说明 |
|---|---|---|
| `clientId` | 授权 XML 的 `<guid>` 节点 | 全局唯一标识，同时作为 AES 加密的 password；服务端用其做防多开 key |
| `iSalt4Docker` | `new Random().Next(100000, 999999)` | AES 加密盐值，首次使用时随机生成，进程生命周期内不变 |

### 3.3 连接方式

统一使用 TCP + SignalR (WebSocket)，URL 格式：

```
http://{DockerHostIP}:{DockerHostPort}/hardwarehub
```

示例：
- Docker Desktop（Windows/Mac）：`http://host.docker.internal:5000/hardwarehub`
- Linux Docker（手动指定网桥）：`http://172.17.0.1:5000/hardwarehub`
- K8s（通过 status.hostIP）：`http://10.0.0.15:5000/hardwarehub`

`host.docker.internal` 在 Docker Desktop 中自动可用。Linux 下需容器启动参数 `--add-host host.docker.internal:host-gateway`（Docker 20.10+ 支持 `host-gateway`）。

**连接生命周期**：

- `connectionBuilder` 是 `static` 变量，首次连接时创建并配置 `WithAutomaticReconnect()`，后续调用复用同一构造器
- `bStartDocker` 标志确保 `StartAsync()` 在整个进程生命周期仅调用一次
- 连接建立后保持 WebSocket 长连接，不主动断开

### 3.4 SignalR 事件处理

| 事件 | 方向 | 行为 |
|---|---|---|
| `ReceiveEncryptedHardwareId` | 服务端→客户端 | AES 解密授权码 → 赋值 `strSMB4Docker` → `dockerSmbReceived.TrySetResult(true)` |
| `ClientAlreadyConnected` | 服务端→客户端 | 同 GUID 已有活跃连接 → `StopAsync()` → `Environment.Exit(1)` |
| `Closed` | SignalR 内置 | 连接永久关闭（重试耗尽或服务端主动断开）→ 三个混淆的 `Environment.Exit(1)` 方法之一被调用 |
| `Reconnecting` | SignalR 内置 | 记录警告日志，输出错误原因 |
| `Reconnected` | SignalR 内置 | 记录恢复日志，**重新调用 `GetEncryptedHardwareId(clientId, iSalt4Docker)` 恢复防多开保护** |
| `Challenge` | 服务端→客户端 | 收到 nonce → HMAC-SHA256(注册码, nonce) → 调用 `ChallengeResponse` 回应 |
| `ForceDisconnect` | 服务端→客户端 | 挑战超时或验证失败 → `Environment.Exit(1)` |

#### 3.4.1 挑战-响应处理细节

客户端收到 `Challenge(nonce)` 后的处理逻辑：

```csharp
connection.On<string>("Challenge", async (nonce) =>
{
    // 注册码未就绪时忽略挑战（避免误判）
    if (string.IsNullOrEmpty(strSMB4Docker) || strSMB4Docker == "未获取" || strSMB4Docker == "超时未获取")
        return;

    // HMAC-SHA256 签名
    byte[] hash = HMACSHA256.HashData(
        Encoding.UTF8.GetBytes(strSMB4Docker),
        Encoding.UTF8.GetBytes(nonce));
    string response = Convert.ToHexString(hash);

    // 回应服务端
    await connection.InvokeAsync("ChallengeResponse", clientId, nonce, response);
});
```

**关键点**：
- 仅当 `strSMB4Docker` 已成功获取（非空、非"未获取"、非"超时未获取"）时才响应
- 使用 `System.Security.Cryptography.HMACSHA256.HashData`（静态方法，无需实例化）
- 响应格式为大写十六进制字符串（`Convert.ToHexString`）

### 3.5 自动重连

```csharp
connectionBuilder.WithAutomaticReconnect();
```

SignalR 默认重连策略（0s → 2s → 10s → 30s，共 4 次尝试，全部失败后触发 `Closed`）。

重连成功后 `Reconnected` 回调执行：
```csharp
await connection.InvokeAsync("GetEncryptedHardwareId", clientId, iSalt4Docker);
```
向服务端重新注册 `clientId → 新ConnectionId` 映射，确保防多开保护在重连后不丢失。注意重连时 salt 不变（`iSalt4Docker` 已缓存），服务端用相同 salt 重新加密。

### 3.6 解密算法

客户端解密方法：`MD5DES.DockerDecryptString(string cipherText, string password, int salt)`

位置：`GWDataCenter/加解密/MD5DES.cs:295`

与服务端 `EncryptionHelper.EncryptString` 完全对称：

| 参数 | 值 |
|---|---|
| 算法 | AES-256-CBC |
| 密钥派生 | PBKDF2 (Rfc2898DeriveBytes.Pbkdf2) |
| Hash 算法 | SHA-256 |
| 迭代次数 | 10,000 |
| 密钥长度 | 256 bit（32 字节） |
| IV 长度 | 128 bit（16 字节） |
| 密文编码 | Base64 |

密钥和 IV 由同一 password + salt 分别派生：
```csharp
byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, saltBytes, 10000, SHA256, 32);
byte[] iv  = Rfc2898DeriveBytes.Pbkdf2(password, saltBytes, 10000, SHA256, 16);
```

### 3.7 防篡改保护机制

为防止攻击者反编译后禁用 `Environment.Exit` 绕过授权，客户端使用了多层冗余退出保护：

#### 3.7.1 三个混淆的退出方法

| 方法 | 触发场景 | 签名特征 |
|---|---|---|
| `FlushTelemetryBuffer(int code)` | `ClientAlreadyConnected`、`ForceDisconnect` | 参数为 `int` |
| `ResetConnectionPool(string tag)` | `Closed` 含错误信息 | 参数为 `string`，附带销毁 `strSMB4Docker` 副作用 |
| `CollectGcStats(long timestamp)` | `Closed` 无错误 | 参数为 `long`（时间戳） |

**设计意图**：
- Eazfuscator 混淆后方法名变为无意义符号（如 `a.b` / `c.d` / `e.f`）
- 三个方法签名各不相同，防止按 `call` 指令模式批量搜索
- 全部标记 `[Obfuscation(Feature = "virtualization")]`，方法体被虚拟化，反编译后不可读
- 攻击者必须全部找到并禁用三个方法才能阻止退出

#### 3.7.2 关键代码保护

所有授权相关方法均使用 `[Obfuscation(Feature = "virtualization", Exclude = false)]` 标记，包括：
- `EvalSB4Docker` / `EvalSB4Docker(object o)`
- `ConnectDockerHost`
- `GetSbm` / `GetSbmV2` / `GetHardwareFingerprint`
- `CheckSMB` / `GetSBMList`
- `DockerDecryptString`（在 MD5DES.cs 中）
- `GetLicenseRunType`

#### 3.7.3 进程退出触发点汇总

| 触发条件 | 退出方式 | 位置 |
|---|---|---|
| 同 GUID 重复连接 | `FlushTelemetryBuffer(1)` | `ClientAlreadyConnected` 回调 |
| 服务端要求断开（挑战失败/超时） | `FlushTelemetryBuffer(0xE001)` | `ForceDisconnect` 回调 |
| 连接关闭（含异常） | `ResetConnectionPool(error.Message)` | `Closed` 事件 |
| 连接关闭（正常） | `CollectGcStats(DateTime.UtcNow.Ticks)` | `Closed` 事件 |

### 3.8 客户端 Docker 授权配置清单

要使客户端以 Docker 授权模式运行，需完成以下配置：

#### 3.8.1 授权 XML 文件

位置：`{应用程序根目录}/data/AlarmCenter/AlarmCenterProperties.xml` 同目录下的授权文件（通常为 `*.xml`，由 `StationItem.XMLDoc` 加载）。

必要节点：

```xml
<root>
  <guid>xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx</guid>
  <!-- 全局唯一标识，同时作为 AES 加密的 password -->
  <shibie>XXXX-XXXX-XXXX-XXXX-XXXX</shibie>
  <!-- 授权的注册码（5段V2格式），用于 CheckSMB() 验证 -->
  <mode>4</mode>
  <!-- 授权模式：1=本地 2=云端 3=仅时间 4=容器部署 -->
  <limitdate>2099-12-31</limitdate>
  <!-- 授权有效期截止日期 -->
  <supporttemper>false</supporttemper>
  <!-- 是否支持临时授权（授权失败时的兜底策略） -->
</root>
```

> **注意**：`<shibie>` 支持多个备选注册码，节点名依次为 `<shibie>`、`<shibie2>`、`<shibie3>` ... `<shibie6>`，`CheckSMB()` 会逐一匹配。

#### 3.8.2 属性配置文件

`AlarmCenterProperties.xml` 中的 `HostServer` 节点：

```xml
<Properties name="HostServer">
    <DockerHostIP value="" />
    <!-- 留空或设为 * / 0.0.0.0 时自动回退到 host.docker.internal -->
    <DockerHostPort value="5000" />
    <!-- 服务端端口，需与服务端 TcpPort 一致 -->
</Properties>
```

#### 3.8.3 环境变量（K8s 场景替代方案）

当使用 Kubernetes 部署时，`DockerHostIP` 可通过环境变量 `DOCKER_HOST_IP` 注入，客户端启动脚本或代码需额外支持从环境变量读取。当前代码优先读取 `PropertyService`，若需支持环境变量需改造 `ConnectDockerHost` 中的读取逻辑。

#### 3.8.4 容器启动参数

```bash
# Docker 桌面版（Windows/Mac）—— host.docker.internal 自动可用
docker run -d your-client-image

# Linux Docker —— 需显式注入 host-gateway
docker run -d \
  --add-host host.docker.internal:host-gateway \
  your-client-image

# 或直接指定网桥 IP
docker run -d \
  -e DockerHostIP=172.17.0.1 \
  your-client-image
```

---

## 4. 通讯协议

### 4.1 正常流程

```
客户端                                    服务端
  │                                        │
  │──── StartAsync() ────────────────────►│  OnConnectedAsync
  │                                        │
  │──── GetEncryptedHardwareId ──────────►│  1. 检查 clientId 唯一性
  │       (clientId, salt)                 │  2. TryAdd 到 _activeClients
  │                                        │  3. 生成硬件注册码 → AES 加密
  │◄─── ReceiveEncryptedHardwareId ──────│  4. 返回密文
  │                                        │
  │  解密 → CheckSMB() → 授权通过           │
  │                                        │
  │═══ WebSocket 长连接 (心跳 5s/超时 10s) ═══│
```

### 4.2 防多开（客户端）

```
客户端B (同 GUID)                          服务端
  │                                        │
  │──── GetEncryptedHardwareId ──────────►│  clientId 已存在
  │                                        │  ConnectionId 不匹配
  │◄─── ClientAlreadyConnected ──────────│  拒绝连接
  │                                        │
  │  Environment.Exit(1)                   │
```

### 4.3 断线重连

```
客户端                                    服务端
  │                                        │
  │═══ 心跳超时 10s ═══════════════════════│
  │                                        ├─ OnDisconnectedAsync
  │                                        ├─ 清理 _activeClients 条目
  │  [检测到断开] Reconnecting              │
  │                                        │
  │──── 自动重连 ────────────────────────►│  OnConnectedAsync
  │                                        │
  │  Reconnected → GetEncryptedHardwareId ─►│  TryAdd 到 _activeClients
  │◄─── ReceiveEncryptedHardwareId ──────│  (恢复防多开保护)
```

### 4.4 周期性挑战-响应

```
客户端                                    服务端
  │                                        │
  │═══ 长连接维持中 ════════════════════════│
  │                                        ├─ 定时器 30s 触发
  │◄── Challenge(nonce) ──────────────────│  生成随机 nonce
  │                                        │  记录 _pendingChallenges[nonce]
  │  HMAC-SHA256(注册码, nonce)              │
  │── ChallengeResponse ─────────────────►│  验证响应
  │                                        │  ├─ 正确 → 移除 pending
  │                                        │  └─ 错误 → ForceDisconnect
  │                                        │
  │  10s 内未响应                           │
  │◄── ForceDisconnect("超时") ───────────│  移除 _activeClients 条目
  │  Environment.Exit(1)                   │
```

---

## 5. Docker 部署

### 5.1 服务端（systemd）

项目编译为自包含可执行文件（SelfContained），无需 .NET 运行时。部署时将 `publish/` 目录下的文件复制到 `/opt/dockerhost/`。

```ini
[Unit]
Description=Docker Host GUID Service
After=network.target
Wants=network.target

[Service]
Type=notify
ExecStart=/opt/dockerhost/DockerHost
WorkingDirectory=/opt/dockerhost
Restart=always
RestartSec=5
TimeoutStartSec=0

# 用户和组
User=dockerhost
Group=dockerhost

# 环境变量
Environment=DOTNET_ENVIRONMENT=Production
Environment=ASPNETCORE_ENVIRONMENT=Production

# 日志配置
StandardOutput=journal
StandardError=journal
SyslogIdentifier=dockerhost

# 安全加固
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/opt/dockerhost

[Install]
WantedBy=multi-user.target
```

**appsettings.json**：

```json
{
  "Communication": {
    "DockerBridgeIps": ["172.17.0.1", "172.18.0.1"],
    "TcpPort": 5555
  }
}
```

### 5.2 服务端（Windows Service）

部署指令（管理员 PowerShell）：

```powershell
# 创建服务
sc.exe create DockerHostAuth `
    binPath="D:\opt\dockerhost\DockerHost.exe" `
    start=auto `
    obj=LocalSystem

# 启动服务
sc.exe start DockerHostAuth

# 查看状态
sc.exe query DockerHostAuth
```

移除服务：

```powershell
sc.exe stop DockerHostAuth
sc.exe delete DockerHostAuth
```

`UseWindowsService()` 在 Windows 上提供完整的守护进程支持：自动启停、崩溃自动恢复（配合 `sc.exe failure` 可配置重启策略）。编译产物为自包含可执行文件，无需安装 .NET 运行时。

### 5.3 客户端容器

```bash
docker run -d \
  --add-host host.docker.internal:host-gateway \
  your-client-image
```

`host-gateway` 自动解析为所在容器网络的宿主机侧网关地址，支持默认 docker0 和 Compose 自定义网络。

---

## 6. Kubernetes 部署

### 6.1 网络架构

```
+-------------------------------------------------------+
| K8s Node                                                |
|                                                         |
|  +---------------------------------------------------+ |
|  | DaemonSet: dockerhost-server                       | |
|  |  hostPort: 5000 (映射到宿主机)                       | |
|  |  privileged: true (读取宿主机硬件信息)                 | |
|  |  绑定 DockerBridgeIps 中配置的网桥 IP                 | |
|  |  凭证锁 /var/tmp/gw-auth-{MD5}.lock                 | |
|  +-----------------------+-----------------------------+ |
|                          |                                |
|                          | TCP :5000 (宿主机端口)           |
|                          v                                |
|  +-----------------------+-----------------------------+ |
|  | Deployment: client-app                              | |
|  |  env: DOCKER_HOST_IP = status.hostIP (Downward API) | |
|  |  连接 $(DOCKER_HOST_IP):5000                         | |
|  +---------------------------------------------------+ |
+-------------------------------------------------------+

同一 K8s 节点内，客户端通过宿主机 IP + hostPort 直连服务端。
不同节点的客户端由 nodeAffinity 确保调度到有服务端守护的节点上。
```

### 6.2 设计原则

| 约束 | 方案 |
|---|---|
| 客户端必须运行在服务端所在节点 | DaemonSet 保证每节点一个服务端 Pod，客户端通过 `podAffinity` 或 `nodeSelector` 与之对齐 |
| 外部节点不可访问授权端口 | `hostPort` 仅绑定到宿主机指定接口，配合 NetworkPolicy 限制 |
| 企业 K8s 合规（Pod 安全策略） | 优先使用 `hostPort` 而非 `hostNetwork`，仅占用单个端口，攻击面最小 |
| 服务端读取宿主机硬件 | `privileged: true`（或更精细的 `SYS_PTRACE` + hostPath 挂载 `/etc/machine-id` 等） |
| 凭证锁防止同节点多开 | `/var/tmp` 使用 hostPath 挂载，确保锁文件在宿主机文件系统上共享 |

### 6.3 服务端 DaemonSet

```yaml
apiVersion: apps/v1
kind: DaemonSet
metadata:
  name: dockerhost-server
  namespace: gw-auth
  labels:
    app: dockerhost-server
spec:
  selector:
    matchLabels:
      app: dockerhost-server
  template:
    metadata:
      labels:
        app: dockerhost-server
    spec:
      # ---------- 调度 ----------
      # 确保每个节点仅运行一个服务端实例（DaemonSet 默认行为）
      # 如果需要仅在特定节点运行，可添加 nodeSelector 或 tolerations

      # ---------- 安全上下文 ----------
      # privileged 用于读取宿主机硬件信息（/etc/machine-id, /proc/cpuinfo, MAC 地址）
      # 若企业策略禁止 privileged，可拆分为具体 Linux Capabilities + hostPath 挂载
      hostPID: false
      containers:
      - name: server
        image: your-registry/dockerhost:latest
        imagePullPolicy: IfNotPresent

        # ---------- 端口 ----------
        # hostPort 将容器端口映射到宿主机，比 hostNetwork 限制更精准
        ports:
        - name: auth
          containerPort: 5000
          hostPort: 5000
          protocol: TCP

        # ---------- 安全上下文 ----------
        securityContext:
          privileged: true

        # ---------- 环境变量 ----------
        env:
        - name: ASPNETCORE_ENVIRONMENT
          value: "Production"
        - name: Communication__TcpPort
          value: "5000"
        # DockerBridgeIps 通过 ConfigMap 或直接写在 appsettings 中

        # ---------- 卷挂载 ----------
        volumeMounts:
        # 凭证锁文件持久化到宿主机，确保同节点不同 Pod 互斥
        - name: vartmp
          mountPath: /var/tmp
        # 硬件信息（非 privileged 模式时需要逐个挂载）
        - name: machine-id
          mountPath: /etc/machine-id
          readOnly: true
        - name: cpuinfo
          mountPath: /proc/cpuinfo
          readOnly: true

        # ---------- 健康检查 ----------
        livenessProbe:
          tcpSocket:
            port: 5000
          initialDelaySeconds: 5
          periodSeconds: 10
        readinessProbe:
          tcpSocket:
            port: 5000
          initialDelaySeconds: 2
          periodSeconds: 5

        # ---------- 资源限制 ----------
        resources:
          requests:
            cpu: 50m
            memory: 64Mi
          limits:
            cpu: 200m
            memory: 128Mi

      # ---------- 卷定义 ----------
      volumes:
      - name: vartmp
        hostPath:
          path: /var/tmp
          type: DirectoryOrCreate
      - name: machine-id
        hostPath:
          path: /etc/machine-id
          type: File
      - name: cpuinfo
        hostPath:
          path: /proc/cpuinfo
          type: File

      # ---------- 节点容忍 ----------
      # 如果需要在 master/control-plane 节点也部署，取消注释：
      # tolerations:
      # - operator: Exists
```

### 6.4 客户端 Deployment

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: client-app
  namespace: gw-auth
  labels:
    app: client-app
spec:
  replicas: 1
  selector:
    matchLabels:
      app: client-app
  template:
    metadata:
      labels:
        app: client-app
    spec:
      containers:
      - name: client
        image: your-registry/gwdatacenter:latest
        imagePullPolicy: IfNotPresent

        env:
        # ---------- 关键：注入宿主机 IP ----------
        # status.hostIP 指向 Pod 所在 K8s 节点的 IP
        # 客户端通过此 IP + hostPort 连接到同节点服务端
        - name: DOCKER_HOST_IP
          valueFrom:
            fieldRef:
              fieldPath: status.hostIP
        - name: DOCKER_HOST_PORT
          value: "5000"

        # ---------- 卷挂载 ----------
        # 客户端镜像内的授权 XML 文件可通过 ConfigMap 或 Secret 注入
        volumeMounts:
        - name: license-config
          mountPath: /app/data/AlarmCenter
          readOnly: true

        resources:
          requests:
            cpu: 100m
            memory: 256Mi
          limits:
            cpu: "2"
            memory: 2Gi

      volumes:
      - name: license-config
        configMap:
          name: client-license-config

      # ---------- 亲和性：将客户端调度到有服务端的节点 ----------
      # 方式一：podAffinity（推荐，自动跟随 DaemonSet 调度）
      affinity:
        podAffinity:
          requiredDuringSchedulingIgnoredDuringExecution:
          - labelSelector:
              matchLabels:
                app: dockerhost-server
            topologyKey: kubernetes.io/hostname

      # 方式二：nodeSelector（备选，简单但需手动维护标签）
      # nodeSelector:
      #   gw-auth: enabled
```

### 6.5 客户端 ConfigMap（授权配置文件）

需要两个配置文件注入到 `/app/data/AlarmCenter/`：

```yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: client-license-config
  namespace: gw-auth
data:
  # ① 网络连接配置
  AlarmCenterProperties.xml: |
    <?xml version="1.0" encoding="utf-8"?>
    <root>
      <Properties name="HostServer">
        <DockerHostIP value="" />
        <!-- 留空时自动回退到 host.docker.internal -->
        <!-- K8s 下若需走宿主机 IP，通过启动脚本将 DOCKER_HOST_IP 写入此值 -->
        <DockerHostPort value="5000" />
      </Properties>
    </root>

  # ② 授权许可文件（文件名与 StationItem 加载的文件名一致）
  YourLicense.xml: |
    <?xml version="1.0" encoding="utf-8"?>
    <root>
      <guid>xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx</guid>
      <shibie>XXXX-XXXX-XXXX-XXXX-XXXX</shibie>
      <mode>4</mode>
      <limitdate>2099-12-31</limitdate>
      <supporttemper>false</supporttemper>
      <softname>YourProductName</softname>
    </root>
```

> **K8s 环境下的 IP 配置**：当前客户端代码优先从 `AlarmCenterProperties.xml` 读取 `DockerHostIP`。K8s 下若需使用 `status.hostIP`，有两种方案：
> 1. 在容器启动脚本中将 `DOCKER_HOST_IP` 环境变量写入 Properties 文件
> 2. 修改 `ConnectDockerHost` 方法增加环境变量回退逻辑

### 6.6 NetworkPolicy（可选加固）

限制授权端口仅被本节点 Pod 访问：

```yaml
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: dockerhost-auth-policy
  namespace: gw-auth
spec:
  podSelector:
    matchLabels:
      app: dockerhost-server
  policyTypes:
  - Ingress
  ingress:
  - from:
    - podSelector:
        matchLabels:
          app: client-app
    ports:
    - protocol: TCP
      port: 5000
```

### 6.7 hostNetwork vs hostPort 合规对比

| | hostNetwork: true | hostPort |
|---|---|---|
| 网络命名空间 | 共享宿主机完整网络栈 | 仅映射指定端口 |
| 攻击面 | 大（可探测宿主机所有本地服务） | 小（仅暴露 :5000） |
| 端口冲突风险 | 高（Pod 端口 = 宿主机端口） | 中（仅映射的端口占用） |
| PSP/OPA 拦截概率 | 高（通常被禁止） | 低（多数策略允许） |
| 适用场景 | 需要完全访问宿主机网络时 | **推荐首选** |

> **推荐**：优先使用 `hostPort`。只有在 Pod 安全策略明确禁止 hostPort 且无法申请例外时，才考虑 `hostNetwork` 并配合 NetworkPolicy 加固。

### 6.8 凭证锁在 K8s 下的行为

K8s 环境下 `/var/tmp` 通过 hostPath 挂载到宿主机，凭证锁文件存储在宿主机文件系统上：

- **同节点第二个服务端 Pod**：DaemonSet 保证每节点仅一个 Pod，正常情况下不会发生冲突。但如果手动创建了第二个 Pod，锁文件在宿主机 `/var/tmp` 下，两个 Pod 通过 hostPath 共享同一文件系统，第二个 Pod 触发 `IOException` 退出。
- **Pod 重启**：旧 Pod 终止 → 内核回收文件句柄 → 锁释放 → 新 Pod 正常获取锁。
- **节点漂移**：Pod 被调度到新节点 → 不同节点的 `/var/tmp` 是隔离的 → 锁文件不存在 → 正常获取锁。

---

## 7. 授权失败处理

### 7.1 客户端侧

| 场景 | 行为 | 退出方式 |
|---|---|---|
| 5 秒内未收到加密授权码 | 超时，`strSMB4Docker = "超时未获取"`，进入临时授权模式 | 不退出，`bTemper4QX=true` |
| AES 解密失败 | 记录错误日志，`TrySetResult(false)` | 不退出，`bTemper4QX=true` |
| `CheckSMB()` 验证不通过 | 转为临时授权模式（`bTemper4QX=true`），运行一段时间后自动退出 | 定时器触发退出 |
| 同 GUID 重复连接 | `StopAsync()` → `FlushTelemetryBuffer(1)` → 退出 | `Environment.Exit(1)` |
| 连接永久关闭（含异常） | `ResetConnectionPool(error.Message)` → 销毁 `strSMB4Docker` → 退出 | `Environment.Exit(1)` |
| 连接永久关闭（正常） | `CollectGcStats(DateTime.UtcNow.Ticks)` → 退出 | `Environment.Exit(1)` |
| 挑战响应超时（10s） | 服务端发送 `ForceDisconnect`，客户端 `FlushTelemetryBuffer(0xE001)` | `Environment.Exit(1)` |
| 挑战响应验证失败 | 服务端发送 `ForceDisconnect`，客户端 `FlushTelemetryBuffer(0xE001)` | `Environment.Exit(1)` |

### 7.2 服务端侧

| 场景 | 行为 |
|---|---|
| 凭证锁冲突（同机第二实例） | 打印错误并 `Environment.Exit(1)` |
| Kestrel 绑定端口失败 | 抛出异常，进程退出 |

### 7.3 临时授权模式

当 Docker 授权验证不通过时，系统不会立即退出，而是进入"临时授权模式"（`bTemper4QX = true`）：

- `GetLicenseRunType()` 返回 `LicenseRunType.Trial`
- 系统可运行一段时间（由定时器控制），到期后自动退出
- 此机制确保网络抖动或服务端短暂不可用时不会误杀正常运行的业务

可通过授权 XML 的 `<supporttemper>` 节点控制是否启用此兜底策略（`true`=启用，`false`=禁用）。

---

## 8. 配置参考

### 服务端 `appsettings.json`

```json
{
  "Communication": {
    "DockerBridgeIps": ["172.17.0.1", "172.18.0.1"],
    "TcpPort": 5555,
    "IpAddress": "0.0.0.0"
  }
}
```

- `DockerBridgeIps`：Linux 下监听的 Docker 网桥地址数组，Windows 下忽略
- `TcpPort`：TCP 端口（Linux/Windows 共用）
- `IpAddress`：Windows 下绑定的 IP，Linux 下忽略

### 客户端 `AlarmCenterProperties.xml`

位置：`{应用程序根目录}/data/AlarmCenter/AlarmCenterProperties.xml`

```xml
<?xml version="1.0" encoding="utf-8"?>
<root>
  <Properties name="HostServer">
    <DockerHostIP value="" />
    <!-- 服务端地址：留空/*/0.0.0.0 自动回退到 host.docker.internal -->
    <DockerHostPort value="5000" />
    <!-- 服务端端口，需与服务端 TcpPort 一致 -->
  </Properties>
</root>
```

### 授权 XML 文件

位置：与 `AlarmCenterProperties.xml` 同目录，由 `StationItem.XMLDoc` 加载。文件通常以 `.xml` 为后缀，包含软件名称、GUID、注册码、授权模式等配置。

Docker 授权模式（`mode=4`）的必要节点：

```xml
<root>
  <guid>xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx</guid>
  <!-- 全局唯一标识（clientId），同时作为 AES 加密的 password -->
  <shibie>XXXX-XXXX-XXXX-XXXX-XXXX</shibie>
  <!-- 授权注册码（5段V2格式），用于 CheckSMB() 精确匹配验证 -->
  <shibie2>YYYY-YYYY-YYYY-YYYY-YYYY</shibie>
  <!-- 可选：备选注册码 2（网络迁移备用） -->
  <shibie3>ZZZZ-ZZZZ-ZZZZ-ZZZZ-ZZZZ</shibie3>
  <!-- 可选：备选注册码 3 -->
  <!-- 最多支持到 shibie6 -->
  <mode>4</mode>
  <!-- 授权模式：1=本地MAC 2=云端 3=仅时间 4=Docker容器 -->
  <limitdate>2099-12-31</limitdate>
  <!-- 授权有效期截止日期，超过此日期授权自动失效 -->
  <supporttemper>false</supporttemper>
  <!-- 是否启用临时授权兜底：true=授权失败时进入临时模式，false=直接退出 -->
  <softname>YourProductName</softname>
  <!-- 产品名称 -->
</root>
```

> **注意**：节点名必须为 `<shibie>`（不是 `<sbm>`），代码中通过 `GetElementsByTagName("shibie")` 查找。多注册码支持 `<shibie2>` 到 `<shibie6>`，通过 `GetSBMList()` 读取全部后由 `CheckSMB()` 逐一精确匹配。
