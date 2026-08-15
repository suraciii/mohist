import assert from 'node:assert/strict'
import { test } from 'node:test'

import { resolveSpawnCommand } from './spawn-command.js'

test('Windows npm phases and Node lanes execute npm-cli through node without a shell', () => {
  const commands = [
    ['run', 'docs:check'],
    ['run', 'build'],
    ['run', 'archtest'],
    ['run', 'test:duration', '-w', 'packages/web', '--', '--reporter=json'],
  ]

  for (const args of commands) {
    assert.deepEqual(
      resolveSpawnCommand('npm', args, {
        platform: 'win32',
        nodeExecutable: 'C:\\Program Files\\nodejs\\node.exe',
        npmExecutable: 'C:\\Program Files\\nodejs\\node_modules\\npm\\bin\\npm-cli.js',
      }),
      {
        command: 'C:\\Program Files\\nodejs\\node.exe',
        args: ['C:\\Program Files\\nodejs\\node_modules\\npm\\bin\\npm-cli.js', ...args],
      },
    )
  }
})

test('spawn command resolution leaves POSIX npm and native apphosts unchanged', () => {
  assert.deepEqual(resolveSpawnCommand('npm', ['run', 'build'], { platform: 'linux' }), {
    command: 'npm',
    args: ['run', 'build'],
  })
  assert.deepEqual(resolveSpawnCommand('C:\\tests\\suite.exe', ['-class', 'Ns.Specs'], { platform: 'win32' }), {
    command: 'C:\\tests\\suite.exe',
    args: ['-class', 'Ns.Specs'],
  })
})

test('Windows npm resolution fails closed without the npm CLI identity', () => {
  assert.throws(
    () =>
      resolveSpawnCommand('npm', ['run', 'build'], {
        platform: 'win32',
        nodeExecutable: 'node.exe',
        npmExecutable: '',
      }),
    /requires npm_execpath/,
  )
})
