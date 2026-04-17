# LocalSend 协议契约（本项目实现范围）

目标平台：.NET Compact Framework 3.5 / Windows Mobile 6 Professional。

由于 CF 3.5 无法做 HTTPS 服务端（`SslStream` 无 server 支持，WinCE SChannel 亦缺服务端绑定），**接收方只实现 v1 HTTP**；发送方可同时支持 v1（HTTP）与 v2（HTTPS 客户端）。

---

## 常量

| 名称 | 值 | 说明 |
|---|---|---|
| 多播组 | `224.0.0.167` | LocalSend 约定 |
| 多播端口 | `53317` | UDP |
| REST 默认端口 | `53317` | TCP |
| 协议声明（本端） | `"http"` | v1 明文 |
| 版本声明（本端） | `"2.0"`（仅当宣称 v2 能力时）/ v1 不含 `version` |
| 设备类型 | `"mobile"` | 固定 |

---

## 发现（UDP 多播）

### 我方发出的 announce

```json
{
  "alias": "WM6 Device",
  "deviceModel": "Windows Mobile 6",
  "deviceType": "mobile",
  "fingerprint": "<random string, per-session>",
  "announcement": true,
  "version": "2.0",
  "port": 53317,
  "protocol": "http",
  "download": false
}
```

- 启动时发一次 `"announcement": true`。
- 收到他人 `"announcement": true` 时，**单播**（或多播）回一条 `"announcement": false` 作为 response。
- `fingerprint` 作随机串（因为走 HTTP）；每次启动可重新生成，持久化到设置文件。

### 我方收到的 announce

- v1 对端只有 `alias` / `deviceModel` / `deviceType` / `fingerprint` / `announcement`
- v2 对端额外带 `version` / `port` / `protocol` / `download`
- 若对端 `protocol == "https"`，我方作发送者时须用 `HttpWebRequest`（放行自签证书）。

---

## REST（接收端实现 v1）

Base URL: `http://<ip>:53317`

### `GET /api/localsend/v1/info`
返回设备信息 JSON（与 announce 同字段，去掉 `announcement`）。

### `POST /api/localsend/v1/send-request`
请求体：
```json
{
  "info": { "alias": "...", "deviceModel": "...", "deviceType": "...", "fingerprint": "..." },
  "files": {
    "fileId1": { "id": "fileId1", "fileName": "...", "size": 123, "fileType": "image|video|pdf|text|other", "preview": null }
  }
}
```
响应（接收）：
```json
{ "fileId1": "token_for_fileId1", "fileId2": "token_for_fileId2" }
```
响应（拒绝）：HTTP 403 / 空 map。只允许**一个活动会话**；有会话在进行时返回 409。

### `POST /api/localsend/v1/send?fileId=X&token=Y`
请求体为**原始文件字节流**（非 multipart）。`Content-Length` 必须存在。校验 token 与 fileId 对应，写入文件，返回 200。

### `POST /api/localsend/v1/cancel`
取消当前会话，清理临时状态，返回 200。

---

## 会话状态机（接收端）

```
Idle  --(send-request accepted)-->  Active{sessionId, tokens[fileId]→token, progress}
Active --(all uploads complete)--> Idle
Active --(/cancel)--> Idle
Active --(timeout 5min no activity)--> Idle
```

活动期间其他 `send-request` 返回 409。

---

## CF 3.5 实现注意

- 无 `HttpListener` → 用 `TcpListener` 手写 HTTP/1.1。只需支持 `GET` / `POST`，`Content-Length` 主体，不需要 chunked（LocalSend 发送端有 Content-Length）。
- 无内置 JSON → 手写解析器，仅覆盖 LocalSend 使用的子集。
- 无 `async/await` → 每个 TCP 连接开一个 `ThreadPool` 工作项。
- 无 `ConcurrentDictionary` → `lock` + `Dictionary`。
- 文件写入大文件时分块读 socket，避免 `byte[]` 一次性驻留。
