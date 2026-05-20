import * as fs from 'fs';
import type { Check, CheckContext, CheckResult } from './index';
import { extractFixSuggestions, parseDimensions } from '../utils';
import { Log } from '../../util/log';
import {
  buildStructuredResult,
  isParseError,
  promiseMarkerContractForPath,
  validatePromiseMarkerFile,
  type ParseError,
} from '../result-contracts';
import { extractRepairResultFromArtifact } from '../task-runtime/self-repair';

const log = Log.create({ service: 'artifact-marker-check' });

export class ArtifactMarkerCheck implements Check {
  constructor(
    public readonly name: string,
    private readonly filePath: string,
    private readonly expectMarker: string,
  ) {}

  async run(ctx: CheckContext): Promise<CheckResult> {
    const content = readMarkerFile(this.filePath);
    const parsed = validatePromiseMarkerFile(this.filePath, content);
    if (isParseError(parsed)) {
      return {
        name: this.name,
        status: 'fail',
        message: describeParseError(parsed),
        output: { kind: 'artifact-marker', path: this.filePath, expect: this.expectMarker, error: parsed.error },
      };
    }
    const matched = parsed.marker.toUpperCase() === this.expectMarker.toUpperCase();
    const structured = buildStructuredResult(parsed);
    const repairResult = extractRepairResultFromArtifact(promiseMarkerContractForPath(this.filePath), content);
    const finalStructured = {
      ...structured,
      ...(repairResult.repairedItemIds.length > 0 ? { repairedItemIds: repairResult.repairedItemIds } : {}),
      ...(repairResult.verification.length > 0 ? { verification: repairResult.verification } : {}),
    };

    return {
      name: this.name,
      status: matched ? 'pass' : 'fail',
      message: matched ? `${this.expectMarker} found in ${this.filePath}` : `${this.expectMarker} not found in ${this.filePath}`,
      output: await this.enrichOutput(ctx, content ?? '', {
        kind: 'artifact-marker',
        path: this.filePath,
        marker: parsed.marker,
        verdict: parsed.verdict,
        structuredResult: finalStructured,
      }),
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

function readMarkerFile(filePath: string): string | null {
  try {
    if (!fs.existsSync(filePath)) return null;
    const content = fs.readFileSync(filePath, 'utf-8');
    return content.trim().length > 0 ? content : null;
  } catch {
    return null;
  }
}

function describeParseError(err: ParseError): string {
  switch (err.error) {
    case 'source-missing':
      return `${err.source} not found or empty`;
    case 'no-marker':
      return `No valid promise marker found in ${err.source}`;
    case 'duplicate-markers':
      return `Multiple promise markers found in ${err.source}`;
    case 'malformed-marker':
      return `Malformed promise marker in ${err.source}: ${err.raw}`;
    case 'source-unavailable':
      return `Output source ${err.source} unavailable${err.cause ? `: ${err.cause}` : ''}`;
  }
}
