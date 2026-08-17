export interface AddRepositoryInput {
  name: string
  gitUrl: string
  baseBranch?: string
  setDefault?: boolean
}

export interface Repository {
  name: string
  gitUrl: string
  baseBranch: string
  isDefault: boolean
}

export interface ProjectDefaultExecutionConfig {
  runtime: 'opencode' | 'pi'
  model: string
  variant?: string | null
}

export interface Project {
  id: string
  name: string
  createdAt: string
  updatedAt: string
  repositories: Repository[]
  defaultExecutionConfig?: ProjectDefaultExecutionConfig | null
}
