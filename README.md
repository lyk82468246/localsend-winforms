# LocalSend for Windows Mobile 6 / Windows desktop

[中文](#中文) · [English](#english)

---

## 中文

这是一个面向 **Windows Mobile 6 / .NET Compact Framework 3.5** 的 LocalSend WinForms 实现，同时可以在安装了 .NET Framework 3.5 的 Windows XP 到 Windows 11 桌面系统上运行。它不依赖托管 `SslStream`：桌面端直接调用 Windows Schannel，HTTP 层只依赖普通 `Stream`，因此同一份 exe 可以在两类运行时中保持单文件部署。

### 当前功能

- UDP 多播发现（`224.0.0.167:53317`），在可用 IPv4 接口上 announce，并在收到 `announce=true` 后执行 v2.2 HTTP register；UDP 回复仍保留为兼容回退
- LocalSend v1 与 v2.2 上传端点：`prepare-upload`、`upload`、`cancel`，以及旧版 `send-request`、`send`、`cancel`
- 桌面 Windows 的 Schannel TLS 1.2：自签名 RSA-2048 身份、双向证书认证、证书 SHA-256 指纹固定
- Windows Mobile 的 Positron TLS ABI 动态探测和 socket/stream 适配：缺失 DLL、架构不匹配、ABI 不兼容都会被捕获并转为可读状态，不会让进程发生系统级异常退出
- “关于”窗体报告操作系统、进程/原生 CPU、指针宽度、运行时、TLS 提供者和三态加密能力
- 中英文界面实时切换、日志页、可选文件日志、手动探测
- 接收文件流式写入下载目录，不把整个文件读入内存

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
3. WM6 设备上只复制 `Localsend.exe`（以及你选择部署的 `positron_tls.dll`）；桌面 Windows 直接运行 exe。
4. 首次运行会在应用数据目录生成配置和 TLS 身份材料。桌面端证书为 DER 文件，私钥保存在当前用户的 CAPI 密钥容器 `LocalSend-WinForms-TLS` 中。

### 与官方客户端互通

本项目的协议实现以 [LocalSend Protocol v2.2](https://github.com/localsend/protocol/blob/main/README.md) 为准：HTTPS 模式使用证书 DER 的 SHA-256 指纹，v2 文件传输使用 `/api/localsend/v2/prepare-upload` 和 `/api/localsend/v2/upload`。HTTP 模式仍兼容 v1 客户端。

如果“关于”显示“完全无法加密”，官方客户端必须关闭“加密/HTTPS”后才能与本端互传；如果显示“可能仅接受官方客户端的加密发送”，本端不会假装具备完整的发送能力，而会在发送前明确报告原因。

### 文件与诊断

- WM6 默认下载目录：`\\My Documents\\LocalSend\\`
- 桌面端配置和身份目录优先使用 `%APPDATA%\\LocalSend-WinForms`，不可写时回退到 exe 当前目录下的 `LocalSendData`
- “关于”中的 `Reason` 和 `Diagnostic detail` 用于区分系统 Schannel 不可用、Positron DLL 缺失/架构错误、ABI 错误和实际握手失败

---

## English

LocalSend for WinForms targets **Windows Mobile 6 / .NET Compact Framework 3.5** and also runs on Windows XP through Windows 11 when .NET Framework 3.5 is available. The HTTP layer is stream-based; desktop TLS is provided directly by Windows Schannel, so the executable does not need a managed TLS adapter DLL.

### Highlights

- UDP discovery on `224.0.0.167:53317`, v2.2 HTTP registration after `announce=true`, and UDP fallback
- LocalSend v1 and v2.2 upload APIs
- Runtime-detected desktop Schannel TLS 1.2 with a self-signed RSA-2048 identity, mutual certificates, and SHA-256 certificate pinning
- Safe late-bound Positron ABI probing and socket/stream adaptation on Windows CE; missing or wrong-architecture DLLs become an explicit capability status instead of a process crash
- A scrollable About window with OS/CPU/runtime and the three-state encryption report
- English / Chinese localization, logs, manual probe, and streaming file writes

### Encryption states

The About window reports one of three states: no encryption, receive-only/possibly compatible with encrypted official sends, or full encryption. Desktop Schannel is probed at runtime. XP may report unavailable when its Schannel cannot provide the TLS 1.2 + mutual-certificate combination required by LocalSend. On Windows CE, `positron_tls.dll` must match the ARM processor and expose the complete ABI v2; its native socket endpoints are adapted to the shared HTTP stream contract at runtime.

HTTP is not replaced by a Positron HTTP implementation. The same HTTP parser and route handlers are used over plain `NetworkStream`, Schannel streams, and Positron ABI v2 streams when the native endpoint is available.

Build the solution with Visual Studio 2008 and the Windows Mobile 6 SDK. The protocol reference is [LocalSend Protocol v2.2](https://github.com/localsend/protocol/blob/main/README.md).
