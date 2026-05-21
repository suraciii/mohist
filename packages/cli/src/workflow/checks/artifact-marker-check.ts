import * as fs from 'fs';
import type { Check, CheckContext, CheckResult } from './index';
import { extractFixSuggestions, parseDimensions } from '../utils';
import { Log } from '../../util/log';
import {
  buildStructuredResult,
  isParseError,
  markerContractForPath,
  validateMarkerFile,
  type ParseError,
} from '../result-contracts';
import { extractStructuredResultMetadata } from '../structured-result-metadata';
import { enrichReviewStructuredResult } from './review-result-contracts';
import type { StructuredWorkflowResult, WorkflowVerdict } from '../../types/workflow-results';

const log = Log.create({ service: 'artifact-marker-check' });

export interface ArtifactMarkerCheckOptions {
  format?: string;
  markers?: string[];
  verdicts?: Record<string, WorkflowVerdict>;
}

export class ArtifactMarkerCheck implements Check {
  private readonly format?: string;
  private readonly allowedMarkers: string[];
  private readonly verdicts?: Record<string, WorkflowVerdict>;

  constructor(
    public readonly name: string,
    private readonly filePath: string,
    private readonly expectMarker: string,
    options: ArtifactMarkerCheckOptions = {},
  ) {
    this.format = options.format;
    this.allowedMarkers = normalizeMarkers(options.markers, expectMarker);
    this.verdicts = options.verdicts;
  }

  async run(ctx: CheckContext): Promise<CheckResult> {
    const content = readMarkerFile(this.filePath);
    const contract = markerContractForPath(this.filePath, this.allowedMarkers, this.verdicts);
    const parsed = validateMarkerFile(this.filePath, content, contract.allowedMarkers, contract.verdicts);
    if (isParseError(parsed)) {
      return {
        name: this.name,
        status: 'fail',
        message: describeParseError(parsed),
        output: { kind: 'artifact-marker', path: this.filePath, expect: this.expectMarker, error: parsed.error },
      };
    }
    const matched = parsed.marker.toUpperCase() === this.expectMarker.toUpperCase();
    const structured = this.enrichStructuredResult(buildStructuredResult(parsed), content ?? '');
    const repairResult = this.format === 'mohist/review'
      ? extractStructuredResultMetadata(contract, content)
      : null;
    const finalStructured = {
      ...structured,
      ...(repairResult && repairResult.repairedItemIds.length > 0 ? { repairedItemIds: repairResult.repairedItemIds } : {}),
      ...(repairResult && repairResult.verification.length > 0 ? { verification: repairResult.verification } : {}),
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

  private enrichStructuredResult(output: StructuredWorkflowResult, content: string): StructuredWorkflowResult {
    if (this.format === 'mohist/review' || this.format === 'mohist/self-review') {
      return enrichReviewStructuredResult(output, content);
    }
    return output;
  }

  private async enrichOutput(ctx: CheckContext, content: string, output: Record<string, unknown>): Promise<Record<string, unknown>> {
    if (this.format === 'mohist/self-review') {
      return {
        ...output,
        selfReviewNotes: content,
        dimensions: parseDimensions(content),
      };
    }
    if (this.format === 'mohist/review') {
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

function normalizeMarkers(markers: string[] | undefined, expectMarker: string): string[] {
  const normalized = (markers ?? []).filter(marker => marker.trim().length > 0);
  return normalized.length > 0 ? normalized : [expectMarker];
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
    case 'source-unavailable':
      return `Output source ${err.source} unavailable${err.cause ? `: ${err.cause}` : ''}`;
  }
}
