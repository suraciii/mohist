import { createHash } from 'node:crypto'
import { join } from 'node:path'
import { NamedWorkspaceManager } from '../../src/runtime/workspace-entity.js'
import { NamedWorkspaceRegistry } from '../../src/runtime/workspace-registry.js'
import type {
  WorkspaceArtifactDirectory,
  WorkspaceArtifactInfo,
} from '../../src/server/connection-workspace-artifacts.js'
import type { ServerConnection } from '../../src/server/connection.js'
import {
  currentRunnerFileSystem,
  type RunnerFileSystem,
  type RunnerResourceContext,
} from '../../src/system/filesystem.js'
import { MemoryFileSystem } from './memory-filesystem.js'
import { withTestRunnerResources } from './test-resources.js'

// Shared hermetic fixture for the Runner Workspace Home recovery Spec. The
// Server is modeled as a fake provisioning connection: it lists only bound
// artifacts and re-serves their bytes; a pending/unbound artifact is never
// listed. Git is a fake command runner whose call log is asserted by the Spec.

export const ROOT = '/virtual/workspace-home-recovery'
export const PROJECT_ID = 'project-694'
export const WORKSPACE_NAME = 'issue-694'
export const REPOSITORY_NAME = 'master'
export const GIT_URL = 'https://example.test/mohist.git'
export const BASE_BRANCH = 'master'
export const RUN_BRANCH = `mohist/ws-${WORKSPACE_NAME}`
export const WORKFLOW_RUN_ID = 'wr-694'
export const STAGE_ONE_WORK_ID = 'plan:write'
export const STAGE_TWO_WORK_ID = 'build:write'
export const NOW = new Date('2026-07-01T08:00:00.000Z')

export const FILE_CONTENT = '{"tasks":[]}'
export const TAMPERED_FILE_CONTENT = '{"tasks":{}}'
export const DIRECTORY_ENTRY_CONTENT = 'research'
export const PENDING_ARTIFACT_ID = 'art_pending'

export function sha256(value: string): string {
  return `sha256:${createHash('sha256').update(value).digest('hex')}`
}

export const fileArtifact: WorkspaceArtifactInfo = {
  artifactId: 'art_plan',
  path: 'PLANS/tasks.json',
  kind: 'file',
  contentType: 'application/json',
  contentHash: sha256(FILE_CONTENT),
  size: FILE_CONTENT.length,
}
export const directoryArtifact: WorkspaceArtifactInfo = {
  artifactId: 'art_research',
  path: 'RESEARCH',
  kind: 'directory',
  contentType: 'application/x-mohist-artifact-directory',
  contentHash: null,
  size: DIRECTORY_ENTRY_CONTENT.length,
}
const boundArtifacts: readonly WorkspaceArtifactInfo[] = [fileArtifact, directoryArtifact]
const directoryListing: WorkspaceArtifactDirectory = {
  artifactId: directoryArtifact.artifactId,
  path: directoryArtifact.path,
  totalSize: DIRECTORY_ENTRY_CONTENT.length,
  entries: [
    {
      relativePath: 'notes.txt',
      size: DIRECTORY_ENTRY_CONTENT.length,
      contentHash: sha256(DIRECTORY_ENTRY_CONTENT),
      contentType: 'text/plain',
    },
  ],
}

export interface ListRequest {
  workflowRunId: string
  workId: string
}
export interface DownloadRequest {
  workflowRunId: string
  workId: string
  artifactId: string
  file: string | null
}
export interface ReportCall {
  projectId: string
  workspaceName: string
  path: string
}
export interface FakeProvisioningConnection {
  listRequests: ListRequest[]
  downloadRequests: DownloadRequest[]
  reportCalls: ReportCall[]
  listWorkspaceArtifacts(workflowRunId: string, workId: string): Promise<WorkspaceArtifactInfo[]>
  readWorkspaceArtifactDirectory(
    workflowRunId: string,
    workId: string,
    artifactId: string,
  ): Promise<WorkspaceArtifactDirectory>
  downloadWorkspaceArtifact(
    workflowRunId: string,
    workId: string,
    artifactId: string,
    signal: AbortSignal,
    file?: string,
  ): Promise<Uint8Array>
  reportWorkspaceProvisioned(
    projectId: string,
    workspaceName: string,
    path: string,
  ): Promise<{ runnerId: string; path: string }>
}

export function createFakeConnection(options: { tamperFileBytes?: boolean } = {}): FakeProvisioningConnection {
  const encoder = new TextEncoder()
  const connection: FakeProvisioningConnection = {
    listRequests: [],
    downloadRequests: [],
    reportCalls: [],
    async listWorkspaceArtifacts(workflowRunId, workId) {
      connection.listRequests.push({ workflowRunId, workId })
      return boundArtifacts.map((artifact) => ({ ...artifact }))
    },
    async readWorkspaceArtifactDirectory(_workflowRunId, _workId, artifactId) {
      if (artifactId !== directoryArtifact.artifactId) throw new Error(`unexpected directory read: ${artifactId}`)
      return directoryListing
    },
    async downloadWorkspaceArtifact(workflowRunId, workId, artifactId, _signal, file) {
      connection.downloadRequests.push({ workflowRunId, workId, artifactId, file: file ?? null })
      if (artifactId === PENDING_ARTIFACT_ID) throw new Error('a pending artifact must never be requested')
      if (artifactId === fileArtifact.artifactId) {
        return encoder.encode(options.tamperFileBytes ? TAMPERED_FILE_CONTENT : FILE_CONTENT)
      }
      if (artifactId === directoryArtifact.artifactId) return encoder.encode(DIRECTORY_ENTRY_CONTENT)
      throw new Error(`unexpected artifact download: ${artifactId}`)
    },
    async reportWorkspaceProvisioned(projectId, workspaceName, path) {
      connection.reportCalls.push({ projectId, workspaceName, path })
      return { runnerId: 'runner-1', path }
    },
  }
  return connection
}

export interface GitCall {
  command: string
  args: string[]
  cwd: string
}

function createCommandRunner(gitCalls: GitCall[]): RunnerResourceContext['commandRunner'] {
  return {
    run: async (command: string, args: string[], cwd: string) => {
      gitCalls.push({ command, args, cwd })
      if (command === 'git' && args[0] === 'clone') {
        const destination = args.at(-1)
        if (!destination) throw new Error('git clone without a destination')
        await currentRunnerFileSystem().ensureDir(join(destination, '.git'))
      }
      if (command === 'git' && args[2] === 'remote' && args[3] === 'get-url') {
        return { exitCode: 0, stdout: `${GIT_URL}\n`, stderr: '' }
      }
      if (command === 'git' && args[2] === 'rev-parse' && args[3] === '--abbrev-ref') {
        return { exitCode: 0, stdout: `${RUN_BRANCH}\n`, stderr: '' }
      }
      // Every other probe (run-branch `rev-parse --verify`, `checkout -B`)
      // succeeds so the run branch is the one ensured.
      return { exitCode: 0, stdout: '', stderr: '' }
    },
  }
}

export interface WorkspaceHomeRecoveryHarness {
  registry: NamedWorkspaceRegistry
  manager: NamedWorkspaceManager
  connection: FakeProvisioningConnection
  gitCalls: GitCall[]
}

export async function withWorkspaceHomeRecovery(
  body: (harness: WorkspaceHomeRecoveryHarness, fileSystem: RunnerFileSystem) => Promise<void>,
  options: { tamperFileBytes?: boolean } = {},
): Promise<void> {
  const gitCalls: GitCall[] = []
  await withTestRunnerResources(
    async (fileSystem) => {
      const registry = new NamedWorkspaceRegistry(ROOT, { now: () => NOW })
      await registry.load()
      const connection = createFakeConnection(options)
      const manager = new NamedWorkspaceManager(ROOT, registry, connection as unknown as ServerConnection, () => NOW)
      try {
        await body({ registry, manager, connection, gitCalls }, fileSystem)
      } finally {
        await fileSystem.deleteDirectory(ROOT)
      }
    },
    { commandRunner: createCommandRunner(gitCalls), fileSystem: new MemoryFileSystem() },
  )
}

export function provisionNamedWorkspace(manager: NamedWorkspaceManager, workId: string) {
  return manager.provisionForIssue(
    PROJECT_ID,
    WORKSPACE_NAME,
    REPOSITORY_NAME,
    GIT_URL,
    BASE_BRANCH,
    new AbortController().signal,
    WORKFLOW_RUN_ID,
    workId,
  )
}
