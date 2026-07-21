export function parseGitHubRepository(repositoryUrl: string): string | null {
  const trimmed = repositoryUrl.trim()
  if (!trimmed) return null

  const scpBody = trimmed.toLowerCase().startsWith("ssh:") ? trimmed.slice("ssh:".length) : trimmed
  if (!scpBody.includes("://")) {
    const scp = /^(?:[^@]+@)?([^:/]+):(.+)$/.exec(scpBody)
    if (scp) return toRepositorySelector(scp[1]!, scp[2]!)
  }

  try {
    const url = new URL(trimmed)
    return toRepositorySelector(url.hostname, url.pathname)
  } catch {
    return null
  }
}

function toRepositorySelector(host: string, rawPath: string): string | null {
  const parts = rawPath.replace(/^\/+|\/+$/g, "").replace(/\.git$/i, "").split("/")
  if (parts.length !== 2 || parts.some((part) => !part)) return null
  return `${host.toLowerCase()}/${parts.join("/")}`
}
