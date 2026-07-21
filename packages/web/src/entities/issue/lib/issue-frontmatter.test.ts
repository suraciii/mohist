import { describe, expect, it } from 'vitest'
import {
  partitionIssueBody,
  recombineIssueBody,
} from './issue-frontmatter'

describe('partitionIssueBody', () => {
  it('returns none with empty description for empty input', () => {
    expect(partitionIssueBody('')).toEqual({ kind: 'none', description: '', rawEnvelope: '' })
    expect(partitionIssueBody(null)).toEqual({ kind: 'none', description: '', rawEnvelope: '' })
    expect(partitionIssueBody(undefined)).toEqual({ kind: 'none', description: '', rawEnvelope: '' })
  })

  it('returns none when the body does not start with a delimiter', () => {
    const body = '## Background\nNo frontmatter here'
    const result = partitionIssueBody(body)
    expect(result.kind).toBe('none')
    expect(result.description).toBe(body)
    expect(result.rawEnvelope).toBe('')
  })

  it('returns none when an interior line is a delimiter', () => {
    const body = 'text\n---\nfoo: bar\n---\nbaz'
    const result = partitionIssueBody(body)
    expect(result.kind).toBe('none')
    expect(result.description).toBe(body)
  })

  it('parses a closed envelope and returns recognized fields plus post-envelope description', () => {
    const body = [
      '---',
      'recommended_workflow: feature-flow',
      'recommended_workflow_reason: "UI changes match feature-flow"',
      'risk: high',
      '---',
      '',
      '## Background',
      'context',
    ].join('\n')

    const result = partitionIssueBody(body)
    expect(result.kind).toBe('closed')
    expect(result.recommendedWorkflow).toBe('feature-flow')
    expect(result.recommendedWorkflowReason).toBe('UI changes match feature-flow')
    expect(result.risk).toBe('high')
    expect(result.description).toBe('\n## Background\ncontext')
    expect(result.rawEnvelope).toBe([
      '---',
      'recommended_workflow: feature-flow',
      'recommended_workflow_reason: "UI changes match feature-flow"',
      'risk: high',
      '---',
      '',
    ].join('\n'))
  })

  it('parses partial envelopes without fabricating missing fields', () => {
    const body = ['---', 'risk: low', '---', '', 'body'].join('\n')
    const result = partitionIssueBody(body)
    expect(result.kind).toBe('closed')
    expect(result.recommendedWorkflow).toBeUndefined()
    expect(result.recommendedWorkflowReason).toBeUndefined()
    expect(result.risk).toBe('low')
    expect(result.description).toBe('\nbody')
  })

  it('parses a literal block scalar reason (|)', () => {
    const body = [
      '---',
      'recommended_workflow: feature-flow',
      'recommended_workflow_reason: |',
      '  Multi-line reason',
      '  continued here',
      'risk: medium',
      '---',
      'body',
    ].join('\n')

    const result = partitionIssueBody(body)
    expect(result.kind).toBe('closed')
    expect(result.recommendedWorkflowReason).toBe('Multi-line reason\ncontinued here')
  })

  it('parses a folded block scalar reason (>)', () => {
    const body = [
      '---',
      'recommended_workflow_reason: >',
      '  Folded',
      '  text',
      '---',
      'body',
    ].join('\n')

    const result = partitionIssueBody(body)
    expect(result.kind).toBe('closed')
    expect(result.recommendedWorkflowReason).toBe('Folded text')
  })

  it('keeps unknown keys without losing recognized values', () => {
    const body = ['---', 'title: hello', 'recommended_workflow: feature-flow', '---', 'body'].join('\n')
    const result = partitionIssueBody(body)
    expect(result.kind).toBe('closed')
    expect(result.recommendedWorkflow).toBe('feature-flow')
    expect(result.recommendedWorkflowReason).toBeUndefined()
    expect(result.risk).toBeUndefined()
    expect(result.description).toBe('body')
    expect(result.rawEnvelope).toBe(['---', 'title: hello', 'recommended_workflow: feature-flow', '---', ''].join('\n'))
  })

  it('returns a closed envelope with empty recognized fields when a field is malformed', () => {
    const body = ['---', 'this has no colon', 'recommended_workflow: feature-flow', '---', 'body'].join('\n')
    const result = partitionIssueBody(body)
    expect(result.kind).toBe('closed')
    expect(result.recommendedWorkflow).toBeUndefined()
    expect(result.recommendedWorkflowReason).toBeUndefined()
    expect(result.risk).toBeUndefined()
    expect(result.description).toBe('body')
    expect(result.rawEnvelope).toBe(['---', 'this has no colon', 'recommended_workflow: feature-flow', '---', ''].join('\n'))
  })

  it('returns unclosed for a leading delimiter without a closing delimiter and exposes no description', () => {
    const body = ['---', 'recommended_workflow: feature-flow', 'body without close'].join('\n')
    const result = partitionIssueBody(body)
    expect(result.kind).toBe('unclosed')
    expect(result.recommendedWorkflow).toBeUndefined()
    expect(result.recommendedWorkflowReason).toBeUndefined()
    expect(result.risk).toBeUndefined()
    expect(result.description).toBe('')
    expect(result.rawEnvelope).toBe(body)
  })

  it('returns unclosed with empty description when the body is just an opening delimiter', () => {
    expect(partitionIssueBody('---')).toEqual({ kind: 'unclosed', description: '', rawEnvelope: '---' })
  })

  it('treats a body that is only a closed envelope as closed with an empty description', () => {
    const body = ['---', 'risk: high', '---'].join('\n')
    const result = partitionIssueBody(body)
    expect(result.kind).toBe('closed')
    expect(result.risk).toBe('high')
    expect(result.description).toBe('')
    expect(result.rawEnvelope).toBe(body)
  })

  it('strips a leading BOM on the opening delimiter', () => {
    const body = ['\uFEFF---', 'risk: high', '---', 'body'].join('\n')
    const result = partitionIssueBody(body)
    expect(result.kind).toBe('closed')
    expect(result.risk).toBe('high')
    expect(result.description).toBe('body')
  })

  it('handles CRLF line endings and preserves them in the raw envelope', () => {
    const body = ['---', 'recommended_workflow: feature-flow', 'risk: medium', '---', '', 'body'].join('\r\n')
    const result = partitionIssueBody(body)
    expect(result.kind).toBe('closed')
    expect(result.recommendedWorkflow).toBe('feature-flow')
    expect(result.risk).toBe('medium')
    expect(result.rawEnvelope).toBe([
      '---',
      'recommended_workflow: feature-flow',
      'risk: medium',
      '---',
      '',
    ].join('\r\n'))
  })

  it('handles single-quoted values', () => {
    const body = ['---', "recommended_workflow: 'feature-flow'", '---', 'body'].join('\n')
    const result = partitionIssueBody(body)
    expect(result.kind).toBe('closed')
    expect(result.recommendedWorkflow).toBe('feature-flow')
  })

  it('treats empty scalar values as absent', () => {
    const body = ['---', 'recommended_workflow:', '---', 'body'].join('\n')
    const result = partitionIssueBody(body)
    expect(result.kind).toBe('closed')
    expect(result.recommendedWorkflow).toBeUndefined()
  })
})

describe('recombineIssueBody', () => {
  it('round-trips a closed envelope byte-for-byte when the description is unchanged', () => {
    const body = [
      '---',
      'recommended_workflow: feature-flow',
      'risk: medium',
      '---',
      '',
      'original description',
    ].join('\n')

    const partition = partitionIssueBody(body)
    expect(recombineIssueBody(partition, partition.description)).toBe(body)
  })

  it('replaces the description content while preserving the closed envelope and line endings', () => {
    const body = [
      '---',
      'recommended_workflow: feature-flow',
      'risk: medium',
      '---',
      '',
      'original description',
    ].join('\r\n')

    const partition = partitionIssueBody(body)
    const recombined = recombineIssueBody(partition, 'edited description')
    expect(recombined).toBe([
      '---',
      'recommended_workflow: feature-flow',
      'risk: medium',
      '---',
      'edited description',
    ].join('\r\n'))
  })

  it('preserves unknown keys and line endings in a closed envelope save', () => {
    const body = [
      '---',
      'title: hello',
      'recommended_workflow: feature-flow',
      'extra: keep me',
      '---',
      'description body',
    ].join('\n')

    const partition = partitionIssueBody(body)
    const recombined = recombineIssueBody(partition, 'edited')
    expect(recombined).toBe([
      '---',
      'title: hello',
      'recommended_workflow: feature-flow',
      'extra: keep me',
      '---',
      'edited',
    ].join('\n'))
  })

  it('inserts exactly one closing delimiter before the new description for an unclosed envelope', () => {
    const body = ['---', 'recommended_workflow: feature-flow', 'risk: medium', 'more body'].join('\n')
    const partition = partitionIssueBody(body)
    expect(partition.kind).toBe('unclosed')

    const recombined = recombineIssueBody(partition, 'hello world')
    expect(recombined).toBe([
      '---',
      'recommended_workflow: feature-flow',
      'risk: medium',
      'more body',
      '---',
      'hello world',
    ].join('\n'))
  })

  it('inserts exactly one closing delimiter for an unclosed envelope with only an opening delimiter', () => {
    const partition = partitionIssueBody('---')
    expect(partition.kind).toBe('unclosed')
    expect(recombineIssueBody(partition, 'new content')).toBe('---\n---\nnew content')
  })

  it('inserts exactly one closing delimiter for an unclosed envelope with a BOM-prefixed opening delimiter', () => {
    const partition = partitionIssueBody('\uFEFF---')
    expect(partition.kind).toBe('unclosed')
    const recombined = recombineIssueBody(partition, 'repaired')
    expect(recombined).toBe('\uFEFF---\n---\nrepaired')
  })

  it('preserves CRLF line endings when repairing an unclosed envelope', () => {
    const body = ['---', 'risk: medium', 'no close'].join('\r\n')
    const partition = partitionIssueBody(body)
    expect(partition.kind).toBe('unclosed')

    const recombined = recombineIssueBody(partition, 'repaired')
    expect(recombined).toBe(['---', 'risk: medium', 'no close', '---', 'repaired'].join('\r\n'))
  })

  it('reopens as a closed envelope after repair so the next partition returns recognized values', () => {
    const partition = partitionIssueBody('---\nrisk: medium')
    const recombined = recombineIssueBody(partition, 'next description')
    const rePartition = partitionIssueBody(recombined)
    expect(rePartition.kind).toBe('closed')
    expect(rePartition.risk).toBe('medium')
    expect(rePartition.description).toBe('next description')
  })

  it('omits a trailing description when the new description is empty', () => {
    const partition = partitionIssueBody(['---', 'risk: high', '---', 'original'].join('\n'))
    expect(recombineIssueBody(partition, '')).toBe(['---', 'risk: high', '---', ''].join('\n'))
  })

  it('recombines a none partition into the new description verbatim', () => {
    const partition = partitionIssueBody('plain description')
    expect(recombineIssueBody(partition, 'updated description')).toBe('updated description')
  })
})
