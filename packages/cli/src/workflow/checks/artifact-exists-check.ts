import * as fs from 'fs';
import * as path from 'path';
import type { Check, CheckContext, CheckResult } from './index';

export class ArtifactExistsCheck implements Check {
  constructor(
    public readonly name: string,
    private readonly artifactPath: string,
  ) {}

  async run(ctx: CheckContext): Promise<CheckResult> {
    const roots = [ctx.changeDir, ctx.acpOptions.cwd].filter((value): value is string => Boolean(value));
    const matched = roots.find(root => fs.existsSync(path.join(root, this.artifactPath)));
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
}
