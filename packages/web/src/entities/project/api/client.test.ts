import { describe, expect, it, vi } from 'vitest'
import { request } from '../../../shared/api/client'
import { createProject } from './client'
import type { Project } from '../model/types'

describe('createProject', () => {
  it('posts only the project name to /api/projects', async () => {
    const project: Project = {
      id: 'proj-1',
      name: 'my-project',
      createdAt: '2026-06-12T00:00:00.000Z',
      updatedAt: '2026-06-12T00:00:00.000Z',
      repositories: [],
    }
    const requester = vi.fn().mockResolvedValue(project)

    await createProject({ name: 'my-project' }, requester as typeof request)

    expect(requester).toHaveBeenCalledWith('/projects', {
      method: 'POST',
      body: JSON.stringify({ name: 'my-project' }),
    })
  })
})
