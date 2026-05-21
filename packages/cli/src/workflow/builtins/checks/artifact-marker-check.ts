import * as fs from 'fs';
import type { Check, CheckContext, CheckResult } from '@mohist/workflow/checks';
import {
  buildStructuredResult,
  isParseError,
  markerContractForPath,
  validateMarkerFile,
  type ParseError,
} from '@mohist/workflow/result-contracts';
import { getMarkerFormat } from '@mohist/workflow/checks/marker-format-registry';
import type { StructuredWorkflowResult, WorkflowVerdict } from '../../../types/workflow-results';

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
    const formatMetadata = getMarkerFormat(this.format)?.metadata?.(contract, content) ?? null;
    const finalStructured = {
      ...structured,
      ...(formatMetadata?.repairedItemIds?.length ? { repairedItemIds: formatMetadata.repairedItemIds } : {}),
      ...(formatMetadata?.verification?.length ? { verification: formatMetadata.verification } : {}),
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
    return getMarkerFormat(this.format)?.enrichStructuredResult?.(output, content) ?? output;
  }

  private async enrichOutput(ctx: CheckContext, content: string, output: Record<string, unknown>): Promise<Record<string, unknown>> {
    return await (getMarkerFormat(this.format)?.enrichOutput?.({ ctx, content, output }) ?? output);
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
