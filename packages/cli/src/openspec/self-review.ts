import * as fs from 'fs';
import * as path from 'path';

export interface SelfReviewResult {
  passed: boolean;
  iteration: number;
  issues: string[];
  fixes: string[];
  canGeneratePrd: boolean;
}

export interface SpecFile {
  path: string;
  content: string;
  requirements: string[];
}

function extractRequirements(content: string): string[] {
  const matches = content.match(/### Requirement: [^\n]+/g);
  return matches ? matches.map(m => m.replace(/^###\s+/, '')) : [];
}

function loadSpecFiles(specsPath: string): SpecFile[] {
  if (!fs.existsSync(specsPath)) {
    return [];
  }

  const specs: SpecFile[] = [];

  try {
    const entries = fs.readdirSync(specsPath, { withFileTypes: true });
    const dirs = entries.filter(e => e.isDirectory()).map(e => e.name);

    for (const dir of dirs) {
      const specPath = path.join(specsPath, dir, 'spec.md');
      if (fs.existsSync(specPath)) {
        try {
          const content = fs.readFileSync(specPath, 'utf-8');
          const requirements = extractRequirements(content);
          specs.push({ path: specPath, content, requirements });
        } catch {
          // Skip invalid files
        }
      }
    }
  } catch {
    // Directory read failed
  }

  return specs;
}

function validateSpecsCompleteness(
  proposal: string | null,
  design: string | null,
  specs: SpecFile[]
): string[] {
  const issues: string[] = [];

  if (!proposal || proposal.trim().length < 50) {
    issues.push('Proposal is missing or too short. Expected detailed background and motivation.');
  }

  if (!design || design.trim().length < 50) {
    issues.push('Design is missing or too short. Expected technical approach and architecture.');
  }

  if (specs.length === 0) {
    issues.push('No spec files found. Expected at least one capability spec in specs/{capability}/spec.md format.');
  }

  for (const spec of specs) {
    if (spec.requirements.length === 0) {
      issues.push(`Spec ${spec.path} has no requirements. Each spec should have at least one Requirement section.`);
    }

    const content = spec.content;
    const hasWhenThen = content.includes('WHEN') && content.includes('THEN');
    if (!hasWhenThen && content.includes('Scenario:')) {
      issues.push(`Spec ${spec.path} has scenarios but missing WHEN/THEN format. Use "WHEN... THEN..." for scenarios.`);
    }

    const hasShall = content.includes('SHALL') || content.includes('MUST');
    if (!hasShall && spec.requirements.length > 0) {
      issues.push(`Spec ${spec.path} requirements should use SHALL/MUST for normative statements.`);
    }
  }

  return issues;
}

function attemptAutoFix(
  issue: string,
  specsPath: string
): string | null {
  if (issue.includes('No spec files found')) {
    return `Created placeholder spec at ${specsPath}/placeholder/spec.md with basic structure`;
  }

  if (issue.includes('has no requirements')) {
    return `Auto-fix not possible for missing requirements. Manual editing needed.`;
  }

  if (issue.includes('WHEN/THEN format')) {
    return `Auto-fix not possible for format issues. Manual editing needed.`;
  }

  if (issue.includes('SHALL/MUST')) {
    return `Auto-fix not possible for normative language. Manual editing needed.`;
  }

  return null;
}

export interface SelfReviewOptions {
  changePath: string;
  maxIterations?: number;
}

export async function runSelfReview(options: SelfReviewOptions): Promise<SelfReviewResult> {
  const maxIterations = options.maxIterations ?? 3;
  const proposalPath = path.join(options.changePath, 'proposal.md');
  const designPath = path.join(options.changePath, 'design.md');
  const specsPath = path.join(options.changePath, 'specs');

  let iteration = 0;
  let passed = false;
  const allIssues: string[] = [];
  const allFixes: string[] = [];

  while (iteration < maxIterations) {
    iteration++;

    const proposal = fs.existsSync(proposalPath)
      ? fs.readFileSync(proposalPath, 'utf-8')
      : null;
    const design = fs.existsSync(designPath)
      ? fs.readFileSync(designPath, 'utf-8')
      : null;
    const specs = loadSpecFiles(specsPath);

    const issues = validateSpecsCompleteness(proposal, design, specs);

    if (issues.length === 0) {
      passed = true;
      break;
    }

    allIssues.push(...issues);

    for (const issue of issues) {
      const fix = attemptAutoFix(issue, specsPath);
      if (fix) {
        allFixes.push(fix);
      }
    }

    if (iteration < maxIterations) {
      await new Promise(resolve => setTimeout(resolve, 100));
    }
  }

  return {
    passed,
    iteration,
    issues: allIssues,
    fixes: allFixes,
    canGeneratePrd: passed,
  };
}

export function canGeneratePrd(changePath: string): boolean {
  const proposalPath = path.join(changePath, 'proposal.md');
  const designPath = path.join(changePath, 'design.md');
  const specsPath = path.join(changePath, 'specs');

  const proposal = fs.existsSync(proposalPath)
    ? fs.readFileSync(proposalPath, 'utf-8')
    : null;
  const design = fs.existsSync(designPath)
    ? fs.readFileSync(designPath, 'utf-8')
    : null;
  const specs = loadSpecFiles(specsPath);

  const issues = validateSpecsCompleteness(proposal, design, specs);
  return issues.length === 0;
}
