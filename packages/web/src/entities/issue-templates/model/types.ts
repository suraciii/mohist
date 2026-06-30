export interface IssueTemplateInfo {
  id: string
  name: string
  description: string
  source: 'builtin' | 'custom'
}

export interface IssueTemplateSection {
  title: string
  guidance: string
  placeholder: string
}

export interface IssueTemplateDetail {
  id: string
  name: string
  description: string
  sections: IssueTemplateSection[]
  source: 'builtin' | 'custom'
}
