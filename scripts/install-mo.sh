#!/bin/bash
set -e

# Mohist CLI 本地安装脚本
# 将 mo 命令作为单文件可执行程序安装到 ~/.local/bin

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
INSTALL_DIR="${HOME}/.local/bin"
MO_BIN="${INSTALL_DIR}/mo"

echo "Installing Mohist CLI (mo)..."
echo "Repository: $REPO_ROOT"
echo "Install directory: $INSTALL_DIR"

# 确保安装目录存在
mkdir -p "$INSTALL_DIR"

# 检查 dotnet 是否可用
if ! command -v dotnet &> /dev/null; then
    echo "Error: dotnet is not installed or not in PATH"
    exit 1
fi

# 发布 CLI 为单文件可执行程序
echo "Publishing Mohist CLI as single-file executable..."
cd "$REPO_ROOT"
dotnet publish packages/server/src/Mohist.Cli/Mohist.Cli.csproj \
    -c Release \
    -r linux-x64 \
    -o "$REPO_ROOT/.publish/mo" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true

# 安装可执行文件
PUBLISH_DIR="$REPO_ROOT/.publish/mo"
if [ -f "$PUBLISH_DIR/Mohist.Cli" ]; then
    # 备份旧版本
    [ -f "$MO_BIN" ] && mv "$MO_BIN" "${MO_BIN}.bak.$(date +%s)"

    # 复制可执行文件
    cp "$PUBLISH_DIR/Mohist.Cli" "$MO_BIN"
    chmod +x "$MO_BIN"

    echo "Successfully installed mo to $MO_BIN"
    echo ""
    echo "Verifying installation..."
    "$MO_BIN" --help
else
    echo "Error: Published executable not found at $PUBLISH_DIR/Mohist.Cli"
    exit 1
fi
