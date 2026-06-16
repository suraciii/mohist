import { describe, it, expect } from 'vitest'
import { parseIssueFrontmatter } from '../src/features/create-issue/lib/frontmatter'

describe('parseIssueFrontmatter', () => {
  it('returns none when body has no frontmatter', () => {
    expect(parseIssueFrontmatter('## Background\nNo frontmatter here')).toEqual({ kind: 'none' })
  })

  it('returns none for empty body', () => {
    expect(parseIssueFrontmatter('')).toEqual({ kind: 'none' })
  })

  it('returns none when body does not start with a delimiter', () => {
    expect(parseIssueFrontmatter('text\n---\nrecommended_workflow: x\n---')).toEqual({ kind: 'none' })
  })

  it('parses recommended_workflow, reason, and risk', () => {
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

    expect(parseIssueFrontmatter(body)).toEqual({
      kind: 'parsed',
      recommendedWorkflow: 'feature-flow',
      recommendedWorkflowReason: 'UI changes match feature-flow',
      risk: 'high',
    })
  })

  it('parses partial frontmatter (only risk)', () => {
    const body = ['---', 'risk: low', '---', '', 'body'].join('\n')
    expect(parseIssueFrontmatter(body)).toEqual({
      kind: 'parsed',
      recommendedWorkflow: undefined,
      recommendedWorkflowReason: undefined,
      risk: 'low',
    })
  })

  it('parses block scalar reason (literal |)', () => {
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

    expect(parseIssueFrontmatter(body)).toEqual({
      kind: 'parsed',
      recommendedWorkflow: 'feature-flow',
      recommendedWorkflowReason: 'Multi-line reason\ncontinued here',
      risk: 'medium',
    })
  })

  it('parses folded block scalar reason (>)', () => {
    const body = [
      '---',
      'recommended_workflow_reason: >',
      '  Folded',
      '  text',
      '---',
      'body',
    ].join('\n')

    expect(parseIssueFrontmatter(body)).toEqual({
      kind: 'parsed',
      recommendedWorkflow: undefined,
      recommendedWorkflowReason: 'Folded text',
      risk: undefined,
    })
  })

  it('silently ignores unrecognized fields', () => {
    const body = ['---', 'title: hello', 'recommended_workflow: feature-flow', '---', 'body'].join('\n')
    expect(parseIssueFrontmatter(body)).toEqual({
      kind: 'parsed',
      recommendedWorkflow: 'feature-flow',
      recommendedWorkflowReason: undefined,
      risk: undefined,
    })
  })

  it('treats colon-less frontmatter line as malformed', () => {
    const body = ['---', 'this has no colon', 'recommended_workflow: feature-flow', '---', 'body'].join('\n')
    expect(parseIssueFrontmatter(body)).toEqual({ kind: 'malformed' })
  })

  it('treats missing closing delimiter as malformed', () => {
    const body = ['---', 'recommended_workflow: feature-flow', 'body without close'].join('\n')
    expect(parseIssueFrontmatter(body)).toEqual({ kind: 'malformed' })
  })

  it('strips a leading BOM on the opening delimiter', () => {
    const body = ['\uFEFF---', 'risk: high', '---', 'body'].join('\n')
    expect(parseIssueFrontmatter(body)).toEqual({
      kind: 'parsed',
      recommendedWorkflow: undefined,
      recommendedWorkflowReason: undefined,
      risk: 'high',
    })
  })

  it('handles CRLF line endings', () => {
    const body = ['---', 'recommended_workflow: feature-flow', 'risk: medium', '---', '', 'body'].join('\r\n')
    expect(parseIssueFrontmatter(body)).toEqual({
      kind: 'parsed',
      recommendedWorkflow: 'feature-flow',
      recommendedWorkflowReason: undefined,
      risk: 'medium',
    })
  })

  it('handles single-quoted values', () => {
    const body = ['---', "recommended_workflow: 'feature-flow'", '---', 'body'].join('\n')
    expect(parseIssueFrontmatter(body)).toEqual({
      kind: 'parsed',
      recommendedWorkflow: 'feature-flow',
      recommendedWorkflowReason: undefined,
      risk: undefined,
    })
  })
})
