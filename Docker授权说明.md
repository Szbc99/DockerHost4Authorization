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

```
StationItem.init()
  └─ DataCenter.EvalSB4Docker(null)
       └─ EvalSB4Docker()                         [私有方法]
            ├─ _ = ConnectDockerHost()             [fire-and-forget，建立 SignalR 连接]
            ├─ dockerSmbReceived.Task.Wait(5s)     [阻塞等待，最多 5 秒]
            └─ CheckSMB(strSMB4Docker)             [验证解密后的注册码]
                 ├─ 通过 → 正常运行
                 └─ 不通过 → 转为临时授权模式 (bTemper4QX=true)
```

### 3.2 连接配置

配置来源于 `AlarmCenterProperties.xml` 的 `<HostServer>` 节点：

| 配置项 | 默认值 | 说明 |
|---|---|---|
| `DockerHostIP` | `host.docker.internal` | 服务端地址，自动解析为所在容器的 Docker 网关 |
| `DockerHostPort` | `5000` | 服务端端口 |

### 3.3 连接方式

统一使用 TCP，URL 格式 `http://{DockerHostIP}:{DockerHostPort}/hardwarehub`。

`host.docker.internal` 通过容器启动参数 `--add-host host.docker.internal:host-gateway` 注入，`host-gateway`（Docker 20.10+）自动解析为当前容器网络的宿主机侧网关地址。

### 3.4 SignalR 事件处理

| 事件 | 行为 |
|---|---|
| `ReceiveEncryptedHardwareId` | AES 解密授权码 → 赋值 `strSMB4Docker` → `dockerSmbReceived.TrySetResult(true)` |
| `ClientAlreadyConnected` | 同 GUID 已有活跃连接 → `Environment.Exit(1)` |
| `Closed` | 连接永久关闭（重试耗尽）→ `Environment.Exit(1)` |
| `Reconnecting` | 记录警告日志 |
| `Reconnected` | 记录恢复日志，**重新调用 `GetEncryptedHardwareId` 恢复防多开保护** |

### 3.5 自动重连

```csharp
connectionBuilder.WithAutomaticReconnect();
```

SignalR 默认重连策略（0s → 2s → 10s → 30s，4 次后触发 `Closed`）。

重连成功后 `Reconnected` 回调重新调用 `GetEncryptedHardwareId`，向服务端注册新的 `ConnectionId`，确保防多开保护在重连后不丢失。

### 3.6 解密算法

`DockerDecryptString(cipherText, password, salt)` — 与服务端 `EncryptionHelper.EncryptString` 完全对称，AES-256-CBC + PBKDF2（SHA-256, 10000 迭代）。

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

```yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: client-license-config
  namespace: gw-auth
data:
  AlarmCenterProperties.xml: |
    <?xml version="1.0" encoding="utf-8"?>
    <root>
      <Properties name="HostServer">
        <DockerHostIP value="" />
        <!-- 留空时客户端自动读取 DOCKER_HOST_IP 环境变量 -->
        <DockerHostPort value="5000" />
      </Properties>
    </root>
```

> 客户端代码需支持：`DockerHostIP` 为空时读取环境变量 `DOCKER_HOST_IP`。或者直接将 ConfigMap 中的值设为 `${DOCKER_HOST_IP}` 并在启动脚本中做环境变量替换。

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

| 场景 | 行为 |
|---|---|
| 5 秒内未收到加密授权码 | 超时，`strSMB4Docker = "超时未获取"` |
| AES 解密失败 | 记录错误日志，`TrySetResult(false)` |
| `CheckSMB()` 验证不通过 | 转为临时授权模式（`bTemper4QX=true`），运行一段时间后自动退出 |
| 同 GUID 重复连接 | `Environment.Exit(1)` |
| 连接永久关闭 | `Environment.Exit(1)` |
| 挑战响应超时（10s） | 服务端发送 `ForceDisconnect`，客户端 `Environment.Exit(1)` |
| 挑战响应验证失败 | 服务端发送 `ForceDisconnect`，客户端 `Environment.Exit(1)` |
| 服务端凭证锁冲突 | 打印错误并 `Environment.Exit(1)` |

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

```xml
<Properties name="HostServer">
    <DockerHostIP value="host.docker.internal" />
    <DockerHostPort value="5555" />
</Properties>
```

### 授权 XML 文件

```xml
<root>
  <guid>xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx</guid>
  <!-- clientId，同时作为 AES 加密的 password -->
  <sbm>...</sbm>
  <!-- 授权注册码，用于 CheckSMB() 验证 -->
</root>
```
