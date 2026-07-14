import { readdirSync, readFileSync } from 'node:fs'
import { basename, dirname, join, relative, resolve, sep } from 'node:path'

const sourceRoot = resolve(import.meta.dirname, '../packages/web/src')
const layers = ['shared', 'entities', 'features', 'widgets', 'pages', 'app']
const layerRank = new Map(layers.map((layer, index) => [layer, index]))

function isTypeScriptModule(filePath) {
  return /\.(?:ts|tsx)$/.test(filePath)
}

function isTestModule(filePath) {
  return /(?:\.(?:test|spec)|TestSupport|TestUtils)\.(?:ts|tsx)$/.test(filePath)
    || basename(filePath).startsWith('_')
}

function walk(directory) {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const filePath = join(directory, entry.name)
    if (entry.isDirectory()) return walk(filePath)
    return isTypeScriptModule(filePath) ? [filePath] : []
  })
}

const allSourceFiles = walk(sourceRoot)
const sourceFiles = allSourceFiles.filter((filePath) => !isTestModule(filePath))
const knownFiles = new Set(allSourceFiles)

function resolveLocalImport(sourceFile, specifier) {
  const base = specifier.startsWith('@/')
    ? join(sourceRoot, specifier.slice(2))
    : specifier.startsWith('.')
      ? resolve(dirname(sourceFile), specifier)
      : null
  if (!base) return null
  return [base, `${base}.ts`, `${base}.tsx`, join(base, 'index.ts'), join(base, 'index.tsx')]
    .find((candidate) => knownFiles.has(candidate)) ?? null
}

function sourceInfo(filePath) {
  const segments = relative(sourceRoot, filePath).split(sep)
  const [layer, slice] = segments
  return { layer, slice: layer === 'shared' || layer === 'app' ? null : slice, segments }
}

function isEntityCrossApi(info) {
  return info.layer === 'entities' && info.segments[2] === '@x'
}

function isPublicApi(filePath, info) {
  return info.slice !== null
    && info.segments.length === 3
    && (basename(filePath) === 'index.ts' || basename(filePath) === 'index.tsx')
}

const violations = []
const moduleGraph = new Map(sourceFiles.map((filePath) => [filePath, new Set()]))
for (const sourceFile of sourceFiles) {
  const source = readFileSync(sourceFile, 'utf8')
  const sourceModule = sourceInfo(sourceFile)
  const sourceRank = layerRank.get(sourceModule.layer)
  if (sourceRank === undefined) continue

  // The `from` branch intentionally covers static imports and re-exports.
  const imports = /(?:\bfrom\s*|\bimport\s*\()(['"])([^'"\n]+)\1/g
  for (let match; (match = imports.exec(source));) {
    const targetFile = resolveLocalImport(sourceFile, match[2])
    if (!targetFile) continue

    const line = source.slice(0, match.index).split('\n').length
    const location = `${relative(sourceRoot, sourceFile)}:${line}`
    if (isTestModule(targetFile)) {
      violations.push(`${location} imports test support via ${match[2]}`)
      continue
    }

    const targetModule = sourceInfo(targetFile)
    const targetRank = layerRank.get(targetModule.layer)
    if (targetRank === undefined) continue
    if (moduleGraph.has(targetFile)) moduleGraph.get(sourceFile).add(targetFile)

    if (targetRank > sourceRank) {
      violations.push(`${location} imports higher layer ${targetModule.layer} via ${match[2]}`)
      continue
    }

    const crossesSlice = sourceModule.layer === targetModule.layer
      && sourceModule.slice !== null
      && targetModule.slice !== null
      && sourceModule.slice !== targetModule.slice

    const isAllowedEntityCrossApi = sourceModule.layer === 'entities' && isEntityCrossApi(targetModule)
    if (crossesSlice && !isAllowedEntityCrossApi) {
      violations.push(`${location} imports sibling slice ${targetModule.layer}/${targetModule.slice} via ${match[2]}`)
      continue
    }

    const crossesToSlicedLayer = sourceModule.layer !== targetModule.layer && targetModule.slice !== null
    if (crossesToSlicedLayer && !isPublicApi(targetFile, targetModule) && !isAllowedEntityCrossApi) {
      violations.push(`${location} imports internal ${targetModule.layer}/${targetModule.slice} module via ${match[2]}`)
    }
  }
}

let traversalIndex = 0
const traversalIndexes = new Map()
const lowLinks = new Map()
const activeModules = new Set()
const traversalStack = []

function findCycles(modulePath) {
  traversalIndexes.set(modulePath, traversalIndex)
  lowLinks.set(modulePath, traversalIndex)
  traversalIndex += 1
  traversalStack.push(modulePath)
  activeModules.add(modulePath)

  for (const dependency of moduleGraph.get(modulePath)) {
    if (!traversalIndexes.has(dependency)) {
      findCycles(dependency)
      lowLinks.set(modulePath, Math.min(lowLinks.get(modulePath), lowLinks.get(dependency)))
    } else if (activeModules.has(dependency)) {
      lowLinks.set(modulePath, Math.min(lowLinks.get(modulePath), traversalIndexes.get(dependency)))
    }
  }

  if (lowLinks.get(modulePath) !== traversalIndexes.get(modulePath)) return

  const component = []
  let member
  do {
    member = traversalStack.pop()
    activeModules.delete(member)
    component.push(member)
  } while (member !== modulePath)

  if (component.length > 1) {
    const names = component.map((filePath) => relative(sourceRoot, filePath)).sort()
    violations.push(`circular dependency: ${names.join(' -> ')}`)
  }
}

for (const sourceFile of sourceFiles) {
  if (!traversalIndexes.has(sourceFile)) findCycles(sourceFile)
}

if (violations.length > 0) {
  console.error('Web FSD boundary violations:')
  for (const violation of violations) console.error(`- ${violation}`)
  process.exitCode = 1
} else {
  console.log(`Web FSD boundaries: checked ${sourceFiles.length} production modules`)
}
