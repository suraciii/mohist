import { describe, expect, it } from 'vitest'
import { createDefaultRegistry } from '../src/actions/registry.js'
import { validateActionInput, injectEngineInputs } from '../src/actions/input-validation.js'

describe('local Git Action manifests', () => {
  it('declares repository delivery and task-list contracts', () => {
    const registry = createDefaultRegistry()
    const inputs = (name: string) => {
      const resolved = registry.resolve(name)
      if (resolved.kind !== 'definition') throw new Error(`Missing action ${name}`)
      return resolved.definition.manifest.inputs
    }
    expect(inputs('mohist/workspace-prepare')).toMatchObject({ expectedBranch: { required: true } })
    expect(inputs('mohist/rebase')).toMatchObject({ baseBranch: { required: true } })
    expect(inputs('mohist/merge-ready')).toMatchObject({
      baseBranch: { required: true },
      source: { required: true },
      remote: { required: true },
    })
    expect(inputs('mohist/push')).toMatchObject({
      source: { required: true },
      target: { required: true },
      remote: { required: true },
    })
    expect(inputs('mohist/task-list')).toMatchObject({ path: { required: true }, task: { required: true } })
    expect(inputs('mohist/enable-github-pr-auto-merge')).toMatchObject({
      repositoryUrl: { required: true },
      prNumber: { required: true },
    })
    expect(registry.resolve('mohist/openspec-tasks').kind).toBe('unknown')
    expect(registry.resolve('mohist/archive-change').kind).toBe('unknown')
    expect(registry.resolve('mohist/merge-github-pr').kind).toBe('unknown')
  })

  it('keeps engine-sourced task-list and rebase inputs out of the public catalog', () => {
    const registry = createDefaultRegistry()
    const entries = registry.catalog().actions
    expect(entries.find((a) => a.name === 'mohist/task-list')?.inputs.map((i) => i.name)).not.toContain('buildPrompt')
    expect(entries.find((a) => a.name === 'mohist/rebase')?.inputs.map((i) => i.name)).not.toContain('expectedBranch')
  })

  it('injects the rebase expected branch from workspace.branch', () => {
    const resolved = createDefaultRegistry().resolve('mohist/rebase')
    if (resolved.kind !== 'definition') throw new Error('Missing mohist/rebase')
    expect(
      injectEngineInputs(
        resolved.definition.manifest,
        { baseBranch: 'master' },
        { workspace: { path: '/ws', branch: 'mohist/run-wr-1' } },
      ),
    ).toMatchObject({ baseBranch: 'master', expectedBranch: 'mohist/run-wr-1' })
  })

  it.each([
    ['mohist/workspace-prepare', 'expectedBranch', {}],
    ['mohist/rebase', 'baseBranch', { remote: 'origin' }],
    ['mohist/merge-ready', 'baseBranch', { source: 'feature', remote: 'origin' }],
    ['mohist/push', 'source', { target: 'master', remote: 'origin' }],
    ['mohist/task-list', 'task', { path: 'PLANS/tasks.json' }],
  ])('rejects missing %s input before execution', (name, field, withInput) => {
    const resolved = createDefaultRegistry().resolve(name)
    if (resolved.kind !== 'definition') throw new Error(`Missing action ${name}`)
    expect(validateActionInput(resolved.definition.manifest, withInput)).toMatchObject({
      kind: 'failure',
      error: { code: 'invalid-input', message: expect.stringContaining(`'${field}'`) },
    })
  })
})
