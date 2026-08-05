import assert from 'node:assert/strict'
import { mock, test } from 'node:test'

import { main, parseArgs } from './guard.js'

function captureStderr(): { calls: () => string; restore: () => void } {
  const stderrMock = mock.method(process.stderr, 'write', () => true)
  return {
    calls: () => stderrMock.mock.calls.map((c) => String(c.arguments[0])).join(''),
    restore: () => stderrMock.mock.restore(),
  }
}

test('parseArgs: focused with both arguments resolves the request', () => {
  const args = parseArgs(['focused', 'packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj', 'Mohist.Cli.Tests.Skills.SkillsContentTests'])
  assert.equal(args.mode, 'focused')
  assert.equal(args.focused?.csproj, 'packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj')
  assert.equal(args.focused?.className, 'Mohist.Cli.Tests.Skills.SkillsContentTests')
})

test('parseArgs: focused without any argument leaves the request unresolved', () => {
  const args = parseArgs(['focused'])
  assert.equal(args.mode, 'focused')
  assert.equal(args.focused, undefined)
})

test('parseArgs: focused with only the csproj leaves the request unresolved', () => {
  const args = parseArgs(['focused', 'packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj'])
  assert.equal(args.mode, 'focused')
  assert.equal(args.focused, undefined)
})

test('main: missing focused arguments print usage and return 2 without throwing', async () => {
  const stderr = captureStderr()
  try {
    const code = await main(['focused'])
    assert.equal(code, 2)
    assert.match(stderr.calls(), /usage: guard focused <csproj> <ClassName\.FQN>/)
  } finally {
    stderr.restore()
  }
})

test('main: partially provided focused arguments (csproj only) fail explicitly', async () => {
  const stderr = captureStderr()
  try {
    const code = await main(['focused', 'packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj'])
    assert.equal(code, 2)
    assert.match(stderr.calls(), /usage: guard focused <csproj> <ClassName\.FQN>/)
  } finally {
    stderr.restore()
  }
})

test('main: unreadable csproj fails explicitly with exit 2 instead of an unhandled exception', async () => {
  const stderr = captureStderr()
  try {
    const code = await main(['focused', 'no/such/project/Mohist.Cli.Tests.csproj', 'X.Y.Z'])
    assert.equal(code, 2)
    assert.match(stderr.calls(), /focused run failed/)
  } finally {
    stderr.restore()
  }
})
