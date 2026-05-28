export interface Repository {
  name: string
  path?: string
  remote?: string
  baseBranch: string
  isDefault: boolean
}

export interface Project {
  id: string
  name: string
  path: string
  createdAt: string
  updatedAt: string
  repositories: Repository[]
}

export interface DirEntry {
  name: string
  absolute: string
}
