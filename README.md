# LocalSend for Windows Mobile 6 / Windows desktop

[中文](#中文) · [English](#english)

---

## 中文

这是一个面向 **Windows Mobile 6 / .NET Compact Framework 3.5** 的 LocalSend WinForms 实现，同时可以在安装了 .NET Framework 3.5 的 Windows XP 到 Windows 11 桌面系统上运行。它不依赖托管 `SslStream`：桌面端直接调用 Windows Schannel，HTTP 层只依赖普通 `Stream`，因此同一份 exe 可以在两类运行时中保持单文件部署。

### 当前功能

- UDP 多播发现（`224.0.0.167:53317`），在可用 IPv4 接口上 announce，并在收到 `announce=true` 后执行 v2.2 HTTP register；启动 burst（100/500/2000ms）和无多播时的 `/24` HTTP(S) staged fallback 覆盖 AP 隔离、WM6 UDP 栈和启动时序问题；UDP 回复仍保留为兼容回退，绑定失败会自动重试，并对受限 WLAN 发送广播兼容副本
- LocalSend v1 与 v2.2 上传端点：`prepare-upload`、`upload`、`cancel`，以及旧版 `send-request`、`send`、`cancel`
- 桌面 Windows 的 Schannel TLS 1.2：自签名 RSA-2048 身份、双向证书认证、证书 SHA-256 指纹固定
- Windows Mobile 的 Positron TLS ABI 动态探测和 socket/stream 适配：缺失 DLL、架构不匹配、ABI 不兼容都会被捕获并转为可读状态，不会让进程发生系统级异常退出
- “关于”窗体报告操作系统、进程/原生 CPU、指针宽度、运行时、TLS 提供者和三态加密能力
- 中英文界面实时切换、日志页、可选文件日志、手动探测，以及可持久化的加密开关
- 接收文件流式写入下载目录，不把整个文件读入内存
- HTTP 服务端同时接受 `Content-Length` 与官方流式客户端可能使用的 `Transfer-Encoding: chunked`

### 加密能力三态

启动时会针对当前环境运行探测，并在“关于”中显示：

| 状态 | 含义 |
| --- | --- |
| 完全无法加密 | 只能宣告 HTTP；发送和接收均走明文 |
| 可能接受官方客户端的加密发送 | 可以尝试作为 HTTPS 接收端，但不允许本端向 HTTPS 对端发送 |
| 完全可以加密 | HTTPS 服务端和客户端均可用，发送时固定校验对端证书指纹 |

桌面端通过 Schannel 的系统 API 动态探测，不会把 TLS DLL 静态打包进 exe。Windows XP 上 TLS 1.2 或双向证书能力可能不足，最终状态以运行时探测结果为准。Windows Mobile 端只有在正确位置找到适配当前 ARM 架构、且暴露完整 ABI v2 的 `positron_tls.dll` 时才会进入 Positron HTTPS 分支；Positron 自有 socket 会被映射到本项目的 HTTP `Stream` 契约，失败时安全地继续使用 HTTP。

### 关于 Positron HTTP

HTTP 不迁移到 Positron。Positron 负责 CE 上的 TLS/证书和（ABI 2）socket 生命周期；LocalSend 的 HTTP 解析、路由和文件流仍由本项目负责，只在端点层把 Positron 的 `PTls_Read/Write` 映射为 `Stream`。这样桌面 Schannel 与 CE Positron 可以共用协议代码，也避免在 PC 上加载 ARM DLL。

### 安装与运行

1. 在 VS2008 中安装 Windows Mobile 6 SDK 和 .NET Compact Framework 3.5。
2. 打开 `Localsend.sln`，选择 `Debug|Any CPU` 或 `Release|Any CPU` 编译。
3. 桌面 Windows 只需运行 `Localsend.exe`。WM6 设备应先安装 .NET Compact Framework 3.5（SDK 中的 `NETCFv35.wm.armv4i.cab` 或设备对应架构的 CAB）。
4. WM6 设备若要体验加密，必须同时下载发布包中的 `Localsend.exe` 和 `positron_tls.dll`，并把两个文件放在同一目录（或把 DLL 放到 Windows 可搜索路径）；只下载 exe 时仍可使用明文 HTTP。
5. 首次运行会在应用数据目录生成配置和 TLS 身份材料。桌面端证书为 DER 文件，私钥保存在当前用户的 CAPI 密钥容器 `LocalSend-WinForms-TLS` 中。

### 2.0 RC 发布包

发布包提供一个单文件程序 `Localsend.exe` 和一个可选的 Windows Mobile TLS 组件 `positron_tls.dll`。PC 端不需要 DLL；WM6 端只有在同时部署这两个文件、且 DLL 与设备 ARM 架构及 ABI v2 匹配时才会启用 HTTPS。DLL 缺失或架构不匹配时，程序会在“关于”中显示原因并安全降级为 HTTP。

### 与官方客户端互通

本项目的协议实现以 [LocalSend Protocol v2.2](https://github.com/localsend/protocol/blob/main/README.md) 为准：HTTPS 模式使用证书 DER 的 SHA-256 指纹，v2 文件传输使用 `/api/localsend/v2/prepare-upload` 和 `/api/localsend/v2/upload`。HTTP 模式仍兼容 v1 客户端。

加密发现的第一条 HTTPS register 使用 TOFU：UDP announce 中的 fingerprint 只用于显示和去重，真正的 peer identity 取自 TLS 会话证书；后续文件请求才使用该证书指纹固定。若多播被网络设备过滤，启动约 3.5 秒后会自动扫描本机 IPv4 `/24` 并直接 POST register，不需要手动逐个输入对端地址。

如果“关于”显示“完全无法加密”，官方客户端必须关闭“加密/HTTPS”后才能与本端互传；如果显示“可能仅接受官方客户端的加密发送”，本端不会假装具备完整的发送能力，而会在发送前明确报告原因。

当“关于”显示“完全可以加密”时，菜单中的“加密”选项默认开启；关闭后本端会重启为 HTTP，并把选择保存到配置。能力不足时该选项保持禁用。

### 文件与诊断

- WM6 默认下载目录：`\\My Documents\\LocalSend\\`
- 桌面端配置和身份目录优先使用 `%APPDATA%\\LocalSend-WinForms`，不可写时回退到 exe 当前目录下的 `LocalSendData`
- “关于”中的 `Reason` 和 `Diagnostic detail` 用于区分系统 Schannel 不可用、Positron DLL 缺失/架构错误、ABI 错误和实际握手失败
- 日志中若只有 `Ignored self-announce`，应继续查看是否出现 `Discovery fallback HTTP scan started`；若没有可用 IPv4 接口，扫描会在网络接口建立后周期性重试

---

## English

LocalSend for WinForms targets **Windows Mobile 6 / .NET Compact Framework 3.5** and also runs on Windows XP through Windows 11 when .NET Framework 3.5 is available. The HTTP layer is stream-based; desktop TLS is provided directly by Windows Schannel, so the executable does not need a managed TLS adapter DLL.

### Highlights

- UDP discovery on `224.0.0.167:53317`, v2.2 HTTP registration after `announce=true`, a 100/500/2000 ms startup burst, and staged `/24` HTTP(S) fallback when multicast is filtered; bind failures retry automatically and a best-effort broadcast copy helps WLANs that filter multicast
- LocalSend v1 and v2.2 upload APIs
- Runtime-detected desktop Schannel TLS 1.2 with a self-signed RSA-2048 identity, mutual certificates, and SHA-256 certificate pinning
- Safe late-bound Positron ABI probing and socket/stream adaptation on Windows CE; missing or wrong-architecture DLLs become an explicit capability status instead of a process crash
- A scrollable About window with OS/CPU/runtime and the three-state encryption report
- English / Chinese localization, logs, manual probe, a persistent encryption toggle, and streaming file writes
- HTTP server support for both `Content-Length` and `Transfer-Encoding: chunked` uploads

### Encryption states

The About window reports one of three states: no encryption, receive-only/possibly compatible with encrypted official sends, or full encryption. Desktop Schannel is probed at runtime. XP may report unavailable when its Schannel cannot provide the TLS 1.2 + mutual-certificate combination required by LocalSend. On Windows CE, `positron_tls.dll` must match the ARM processor and expose the complete ABI v2; its native socket endpoints are adapted to the shared HTTP stream contract at runtime.

When the status is full encryption, the Menu → Encryption item is enabled and on by default. Turning it off restarts the service in HTTP mode and persists the choice; the item remains disabled for the other capability states.

HTTP is not replaced by a Positron HTTP implementation. The same HTTP parser and route handlers are used over plain `NetworkStream`, Schannel streams, and Positron ABI v2 streams when the native endpoint is available.

The first encrypted discovery register uses TOFU: the UDP fingerprint is only discovery metadata; the peer identity is learned from the TLS certificate and is pinned for subsequent file requests. If multicast is filtered, the service scans each local IPv4 `/24` after startup and registers directly, so manual probing is not required.

Build the solution with Visual Studio 2008 and the Windows Mobile 6 SDK. A WM6 device must have .NET Compact Framework 3.5 installed (for example `NETCFv35.wm.armv4i.cab`) before launching this build. The protocol reference is [LocalSend Protocol v2.2](https://github.com/localsend/protocol/blob/main/README.md).

### 2.0 RC package

The release package contains the single-file `Localsend.exe` plus the optional Windows Mobile TLS component `positron_tls.dll`. Desktop Windows needs only the exe. To use encryption on WM6, download both files and place them in the same directory (or put the DLL in another Windows DLL search path); a missing or wrong-architecture DLL is reported in About and safely falls back to HTTP.
