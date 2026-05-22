export type StageCheckStatus = 'pending' | 'passed' | 'failed';

export class StageCheck {
  status: StageCheckStatus = 'pending';
  message: string | null = null;
  output: unknown | null = null;

  constructor(
    readonly name: string,
    readonly title: string,
    readonly uses?: string,
    readonly withInput?: Record<string, unknown>,
  ) {}

  reset(): void {
    this.status = 'pending';
    this.message = null;
    this.output = null;
  }

  pass(): void {
    this.status = 'passed';
  }

  fail(): void {
    this.status = 'failed';
  }
}
