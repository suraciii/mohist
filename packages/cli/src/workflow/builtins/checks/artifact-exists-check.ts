import * as fs from 'fs';
import * as path from 'path';
import type { Check, CheckContext, CheckResult } from '@mohist/workflow/checks';

export class ArtifactExistsCheck implements Check {
  constructor(
    public readonly name: string,
    private readonly artifactPath: string,
  ) {}

  async run(ctx: CheckContext): Promise<CheckResult> {
    const matched = this.findExistingPath(ctx);
    if (matched) {
      return {
        name: this.name,
        status: 'pass',
        message: `${this.artifactPath} exists`,
        output: { kind: 'artifact-exists', path: this.artifactPath, root: matched },
      };
    }
    return {
      name: this.name,
      status: 'fail',
      message: `${this.artifactPath} not found`,
      output: { kind: 'artifact-exists', path: this.artifactPath },
    };
  }

  private findExistingPath(ctx: CheckContext): string | undefined {
    if (path.isAbsolute(this.artifactPath)) {
      return fs.existsSync(this.artifactPath) ? this.artifactPath : undefined;
    }
    const roots = [ctx.changeDir, ctx.acpOptions.cwd].filter((value): value is string => Boolean(value));
    return roots.find(root => fs.existsSync(path.join(root, this.artifactPath)));
  }
}
