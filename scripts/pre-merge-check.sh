#!/bin/bash
set -uo pipefail

# =============================================================================
# scripts/pre-merge-check.sh —— 合并前本地快速 gate（broken-CI 协议的"绕过"分支）
#
# 用途：CI 强制 gate 不可用/挂起时，合并进 master 前的本地安全网。来源
# retro R3 / #311 教训：CI 挂起期间 defect 直接合入 master，漏掉的正是
# ArchTests 的 spec-file-size 门槛这道网。本脚本即 #314 的交付物，供
# broken-CI 协议（#313）的"绕过"分支调用。
#
# 快速套件（几分钟内完成）：
#   1. server 构建      dotnet build Mohist.sln -p:SkipWebBuild=true
#   2. ArchTests        含 spec-file-size 门槛
#   3. server UnitTests
#   4. mohist-slack     vitest
#
# 明确不跑全量 SpecTests：那是 #312 CI hang 的域，本脚本是 hang 时的安全网，
# 自己不能 hang。
#
# 输出：每步名称 + 耗时 + PASS/FAIL，任一步失败即整体非零退出 + 红/绿汇总。
# 幂等：新 worktree 可直接跑——缺 node_modules 时自动 npm ci，缺
# obj/project.assets.json 时自动 dotnet restore，之后一律 --no-restore。
# =============================================================================

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$REPO_ROOT"

# 颜色仅在 TTY 且未设置 NO_COLOR 时启用
if [ -t 1 ] && [ -z "${NO_COLOR:-}" ]; then
    C_RED=$'\e[31m'; C_GREEN=$'\e[32m'; C_YELLOW=$'\e[33m'; C_BOLD=$'\e[1m'; C_RESET=$'\e[0m'
else
    C_RED=''; C_GREEN=''; C_YELLOW=''; C_BOLD=''; C_RESET=''
fi

# --- 前置：工具可用性 ---
for cmd in dotnet node npm; do
    if ! command -v "$cmd" >/dev/null 2>&1; then
        echo "${C_RED}error: 缺少 $cmd，无法运行 pre-merge check${C_RESET}" >&2
        exit 1
    fi
done

if ! node -e 'const [maj, min] = process.versions.node.split(".").map(Number); process.exit(maj < 22 || (maj === 22 && min < 19) ? 1 : 0)'; then
    echo "${C_RED}error: 需要 Node >= 22.19.0（见 .nvmrc）${C_RESET}" >&2
    exit 1
fi

# --- 前置：npm ci（仅首次，幂等） ---
if [ ! -x node_modules/.bin/vitest ]; then
    echo "${C_BOLD}==> 首次运行：安装 npm 依赖（npm ci）${C_RESET}"
    npm ci --no-audit --no-fund || {
        echo "${C_RED}error: npm ci 失败${C_RESET}" >&2
        exit 1
    }
fi

# --- 前置：dotnet restore（仅首次，幂等） ---
# Mohist.sln 里任一项目缺 obj/project.assets.json 就先 restore 整个 sln，
# 之后 build/test 一律 --no-restore。
RESTORE_NEEDED=0
for proj in \
    packages/server/src/Mohist.Server \
    packages/server/src/Mohist.Workflow.Definition \
    packages/server/tests/Mohist.Server.ArchTests \
    packages/server/tests/Mohist.Server.UnitTests \
    packages/server/tests/Mohist.Server.SpecTests \
    packages/server/tests/Mohist.Workflow.Definition.Tests \
    packages/cli/Mohist.Cli \
    packages/cli/tests/Mohist.Cli.Tests; do
    if [ ! -f "$proj/obj/project.assets.json" ]; then
        RESTORE_NEEDED=1
        break
    fi
done

if [ "$RESTORE_NEEDED" = 1 ]; then
    echo "${C_BOLD}==> 首次运行：还原 NuGet 包（dotnet restore Mohist.sln）${C_RESET}"
    dotnet restore Mohist.sln || {
        echo "${C_RED}error: dotnet restore 失败${C_RESET}" >&2
        exit 1
    }
fi

# --- 步骤执行与汇总 ---
FAILURES=0
STEP_SUMMARY=''
LAST_STATUS=''

mark_step() {
    local status="$1" name="$2" elapsed="$3"
    local color
    case "$status" in
        PASS) color="$C_GREEN" ;;
        FAIL) color="$C_RED" ;;
        SKIP) color="$C_YELLOW" ;;
    esac
    local line
    printf -v line '%-4s %-46s %4s' "$status" "$name" "$elapsed"
    STEP_SUMMARY+="${color}${line}${C_RESET}"$'\n'
    LAST_STATUS="$status"
    [ "$status" = "FAIL" ] && FAILURES=$((FAILURES + 1))
}

run_step() {
    local name="$1"; shift
    local start elapsed
    start=$(date +%s)
    echo ""
    echo "${C_BOLD}==> ${name}${C_RESET}"
    if "$@"; then
        elapsed=$(( $(date +%s) - start ))
        mark_step PASS "$name" "${elapsed}s"
        echo "${C_GREEN}PASS${C_RESET}（${elapsed}s）"
    else
        elapsed=$(( $(date +%s) - start ))
        mark_step FAIL "$name" "${elapsed}s"
        echo "${C_RED}FAIL${C_RESET}（${elapsed}s）"
    fi
}

skip_step() {
    local name="$1" reason="$2"
    echo ""
    echo "==> ${name} — ${C_YELLOW}SKIP${C_RESET}（$reason）"
    mark_step SKIP "$name" "-"
}

# --- 1. server 构建 ---
run_step "1. server build" dotnet build Mohist.sln -p:SkipWebBuild=true --no-restore
BUILD_OK=0
[ "$LAST_STATUS" = "PASS" ] && BUILD_OK=1

# --- 2. ArchTests（含 spec-file-size 门槛）---
if [ "$BUILD_OK" = 1 ]; then
    run_step "2. ArchTests" dotnet test packages/server/tests/Mohist.Server.ArchTests/Mohist.Server.ArchTests.csproj -p:SkipWebBuild=true --no-restore --no-build
else
    skip_step "2. ArchTests" "server build 失败"
fi

# --- 3. server UnitTests ---
if [ "$BUILD_OK" = 1 ]; then
    run_step "3. UnitTests" dotnet test packages/server/tests/Mohist.Server.UnitTests/Mohist.Server.UnitTests.csproj -p:SkipWebBuild=true --no-restore --no-build
else
    skip_step "3. UnitTests" "server build 失败"
fi

# --- 4. mohist-slack vitest ---
run_step "4. mohist-slack vitest" npm run test:run -w packages/mohist-slack

# --- 汇总 ---
echo ""
echo "${C_BOLD}================= pre-merge check 汇总 =================${C_RESET}"
printf '%s' "$STEP_SUMMARY"
echo "${C_BOLD}=========================================================${C_RESET}"

if [ "$FAILURES" -gt 0 ]; then
    echo ""
    echo "${C_RED}FAIL：${FAILURES} 个环节失败，不允许绕过 CI 合并。${C_RESET}"
    exit 1
fi

echo ""
echo "${C_GREEN}PASS：快速套件全绿，可按 broken-CI 协议（#313）绕过 CI 合并。${C_RESET}"
exit 0
