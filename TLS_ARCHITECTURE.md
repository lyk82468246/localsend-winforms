# TLS 与传输架构

本文记录当前实现的边界、运行时决策和排错方法。协议字段和端点以 [LocalSend Protocol v2.2](https://github.com/localsend/protocol/blob/main/README.md) 为准；本文只描述本项目如何在桌面 Windows 和 Windows CE 上提供这些端点。

## 设计目标

1. 在 PC 上优先使用系统 Schannel，而不是增加一个 AnyCPU 托管 TLS 适配器 DLL。
2. 在 WM6 上允许 Positron TLS ABI 以迟绑定方式存在；`positron_tls.dll` 缺失、架构错误或 ABI 错误只能变成状态报告，不能导致 CLR/系统级异常退出。
3. HTTP 路由和文件流不依赖某个 TLS 实现。桌面服务端接收普通 `Stream`，而 CE 的 Positron endpoint 在握手完成后也映射成同一个 `Stream` 契约。
4. TLS 能力必须经过真实的运行时探测，不能因为 DLL 存在或操作系统名称看起来正确就宣称“完全可以加密”。

## 组件关系

```text
Form1 / AboutForm
        |
RuntimeEnvironmentInfo  --->  TlsProviderRouter  --->  ITlsProvider
        |                              |                 |
        |                              |                 +-- SchannelTlsProvider (desktop)
        |                              |                 +-- PositronTlsProvider (Windows CE)
        |
LocalSendService ---> HttpServer(Stream/endpoint) <--- SimpleHttpClient(Stream/endpoint)
        |                         |
        +-- v1/v2 handlers        +-- NetworkStream+Schannel or Positron socket stream
```

`TlsProviderRouter` 在构造时根据 `Environment.OSVersion.Platform` 选择 provider，然后调用 `Probe()`。路由器暴露的报告包含：

- `Unavailable`：不能安全地建立 LocalSend 所需的加密通道；服务以 HTTP 运行。
- `ReceiveOnly`：服务端自检可用，但双向证书/客户端路径未通过；服务可以尝试 HTTPS 接收，本端发送器拒绝向 HTTPS 对端发送。
- `Full`：所选 provider 已通过其可执行的服务端/客户端能力检查；桌面 Schannel 还会通过双向 loopback，Positron ABI v2 会确认完整 endpoint ABI、初始化和持久身份可用，实际网络握手仍在连接时验证。

`SupportsStreamTransport` 与 `SupportsHttpsTransport` 是两个维度。桌面 Schannel 包装已有的 `NetworkStream`；Positron ABI 2 使用自己的 listener/socket，再把连接映射成 `Stream`。只有其中一种 transport 实际可用时，LocalSend 才会把 HTTP 端口宣告为 HTTPS；About 会同时显示 capability 和 HTTP TLS transport 状态。

## 桌面 Schannel

### 身份

- 首次使用时生成 RSA-2048 自签名证书。
- 证书 DER 保存到 `%APPDATA%\\LocalSend-WinForms\\identity.der`；不可写时回退到 exe 当前目录的 `LocalSendData`。
- 私钥放在当前用户的 CAPI key container `LocalSend-WinForms-TLS` 中，并通过 `CERT_KEY_PROV_INFO_PROP_ID` 关联到证书。
- LocalSend 指纹是证书 DER 的 SHA-256 大写十六进制字符串（64 个字符）。HTTPS 对端的该字段会在握手之后固定校验；空指纹不会关闭 TLS，只表示没有可用的 pin。

### 握手

`SchannelTlsProvider` 通过 `secur32.dll` 的 SSPI API 建立 TLS 1.2：

- 入站使用 `AcquireCredentialsHandle` + `AcceptSecurityContext`。
- 出站使用 `AcquireCredentialsHandle` + `InitializeSecurityContext`。
- HTTP 只看到 `SchannelStream`，由它负责 `EncryptMessage` / `DecryptMessage`、分片和 `Content-Length` 之上的字节流。
- 服务端要求客户端证书；客户端在完成握手后读取对端 DER 并做指纹固定。
- 连接信息中的协议和 cipher 会显示在 About/日志中。

启动探测做一次本机 loopback：客户端和服务端都使用生成的身份并要求双向证书；失败后再做一次不要求客户端证书的服务端自检，以区分 `Full`、`ReceiveOnly` 和 `Unavailable`。探测失败只记录报告，不阻止程序以 HTTP 启动。

### XP 到 Windows 11

桌面 provider 是运行时探测而不是版本硬编码。XP、Vista、Windows 7/8.1、Windows 10/11 可能因为系统 Schannel 的协议、证书提供程序或策略不同而返回不同状态；尤其不能假定所有 XP 安装都具备 TLS 1.2 + 双向证书组合。应用固定请求 TLS 1.2，结果以 About 中的报告为准。

## Positron ABI

`PositronTlsProvider` 只在 Windows CE 分支调用 `positron_tls.dll`，所有 P/Invoke 都封装在 provider 内部。探测过程按以下顺序进行：

1. 获取 ABI 版本。
2. 调用初始化。
3. 使用 ABI 2 的 `PTls_IdentityLoadOrCreate` 加载/生成身份并读取指纹。
4. 以无副作用的空句柄调用解析 listener/connect/read/write/close 入口，确认完整 ABI v2。
5. 捕获 `DllNotFoundException`、`EntryPointNotFoundException` 和其他异常，转换为 `tls.positronDllMissing`、`tls.positronAbiInvalid` 等 reason code。

正式部署时 DLL 应放在 exe 同目录或 Windows 可搜索目录，且必须是目标 ARMv4/VC 架构。PC 进程不会加载 ARM DLL；PC 一律走 Schannel。

Positron ABI 2 的 listener/socket 生命周期由 DLL 持有。本项目通过 `ITlsEndpointProvider`/`ITlsEndpointListener` 明确表达这种差异：`PTls_ServerListen`/`PTls_ServerAccept` 用于服务端，`PTls_ConnectPeer` 用于客户端，`PTls_Read`/`PTls_Write`/`PTls_Close` 封装成 `Stream`，并按 `PTls_PeerFingerprint` 填充会话指纹。缺失 DLL、架构错误、ABI 不完整或初始化失败时不会创建 endpoint，CE 自动保持 HTTP。

| LocalSend 适配层 | Positron ABI v2 |
| --- | --- |
| `PositronTlsProvider.Listen` | `PTls_ServerListen(identity, port, REQUIRE_CLIENT_CERT, timeout)` |
| `ITlsEndpointListener.Accept` | `PTls_ServerAccept`，同时取得 IPv4 对端地址和端口 |
| `PositronTlsProvider.ConnectPeer` | `PTls_ConnectPeer(host, port, identity, expectedFingerprint, timeout)` |
| `PositronStream.Read/Write/Dispose` | `PTls_Read` / `PTls_Write` / `PTls_Close` |
| 会话身份 | `PTls_PeerFingerprint`；指纹不匹配由 native handshake 直接拒绝 |

所有输入字符串先显式 UTF-8 编码并追加 NUL；`BOOL` 返回值按四字节整数声明；句柄只由对应的 Positron close 函数释放。provider 在 listener 和连接全部释放后才调用 `PTls_IdentityClose`/`PTls_Cleanup`。

## HTTP 与官方 v2.2

本项目没有把 HTTP 迁移到 Positron。`HttpServer` 仍是一个小型 HTTP/1.1 实现，`SimpleHttpClient` 负责 PC/CF 共有的 Content-Length 请求；二者通过 `Stream` 或 endpoint 会话与 TLS 解耦。

已经对齐的关键路径：

- `POST /api/localsend/v2/register`
- `POST /api/localsend/v2/prepare-upload`
- `POST /api/localsend/v2/upload?sessionId=...&fileId=...&token=...`
- `POST /api/localsend/v2/cancel?sessionId=...`
- 旧版 v1 `/send-request`、`/send`、`/cancel` 仍保留
- 多播字段使用官方 `announce`；同时发出旧版本项目使用的 `announcement` 别名
- v2 `204` preparation response 被视为成功的“无需传输”，而不是拒绝

HTTPS peer 的所有客户端请求都使用 announce/register 中的证书指纹进行 pinning。不能完成完整 TLS 的本端不会把 HTTPS peer 改写成 HTTP，也不会绕过指纹检查来“试试看”。

当 discovery/register 请求本身使用 HTTPS 且原先没有 pin 时，`SimpleHttpClient` 会把 TLS 会话实际看到的 `PeerFingerprint` 回填到响应；LocalSend 随后按该证书指纹缓存对端，而不是信任 HTTPS JSON 中仅用于发现的随机字段。

## 明文降级与用户提示

降级只发生在 provider 探测报告为 `Unavailable`，或所需 endpoint transport 尚未接入时。此时：

- 本端 discovery 的 `protocol` 明确写为 `http`。
- 向 HTTPS peer 发送在请求发出前失败，并通过发送进度事件/日志给出原因。
- “关于”显示 provider、三态能力、transport 状态、reason code 和诊断详情。
- 不会尝试加载错误架构的 DLL，更不会让 Windows CE 的系统加载器直接终止进程。
- 正式 `Program.Main` 还有最后一道 UI 线程保护；意外启动异常会显示普通错误信息，不落入通用 CLR `0xe0434352` 崩溃对话框。

这意味着用户需要从 Positron 仓库下载或编译正确架构的 DLL，并放到可搜索目录；PC 用户则应检查系统 Schannel、证书密钥容器权限和本机安全策略。

## 构建与验证

在带 Windows Mobile 6 SDK 的 VS2008 命令环境中构建：

```text
msbuild Localsend.sln /t:Rebuild /p:Configuration=Debug /p:Platform="Any CPU"
msbuild Localsend.sln /t:Rebuild /p:Configuration=Release /p:Platform="Any CPU"
```

桌面回归至少应确认：启动不崩溃、About 能显示环境和 TLS 报告、Schannel loopback 为 `Full`（若系统支持）、HTTPS `/info` 返回 200、v2 preparation/upload 可以写出文件。若在受限沙箱中运行，`AcquireCredentialsHandle` 可能返回 `SEC_E_NO_CREDENTIALS`；这属于令牌/密钥容器权限限制，应在真实桌面进程中复验，不应据此修改 provider ABI。
