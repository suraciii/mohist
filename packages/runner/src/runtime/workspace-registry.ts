import { dirname, join, resolve } from 'node:path'
import { exists, readText } from '../system/process.js'
import { currentRunnerFileSystem } from '../system/filesystem.js'

export type WorkspaceRegistryPhase = 'active' | 'eligible' | 'stuck'

// --- Named workspace registry (Workspace entity dimension) ---
//
// Runner-local registry of NAMED workspaces (the Workspace entity:
// persistent execution environments independent of any WorkflowRun)
// this runner has provisioned. The registry is runtime state, NOT domain
// truth — the server's Workspace grain owns status/home, and the on-disk
// identity marker owns the directory. The registry exists so cleanup never
// touches another runner's directory and so provisioning timestamps survive
// restarts. It is a rebuildable index: missing/unreadable/corrupt JSON loads
// empty and it may never recover work, infer an outcome, claim a Home or
// override Server Workspace state.
//
// Persistence layout:
//
//   <runnerRoot>/.mohist/named-workspaces.json
//
// Keyed by `ws:<projectId>:<workspaceName>` (the stable entity key used
// by the cleanup guards' disk-identity comparison).

export interface NamedWorkspaceRegistryEntry {
  projectId: string
  workspaceName: string
  workspacePath: string
  phase: WorkspaceRegistryPhase
  provisionedAt: string
  terminalAt: string | null
}

interface NamedWorkspaceRegistryFile {
  version: 1
  entries: Record<string, NamedWorkspaceRegistryEntry>
}

export interface NamedWorkspaceRegisterInput {
  projectId: string
  workspaceName: string
  workspacePath: string
}

export interface NamedWorkspaceRegistryOptions {
  filePath?: string
  now?: () => Date
}

export const DEFAULT_NAMED_WORKSPACE_REGISTRY_FILE = '.mohist/named-workspaces.json'

export function namedWorkspaceRegistryKey(projectId: string, workspaceName: string): string {
  return `ws:${projectId}:${workspaceName}`
}

export class NamedWorkspaceRegistry {
  private readonly filePath: string
  private readonly now: () => Date
  private entries: Map<string, NamedWorkspaceRegistryEntry> = new Map()
  private pathIndex: Map<string, string> = new Map()
  private loaded = false
  private tempSequence = 0

  constructor(runnerRoot: string, options: NamedWorkspaceRegistryOptions = {}) {
    this.filePath = options.filePath
      ? resolve(options.filePath)
      : resolve(join(runnerRoot, DEFAULT_NAMED_WORKSPACE_REGISTRY_FILE))
    this.now = options.now ?? (() => new Date())
  }

  getFilePath(): string {
    return this.filePath
  }

  list(): NamedWorkspaceRegistryEntry[] {
    this.ensureLoaded()
    return Array.from(this.entries.values()).map((entry) => ({ ...entry }))
  }

  get(projectId: string, workspaceName: string): NamedWorkspaceRegistryEntry | null {
    this.ensureLoaded()
    const entry = this.entries.get(namedWorkspaceRegistryKey(projectId, workspaceName))
    return entry ? { ...entry } : null
  }

  findByWorkspacePath(workspacePath: string): NamedWorkspaceRegistryEntry | null {
    this.ensureLoaded()
    const target = resolve(workspacePath)
    const key = this.pathIndex.get(target)
    const entry = key ? this.entries.get(key) : undefined
    return entry ? { ...entry } : null
  }

  entryKey(entry: NamedWorkspaceRegistryEntry): string {
    return namedWorkspaceRegistryKey(entry.projectId, entry.workspaceName)
  }

  async register(input: NamedWorkspaceRegisterInput): Promise<NamedWorkspaceRegistryEntry> {
    this.ensureLoaded()
    if (!input.projectId) throw new Error('named workspace registry register requires projectId')
    if (!input.workspaceName) throw new Error('named workspace registry register requires workspaceName')
    if (!input.workspacePath) throw new Error('named workspace registry register requires workspacePath')
    const key = namedWorkspaceRegistryKey(input.projectId, input.workspaceName)
    const workspacePath = resolve(input.workspacePath)
    const owner = this.pathIndex.get(workspacePath)
    if (owner && owner !== key) {
      throw new Error(`named workspace registry path is already owned by ${owner}`)
    }
    const provisionedAt = this.now().toISOString()
    const existing = this.entries.get(key)
    const entry: NamedWorkspaceRegistryEntry = {
      projectId: input.projectId,
      workspaceName: input.workspaceName,
      workspacePath,
      phase: 'active',
      provisionedAt,
      terminalAt: existing?.terminalAt ?? null,
    }
    if (existing && this.pathIndex.get(existing.workspacePath) === key && existing.workspacePath !== workspacePath) {
      this.pathIndex.delete(existing.workspacePath)
    }
    this.entries.set(key, entry)
    this.pathIndex.set(workspacePath, key)
    await this.persist()
    return { ...entry }
  }

  // Active -> eligible: the workspace is reclaimable per the server's
  // lifecycle observation (archived, or no active bound session). The
  // runner keeps no terminal fact of its own — the probe owns the
  // transition — but eligibility is sticky across re-provisionings
  // (a re-dispatch re-registers and flips the entry back to active).
  async markEligible(projectId: string, workspaceName: string): Promise<NamedWorkspaceRegistryEntry | null> {
    this.ensureLoaded()
    const key = namedWorkspaceRegistryKey(projectId, workspaceName)
    const existing = this.entries.get(key)
    if (!existing) return null
    if (existing.phase !== 'active') return { ...existing }
    existing.phase = 'eligible'
    existing.terminalAt = this.now().toISOString()
    await this.persist()
    return { ...existing }
  }

  async markStuck(key: string): Promise<NamedWorkspaceRegistryEntry | null> {
    this.ensureLoaded()
    const existing = this.entries.get(key)
    if (!existing) return null
    if (existing.phase !== 'eligible') return { ...existing }
    existing.phase = 'stuck'
    await this.persist()
    return { ...existing }
  }

  async remove(key: string): Promise<boolean> {
    this.ensureLoaded()
    const existing = this.entries.get(key)
    if (!existing) return false
    this.entries.delete(key)
    if (this.pathIndex.get(existing.workspacePath) === key) this.pathIndex.delete(existing.workspacePath)
    await this.persist()
    return true
  }

  async reload(): Promise<void> {
    await this.loadFromDisk()
  }

  async load(): Promise<void> {
    await this.loadFromDisk()
  }

  private ensureLoaded(): void {
    if (!this.loaded) {
      throw new Error(`NamedWorkspaceRegistry at ${this.filePath} has not been loaded; call load() first`)
    }
  }

  private async loadFromDisk(): Promise<void> {
    this.entries = new Map()
    this.pathIndex = new Map()
    if (!exists(this.filePath)) {
      this.loaded = true
      return
    }
    let raw: string
    try {
      raw = await readText(this.filePath)
    } catch {
      this.loaded = true
      return
    }
    let parsed: unknown
    try {
      parsed = JSON.parse(raw)
    } catch {
      this.loaded = true
      return
    }
    const file = parsed as Partial<NamedWorkspaceRegistryFile> | null
    if (!file || typeof file !== 'object' || file.version !== 1 || !file.entries || typeof file.entries !== 'object') {
      this.loaded = true
      return
    }
    const nextEntries = new Map<string, NamedWorkspaceRegistryEntry>()
    const nextPathIndex = new Map<string, string>()
    for (const [key, value] of Object.entries(file.entries)) {
      if (!value || typeof value !== 'object') continue
      const entry = value as Partial<NamedWorkspaceRegistryEntry>
      if (typeof entry.projectId !== 'string' || entry.projectId.length === 0) continue
      if (typeof entry.workspaceName !== 'string' || entry.workspaceName.length === 0) continue
      if (typeof entry.workspacePath !== 'string' || entry.workspacePath.length === 0) continue
      if (entry.phase !== 'active' && entry.phase !== 'eligible' && entry.phase !== 'stuck') continue
      if (typeof entry.provisionedAt !== 'string') continue
      const workspacePath = resolve(entry.workspacePath)
      if (nextPathIndex.has(workspacePath)) {
        this.entries = new Map()
        this.pathIndex = new Map()
        this.loaded = true
        return
      }
      nextEntries.set(key, {
        projectId: entry.projectId,
        workspaceName: entry.workspaceName,
        workspacePath,
        phase: entry.phase,
        provisionedAt: entry.provisionedAt,
        terminalAt: typeof entry.terminalAt === 'string' ? entry.terminalAt : null,
      })
      nextPathIndex.set(workspacePath, key)
    }
    this.entries = nextEntries
    this.pathIndex = nextPathIndex
    this.loaded = true
  }

  private async persist(): Promise<void> {
    const dir = dirname(this.filePath)
    await currentRunnerFileSystem().ensureDir(dir)
    const tempPath = `${this.filePath}.${this.tempSequence++}.tmp`
    const file: NamedWorkspaceRegistryFile = {
      version: 1,
      entries: Object.fromEntries(this.entries),
    }
    await currentRunnerFileSystem().writeText(tempPath, JSON.stringify(file, null, 2))
    await currentRunnerFileSystem().rename(tempPath, this.filePath)
  }
}

// Default per-runner named registry file path resolver. Exposed for
// callers (and tests) that want to assert the on-disk location without
// instantiating a registry.
export function defaultNamedWorkspaceRegistryFilePath(runnerRoot: string): string {
  return resolve(join(runnerRoot, DEFAULT_NAMED_WORKSPACE_REGISTRY_FILE))
}
