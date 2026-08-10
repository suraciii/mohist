export interface BuildManifest {
  gitHash: string | null
  builtAt: number
  component?: string
  version?: string
  sourceRevision?: string
  treeHash?: string
  artifactDigest?: string
  releaseId?: string
  generation?: number
  runnerId?: string
}

export type GitHeadReader = () => string | null
export type BuildClock = () => number

export function buildManifest(
  readGitHead: GitHeadReader,
  now: BuildClock,
  identity?: Omit<BuildManifest, "gitHash" | "builtAt">,
): BuildManifest {
  const base: BuildManifest = {
    gitHash: readGitHead(),
    builtAt: now(),
  }
  return identity ? { ...base, ...identity } : base
}
