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
      object: '#42',
      reference: { kind: 'issue', label: 'Issue #42', issueNumber: 42 },
      source: 'shell',
    })
    expect(tool).toEqual({ ...shell, source: 'tool' })
  })

  it('maps shell and structured run actions to the same workflow reference', () => {
    const shell = detectShellDomainAction('mo run approve wr_internal_123 --author supervisor')
    const tool = detectToolDomainAction({
      callId: 'approve',
      name: 'mohist_run_approve',
      input: { workflowRunId: 'wr_internal_123' },
    })

    expect(shell).toEqual({
      verb: '批准了',
      object: 'Workflow',
      reference: { kind: 'workflow', label: 'Workflow', workflowRunId: 'wr_internal_123' },
      source: 'shell',
    })
    expect(tool).toEqual({ ...shell, source: 'tool' })
  })

  it('does not infer a workflow run id from shell flags', () => {
    const action = detectShellDomainAction('mo run approve --author wr_flag_value')

    expect(action).toMatchObject({
      verb: '批准了',
      object: 'Workflow',
      reference: undefined,
    })
  })

  it('falls back when shell syntax is not a single deterministic mo command', () => {
    expect(detectShellDomainAction('mo issue start 42 | tee output.txt')).toBeUndefined()
    expect(detectShellDomainAction('bash -c "mo issue start 42; mo issue start 43"')).toBeUndefined()
    expect(detectShellDomainAction('mo issue start 42\nmo issue start 43')).toBeUndefined()
    expect(detectShellDomainAction('mo issue comment create')).toBeUndefined()
  })
})
