import * as fs from 'fs';
import type { Check, CheckContext, CheckResult } from './index';
import { extractFixSuggestions, parseDimensions } from '../utils';
import { Log } from '../../util/log';

const log = Log.create({ service: 'artifact-marker-check' });

export class ArtifactMarkerCheck implements Check {
  constructor(
    public readonly name: string,
    private readonly filePath: string,
    private readonly expectMarker: string,
  ) {}

  async run(ctx: CheckContext): Promise<CheckResult> {
    if (!fs.existsSync(this.filePath)) {
      return {
        name: this.name,
        status: 'fail',
        message: `${this.filePath} not found`,
        output: { kind: 'artifact-marker', path: this.filePath, expect: this.expectMarker },
      };
    }
    const content = fs.readFileSync(this.filePath, 'utf-8');
    const matchedMarker = markerIn(content, [this.expectMarker]);
    const anyPromiseMarker = markerIn(content, ['<promise>PASS</promise>', '<promise>FAIL</promise>']);
    if (matchedMarker) {
      return {
        name: this.name,
        status: 'pass',
        message: `${this.expectMarker} found in ${this.filePath}`,
        output: await this.enrichOutput(ctx, content, markerOutput(this.filePath, matchedMarker)),
      };
    }
    return {
      name: this.name,
      status: 'fail',
      message: `${this.expectMarker} not found in ${this.filePath}`,
      output: await this.enrichOutput(ctx, content, markerOutput(this.filePath, anyPromiseMarker)),
    };
  }

  private async enrichOutput(ctx: CheckContext, content: string, output: Record<string, unknown>): Promise<Record<string, unknown>> {
    if (this.name === 'self-review-passed') {
      return {
        ...output,
        selfReviewNotes: content,
        dimensions: parseDimensions(content),
      };
    }
    if (this.name === 'review-passed') {
      const snapshotSha = await this.getCandidateHeadSha(ctx);
      return {
        ...output,
        reviewReport: content,
        fixSuggestions: output.verdict === 'FAIL' ? extractFixSuggestions(content) : '',
        ...(snapshotSha ? { snapshotSha } : {}),
      };
    }
    return output;
  }

  private async getCandidateHeadSha(ctx: CheckContext): Promise<string | null> {
    try {
      const project = ctx.projectRepo?.findById(ctx.issue.projectId);
      const worktreePath = project && ctx.worktreeManager?.getPath(project.name, ctx.issue.number);
      if (!worktreePath) return null;
      if (ctx.worktreeManager?.isWorktreeClean) {
        const clean = await ctx.worktreeManager.isWorktreeClean(worktreePath);
        if (!clean) return null;
      }
      return await ctx.worktreeManager!.getHeadSha(worktreePath);
    } catch (err) {
      log.warn('Failed to resolve marker check snapshot SHA', {
        issueNumber: ctx.issue.number,
        error: err instanceof Error ? err.message : String(err),
      });
      return null;
    }
  }
}

function markerOutput(path: string, marker: string | null): Record<string, unknown> {
  const verdict = marker?.toUpperCase().includes('PASS')
    ? 'PASS'
    : marker?.toUpperCase().includes('FAIL')
      ? 'FAIL'
      : undefined;
  return {
    kind: 'artifact-marker',
    path,
    marker,
    ...(verdict ? { verdict } : {}),
  };
}

function markerIn(content: string, markers: string[]): string | null {
  const upper = content.toUpperCase();
  return markers.find(marker => upper.includes(marker.toUpperCase())) ?? null;
}
