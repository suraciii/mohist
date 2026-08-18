import { describe, expect, it } from 'vitest'
import { resolveSessionTarget, type ReceiveFollowupPayload } from '../src/server/runner-signalr.js'

describe('resolveSessionTarget', () => {
  it('ResolvesTargetField', () => {
    const payload: ReceiveFollowupPayload = {
      target: { kind: 'generic', projectId: 'proj-1', sessionId: 'gen-1' },
      text: 'x',
    }
    expect(resolveSessionTarget(payload)).toEqual({
      kind: 'generic',
      projectId: 'proj-1',
      sessionId: 'gen-1',
    })
  })

  it('CarriesPersistedBinding_WhenPresent', () => {
    const payload: ReceiveFollowupPayload = {
      target: {
        kind: 'generic',
        projectId: 'proj-1',
        sessionId: 'gen-1',
        binding: {
          runtime: 'opencode',
          runtimeSessionId: 'runtime-1',
          runnerId: 'runner-1',
          workDir: '/work/project',
        },
      },
      text: 'x',
    }

    expect(resolveSessionTarget(payload)).toEqual({
      kind: 'generic',
      projectId: 'proj-1',
      sessionId: 'gen-1',
      binding: {
        runtime: 'opencode',
        runtimeSessionId: 'runtime-1',
        runnerId: 'runner-1',
        workDir: '/work/project',
      },
    })
  })

  it('CarriesTheFrozenDefinition_IncludingReasoningEffort_WhenPresent', () => {
    // Issue-557 T-002: the server freezes the execution tuple — including
    // the canonical reasoning effort — onto the session definition the
    // follow-up target carries on the wire. The resolver must preserve
    // the effort verbatim beside model and variant.
    const payload: ReceiveFollowupPayload = {
      target: {
        kind: 'generic',
        projectId: 'proj-1',
        sessionId: 'gen-1',
        definition: {
          instructions: 'be terse',
          runtime: 'opencode',
          model: 'openai/gpt-5.5',
          variant: 'balanced',
          reasoningEffort: 'high',
          skills: [],
        },
        binding: {
          runtime: 'opencode',
          runtimeSessionId: 'runtime-1',
          runnerId: 'runner-1',
          workDir: '/work/project',
        },
      },
      text: 'continue',
    }

    const resolved = resolveSessionTarget(payload)
    expect(resolved).not.toBeNull()
    const generic = resolved?.kind === 'generic' ? resolved : null
    expect(generic).not.toBeNull()
    expect(generic?.definition?.model).toBe('openai/gpt-5.5')
    expect(generic?.definition?.variant).toBe('balanced')
    expect(generic?.definition?.reasoningEffort).toBe('high')
  })

  it('KeepsAnAbsentDefinitionEffortUnset_WithoutSynthesizingADefault', () => {
    const payload: ReceiveFollowupPayload = {
      target: {
        kind: 'generic',
        projectId: 'proj-1',
        sessionId: 'gen-1',
        definition: {
          instructions: 'be terse',
          runtime: 'opencode',
          model: 'openai/gpt-5.5',
          skills: [],
        },
        binding: {
          runtime: 'opencode',
          runtimeSessionId: 'runtime-1',
          runnerId: 'runner-1',
          workDir: '/work/project',
        },
      },
      text: 'continue',
    }

    const resolved = resolveSessionTarget(payload)
    const generic = resolved?.kind === 'generic' ? resolved : null
    expect(generic?.definition?.reasoningEffort).toBeUndefined()
  })

  it('ReturnsNull_WhenGenericTargetMissingSessionId', () => {
    const payload: ReceiveFollowupPayload = {
      target: { kind: 'generic', projectId: 'proj-1' },
      text: 'x',
    }
    expect(resolveSessionTarget(payload)).toBeNull()
  })

  it('ReturnsNull_WhenWorkflowTargetMissingSessionName', () => {
    const payload: ReceiveFollowupPayload = {
      target: { kind: 'workflow', projectId: 'proj-1', workflowRunId: 'wr-1' },
      text: 'x',
    }
    expect(resolveSessionTarget(payload)).toBeNull()
  })

  it('ReturnsNull_WhenNoTarget', () => {
    const payload: ReceiveFollowupPayload = { text: 'x' }
    expect(resolveSessionTarget(payload)).toBeNull()
  })

  it('ReturnsNull_OnUnknownTargetKind', () => {
    const payload: ReceiveFollowupPayload = {
      target: { kind: 'weird' as unknown as 'workflow', projectId: 'proj-1' },
      text: 'x',
    }
    expect(resolveSessionTarget(payload)).toBeNull()
  })
})
