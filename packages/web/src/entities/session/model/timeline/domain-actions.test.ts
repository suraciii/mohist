import { describe, expect, it } from 'vitest'
import { detectShellDomainAction, detectToolDomainAction } from './domain-actions'

describe('session timeline domain actions', () => {
  it('maps shell and structured tool facts to the same Issue action', () => {
    const shell = detectShellDomainAction('bash -lc "mo issue comment create 42 --body ready"')
    const tool = detectToolDomainAction({
      callId: 'comment-42',
      name: 'mohist.issue.comment.create',
      input: { issueNumber: 42, body: 'ready' },
    })

    expect(shell).toEqual({
      verb: '评论了',
      object: 'Issue #42',
      reference: { kind: 'issue', label: 'Issue #42', issueNumber: 42 },
      source: 'shell',
    })
    expect(tool).toEqual({ ...shell, source: 'tool' })
  })

  it('maps the supported run verbs without exposing a raw workflow id', () => {
    const action = detectToolDomainAction({
      callId: 'approve',
      name: 'mohist_run_approve',
      input: { workflowRunId: 'wr_internal_123' },
    })

    expect(action).toEqual({
      verb: '批准了',
      object: 'Workflow',
      reference: { kind: 'workflow', label: 'Workflow', workflowRunId: 'wr_internal_123' },
      source: 'tool',
    })
  })

  it('falls back when shell syntax is not a single deterministic mo command', () => {
    expect(detectShellDomainAction('mo issue start 42 | tee output.txt')).toBeUndefined()
    expect(detectShellDomainAction('bash -c "mo issue start 42; mo issue start 43"')).toBeUndefined()
    expect(detectShellDomainAction('mo issue start 42\nmo issue start 43')).toBeUndefined()
    expect(detectShellDomainAction('mo issue comment create')).toBeUndefined()
  })
})
