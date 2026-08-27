import { mkdtempSync, mkdirSync, rmSync, symlinkSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { taskListAction } from '../src/actions/task-list.js'
import { isSafeRepositoryName } from '../src/runtime/workspace-identity.js'

function host(workDir: string): any {
  return { workDir, signal: new AbortController().signal }
}
function write(value: unknown) {
  const root = mkdtempSync(join(tmpdir(), 'task-list-'))
  mkdirSync(join(root, 'PLANS'))
  writeFileSync(join(root, 'PLANS/tasks.json'), JSON.stringify(value))
  return root
}
const template = {
  path: 'PLANS/tasks.json',
  task: { uses: 'mohist/agent', with: { name: 'mohist/builder' } },
  buildPrompt: 'Build',
}

function task(overrides: Record<string, unknown> = {}) {
  return { id: 'T', title: 'Title', goal: 'Goal', acceptance: [], refs: [], ...overrides }
}

describe('portable repository names', () => {
  it.each([
    '.',
    '..',
    'a/b',
    'a\\b',
    'CON',
    'con.txt',
    'PRN.md',
    'AUX',
    'NUL.log',
    'COM1',
    'com9.ext',
    'LPT1',
    'lpt9.txt',
    'repo.',
    'repo ',
    'repo:name',
    'repo<name',
    'repo>name',
    'repo"name',
    'repo|name',
    'repo?name',
    'repo*name',
    'repo\u001f',
    'repo\u0085',
  ])('rejects unsafe repository name %j', (name) => expect(isSafeRepositoryName(name)).toBe(false))
  it.each(['repo', 'repo.name', 'COM10', 'LPT10', 'console'])('accepts portable repository name %j', (name) =>
    expect(isSafeRepositoryName(name)).toBe(true),
  )
})

describe('mohist/task-list', () => {
  it('expands the strict schema in array order and snapshots authored fields', async () => {
    const root = write({
      tasks: [task({ id: 'T-1', title: 'One', goal: 'Do it', acceptance: ['works'], refs: ['PLANS/PLAN.md'] })],
    })
    const result: any = await taskListAction(template as any, host(root))
    expect(result.effects.addTasks[0]).toMatchObject({
      id: 'T-1',
      title: 'One',
      uses: 'mohist/agent',
      expect: null,
      with: { name: 'mohist/builder' },
    })
    expect(result.effects.addTasks[0].with.prompt).toContain('Build')
    expect(result.effects.addTasks[0].with.prompt).toContain('<task id="T-1">')
    expect(result.effects.addTasks[0].with.prompt).toContain('- works')
    expect(result.output.loaded).toBe(1)
  })

  it.each([
    [{ tasks: [task({ id: '' })] }],
    [{ tasks: [task({ expect: {} })] }],
    [{ tasks: [task(), task({ title: 'Other' })] }],
    [{ tasks: [task({ acceptance: 'x' })] }],
    [{ tasks: [task({ refs: [1] })] }],
    [{ tasks: [task({ id: 1 })] }],
    [{ tasks: [task({ title: true })] }],
    [{ tasks: [task({ goal: { text: 'x' } })] }],
  ])('rejects invalid task lists', async (value) => {
    const root = write(value)
    const result: any = await taskListAction(template as any, host(root))
    expect(result.error.code).toBe('invalid-input')
  })

  it.each(['/tmp/tasks.json', '../tasks.json', 'PLANS/../../tasks.json'])(
    'rejects paths outside the Workspace: %s',
    async (path) => {
      const root = write({ tasks: [task()] })
      const result: any = await taskListAction({ ...template, path } as any, host(root))
      expect(result.error.code).toBe('invalid-input')
    },
  )

  it('rejects a task-list path that escapes through a symlink', async () => {
    const root = write({ tasks: [task()] })
    const outside = mkdtempSync(join(tmpdir(), 'task-list-outside-'))
    writeFileSync(join(outside, 'tasks.json'), JSON.stringify({ tasks: [task()] }))
    rmSync(join(root, 'PLANS'), { recursive: true })
    symlinkSync(outside, join(root, 'PLANS'))
    const result: any = await taskListAction(template as any, host(root))
    expect(result.error.code).toBe('invalid-input')
  })

  it('keeps literal template syntax opaque and survives source mutation or deletion', async () => {
    const root = write({
      tasks: [task({ goal: 'Keep ${{ vars.secret }} literal', acceptance: ['A'], refs: ['PLANS/PLAN.md'] })],
    })
    const result: any = await taskListAction(template as any, host(root))
    const text = result.effects.addTasks[0].with.prompt
    writeFileSync(join(root, 'PLANS/tasks.json'), JSON.stringify({ tasks: [task({ goal: 'MUTATED' })] }))
    rmSync(join(root, 'PLANS/tasks.json'))
    expect(text).toContain('${{ vars.secret }}')
    expect(text).not.toContain('MUTATED')
    expect(text).toContain('- A')
  })
})
