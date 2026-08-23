# LocalSend 协议契约（本项目实现范围）

协议基线：[LocalSend Protocol v2.2](https://github.com/localsend/protocol/blob/main/README.md)。本文件记录本项目在 .NET Compact Framework 3.5 和桌面 .NET Framework 3.5 上实际启用的字段与端点。

## 传输与指纹

| 项 | 值 |
| --- | --- |
| 多播组 | `224.0.0.167` |
| 多播/REST 默认端口 | `53317` |
| 协议 | `http` 或 `https` |
| 版本 | `2.0` |
| HTTPS 指纹 | 证书 DER 的 SHA-256 大写十六进制 |
| HTTP 指纹 | 持久化随机字符串或 provider 身份指纹 |

桌面 Windows 的 `https` 由 Schannel TLS 1.2 提供，并要求双向证书；客户端在握手后固定校验对端证书指纹。Windows CE 在发现 ABI v2 的 Positron socket 端点可用时，也会使用同一套 HTTP 路由；缺少 DLL、架构不匹配或 ABI 不完整时保持 HTTP。详见 [TLS_ARCHITECTURE.md](TLS_ARCHITECTURE.md)。

## 发现

### UDP announce

启动和定时发送：

```json
{
  "alias": "LocalSend",
  "version": "2.0",
  "deviceModel": "Windows Mobile 6",
  "deviceType": "mobile",
  "fingerprint": "...",
  "port": 53317,
  "protocol": "https",
  "download": false,
  "announce": true
}
```

本项目同时附带旧版项目使用的 `announcement` 布尔别名；接收时优先使用官方 `announce`，没有该字段才读取旧别名。收到 `announce=true` 后：

1. 服务层按对端宣告的 scheme/port 发 `POST /api/localsend/v2/register`。
2. 无法完成 HTTP 注册时，仍向 UDP 来源发送 `announce=false` 回复，兼容只实现 UDP 的客户端。

### HTTP register

```text
POST /api/localsend/v2/register
Content-Type: application/json
```

请求体是本端设备信息（不包 `announcement`）：

```json
{
  "alias": "LocalSend",
  "version": "2.0",
  "deviceModel": "Windows",
  "deviceType": "desktop",
  "fingerprint": "...",
  "port": 53317,
  "protocol": "https",
  "download": false
}
```

响应是对端同形状的设备信息。v1 `/api/localsend/v1/register` 也映射到同一处理器。

## 文件上传 API

### v2.2 preparation

```text
POST /api/localsend/v2/prepare-upload
```

请求体：

```json
{
  "info": { "alias": "...", "version": "2.0", "fingerprint": "...", "port": 53317, "protocol": "https" },
  "files": {
    "file-id": {
      "id": "file-id",
      "fileName": "image.png",
      "size": 1234,
      "fileType": "image/png",
      "preview": null
    }
  }
}
```

接收端按 `IReceivePolicy` 决定全部或部分文件。成功响应：

```json
{
  "sessionId": "session-id",
  "files": { "file-id": "file-token" }
}
```

`204 No Content` 表示“无需传输”，发送端把它视为成功完成；`409` 表示已有活动会话，`403` 表示策略拒绝。

### v2.2 upload

```text
POST /api/localsend/v2/upload?sessionId=session-id&fileId=file-id&token=file-token
Content-Length: <size>
Content-Type: application/octet-stream
```

请求体是原始文件字节流，不使用 multipart。接收端校验 session/file/token，流式写入下载目录，成功返回无 body 的 200。

### v2.2 cancel

```text
POST /api/localsend/v2/cancel?sessionId=session-id
```

取消当前会话并清理 token。发送端在用户取消或上传失败时尽力调用该端点。

## v1 兼容

以下旧路径保持可用：

- `GET /api/localsend/v1/info`
- `POST /api/localsend/v1/send-request`
- `POST /api/localsend/v1/send?fileId=...&token=...`
- `POST /api/localsend/v1/cancel`
- `POST /api/localsend/v1/register`

v1 preparation 返回扁平的 `{ "fileId": "token" }`；只有在发现信息包含版本 `2.x` 时发送端才选择 v2 路径。

## 会话状态机

```text
Idle --prepare/send-request accepted--> Active{sessionId,tokens,progress}
Active --all files uploaded--> Idle
Active --cancel or 5 min idle--> Idle
Active --another preparation--> HTTP 409
```

同一时刻只允许一个接收会话；每个上传请求仍按 `Content-Length` 限长读取，避免把大文件一次性放入内存。

## Compact Framework 约束

- 不使用 `HttpListener`、`async/await` 或 `SslStream` 服务端。
- `HttpServer` 在桌面明文/Schannel 路径基于 `TcpListener`，在 Positron ABI v2 路径基于 DLL 自有 listener；每条连接都在线程池工作项中处理。
- HTTP 客户端使用 `TcpClient` + `Stream`；桌面 HTTPS 把已连接流交给 Schannel，CE HTTPS 由 Positron 自己建立 socket 后映射成同一个 `Stream` 契约。
- Positron 不替代 HTTP parser；其 ABI 2 只负责 TLS、证书和 socket 生命周期，HTTP parser、路由和文件流仍由本项目共用。
