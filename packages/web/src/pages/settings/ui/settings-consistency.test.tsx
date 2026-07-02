// @vitest-environment jsdom
import { describe, expect, it } from 'vitest'
import { readdirSync, readFileSync, statSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { SETTINGS_SECTIONS, type SettingsSectionKey } from '../lib/sections'

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
    // Forbid opacity-based foreground tokens that fall below the contrast
    // floor used by Settings (>= 4.5:1). The accessibility-tuned `/85` value
    // introduced for section descriptions in PR #118 meets that target and is
    // allowed; the older `/8`, `/75`, and `/80` values are forbidden. Word
    // boundaries keep `/85` from tripping the `/8` branch.
    const foregroundOpacityToken = /text-foreground\/(?:8|75|80)\b/
    const forbiddenTextTokens = new RegExp(`${hardcodedGrayToken}|${foregroundOpacityToken.source}`)

    expect(matchingFiles(settingsSourceFiles, forbiddenTextTokens)).toEqual([])
  })

  it('does not define inline svg icons in settings source', () => {
    expect(matchingFiles(settingsSourceFiles, /<svg/)).toEqual([])
  })

  it('keeps page-title styling owned by SettingsSection', () => {
    const locallyStyledPageTitle = /<h2\s+className="text-sm font-medium text-foreground"/
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
    const foregroundOpacityToken = /text-foreground\/(?:8|75|80)(?!\d)/

    expect(source).not.toMatch(foregroundOpacityToken)
  })
})

describe('settings section heading is sourced from sections SOT (T-005)', () => {
  const settingsSourceFiles = collectSourceFiles(settingsDir)

  function fileFor(key: SettingsSectionKey): string {
    switch (key) {
      case 'ai':
        return 'AiSettingsSection.tsx'
      case 'agent':
        return 'AgentSettingsSection.tsx'
      case 'repositories':
        return 'RepositoriesSection.tsx'
      case 'workflows':
        return 'WorkflowProfilesSection.tsx'
      case 'templates':
        return 'TemplatesSection.tsx'
      case 'label-catalog':
        return 'LabelCatalogSection.tsx'
      case 'inbox':
        return 'InboxSubscriptionSection.tsx'
      case 'system':
        return 'SystemSettingsSection.tsx'
      case 'preferences':
        return 'PreferencesSection.tsx'
    }
  }

  it('every section component imports getSectionMeta from the lib SOT', () => {
    const sectionComponents = settingsSourceFiles.filter((file) =>
      /(?:AgentSettingsSection|AiSettingsSection|InboxSubscriptionSection|LabelCatalogSection|PreferencesSection|RepositoriesSection|SystemSettingsSection|TemplatesSection|WorkflowProfilesSection)\.tsx$/.test(file),
    )

    expect(sectionComponents.length).toBe(9)

    const offenders = sectionComponents.filter(
      (file) => !/import\s*\{[^}]*getSectionMeta[^}]*\}\s*from\s*['"]\.\.\/lib\/sections['"]/.test(readFileSync(file, 'utf8')),
    )

    expect(offenders).toEqual([])
  })

  for (const key of SETTINGS_SECTIONS.map((entry) => entry.key)) {
    const navLabel = SETTINGS_SECTIONS.find((entry) => entry.key === key)?.label ?? ''

    it(`section "${key}" reads its heading from getSectionMeta('${key}') (which yields "${navLabel}")`, () => {
      const sourceFile = settingsSourceFiles.find((file) => file.endsWith(`/pages/settings/ui/${fileFor(key)}`))
      expect(sourceFile, `expected source file for ${key}`).toBeTruthy()

      const source = readFileSync(sourceFile!, 'utf8')
      expect(source).toMatch(new RegExp(`getSectionMeta\\(\\s*['"]${key}['"]\\s*\\)`))
    })
  }
})

describe('settings typography baseline (T-008)', () => {
  const settingsSourceFiles = collectSourceFiles(settingsDir)
  const settingsSectionPath = settingsSourceFiles.find((file) => file.endsWith('/pages/settings/ui/SettingsSection.tsx'))

  it('SettingsSection is the single source of truth for the page-level heading and description', () => {
    expect(settingsSectionPath, 'expected SettingsSection.tsx to exist').toBeTruthy()

    const source = readFileSync(settingsSectionPath!, 'utf8')
    expect(source).toMatch(/<h2[^>]*className="[^"]*\btext-balance\b/)
    expect(source).toMatch(/<p[^>]*className="[^"]*\btext-pretty\b/)
  })

  it('per-section files do not duplicate text-balance / text-pretty on their own headings or descriptions', () => {
    const pageSectionFiles = settingsSourceFiles.filter((file) =>
      /(?:AgentSettingsSection|AiSettingsSection|InboxSubscriptionSection|LabelCatalogSection|PreferencesSection|RepositoriesSection|SystemSettingsSection|TemplatesSection|WorkflowProfilesSection)\.tsx$/.test(file),
    )

    const offenders = pageSectionFiles.flatMap((file) => {
      const source = readFileSync(file, 'utf8')
      const matches: string[] = []
      if (/\btext-balance\b/.test(source)) matches.push(`${file}: text-balance`)
      if (/\btext-pretty\b/.test(source)) matches.push(`${file}: text-pretty`)
      return matches
    })

    expect(offenders).toEqual([])
  })

  it('System InfoRow values apply tabular-nums on the mono-spaced value span', () => {
    const source = readFileSync(
      settingsSourceFiles.find((file) => file.endsWith('/pages/settings/ui/SystemSettingsSection.tsx'))!,
      'utf8',
    )

    expect(source).toMatch(/font-mono[^"]*tabular-nums|tabular-nums[^"]*font-mono/)
  })

  it('Agent numeric inputs, unit spans, and mono data rows apply tabular-nums', () => {
    const source = readFileSync(
      settingsSourceFiles.find((file) => file.endsWith('/pages/settings/ui/AgentSettingsSection.tsx'))!,
      'utf8',
    )

    const inputMatch = source.match(/<Input\b[\s\S]*?className="w-24[^"]*"/)
    expect(inputMatch, 'expected Agent numeric Input with w-24 class').toBeTruthy()
    expect(inputMatch![0]).toMatch(/\btabular-nums\b/)

    const unitMatch = source.match(/<span[^>]*>{unit}<\/span>/)
    expect(unitMatch, 'expected Agent unit span rendering {unit}').toBeTruthy()
    expect(unitMatch![0]).toMatch(/\btabular-nums\b/)

    const timeoutDiagramMatch = source.match(/<pre\b[\s\S]*?>\{lines\.join\('\\n'\)\}<\/pre>/)
    expect(timeoutDiagramMatch, 'expected Agent timeout diagram mono row').toBeTruthy()
    expect(timeoutDiagramMatch![0]).toMatch(/\btabular-nums\b/)
  })

  it('page title does not carry tabular-nums (numeric columns / mono rows only)', () => {
    expect(settingsSectionPath).toBeTruthy()
    const source = readFileSync(settingsSectionPath!, 'utf8')
    const titleMatch = source.match(/<h2[^>]*className="([^"]+)"/)
    expect(titleMatch, 'expected SettingsSection heading element').toBeTruthy()
    expect(titleMatch![1]).not.toMatch(/\btabular-nums\b/)
  })

  it('typography pass introduces no animation or gradient effects on touched lines', () => {
    // The T-008 contract: text-balance / text-pretty / tabular-nums only. Pre-existing
    // motion (Loader2Icon.animate-spin, SectionState skeleton.animate-pulse) predates
    // this pass and is not in scope. We assert the lines T-008 touches don't also add
    // motion/gradient tokens.
    const forbiddenNewTokens = /\b(?:animate-(?!none\b)|motion-safe:|motion-reduce:|bg-gradient-\w+|gradient-to-\w+)\b/
    const allowedTypographyTokens = /\b(?:text-balance|text-pretty|tabular-nums)\b/

    const typographyPassFiles = [
      settingsSectionPath!,
      settingsSourceFiles.find((file) => file.endsWith('/pages/settings/ui/SystemSettingsSection.tsx'))!,
      settingsSourceFiles.find((file) => file.endsWith('/pages/settings/ui/AgentSettingsSection.tsx'))!,
    ]

    const offenders = typographyPassFiles.flatMap((file) => {
      const lines = readFileSync(file, 'utf8').split('\n')
      return lines
        .filter((line) => allowedTypographyTokens.test(line))
        .filter((line) => forbiddenNewTokens.test(line))
        .map((line) => `${file}: ${line.trim()}`)
    })

    expect(offenders).toEqual([])
  })
})
