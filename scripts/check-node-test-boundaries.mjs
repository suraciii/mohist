import { existsSync, readdirSync, readFileSync } from 'node:fs'
import { dirname, relative, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import ts from 'typescript'

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const sharedStateMutationRule = 'no-direct-shared-state-mutation'
const timerPromiseRule = 'no-real-time-sleep'
const runnerHostWaitForRule = 'no-runner-host-wait-for'
const elapsedTimeAssertionRule = 'no-elapsed-time-assertion'
const jsdomGeometryRule = 'no-jsdom-page-geometry-assertion'
const protectedGlobalNames = new Set(['window', 'document', 'navigator'])
const protectedPrototypeNames = new Set(['Element', 'HTMLElement'])
const pageGeometryProperties = ['scrollWidth', 'clientWidth', 'offsetWidth']
const boundingRectProperties = new Set(['bottom', 'height', 'left', 'right', 'top', 'width', 'x', 'y'])

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

function createViolation(filePath, sourceFile, node, { rule, root, description, fix }) {
  const position = sourceFile.getLineAndCharacterOfPosition(node.getStart(sourceFile))
  return {
    filePath,
    line: position.line + 1,
    column: position.character + 1,
    rule,
    root,
    description,
    fix,
  }
}

function createSharedStateMutationViolation(filePath, sourceFile, node, root) {
  return createViolation(filePath, sourceFile, node, {
    rule: sharedStateMutationRule,
    root,
    description: `directly mutates ${root}`,
    fix: 'Use setScopedProperty/setScopedValue from tests/support/scoped-property.ts, or vi.stubGlobal for a whole global.',
  })
}

function createRunnerViolation(filePath, sourceFile, node, rule, description, fix) {
  return createViolation(filePath, sourceFile, node, { rule, description, fix })
}

function createJsdomGeometryViolation(filePath, sourceFile, node) {
  return createViolation(filePath, sourceFile, node, {
    rule: jsdomGeometryRule,
    description: 'asserts page-fit geometry that jsdom does not measure',
    fix: 'Keep semantic structure assertions here and move viewport geometry coverage to Playwright.',
  })
}

function isComparisonOperator(kind) {
  return new Set([
    ts.SyntaxKind.EqualsEqualsToken,
    ts.SyntaxKind.EqualsEqualsEqualsToken,
    ts.SyntaxKind.ExclamationEqualsToken,
    ts.SyntaxKind.ExclamationEqualsEqualsToken,
    ts.SyntaxKind.LessThanToken,
    ts.SyntaxKind.LessThanEqualsToken,
    ts.SyntaxKind.GreaterThanToken,
    ts.SyntaxKind.GreaterThanEqualsToken,
  ]).has(kind)
}

function containsPageGeometryProperty(expression, propertyName, bindings = []) {
  return forEachDescendant(expression, (node) => {
    if (!ts.isExpression(node)) return false
    if (getMember(node)?.name === propertyName) return true
    return ts.isIdentifier(node) && resolveLexicalBinding(bindings, node.text, node)?.[propertyName] === true
  })
}

function isDocumentElement(expression) {
  const member = getMember(expression)
  if (member === null || member.name !== 'documentElement') return false
  if (isIdentifierNamed(member.object, 'document')) return true
  const globalDocument = getMember(member.object)
  return globalDocument !== null && globalDocument.name === 'document' && isIdentifierNamed(globalDocument.object, 'globalThis')
}

function isVisualViewport(expression) {
  if (isIdentifierNamed(expression, 'visualViewport')) return true
  const member = getMember(expression)
  return member !== null && member.name === 'visualViewport'
    && (isIdentifierNamed(member.object, 'window') || isIdentifierNamed(member.object, 'globalThis'))
}

function isWindow(expression) {
  return isIdentifierNamed(expression, 'window') || isIdentifierNamed(expression, 'globalThis')
}

function isViewportSize(expression) {
  const member = getMember(expression)
  if (member === null) return false
  if ((member.name === 'innerWidth' || member.name === 'innerHeight') && isWindow(member.object)) return true
  if ((member.name === 'clientWidth' || member.name === 'clientHeight') && isDocumentElement(member.object)) return true
  return (member.name === 'width' || member.name === 'height') && isVisualViewport(member.object)
}

function isBoundingRectCall(expression) {
  if (!ts.isCallExpression(expression)) return false
  return getMember(expression.expression)?.name === 'getBoundingClientRect'
}

function propertyNameText(name) {
  return ts.isIdentifier(name) || ts.isStringLiteral(name) || ts.isNoSubstitutionTemplateLiteral(name)
    ? name.text
    : undefined
}

function isDestructuredViewportSize(binding) {
  const { propertyName, initializer } = binding
  if (propertyName === undefined) return false
  if ((propertyName === 'innerWidth' || propertyName === 'innerHeight') && isWindow(initializer)) return true
  if ((propertyName === 'clientWidth' || propertyName === 'clientHeight') && isDocumentElement(initializer)) return true
  return (propertyName === 'width' || propertyName === 'height') && isVisualViewport(initializer)
}

function isDestructuredBoundingRectProperty(binding, bindings) {
  return binding.propertyName !== undefined
    && boundingRectProperties.has(binding.propertyName)
    && containsBoundingRect(binding.initializer, bindings)
}

function geometryBindings(sourceFile) {
  const bindings = []

  function addBinding(name, declaration, initializer, propertyName) {
    bindings.push({
      name,
      declaration,
      initializer,
      propertyName,
      scope: lexicalScope(declaration),
      hoisted: false,
      viewport: false,
      boundingRect: false,
      scrollWidth: false,
      clientWidth: false,
      offsetWidth: false,
    })
  }

  function visit(node) {
    if (ts.isVariableDeclaration(node) && node.initializer !== undefined) {
      if (ts.isIdentifier(node.name)) {
        addBinding(node.name.text, node, node.initializer, undefined)
      }
      if (ts.isObjectBindingPattern(node.name)) {
        for (const element of node.name.elements) {
          if (!ts.isIdentifier(element.name)) continue
          addBinding(element.name.text, element, node.initializer, propertyNameText(element.propertyName ?? element.name))
        }
      }
    }
    ts.forEachChild(node, visit)
  }

  visit(sourceFile)

  let changed = true
  while (changed) {
    changed = false
    for (const binding of bindings) {
      if (!binding.viewport && (containsViewportSize(binding.initializer, bindings) || isDestructuredViewportSize(binding))) {
        binding.viewport = true
        changed = true
      }
      if (!binding.boundingRect && (binding.propertyName === undefined
        ? containsBoundingRect(binding.initializer, bindings)
        : isDestructuredBoundingRectProperty(binding, bindings))) {
        binding.boundingRect = true
        changed = true
      }
      for (const propertyName of pageGeometryProperties) {
        if (!binding[propertyName] && (binding.propertyName === propertyName
          || containsPageGeometryProperty(binding.initializer, propertyName, bindings))) {
          binding[propertyName] = true
          changed = true
        }
      }
    }
  }

  return bindings
}

function containsViewportSize(expression, bindings = []) {
  return forEachDescendant(expression, (node) => {
    if (!ts.isExpression(node)) return false
    if (isViewportSize(node)) return true
    return ts.isIdentifier(node) && resolveLexicalBinding(bindings, node.text, node)?.viewport === true
  })
}

function containsBoundingRect(expression, bindings = []) {
  return forEachDescendant(expression, (node) => {
    if (!ts.isExpression(node)) return false
    if (isBoundingRectCall(node)) return true
    return ts.isIdentifier(node) && resolveLexicalBinding(bindings, node.text, node)?.boundingRect === true
  })
}

function isJsdomGeometryComparison(left, right, bindings) {
  const leftScrollWidth = containsPageGeometryProperty(left, 'scrollWidth', bindings)
  const rightScrollWidth = containsPageGeometryProperty(right, 'scrollWidth', bindings)
  const leftClientWidth = containsPageGeometryProperty(left, 'clientWidth', bindings)
  const rightClientWidth = containsPageGeometryProperty(right, 'clientWidth', bindings)
  if ((leftScrollWidth && rightClientWidth) || (rightScrollWidth && leftClientWidth)) return true

  const leftOffsetWidth = containsPageGeometryProperty(left, 'offsetWidth', bindings)
  const rightOffsetWidth = containsPageGeometryProperty(right, 'offsetWidth', bindings)
  if ((leftOffsetWidth && containsViewportSize(right, bindings)) || (rightOffsetWidth && containsViewportSize(left, bindings))) return true

  return (containsBoundingRect(left, bindings) && containsViewportSize(right, bindings))
    || (containsBoundingRect(right, bindings) && containsViewportSize(left, bindings))
}

function expectationComparisonOperands(call) {
  const matcher = getMember(call.expression)
  const comparisonMatchers = new Set([
    'toBe',
    'toEqual',
    'toStrictEqual',
    'toBeCloseTo',
    'toBeLessThan',
    'toBeLessThanOrEqual',
    'toBeGreaterThan',
    'toBeGreaterThanOrEqual',
  ])
  if (matcher === null || matcher.name === null || !comparisonMatchers.has(matcher.name) || call.arguments.length === 0) return undefined
  const received = expectationReceivedExpression(call)
  return received === undefined ? undefined : { left: received, right: call.arguments[0] }
}

export function scanSourceFile(filePath, sourceText = readFileSync(filePath, 'utf8')) {
  const sourceFile = ts.createSourceFile(filePath, sourceText, ts.ScriptTarget.Latest, true, scriptKindFor(filePath))
  const violations = []
  const bindings = geometryBindings(sourceFile)

  function visit(node) {
    if (ts.isBinaryExpression(node) && isComparisonOperator(node.operatorToken.kind) && isJsdomGeometryComparison(node.left, node.right, bindings)) {
      violations.push(createJsdomGeometryViolation(filePath, sourceFile, node))
    }

    if (ts.isBinaryExpression(node) && ts.isAssignmentOperator(node.operatorToken.kind)) {
      const root = findProtectedRoot(node.left)
      if (root !== null) violations.push(createSharedStateMutationViolation(filePath, sourceFile, node.left, root))
    }

    if (ts.isCallExpression(node)) {
      const root = findMutationCallTarget(node)
      if (root !== null) violations.push(createSharedStateMutationViolation(filePath, sourceFile, node.arguments[0], root))

      const comparison = expectationComparisonOperands(node)
      if (comparison !== undefined && isJsdomGeometryComparison(comparison.left, comparison.right, bindings)) {
        violations.push(createJsdomGeometryViolation(filePath, sourceFile, node))
      }
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
  if (relativePath.startsWith('tests/e2e/')) return false
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

function isActiveRunnerVitestFile(relativePath) {
  if (!relativePath.startsWith('src/') && !relativePath.startsWith('tests/')) return false
  if (relativePath.startsWith('tests/integration/')) return false
  return /\.(?:spec|test)\.tsx?$/.test(relativePath)
}

export function collectRunnerVitestFiles(runnerRoot = resolve(repositoryRoot, 'packages/runner')) {
  return [
    ...walkFiles(resolve(runnerRoot, 'src')),
    ...walkFiles(resolve(runnerRoot, 'tests')),
  ].filter((filePath) => isActiveRunnerVitestFile(relative(runnerRoot, filePath).replaceAll('\\', '/')))
}

function isGlobalTimerCall(call) {
  if (isGlobalBuiltin(call.expression, 'setTimeout')) return true
  const member = getMember(call.expression)
  return member !== null && member.name === 'setTimeout' && isIdentifierNamed(member.object, 'global')
}

function isPromiseConstruction(expression) {
  return ts.isNewExpression(expression) && isGlobalBuiltin(expression.expression, 'Promise')
}

function forEachDescendant(node, callback) {
  let matched = false

  function visit(current) {
    if (callback(current)) {
      matched = true
      return
    }
    ts.forEachChild(current, visit)
  }

  visit(node)
  return matched
}

function functionParameterNames(node) {
  return node.parameters
    .map((parameter) => unwrapExpression(parameter.name))
    .filter(ts.isIdentifier)
    .map((parameter) => parameter.text)
}

function timerCallbackResolvesPromise(callback, resolveNames) {
  const current = unwrapExpression(callback)
  if (ts.isIdentifier(current)) return resolveNames.has(current.text)
  return forEachDescendant(current, (node) => {
    if (!ts.isCallExpression(node)) return false
    const callee = unwrapExpression(node.expression)
    return ts.isIdentifier(callee) && resolveNames.has(callee.text)
  })
}

function resolvePromiseExecutor(expression, bindings, seen = new Set()) {
  const current = unwrapExpression(expression)
  if (isFunctionLike(current)) return current
  if (!ts.isIdentifier(current)) return undefined

  const binding = resolveLexicalBinding(bindings, current.text, current)
  if (binding === undefined || seen.has(binding)) return undefined
  seen.add(binding)
  if (binding.functionNode !== undefined) return binding.functionNode
  return binding.initializer === undefined
    ? undefined
    : resolvePromiseExecutor(binding.initializer, bindings, seen)
}

function promiseResolveNames(executor) {
  const resolveNames = new Set(functionParameterNames(executor).slice(0, 1))
  if (resolveNames.size === 0) return resolveNames

  let changed = true
  while (changed) {
    changed = false
    function visit(node) {
      if (node !== executor.body && isFunctionLike(node)) return
      if (ts.isVariableDeclaration(node) && ts.isIdentifier(node.name) && node.initializer !== undefined) {
        const initializer = unwrapExpression(node.initializer)
        if (ts.isIdentifier(initializer) && resolveNames.has(initializer.text) && !resolveNames.has(node.name.text)) {
          resolveNames.add(node.name.text)
          changed = true
        }
      }
      ts.forEachChild(node, visit)
    }
    visit(executor.body)
  }

  return resolveNames
}

function isTimerBackedPromise(expression, bindings) {
  const current = unwrapExpression(expression)
  if (!isPromiseConstruction(current)) return false

  const executorArgument = current.arguments?.[0]
  if (executorArgument === undefined) return false
  const executor = resolvePromiseExecutor(executorArgument, bindings)
  if (executor === undefined) return false

  const resolveNames = promiseResolveNames(executor)
  if (resolveNames.size === 0) return false

  return forEachDescendant(executor.body, (node) => {
    if (!ts.isCallExpression(node) || !isGlobalTimerCall(node)) return false
    const callback = node.arguments[0]
    return callback !== undefined && timerCallbackResolvesPromise(callback, resolveNames)
  })
}

function isFunctionLike(node) {
  return ts.isArrowFunction(node) || ts.isFunctionExpression(node) || ts.isFunctionDeclaration(node)
}

function lexicalScope(node) {
  let current = node.parent
  while (current !== undefined) {
    if (ts.isBlock(current) || ts.isSourceFile(current) || isFunctionLike(current)) return current
    current = current.parent
  }
  return node.getSourceFile()
}

function isAncestor(ancestor, node) {
  let current = node
  while (current !== undefined) {
    if (current === ancestor) return true
    current = current.parent
  }
  return false
}

function scopeDepth(scope) {
  let depth = 0
  let current = scope.parent
  while (current !== undefined) {
    depth += 1
    current = current.parent
  }
  return depth
}

function resolveLexicalBinding(bindings, name, node) {
  const candidates = bindings.filter((binding) => (
    binding.name === name
    && isAncestor(binding.scope, node)
    && (binding.hoisted || binding.declaration.getStart() < node.getStart())
  ))
  candidates.sort((left, right) => (
    scopeDepth(right.scope) - scopeDepth(left.scope)
    || right.declaration.getStart() - left.declaration.getStart()
  ))
  return candidates[0]
}

function hasTimerBinding(bindings, name, node) {
  return resolveLexicalBinding(bindings, name, node)?.timer === true
}

function expressionUsesTimerPromise(expression, bindings) {
  const current = unwrapExpression(expression)
  if (isTimerBackedPromise(current, bindings)) return true
  if (ts.isIdentifier(current)) return hasTimerBinding(bindings, current.text, current)
  if (ts.isArrayLiteralExpression(current)) {
    return current.elements.some((element) => ts.isExpression(element) && expressionUsesTimerPromise(element, bindings))
  }
  if (!ts.isCallExpression(current)) return false

  const callee = unwrapExpression(current.expression)
  if (ts.isIdentifier(callee) && hasTimerBinding(bindings, callee.text, callee)) return true
  return current.arguments.some((argument) => expressionUsesTimerPromise(argument, bindings))
}

function functionCreatesTimerPromise(node, bindings) {
  if (ts.isArrowFunction(node) && !ts.isBlock(node.body)) return expressionUsesTimerPromise(node.body, bindings)
  if (node.body === undefined) return false

  let createsTimerPromise = false
  function visit(child) {
    if (child !== node.body && isFunctionLike(child)) return
    if (ts.isReturnStatement(child) && child.expression !== undefined && expressionUsesTimerPromise(child.expression, bindings)) {
      createsTimerPromise = true
      return
    }
    if (ts.isAwaitExpression(child) && expressionUsesTimerPromise(child.expression, bindings)) {
      createsTimerPromise = true
      return
    }
    ts.forEachChild(child, visit)
  }

  visit(node.body)
  return createsTimerPromise
}

function localTimerPromiseBindings(sourceFile) {
  const bindings = []

  function visit(node) {
    if (ts.isFunctionDeclaration(node) && node.name !== undefined) {
      bindings.push({
        name: node.name.text,
        declaration: node,
        scope: lexicalScope(node),
        functionNode: node,
        initializer: undefined,
        hoisted: true,
        timer: false,
      })
    }
    if (ts.isVariableDeclaration(node) && ts.isIdentifier(node.name) && node.initializer !== undefined) {
      bindings.push({
        name: node.name.text,
        declaration: node,
        scope: lexicalScope(node),
        functionNode: isFunctionLike(node.initializer) ? node.initializer : undefined,
        initializer: node.initializer,
        hoisted: false,
        timer: false,
      })
    }
    ts.forEachChild(node, visit)
  }

  visit(sourceFile)

  let changed = true
  while (changed) {
    changed = false
    for (const binding of bindings) {
      if (binding.timer) continue
      if (binding.initializer !== undefined && expressionUsesTimerPromise(binding.initializer, bindings)) {
        binding.timer = true
        changed = true
        continue
      }
      if (binding.functionNode !== undefined && functionCreatesTimerPromise(binding.functionNode, bindings)) {
        binding.timer = true
        changed = true
      }
    }
  }

  return bindings
}

function awaitedTimerPromise(awaitExpression, bindings) {
  return expressionUsesTimerPromise(awaitExpression.expression, bindings)
}

function isViMethodCall(call, methodNames) {
  const member = getMember(call.expression)
  return member !== null
    && member.name !== null
    && methodNames.has(member.name)
    && isIdentifierNamed(member.object, 'vi')
}

function enclosingFunction(node) {
  let current = node.parent
  while (current !== undefined) {
    if (isFunctionLike(current)) return current
    current = current.parent
  }
  return undefined
}

function isDescribeCall(call) {
  if (isIdentifierNamed(call.expression, 'describe')) return true
  const member = getMember(call.expression)
  return member !== null && isIdentifierNamed(member.object, 'describe')
}

function enclosingDescribeCalls(node) {
  const descriptions = new Set()
  let current = node.parent
  while (current !== undefined) {
    if (isFunctionLike(current) && ts.isCallExpression(current.parent) && isDescribeCall(current.parent)) {
      descriptions.add(current.parent)
    }
    current = current.parent
  }
  return descriptions
}

function beforeEachEnablesFakeTimers(sourceFile, node) {
  const descriptions = enclosingDescribeCalls(node)
  const modeCalls = []

  forEachDescendant(sourceFile, (candidate) => {
    if (!ts.isCallExpression(candidate) || !isIdentifierNamed(candidate.expression, 'beforeEach')) return false
    const hookDescriptions = enclosingDescribeCalls(candidate)
    if (![...hookDescriptions].every((description) => descriptions.has(description))) return false
    const callback = candidate.arguments.find((argument) => isFunctionLike(argument))
    if (callback === undefined) return false

    forEachDescendant(callback.body, (child) => {
      if (ts.isCallExpression(child) && isViMethodCall(child, new Set(['useFakeTimers', 'useRealTimers']))) {
        modeCalls.push({
          call: child,
          describeDepth: hookDescriptions.size,
          hookPosition: candidate.getStart(),
        })
      }
      return false
    })
    return false
  })

  modeCalls.sort((left, right) => (
    left.describeDepth - right.describeDepth
    || left.hookPosition - right.hookPosition
    || left.call.getStart() - right.call.getStart()
  ))
  const lastMode = modeCalls.at(-1)?.call
  return lastMode !== undefined && isViMethodCall(lastMode, new Set(['useFakeTimers']))
}

function hasExplicitFakeTimerAdvanceBefore(node, sourceFile) {
  const scope = enclosingFunction(node)
  if (scope === undefined) return false

  const timerModeCalls = []
  const advanceCalls = []
  function visit(child) {
    if (child !== scope && isFunctionLike(child)) return
    if (ts.isCallExpression(child) && child.getStart() < node.getStart()) {
      if (isViMethodCall(child, new Set(['useFakeTimers', 'useRealTimers']))) timerModeCalls.push(child)
      if (isViMethodCall(child, new Set([
        'advanceTimersByTime',
        'advanceTimersByTimeAsync',
        'advanceTimersToNextTimer',
        'advanceTimersToNextTimerAsync',
        'runAllTimers',
        'runAllTimersAsync',
        'runOnlyPendingTimers',
        'runOnlyPendingTimersAsync',
      ]))) advanceCalls.push(child)
    }
    ts.forEachChild(child, visit)
  }

  visit(scope.body)

  const lastMode = timerModeCalls.at(-1)
  const fakeTimersEnabled = lastMode === undefined
    ? beforeEachEnablesFakeTimers(sourceFile, node)
    : isViMethodCall(lastMode, new Set(['useFakeTimers']))
  return fakeTimersEnabled && advanceCalls.some((call) => call.getStart() > (lastMode?.getStart() ?? -1))
}

function isRunnerHostSpec(filePath) {
  return /(?:^|\/)runner-host[^/]*\.spec\.tsx?$/.test(filePath.replaceAll('\\', '/'))
}

function isTimeNowCall(expression) {
  if (!ts.isCallExpression(expression)) return false
  const member = getMember(expression.expression)
  if (member === null || member.name !== 'now') return false
  return isIdentifierNamed(member.object, 'Date')
    || isIdentifierNamed(member.object, 'performance')
    || isGlobalBuiltin(member.object, 'Date')
    || isGlobalBuiltin(member.object, 'performance')
}

function containsTimeNow(expression) {
  return forEachDescendant(expression, (node) => isTimeNowCall(node))
}

function timeNowBindings(sourceFile) {
  const bindings = []

  function visit(node) {
    if (ts.isVariableDeclaration(node) && ts.isIdentifier(node.name) && node.initializer !== undefined) {
      bindings.push({
        name: node.name.text,
        declaration: node,
        scope: lexicalScope(node),
        hoisted: false,
        timeNow: false,
      })
    }
    ts.forEachChild(node, visit)
  }

  visit(sourceFile)

  let changed = true
  while (changed) {
    changed = false
    for (const binding of bindings) {
      if (binding.timeNow || binding.declaration.initializer === undefined) continue
      const initializer = unwrapExpression(binding.declaration.initializer)
      const isTimeAlias = ts.isIdentifier(initializer)
        && resolveLexicalBinding(bindings, initializer.text, initializer)?.timeNow === true
      if (isTimeNowCall(initializer) || isTimeAlias) {
        binding.timeNow = true
        changed = true
      }
    }
  }

  return bindings
}

function containsTimeSnapshot(expression, bindings) {
  return containsTimeNow(expression) || forEachDescendant(expression, (node) => (
    ts.isIdentifier(node) && resolveLexicalBinding(bindings, node.text, node)?.timeNow === true
  ))
}

function isElapsedExpression(expression, timeBindings) {
  const current = unwrapExpression(expression)
  if (!ts.isBinaryExpression(current)) return false
  const operator = current.operatorToken.kind
  const isElapsedDifference = operator === ts.SyntaxKind.MinusToken
  const isTimeComparison = new Set([
    ts.SyntaxKind.LessThanToken,
    ts.SyntaxKind.LessThanEqualsToken,
    ts.SyntaxKind.GreaterThanToken,
    ts.SyntaxKind.GreaterThanEqualsToken,
  ]).has(operator)
  return (isElapsedDifference || isTimeComparison)
    && (containsTimeSnapshot(current.left, timeBindings) || containsTimeSnapshot(current.right, timeBindings))
}

function elapsedTimeBindings(sourceFile, timeBindings) {
  const bindings = []

  function visit(node) {
    if (ts.isVariableDeclaration(node) && ts.isIdentifier(node.name) && node.initializer !== undefined) {
      bindings.push({
        name: node.name.text,
        declaration: node,
        scope: lexicalScope(node),
        hoisted: false,
        elapsed: isElapsedExpression(node.initializer, timeBindings),
      })
    }
    ts.forEachChild(node, visit)
  }

  visit(sourceFile)
  return bindings
}

function containsElapsedTimeBinding(expression, bindings) {
  return forEachDescendant(expression, (node) => (
    ts.isIdentifier(node) && resolveLexicalBinding(bindings, node.text, node)?.elapsed === true
  ))
}

function expectationReceivedExpression(call) {
  const member = getMember(call.expression)
  if (member === null) return undefined

  let current = member.object
  while (true) {
    const nextMember = getMember(current)
    if (nextMember === null) break
    current = nextMember.object
  }

  return ts.isCallExpression(current) && isIdentifierNamed(current.expression, 'expect')
    ? current.arguments[0]
    : undefined
}

export function scanRunnerSourceFile(filePath, sourceText = readFileSync(filePath, 'utf8')) {
  const sourceFile = ts.createSourceFile(filePath, sourceText, ts.ScriptTarget.Latest, true, scriptKindFor(filePath))
  const violations = []
  const timerBindings = localTimerPromiseBindings(sourceFile)
  const timeBindings = timeNowBindings(sourceFile)
  const elapsedBindings = elapsedTimeBindings(sourceFile, timeBindings)

  function visit(node) {
    if (ts.isAwaitExpression(node) && awaitedTimerPromise(node, timerBindings) && !hasExplicitFakeTimerAdvanceBefore(node, sourceFile)) {
      violations.push(createRunnerViolation(
        filePath,
        sourceFile,
        node,
        timerPromiseRule,
        'awaits a Promise resolved by real setTimeout',
        'Use a deferred signal, or fake timers with an explicit advance for a product interval.',
      ))
    }

    if (ts.isCallExpression(node)) {
      if (isRunnerHostSpec(filePath) && isViMethodCall(node, new Set(['waitFor']))) {
        violations.push(createRunnerViolation(
          filePath,
          sourceFile,
          node,
          runnerHostWaitForRule,
          'uses vi.waitFor in a RunnerHost spec',
          'Use a test-owned deferred signal or advance fake time.',
        ))
      }

      const received = expectationReceivedExpression(node)
      if (received !== undefined && (isElapsedExpression(received, timeBindings) || containsElapsedTimeBinding(received, elapsedBindings))) {
        violations.push(createRunnerViolation(
          filePath,
          sourceFile,
          received,
          elapsedTimeAssertionRule,
          'asserts correctness from elapsed wall-clock time',
          'Drive the interval with fake timers and assert the observable result.',
        ))
      }
    }

    ts.forEachChild(node, visit)
  }

  visit(sourceFile)
  return violations
}

export function checkRunnerTestBoundaries(runnerRoot = resolve(repositoryRoot, 'packages/runner')) {
  const files = collectRunnerVitestFiles(runnerRoot)
  return {
    files,
    violations: files.flatMap((filePath) => scanRunnerSourceFile(filePath)),
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
    'src/jsdom-geometry.test.tsx',
    'src/prototype-calls.test.tsx',
    'tests/active.spec.tsx',
  ]

  assertSelfTest(
    JSON.stringify(relativeFiles) === JSON.stringify(expectedFiles),
    `expected active files ${expectedFiles.join(', ')}, got ${relativeFiles.join(', ')}`,
  )

  const sharedStateViolations = violations.filter((violation) => violation.rule === sharedStateMutationRule)
  const actualRoots = Object.groupBy(
    sharedStateViolations,
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
    violations.every((violation) => violation.line > 0 && violation.column > 0),
    'diagnostics are missing source location',
  )

  const webRules = Object.groupBy(
    violations,
    (violation) => relative(fixtureWebRoot, violation.filePath).replaceAll('\\', '/'),
  )
  const expectedWebRules = {
    'src/direct-assignment.test.tsx': [sharedStateMutationRule, sharedStateMutationRule, sharedStateMutationRule, sharedStateMutationRule],
    'src/jsdom-geometry.test.tsx': [jsdomGeometryRule, jsdomGeometryRule, jsdomGeometryRule, jsdomGeometryRule, jsdomGeometryRule, jsdomGeometryRule, jsdomGeometryRule, jsdomGeometryRule, jsdomGeometryRule, jsdomGeometryRule],
    'src/prototype-calls.test.tsx': [sharedStateMutationRule, sharedStateMutationRule, sharedStateMutationRule, sharedStateMutationRule],
    'tests/active.spec.tsx': [sharedStateMutationRule],
  }

  assertSelfTest(
    JSON.stringify(Object.fromEntries(Object.entries(webRules).map(([file, items]) => [file, items.map((item) => item.rule)])))
      === JSON.stringify(expectedWebRules),
    'did not report the expected Web boundary rules',
  )

  const fixtureRunnerRoot = resolve(repositoryRoot, 'scripts/fixtures/node-test-boundaries/runner')
  const runnerResult = checkRunnerTestBoundaries(fixtureRunnerRoot)
  const runnerRelativeFiles = runnerResult.files
    .map((filePath) => relative(fixtureRunnerRoot, filePath).replaceAll('\\', '/'))
  const expectedRunnerFiles = [
    'tests/allowed-before-each-fake-timer.spec.ts',
    'tests/allowed-fake-timer.spec.ts',
    'tests/allowed-fixture-date.spec.ts',
    'tests/allowed-shadowed-elapsed.spec.ts',
    'tests/allowed-shadowed-timer-helper.spec.ts',
    'tests/awaited-global-timer.spec.ts',
    'tests/awaited-local-bound-timer.spec.ts',
    'tests/awaited-local-timer.spec.ts',
    'tests/awaited-named-executor-timer.spec.ts',
    'tests/awaited-nested-real-timer.spec.ts',
    'tests/awaited-promise-all-timer.spec.ts',
    'tests/awaited-promise-timer.spec.ts',
    'tests/awaited-resolve-alias-timer.spec.ts',
    'tests/awaited-sibling-real-timer.spec.ts',
    'tests/elapsed-local-assertion.spec.ts',
    'tests/elapsed-snapshot-assertion.spec.ts',
    'tests/elapsed-time-assertion.spec.ts',
    'tests/ordinary-wait-for.spec.ts',
    'tests/runner-host-wait-for.spec.ts',
  ]

  assertSelfTest(
    JSON.stringify(runnerRelativeFiles) === JSON.stringify(expectedRunnerFiles),
    `expected active Runner files ${expectedRunnerFiles.join(', ')}, got ${runnerRelativeFiles.join(', ')}`,
  )

  const runnerRules = Object.groupBy(
    runnerResult.violations,
    (violation) => relative(fixtureRunnerRoot, violation.filePath).replaceAll('\\', '/'),
  )
  const expectedRunnerRules = {
    'tests/awaited-global-timer.spec.ts': [timerPromiseRule],
    'tests/awaited-local-bound-timer.spec.ts': [timerPromiseRule],
    'tests/awaited-local-timer.spec.ts': [timerPromiseRule],
    'tests/awaited-named-executor-timer.spec.ts': [timerPromiseRule],
    'tests/awaited-nested-real-timer.spec.ts': [timerPromiseRule],
    'tests/awaited-promise-all-timer.spec.ts': [timerPromiseRule],
    'tests/awaited-promise-timer.spec.ts': [timerPromiseRule],
    'tests/awaited-resolve-alias-timer.spec.ts': [timerPromiseRule],
    'tests/awaited-sibling-real-timer.spec.ts': [timerPromiseRule],
    'tests/elapsed-local-assertion.spec.ts': [elapsedTimeAssertionRule],
    'tests/elapsed-snapshot-assertion.spec.ts': [elapsedTimeAssertionRule],
    'tests/elapsed-time-assertion.spec.ts': [elapsedTimeAssertionRule, elapsedTimeAssertionRule],
    'tests/runner-host-wait-for.spec.ts': [runnerHostWaitForRule],
  }

  assertSelfTest(
    JSON.stringify(Object.fromEntries(Object.entries(runnerRules).map(([file, items]) => [file, items.map((item) => item.rule)])))
      === JSON.stringify(expectedRunnerRules),
    'did not report the expected Runner time-boundary rules',
  )

  assertSelfTest(
    runnerResult.violations.every((violation) => violation.line > 0 && violation.column > 0 && violation.description !== undefined),
    'Runner diagnostics are missing rule, source location, or description',
  )
  console.log('node test boundary checker self-test passed')
}

function printViolations(violations) {
  for (const violation of violations) {
    const filePath = relative(repositoryRoot, violation.filePath).replaceAll('\\', '/')
    console.error(
      `${filePath}:${violation.line}:${violation.column} ${violation.rule}: ${violation.description}. ${violation.fix}`,
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

  const { files, violations } = scope === 'runner'
    ? checkRunnerTestBoundaries()
    : checkWebTestBoundaries()
  const scopeName = scope === 'runner' ? 'Runner' : 'Web'
  if (violations.length === 0) {
    console.log(`node test boundaries: checked ${files.length} active ${scopeName} Vitest files`)
    return
  }

  printViolations(violations)
  console.error(`${violations.length} node test boundary violation(s) found in ${files.length} active ${scopeName} Vitest files.`)
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
