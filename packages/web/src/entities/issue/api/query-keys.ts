export type IssueListParams = {
  stage?: string
  label?: string
  projectId?: string
  archived?: boolean
  all?: boolean
  repository?: string
  parent?: number
}

export const issueListKeys = {
  all: ['issue-list'] as const,
  project: (projectId?: string | null) => ['issue-list', projectId ?? null] as const,
  list: (params?: IssueListParams) => ['issue-list', params?.projectId ?? null, params ?? null] as const,
  archived: (projectId?: string | null) => ['issue-list', projectId ?? null, 'archived'] as const,
}

export const issueDetailKeys = {
  detail: (projectId?: string | null, issueNumber?: number) =>
    ['issue-detail', projectId ?? null, issueNumber ?? null] as const,
}

export const workflowRunKeys = {
  detail: (workflowRunId?: string | null) => ['workflow-run', workflowRunId ?? null] as const,
}

export const issueWorkflowKeys = {
  root: (projectId?: string | null, issueNumber?: number) =>
    ['issue-workflow', projectId ?? null, issueNumber ?? null] as const,
  taskLog: (
    projectId: string | null | undefined,
    issueNumber: number,
    taskId: string | null,
    workflowRunId?: string | null,
    params?: unknown,
  ) =>
    [
      'issue-workflow',
      projectId ?? null,
      issueNumber,
      'task-log',
      taskId,
      workflowRunId ?? null,
      params ?? null,
    ] as const,
  timeline: (projectId?: string | null, issueNumber?: number) =>
    ['issue-workflow', projectId ?? null, issueNumber ?? null, 'timeline'] as const,
  events: (projectId?: string | null, issueNumber?: number) =>
    ['issue-workflow', projectId ?? null, issueNumber ?? null, 'events'] as const,
  workspace: (projectId?: string | null, issueNumber?: number) =>
    ['issue-workflow', projectId ?? null, issueNumber ?? null, 'workspace'] as const,
  diff: (projectId?: string | null, issueNumber?: number) =>
    ['issue-workflow', projectId ?? null, issueNumber ?? null, 'diff'] as const,
  commits: (projectId?: string | null, issueNumber?: number, hash?: string | null) =>
    ['issue-workflow', projectId ?? null, issueNumber ?? null, 'commits', hash ?? null] as const,
  profileYaml: (projectId?: string | null, issueNumber?: number) =>
    ['issue-workflow', projectId ?? null, issueNumber ?? null, 'profile-yaml'] as const,
  session: (projectId?: string | null, issueNumber?: number, kind?: string, id?: string | null, extra?: unknown) =>
    ['issue-workflow', projectId ?? null, issueNumber ?? null, kind ?? null, id ?? null, extra ?? null] as const,
}

export const issueArtifactKeys = {
  root: (projectId?: string | null, issueNumber?: number) =>
    ['issue-artifacts', projectId ?? null, issueNumber ?? null] as const,
  list: (projectId: string | null | undefined, issueNumber: number, workflowRunId?: string | null, params?: unknown) =>
    ['issue-artifacts', projectId ?? null, issueNumber, 'list', workflowRunId ?? null, params ?? null] as const,
  content: (projectId: string | null | undefined, issueNumber: number, artifactId: string, options?: unknown) =>
    ['issue-artifacts', projectId ?? null, issueNumber, 'content', artifactId, options ?? null] as const,
}

export const issueCandidateKeys = {
  project: (projectId?: string | null) => ['issue-candidates', projectId ?? null] as const,
}
