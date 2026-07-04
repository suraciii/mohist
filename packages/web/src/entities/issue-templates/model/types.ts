export interface IssueTemplateInfo {
  id: string
  name: string
  description: string
  source: 'builtin' | 'custom'
}

export interface IssueTemplateDetail {
  id: string
  name: string
  description: string
  body: string
  source: 'builtin' | 'custom'
}
