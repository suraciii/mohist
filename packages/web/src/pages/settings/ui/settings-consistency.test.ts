import { describe, expect, it } from 'vitest'
import { readdirSync, readFileSync, statSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const currentDir = dirname(fileURLToPath(import.meta.url))
const settingsDir = join(currentDir, '..')
const modelSelectPath = join(currentDir, '..', '..', '..', 'shared', 'ui', 'ModelSelect.tsx')

function collectSourceFiles(dir: string): string[] {
  return readdirSync(dir).flatMap((entry) => {
    const path = join(dir, entry)
    const stats = statSync(path)

    if (stats.isDirectory()) {
      return collectSourceFiles(path)
    }

    return /(?<!\.test)\.tsx?$/.test(entry) ? [path] : []
  })
}

function matchingFiles(files: string[], pattern: RegExp): string[] {
  return files.filter((file) => pattern.test(readFileSync(file, 'utf8')))
}

describe('settings visual consistency contract', () => {
  const settingsSourceFiles = collectSourceFiles(settingsDir)

  it('does not use forbidden settings text color tokens', () => {
    const hardcodedGrayToken = ['text', 'gray', ''].join('-')
    const foregroundOpacityToken = ['text-foreground', '(?:8|75|80)'].join('\\/')
    const forbiddenTextTokens = new RegExp(`${hardcodedGrayToken}|${foregroundOpacityToken}`)

    expect(matchingFiles(settingsSourceFiles, forbiddenTextTokens)).toEqual([])
  })

  it('does not define inline svg icons in settings source', () => {
    expect(matchingFiles(settingsSourceFiles, /<svg/)).toEqual([])
  })

  it('keeps page-title styling owned by SettingsSection', () => {
    const locallyStyledPageTitle = /<h3\s+className="text-sm font-medium text-foreground"/
    const pageSectionFiles = settingsSourceFiles.filter((file) =>
      /(?:AgentSettingsSection|AiSettingsSection|RepositoriesSection|SystemSettingsSection|TemplatesSection|WorkflowProfilesSection)\.tsx$/.test(file),
    )

    expect(matchingFiles(pageSectionFiles, locallyStyledPageTitle)).toEqual([])
  })

  it('keeps ModelSelect icon rendering delegated to lucide-react', () => {
    const source = readFileSync(modelSelectPath, 'utf8')

    expect(source).not.toMatch(/<svg/)
    expect(source).not.toMatch(/(?:function|const)\s+(?:SearchIcon|ChevronDownIcon|XIcon)\b/)
  })

  it('keeps ModelSelect aligned with settings text tokens', () => {
    const source = readFileSync(modelSelectPath, 'utf8')
    const foregroundOpacityToken = /text-foreground\/(?:8|75|80)/

    expect(source).not.toMatch(foregroundOpacityToken)
  })
})
