export interface SystemTemplate {
  key: string
  displayName: string
  description: string
  tags: string[]
  stage: string | null
  body: string
}

export type ProjectTemplateSource = 'system' | 'project-override' | 'project-new'

export interface ProjectTemplate {
  key: string
  displayName: string
  description: string
  tags: string[]
  stage: string | null
  body: string
  source: ProjectTemplateSource
}

export interface ProjectTemplateOverride {
  projectId: string
  key: string
  displayName: string
  description: string
  tags: string[]
  stage: string | null
  body: string
  updatedAt: string
}

export interface ProjectTemplateOverridePayload {
  displayName: string
  description: string
  tags: string[]
  stage: string | null
  body: string
}

export interface PreviewResponse {
  rendered: string
  missingVariables: string[]
  depth: number
}

export interface ExtractVariablesResponse {
  variables: string[]
}
