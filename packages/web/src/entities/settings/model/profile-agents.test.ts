import { describe, expect, it } from 'vitest'
import { parseProfileAgentNames } from './profile-agents'

const BUILT_IN_SHAPE = [
  'approval:',
  '  feedback:',
  '    tasks:',
  '    - id: apply-feedback',
  '      title: Apply approval feedback',
  '      uses: mohist/agent',
  '      with:',
  '        name: mohist/builder',
  '        session: feedback-${{ stage.name }}',
  'stages:',
  '- stage: plan',
  '  tasks:',
  '  - id: workspace-prepare',
  '    title: Prepare workspace',
  '    uses: mohist/workspace-prepare',
  '    with:',
  '      expectedBranch: ${{ workspace.branch }}',
  '  - id: plan',
  '    title: Plan the change',
  '    uses: mohist/agent',
  '    with:',
  '      name: mohist/planner',
  '      session: plan',
  '- stage: build',
  '  tasks:',
  '  - id: build',
  '    uses: mohist/agent',
  '    with:',
  '      name: mohist/builder',
].join('\n')

describe('parseProfileAgentNames', () => {
  it('reads the named Agents of every mohist/agent task in definition order', () => {
    expect(parseProfileAgentNames(BUILT_IN_SHAPE)).toEqual(['mohist/builder', 'mohist/planner'])
  })

  it('ignores non-Agent actions, including their own with blocks', () => {
    const definition = [
      'stages:',
      '- stage: plan',
      '  tasks:',
      '  - id: health',
      '    uses: core/script',
      '    with:',
      '      name: not-an-agent',
      '  - id: plan',
      '    uses: mohist/agent',
      '    with:',
      '      name: mohist/planner',
    ].join('\n')
    expect(parseProfileAgentNames(definition)).toEqual(['mohist/planner'])
  })

  it('deduplicates repeated Agents while keeping the first occurrence', () => {
    const definition = [
      'uses: mohist/agent',
      'with:',
      '  name: mohist/builder',
      'uses: mohist/agent',
      'with:',
      '  name: mohist/planner',
      'uses: mohist/agent',
      'with:',
      '  name: mohist/builder',
    ].join('\n')
    expect(parseProfileAgentNames(definition)).toEqual(['mohist/builder', 'mohist/planner'])
  })

  it('reads an inline with mapping', () => {
    expect(parseProfileAgentNames('uses: mohist/agent\nwith: { name: mohist/reviewer, session: check }')).toEqual([
      'mohist/reviewer',
    ])
  })

  it('strips surrounding quotes and trailing comments', () => {
    expect(parseProfileAgentNames('uses: mohist/agent\nwith:\n  name: "mohist/reviewer" # the named Agent')).toEqual([
      'mohist/reviewer',
    ])
  })

  it('does not borrow the next task name when an Agent task has no with.name', () => {
    const definition = [
      'stages:',
      '- stage: build',
      '  tasks:',
      '  - id: build',
      '    uses: mohist/agent',
      '  - id: other',
      '    uses: mohist/agent',
      '    with:',
      '      name: mohist/builder',
    ].join('\n')
    expect(parseProfileAgentNames(definition)).toEqual(['mohist/builder'])
  })

  it('returns no names for a definition without Agent tasks or without a definition', () => {
    expect(parseProfileAgentNames('stages:\n- stage: plan\n  tasks: []')).toEqual([])
    expect(parseProfileAgentNames(null)).toEqual([])
    expect(parseProfileAgentNames(undefined)).toEqual([])
    expect(parseProfileAgentNames('')).toEqual([])
  })
})
