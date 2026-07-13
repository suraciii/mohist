import { describe, expect, it } from 'vitest'
import { projectEventsQueryKey } from './queries'

describe('projectEventsQueryKey', () => {
  it('isolates project event caches by effective limit', () => {
    expect(projectEventsQueryKey('project-1', 50)).toEqual(['project-events', 'project-1', 50])
    expect(projectEventsQueryKey('project-1', 50)).not.toEqual(projectEventsQueryKey('project-1', 200))
  })
})
