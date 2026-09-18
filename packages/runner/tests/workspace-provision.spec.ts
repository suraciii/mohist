import { createHash } from 'node:crypto'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { provisionWorkspaceArtifacts } from '../src/runtime/workspace-provision.js'
import type { WorkspaceArtifactDirectory, WorkspaceArtifactInfo } from '../src/server/connection-workspace-artifacts.js'
import { withTestRunnerResources } from './support/test-resources.js'

function hash(value: string): string {
  return `sha256:${createHash('sha256').update(value).digest('hex')}`
}

describe('Workspace Home provisioning', () => {
  it('provisions bound file and directory artifacts into a new Home', async () => {
    await withTestRunnerResources(async (fileSystem) => {
      const root = '/workspace'
      await fileSystem.ensureDir(join(root, 'PLANS'))
      await fileSystem.writeText(join(root, 'RESEARCH/existing.txt'), 'keep')
      const file = new TextEncoder().encode('{"tasks":[]}')
      const research = new TextEncoder().encode('research')
      const artifacts: WorkspaceArtifactInfo[] = [
        {
          artifactId: 'art_plan',
          path: 'PLANS/tasks.json',
          kind: 'file',
          contentType: 'application/json',
          contentHash: hash('{"tasks":[]}'),
          size: file.byteLength,
        },
        {
          artifactId: 'art_research',
          path: 'RESEARCH',
          kind: 'directory',
          contentType: 'application/x-mohist-artifact-directory',
          contentHash: null,
          size: 999,
        },
      ]
      const directory: WorkspaceArtifactDirectory = {
        artifactId: 'art_research',
        path: 'RESEARCH',
        totalSize: research.byteLength,
        entries: [
          {
            relativePath: 'notes.txt',
            size: research.byteLength,
            contentHash: hash('research'),
            contentType: 'text/plain',
          },
        ],
      }
      const downloads: string[] = []
      const client = {
        async listWorkspaceArtifacts() {
          return artifacts
        },
        async readWorkspaceArtifactDirectory() {
          return directory
        },
        async downloadWorkspaceArtifact(
          _workflowRunId: string,
          _workId: string,
          artifactId: string,
          _signal: AbortSignal,
          filePath?: string,
        ) {
          downloads.push(`${artifactId}:${filePath ?? ''}`)
          return artifactId === 'art_plan' ? file : research
        },
      }

      await provisionWorkspaceArtifacts(client, 'run-1', 'work-1', root, new AbortController().signal)

      await expect(fileSystem.readText(join(root, 'PLANS/tasks.json'))).resolves.toBe('{"tasks":[]}')
      await expect(fileSystem.readText(join(root, 'RESEARCH/notes.txt'))).resolves.toBe('research')
      await expect(fileSystem.readText(join(root, 'RESEARCH/existing.txt'))).resolves.toBe('keep')
      expect(downloads).toEqual(['art_plan:', 'art_research:notes.txt'])

      await provisionWorkspaceArtifacts(client, 'run-1', 'work-1', root, new AbortController().signal)
      expect(downloads).toEqual(['art_plan:', 'art_research:notes.txt'])
    })
  })

  it('rejects an existing file whose durable content does not match', async () => {
    await withTestRunnerResources(async (fileSystem) => {
      const root = '/workspace'
      await fileSystem.writeText(join(root, 'PLANS/tasks.json'), 'wrong')
      const client = {
        async listWorkspaceArtifacts() {
          return [
            {
              artifactId: 'art_plan',
              path: 'PLANS/tasks.json',
              kind: 'file' as const,
              contentType: 'application/json',
              contentHash: hash('{"tasks":[]}'),
              size: 'wrong'.length,
            },
          ]
        },
        async readWorkspaceArtifactDirectory() {
          throw new Error('unexpected directory read')
        },
        async downloadWorkspaceArtifact() {
          throw new Error('existing content must be checked locally')
        },
      }

      await expect(
        provisionWorkspaceArtifacts(client, 'run-1', 'work-1', root, new AbortController().signal),
      ).rejects.toThrow(/content hash mismatch/)
    })
  })

  it.each([
    'REPOS/master/secret',
    '.mohist/marker',
    '.scratch/tmp',
    '.mohist-provision-temp/file',
    '../outside',
    '/absolute',
  ])('rejects reserved or escaping artifact path %s', async (path) => {
    await withTestRunnerResources(async () => {
      const client = {
        async listWorkspaceArtifacts() {
          return [
            {
              artifactId: 'bad',
              path,
              kind: 'file' as const,
              contentType: null,
              contentHash: null,
              size: 1,
            },
          ]
        },
        async readWorkspaceArtifactDirectory() {
          throw new Error('unexpected directory read')
        },
        async downloadWorkspaceArtifact() {
          throw new Error('unexpected artifact download')
        },
      }

      await expect(
        provisionWorkspaceArtifacts(client, 'run-1', 'work-1', '/workspace', new AbortController().signal),
      ).rejects.toThrow()
    })
  })
})
