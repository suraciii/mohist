import { describe, expect, it } from 'vitest'
import { projectEventsQueryKey } from './queries'

describe('projectEventsQueryKey', () => {
  it('isolates project event caches by limit, types, and attention filter', () => {
    expect(projectEventsQueryKey('project-1', 50)).toEqual(['project-events', 'project-1', 50, [], false])
    expect(projectEventsQueryKey('project-1', 50)).not.toEqual(projectEventsQueryKey('project-1', 200))
    expect(projectEventsQueryKey('project-1', 50, ['failure'])).not.toEqual(projectEventsQueryKey('project-1', 50))
    expect(projectEventsQueryKey('project-1', 50, [], true)).not.toEqual(projectEventsQueryKey('project-1', 50))
  })
})
