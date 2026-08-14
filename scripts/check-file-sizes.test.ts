import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  allowedLineCount,
  countLines,
  evaluateFileSize,
  isGovernedPath,
  parseChangedFiles,
} from './check-file-sizes.js'

test('countLines counts physical lines with any newline style', () => {
  assert.equal(countLines(''), 0)
  assert.equal(countLines('one'), 1)
  assert.equal(countLines('one\ntwo'), 2)
  assert.equal(countLines('one\r\ntwo'), 2)
  assert.equal(countLines('one\rtwo'), 2)
  assert.equal(countLines('one\n'), 2)
})

test('a new file gets the absolute limit, an over-limit base freezes exactly', () => {
  assert.equal(allowedLineCount(null), 1000)
  assert.equal(allowedLineCount(999), 1000)
  assert.equal(allowedLineCount(1000), 1000)
  assert.equal(allowedLineCount(1437), 1437)
})

test('evaluateFileSize separates organic edits from growth', () => {
  assert.deepEqual(evaluateFileSize({ baseLines: null, candidateLines: 1000 }), { limit: 1000, violates: false })
  assert.deepEqual(evaluateFileSize({ baseLines: null, candidateLines: 1001 }), { limit: 1000, violates: true })
  assert.deepEqual(evaluateFileSize({ baseLines: 1200, candidateLines: 1200 }), { limit: 1200, violates: false })
  assert.deepEqual(evaluateFileSize({ baseLines: 1200, candidateLines: 1201 }), { limit: 1200, violates: true })
  assert.deepEqual(evaluateFileSize({ baseLines: 800, candidateLines: 1200 }), { limit: 1000, violates: true })
})

test('governance covers first-party source under packages/, excluding EF-generated migrations', () => {
  assert.equal(isGovernedPath('packages/server/src/Mohist.Server/Api/Routes.cs'), true)
  assert.equal(isGovernedPath('packages/web/src/pages/epics/ui/EpicListPage.test.tsx'), true)
  assert.equal(isGovernedPath('packages/runner/src/runtime/host.ts'), true)
  assert.equal(isGovernedPath('packages/cli/Mohist.Cli/MohistCliCommands.Slack.cs'), true)
  assert.equal(isGovernedPath('scripts/check-file-sizes.ts'), false)
  assert.equal(isGovernedPath('packages/web/src/pages/page.md'), false)
  assert.equal(
    isGovernedPath('packages/server/src/Mohist.Server/Infrastructure/Data/Migrations/MohistDbContextModelSnapshot.cs'),
    false,
  )
})

test('parseChangedFiles reads NUL-separated name-status with renames', () => {
  const output = [
    'M\0packages/server/src/A.cs\0',
    'A\0packages/web/src/B.tsx\0',
    'D\0packages/runner/src/C.ts\0',
    'R90\0packages/web/src/Old.ts\0packages/web/src/New.ts\0',
  ].join('')

  assert.deepEqual(parseChangedFiles(output), [
    { status: 'M', path: 'packages/server/src/A.cs' },
    { status: 'A', path: 'packages/web/src/B.tsx' },
    { status: 'D', path: 'packages/runner/src/C.ts' },
    { status: 'R', oldPath: 'packages/web/src/Old.ts', path: 'packages/web/src/New.ts' },
  ])
})
