import { describe, expect, it } from 'vitest'
import { issueWorkflowArtifactsQueryOptions } from './queries'

describe('artifact query scope', () => {
  it('includes workflowRunId in identity while keeping the request params unchanged', () => {
    const first = issueWorkflowArtifactsQueryOptions('project', 455, { path: 'proposal.md' }, true, 'run-a')
    const second = issueWorkflowArtifactsQueryOptions('project', 455, { path: 'proposal.md' }, true, 'run-b')

    expect(first.queryKey).not.toEqual(second.queryKey)
    expect(first.queryKey.at(-1)).toEqual({ path: 'proposal.md' })
    expect(second.queryKey.at(-1)).toEqual({ path: 'proposal.md' })
  })
})
