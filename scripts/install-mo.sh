#!/bin/bash
set -euo pipefail

# Mohist CLI 本地安装脚本
# 将 mo 命令作为单文件可执行程序安装到 ~/.local/bin
# 并将 publish 输出的 packaged skill assets 同步到
# ~/.mohist/cli/skill-data，保证 mo skill view 等命令在
# 安装后不需要设置 MOHIST_SKILLS_DIR 即可工作。

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
INSTALL_DIR="${HOME}/.local/bin"
MO_BIN="${INSTALL_DIR}/mo"
PUBLISH_DIR="$REPO_ROOT/.publish/mo"
SOURCE_SKILL_DATA="$PUBLISH_DIR/skill-data"
MANAGED_PARENT="${HOME}/.mohist/cli"
MANAGED_SKILL_DATA="$MANAGED_PARENT/skill-data"

echo "Installing Mohist CLI (mo)..."
echo "Repository: $REPO_ROOT"
echo "Install directory: $INSTALL_DIR"
echo "Managed skill asset root: $MANAGED_SKILL_DATA"

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
dotnet publish packages/cli/Mohist.Cli/Mohist.Cli.csproj \
    -c Release \
    -r linux-x64 \
    -o "$PUBLISH_DIR" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true

# 安装可执行文件
if [ ! -f "$PUBLISH_DIR/Mohist.Cli" ]; then
    echo "Error: Published executable not found at $PUBLISH_DIR/Mohist.Cli"
    exit 1
fi

# 备份旧版本
[ -f "$MO_BIN" ] && mv "$MO_BIN" "${MO_BIN}.bak.$(date +%s)"

# 复制可执行文件
cp "$PUBLISH_DIR/Mohist.Cli" "$MO_BIN"
chmod +x "$MO_BIN"

echo "Successfully installed mo to $MO_BIN"

# 同步 packaged skill assets 到 managed cache
echo ""
echo "Synchronizing packaged skill assets to managed cache..."

TEMP_SKILL_DATA=""
cleanup_temp() {
    if [ -n "$TEMP_SKILL_DATA" ] && [ -d "$TEMP_SKILL_DATA" ]; then
        rm -rf "$TEMP_SKILL_DATA"
    fi
}
trap cleanup_temp EXIT

if [ ! -d "$SOURCE_SKILL_DATA" ]; then
    echo "Error: Published skill-data not found at $SOURCE_SKILL_DATA. Aborting managed asset sync." >&2
    exit 1
fi

mkdir -p "$MANAGED_PARENT"

if ! TEMP_SKILL_DATA=$(mktemp -d -p "$MANAGED_PARENT" "skill-data.tmp.XXXXXX"); then
    echo "Error: Failed to create temporary managed skill-data directory under '$MANAGED_PARENT'." >&2
    exit 1
fi

# 复制 source skill-data 到临时目录
if ! cp -R "$SOURCE_SKILL_DATA/." "$TEMP_SKILL_DATA/"; then
    echo "Error: Failed to copy source skill-data to '$TEMP_SKILL_DATA'." >&2
    exit 1
fi

# 验证临时目录中至少存在一个 skill（任意 */SKILL.md）
if ! ls -1 "$TEMP_SKILL_DATA"/*/SKILL.md >/dev/null 2>&1; then
    echo "Error: Prepared skill-data at '$TEMP_SKILL_DATA' contains no '*/SKILL.md'. Aborting managed asset sync." >&2
    exit 1
fi

# 替换现有的 managed skill-data 目录
if [ -d "$MANAGED_SKILL_DATA" ]; then
    rm -rf "$MANAGED_SKILL_DATA"
fi
mv "$TEMP_SKILL_DATA" "$MANAGED_SKILL_DATA"
TEMP_SKILL_DATA=""
trap - EXIT

echo "Synchronized managed skill assets to $MANAGED_SKILL_DATA"

echo ""
echo "Verifying installation..."
"$MO_BIN" --help
