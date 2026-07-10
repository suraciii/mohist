import { runCommand, runCommand as execute } from '../src/system/process.js'
import * as systemProcess from '../src/system/process.js'
import { runCommand as fakeRunCommand } from './support/run-command.js'

const signal = new AbortController().signal

it('runs platform commands through system process', () => {
  void runCommand('git', [], '/tmp', signal)
  void execute(process.execPath, [], '/tmp', signal)
  void systemProcess.runCommand('git', [], '/tmp', signal)
  void systemProcess.runCommand(globalThis.process.execPath, [], '/tmp', signal)
  void fakeRunCommand('git', [], '/tmp', signal)
})
