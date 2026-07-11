import { spawn } from 'node:child_process'
import { createRequire } from 'node:module'

it('imports child process directly', async () => {
  await import('node:child_process')
  const require = createRequire(import.meta.url)
  require('child_process')
  createRequire(import.meta.url)('node:child_process')
  expect(spawn).toBeDefined()
})
