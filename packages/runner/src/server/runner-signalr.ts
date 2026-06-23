import { existsSync as defaultExistsSync } from "node:fs"
import { resolve, relative, isAbsolute } from "node:path"
import * as signalR from "@microsoft/signalr"
import type { ClientSideConnection } from "@agentclientprotocol/sdk"
import { deleteDirectory, runCommand as defaultRunCommand } from "../system/process.js"
import { WorkspaceManager } from "../runtime/workspace.js"
import type { JsonObject, WorkItem } from "../core/types.js"
import type { ServerConnection } from "./connection.js"

export interface FollowupTarget {
  readonly connection: ClientSideConnection
  readonly sessionId: string
  readonly projectId: string
}

export type FollowupTargetResolver = (workflowRunId: string, sessionName: string) => FollowupTarget | null

export interface ReceiveFollowupPayload {
  workflowRunId: string
  sessionName: string
  text: string
}

export interface RunnerSignalRClientOptions {
  probeTimeoutMs?: number
  onReconnected?: (connectionId: string) => void
  serverConnection?: ServerConnection | null
  followupTargetResolver?: FollowupTargetResolver | null
}

let runGitCommand: typeof defaultRunCommand = defaultRunCommand
let pathExists: typeof defaultExistsSync = defaultExistsSync

export function setRunnerSignalRGitRunnerForTest(runner: typeof defaultRunCommand | null) {
  runGitCommand = runner ?? defaultRunCommand
}

export function setRunnerSignalRExistsCheckerForTest(checker: typeof defaultExistsSync | null) {
  pathExists = checker ?? defaultExistsSync
}

export class RunnerSignalRClient {
  private connection: signalR.HubConnection
  private readonly workspaceManager: WorkspaceManager
  private readonly probeTimeoutMs: number
  private readonly onReconnected: ((connectionId: string) => void) | undefined
  private readonly serverConnection: ServerConnection | null
  private readonly followupTargetResolver: FollowupTargetResolver | null

  constructor(
    serverUrl: string,
    runnerId: string,
    private readonly runnerRoot: string,
    buildGitHash: string | null = null,
    options: RunnerSignalRClientOptions = {},
  ) {
    const baseUrl = serverUrl.replace(/\/$/, "")
    const params = new URLSearchParams()
    params.set("runnerId", runnerId)
    if (buildGitHash) params.set("buildGitHash", buildGitHash)
    this.probeTimeoutMs = options.probeTimeoutMs ?? 5_000
    this.onReconnected = options.onReconnected
    this.connection = new signalR.HubConnectionBuilder()
      .withUrl(`${baseUrl}/hubs/runner?${params.toString()}`)
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .build()
    this.workspaceManager = new WorkspaceManager(runnerRoot)
    this.serverConnection = options.serverConnection ?? null
    this.followupTargetResolver = options.followupTargetResolver ?? null

    this.registerHandlers()
    this.registerLifecycleCallbacks()
  }

  async start(): Promise<void> {
    await this.connection.start()
  }

  async stop(): Promise<void> {
    await this.connection.stop()
  }

  getConnectionId(): string | null {
    return this.connection.connectionId
  }

  async probeLiveness(signal: AbortSignal): Promise<boolean> {
    if (this.connection.state !== signalR.HubConnectionState.Connected) {
      return false
    }
    return await new Promise<boolean>((resolve) => {
      let settled = false
      let timer: ReturnType<typeof setTimeout> | undefined
      const finish = (result: boolean) => {
        if (settled) return
        settled = true
        if (timer) clearTimeout(timer)
        if (signal) signal.removeEventListener("abort", onAbort)
        resolve(result)
      }
      const onAbort = () => finish(false)
      timer = setTimeout(() => finish(false), this.probeTimeoutMs)
      if (signal.aborted) {
        finish(false)
        return
      }
      signal.addEventListener("abort", onAbort, { once: true })
      this.connection
        .invoke("Ping")
        .then(() => finish(true))
        .catch(() => finish(false))
    })
  }

  async forceReconnect(signal: AbortSignal): Promise<void> {
    if (this.connection.state === signalR.HubConnectionState.Disconnected) {
      await this.connection.start()
      this.notifyReconnected()
      return
    }
    try {
      await this.connection.stop()
    } catch {
      // best effort — a half-open socket may throw on stop; the start() below
      // will surface the real state.
    }
    if (signal.aborted) return
    await this.connection.start()
    this.notifyReconnected()
  }

  private registerLifecycleCallbacks(): void {
    this.connection.onreconnected((connectionId) => {
      this.notifyReconnected(connectionId)
    })
  }

  private notifyReconnected(connectionId?: string): void {
    if (!this.onReconnected) return
    const id = typeof connectionId === "string" && connectionId.length > 0
      ? connectionId
      : (this.connection.connectionId ?? "")
    if (id) this.onReconnected(id)
  }

  private registerHandlers(): void {
    this.connection.on("GetDiff", async (query: WorkspaceQuery) => {
      const workspace = resolveWorkspaceQuery(query)
      if (!workspace) return null
      const ac = new AbortController()
      if (!await isGitWorkTree(workspace.workDir, ac.signal)) return null

      const branchExists = await git(workspace.workDir, ["rev-parse", "--verify", `refs/heads/${workspace.head}`], ac.signal)
      if (branchExists.exitCode !== 0) return null

      const [numstat, fullDiff, mergeBaseResult, aheadBehindResult, logResult] = await Promise.all([
        git(workspace.workDir, ["diff", `${workspace.baseBranch}...${workspace.head}`, "--numstat"], ac.signal),
        git(workspace.workDir, ["diff", `${workspace.baseBranch}...${workspace.head}`], ac.signal),
        git(workspace.workDir, ["merge-base", workspace.baseBranch, workspace.head], ac.signal),
        git(workspace.workDir, ["rev-list", "--left-right", "--count", `${workspace.baseBranch}...${workspace.head}`], ac.signal),
        git(workspace.workDir, ["log", `${workspace.baseBranch}...${workspace.head}`, "--format=%H"], ac.signal),
      ])

      const files = parseDiffFiles(numstat.stdout, fullDiff.stdout)
      const mergeBase = mergeBaseResult.exitCode === 0 ? mergeBaseResult.stdout.trim() : workspace.baseBranch
      const commitCount = logResult.exitCode === 0 ? logResult.stdout.trim().split("\n").filter(Boolean).length : 0
      const [ahead, behind] = parseAheadBehind(aheadBehindResult.stdout)

      return {
        base: workspace.baseBranch,
        head: workspace.head,
        mergeBase,
        ahead,
        behind,
        commitCount,
        totalAdditions: files.reduce((s, f) => s + f.additions, 0),
        totalDeletions: files.reduce((s, f) => s + f.deletions, 0),
        files,
      }
    })

    this.connection.on("GetCommits", async (query: WorkspaceQuery) => {
      const workspace = resolveWorkspaceQuery(query)
      if (!workspace) return null
      const ac = new AbortController()
      if (!await isGitWorkTree(workspace.workDir, ac.signal)) return null

      const [logResult, numstat, mergeBaseResult, aheadBehindResult] = await Promise.all([
        git(workspace.workDir, ["log", `${workspace.baseBranch}...${workspace.head}`, "--format=%H\t%h\t%s\t%an\t%ad", "--date=iso"], ac.signal),
        git(workspace.workDir, ["diff", `${workspace.baseBranch}...${workspace.head}`, "--numstat"], ac.signal),
        git(workspace.workDir, ["merge-base", workspace.baseBranch, workspace.head], ac.signal),
        git(workspace.workDir, ["rev-list", "--left-right", "--count", `${workspace.baseBranch}...${workspace.head}`], ac.signal),
      ])

      const commits = parseCommits(logResult.stdout)
      const mergeBase = mergeBaseResult.exitCode === 0 ? mergeBaseResult.stdout.trim() : workspace.baseBranch
      const [ahead, behind] = parseAheadBehind(aheadBehindResult.stdout)
      const fileStats = parseNumstatTotal(numstat.stdout)

      return {
        base: workspace.baseBranch,
        head: workspace.head,
        mergeBase,
        ahead,
        behind,
        filesChanged: fileStats.filesChanged,
        totalAdditions: fileStats.additions,
        totalDeletions: fileStats.deletions,
        commits,
      }
    })

    this.connection.on("GetCommitDiff", async (query: WorkspaceQuery, hash: string) => {
      const workspace = resolveWorkspaceQuery(query)
      if (!workspace) return null

      const ac = new AbortController()
      if (!await isGitWorkTree(workspace.workDir, ac.signal)) return null
      const result = await git(workspace.workDir, ["show", "--format=", "--patch", hash], ac.signal)
      if (result.exitCode !== 0) return null
      return { diff: result.stdout }
    })

    this.connection.on("GetWorkspaceStatus", async (query: WorkspaceQuery) => {
      const workspace = resolveWorkspaceQuery(query)
      if (!workspace) return { exists: false }

      const ac = new AbortController()
      if (!await isGitWorkTree(workspace.workDir, ac.signal)) return { exists: false }

      const branchExists = await git(workspace.workDir, ["rev-parse", "--verify", `refs/heads/${workspace.head}`], ac.signal)
      if (branchExists.exitCode !== 0) return { exists: false }

      const rebaseResult = await git(workspace.workDir, ["rebase", "--show-current-patch"], ac.signal)
      const rebaseInProgress = rebaseResult.exitCode === 0

      let conflictingFiles: string[] = []
      if (rebaseInProgress) {
        const statusResult = await git(workspace.workDir, ["diff", "--name-only", "--diff-filter=U"], ac.signal)
        conflictingFiles = statusResult.stdout.trim().split("\n").filter(Boolean)
      }

      const baseStatus = { exists: true, branch: workspace.head, baseBranch: workspace.baseBranch, rebaseInProgress, conflictingFiles }
      const fetchResult = await git(workspace.workDir, ["fetch", "origin", workspace.baseBranch], ac.signal)
      if (fetchResult.exitCode !== 0) return { ...baseStatus, reason: "fetch_failed" }
      const remoteRef = `origin/${workspace.baseBranch}`
      const aheadBehindResult = await git(workspace.workDir, ["rev-list", "--left-right", "--count", `${remoteRef}...${workspace.head}`], ac.signal)
      const [ahead, behind] = parseAheadBehind(aheadBehindResult.stdout)

      return { ...baseStatus, ahead, behind }
    })

    this.connection.on("GetFileContent", async (query: WorkspaceQuery, path: string) => {
      const workspace = resolveWorkspaceQuery(query)
      if (!workspace) return { base: null, head: null }

      const ac = new AbortController()
      if (!await isGitWorkTree(workspace.workDir, ac.signal)) return { base: null, head: null }

      const [baseResult, headResult] = await Promise.all([
        git(workspace.workDir, ["show", `${workspace.baseBranch}:${path}`], ac.signal),
        git(workspace.workDir, ["show", `${workspace.head}:${path}`], ac.signal),
      ])

      return {
        base: baseResult.exitCode === 0 ? baseResult.stdout : null,
        head: headResult.exitCode === 0 ? headResult.stdout : null,
      }
    })

    this.connection.on("RemoveWorkspace", async (query: WorkspaceQuery) => {
      if (!query?.workspacePath) return removal(false, "missing", query?.workspacePath ?? null, "workspace_missing", "Workspace already removed")
      const workspacePath = resolve(query.workspacePath)
      if (!pathExists(workspacePath)) return removal(false, "missing", workspacePath, "workspace_missing", "Workspace already removed")
      if (!isUnderRunnerRoot(this.runnerRoot, workspacePath)) {
        return removal(false, "failed", workspacePath, "workspace_cleanup_refused", "Workspace path is outside the runner-managed root")
      }
      try {
        await deleteDirectory(workspacePath)
        return removal(true, "removed", workspacePath, null, "Workspace removed")
      } catch (error) {
        return removal(false, "failed", workspacePath, "workspace_cleanup_failed", error instanceof Error ? error.message : String(error))
      }
    })

    this.connection.on("MaterializeWorkspace", async (payload: unknown) => {
      const ac = new AbortController()
      return await this.workspaceManager.materialize(normalizeMaterializePayload(payload), ac.signal)
    })

    this.connection.on("ReceiveFollowup", (payload: ReceiveFollowupPayload | null | undefined) => {
      void this.handleFollowup(payload)
    })
  }

  private async handleFollowup(payload: ReceiveFollowupPayload | null | undefined): Promise<void> {
    if (!payload || typeof payload.text !== "string" || payload.text.length === 0) return
    if (!payload.workflowRunId || !payload.sessionName) return
    if (!this.followupTargetResolver || !this.serverConnection) return

    let target: FollowupTarget | null
    try {
      target = this.followupTargetResolver(payload.workflowRunId, payload.sessionName)
    } catch (error) {
      console.error("followup target resolver threw:", error)
      return
    }
    if (!target) return

    void this.serverConnection.workflowAgentSessionRuntimeEvents(
        target.projectId,
        payload.workflowRunId,
        payload.sessionName,
        {
          workId: null,
          workType: null,
          stage: null,
          runtimeEvents: [
            {
              type: "session.input",
              payload: {
                role: "user",
                text: payload.text,
                kind: "followup",
                sentAt: new Date().toISOString(),
                acpSessionId: target.sessionId,
                source: "followup",
              },
            },
          ],
        },
        new AbortController().signal,
      ).catch((error) => {
        console.error("failed to emit followup session.input event:", error)
      })

    void target.connection
      .prompt({
        sessionId: target.sessionId,
        prompt: [{ type: "text", text: payload.text }],
      })
      .catch((error) => {
        console.error("followup connection.prompt rejected:", error instanceof Error ? error.message : String(error))
      })
  }
}

async function git(workDir: string, args: string[], signal: AbortSignal) {
  return runGitCommand("git", args, workDir, signal)
}

export interface WorkspaceQuery {
  issueNumber?: number
  workspacePath?: string | null
  branch?: string | null
  baseBranch?: string | null
}

export function resolveWorkspaceQuery(query: WorkspaceQuery | null | undefined): { workDir: string; baseBranch: string; head: string } | null {
  if (!query?.workspacePath || !query.baseBranch) return null
  // The legacy `mo/issue-{N}` worktree branch is no longer created by the
  // runner. Callers MUST supply an explicit `branch` (the workspace's HEAD
  // ref, e.g. `mohist/run-${workflowRunId}`). Returning null forces the
  // server review APIs to surface `branch_missing` instead of a phantom
  // `mo/issue-{N}` ref that would never resolve.
  const head = query.branch ?? null
  if (!head) return null
  return { workDir: query.workspacePath, baseBranch: query.baseBranch, head }
}

export function normalizeMaterializePayload(payload: unknown): WorkItem {
  if (!payload || typeof payload !== "object" || Array.isArray(payload)) throw new Error("MaterializeWorkspace payload must be an object")
  const source = payload as Record<string, unknown>
  const workflowRunId = readString(source, "workflowRunId")
  const workId = readString(source, "workId")
  const workType = readString(source, "workType")
  if (!workflowRunId || !workId || !workType) throw new Error("MaterializeWorkspace payload requires workflowRunId, workId, and workType")

  return {
    workflowRunId,
    workId,
    workType,
    stage: readNullableString(source, "stage"),
    title: readNullableString(source, "title"),
    uses: readNullableString(source, "uses"),
    with: parseJsonObject(source["with"], "with"),
    variables: parseJsonObject(source["variables"], "variables"),
    projectId: readNullableString(source, "projectId"),
    issueNumber: readNullableNumber(source, "issueNumber") ?? undefined,
    artifacts: parseJsonObject(source["artifacts"], "artifacts"),
    outputs: parseOutputs(source["outputs"]),
    ownerKind: readNullableString(source, "ownerKind") ?? undefined,
    agentJobId: readNullableString(source, "agentJobId") ?? undefined,
  }
}

function parseOutputs(value: unknown): WorkItem["outputs"] {
  if (value === undefined || value === null || value === "") return null
  const parsed = typeof value === "string" ? JSON.parse(value) : value
  if (!Array.isArray(parsed)) throw new Error("MaterializeWorkspace outputs must be an array")
  return parsed.map((entry) => {
    if (!entry || typeof entry !== "object" || Array.isArray(entry)) throw new Error("MaterializeWorkspace outputs entries must be objects")
    const source = entry as Record<string, unknown>
    const name = source["name"]
    const from = source["from"]
    if (typeof name !== "string" || name.length === 0 || typeof from !== "string" || from.length === 0) {
      throw new Error("MaterializeWorkspace outputs entries require name and from")
    }
    return { name, from }
  })
}

function parseJsonObject(value: unknown, field: string): JsonObject | null {
  if (value === undefined || value === null || value === "") return null
  const parsed = typeof value === "string" ? JSON.parse(value) : value
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) throw new Error(`MaterializeWorkspace ${field} must be an object`)
  return parsed as JsonObject
}

function readString(source: Record<string, unknown>, field: string): string | null {
  const value = source[field]
  return typeof value === "string" && value.length > 0 ? value : null
}

function readNullableString(source: Record<string, unknown>, field: string): string | null | undefined {
  const value = source[field]
  return value === undefined || value === null || typeof value === "string" ? value : undefined
}

function readNullableNumber(source: Record<string, unknown>, field: string): number | null | undefined {
  const value = source[field]
  return value === undefined || value === null || (typeof value === "number" && Number.isFinite(value)) ? value : undefined
}

async function isGitWorkTree(workDir: string, signal: AbortSignal): Promise<boolean> {
  if (!pathExists(workDir)) return false
  const result = await git(workDir, ["rev-parse", "--is-inside-work-tree"], signal)
  return result.exitCode === 0 && result.stdout.trim() === "true"
}

function parseDiffFiles(numstat: string, fullDiff: string): Array<{ file: string; additions: number; deletions: number; diff: string; isBinary: boolean }> {
  const patches = splitDiffByFile(fullDiff)
  const files: Array<{ file: string; additions: number; deletions: number; diff: string; isBinary: boolean }> = []

  for (const line of numstat.split("\n")) {
    if (!line.trim()) continue
    const parts = line.split("\t")
    if (parts.length < 3) continue
    const isBinary = parts[0] === "-" && parts[1] === "-"
    const add = isBinary ? 0 : parseInt(parts[0]) || 0
    const del = isBinary ? 0 : parseInt(parts[1]) || 0
    files.push({ file: parts[2], additions: add, deletions: del, diff: patches[parts[2]] ?? "", isBinary })
  }

  return files
}

function splitDiffByFile(diff: string): Record<string, string> {
  const result: Record<string, string> = {}
  if (!diff.trim()) return result

  let currentPath: string | null = null
  const current: string[] = []

  for (const line of diff.split("\n")) {
    if (line.startsWith("diff --git ")) {
      flush()
      const parts = line.split(" ").filter(Boolean)
      currentPath = parts.length >= 4 && parts[3].startsWith("b/") ? parts[3].slice(2) : null
    }
    current.push(line)
  }
  flush()

  function flush() {
    if (currentPath && current.length > 0) result[currentPath] = current.join("\n") + "\n"
    current.length = 0
  }

  return result
}

function parseCommits(log: string): Array<{ hash: string; shortHash: string; message: string; author: string; date: string; files: string[] }> {
  if (!log.trim()) return []
  return log.split("\n").filter(Boolean).map(line => {
    const parts = line.split("\t")
    if (parts.length < 5) return null
    return { hash: parts[0], shortHash: parts[1], message: parts[2], author: parts[3], date: parts[4], files: [] as string[] }
  }).filter(Boolean) as Array<{ hash: string; shortHash: string; message: string; author: string; date: string; files: string[] }>
}

function parseAheadBehind(output: string): [number, number] {
  const parts = output.trim().split("\t")
  if (parts.length === 2) {
    const behind = parseInt(parts[0]) || 0
    const ahead = parseInt(parts[1]) || 0
    return [ahead, behind]
  }
  return [0, 0]
}

function parseNumstatTotal(numstat: string): { filesChanged: number; additions: number; deletions: number } {
  let filesChanged = 0
  let additions = 0
  let deletions = 0
  for (const line of numstat.split("\n")) {
    if (!line.trim()) continue
    const parts = line.split("\t")
    if (parts.length < 3) continue
    const isBinary = parts[0] === "-" && parts[1] === "-"
    additions += isBinary ? 0 : parseInt(parts[0]) || 0
    deletions += isBinary ? 0 : parseInt(parts[1]) || 0
    filesChanged++
  }
  return { filesChanged, additions, deletions }
}

export function isUnderRunnerRoot(root: string, candidate: string): boolean {
  const rootPath = resolve(root)
  const target = resolve(candidate)
  const rel = relative(rootPath, target)
  return rel === "" || (!rel.startsWith("..") && !isAbsolute(rel))
}

function removal(removed: boolean, status: string, path: string | null, reason: string | null, message: string) {
  return { removed, status, path, reason, message }
}
