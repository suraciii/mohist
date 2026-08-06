import { mkdir, rename, writeFile } from "node:fs/promises"
import { dirname, join, resolve } from "node:path"
import { exists, readText } from "../system/process.js"
import type { WorkspaceRegistryPhase } from "./workspace-registry.js"

// Runner-local registry of agent managed worktrees this runner has
// materialized. Mirrors `WorkspaceRegistry` for workflow workspaces:
// the registry is a rebuildable index, NOT domain truth — the on-disk
// git worktree is the fact, and `AgentWorkspaceManager.recover()`
// rescans `<runnerRoot>/agent-workspaces/` when the registry is lost.
//
// Persistence layout (sibling of the workflow workspace registry):
//
//   <runnerRoot>/.mohist/runner-state/agent-workspaces.json
//
// Keyed by `childSessionId` (the materialization idempotency key).
// Mutations are write-through: every in-memory change is immediately
// persisted via an atomic temp-file + rename. The atomic rename keeps
// the on-disk file valid even if the runner is killed mid-write.

export const DEFAULT_AGENT_WORKSPACE_REGISTRY_FILE = ".mohist/runner-state/agent-workspaces.json"

export interface AgentWorkspaceRegistryEntry {
  childSessionId: string
  projectId: string | null
  workspaceIdentity: string
  workspacePath: string
  branch: string
  parentWorkDir: string
  repositoryName: string | null
  phase: WorkspaceRegistryPhase
  materializedAt: string
  terminalAt: string | null
}

interface AgentWorkspaceRegistryFile {
  version: 1
  entries: Record<string, AgentWorkspaceRegistryEntry>
}

export interface AgentWorkspaceRegisterInput {
  childSessionId: string
  projectId: string | null
  workspaceIdentity: string
  workspacePath: string
  branch: string
  parentWorkDir: string
  repositoryName: string | null
}

export interface AgentWorkspaceRegistryOptions {
  // Override the registry file path (used by tests). Defaults to
  // `<runnerRoot>/.mohist/runner-state/agent-workspaces.json`.
  filePath?: string
  // Override the clock for tests. Defaults to `() => new Date()`.
  now?: () => Date
}

export class AgentWorkspaceRegistry {
  private readonly filePath: string
  private readonly now: () => Date
  private entries: Map<string, AgentWorkspaceRegistryEntry> = new Map()
  private pathIndex: Map<string, string> = new Map()
  private loaded = false

  constructor(runnerRoot: string, options: AgentWorkspaceRegistryOptions = {}) {
    this.filePath = options.filePath
      ? resolve(options.filePath)
      : resolve(join(runnerRoot, DEFAULT_AGENT_WORKSPACE_REGISTRY_FILE))
    this.now = options.now ?? (() => new Date())
  }

  getFilePath(): string {
    return this.filePath
  }

  list(): AgentWorkspaceRegistryEntry[] {
    this.ensureLoaded()
    return Array.from(this.entries.values()).map((entry) => ({ ...entry }))
  }

  get(childSessionId: string): AgentWorkspaceRegistryEntry | null {
    this.ensureLoaded()
    const entry = this.entries.get(childSessionId)
    return entry ? { ...entry } : null
  }

  findByWorkspacePath(workspacePath: string): AgentWorkspaceRegistryEntry | null {
    this.ensureLoaded()
    const target = resolve(workspacePath)
    const childSessionId = this.pathIndex.get(target)
    const entry = childSessionId ? this.entries.get(childSessionId) : undefined
    return entry ? { ...entry } : null
  }

  // The registry key for an entry. Used by the shared maintenance loop
  // to address `markStuck` / `remove` without knowing the entry type.
  entryKey(entry: AgentWorkspaceRegistryEntry): string {
    return entry.childSessionId
  }

  // Upsert a registration. An existing entry keeps its phase and
  // `terminalAt` (eligibility is sticky; adoption / re-register must
  // not revive a terminal entry); a fresh childSessionId starts
  // `active`. Stamp `materializedAt = now` on every write.
  async register(input: AgentWorkspaceRegisterInput): Promise<AgentWorkspaceRegistryEntry> {
    this.ensureLoaded()
    if (!input.childSessionId) throw new Error("agent workspace registry register requires childSessionId")
    if (!input.workspacePath) throw new Error("agent workspace registry register requires workspacePath")
    const workspacePath = resolve(input.workspacePath)
    const owner = this.pathIndex.get(workspacePath)
    if (owner && owner !== input.childSessionId) {
      throw new Error(`agent workspace registry path is already owned by childSessionId ${owner}`)
    }
    const materializedAt = this.now().toISOString()
    const existing = this.entries.get(input.childSessionId)
    const entry: AgentWorkspaceRegistryEntry = {
      childSessionId: input.childSessionId,
      projectId: input.projectId,
      workspaceIdentity: input.workspaceIdentity,
      workspacePath,
      branch: input.branch,
      parentWorkDir: resolve(input.parentWorkDir),
      repositoryName: input.repositoryName,
      phase: existing?.phase ?? "active",
      materializedAt,
      terminalAt: existing?.terminalAt ?? null,
    }
    if (existing && this.pathIndex.get(existing.workspacePath) === input.childSessionId && existing.workspacePath !== workspacePath) {
      this.pathIndex.delete(existing.workspacePath)
    }
    this.entries.set(input.childSessionId, entry)
    this.pathIndex.set(workspacePath, input.childSessionId)
    await this.persist()
    return { ...entry }
  }

  // Transition an entry to `eligible` and stamp `terminalAt`. Release
  // is Server-authoritative, so ANY phase (including `stuck`) moves to
  // `eligible`. Idempotent: an already-eligible entry is returned
  // unchanged and the file is not rewritten. Returns null when no
  // entry exists.
  async markEligible(childSessionId: string): Promise<AgentWorkspaceRegistryEntry | null> {
    this.ensureLoaded()
    const existing = this.entries.get(childSessionId)
    if (!existing) return null
    if (existing.phase === "eligible") return { ...existing }
    existing.phase = "eligible"
    existing.terminalAt = this.now().toISOString()
    await this.persist()
    return { ...existing }
  }

  // Transition an entry from `eligible` to `stuck`. Called by the
  // cleanup loop's resolution pass when a pre-delete guard
  // deterministically refuses an `eligible` entry. Idempotent: a
  // non-eligible entry is returned unchanged and the file is not
  // rewritten. Returns null when no entry exists.
  async markStuck(childSessionId: string): Promise<AgentWorkspaceRegistryEntry | null> {
    this.ensureLoaded()
    const existing = this.entries.get(childSessionId)
    if (!existing) return null
    if (existing.phase !== "eligible") return { ...existing }
    existing.phase = "stuck"
    await this.persist()
    return { ...existing }
  }

  // Drop a registry entry. Returns true when an entry was removed.
  async remove(childSessionId: string): Promise<boolean> {
    this.ensureLoaded()
    const existing = this.entries.get(childSessionId)
    if (!existing) return false
    this.entries.delete(childSessionId)
    if (this.pathIndex.get(existing.workspacePath) === childSessionId) this.pathIndex.delete(existing.workspacePath)
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
      throw new Error(`AgentWorkspaceRegistry at ${this.filePath} has not been loaded; call load() first`)
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
    const file = parsed as Partial<AgentWorkspaceRegistryFile> | null
    if (!file || typeof file !== "object" || file.version !== 1 || !file.entries || typeof file.entries !== "object") {
      this.loaded = true
      return
    }
    const nextEntries = new Map<string, AgentWorkspaceRegistryEntry>()
    const nextPathIndex = new Map<string, string>()
    for (const [key, value] of Object.entries(file.entries)) {
      if (!value || typeof value !== "object") continue
      const entry = value as Partial<AgentWorkspaceRegistryEntry>
      if (typeof entry.childSessionId !== "string" || entry.childSessionId.length === 0) continue
      if (typeof entry.workspaceIdentity !== "string" || entry.workspaceIdentity.length === 0) continue
      if (typeof entry.workspacePath !== "string" || entry.workspacePath.length === 0) continue
      if (typeof entry.branch !== "string" || entry.branch.length === 0) continue
      if (typeof entry.parentWorkDir !== "string" || entry.parentWorkDir.length === 0) continue
      if (entry.phase !== "active" && entry.phase !== "eligible" && entry.phase !== "stuck") continue
      if (typeof entry.materializedAt !== "string") continue
      const workspacePath = resolve(entry.workspacePath)
      if (nextPathIndex.has(workspacePath)) {
        this.entries = new Map()
        this.pathIndex = new Map()
        this.loaded = true
        return
      }
      const loadedEntry: AgentWorkspaceRegistryEntry = {
        childSessionId: entry.childSessionId,
        projectId: typeof entry.projectId === "string" ? entry.projectId : null,
        workspaceIdentity: entry.workspaceIdentity,
        workspacePath,
        branch: entry.branch,
        parentWorkDir: resolve(entry.parentWorkDir),
        repositoryName: typeof entry.repositoryName === "string" ? entry.repositoryName : null,
        phase: entry.phase,
        materializedAt: entry.materializedAt,
        terminalAt: typeof entry.terminalAt === "string" ? entry.terminalAt : null,
      }
      nextEntries.set(key, loadedEntry)
      nextPathIndex.set(workspacePath, key)
    }
    this.entries = nextEntries
    this.pathIndex = nextPathIndex
    this.loaded = true
  }

  private async persist(): Promise<void> {
    const dir = dirname(this.filePath)
    await mkdir(dir, { recursive: true })
    const tempPath = `${this.filePath}.${process.pid}.${Date.now()}.tmp`
    const file: AgentWorkspaceRegistryFile = {
      version: 1,
      entries: Object.fromEntries(this.entries),
    }
    await writeFile(tempPath, JSON.stringify(file, null, 2))
    await rename(tempPath, this.filePath)
  }
}

export function defaultAgentWorkspaceRegistryFilePath(runnerRoot: string): string {
  return resolve(join(runnerRoot, DEFAULT_AGENT_WORKSPACE_REGISTRY_FILE))
}
