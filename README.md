# LocalSend for Windows Mobile 6

[中文](#中文) · [English](#english)

---

## 中文

在 **Windows Mobile 6 Professional**（.NET Compact Framework 3.5）上"假装"成一个 LocalSend 对端：其他 LocalSend 客户端（Flutter 版 LocalSend on Android / iOS / Windows / macOS / Linux）在局域网里能自动发现这台 WM6 设备，并与它互相收发文件。

### 功能
- UDP 多播发现（`224.0.0.167:53317`），在本机每个可用 IPv4 接口上同时 announce；DHCP/网卡变化自动收敛
- HTTP 端点：`/api/localsend/v1/{info, send-request, send, cancel, register}`，附带 `/v2/{info, register}` 兼容
- HTTP 注册（`/register`）兜底：当多播被路由器或 AP 隔离掉时，对端仍可通过 HTTP POST 把自己通告给本端
- 接收到的文件保存到 `\My Documents\LocalSend\`（可在配置文件中修改）
- 中英文界面实时切换
- 应用内日志页 + 可选的文件日志（默认关闭）
- 手动探测菜单：输入任意 IP:Port，做一次 TCP 连接 + HTTP GET /info 的连通性测试

### 安装与运行
1. 在本仓库的 [Releases](https://github.com/lyk82468246/localsend-winforms/releases) 页下载最新 `Localsend.exe`
2. 通过 ActiveSync / Windows Mobile Device Center 或存储卡，把 exe 复制到设备任意可写目录（建议 `\Program Files\Localsend\` 或 `\Storage Card\`）
3. 在设备上双击运行；首次运行会在 `\My Documents\LocalSend\config.json` 生成配置（别名、指纹、下载目录等）
4. 确保设备 Wi‑Fi 已经连入与对端设备**同一个子网的同一个 SSID**

### ⚠️ 重要：对端必须关闭"加密"才能与 WM6 互通

WM6 的 .NET CF 3.5 与 WinCE SChannel **无法提供 HTTPS 服务端**，所以本端只能以 HTTP 方式工作。主流 LocalSend 客户端默认启用 HTTPS（v2 协议），你必须在对端关闭它：

- **Android / iOS**：打开 LocalSend → 左下角"设置" → "网络" → 关闭 **"加密 (HTTPS)"** / **"Encryption"**
- **Windows / macOS / Linux 桌面版**：LocalSend → 设置 → 网络 → 取消勾选 **"加密"** / **"Encryption"**

关闭加密后，对端会以明文 HTTP 与 WM6 通信，发送/接收都能正常工作。

> 若对端只能以 HTTPS 宣称自己，本端作为**发送方**会对任意证书放行（CF 3.5 的 `ICertificatePolicy`），但 TLS 握手能否成功取决于对端最低 TLS 版本——WM6 默认 TLS 1.0 / SSL3，许多现代 LocalSend 服务端只接受 TLS 1.2+，握手会失败。因此强烈建议**直接在对端关加密**。

### 使用
- **接收**：启动 WM6 端后保持应用在前台；在对端 LocalSend 里选中这台 WM6 设备（别名形如 `WM6-xxxx`），发送文件即可。文件落到 `\My Documents\LocalSend\`
- **发送**：在 WM6 主界面选中列表里的对端 → 菜单 → 发送 → 选文件
- **对端没出现在列表里**：菜单 → 探测... → 填入对端 IP 和端口（默认 53317）→ 确定；观察日志页

### 菜单说明
| 项 | 作用 |
|---|---|
| 发送 | 选中对端后发送文件 |
| 刷新 | 手动刷新对端列表 |
| 语言 | 中英文切换 |
| 日志 | 查看最近 400 行内存日志 |
| 持久日志 | 开关：把日志写到 `\My Documents\localsend.log` |
| 探测... | 对指定 IP:Port 做连通性测试 |
| 关于 | 显示本端别名、指纹、端口 |

### 限制
- 只实现了 LocalSend v1 的 HTTP 服务端；不支持 HTTPS 服务端（WM6 技术限制）
- 单个接收会话（同一时刻只能接收一组文件，期间其他发送方收到 409）
- 大文件通过流式写盘，不受内存限制；但 WM6 存储写速有限

### 许可
MIT。协议参考 [LocalSend 官方协议](https://github.com/localsend/protocol)。

---

## English

A drop‑in LocalSend peer running on **Windows Mobile 6 Professional** (.NET Compact Framework 3.5). Other LocalSend clients (Flutter LocalSend on Android / iOS / Windows / macOS / Linux) automatically discover the WM6 device on the LAN and exchange files with it.

### Features
- UDP multicast discovery (`224.0.0.167:53317`), announcing simultaneously on every usable local IPv4 interface; DHCP / interface changes self‑converge
- HTTP endpoints: `/api/localsend/v1/{info, send-request, send, cancel, register}`, plus `/v2/{info, register}` shims
- HTTP registration (`/register`) fallback: when multicast is filtered by the router / AP isolation, peers can still POST their info over HTTP
- Received files land in `\My Documents\LocalSend\` (configurable)
- Live English / Chinese UI switch
- In‑app log viewer + optional file log (off by default)
- Manual Probe menu: enter any `IP:port` to run a TCP connect + HTTP GET /info reachability test

### Install & Run
1. Download the latest `Localsend.exe` from [Releases](https://github.com/lyk82468246/localsend-winforms/releases)
2. Copy it to the device (ActiveSync / WMDC / storage card) into any writable folder — `\Program Files\Localsend\` or `\Storage Card\` work
3. Launch it; first run creates `\My Documents\LocalSend\config.json` (alias, fingerprint, download dir)
4. Make sure the device's Wi‑Fi is on **the same SSID on the same subnet** as the peer

### ⚠️ Important: peers MUST turn off "Encryption" to talk to WM6

.NET CF 3.5 + WinCE SChannel **cannot act as an HTTPS server**, so WM6 speaks plain HTTP only. Mainstream LocalSend clients default to HTTPS (v2 protocol); you have to disable it on the peer:

- **Android / iOS**: LocalSend → Settings (bottom left) → Network → turn off **"Encryption (HTTPS)"**
- **Windows / macOS / Linux desktop**: LocalSend → Settings → Network → uncheck **"Encryption"**

With encryption off, peers fall back to plain HTTP and both send and receive work normally.

> If a peer can only announce itself as HTTPS, WM6 as a **sender** will accept any cert (CF 3.5 `ICertificatePolicy`), but whether the TLS handshake succeeds depends on the peer's minimum TLS version. WM6 defaults to TLS 1.0 / SSL3; many modern LocalSend servers require TLS 1.2+ and will refuse. Turning off encryption on the peer is by far the simplest fix.

### Usage
- **Receive**: launch WM6 app and keep it foreground; from the peer's LocalSend, select this WM6 device (alias looks like `WM6-xxxx`) and send. Files arrive in `\My Documents\LocalSend\`
- **Send**: on WM6, select a peer in the list → Menu → Send → pick a file
- **Peer not in the list**: Menu → Probe... → enter the peer's IP and port (default 53317) → OK; watch the Log page

### Menu
| Item | What it does |
|---|---|
| Send | Send a file to the selected peer |
| Refresh | Force peer list refresh |
| Language | Toggle EN / 中文 |
| Log | View the last 400 in‑memory log lines |
| Log to file | Toggle writing log to `\My Documents\localsend.log` |
| Probe... | Reachability test against a given `IP:port` |
| About | Show local alias / fingerprint / port |

### Limits
- Only LocalSend v1 HTTP server is implemented; no HTTPS server (WM6 technical limitation)
- One receive session at a time; concurrent senders get HTTP 409
- Large files are streamed to disk so memory stays flat, but WM6 storage write throughput is modest

### License
MIT. Protocol reference: [LocalSend protocol](https://github.com/localsend/protocol).
