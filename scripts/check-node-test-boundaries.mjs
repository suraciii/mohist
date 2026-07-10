import { existsSync, readdirSync, readFileSync } from 'node:fs'
import { dirname, relative, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import ts from 'typescript'

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const rule = 'no-direct-shared-state-mutation'
const protectedGlobalNames = new Set(['window', 'document', 'navigator'])
const protectedPrototypeNames = new Set(['Element', 'HTMLElement'])

function unwrapExpression(expression) {
  let current = expression
  while (true) {
    if (ts.isParenthesizedExpression(current)) {
      current = current.expression
      continue
    }
    if (ts.isAsExpression(current)) {
      current = current.expression
      continue
    }
    if (ts.isTypeAssertionExpression(current)) {
      current = current.expression
      continue
    }
    if (ts.isNonNullExpression(current)) {
      current = current.expression
      continue
    }
    if (ts.isSatisfiesExpression(current)) {
      current = current.expression
      continue
    }
    if (ts.isPartiallyEmittedExpression(current)) {
      current = current.expression
      continue
    }
    return current
  }
}

function getMember(expression) {
  const current = unwrapExpression(expression)
  if (ts.isPropertyAccessExpression(current)) {
    return { object: unwrapExpression(current.expression), name: current.name.text }
  }
  if (ts.isElementAccessExpression(current)) {
    const argument = unwrapExpression(current.argumentExpression)
    const name = ts.isStringLiteral(argument) || ts.isNoSubstitutionTemplateLiteral(argument)
      ? argument.text
      : null
    return { object: unwrapExpression(current.expression), name }
  }
  return null
}

function isIdentifierNamed(expression, name) {
  const current = unwrapExpression(expression)
  return ts.isIdentifier(current) && current.text === name
}

function isGlobalBuiltin(expression, name) {
  if (isIdentifierNamed(expression, name)) return true
  const member = getMember(expression)
  return member !== null && member.name === name && isIdentifierNamed(member.object, 'globalThis')
}

function findProtectedRoot(expression) {
  const current = unwrapExpression(expression)
  if (ts.isIdentifier(current) && protectedGlobalNames.has(current.text)) return current.text

  const member = getMember(current)
  if (member === null) return null

  if (member.name !== null && isIdentifierNamed(member.object, 'globalThis') && protectedGlobalNames.has(member.name)) {
    return `globalThis.${member.name}`
  }

  if (member.name === 'prototype') {
    for (const prototypeName of protectedPrototypeNames) {
      if (isIdentifierNamed(member.object, prototypeName)) return `${prototypeName}.prototype`
    }
  }

  return findProtectedRoot(member.object)
}

function findMutationCallTarget(call) {
  const member = getMember(call.expression)
  if (member === null || member.name === null || call.arguments.length === 0) return null

  const isObjectDefineProperty = isGlobalBuiltin(member.object, 'Object') && member.name === 'defineProperty'
  const isReflectMutation = isGlobalBuiltin(member.object, 'Reflect')
    && (member.name === 'defineProperty' || member.name === 'deleteProperty')

  return isObjectDefineProperty || isReflectMutation
    ? findProtectedRoot(call.arguments[0])
    : null
}

function scriptKindFor(filePath) {
  return filePath.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS
}

function createViolation(filePath, sourceFile, node, root) {
  const position = sourceFile.getLineAndCharacterOfPosition(node.getStart(sourceFile))
  return {
    filePath,
    line: position.line + 1,
    column: position.character + 1,
    rule,
    root,
    fix: 'Use setScopedProperty/setScopedValue from tests/support/scoped-property.ts, or vi.stubGlobal for a whole global.',
  }
}

export function scanSourceFile(filePath, sourceText = readFileSync(filePath, 'utf8')) {
  const sourceFile = ts.createSourceFile(filePath, sourceText, ts.ScriptTarget.Latest, true, scriptKindFor(filePath))
  const violations = []

  function visit(node) {
    if (ts.isBinaryExpression(node) && ts.isAssignmentOperator(node.operatorToken.kind)) {
      const root = findProtectedRoot(node.left)
      if (root !== null) violations.push(createViolation(filePath, sourceFile, node.left, root))
    }

    if (ts.isCallExpression(node)) {
      const root = findMutationCallTarget(node)
      if (root !== null) violations.push(createViolation(filePath, sourceFile, node.arguments[0], root))
    }

    ts.forEachChild(node, visit)
  }

  visit(sourceFile)
  return violations
}

function walkFiles(directory) {
  if (!existsSync(directory)) return []

  return readdirSync(directory, { withFileTypes: true })
    .sort((left, right) => left.name.localeCompare(right.name))
    .flatMap((entry) => {
      const entryPath = resolve(directory, entry.name)
      if (entry.isDirectory()) return walkFiles(entryPath)
      return entry.isFile() ? [entryPath] : []
    })
}

function isExcludedWebFile(relativePath) {
  return relativePath === 'tests/setup.ts' || relativePath === 'tests/support/scoped-property.ts'
}

function isActiveWebVitestFile(relativePath) {
  if (isExcludedWebFile(relativePath)) return false
  if (relativePath.startsWith('src/')) return /\.test\.tsx?$/.test(relativePath)
  if (!relativePath.startsWith('tests/')) return false
  if (relativePath.startsWith('tests/a11y/') || relativePath.startsWith('tests/e2e/')) return false
  return /\.spec\.tsx$/.test(relativePath)
}

export function collectWebVitestFiles(webRoot = resolve(repositoryRoot, 'packages/web')) {
  return [
    ...walkFiles(resolve(webRoot, 'src')),
    ...walkFiles(resolve(webRoot, 'tests')),
  ].filter((filePath) => isActiveWebVitestFile(relative(webRoot, filePath).replaceAll('\\', '/')))
}

export function checkWebTestBoundaries(webRoot = resolve(repositoryRoot, 'packages/web')) {
  const files = collectWebVitestFiles(webRoot)
  return {
    files,
    violations: files.flatMap((filePath) => scanSourceFile(filePath)),
  }
}

function assertSelfTest(condition, message) {
  if (!condition) throw new Error(`Boundary checker self-test failed: ${message}`)
}

function runSelfTest() {
  const fixtureWebRoot = resolve(repositoryRoot, 'scripts/fixtures/node-test-boundaries/web')
  const { files, violations } = checkWebTestBoundaries(fixtureWebRoot)
  const relativeFiles = files.map((filePath) => relative(fixtureWebRoot, filePath).replaceAll('\\', '/'))
  const expectedFiles = [
    'src/allowed-local.test.tsx',
    'src/direct-assignment.test.tsx',
    'src/prototype-calls.test.tsx',
    'tests/active.spec.tsx',
  ]

  assertSelfTest(
    JSON.stringify(relativeFiles) === JSON.stringify(expectedFiles),
    `expected active files ${expectedFiles.join(', ')}, got ${relativeFiles.join(', ')}`,
  )

  const actualRoots = Object.groupBy(
    violations,
    (violation) => relative(fixtureWebRoot, violation.filePath).replaceAll('\\', '/'),
  )
  const expectedRoots = {
    'src/direct-assignment.test.tsx': ['window', 'document', 'navigator', 'globalThis.window'],
    'src/prototype-calls.test.tsx': ['Element.prototype', 'HTMLElement.prototype', 'document', 'globalThis.navigator'],
    'tests/active.spec.tsx': ['globalThis.document'],
  }

  assertSelfTest(
    JSON.stringify(Object.fromEntries(Object.entries(actualRoots).map(([file, items]) => [file, items.map((item) => item.root)])))
      === JSON.stringify(expectedRoots),
    'did not report the expected protected mutation roots',
  )

  assertSelfTest(
    violations.every((violation) => violation.rule === rule && violation.line > 0 && violation.column > 0),
    'diagnostics are missing rule or source location',
  )
  console.log('node test boundary checker self-test passed')
}

function printViolations(violations) {
  for (const violation of violations) {
    const filePath = relative(repositoryRoot, violation.filePath).replaceAll('\\', '/')
    console.error(
      `${filePath}:${violation.line}:${violation.column} ${violation.rule}: directly mutates ${violation.root}. ${violation.fix}`,
    )
  }
}

function parseArguments(args) {
  let scope = null
  let selfTest = false

  for (let index = 0; index < args.length; index += 1) {
    const argument = args[index]
    if (argument === '--self-test') {
      selfTest = true
      continue
    }
    if (argument === '--scope') {
      scope = args[index + 1]
      index += 1
      continue
    }
    throw new Error(`Unknown argument: ${argument}`)
  }

  if (selfTest) return { selfTest, scope }
  if (scope !== 'web' && scope !== 'runner') {
    throw new Error('Usage: node scripts/check-node-test-boundaries.mjs --scope web|runner')
  }
  return { selfTest, scope }
}

function main() {
  const { selfTest, scope } = parseArguments(process.argv.slice(2))
  if (selfTest) {
    runSelfTest()
    return
  }

  if (scope === 'runner') {
    console.log('node test boundaries: no Runner rules are enabled')
    return
  }

  const { files, violations } = checkWebTestBoundaries()
  if (violations.length === 0) {
    console.log(`node test boundaries: checked ${files.length} active Web Vitest files`)
    return
  }

  printViolations(violations)
  console.error(`${violations.length} node test boundary violation(s) found in ${files.length} active Web Vitest files.`)
  process.exitCode = 1
}

if (process.argv[1] !== undefined && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    main()
  } catch (error) {
    console.error(error instanceof Error ? error.message : String(error))
    process.exitCode = 1
  }
}
