import * as fs from 'fs';
import * as path from 'path';

const PROMISE_PASS = '<promise>PASS</promise>';
const PROMISE_FAIL = '<promise>FAIL</promise>';

export function parseVerdict(content: string): 'PASS' | 'FAIL' | null {
  const upper = content.toUpperCase();
  if (upper.includes(PROMISE_PASS.toUpperCase())) return 'PASS';
  if (upper.includes(PROMISE_FAIL.toUpperCase())) return 'FAIL';
  return null;
}

export function parseResult(content: string): 'PASS' | 'FAIL' | null {
  return parseVerdict(content);
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

export interface ReviewArtifactValidation {
  valid: boolean;
  content: string | null;
  verdict: 'PASS' | 'FAIL' | null;
  error?: 'missing' | 'unparsable';
}

export function validateReviewArtifact(changeDir: string, filename: string = 'review.md'): ReviewArtifactValidation {
  const content = readReportFile(changeDir, filename);
  if (content === null) {
    return { valid: false, content: null, verdict: null, error: 'missing' };
  }
  const verdict = parseVerdict(content);
  if (verdict === null) {
    return { valid: false, content, verdict: null, error: 'unparsable' };
  }
  return { valid: true, content, verdict };
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
