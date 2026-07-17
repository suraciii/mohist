import { createHash } from "node:crypto"

export const REMOTE_IDENTITY_VERSION = "git-remote-url/v1"

export interface RemoteIdentity {
  remoteFingerprint: string
  remoteIdentityVersion: string
}

export function normalizeGitRemote(rawUrl: string | null | undefined): string | null {
  if (!rawUrl?.trim()) return null
  const trimmed = rawUrl.trim()
  const schemeEnd = trimmed.indexOf("://")
  if (schemeEnd > 0) {
    return composeRemote(trimmed.slice(0, schemeEnd + 3).toLowerCase(), trimmed.slice(schemeEnd + 3))
  }
  if (trimmed.startsWith("git@")) return composeScpRemote("ssh://", trimmed.slice(4))
  if (trimmed.toLowerCase().startsWith("ssh:")) return composeScpRemote("ssh://", trimmed.slice(4))
  return null
}

export function fingerprintGitRemote(rawUrl: string | null | undefined): RemoteIdentity | null {
  const canonical = normalizeGitRemote(rawUrl)
  if (!canonical) return null
  return {
    remoteFingerprint: createHash("sha256").update(canonical).digest("hex"),
    remoteIdentityVersion: REMOTE_IDENTITY_VERSION,
  }
}

function composeScpRemote(scheme: string, body: string): string | null {
  const colon = body.indexOf(":")
  const slash = body.indexOf("/")
  if (colon < 0 || (slash >= 0 && slash < colon)) return null
  return composeRemote(scheme, `${body.slice(0, colon)}/${body.slice(colon + 1)}`)
}

function composeRemote(scheme: string, remainder: string): string | null {
  const at = remainder.indexOf("@")
  let authority = at >= 0 ? remainder.slice(at + 1) : remainder
  const slash = authority.indexOf("/")
  let host = slash >= 0 ? authority.slice(0, slash) : authority
  let path = slash >= 0 ? authority.slice(slash) : ""
  if (!host) return null
  if (host.startsWith("[") && host.endsWith("]")) host = host.slice(1, -1)
  const port = host.indexOf(":")
  const portValue = port >= 0 ? host.slice(port + 1) : ""
  if ((scheme === "https://" && portValue === "443") || (scheme === "http://" && portValue === "80") || (scheme === "ssh://" && portValue === "22")) {
    host = host.slice(0, port)
  }
  host = host.toLowerCase()
  const query = path.search(/[?#]/)
  if (query >= 0) path = path.slice(0, query)
  while (path.length > 1 && path.endsWith("/")) path = path.slice(0, -1)
  if (path.length > 1 && path.toLowerCase().endsWith(".git")) path = path.slice(0, -4)
  if (path && !path.startsWith("/")) path = `/${path}`
  return `${scheme}${host}${path}`
}
