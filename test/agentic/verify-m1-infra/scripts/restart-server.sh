#!/bin/bash
# scripts/restart-server.sh
#
# 停止当前 Mohist.Server 进程并重新启动。
# 等待 server 恢复健康后退出。
# 成功返回 0，超时返回 1。
set -euo pipefail

echo "Stopping Mohist.Server..."
kill "$(pgrep -f "Mohist.Server")" 2>/dev/null || true
sleep 2

if curl -sf http://localhost:3456/api/health > /dev/null 2>&1; then
    echo "ERROR: server still running after kill"
    exit 1
fi

echo "Starting Mohist.Server..."
cd /opt/mohist-src
dotnet run --no-build --project packages/server/src/Mohist.Server/Mohist.Server.csproj --urls http://0.0.0.0:3456 &

for i in $(seq 1 30); do
    if curl -sf http://localhost:3456/api/health > /dev/null 2>&1; then
        echo "Server restarted OK"
        exit 0
    fi
    sleep 0.5
done

echo "ERROR: server failed to restart within 15s"
exit 1
