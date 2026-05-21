export interface ExecutableTask {
  taskId: string;
  title: string;
  uses?: string;
  prompt?: string;
  input?: unknown;
}
