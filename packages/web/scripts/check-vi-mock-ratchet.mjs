// vi.mock 棘轮：总数只准降不准升（openspec/changes/web-test-boundary-mocks）。
// 迁移一批后手动下调 scripts/vi-mock-baseline.json 并随批提交。
import { execSync } from 'node:child_process'
import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = join(dirname(fileURLToPath(import.meta.url)), '..')
const { baseline } = JSON.parse(readFileSync(join(root, 'scripts/vi-mock-baseline.json'), 'utf8'))

const out = execSync(
  String.raw`grep -rh "vi\.mock(" src tests --include="*.ts" --include="*.tsx" | wc -l`,
  { cwd: root, encoding: 'utf8' },
)
const count = Number(out.trim())

if (count > baseline) {
  console.error(
    `vi.mock 调用数 ${count} 超过棘轮基线 ${baseline}。` +
      `新测试请用边界 mock（MSW / Provider 注入 / config alias），` +
      `见 openspec/changes/web-test-boundary-mocks/plan.md。`,
  )
  process.exit(1)
}
if (count < baseline) {
  console.log(`vi.mock ${count}/${baseline}：低于基线，请把 scripts/vi-mock-baseline.json 下调到 ${count} 并随本批提交。`)
} else {
  console.log(`vi.mock ${count}/${baseline}：符合棘轮。`)
}
