import type { ActionContext, ActionResult } from "../core/types.js"
import { numberInput, stringAt, stringInput } from "../core/json.js"

const POLL_INTERVAL_MS = 30_000
const DEFAULT_TIMEOUT_MINUTES = 30
const API_RETRY_MAX = 3
const API_RETRY_BASE_MS = 1_000

export async function githubPrAction(context: ActionContext): Promise<ActionResult> {
  const token = stringInput(context.with, "token")
  if (!token) return { status: "failure", message: "GitHub PR action requires 'token'" }

  const source = stringInput(context.with, "source")
  if (!source) return { status: "failure", message: "GitHub PR action requires 'source' (head branch)" }

  const target = stringInput(context.with, "target") ?? "main"
  const title = stringInput(context.with, "title") ?? ""
  const prBody = stringInput(context.with, "body") ?? `Closes via Mohist`

  const { owner, repo } = resolveOwnerRepo(context)
  if (!owner || !repo) {
    return { status: "failure", message: "Cannot determine GitHub owner/repo. Set github.owner and github.repo variables or configure repository.remote." }
  }

  const pr = await createPR(token, owner, repo, source, target, title, prBody)
  if ("error" in pr) return { status: "failure", message: `Failed to create PR: ${pr.error}` }

  const timeoutMs = (numberInput(context.with, "timeout") ?? DEFAULT_TIMEOUT_MINUTES) * 60_000
  const result = await waitForMerge(token, owner, repo, pr.number, timeoutMs, context.signal)

  const output = JSON.stringify({
    kind: "github-pr",
    number: pr.number,
    htmlUrl: pr.htmlUrl,
    source,
    target,
    merged: result.merged,
    message: result.message,
    mergeCommitSha: result.mergeCommitSha,
  })

  return result.merged
    ? { status: "success", message: `PR #${pr.number} merged`, output }
    : { status: "failure", message: `PR #${pr.number}: ${result.message}`, output }
}

function resolveOwnerRepo(context: ActionContext): { owner: string | undefined; repo: string | undefined } {
  const owner = stringInput(context.with, "owner") ?? stringAt(context.variables, ["github", "owner"])
  const repo = stringInput(context.with, "repo") ?? stringAt(context.variables, ["github", "repo"])

  if (owner && repo) return { owner, repo }

  const remote = stringAt(context.variables, ["repository", "remote"])
  if (remote) {
    const parsed = parseGitHubRemote(remote)
    if (parsed) return { owner: owner ?? parsed.owner, repo: repo ?? parsed.repo }
  }

  return { owner, repo }
}

function parseGitHubRemote(remote: string): { owner: string; repo: string } | null {
  const m = remote.match(/github\.com[/:]([\w.-]+)\/([\w.-]+?)(?:\.git)?$/)
  return m ? { owner: m[1], repo: m[2] } : null
}

async function createPR(
  token: string,
  owner: string,
  repo: string,
  head: string,
  base: string,
  title: string,
  body: string,
): Promise<{ number: number; htmlUrl: string } | { error: string }> {
  const res = await fetch(`https://api.github.com/repos/${owner}/${repo}/pulls`, {
    method: "POST",
    headers: {
      Authorization: `Bearer ${token}`,
      "Content-Type": "application/json",
      Accept: "application/vnd.github+json",
      "X-GitHub-Api-Version": "2022-11-28",
    },
    body: JSON.stringify({ head, base, title, body }),
  })

  if (res.ok) {
    const data = await res.json() as { number: number; html_url: string }
    return { number: data.number, htmlUrl: data.html_url }
  }

  if (res.status === 422) {
    const existing = await findExistingPR(token, owner, repo, head, base)
    if (existing) return existing
  }

  const err = await res.text()
  return { error: `HTTP ${res.status}: ${err.slice(0, 500)}` }
}

async function findExistingPR(
  token: string,
  owner: string,
  repo: string,
  head: string,
  base: string,
): Promise<{ number: number; htmlUrl: string } | null> {
  const params = new URLSearchParams({ head: `${owner}:${head}`, base, state: "open", per_page: "1" })
  const res = await fetch(`https://api.github.com/repos/${owner}/${repo}/pulls?${params}`, {
    headers: {
      Authorization: `Bearer ${token}`,
      Accept: "application/vnd.github+json",
      "X-GitHub-Api-Version": "2022-11-28",
    },
  })

  if (!res.ok) return null
  const data = await res.json() as { number: number; html_url: string }[]
  return data.length > 0 ? { number: data[0].number, htmlUrl: data[0].html_url } : null
}

async function waitForMerge(
  token: string,
  owner: string,
  repo: string,
  prNumber: number,
  timeoutMs: number,
  signal: AbortSignal,
): Promise<{ merged: boolean; message: string; mergeCommitSha?: string }> {
  const deadline = Date.now() + timeoutMs

  while (Date.now() < deadline) {
    if (signal.aborted) return { merged: false, message: "Aborted" }

    const res = await fetchWithRetry(token, owner, repo, prNumber)
    if (!res) return { merged: false, message: "API unreachable after retries" }

    if (!res.ok) {
      return { merged: false, message: `API error: HTTP ${res.status}` }
    }

    const pr = await res.json() as GHPR
    if (pr.merged) {
      return { merged: true, message: "Merged", mergeCommitSha: pr.merge_commit_sha }
    }

    if (pr.state === "closed" && !pr.merged) {
      return { merged: false, message: "PR closed without merge" }
    }

    await sleep(Math.min(POLL_INTERVAL_MS, deadline - Date.now()))
  }

  return { merged: false, message: `Timed out after ${timeoutMs / 60_000} minutes` }
}

async function fetchWithRetry(
  token: string,
  owner: string,
  repo: string,
  prNumber: number,
): Promise<Response | null> {
  for (let attempt = 0; attempt <= API_RETRY_MAX; attempt++) {
    try {
      const res = await fetch(`https://api.github.com/repos/${owner}/${repo}/pulls/${prNumber}`, {
        headers: {
          Authorization: `Bearer ${token}`,
          Accept: "application/vnd.github+json",
          "X-GitHub-Api-Version": "2022-11-28",
        },
      })

      if (res.ok || res.status < 500) return res

      if (attempt < API_RETRY_MAX) {
        await sleep(API_RETRY_BASE_MS * 2 ** attempt)
      } else {
        return res
      }
    } catch {
      if (attempt < API_RETRY_MAX) {
        await sleep(API_RETRY_BASE_MS * 2 ** attempt)
      } else {
        return null
      }
    }
  }

  return null
}

function sleep(ms: number): Promise<void> {
  if (ms <= 0) return Promise.resolve()
  return new Promise((resolve) => setTimeout(resolve, ms))
}

interface GHPR {
  number: number
  html_url: string
  state: string
  merged: boolean
  merge_commit_sha?: string
}
