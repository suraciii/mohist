# Self-host 部署

Mohist 是 self-hosted 产品。这篇覆盖长跑部署、开机自启、远程访问、备份。

## 两种部署模式

Mohist server 支持两种长跑部署模式，选其一即可。**两种模式下 runner 都跑在宿主机上**（它要操作你的 git 仓库、调 opencode/git/gh 等 shell 工具，不属于容器）。使用 Slack Agent 接入时，可选的 `mohist-slack` 也作为宿主机受管服务运行，只需主动连接 Slack 和 Server，不需要公网入站端口。

Server 默认启用受资源预算保护的内置观测：Trace 最长保留 72 小时，观测存储预算为 1 GiB，
OTLP 接收端只监听 `localhost:4318`，默认部署不会发布 `4318`。运行 `mo otel status` 查看
`healthy`、`degraded` 或 `off` 状态。若需要回退为完全关闭观测的行为，设置
`Mohist:Otel:Enabled=false` 后重启 Server。

| 模式 | 适合 | 说明 |
|---|---|---|
| **systemd 模式** | Linux 主机（NUC / NAS / VPS / 笔记本） | 原生进程，`mo install` 自动写 unit、开机自启；runner 也可一并装成 unit。改动小、与系统最贴合。 |
| **Docker 模式** | 任何装了 Docker 的环境；想要隔离 / 易迁移 / 不想装 .NET SDK | server 跑在容器里，状态全部落在挂载卷；runner 仍在宿主机上连容器。 |

选定模式后，按对应章节操作；后续的远程访问、备份、升级章节会同时给出两种模式的做法。

| 场景 | 推荐模式 | 见 |
|---|---|---|
| 本地开发、试用 | 开发模式（`dev:server` / `dev:runner` / `dev:web`） | [快速上手](getting-started.md) |
| 笔记本日常使用 | systemd 模式（本地 daemon） | systemd 模式 → 场景 1 |
| 家用 server / NAS（always-on） | systemd 或 Docker | systemd 模式 → 场景 2；或 Docker 模式 |
| 远程 VPS（出差访问） | 任一 + 反代/VPN | 远程访问章节 |

---

# systemd 模式

通过 `mo` CLI 把 server（可选 runner 与 Slack 接入服务）装成 systemd user service。下面按场景给步骤。

## 场景 1：本地 daemon（你的笔记本）

适合日常使用。开机自启，不用每次手动启动。

### Linux（systemd user service）

前提：已按 [快速上手](getting-started.md) `npm ci && npm run build` 构建过，并装好 `mo` CLI（仓库内 `bash scripts/install-mo.sh`）。

```bash
# 安装为 systemd user service（自动写 unit、enable、启动、enable-linger）
mo install server
mo install runner
# 只有使用 Slack Agent 接入时才需要
mo install slack
```

核心有两个 unit：`mohist.service`（server）、`mohist-runner.service`（runner）；安装 Slack 接入后再增加 `mohist-slack.service`。`mo install` 已自动 `enable` + `restart` + `loginctl enable-linger`，所以**未登录或开机也会运行**。

Slack 接入会从受保护的 `~/.mohist/operator-token` 文件加载本机 operator credential，不会把文件内容复制到 service unit。若安装命令进程显式设置了 `MOHIST_OPERATOR_TOKEN`，则该值仍按显式安装配置写入 unit；否则服务会把 `MOHIST_OPERATOR_TOKEN_PATH` 指向受保护的 systemd credential 文件。也可用 `MOHIST_OPERATOR_TOKEN_PATH` 指定安装时的受保护源文件路径。

常用命令：

```bash
systemctl --user status mohist mohist-runner
systemctl --user restart mohist             # 或：mo update server（推荐）
journalctl --user -u mohist -f               # 实时日志

# Slack 接入服务
mo service status slack
mo service logs slack -f
```

> 重启受管理服务优先用 `mo update server` / `mo update runner`，不要手动 `dotnet run`：会触发 runner id 漂移，导致 workflow sticky assignment 失配。

### macOS

`mo install` 暂不支持 macOS（CLI 仅实现 Linux systemd 与 Windows 计划任务）。开发期用 `npm run dev:server` / `npm run dev:runner`，或自行编写 launchd plist。

### Windows（Scheduled Task）

```bash
mo install server
mo install runner
# 只有使用 Slack Agent 接入时才需要
mo install slack
```

会按平台自动在 Task Scheduler 里创建登录时启动的任务。

## 场景 2：家用 server / NAS（always-on）

适合：你有一台 always-on 的小机器（Intel NUC、家用 NAS、迷你主机、旧笔记本）。把 Mohist 放上面，真正做到 fire-and-forget。

### 步骤

1. **装系统依赖**：.NET 11 SDK、Node.js 22.19.0 或更高版本、opencode（按官方文档）
2. **clone Mohist 仓库**：`git clone <mohist> /opt/mohist && cd /opt/mohist && npm ci && npm run build`
3. **创建专用用户**（推荐）：`sudo useradd -m -s /bin/bash mohist`
4. **装为 systemd user service**（在专用用户下运行）：

```bash
sudo -u mohist mo install server --repo-root /opt/mohist
sudo -u mohist mo install runner --repo-root /opt/mohist
    # 只有使用 Slack Agent 接入时才需要
sudo -u mohist mo install slack --repo-root /opt/mohist
```

Mohist 目前只提供 systemd **user** service（非 system service）。专用用户 + `enable-linger`（`mo install` 会自动执行）即可实现 always-on、开机自启。

5. **从你的笔记本访问**：

```bash
# SSH 端口转发（最简单）
ssh -L 3456:localhost:3456 mohist@your-server

# 然后本地浏览器打开 http://localhost:3456
```

### 远程 git 仓库配置

Runner 要操作 git，需要：

- SSH key 配好（能 push 到你的远程仓库）
- 或 token 配好（HTTPS）

```bash
sudo -u mohist ssh-keygen -t ed25519
# 把公钥加到你 GitHub/GitLab 的 deploy key
```

### 注意事项

- **Git push 权限**：Runner 进程用户必须能 push 到 base branch
- **磁盘空间**：每个 issue 一个 worktree，会占空间。定期 `git worktree prune`
- **网络**：Server / Runner 默认监听 localhost。远程访问要绑 0.0.0.0 + 配反代（见下）

---

# Docker 模式

不想装 .NET SDK / 想要隔离和易迁移的话，用容器跑 server。web SPA 已在构建期打包进镜像，状态全部落在挂载卷。runner 仍留在宿主机上，通过 HTTP 连容器里的 server。

### 构建

仓库根有 `Dockerfile`（多阶段：Node 编译 web → .NET publish server → aspnet 运行时）：

```bash
git clone <repo> && cd mohist
docker build -t mohist-server .
```

镜像基于 .NET 11 preview（`mcr.microsoft.com/dotnet/nightly/aspnet:11.0-preview`），匹配仓库的 `global.json`。

### 跑起来

**单容器**（命名卷持久化）：

```bash
docker run -d \
  -p 3456:3456 \
  -v mohist-data:/data \
  --name mohist \
  --restart unless-stopped \
  mohist-server
```

**docker compose**（推荐，仓库已带 `docker-compose.yml`）：

```bash
docker compose up -d
docker compose logs -f
```

验证：`curl http://localhost:3456/api/health` 应返回 `{"status":"ok",...}`。

### 数据持久化

镜像内 `HOME=/data`，而 server 的所有状态都解析自 `$HOME/.mohist/`（主库 `mohist.db`、`otel.db`、`attachments/`、`artifacts/`、`system-update.json`）。所以挂一个卷到 `/data` 就托管了全部：

```bash
# 看卷实际位置
docker volume inspect mohist-data

# 备份
docker run --rm -v mohist-data:/d -v "$PWD":/backup alpine \
  tar czf /backup/mohist-data-$(date +%Y%m%d).tgz -C /d .

# 还原
docker run --rm -v mohist-data:/d -v "$PWD":/backup alpine \
  tar xzf /backup/mohist-data-YYYYMMDD.tgz -C /d
```

想直接在宿主上看到数据文件，把 compose 里的卷换成绑定挂载（`./data:/data`），并让宿主目录归 uid 1001（容器内用户）：`sudo chown -R 1001:1001 ./data`。

### 让 runner 连上

runner 留在宿主机上（systemd 模式下装法见上文「场景 2」），起的时候指向容器：

```bash
SERVER_URL=http://localhost:3456 RUNNER_ID=my-runner npm start
```

务必显式设 `RUNNER_ID`——容器化后 server 的主机名/网络变了，runner id 默认基于 hostname，漂移会让 workflow 的 sticky assignment 失配。

### Pi provider retry policy

Runner 在领取工作前校验这两个可选配置。默认策略把额度、余额、计费和 usage-limit 类
消息视为终态失败，并允许 provider 连续重试 5 次。额外的匹配模式是 JSON 格式的正则
字符串数组，追加到默认模式之后；阈值必须是正整数。

```bash
MOHIST_PROVIDER_ERROR_PATTERNS='["account suspended","provider-specific limit"]'
MOHIST_PROVIDER_RETRY_THRESHOLD=5
```

JSON 非法、正则非法或阈值非法都会让 Runner 启动失败并给出诊断。凭证仍由 Pi 自己
管理，不复制进 Mohist 配置。

---

# 远程访问（两种模式通用）

下面的方案对 systemd 模式和 Docker 模式都适用，区别只是反代上游指向哪：systemd 模式指向 `localhost:3456`，Docker 模式指向容器映射出的同一端口。

## 方案 A：反向代理 + HTTPS（推荐）

你想从外网（出差、咖啡馆、手机）访问家里的 Mohist。

用 Caddy / nginx 反代 Mohist Server，自动 HTTPS：

**Caddy**：

```caddyfile
mohist.yourdomain.com {
    reverse_proxy localhost:3456
}
```

Caddy 自动从 Let's Encrypt 拿证书。前提：你有域名 + 公网 IP / DDNS。

**自签证书**（没域名时）：

```bash
# 用 mkcert 生成本地信任的证书
mkcert -install
mkcert mohist.local your-server-ip

# 配 Mohist server 用这个证书
# 通过 server 的 TLS / 证书由 server 配置文件管理；参考 `mo server --help` 和 Mohist 服务端配置文档。
```

## 方案 B：Tailscale / WireGuard VPN（推荐 - 简单）

把家里 server 和你的设备拉进同一个 VPN：

```bash
# 家里 server
curl -fsSL https://tailscale.com/install.sh | sh
sudo tailscale up

# 你的笔记本 / 手机
# 装 Tailscale 客户端，登录同账号

# 访问
http://your-server-tailscale-name:3456
```

不需要域名、不需要证书、不需要端口转发。这是 self-host 远程访问的甜蜜点。

## 方案 C：Cloudflare Tunnel（穿透 NAT）

家里 NAT 后面、没公网 IP 时：

```bash
# 装 cloudflared
cloudflared tunnel login
cloudflared tunnel create mohist
cloudflared tunnel route dns mohist mohist.yourdomain.com

# 配置 tunnel 指向 localhost:3456
cloudflared tunnel run mohist
```

通过 Cloudflare 的边缘网络访问，免维护证书。

## Slack 中的备用 Web 链接

Slack Agent 接入不依赖远程 Web；回复必须在 Slack 中自包含。只有完成上述任一种远程访问
方案后，才在 Server 设置中填写 Slack 成员实际可访问的 **External Web URL**。Mohist 随后
可以在回复中显示 **Open in Mohist**，把 Web 作为查看完整执行证据和人工接管的备用平面。

未配置时只显示稳定的 Job / Session 标识。`localhost`、`127.0.0.1` 和只对 Server 宿主机
有效的地址不能配置为 External Web URL，也不能出现在 Slack 消息中。

## 数据备份

Mohist 的数据分两类：

### 1. 必须备份

- **Mohist database**（SQLite）：包含所有 issue、epic、workflow state、events
  - systemd 模式：位置由服务端配置文件指定，默认 `~/.mohist/mohist.db`
  - Docker 模式：卷 `/data/.mohist/mohist.db`，备份见上文「Docker 模式 → 数据持久化」

- **你的项目仓库**：含所有 issue 产物（`openspec/changes/`）
  - 因为已经 commit 到 git，远程仓库就是备份

### 2. 可丢弃（可重建）

- **Worktrees**：`<repo>/.mohist/worktrees/`
- **临时日志**：`~/.mohist/logs/`

### 备份策略

**systemd 模式**：每天 cron 备份 Mohist database：

```bash
# /etc/cron.daily/mohist-backup
cp ~/.mohist/mohist.db ~/.mohist/mohist.db.$(date +%Y%m%d).bak
# 保留最近 30 天
find ~/.mohist/ -name "mohist.db.*.bak" -mtime +30 -delete
```

**Docker 模式**：备份命名卷，命令见上文「Docker 模式 → 数据持久化」。

Slack 接入不增加第二个备份边界。`mohist-slack` 不保存需要恢复的接入数据；接入凭据、
解密所需材料、消息接收进度、会话归属和待发消息都必须位于 Server 现有的数据根目录或容器卷中，
并作为一个整体备份和恢复。配置 Slack 接入后，不要只复制 `mohist.db`，应使用下面的完整目录
或卷备份。

**严肃**（两种模式都适用）：用 restic / borg backup 增量备份到异地。systemd 模式备份 `~/.mohist/`，Docker 模式备份命名卷或绑定挂载的宿主目录：

```bash
restic -r /backup/mohist backup ~/.mohist <repo>/openspec
```

## 升级

升级前**备份数据库**。Mohist 还没有自动迁移（roadmap），版本间 schema 偶尔会变。

**systemd 模式**：

```bash
cd /opt/mohist
git pull
npm ci
npm run build
mo update            # 重建并重启已安装的 server、runner、slack（同步更新 mo CLI）
# 或只更新其一：mo update server / mo update runner / mo update slack
```

**Docker 模式**：

```bash
# 用 registry 镜像：
docker compose pull
# 或本地重建：
docker compose build
docker compose up -d     # 重启到新镜像，数据卷保留
```

> ⚠️ Docker 模式下 runner 与可选的 `mohist-slack` 仍在宿主机上，升级 server 容器不会更新它们。分别使用 `mo update runner` 与 `mo update slack`。

## 监控（可选）

简单的健康检查（两种模式都用同一个端点 `/api/health`，区别只是失败后怎么重启）：

**systemd 模式**：

```bash
# user cron 每 5 分钟检查（user service 要加 --user）
*/5 * * * * curl -sf http://localhost:3456/api/health || systemctl --user restart mohist
```

**Docker 模式**：镜像已内置 `HEALTHCHECK`（打 `/api/health`），`docker ps` 会显示健康状态；搭配 compose 的 `restart: unless-stopped`，容器挂掉会自动拉起。

严肃监控：把 Mohist 日志接到 Loki / ELK，把 health 接到 Prometheus / Uptime Kuma。

## 安全注意

- **不要把 Mohist 直接暴露公网**（没认证）。用 VPN 或反代 + auth。
- **Runner 有 shell 权限**。Runner 进程用户能跑任意命令（agent 让跑啥跑啥）。别用 root 跑，给个专用用户。
- **SSH key 范围限制**：Runner 用的 SSH key 最好是 read/write 你的项目仓库即可，别用你主 key。
- **项目仓库的可信度**：AI 会读你的代码，确保代码里没有敏感信息（密钥、token 等）。

## 当前限制

Roadmap（已知不足）：

- 没有内置认证（任何人能访问 Web UI 和 API）——方案已定稿，见 [认证与访问](auth.md)，待实装
- 没有 multi-user（单用户假设；认证模型按单一管理员设计，见 [认证与访问](auth.md)）
- 数据库 schema 升级没自动化

当前**默认信任局域网**，远程访问必须自己加层（见上「安全注意」）。

---

对应源码：systemd 模式见 `mo install`（`packages/cli/`）、`scripts/`；Docker 模式见仓库根 `Dockerfile` / `docker-compose.yml`。
