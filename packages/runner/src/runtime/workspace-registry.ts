import { mkdir, rename, writeFile } from "node:fs/promises"
import { dirname, join, resolve } from "node:path"
import { exists, readText } from "../system/process.js"

// Runner-local registry of workspaces this runner has materialized. The
// registry is runtime state, NOT domain truth — workflow run lifecycle
// facts come from the server (events + status queries). The registry
// exists so the runner can:
//   - track which workspaces it owns (so automatic cleanup never touches
//     something another runner materialized);
//   - remember `materializedAt` / `terminalAt` timestamps without writing
//     them into the on-disk marker (the marker stays identity-only);
//   - survive a runner restart with the same set of tracked workspaces.
//
// Persistence layout (per D1 of the workspace-cleanup design):
//
//   <runnerRoot>/.mohist/runner-state/workspaces.json
//
// Keyed by `workflowRunId` (the stable run identity used by both the
// marker and the workflow grain). Mutations are write-through: every
// in-memory change is immediately persisted via an atomic temp-file +
// rename. The atomic rename keeps the on-disk file valid even if the
// runner is killed mid-write.

export type WorkspaceRegistryPhase = "active" | "eligible" | "stuck"

export interface WorkspaceRegistryEntry {
  issueNumber: number
  workflowRunId: string
  workspacePath: string
  runBranch?: string | null
  phase: WorkspaceRegistryPhase
  materializedAt: string
  terminalAt: string | null
}

// Wire shape. Persistence is intentionally versioned so a future schema
// change can be detected and handled without silently corrupting state.
interface WorkspaceRegistryFile {
  version: 2
  entries: Record<string, WorkspaceRegistryEntry>
}

export interface RegisterInput {
  issueNumber: number
  workflowRunId: string
  workspacePath: string
  runBranch?: string | null
}

export interface WorkspaceRegistryOptions {
  // Override the registry file path (used by tests). Defaults to
  // `<runnerRoot>/.mohist/runner-state/workspaces.json`.
  filePath?: string
  // Override the clock for tests. Defaults to `() => new Date()`.
  now?: () => Date
}

export const DEFAULT_WORKSPACE_REGISTRY_FILE = ".mohist/runner-state/workspaces.json"

export class WorkspaceRegistry {
  private readonly filePath: string
  private readonly now: () => Date
  private entries: Map<string, WorkspaceRegistryEntry> = new Map()
  private pathIndex: Map<string, string> = new Map()
  private loaded = false

  constructor(runnerRoot: string, options: WorkspaceRegistryOptions = {}) {
    this.filePath = options.filePath
      ? resolve(options.filePath)
      : resolve(join(runnerRoot, DEFAULT_WORKSPACE_REGISTRY_FILE))
    this.now = options.now ?? (() => new Date())
  }

  // Path of the registry file. Stable regardless of load state so
  // callers (and tests) can reason about disk layout independently.
  getFilePath(): string {
    return this.filePath
  }

  // Read the current in-memory entries. Returns a snapshot so callers
  // cannot mutate registry state by editing the returned object.
  list(): WorkspaceRegistryEntry[] {
    this.ensureLoaded()
    return Array.from(this.entries.values()).map((entry) => ({ ...entry }))
  }

  get(workflowRunId: string): WorkspaceRegistryEntry | null {
    this.ensureLoaded()
    const entry = this.entries.get(workflowRunId)
    return entry ? { ...entry } : null
  }

  // Find the entry whose resolved workspace path equals `workspacePath`.
  // Used by the manual RemoveWorkspace handler which only knows the path
  // and needs to drop the matching registry entry to keep the registry
  // consistent with disk reality.
  findByWorkspacePath(workspacePath: string): WorkspaceRegistryEntry | null {
    this.ensureLoaded()
    const target = resolve(workspacePath)
    const workflowRunId = this.pathIndex.get(target)
    const entry = workflowRunId ? this.entries.get(workflowRunId) : undefined
    return entry ? { ...entry } : null
  }

  // The registry key for an entry. Used by the shared maintenance loop
  // to address `markStuck` / `remove` without knowing the entry type.
  entryKey(entry: WorkspaceRegistryEntry): string {
    return entry.workflowRunId
  }

  // Upsert a workspace registration in the `active` phase. Called from
  // WorkspaceManager.materialize() success. Every
  // successful materialize stamps `materializedAt = now` so the
  // timestamp records the last materialization. `terminalAt` is
  // preserved if the entry was previously eligible — eligibility is
  // sticky across re-materializations for the same workflowRunId.
  // A new run (different workflowRunId) starts a fresh entry because
  // the registry is keyed by workflowRunId.
  async register(input: RegisterInput): Promise<WorkspaceRegistryEntry> {
    this.ensureLoaded()
    if (!input.workflowRunId) throw new Error("workspace registry register requires workflowRunId")
    if (!input.workspacePath) throw new Error("workspace registry register requires workspacePath")
    const workspacePath = resolve(input.workspacePath)
    const owner = this.pathIndex.get(workspacePath)
    if (owner && owner !== input.workflowRunId) {
      throw new Error(`workspace registry path is already owned by workflowRunId ${owner}`)
    }
    const materializedAt = this.now().toISOString()
    const existing = this.entries.get(input.workflowRunId)
    const entry: WorkspaceRegistryEntry = {
      issueNumber: input.issueNumber,
      workflowRunId: input.workflowRunId,
      workspacePath,
      runBranch: input.runBranch ?? null,
      phase: "active",
      materializedAt,
      terminalAt: existing?.terminalAt ?? null,
    }
    if (existing && this.pathIndex.get(existing.workspacePath) === input.workflowRunId && existing.workspacePath !== workspacePath) {
      this.pathIndex.delete(existing.workspacePath)
    }
    this.entries.set(input.workflowRunId, entry)
    this.pathIndex.set(workspacePath, input.workflowRunId)
    await this.persist()
    return { ...entry }
  }

  // Refresh `materializedAt` for an entry that already exists (a verify()
  // hit on an already-materialized workspace). Does not downgrade an
  // existing `eligible` entry — eligibility is sticky; only a fresh
  // workflowRunId (a new dispatch) starts a new active entry.
  // Returns null when no entry exists for `workflowRunId`; callers that
  // need an active entry can decide to call `register()` separately.
  async refreshMaterializedAt(workflowRunId: string): Promise<WorkspaceRegistryEntry | null> {
    this.ensureLoaded()
    const existing = this.entries.get(workflowRunId)
    if (!existing) return null
    existing.materializedAt = this.now().toISOString()
    await this.persist()
    return { ...existing }
  }

  // Transition an entry from `active` to `eligible` and stamp
  // `terminalAt`. Idempotent: an already-eligible entry is returned
  // unchanged and the on-disk file is not rewritten. Returns null when
  // no entry exists (the runner only tracks workspaces it materialized).
  // The guard is `phase !== "active"` (not just `phase === "eligible"`)
  // so a redelivered terminal workflow-status event cannot revive a
  // `stuck` entry back into the eligible loop.
  async markEligible(workflowRunId: string): Promise<WorkspaceRegistryEntry | null> {
    this.ensureLoaded()
    const existing = this.entries.get(workflowRunId)
    if (!existing) return null
    if (existing.phase !== "active") return { ...existing }
    existing.phase = "eligible"
    existing.terminalAt = this.now().toISOString()
    await this.persist()
    return { ...existing }
  }

  // Transition an entry from `eligible` to `stuck`. Called by the
  // cleanup loop's resolution pass when a pre-delete guard refuses an
  // `eligible` entry — the entry leaves the eligible set so it is no
  // longer re-evaluated or re-warned on subsequent ticks (the phase
  // transition is the structural warning de-duplication). Only an
  // `eligible` entry transitions: an `active` entry is not yet
  // terminal, and an already-`stuck` entry is a no-op. Idempotent: a
  // non-eligible entry is returned unchanged and the on-disk file is
  // not rewritten. Returns null when no entry exists.
  async markStuck(workflowRunId: string): Promise<WorkspaceRegistryEntry | null> {
    this.ensureLoaded()
    const existing = this.entries.get(workflowRunId)
    if (!existing) return null
    if (existing.phase !== "eligible") return { ...existing }
    existing.phase = "stuck"
    await this.persist()
    return { ...existing }
  }

  // Drop a registry entry. Called by the manual RemoveWorkspace handler
  // once the directory is gone so the registry stays consistent with
  // disk reality. Returns true when an entry was actually removed.
  async remove(workflowRunId: string): Promise<boolean> {
    this.ensureLoaded()
    const existing = this.entries.get(workflowRunId)
    if (!existing) return false
    this.entries.delete(workflowRunId)
    if (this.pathIndex.get(existing.workspacePath) === workflowRunId) this.pathIndex.delete(existing.workspacePath)
    await this.persist()
    return true
  }

  // Force a reload from disk. Mostly used by tests that want to assert
  // the post-write file is readable independently of this instance.
  async reload(): Promise<void> {
    await this.loadFromDisk()
  }

  // Eagerly load the registry from disk. Idempotent. Called by
  // RunnerHost at startup so the in-memory cache is hot before the
  // first dispatch or SignalR RPC.
  async load(): Promise<void> {
    await this.loadFromDisk()
  }

  private ensureLoaded(): void {
    if (!this.loaded) {
      // Synchronous trigger of the load is not allowed — callers must
      // explicitly invoke load(). Methods that read state first call
      // this guard to surface a clearer error if load was forgotten.
      throw new Error(`WorkspaceRegistry at ${this.filePath} has not been loaded; call load() first`)
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
      // Treat unreadable file as empty — the next persist() will rewrite
      // it. This is safer than throwing: a transient permission error
      // must not prevent the runner from starting.
      this.loaded = true
      return
    }
    let parsed: unknown
    try {
      parsed = JSON.parse(raw)
    } catch {
      // Corrupt JSON: behave as empty. The atomic-write guarantee means
      // a half-written file should not appear, but legacy or external
      // edits could corrupt the file.
      this.loaded = true
      return
    }
    const file = parsed as Partial<WorkspaceRegistryFile> | null
    if (!file || typeof file !== "object" || file.version !== 2 || !file.entries || typeof file.entries !== "object") {
      this.loaded = true
      return
    }
    const nextEntries = new Map<string, WorkspaceRegistryEntry>()
    const nextPathIndex = new Map<string, string>()
    for (const [key, value] of Object.entries(file.entries)) {
      if (!value || typeof value !== "object") continue
      const entry = value as Partial<WorkspaceRegistryEntry>
      if (typeof entry.workflowRunId !== "string" || entry.workflowRunId.length === 0) continue
      if (typeof entry.workspacePath !== "string" || entry.workspacePath.length === 0) continue
      if (entry.phase !== "active" && entry.phase !== "eligible" && entry.phase !== "stuck") continue
      if (typeof entry.materializedAt !== "string") continue
      const workspacePath = resolve(entry.workspacePath)
      if (nextPathIndex.has(workspacePath)) {
        this.entries = new Map()
        this.pathIndex = new Map()
        this.loaded = true
        return
      }
      const loadedEntry = {
        issueNumber: typeof entry.issueNumber === "number" ? entry.issueNumber : 0,
        workflowRunId: entry.workflowRunId,
        workspacePath,
        runBranch: typeof entry.runBranch === "string" ? entry.runBranch : null,
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

  // Write-through atomic persistence. Writes to a sibling temp file and
  // renames over the live path so a crash mid-write cannot leave a
  // half-written file behind.
  private async persist(): Promise<void> {
    const dir = dirname(this.filePath)
    await mkdir(dir, { recursive: true })
    const tempPath = `${this.filePath}.${process.pid}.${Date.now()}.tmp`
    const file: WorkspaceRegistryFile = {
      version: 2,
      entries: Object.fromEntries(this.entries),
    }
    await writeFile(tempPath, JSON.stringify(file, null, 2))
    await rename(tempPath, this.filePath)
  }
}

// Default per-runner registry file path resolver. Exposed for callers
// (and tests) that want to assert the on-disk location without
// instantiating a registry.
export function defaultWorkspaceRegistryFilePath(runnerRoot: string): string {
  return resolve(join(runnerRoot, DEFAULT_WORKSPACE_REGISTRY_FILE))
}

// --- Named workspace registry (Workspace entity dimension) ---
//
// Runner-local registry of NAMED workspaces (the Workspace entity:
// persistent execution environments independent of any WorkflowRun)
// this runner has materialized. Sibling of the workflow registry above
// and governed by the same rules: runtime state, NOT domain truth —
// the server's Workspace grain owns status/home, and the on-disk
// identity marker owns the directory. The registry exists so cleanup
// never touches another runner's directory and so materialization
// timestamps survive restarts.
//
// Persistence layout:
//
//   <runnerRoot>/.mohist/runner-state/named-workspaces.json
//
// Keyed by `ws:<projectId>:<workspaceName>` (the stable entity key used
// by the cleanup guards' disk-identity comparison).

export interface NamedWorkspaceRegistryEntry {
  projectId: string
  workspaceName: string
  workspacePath: string
  phase: WorkspaceRegistryPhase
  materializedAt: string
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

export const DEFAULT_NAMED_WORKSPACE_REGISTRY_FILE = ".mohist/runner-state/named-workspaces.json"

export function namedWorkspaceRegistryKey(projectId: string, workspaceName: string): string {
  return `ws:${projectId}:${workspaceName}`
}

export class NamedWorkspaceRegistry {
  private readonly filePath: string
  private readonly now: () => Date
  private entries: Map<string, NamedWorkspaceRegistryEntry> = new Map()
  private pathIndex: Map<string, string> = new Map()
  private loaded = false

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
    if (!input.projectId) throw new Error("named workspace registry register requires projectId")
    if (!input.workspaceName) throw new Error("named workspace registry register requires workspaceName")
    if (!input.workspacePath) throw new Error("named workspace registry register requires workspacePath")
    const key = namedWorkspaceRegistryKey(input.projectId, input.workspaceName)
    const workspacePath = resolve(input.workspacePath)
    const owner = this.pathIndex.get(workspacePath)
    if (owner && owner !== key) {
      throw new Error(`named workspace registry path is already owned by ${owner}`)
    }
    const materializedAt = this.now().toISOString()
    const existing = this.entries.get(key)
    const entry: NamedWorkspaceRegistryEntry = {
      projectId: input.projectId,
      workspaceName: input.workspaceName,
      workspacePath,
      phase: "active",
      materializedAt,
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
  // transition — but eligibility is sticky across re-materializations
  // (a re-dispatch re-registers and flips the entry back to active).
  async markEligible(projectId: string, workspaceName: string): Promise<NamedWorkspaceRegistryEntry | null> {
    this.ensureLoaded()
    const key = namedWorkspaceRegistryKey(projectId, workspaceName)
    const existing = this.entries.get(key)
    if (!existing) return null
    if (existing.phase !== "active") return { ...existing }
    existing.phase = "eligible"
    existing.terminalAt = this.now().toISOString()
    await this.persist()
    return { ...existing }
  }

  async markStuck(key: string): Promise<NamedWorkspaceRegistryEntry | null> {
    this.ensureLoaded()
    const existing = this.entries.get(key)
    if (!existing) return null
    if (existing.phase !== "eligible") return { ...existing }
    existing.phase = "stuck"
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
    if (!file || typeof file !== "object" || file.version !== 1 || !file.entries || typeof file.entries !== "object") {
      this.loaded = true
      return
    }
    const nextEntries = new Map<string, NamedWorkspaceRegistryEntry>()
    const nextPathIndex = new Map<string, string>()
    for (const [key, value] of Object.entries(file.entries)) {
      if (!value || typeof value !== "object") continue
      const entry = value as Partial<NamedWorkspaceRegistryEntry>
      if (typeof entry.projectId !== "string" || entry.projectId.length === 0) continue
      if (typeof entry.workspaceName !== "string" || entry.workspaceName.length === 0) continue
      if (typeof entry.workspacePath !== "string" || entry.workspacePath.length === 0) continue
      if (entry.phase !== "active" && entry.phase !== "eligible" && entry.phase !== "stuck") continue
      if (typeof entry.materializedAt !== "string") continue
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
        materializedAt: entry.materializedAt,
        terminalAt: typeof entry.terminalAt === "string" ? entry.terminalAt : null,
      })
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
    const file: NamedWorkspaceRegistryFile = {
      version: 1,
      entries: Object.fromEntries(this.entries),
    }
    await writeFile(tempPath, JSON.stringify(file, null, 2))
    await rename(tempPath, this.filePath)
  }
}

// Default per-runner named registry file path resolver. Exposed for
// callers (and tests) that want to assert the on-disk location without
// instantiating a registry.
export function defaultNamedWorkspaceRegistryFilePath(runnerRoot: string): string {
  return resolve(join(runnerRoot, DEFAULT_NAMED_WORKSPACE_REGISTRY_FILE))
}
