import { spawn } from 'node:child_process'
import { runCommand } from '../../src/system/process.js'

const signal = new AbortController().signal

it('uses real platform seams in the integration track', () => {
  void spawn(process.execPath, ['-e', ''])
  void runCommand('git', ['--version'], '/tmp', signal)
})
