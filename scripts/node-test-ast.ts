import { parse } from '@babel/parser'

function isNode(value) {
  return value !== null && typeof value === 'object' && typeof value.type === 'string'
}

function define(node, name, value) {
  Object.defineProperty(node, name, { configurable: true, value })
}

function childNodes(node) {
  const children = []
  for (const [name, value] of Object.entries(node)) {
    if (name === 'comments' || name === 'extra' || name === 'loc') continue
    if (Array.isArray(value)) {
      for (const item of value) if (isNode(item)) children.push(item)
      continue
    }
    if (isNode(value)) children.push(value)
  }
  return children
}

function lineAndCharacter(sourceText, position) {
  let line = 0
  let lineStart = 0
  for (let index = 0; index < position; index += 1) {
    if (sourceText[index] !== '\n') continue
    line += 1
    lineStart = index + 1
  }
  return { line, character: position - lineStart }
}

function enrich(node, parent, sourceFile) {
  if (!isNode(node)) return

  define(node, 'parent', parent)
  define(node, 'forEachChild', (visitor) => {
    for (const child of childNodes(node)) visitor(child)
  })
  define(node, 'getSourceFile', () => sourceFile)
  define(node, 'getStart', () => node.start)
  define(node, 'getText', () => sourceFile.text.slice(node.start, node.end))
  define(node, 'getFullText', () => sourceFile.text.slice(node.start, node.end))

  if (node.type === 'Identifier') define(node, 'text', node.name)
  if (node.type === 'StringLiteral') define(node, 'text', node.value)
  if (node.type === 'TemplateLiteral' && node.expressions.length === 0) {
    define(node, 'text', node.quasis[0]?.value.cooked ?? '')
  }

  if (node.type === 'MemberExpression' || node.type === 'OptionalMemberExpression') {
    if (node.computed) define(node, 'argumentExpression', node.property)
    else define(node, 'name', node.property)
    define(node, 'expression', node.object)
  }

  if (node.type === 'CallExpression' || node.type === 'OptionalCallExpression' || node.type === 'NewExpression') {
    define(node, 'expression', node.callee)
  }
  if (node.type === 'AwaitExpression') define(node, 'expression', node.argument)
  if (node.type === 'VariableDeclarator') {
    define(node, 'name', node.id)
    define(node, 'initializer', node.init ?? undefined)
  }
  if (node.type === 'ObjectPattern') define(node, 'elements', node.properties)
  if (node.type === 'ArrowFunctionExpression' || node.type === 'FunctionExpression' || node.type === 'FunctionDeclaration') {
    define(node, 'parameters', node.params)
  }
  if (node.type === 'ObjectProperty') {
    define(node, 'name', node.key)
    define(node, 'propertyName', node.key)
  }
  if (node.type === 'ImportSpecifier') {
    define(node, 'name', node.local)
    define(node, 'propertyName', node.imported)
  }
  if (node.type === 'ImportDeclaration') {
    define(node, 'moduleSpecifier', node.source)
    const defaultSpecifier = node.specifiers.find((specifier) => specifier.type === 'ImportDefaultSpecifier')
    const namespaceSpecifier = node.specifiers.find((specifier) => specifier.type === 'ImportNamespaceSpecifier')
    const namedSpecifiers = node.specifiers.filter((specifier) => specifier.type === 'ImportSpecifier')
    const namedBindings = namespaceSpecifier === undefined
      ? namedSpecifiers.length === 0 ? undefined : { type: 'NamedImports', elements: namedSpecifiers }
      : { type: 'NamespaceImport', name: namespaceSpecifier.local }
    define(node, 'importClause', defaultSpecifier === undefined && namedBindings === undefined
      ? undefined
      : { type: 'ImportClause', name: defaultSpecifier?.local, namedBindings })
  }
  if (node.type === 'BinaryExpression' || node.type === 'LogicalExpression' || node.type === 'AssignmentExpression') {
    define(node, 'operatorToken', { kind: node.operator })
  }

  for (const child of childNodes(node)) enrich(child, node, sourceFile)
}

export function parseSourceFile(filePath, sourceText) {
  const parsed = parse(sourceText, {
    sourceFilename: filePath,
    sourceType: 'unambiguous',
    plugins: ['typescript', 'jsx', 'importAttributes'],
  })
  const sourceFile = parsed.program
  define(sourceFile, 'fileName', filePath)
  define(sourceFile, 'text', sourceText)
  define(sourceFile, 'statements', sourceFile.body)
  define(sourceFile, 'comments', parsed.comments ?? [])
  define(sourceFile, 'getLineAndCharacterOfPosition', (position) => lineAndCharacter(sourceText, position))
  enrich(sourceFile, undefined, sourceFile)
  return sourceFile
}

const hasType = (...types) => (node) => isNode(node) && types.includes(node.type)
const assignmentOperators = new Set(['=', '+=', '-=', '*=', '/=', '%=', '**=', '&&=', '||=', '??=', '<<=', '>>=', '>>>=', '&=', '|=', '^='])

export const ts = {
  SyntaxKind: {
    EqualsEqualsToken: '==',
    EqualsEqualsEqualsToken: '===',
    ExclamationEqualsToken: '!=',
    ExclamationEqualsEqualsToken: '!==',
    LessThanToken: '<',
    LessThanEqualsToken: '<=',
    GreaterThanToken: '>',
    GreaterThanEqualsToken: '>=',
    MinusToken: '-',
    ImportKeyword: 'Import',
  },
  isArrayBindingPattern: hasType('ArrayPattern'),
  isArrayLiteralExpression: hasType('ArrayExpression'),
  isArrowFunction: hasType('ArrowFunctionExpression'),
  isAsExpression: hasType('TSAsExpression'),
  isAssignmentOperator: (kind) => assignmentOperators.has(kind),
  isAwaitExpression: hasType('AwaitExpression'),
  isBinaryExpression: hasType('AssignmentExpression', 'BinaryExpression', 'LogicalExpression'),
  isBindingElement: hasType('ObjectProperty'),
  isBlock: hasType('BlockStatement'),
  isCallExpression: hasType('CallExpression', 'OptionalCallExpression'),
  isCatchClause: hasType('CatchClause'),
  isClassDeclaration: hasType('ClassDeclaration'),
  isElementAccessExpression: (node) => hasType('MemberExpression', 'OptionalMemberExpression')(node) && node.computed,
  isEnumDeclaration: hasType('TSEnumDeclaration'),
  isEnumMember: hasType('TSEnumMember'),
  isExportSpecifier: hasType('ExportSpecifier'),
  isExpression: (node) => isNode(node) && (
    node.type.endsWith('Expression')
    || ['Identifier', 'StringLiteral', 'NumericLiteral', 'BooleanLiteral', 'BigIntLiteral', 'NullLiteral', 'RegExpLiteral', 'TemplateLiteral', 'ThisExpression', 'Super', 'JSXElement', 'JSXFragment'].includes(node.type)
  ),
  isExternalModuleReference: (_node) => false,
  isFunctionDeclaration: hasType('FunctionDeclaration'),
  isFunctionExpression: hasType('FunctionExpression'),
  isGetAccessorDeclaration: (node) => isNode(node) && node.type === 'ClassMethod' && node.kind === 'get',
  isIdentifier: hasType('Identifier'),
  isImportClause: hasType('ImportClause'),
  isImportDeclaration: hasType('ImportDeclaration'),
  isImportEqualsDeclaration: hasType('TSImportEqualsDeclaration'),
  isImportSpecifier: hasType('ImportSpecifier'),
  isInterfaceDeclaration: hasType('TSInterfaceDeclaration'),
  isMethodDeclaration: hasType('ClassMethod', 'ObjectMethod'),
  isMethodSignature: hasType('TSMethodSignature'),
  isNamedImports: hasType('NamedImports'),
  isNamespaceImport: hasType('NamespaceImport'),
  isNewExpression: hasType('NewExpression'),
  isNoSubstitutionTemplateLiteral: (node) => isNode(node) && node.type === 'TemplateLiteral' && node.expressions.length === 0,
  isNonNullExpression: hasType('TSNonNullExpression'),
  isObjectBindingPattern: hasType('ObjectPattern'),
  isOmittedExpression: (node) => node === null || node === undefined || (isNode(node) && node.type === 'ArgumentPlaceholder'),
  isParameterDeclaration: () => false,
  isParenthesizedExpression: hasType('ParenthesizedExpression'),
  isPartiallyEmittedExpression: (_node) => false,
  isPropertyAccessExpression: (node) => hasType('MemberExpression', 'OptionalMemberExpression')(node) && !node.computed,
  isPropertyAssignment: hasType('ObjectProperty'),
  isPropertyDeclaration: hasType('ClassProperty'),
  isPropertySignature: hasType('TSPropertySignature'),
  isQualifiedName: hasType('TSQualifiedName'),
  isReturnStatement: hasType('ReturnStatement'),
  isSatisfiesExpression: hasType('TSSatisfiesExpression'),
  isSetAccessorDeclaration: (node) => isNode(node) && node.type === 'ClassMethod' && node.kind === 'set',
  isShorthandPropertyAssignment: (node) => isNode(node) && node.type === 'ObjectProperty' && node.shorthand,
  isSourceFile: hasType('Program'),
  isStatement: (node) => isNode(node) && (
    node.type.endsWith('Statement')
    || ['VariableDeclaration', 'FunctionDeclaration', 'ClassDeclaration', 'ImportDeclaration', 'ExportNamedDeclaration', 'ExportDefaultDeclaration', 'TSEnumDeclaration', 'TSInterfaceDeclaration', 'TSTypeAliasDeclaration'].includes(node.type)
  ),
  isStringLiteral: hasType('StringLiteral'),
  isTypeAliasDeclaration: hasType('TSTypeAliasDeclaration'),
  isTypeAssertion: hasType('TSTypeAssertion'),
  isTypeNode: (node) => isNode(node)
    && node.type.startsWith('TS')
    && !['TSAsExpression', 'TSNonNullExpression', 'TSSatisfiesExpression'].includes(node.type),
  isVariableDeclaration: hasType('VariableDeclarator'),
}
