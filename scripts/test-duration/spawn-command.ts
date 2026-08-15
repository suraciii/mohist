export interface SpawnCommandOptions {
  readonly platform?: NodeJS.Platform
  readonly nodeExecutable?: string
  readonly npmExecutable?: string
}

export interface SpawnCommand {
  readonly command: string
  readonly args: readonly string[]
}

export function resolveSpawnCommand(
  command: string,
  args: readonly string[],
  options: SpawnCommandOptions = {},
): SpawnCommand {
  const platform = options.platform ?? process.platform
  if (platform !== 'win32' || command !== 'npm') return { command, args }

  const npmExecutable = options.npmExecutable ?? process.env.npm_execpath
  if (!npmExecutable) {
    throw new Error('Windows npm execution requires npm_execpath; invoke the gate through npm run verify')
  }
  return {
    command: options.nodeExecutable ?? process.execPath,
    args: [npmExecutable, ...args],
  }
}
