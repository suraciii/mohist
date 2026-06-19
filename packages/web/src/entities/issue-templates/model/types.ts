export interface IssueTemplateInfo {
  id: string
  name: string
  about: string
  isDefault: boolean
  suitableFor: string[]
  source: 'builtin' | 'custom'
}

export interface IssueTemplateSection {
  title: string
  guidance: string
  placeholder: string
}

export interface IssueTemplateDefaults {
  labels?: Record<string, string> | null
  risk?: string | null
  workflow?: string | null
}

export interface IssueTemplateDetail {
  id: string
  name: string
  about: string
  isDefault: boolean
  suitableFor: string[]
  defaults: IssueTemplateDefaults | null
  sections: IssueTemplateSection[]
  source: 'builtin' | 'custom'
}
