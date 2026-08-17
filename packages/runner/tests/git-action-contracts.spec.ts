import { describe, expect, it } from 'vitest'
import { createDefaultRegistry } from '../src/actions/registry.js'
import { validateActionInput, injectEngineInputs } from '../src/actions/input-validation.js'

describe('local Git Action manifests', () => {
  it('declare the explicit delivery contract', () => {
    const registry = createDefaultRegistry()
    const inputs = (name: string) => {
      const resolved = registry.resolve(name)
      if (resolved.kind !== 'definition') throw new Error(`Missing action ${name}`)
      return resolved.definition.manifest.inputs
    }

    expect(inputs('mohist/workspace-prepare')).toMatchObject({ expectedBranch: { required: true } })
    expect(inputs('mohist/rebase')).toMatchObject({ baseBranch: { required: true } })
    expect(inputs('mohist/rebase-status')).toMatchObject({ baseBranch: { required: true } })
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
    expect(inputs('mohist/openspec-tasks')).toMatchObject({ task: { required: true } })
    expect(inputs('mohist/push')).not.toHaveProperty('baseBranch')
  })

  it('sources the rebase expected branch from workspace.branch without a second declaration', () => {
    const registry = createDefaultRegistry()
    const resolved = registry.resolve('mohist/rebase')
    if (resolved.kind !== 'definition') throw new Error('Missing mohist/rebase')

    const expectedBranch = resolved.definition.manifest.inputs.expectedBranch
    expect(expectedBranch).toBeDefined()
    expect(expectedBranch?.engineSource).toBe('workspace.branch')
    expect(expectedBranch?.required).not.toBe(true)
    // baseBranch stays the rebase target; it is never the workspace identity.
    expect(resolved.definition.manifest.inputs.baseBranch?.engineSource).toBeUndefined()
    expect(resolved.definition.manifest.inputs.baseBranch?.required).toBe(true)
  })

  it('keeps the engine-sourced rebase expected branch out of the public catalog', () => {
    const registry = createDefaultRegistry()
    const rebaseEntry = registry.catalog().actions.find((action) => action.name === 'mohist/rebase')

    expect(rebaseEntry?.inputs.map((input) => input.name)).toContain('baseBranch')
    expect(rebaseEntry?.inputs.map((input) => input.name)).not.toContain('expectedBranch')
  })

  it('injects the rebase expected branch from workspace.branch during engine input resolution', () => {
    const resolved = createDefaultRegistry().resolve('mohist/rebase')
    if (resolved.kind !== 'definition') throw new Error('Missing mohist/rebase')

    const injected = injectEngineInputs(
      resolved.definition.manifest,
      { baseBranch: 'master', remote: 'origin' },
      { workspace: { path: '/ws', branch: 'mohist/run-wr-inject-1' } },
    )

    expect(injected).toMatchObject({
      baseBranch: 'master',
      remote: 'origin',
      expectedBranch: 'mohist/run-wr-inject-1',
    })
  })

  it('omits the rebase expected branch when workspace.branch is unavailable', () => {
    const resolved = createDefaultRegistry().resolve('mohist/rebase')
    if (resolved.kind !== 'definition') throw new Error('Missing mohist/rebase')

    const injected = injectEngineInputs(
      resolved.definition.manifest,
      { baseBranch: 'master' },
      { workspace: { path: '/ws', branch: null } },
    )

    // A null workspace.branch is not a usable expected branch and is never
    // substituted from baseBranch; validation treats the null as absent and
    // the action turns the absence into an actionable failure.
    expect(injected).toMatchObject({ baseBranch: 'master', expectedBranch: null })
    const validated = validateActionInput(resolved.definition.manifest, injected)
    expect(validated.kind).toBe('ok')
    if (validated.kind === 'ok') expect(validated.input).not.toHaveProperty('expectedBranch')
  })

  it.each([
    ['mohist/workspace-prepare', 'expectedBranch', {}],
    ['mohist/rebase', 'baseBranch', { remote: 'origin' }],
    ['mohist/rebase-status', 'baseBranch', { remote: 'origin' }],
    ['mohist/merge-ready', 'baseBranch', { source: 'feature', remote: 'origin' }],
    ['mohist/push', 'source', { target: 'master', remote: 'origin' }],
    ['mohist/openspec-tasks', 'task', { path: 'tasks.json' }],
  ])('rejects missing %s input before execution', (name, field, withInput) => {
    const resolved = createDefaultRegistry().resolve(name)
    if (resolved.kind !== 'definition') throw new Error(`Missing action ${name}`)

    const result = validateActionInput(resolved.definition.manifest, withInput)

    expect(result).toMatchObject({
      kind: 'failure',
      error: { code: 'invalid-input', message: expect.stringContaining(`'${field}'`) },
    })
  })

  it('keeps engine-sourced OpenSpec inputs out of the public catalog', () => {
    const registry = createDefaultRegistry()
    const entries = registry.catalog().actions
    const taskEntry = entries.find((action) => action.name === 'mohist/openspec-tasks')
    const archiveEntry = entries.find((action) => action.name === 'mohist/archive-change')

    expect(taskEntry?.inputs.map((input) => input.name)).not.toContain('buildPrompt')
    expect(archiveEntry?.inputs.map((input) => input.name)).not.toContain('archiveHint')

    const archiveAction = registry.resolve('mohist/archive-change')
    if (archiveAction.kind !== 'definition') throw new Error('Missing mohist/archive-change')
    expect(archiveAction.definition.manifest.inputs.archiveHint?.engineSource).toBe('vars.archive')
  })
})

describe('validateActionInput null handling', () => {
  it('treats an explicit null on an optional field as absent', () => {
    const resolved = createDefaultRegistry().resolve('mohist/archive-change')
    if (resolved.kind !== 'definition') throw new Error('Missing mohist/archive-change')

    const result = validateActionInput(resolved.definition.manifest, {
      changeDir: 'openspec/changes/issue-1',
      archiveHint: null,
    })

    expect(result).toMatchObject({ kind: 'ok' })
    if (result.kind !== 'ok') throw new Error('expected ok')
    // null on optional field is dropped, not carried through as null
    expect(result.input).not.toHaveProperty('archiveHint')
    expect(result.input.changeDir).toBe('openspec/changes/issue-1')
  })

  it('still rejects an explicit null on a required field', () => {
    const resolved = createDefaultRegistry().resolve('mohist/archive-change')
    if (resolved.kind !== 'definition') throw new Error('Missing mohist/archive-change')

    const result = validateActionInput(resolved.definition.manifest, {
      changeDir: null,
      archiveHint: null,
    })

    expect(result).toMatchObject({
      kind: 'failure',
      error: {
        code: 'invalid-input',
        message: expect.stringContaining("'changeDir'"),
      },
    })
  })
})
