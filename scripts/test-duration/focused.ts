// Resolves the xUnit v3 in-process apphost command for a focused test run.
//
// The MTP test host ignores `dotnet test --filter` (it reports MTP0001 and may
// run the whole assembly). The supported focused path is the compiled apphost
// binary with its own simple filters: `-class`, `-method`, `-namespace`. This
// module turns that flow into a resolvable, asserted command so a caller never
// has to hand-assemble it and cannot accidentally fall back to `--filter`.

export interface ApphostInput {
  readonly csprojXml: string
  readonly assemblyName?: string
  readonly configuration?: string
  readonly projectDir?: string
}

export interface FocusedRequest extends ApphostInput {
  readonly className: string
}

export interface FocusedCommand {
  readonly apphost: string
  readonly args: readonly string[]
  readonly verify: readonly string[]
  readonly report: (trxFile: string) => readonly string[]
}

export interface DiscoveryCommand {
  readonly apphost: string
  readonly args: readonly string[]
}

export function parseTargetFramework(csprojXml: string): string | undefined {
  const single = csprojXml.match(/<TargetFramework>\s*([^<]+?)\s*<\/TargetFramework>/)
  if (single) return single[1]
  const frameworks = csprojXml.match(/<TargetFrameworks>\s*([^<]+?)\s*<\/TargetFrameworks>/)
  if (frameworks) return frameworks[1].split(';')[0]
  return undefined
}

export function parseAssemblyName(csprojXml: string): string | undefined {
  const explicit = csprojXml.match(/<AssemblyName>\s*([^<]+?)\s*<\/AssemblyName>/)
  if (explicit) return explicit[1]
  return undefined
}

export function resolveApphostPath(request: ApphostInput): string {
  const tfm = parseTargetFramework(request.csprojXml) ?? 'net11.0'
  const assembly = request.assemblyName ?? parseAssemblyName(request.csprojXml) ?? 'Unknown'
  const configuration = request.configuration ?? 'Debug'
  const dir = request.projectDir
    ? `${request.projectDir}/bin/${configuration}/${tfm}`
    : `bin/${configuration}/${tfm}`
  return `${dir}/${assembly}`
}

export function resolveFocusedCommand(request: FocusedRequest): FocusedCommand {
  const apphost = resolveApphostPath(request)
  const base = ['-noColor', '-noLogo', '-class', request.className]
  return {
    apphost,
    args: base,
    verify: ['-list', 'classes', '-noColor', '-noLogo'],
    report: (trxFile: string) => [...base, '-trx', trxFile],
  }
}

export function resolveDiscoveryCommand(request: ApphostInput): DiscoveryCommand {
  return {
    apphost: resolveApphostPath(request),
    args: ['-list', 'tests', '-noColor', '-noLogo'],
  }
}
