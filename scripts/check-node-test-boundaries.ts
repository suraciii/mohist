import { execFileSync } from 'node:child_process'
import { existsSync, readdirSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname, relative, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { parseSourceFile, ts } from './node-test-ast.js'

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const sharedStateMutationRule = 'no-direct-shared-state-mutation'
const timerPromiseRule = 'no-real-time-sleep'
const runnerHostWaitForRule = 'no-runner-host-wait-for'
const elapsedTimeAssertionRule = 'no-elapsed-time-assertion'
const runnerChildProcessImportRule = 'no-default-runner-child-process-import'
const runnerExternalCommandRule = 'no-default-runner-platform-command'
const runnerExecutableScriptImportRule = 'no-default-runner-executable-script-import'
const runnerProcessPolicyMockRule = 'no-default-runner-process-policy-mock'
const runnerTestModifierRule = 'no-runner-test-modifier'
const historicalTestTitleRule = 'no-historical-ticket-test-title'
const testFileBudgetRule = 'test-file-size-budget'
const jsdomGeometryRule = 'no-jsdom-page-geometry-assertion'
const webFetchGlobalStubRule = 'no-web-fetch-global-stub'
const webFetchGlobalMutationRule = 'no-web-fetch-global-mutation'
const webViMockRule = 'no-web-vi-mock'
const vitestEnvironmentDirectiveRule = 'no-vitest-environment-directive'
const webDomInPlainTestRule = 'no-dom-in-plain-web-test'
const webNodeFsSourceReadRule = 'no-web-node-fs-source-read'
const protectedGlobalNames = new Set(['window', 'document', 'navigator'])
const protectedPrototypeNames = new Set(['Element', 'HTMLElement'])
const pageGeometryProperties = ['scrollWidth', 'clientWidth', 'offsetWidth']
const boundingRectProperties = new Set(['bottom', 'height', 'left', 'right', 'top', 'width', 'x', 'y'])
const childProcessModuleSpecifiers = new Set(['child_process', 'node:child_process'])
const nodeFsModuleSpecifiers = new Set(['fs', 'fs/promises', 'node:fs', 'node:fs/promises'])
const nodeFsReadMemberNames = new Set([
  'access',
  'accessSync',
  'createReadStream',
  'existsSync',
  'lstat',
  'lstatSync',
  'opendir',
  'opendirSync',
  'readFile',
  'readFileSync',
  'readdir',
  'readdirSync',
  'realpath',
  'realpathSync',
  'stat',
  'statSync',
])
const webDomGlobalNames = new Set([
  'Comment',
  'CustomEvent',
  'DOMParser',
  'DOMRect',
  'DragEvent',
  'Element',
  'Event',
  'FocusEvent',
  'HTMLElement',
  'InputEvent',
  'IntersectionObserver',
  'KeyboardEvent',
  'MouseEvent',
  'MutationObserver',
  'Node',
  'PointerEvent',
  'ResizeObserver',
  'SubmitEvent',
  'Text',
  'WheelEvent',
  'cancelAnimationFrame',
  'document',
  'getComputedStyle',
  'localStorage',
  'matchMedia',
  'navigator',
  'requestAnimationFrame',
  'sessionStorage',
  'window',
])
const runnerTestModifierNames = new Set(['skip', 'only', 'todo', 'skipIf'])
const testApiFunctionNames = new Set(['it', 'test', 'describe', 'suite', 'context'])
const runnerDefaultTrack = 'default'
const runnerIntegrationTrack = 'integration'
const testFileBudgetBaselineRelativePath = 'scripts/node-test-file-budget-baseline.json'
const testFileLineBudgets = Object.freeze({ test: 500, spec: 800 })

type BudgetRecord = {
  filePath: string
  relativePath: string
  budget: number
  lines: number
}

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
    if (ts.isTypeAssertion(current)) {
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

function createViolation(filePath, sourceFile, node, { rule, root = undefined, description, fix }) {
  return createViolationAtPosition(filePath, sourceFile, node.getStart(sourceFile), { rule, root, description, fix })
}

function createViolationAtPosition(filePath, sourceFile, start, { rule, root = undefined, description, fix }) {
  const position = sourceFile.getLineAndCharacterOfPosition(start)
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

function stringLiteralText(expression) {
  if (expression === undefined) return undefined
  const current = unwrapExpression(expression)
  return ts.isStringLiteral(current) || ts.isNoSubstitutionTemplateLiteral(current)
    ? current.text
    : undefined
}

function importSpecifierText(node) {
  if (ts.isImportDeclaration(node)) return stringLiteralText(node.moduleSpecifier)
  if (ts.isImportEqualsDeclaration(node) && ts.isExternalModuleReference(node.moduleReference)) {
    return node.moduleReference.expression === undefined
      ? undefined
      : stringLiteralText(node.moduleReference.expression)
  }
  if (ts.isCallExpression(node) && node.expression.kind === ts.SyntaxKind.ImportKeyword) {
    return node.arguments[0] === undefined ? undefined : stringLiteralText(node.arguments[0])
  }
  return undefined
}

function isChildProcessImport(node) {
  const specifier = importSpecifierText(node)
  if (specifier !== undefined && childProcessModuleSpecifiers.has(specifier)) return true
  if (!ts.isCallExpression(node)) return false
  const argument = node.arguments[0]
  if (argument === undefined) return false
  const required = stringLiteralText(argument)
  if (required === undefined || !childProcessModuleSpecifiers.has(required)) return false
  const callee = unwrapExpression(node.expression)
  if (isIdentifierNamed(callee, 'require')) return true
  return ts.isCallExpression(callee)
    && (isIdentifierNamed(unwrapExpression(callee.expression), 'createRequire')
      || getMember(callee.expression)?.name === 'createRequire')
}

function resolveLocalModuleImport(filePath, moduleSpecifier) {
  if (!moduleSpecifier.startsWith('.')) return undefined

  const candidate = resolve(dirname(filePath), moduleSpecifier)
  const base = candidate.replace(/\.(?:[cm]?[jt]sx?)$/, '')
  const extensions = ['.ts', '.tsx', '.mts', '.cts', '.js', '.jsx', '.cjs']
  const candidates = [
    candidate,
    ...extensions.map((extension) => `${base}${extension}`),
    ...extensions.map((extension) => resolve(candidate, `index${extension}`)),
  ]
  return candidates.find(existsSync)
}

function importResolvesTo(filePath, moduleSpecifier, targetFile) {
  const importedFile = resolveLocalModuleImport(filePath, moduleSpecifier)
  return importedFile !== undefined && resolve(importedFile) === resolve(targetFile)
}

function runnerSystemProcessFile(runnerRoot) {
  return resolve(runnerRoot, 'src/system/process.ts')
}

function runnerProcessPolicyFile(runnerRoot) {
  return resolve(runnerRoot, 'src/system/process-policy.ts')
}

function runnerExecutableModuleFiles(runnerRoot) {
  return [
    resolve(runnerRoot, 'scripts/write-build-info.ts'),
    resolve(runnerRoot, 'src/cli.ts'),
  ]
}

function systemProcessRunCommandImports(sourceFile, filePath, runnerRoot) {
  const named = new Set()
  const namespaces = new Set()
  const systemProcessFile = runnerSystemProcessFile(runnerRoot)

  function visit(node) {
    if (ts.isImportDeclaration(node)) {
      const specifier = importSpecifierText(node)
      if (specifier !== undefined && importResolvesTo(filePath, specifier, systemProcessFile)) {
        const bindings = node.importClause?.namedBindings
        if (bindings !== undefined && ts.isNamedImports(bindings)) {
          for (const element of bindings.elements) {
            const importedName = element.propertyName?.text ?? element.name.text
            if (importedName === 'runCommand') named.add(element.name.text)
          }
        }
        if (bindings !== undefined && ts.isNamespaceImport(bindings)) namespaces.add(bindings.name.text)
      }
    }
    node.forEachChild(visit)
  }

  visit(sourceFile)
  return { named, namespaces }
}

function isSystemProcessRunCommandCall(call, imports) {
  const callee = unwrapExpression(call.expression)
  if (ts.isIdentifier(callee)) return imports.named.has(callee.text)

  const member = getMember(callee)
  return member !== null
    && member.name === 'runCommand'
    && ts.isIdentifier(member.object)
    && imports.namespaces.has(member.object.text)
}

function isProcessExecPath(expression) {
  const member = getMember(expression)
  if (member === null || member.name !== 'execPath') return false
  if (isIdentifierNamed(member.object, 'process')) return true
  const globalProcess = getMember(member.object)
  return globalProcess !== null
    && globalProcess.name === 'process'
    && isIdentifierNamed(globalProcess.object, 'globalThis')
}

function isPlatformCommand(expression) {
  return stringLiteralText(expression) === 'git' || isProcessExecPath(expression)
}

function isDefaultRunnerPlatformCommandCall(call, systemProcessImports) {
  if (!isSystemProcessRunCommandCall(call, systemProcessImports)) return false
  return call.arguments[0] !== undefined && isPlatformCommand(call.arguments[0])
}

function isRunnerExecutableScriptImport(node, filePath, runnerRoot) {
  const specifier = importSpecifierText(node)
  return specifier !== undefined
    && runnerExecutableModuleFiles(runnerRoot).some((targetFile) => importResolvesTo(filePath, specifier, targetFile))
}

function mockModuleSpecifier(call) {
  const argument = call.arguments[0]
  if (argument === undefined) return undefined
  const literal = stringLiteralText(argument)
  if (literal !== undefined) return literal
  return ts.isCallExpression(unwrapExpression(argument))
    ? importSpecifierText(unwrapExpression(argument))
    : undefined
}

function isProcessPolicyMock(call, filePath, runnerRoot) {
  if (!isViMethodCall(call, new Set(['mock', 'doMock']))) return false
  const specifier = mockModuleSpecifier(call)
  return specifier !== undefined && importResolvesTo(filePath, specifier, runnerProcessPolicyFile(runnerRoot))
}

function testApiBase(expression) {
  let current = unwrapExpression(expression)
  while (true) {
    if (ts.isCallExpression(current)) {
      current = unwrapExpression(current.expression)
      continue
    }
    if (ts.isIdentifier(current)) return testApiFunctionNames.has(current.text) ? current.text : undefined
    const member = getMember(current)
    if (member === null) return undefined
    current = member.object
  }
}

function historicalTicketTitle(call) {
  const base = testApiBase(call.expression)
  if (base === undefined) return undefined
  const title = call.arguments[0] === undefined ? undefined : stringLiteralText(call.arguments[0])
  if (title === undefined) return undefined
  return /\bissue-\d+\b/i.test(title)
    || /\bissue\s*#\d+\b/i.test(title)
    || /\bT-\d+\b/i.test(title)
    ? { base, title }
    : undefined
}

function createHistoricalTicketTitleViolation(filePath, sourceFile, call, title) {
  return createViolation(filePath, sourceFile, call, {
    rule: historicalTestTitleRule,
    description: `uses historical ticket provenance in a ${title.base} title`,
    fix: 'Name the behavior under test; keep historical ticket identifiers out of test titles.',
  })
}

function testModifier(call) {
  let current = unwrapExpression(call.expression)
  while (true) {
    const member = getMember(current)
    if (member === null || member.name === null) return undefined
    if (runnerTestModifierNames.has(member.name)) {
      const base = testApiBase(member.object)
      if (base !== undefined) return { base, modifier: member.name }
    }
    current = member.object
  }
}

function isAllowedRunnerTestModifier(modifier, track) {
  return track === runnerIntegrationTrack
    && modifier.modifier === 'skipIf'
    && (modifier.base === 'it' || modifier.base === 'test')
}

function isGlobalWindow(expression) {
  const current = unwrapExpression(expression)
  if (isIdentifierNamed(current, 'window')) return true
  const member = getMember(current)
  return member !== null
    && member.name === 'window'
    && (isIdentifierNamed(member.object, 'globalThis') || isIdentifierNamed(member.object, 'global'))
}

function bindingIdentifiers(name) {
  if (ts.isIdentifier(name)) return [name]
  if (!ts.isObjectBindingPattern(name) && !ts.isArrayBindingPattern(name)) return []
  return name.elements.flatMap((element) => ts.isOmittedExpression(element) ? [] : bindingIdentifiers(element.name))
}

function webValueBindings(sourceFile) {
  const bindings = []

  function addBinding(name, declaration) {
    for (const identifier of bindingIdentifiers(name)) {
      bindings.push({ name: identifier.text, scope: lexicalScope(declaration) })
    }
  }

  function visit(node) {
    if (ts.isImportDeclaration(node)) {
      const clause = node.importClause
      if (clause?.name !== undefined) addBinding(clause.name, node)
      const namedBindings = clause?.namedBindings
      if (namedBindings !== undefined && ts.isNamespaceImport(namedBindings)) addBinding(namedBindings.name, node)
      if (namedBindings !== undefined && ts.isNamedImports(namedBindings)) {
        for (const element of namedBindings.elements) addBinding(element.name, node)
      }
    }
    if (ts.isVariableDeclaration(node)) addBinding(node.name, node)
    if (isFunctionLike(node)) {
      for (const parameter of node.parameters) addBinding(parameter.name ?? parameter, node)
    }
    if ((ts.isFunctionDeclaration(node) || ts.isClassDeclaration(node) || ts.isEnumDeclaration(node)) && node.name !== undefined) {
      addBinding(node.name, node)
    }
    if (ts.isCatchClause(node) && node.variableDeclaration !== undefined) addBinding(node.variableDeclaration.name, node)
    node.forEachChild(visit)
  }

  visit(sourceFile)
  return bindings
}

function isLocallyBound(identifier, bindings) {
  return bindings.some((binding) => binding.name === identifier.text && isAncestor(binding.scope, identifier))
}

function isGlobalFetchTarget(expression) {
  return isIdentifierNamed(expression, 'globalThis')
    || isIdentifierNamed(expression, 'global')
    || isGlobalWindow(expression)
}

function isGlobalFetch(expression, bindings) {
  const current = unwrapExpression(expression)
  if (ts.isIdentifier(current) && current.text === 'fetch') return !isLocallyBound(current, bindings)
  const member = getMember(current)
  return member !== null
    && member.name === 'fetch'
    && isGlobalFetchTarget(member.object)
}

function isGlobalFetchDefinition(call) {
  const member = getMember(call.expression)
  if (member === null || member.name !== 'defineProperty') return false
  if (!isGlobalBuiltin(member.object, 'Object') && !isGlobalBuiltin(member.object, 'Reflect')) return false
  return call.arguments[0] !== undefined
    && call.arguments[1] !== undefined
    && isGlobalFetchTarget(call.arguments[0])
    && stringLiteralText(call.arguments[1]) === 'fetch'
}

function isWebFetchGlobalStub(call) {
  return isViMethodCall(call, new Set(['stubGlobal']))
    && stringLiteralText(call.arguments[0]) === 'fetch'
}

function isWebFetchGlobalSpy(call) {
  return isViMethodCall(call, new Set(['spyOn']))
    && call.arguments[0] !== undefined
    && isGlobalFetchTarget(call.arguments[0])
    && stringLiteralText(call.arguments[1]) === 'fetch'
}

function isWebViMock(call) {
  return isViMethodCall(call, new Set(['mock', 'doMock']))
}

function isViObject(expression) {
  const current = unwrapExpression(expression)
  if (isIdentifierNamed(current, 'vi')) return true
  const member = getMember(current)
  return member !== null
    && member.name === 'vi'
    && (isIdentifierNamed(member.object, 'globalThis') || isIdentifierNamed(member.object, 'global'))
}

function isWebViMockAliasDeclaration(node) {
  if (!ts.isVariableDeclaration(node) || node.initializer === undefined) return false
  const initializer = unwrapExpression(node.initializer)
  const member = getMember(initializer)
  if (member !== null && member.name !== null && new Set(['mock', 'doMock']).has(member.name) && isViObject(member.object)) {
    return true
  }
  if (!ts.isObjectBindingPattern(node.name) || !isViObject(initializer)) return false
  return node.name.elements.some((element) => {
    const propertyName = propertyNameText(element.propertyName) ?? propertyNameText(element.name)
    return propertyName === 'mock' || propertyName === 'doMock'
  })
}

function isOrdinaryWebNodeTest(filePath) {
  const normalized = filePath.replaceAll('\\', '/')
  return normalized.endsWith('.test.ts') && !normalized.endsWith('.dom.test.ts')
}

function isIdentifierValueReference(identifier) {
  const parent = identifier.parent
  if (isIdentifierInTypePosition(identifier)) return false
  if (ts.isPropertyAccessExpression(parent) && parent.name === identifier) return false
  if (ts.isQualifiedName(parent) && parent.right === identifier) return false
  if (ts.isPropertyAssignment(parent) && parent.name === identifier) return false
  if (ts.isShorthandPropertyAssignment(parent) && parent.name === identifier) return true
  if (ts.isBindingElement(parent) && (parent.name === identifier || parent.propertyName === identifier)) return false
  if (ts.isVariableDeclaration(parent) && parent.name === identifier) return false
  if (isFunctionLike(parent) && parent.parameters.some((parameter) => parameter === identifier || parameter.name === identifier)) return false
  if (ts.isFunctionDeclaration(parent) && parent.name === identifier) return false
  if (ts.isClassDeclaration(parent) && parent.name === identifier) return false
  if (ts.isInterfaceDeclaration(parent) && parent.name === identifier) return false
  if (ts.isTypeAliasDeclaration(parent) && parent.name === identifier) return false
  if (ts.isEnumDeclaration(parent) && parent.name === identifier) return false
  if (ts.isEnumMember(parent) && parent.name === identifier) return false
  if (ts.isImportClause(parent) && parent.name === identifier) return false
  if (ts.isImportSpecifier(parent) || ts.isNamespaceImport(parent) || ts.isExportSpecifier(parent)) return false
  if (ts.isPropertyDeclaration(parent) && parent.name === identifier) return false
  if (ts.isPropertySignature(parent) && parent.name === identifier) return false
  if (ts.isMethodDeclaration(parent) && parent.name === identifier) return false
  if (ts.isMethodSignature(parent) && parent.name === identifier) return false
  if (ts.isGetAccessorDeclaration(parent) && parent.name === identifier) return false
  if (ts.isSetAccessorDeclaration(parent) && parent.name === identifier) return false
  return true
}

function isIdentifierInTypePosition(identifier) {
  let current = identifier.parent
  while (current !== undefined) {
    if (ts.isTypeNode(current)) return true
    if (ts.isSourceFile(current) || ts.isStatement(current) || ts.isExpression(current)) return false
    current = current.parent
  }
  return false
}

function isOrdinaryWebDomGlobal(expression) {
  const current = unwrapExpression(expression)
  if (ts.isIdentifier(current)) {
    return isIdentifierValueReference(current) && webDomGlobalNames.has(current.text)
  }

  const member = getMember(current)
  return member !== null
    && member.name !== null
    && webDomGlobalNames.has(member.name)
    && (isIdentifierNamed(member.object, 'globalThis') || isIdentifierNamed(member.object, 'global'))
}

function isTestingLibraryReactImport(node) {
  const specifier = importSpecifierText(node)
  if (specifier === '@testing-library/react' || specifier?.startsWith('@testing-library/react/') === true) return true
  if (!ts.isCallExpression(node)) return false
  const callee = unwrapExpression(node.expression)
  const isModuleLoad = callee.kind === ts.SyntaxKind.ImportKeyword
    || isIdentifierNamed(callee, 'require')
    || (ts.isCallExpression(callee)
      && (isIdentifierNamed(unwrapExpression(callee.expression), 'createRequire')
        || getMember(callee.expression)?.name === 'createRequire'))
  const moduleSpecifier = stringLiteralText(node.arguments[0])
  return isModuleLoad
    && (moduleSpecifier === '@testing-library/react' || moduleSpecifier?.startsWith('@testing-library/react/') === true)
}

function isNodeFsSpecifier(specifier) {
  return specifier !== undefined && nodeFsModuleSpecifiers.has(specifier)
}

function isNodeFsModuleLoadCall(call) {
  const specifier = call.expression.kind === ts.SyntaxKind.ImportKeyword
    ? importSpecifierText(call)
    : call.arguments[0] === undefined ? undefined : stringLiteralText(call.arguments[0])
  if (!isNodeFsSpecifier(specifier)) return false
  const callee = unwrapExpression(call.expression)
  if (callee.kind === ts.SyntaxKind.ImportKeyword || isIdentifierNamed(callee, 'require')) return true
  return ts.isCallExpression(callee)
    && (isIdentifierNamed(unwrapExpression(callee.expression), 'createRequire')
      || getMember(callee.expression)?.name === 'createRequire')
}

function isNodeFsModuleLoadExpression(expression) {
  const current = unwrapExpression(expression)
  const loaded = ts.isAwaitExpression(current) ? unwrapExpression(current.expression) : current
  return ts.isCallExpression(loaded) && isNodeFsModuleLoadCall(loaded)
}

function addNodeFsBinding(name, propertyName, bindings) {
  if (ts.isIdentifier(name)) {
    if (propertyName === undefined || propertyName === 'promises') bindings.namespaces.add(name.text)
    if (propertyName !== undefined && nodeFsReadMemberNames.has(propertyName)) bindings.named.add(name.text)
    return
  }

  if (!ts.isObjectBindingPattern(name)) return
  for (const element of name.elements) {
    const memberName = propertyNameText(element.propertyName) ?? propertyNameText(element.name)
    addNodeFsBinding(element.name, memberName, bindings)
  }
}

function nodeFsReadBindings(sourceFile) {
  const named = new Set()
  const namespaces = new Set()
  const bindings = { named, namespaces }

  function addNamespaceBinding(name) {
    namespaces.add(name)
  }

  function visit(node) {
    if (ts.isImportDeclaration(node) && isNodeFsSpecifier(importSpecifierText(node))) {
      const clause = node.importClause
      if (clause?.name !== undefined) addNamespaceBinding(clause.name.text)
      const bindings = clause?.namedBindings
      if (bindings !== undefined && ts.isNamespaceImport(bindings)) addNamespaceBinding(bindings.name.text)
      if (bindings !== undefined && ts.isNamedImports(bindings)) {
        for (const element of bindings.elements) {
          const importedName = element.propertyName?.text ?? element.name.text
          if (importedName === 'default' || importedName === 'promises') addNamespaceBinding(element.name.text)
          if (nodeFsReadMemberNames.has(importedName)) named.add(element.name.text)
        }
      }
    }

    if (ts.isImportEqualsDeclaration(node) && isNodeFsSpecifier(importSpecifierText(node))) {
      addNamespaceBinding(node.name.text)
    }

    if (ts.isVariableDeclaration(node) && node.initializer !== undefined) {
      const initializer = unwrapExpression(node.initializer)
      const loaded = ts.isAwaitExpression(initializer) ? unwrapExpression(initializer.expression) : initializer
      if (ts.isCallExpression(loaded) && isNodeFsModuleLoadCall(loaded)) addNodeFsBinding(node.name, undefined, bindings)
    }

    node.forEachChild(visit)
  }

  visit(sourceFile)
  return { named, namespaces }
}

function isNodeFsReadCall(call, bindings) {
  const callee = unwrapExpression(call.expression)
  if (ts.isIdentifier(callee)) return bindings.named.has(callee.text)

  const member = getMember(callee)
  if (member === null || member.name === null || !nodeFsReadMemberNames.has(member.name)) return false
  if (isIdentifierNamed(member.object, 'fs') && bindings.namespaces.has('fs')) return true
  if (ts.isIdentifier(member.object) && bindings.namespaces.has(member.object.text)) return true
  if (isNodeFsModuleLoadExpression(member.object)) return true

  const promises = getMember(member.object)
  return promises !== null
    && promises.name === 'promises'
    && (
      (ts.isIdentifier(promises.object) && bindings.namespaces.has(promises.object.text))
      || isNodeFsModuleLoadExpression(promises.object)
    )
}

function vitestEnvironmentDirectivePositions(sourceFile) {
  return sourceFile.comments
    .filter((comment) => /@vitest-environment\b/i.test(comment.value))
    .map((comment) => comment.start)
}

function createJsdomGeometryViolation(filePath, sourceFile, node) {
  return createViolation(filePath, sourceFile, node, {
    rule: jsdomGeometryRule,
    description: 'asserts page-fit geometry that jsdom does not measure',
    fix: 'Keep semantic structure assertions here and move viewport geometry coverage to the browser track.',
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
  if (name === undefined) return undefined
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
    node.forEachChild(visit)
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

function scanWebSourceFile(filePath, sourceText, sourceFile) {
  const violations = []
  const bindings = geometryBindings(sourceFile)
  const valueBindings = webValueBindings(sourceFile)
  const ordinaryNodeTest = isOrdinaryWebNodeTest(filePath)
  const fsReadBindings = nodeFsReadBindings(sourceFile)

  for (const position of vitestEnvironmentDirectivePositions(sourceFile)) {
    violations.push(createViolationAtPosition(filePath, sourceFile, position, {
      rule: vitestEnvironmentDirectiveRule,
      description: 'uses a per-file Vitest environment directive',
      fix: 'Use the .test.ts, .dom.test.ts, .test.tsx, or .spec.tsx suffix to select the test environment.',
    }))
  }

  function visit(node) {
    if (isWebViMockAliasDeclaration(node)) {
      violations.push(createViolation(filePath, sourceFile, node, {
        rule: webViMockRule,
        description: 'creates an alias for vi.mock or vi.doMock',
        fix: 'Use an explicit test seam, provider, or MSW handler instead of module mocking.',
      }))
    }

    if (ordinaryNodeTest && ts.isExpression(node) && isOrdinaryWebDomGlobal(node)) {
      violations.push(createViolation(filePath, sourceFile, node, {
        rule: webDomInPlainTestRule,
        description: 'uses a DOM global from an ordinary .test.ts file',
        fix: 'Rename the file to .dom.test.ts, .test.tsx, or .spec.tsx, then use the matching test environment.',
      }))
    }

    if (ordinaryNodeTest && isTestingLibraryReactImport(node)) {
      violations.push(createViolation(filePath, sourceFile, node, {
        rule: webDomInPlainTestRule,
        description: 'imports @testing-library/react from an ordinary .test.ts file',
        fix: 'Rename the file to .dom.test.ts, .test.tsx, or .spec.tsx, then use the matching test environment.',
      }))
    }

    if (ts.isCallExpression(node) && isNodeFsReadCall(node, fsReadBindings)) {
      violations.push(createViolation(filePath, sourceFile, node, {
        rule: webNodeFsSourceReadRule,
        description: 'reads source through node:fs in a Web Vitest file',
        fix: 'Test the rendered or imported behavior instead of reading source files from a test.',
      }))
    }

    if (ts.isBinaryExpression(node) && isComparisonOperator(node.operatorToken.kind) && isJsdomGeometryComparison(node.left, node.right, bindings)) {
      violations.push(createJsdomGeometryViolation(filePath, sourceFile, node))
    }

    if (ts.isBinaryExpression(node) && ts.isAssignmentOperator(node.operatorToken.kind)) {
      if (isGlobalFetch(node.left, valueBindings)) {
        violations.push(createViolation(filePath, sourceFile, node.left, {
          rule: webFetchGlobalMutationRule,
          description: 'assigns the global fetch implementation',
          fix: 'Use the shared MSW server to model HTTP behavior instead of replacing global fetch.',
        }))
      }
      const root = findProtectedRoot(node.left)
      if (root !== null && !isGlobalFetch(node.left, valueBindings)) {
        violations.push(createSharedStateMutationViolation(filePath, sourceFile, node.left, root))
      }
    }

    if (ts.isCallExpression(node)) {
      if (isWebFetchGlobalStub(node)) {
        violations.push(createViolation(filePath, sourceFile, node, {
          rule: webFetchGlobalStubRule,
          description: 'stubs the global fetch implementation',
          fix: 'Use the shared MSW server to model HTTP behavior instead of stubbing global fetch.',
        }))
      }

      if (isWebFetchGlobalSpy(node) || isGlobalFetchDefinition(node)) {
        violations.push(createViolation(filePath, sourceFile, node, {
          rule: webFetchGlobalMutationRule,
          description: 'mutates or spies on the global fetch implementation',
          fix: 'Use the shared MSW server to model HTTP behavior instead of replacing global fetch.',
        }))
      }

      if (isWebViMock(node)) {
        violations.push(createViolation(filePath, sourceFile, node, {
          rule: webViMockRule,
          description: 'uses vi.mock in a Web Vitest file',
          fix: 'Use an explicit test seam, provider, or MSW handler instead of module mocking.',
        }))
      }

      const title = historicalTicketTitle(node)
      if (title !== undefined) violations.push(createHistoricalTicketTitleViolation(filePath, sourceFile, node, title))

      const root = findMutationCallTarget(node)
      if (root !== null) violations.push(createSharedStateMutationViolation(filePath, sourceFile, node.arguments[0], root))

      const comparison = expectationComparisonOperands(node)
      if (comparison !== undefined && isJsdomGeometryComparison(comparison.left, comparison.right, bindings)) {
        violations.push(createJsdomGeometryViolation(filePath, sourceFile, node))
      }
    }

    node.forEachChild(visit)
  }

  visit(sourceFile)
  return violations
}

export function scanSourceFile(filePath, sourceText = readFileSync(filePath, 'utf8')) {
  return scanWebSourceFile(filePath, sourceText, parseSourceFile(filePath, sourceText))
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
  if (relativePath.startsWith('src/')) {
    return /\.test\.tsx?$/.test(relativePath) || /\.spec\.tsx$/.test(relativePath)
  }
  if (!relativePath.startsWith('tests/')) return false
  if (relativePath.startsWith('tests/browser/')) return false
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

function isActiveRunnerIntegrationVitestFile(relativePath) {
  return relativePath.startsWith('tests/integration/') && /\.spec\.tsx?$/.test(relativePath)
}

export function collectRunnerVitestFiles(runnerRoot = resolve(repositoryRoot, 'packages/runner')) {
  return [
    ...walkFiles(resolve(runnerRoot, 'src')),
    ...walkFiles(resolve(runnerRoot, 'tests')),
  ].filter((filePath) => isActiveRunnerVitestFile(relative(runnerRoot, filePath).replaceAll('\\', '/')))
}

export function collectRunnerIntegrationVitestFiles(runnerRoot = resolve(repositoryRoot, 'packages/runner')) {
  return walkFiles(resolve(runnerRoot, 'tests/integration'))
    .filter((filePath) => isActiveRunnerIntegrationVitestFile(relative(runnerRoot, filePath).replaceAll('\\', '/')))
}

function repositoryRelativePath(filePath) {
  return relative(repositoryRoot, filePath).replaceAll('\\', '/')
}

function testFileLineBudget(filePath) {
  if (/\.test\.[^/]+$/.test(filePath)) return testFileLineBudgets.test
  if (/\.spec\.[^/]+$/.test(filePath)) return testFileLineBudgets.spec
  return undefined
}

function budgetScopeForRepositoryPath(filePath) {
  if (filePath.startsWith('packages/runner/')) return 'runner'
  if (filePath.startsWith('packages/web/')) return 'web'
  return undefined
}

function isActiveNodeVitestRepositoryPath(filePath) {
  if (filePath.startsWith('packages/runner/')) {
    const runnerRelativePath = filePath.slice('packages/runner/'.length)
    return isActiveRunnerVitestFile(runnerRelativePath) || isActiveRunnerIntegrationVitestFile(runnerRelativePath)
  }

  if (filePath.startsWith('packages/web/')) {
    return isActiveWebVitestFile(filePath.slice('packages/web/'.length))
  }

  return false
}

function countSourceLines(sourceText) {
  if (sourceText.length === 0) return 0
  const lineBreaks = sourceText.match(/\r\n|\r|\n/g)?.length ?? 0
  return lineBreaks + (/(?:\r\n|\r|\n)$/.test(sourceText) ? 0 : 1)
}

function createBudgetRecord(filePath, sourceText = readFileSync(filePath, 'utf8')): BudgetRecord {
  const relativePath = repositoryRelativePath(filePath)
  const budget = testFileLineBudget(relativePath)
  if (budget === undefined) throw new Error(`No file-size budget is configured for ${relativePath}`)

  return {
    filePath,
    relativePath,
    budget,
    lines: countSourceLines(sourceText),
  }
}

function budgetRecordsForFiles(files): BudgetRecord[] {
  return files.map((filePath) => createBudgetRecord(filePath))
}

function budgetBaselineFilePath() {
  return resolve(repositoryRoot, testFileBudgetBaselineRelativePath)
}

function validateBudgetBaselinePath(filePath, sourceName) {
  if (
    filePath.startsWith('/')
    || filePath.includes('\\')
    || filePath.split('/').some((segment) => segment.length === 0 || segment === '.' || segment === '..')
    || !isActiveNodeVitestRepositoryPath(filePath)
  ) {
    throw new Error(`${sourceName} has an invalid test-file path: ${filePath}`)
  }
}

function parseBudgetBaseline(sourceText, sourceName) {
  let parsed
  try {
    parsed = JSON.parse(sourceText)
  } catch (error) {
    throw new Error(`${sourceName} is not valid JSON: ${error instanceof Error ? error.message : String(error)}`)
  }

  if (parsed === null || Array.isArray(parsed) || typeof parsed !== 'object') {
    throw new Error(`${sourceName} must be an object from repository-relative test paths to allowed line counts`)
  }

  const entries = new Map<string, number>()
  for (const [filePath, allowedLines] of Object.entries(parsed as Record<string, number>)) {
    validateBudgetBaselinePath(filePath, sourceName)
    if (!Number.isSafeInteger(allowedLines)) {
      throw new Error(`${sourceName} must allow a positive integer line count for ${filePath}`)
    }
    const budget = testFileLineBudget(filePath)
    if (allowedLines <= budget) continue
    entries.set(filePath, allowedLines)
  }
  return entries
}

function readBudgetBaseline() {
  const baselinePath = budgetBaselineFilePath()
  if (!existsSync(baselinePath)) return new Map<string, number>()
  return parseBudgetBaseline(readFileSync(baselinePath, 'utf8'), testFileBudgetBaselineRelativePath)
}

function readBudgetBaselineFromGitTree(commit) {
  if (!gitFileExists(commit, testFileBudgetBaselineRelativePath)) {
    throw new Error(`${testFileBudgetBaselineRelativePath} must be committed before --budget-base-ref can compare Git history`)
  }
  return parseBudgetBaseline(
    readGitFile(commit, testFileBudgetBaselineRelativePath),
    `${commit}:${testFileBudgetBaselineRelativePath}`,
  )
}

function formatBudgetBaseline(entries) {
  return `${JSON.stringify(Object.fromEntries([...entries].sort(([left], [right]) => left.localeCompare(right))), null, 2)}\n`
}

function createBudgetViolation(relativePath, description, fix) {
  return {
    filePath: resolve(repositoryRoot, relativePath),
    line: 1,
    column: 1,
    rule: testFileBudgetRule,
    description,
    fix,
  }
}

function validateBudgetRecords(records: BudgetRecord[], baseline: Map<string, number>, scope) {
  const recordsByPath = new Map<string, BudgetRecord>(records.map((record) => [record.relativePath, record]))
  const violations = []

  for (const record of records) {
    if (budgetScopeForRepositoryPath(record.relativePath) !== scope) continue
    if (record.lines <= record.budget) continue
    const allowedLines = baseline.get(record.relativePath)
    if (allowedLines === undefined) {
      violations.push(createBudgetViolation(
        record.relativePath,
        `has ${record.lines} lines, exceeding the ${record.budget}-line limit without a baseline allowance`,
        `Split the file below ${record.budget} lines; do not add a new baseline entry.`,
      ))
      continue
    }
    if (record.lines > allowedLines) {
      violations.push(createBudgetViolation(
        record.relativePath,
        `has ${record.lines} lines, exceeding its baseline allowance of ${allowedLines} lines`,
        `Split the file or lower it to at most ${allowedLines} lines, then lower or remove the baseline entry.`,
      ))
    }
  }

  for (const [relativePath, allowedLines] of baseline) {
    if (budgetScopeForRepositoryPath(relativePath) !== scope) continue
    const record = recordsByPath.get(relativePath)
    if (record === undefined) {
      violations.push(createBudgetViolation(
        relativePath,
        `has a baseline allowance of ${allowedLines} lines but no active test file at that path`,
        'Remove the stale baseline entry; a renamed over-budget file must be split instead of receiving a new allowance.',
      ))
      continue
    }
    if (record.lines <= record.budget) {
      violations.push(createBudgetViolation(
        relativePath,
        `has ${record.lines} lines and no longer needs its ${allowedLines}-line baseline allowance`,
        'Remove the baseline entry now that the file satisfies the absolute limit.',
      ))
    }
  }

  return violations
}

function resolveGitCommit(ref, optionName) {
  if (ref.length === 0 || ref.startsWith('-')) throw new Error(`${optionName} must name a Git commit or ref`)
  try {
    return execFileSync('git', ['rev-parse', '--verify', `${ref}^{commit}`], {
      cwd: repositoryRoot,
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'pipe'],
    }).trim()
  } catch {
    throw new Error(`${optionName} does not resolve to a Git commit: ${ref}`)
  }
}

function readGitFile(commit, relativePath) {
  return execFileSync('git', ['show', `${commit}:${relativePath}`], {
    cwd: repositoryRoot,
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'pipe'],
  })
}

function gitFileExists(commit, relativePath) {
  try {
    execFileSync('git', ['cat-file', '-e', `${commit}:${relativePath}`], {
      cwd: repositoryRoot,
      stdio: 'ignore',
    })
    return true
  } catch {
    return false
  }
}

function collectBudgetRecordsFromGitTree(commit) {
  let output
  try {
    output = execFileSync('git', [
      'ls-tree',
      '-r',
      '-z',
      '--name-only',
      commit,
      '--',
      'packages/runner',
      'packages/web',
    ], {
      cwd: repositoryRoot,
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'pipe'],
    })
  } catch {
    throw new Error(`Could not read test files from trusted Git commit ${commit}`)
  }

  return output
    .split('\0')
    .filter((filePath) => filePath.length > 0 && isActiveNodeVitestRepositoryPath(filePath))
    .map((relativePath) => {
      const budget = testFileLineBudget(relativePath)
      if (budget === undefined) throw new Error(`No file-size budget is configured for ${relativePath}`)
      return {
        filePath: resolve(repositoryRoot, relativePath),
        relativePath,
        budget,
        lines: countSourceLines(readGitFile(commit, relativePath)),
      }
    })
}

function budgetBaselineFromRecords(records) {
  return new Map(records
    .filter((record) => record.lines > record.budget)
    .map((record) => [record.relativePath, record.lines]))
}

function gitRenameMap(sourceCommit, targetCommit) {
  let output
  try {
    output = execFileSync('git', [
      'diff',
      '--name-status',
      '-z',
      '--find-renames=90%',
      sourceCommit,
      targetCommit,
      '--',
      'packages/runner',
      'packages/web',
    ], {
      cwd: repositoryRoot,
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'pipe'],
    })
  } catch {
    throw new Error(`Could not read test-file renames from ${sourceCommit} to ${targetCommit}`)
  }

  const values = output.split('\0')
  const renames = new Map()
  const renamedTargets = new Set()
  for (let index = 0; index < values.length - 1;) {
    const status = values[index]
    index += 1
    if (status.length === 0) continue

    const sourcePath = values[index]
    index += 1
    if (status.startsWith('R') || status.startsWith('C')) {
      const targetPath = values[index]
      index += 1
      if (!status.startsWith('R')) continue
      if (renames.has(sourcePath) || renamedTargets.has(targetPath)) {
        throw new Error(`Ambiguous test-file rename from ${sourceCommit} to ${targetCommit}`)
      }
      renames.set(sourcePath, targetPath)
      renamedTargets.add(targetPath)
    }
  }
  return renames
}

function remapBudgetBaseline(sourceBaseline, currentRecords, renamedPaths, useCurrentHeadAllowance) {
  const remapped = new Map()

  for (const [sourcePath, sourceAllowedLines] of sourceBaseline) {
    const currentPath = renamedPaths.get(sourcePath) ?? sourcePath
    const currentRecord = currentRecords.get(currentPath)
    if (
      currentRecord === undefined
      || currentRecord.lines <= currentRecord.budget
      || testFileLineBudget(sourcePath) !== currentRecord.budget
    ) continue
    if (remapped.has(currentPath)) {
      throw new Error(`Multiple trusted baseline paths map to ${currentPath}`)
    }
    remapped.set(currentPath, useCurrentHeadAllowance ? currentRecord.lines : sourceAllowedLines)
  }

  return remapped
}

function remapBaselineToCurrentHead(sourceBaseline, sourceCommit, currentHead, useCurrentHeadAllowance) {
  const currentRecords = new Map(collectBudgetRecordsFromGitTree(currentHead)
    .map((record) => [record.relativePath, record]))
  return remapBudgetBaseline(
    sourceBaseline,
    currentRecords,
    gitRenameMap(sourceCommit, currentHead),
    useCurrentHeadAllowance,
  )
}

function trustedBudgetBaseline(sourceCommit, currentHead) {
  if (gitFileExists(sourceCommit, testFileBudgetBaselineRelativePath)) {
    const baseline = parseBudgetBaseline(
      readGitFile(sourceCommit, testFileBudgetBaselineRelativePath),
      `${sourceCommit}:${testFileBudgetBaselineRelativePath}`,
    )
    return remapBaselineToCurrentHead(baseline, sourceCommit, currentHead, false)
  }

  const sourceOffenders = budgetBaselineFromRecords(collectBudgetRecordsFromGitTree(sourceCommit))
  return remapBaselineToCurrentHead(sourceOffenders, sourceCommit, currentHead, true)
}

function baselineRegressionViolations(currentBaseline, trustedBaseline, scope) {
  const violations = []
  for (const [relativePath, allowedLines] of currentBaseline) {
    if (budgetScopeForRepositoryPath(relativePath) !== scope) continue
    const trustedAllowedLines = trustedBaseline.get(relativePath)
    if (trustedAllowedLines === undefined) {
      violations.push(createBudgetViolation(
        relativePath,
        `adds a ${allowedLines}-line baseline allowance that is absent from the trusted base`,
        'Do not add baseline entries. Split a newly over-budget file below its absolute limit.',
      ))
      continue
    }
    if (allowedLines > trustedAllowedLines) {
      violations.push(createBudgetViolation(
        relativePath,
        `raises its baseline allowance from ${trustedAllowedLines} to ${allowedLines} lines`,
        'Baseline allowances may only decrease; split the file instead.',
      ))
    }
  }
  return violations
}

function writeBudgetBaseline(sourceRef) {
  const baselinePath = budgetBaselineFilePath()
  if (existsSync(baselinePath)) {
    throw new Error(`${testFileBudgetBaselineRelativePath} already exists; bootstrap may only create it once`)
  }

  const sourceCommit = resolveGitCommit(sourceRef, '--source-ref')
  const currentHead = resolveGitCommit('HEAD', 'current HEAD')
  const baseline = trustedBudgetBaseline(sourceCommit, currentHead)
  writeFileSync(baselinePath, formatBudgetBaseline(baseline))
  console.log(`node test file budget baseline: wrote ${baseline.size} allowance(s) from ${sourceCommit} to committed ${currentHead}`)
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
    if (current === null || current === undefined) return
    if (callback(current)) {
      matched = true
      return
    }
    current.forEachChild(visit)
  }

  visit(node)
  return matched
}

function functionParameterNames(node) {
  return node.parameters
    .map((parameter) => ts.isIdentifier(parameter) ? parameter : unwrapExpression(parameter.name ?? parameter.left ?? parameter))
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
      node.forEachChild(visit)
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
    child.forEachChild(visit)
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
    node.forEachChild(visit)
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
    && isViObject(member.object)
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
    child.forEachChild(visit)
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
    node.forEachChild(visit)
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
    node.forEachChild(visit)
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

function scanRunnerSourceFileAst(
  filePath,
  sourceFile,
  { runnerRoot = resolve(repositoryRoot, 'packages/runner'), track = runnerDefaultTrack } = {},
) {
  const violations = []
  const timerBindings = localTimerPromiseBindings(sourceFile)
  const timeBindings = timeNowBindings(sourceFile)
  const elapsedBindings = elapsedTimeBindings(sourceFile, timeBindings)
  const systemProcessImports = systemProcessRunCommandImports(sourceFile, filePath, runnerRoot)

  function visit(node) {
    if (track === runnerDefaultTrack && isChildProcessImport(node)) {
      violations.push(createRunnerViolation(
        filePath,
        sourceFile,
        node,
        runnerChildProcessImportRule,
        'imports node:child_process in a default Runner test',
        'Move real process coverage to tests/integration and use an injected process fake in the default track.',
      ))
    }

    if (track === runnerDefaultTrack && isRunnerExecutableScriptImport(node, filePath, runnerRoot)) {
      violations.push(createRunnerViolation(
        filePath,
        sourceFile,
        node,
        runnerExecutableScriptImportRule,
        'imports an executable Runner module in a default test',
        'Test behavior through a hermetic seam, or move executable coverage to tests/integration.',
      ))
    }

    if (track === runnerDefaultTrack && ts.isAwaitExpression(node) && awaitedTimerPromise(node, timerBindings) && !hasExplicitFakeTimerAdvanceBefore(node, sourceFile)) {
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
      const title = historicalTicketTitle(node)
      if (title !== undefined) violations.push(createHistoricalTicketTitleViolation(filePath, sourceFile, node, title))

      const modifier = testModifier(node)
      if (modifier !== undefined && !isAllowedRunnerTestModifier(modifier, track)) {
        violations.push(createRunnerViolation(
          filePath,
          sourceFile,
          node,
          runnerTestModifierRule,
          `uses ${modifier.base}.${modifier.modifier} in a Runner ${track} test`,
          track === runnerIntegrationTrack
            ? 'Integration tests may use only it.skipIf/test.skipIf for platform-specific coverage.'
            : 'Do not disable default Runner tests; make the test hermetic instead.',
        ))
      }

      if (track === runnerDefaultTrack && isDefaultRunnerPlatformCommandCall(node, systemProcessImports)) {
        violations.push(createRunnerViolation(
          filePath,
          sourceFile,
          node,
          runnerExternalCommandRule,
          'runs git or process.execPath through system/process in a default Runner test',
          'Move real command coverage to tests/integration and keep default tests behind a fake process seam.',
        ))
      }

      if (track === runnerDefaultTrack && isProcessPolicyMock(node, filePath, runnerRoot)) {
        violations.push(createRunnerViolation(
          filePath,
          sourceFile,
          node,
          runnerProcessPolicyMockRule,
          'mocks system/process-policy in a default Runner test',
          'Use the installed default deny policy and inject a local process fake instead.',
        ))
      }

      if (track === runnerDefaultTrack && isRunnerHostSpec(filePath) && isViMethodCall(node, new Set(['waitFor']))) {
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
      if (track === runnerDefaultTrack && received !== undefined && (isElapsedExpression(received, timeBindings) || containsElapsedTimeBinding(received, elapsedBindings))) {
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

    node.forEachChild(visit)
  }

  visit(sourceFile)
  return violations
}

export function scanRunnerSourceFile(
  filePath,
  sourceText = readFileSync(filePath, 'utf8'),
  options = {},
) {
  return scanRunnerSourceFileAst(filePath, parseSourceFile(filePath, sourceText), options)
}

function checkRunnerTrackBoundaries(files, runnerRoot, track) {
  return {
    files,
    violations: files.flatMap((filePath) => scanRunnerSourceFile(
      filePath,
      readFileSync(filePath, 'utf8'),
      { runnerRoot, track },
    )),
  }
}

export function checkRunnerIntegrationTestBoundaries(runnerRoot = resolve(repositoryRoot, 'packages/runner')) {
  return checkRunnerTrackBoundaries(
    collectRunnerIntegrationVitestFiles(runnerRoot),
    runnerRoot,
    runnerIntegrationTrack,
  )
}

export function checkRunnerTestBoundaries(runnerRoot = resolve(repositoryRoot, 'packages/runner')) {
  const defaultResult = checkRunnerTrackBoundaries(
    collectRunnerVitestFiles(runnerRoot),
    runnerRoot,
    runnerDefaultTrack,
  )
  const integrationResult = checkRunnerIntegrationTestBoundaries(runnerRoot)
  return {
    files: [...defaultResult.files, ...integrationResult.files],
    violations: [...defaultResult.violations, ...integrationResult.violations],
  }
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
  let budgetBaseRef = null
  let writeBudgetBaseline = false
  let sourceRef = null

  for (let index = 0; index < args.length; index += 1) {
    const argument = args[index]
    if (argument === '--scope') {
      const value = args[index + 1]
      if (value === undefined) throw new Error('--scope requires web or runner')
      scope = value
      index += 1
      continue
    }
    if (argument === '--budget-base-ref') {
      const value = args[index + 1]
      if (value === undefined) throw new Error('--budget-base-ref requires a Git ref')
      budgetBaseRef = value
      index += 1
      continue
    }
    if (argument === '--write-budget-baseline') {
      writeBudgetBaseline = true
      continue
    }
    if (argument === '--source-ref') {
      const value = args[index + 1]
      if (value === undefined) throw new Error('--source-ref requires a Git ref')
      sourceRef = value
      index += 1
      continue
    }
    throw new Error(`Unknown argument: ${argument}`)
  }

  if (writeBudgetBaseline) {
    if (scope !== null || budgetBaseRef !== null || sourceRef === null) {
      throw new Error('Usage: tsx scripts/check-node-test-boundaries.ts --write-budget-baseline --source-ref <git-ref>')
    }
    if (sourceRef.length === 0) throw new Error('--source-ref requires a Git ref')
    return { writeBudgetBaseline, sourceRef }
  }
  if (sourceRef !== null) {
    throw new Error('--source-ref is only valid with --write-budget-baseline')
  }
  if (scope !== 'web' && scope !== 'runner') {
    throw new Error('Usage: tsx scripts/check-node-test-boundaries.ts --scope web|runner [--budget-base-ref <git-ref>]')
  }
  if (budgetBaseRef !== null && budgetBaseRef.length === 0) throw new Error('--budget-base-ref requires a Git ref')
  return { scope, budgetBaseRef }
}

function main() {
  const options = parseArguments(process.argv.slice(2))
  if (options.writeBudgetBaseline) {
    writeBudgetBaseline(options.sourceRef)
    return
  }

  const { scope, budgetBaseRef: argumentBudgetBaseRef } = options
  const budgetBaseRef = argumentBudgetBaseRef ?? process.env.NODE_TEST_BUDGET_BASE_REF ?? null
  if (budgetBaseRef !== null && budgetBaseRef.length === 0) {
    throw new Error('NODE_TEST_BUDGET_BASE_REF must not be empty')
  }

  const { files, violations } = scope === 'runner'
    ? checkRunnerTestBoundaries()
    : checkWebTestBoundaries()
  const baseline = readBudgetBaseline()
  const budgetViolations = validateBudgetRecords(budgetRecordsForFiles(files), baseline, scope)
  if (budgetBaseRef !== null) {
    const trustedCommit = resolveGitCommit(budgetBaseRef, '--budget-base-ref')
    const currentHead = resolveGitCommit('HEAD', 'current HEAD')
    const committedBaseline = readBudgetBaselineFromGitTree(currentHead)
    const committedRecords = collectBudgetRecordsFromGitTree(currentHead)
    budgetViolations.push(...validateBudgetRecords(committedRecords, committedBaseline, scope))
    budgetViolations.push(...baselineRegressionViolations(
      committedBaseline,
      trustedBudgetBaseline(trustedCommit, currentHead),
      scope,
    ))
  }
  const allViolations = [...violations, ...budgetViolations]
  const scopeName = scope === 'runner' ? 'Runner' : 'Web'
  if (allViolations.length === 0) {
    console.log(`node test boundaries: checked ${files.length} active ${scopeName} Vitest files and file-size budgets`)
    return
  }

  printViolations(allViolations)
  console.error(`${allViolations.length} node test boundary or file-size budget violation(s) found in ${files.length} active ${scopeName} Vitest files.`)
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
