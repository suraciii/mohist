import * as fs from 'fs';
import * as path from 'path';
import { Log } from '../util/log';

const log = Log.create({ service: 'workflow-utils' });

const RESULT_RE = /^##\s*Result\s*:\s*(PASS|FAIL)\s*$/im;
const LEGACY_VERDICT_RE = /^##\s*Verdict\s*:\s*(PASS|FAIL)\s*$/im;

export function parseVerdict(content: string): 'PASS' | 'FAIL' | null {
  const match = RESULT_RE.exec(content);
  if (match) return match[1].toUpperCase() as 'PASS' | 'FAIL';
  const legacyMatch = LEGACY_VERDICT_RE.exec(content);
  if (legacyMatch) {
    log.warn('parseVerdict: matched legacy "## Verdict:" header, update prompt templates to use "## Result:"');
    return legacyMatch[1].toUpperCase() as 'PASS' | 'FAIL';
  }
  return null;
}

export function parseResult(content: string): 'PASS' | 'FAIL' | null {
  const match = RESULT_RE.exec(content);
  if (match) return match[1].toUpperCase() as 'PASS' | 'FAIL';
  const legacyMatch = LEGACY_VERDICT_RE.exec(content);
  if (legacyMatch) {
    process.stderr.write('[warn] parseResult: matched legacy "## Verdict:" header, update prompt templates to use "## Result:"\n');
    return legacyMatch[1].toUpperCase() as 'PASS' | 'FAIL';
  }
  return null;
}

export function extractFixSuggestions(content: string): string {
  const match = content.match(/^##\s*Fix\s*Suggestions\s*$/im);
  if (!match) return '';
  const startIdx = match.index! + match[0].length;
  return content.slice(startIdx).trim();
}

export interface ParsedDimension {
  name: string;
  status: 'PASS' | 'FAIL';
  issues?: string[];
}

export function parseDimensions(content: string): ParsedDimension[] {
  const dimensions: ParsedDimension[] = [];
  const lines = content.split('\n');
  let currentDim: ParsedDimension | null = null;
  let currentIssues: string[] = [];

  for (const line of lines) {
    const dimMatch = line.match(/^###\s+(.+?)\s*:\s*(PASS|FAIL)\s*$/);
    if (dimMatch) {
      if (currentDim) {
        if (currentIssues.length > 0) {
          currentDim.issues = currentIssues;
        }
        dimensions.push(currentDim);
      }
      currentDim = { name: dimMatch[1], status: dimMatch[2] as 'PASS' | 'FAIL' };
      currentIssues = [];
      continue;
    }
    if (currentDim && currentDim.status === 'FAIL') {
      const issueMatch = line.match(/^\s*-\s+(.+)/);
      if (issueMatch) {
        currentIssues.push(issueMatch[1].trim());
      }
    }
  }

  if (currentDim) {
    if (currentIssues.length > 0) {
      currentDim.issues = currentIssues;
    }
    dimensions.push(currentDim);
  }

  return dimensions;
}

export function readReportFile(changeDir: string, filename: string): string | null {
  const filePath = path.join(changeDir, filename);
  try {
    if (!fs.existsSync(filePath)) return null;
    const content = fs.readFileSync(filePath, 'utf-8').trim();
    return content.length > 0 ? content : null;
  } catch {
    return null;
  }
}

export function cleanChangeDir(changeDir: string): void {
  if (!fs.existsSync(changeDir)) {
    return;
  }
  const entries = fs.readdirSync(changeDir);
  for (const entry of entries) {
    if (entry === '.openspec.yaml') continue;
    const entryPath = path.join(changeDir, entry);
    fs.rmSync(entryPath, { recursive: true, force: true });
  }
}
