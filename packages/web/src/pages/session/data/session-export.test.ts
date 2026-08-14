import { describe, expect, it } from 'vitest'
import { buildSessionExport } from './session-export'

describe('buildSessionExport', () => {
  it('preserves stable Session navigation context with the current public view', () => {
    const result = buildSessionExport({
      exportedAt: '2026-08-10T00:00:00.000Z',
      context: {
        projectId: 'project-1',
        sessionId: 'session-1',
        inputId: 'input-1',
        turnId: 'turn-1',
        jobId: 'job-1',
        view: 'public',
      },
      metadata: null,
      transcript: null,
      timeline: [],
    })

    expect(result.version).toBe(1)
    expect(result.context).toEqual({
      projectId: 'project-1',
      sessionId: 'session-1',
      inputId: 'input-1',
      turnId: 'turn-1',
      jobId: 'job-1',
      view: 'public',
    })
  })
})
