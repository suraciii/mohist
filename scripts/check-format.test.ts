import assert from 'node:assert/strict'
import { test } from 'node:test'
import { isFormattablePath } from './check-format.js'

test('formattable paths cover the extensions biome formats', () => {
  assert.equal(isFormattablePath('packages/web/src/pages/Page.tsx'), true)
  assert.equal(isFormattablePath('packages/runner/src/runtime/host.ts'), true)
  assert.equal(isFormattablePath('scripts/check-format.ts'), true)
  assert.equal(isFormattablePath('packages/mohist-slack/src/cli.ts'), true)
  assert.equal(isFormattablePath('packages/web/src/styles/theme.css'), true)
  assert.equal(isFormattablePath('scripts/assert-web-build.mjs'), true)
  assert.equal(isFormattablePath('packages/web/src/lib/utils.js'), true)
  assert.equal(isFormattablePath('packages/web/index.html'), false)
  assert.equal(isFormattablePath('test-duration.config.jsonc'), false)
  assert.equal(isFormattablePath('packages/web/src/data.json'), false)
})
