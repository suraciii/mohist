# Self-host 部署

Mohist 是 self-hosted 产品。这篇覆盖长跑部署、开机自启、远程访问、备份。

## 部署形态选型

| 形态 | 适合 | 优缺点 |
|---|---|---|
| **开发模式**（dev:server / dev:runner / dev:web） | 本地开发、试用 | 简单，但要 3 个终端，关机就停 |
| **本地 daemon** | 笔记本日常使用 | 单进程，关机就停 |
| **家用 server / NAS** | always-on 场景 | 真正的 fire-and-forget |
| **远程 VPS** | 出差时也能访问 | 网络配置复杂 |

下面分场景给步骤。

## 场景 1：本地 daemon（你的笔记本）

适合日常使用。开机自启，不用每次手动启动。

### Linux（systemd user service）

```bash
# 创建 user systemd 目录
mkdir -p ~/.config/systemd/user/

# 安装 Mohist
mo install

# 安装为 systemd user service
mo install service --type systemd-user

# 启动 + 开机自启
systemctl --user start mohist-server
systemctl --user start mohist-runner
systemctl --user enable mohist-server mohist-runner

# 让 user service 在你没登录时也跑
loginctl enable-linger $USER
```

常用命令：

```bash
systemctl --user status mohist-server
systemctl --user status mohist-runner
systemctl --user restart mohist-server
journalctl --user -u mohist-server -f    # 实时日志
```

### macOS（launchd）

```bash
mo install service --type launchd
```

会在 `~/Library/LaunchAgents/` 下生成 plist，登录后自动启动。

### Windows（Scheduled Task）

```powershell
mo install service --type scheduled-task
```

会在 Task Scheduler 里创建登录时启动的任务。

## 场景 2：家用 server / NAS（always-on）

适合：你有一台 always-on 的小机器（Intel NUC、家用 NAS、迷你主机、旧笔记本）。把 Mohist 放上面，真正做到 fire-and-forget。

### 步骤

1. **装系统依赖**：.NET 10 SDK、Node 18、opencode（按官方文档）
2. **clone Mohist 仓库**：`git clone <mohist> /opt/mohist && cd /opt/mohist && npm install && npm run build`
3. **创建专用用户**（推荐）：`sudo useradd -m -s /bin/bash mohist`
4. **配 systemd system service**：

```bash
sudo mo install service --type systemd --user mohist
sudo systemctl enable --now mohist-server
sudo systemctl enable --now mohist-runner
```

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

## 场景 3：远程访问

你想从外网（出差、咖啡馆、手机）访问家里的 Mohist。

### 方案 A：反向代理 + HTTPS（推荐）

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
mo config set server.tls.cert /path/to/cert.pem
mo config set server.tls.key /path/to/key.pem
```

### 方案 B：Tailscale / WireGuard VPN（推荐 - 简单）

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

### 方案 C：Cloudflare Tunnel（穿透 NAT）

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

## 数据备份

Mohist 的数据分两类：

### 1. 必须备份

- **Mohist database**（SQLite）：包含所有 issue、epic、workflow state、events
  - 位置看 `mo config list` 的 storage 配置
  - 默认 `~/.mohist/data/` 或类似

- **你的项目仓库**：含所有 issue 产物（`openspec/changes/`）
  - 因为已经 commit 到 git，远程仓库就是备份

### 2. 可丢弃（可重建）

- **Worktrees**：`<repo>/.mohist/worktrees/`
- **临时日志**：`~/.mohist/logs/`

### 备份策略

**最简**：每天 cron 备份 Mohist database：

```bash
# /etc/cron.daily/mohist-backup
cp ~/.mohist/data/mohist.db ~/.mohist/data/mohist.db.$(date +%Y%m%d).bak
# 保留最近 30 天
find ~/.mohist/data/ -name "mohist.db.*.bak" -mtime +30 -delete
```

**严肃**：用 restic / borg backup 增量备份到异地：

```bash
restic -r /backup/mohist backup ~/.mohist/data <repo>/openspec
```

## 升级

```bash
cd /opt/mohist
git pull
npm install
npm run build
sudo systemctl restart mohist-server mohist-runner
```

升级前**备份数据库**。Mohist 还没有自动迁移（roadmap），版本间 schema 偶尔会变。

## 监控（可选）

简单的健康检查：

```bash
# cron 每 5 分钟检查
*/5 * * * * curl -sf http://localhost:3456/api/health || systemctl restart mohist-server
```

严肃监控：把 Mohist 日志接到 Loki / ELK，把 health 接到 Prometheus / Uptime Kuma。

## 安全注意

- **不要把 Mohist 直接暴露公网**（没认证）。用 VPN 或反代 + auth。
- **Runner 有 shell 权限**。Runner 进程用户能跑任意命令（agent 让跑啥跑啥）。别用 root 跑，给个专用用户。
- **SSH key 范围限制**：Runner 用的 SSH key 最好是 read/write 你的项目仓库即可，别用你主 key。
- **项目仓库的可信度**：AI 会读你的代码，确保代码里没有敏感信息（密钥、token 等）。

## 当前限制

Roadmap（已知不足）：

- 没有内置认证（任何人能访问 Web UI 和 API）
- 没有 multi-user（单用户假设）
- 数据库 schema 升级没自动化

这些在 roadmap。当前**默认信任局域网**，远程访问必须自己加层。
