export interface BuildManifest {
  gitHash: string | null
  builtAt: number
}

export type GitHeadReader = () => string | null
export type BuildClock = () => number

export function buildManifest(readGitHead: GitHeadReader, now: BuildClock): BuildManifest {
  return {
    gitHash: readGitHead(),
    builtAt: now(),
  }
}
