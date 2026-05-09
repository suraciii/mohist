import * as fs from 'fs';
import * as path from 'path';

export type DeltaType = 'added' | 'modified' | 'removed' | 'renamed';

export interface ParsedRequirement {
  header: string;
  name: string;
  content: string;
  scenarios: string[];
}

export interface RenameSpec {
  from: string;
  to: string;
}

export interface CapabilityDelta {
  capability: string;
  added: ParsedRequirement[];
  modified: ParsedRequirement[];
  removed: string[];
  renamed: RenameSpec[];
}

export interface SpecSyncSummary {
  capabilities: string[];
  added: number;
  modified: number;
  removed: number;
  renamed: number;
  targetFiles: string[];
  conflicts: SpecConflict[];
  valid: boolean;
  errors: string[];
}

export interface SpecConflict {
  capability: string;
  type: 'duplicate_header' | 'missing_source' | 'duplicate_target' | 'missing_scenarios' | 'malformed_delta';
  detail: string;
  requirementHeader?: string;
}

export interface SpecCorrection {
  capability: string;
  requirement: string;
  from: DeltaType;
  to: DeltaType;
  reason: string;
}

export interface SpecSyncSummary {
  capabilities: string[];
  added: number;
  modified: number;
  removed: number;
  renamed: number;
  targetFiles: string[];
  conflicts: SpecConflict[];
  corrections: SpecCorrection[];
  valid: boolean;
  errors: string[];
  mode: 'dry-run' | 'apply';
}

interface MainSpecState {
  requirements: Map<string, ParsedRequirement>;
}

interface ChangeSpecFile {
  capability: string;
  path: string;
}

export class OpenSpecIntegrator {
  async preview(changeDir: string, projectPath: string): Promise<SpecSyncSummary> {
    return this.runSync(changeDir, projectPath, 'dry-run');
  }

  async apply(changeDir: string, projectPath: string): Promise<SpecSyncSummary> {
    return this.runSync(changeDir, projectPath, 'apply');
  }

  private async runSync(changeDir: string, projectPath: string, mode: 'dry-run' | 'apply'): Promise<SpecSyncSummary> {
    const specsDir = path.join(changeDir, 'specs');
    if (!fs.existsSync(specsDir)) {
      return this.emptySummary([], [], mode);
    }

    const mainSpecsDir = path.join(projectPath, 'openspec', 'specs');
    const changeSpecs = this.discoverChangeSpecs(specsDir);

    const mainStates = new Map<string, MainSpecState>();
    const deltas: CapabilityDelta[] = [];

    for (const specFile of changeSpecs) {
      const capability = specFile.capability;
      const changeSpecPath = specFile.path;
      const changeContent = fs.readFileSync(changeSpecPath, 'utf-8');
      const delta = this.parseCapabilityDelta(capability, changeContent);

      if (delta.added.length > 0 || delta.modified.length > 0 || delta.removed.length > 0 || delta.renamed.length > 0) {
        deltas.push(delta);
      }

      const mainSpecPath = path.join(mainSpecsDir, capability, 'spec.md');
      if (fs.existsSync(mainSpecPath)) {
        const mainContent = fs.readFileSync(mainSpecPath, 'utf-8');
        mainStates.set(capability, this.parseMainSpec(mainContent));
      }
    }

    const corrections: SpecCorrection[] = [];
    const renamedFromTo = new Map<string, string>();
    const renamedToFrom = new Map<string, string>();
    for (const delta of deltas) {
      for (const rename of delta.renamed) {
        renamedFromTo.set(`${delta.capability}:${rename.from}`, rename.to);
        renamedToFrom.set(`${delta.capability}:${rename.to}`, rename.from);
      }
    }

    if (mode === 'apply') {
      for (const delta of deltas) {
        const mainState = mainStates.get(delta.capability);
        const safeModified: ParsedRequirement[] = [];

        for (const modified of delta.modified) {
          const sourceName = renamedToFrom.get(`${delta.capability}:${modified.name}`) || modified.name;
          const targetExists = mainState && mainState.requirements.has(modified.name);
          const sourceExists = mainState && mainState.requirements.has(sourceName);

          if (!sourceExists && !targetExists) {
            corrections.push({
              capability: delta.capability,
              requirement: modified.name,
              from: 'modified',
              to: 'added',
              reason: 'missing-source-treated-as-new-requirement'
            });
            delta.added.push(modified);
          } else {
            safeModified.push(modified);
          }
        }

        delta.modified = safeModified;
      }
    }

    const conflicts: SpecConflict[] = [];

    for (const delta of deltas) {
      const mainState = mainStates.get(delta.capability);

      for (const rename of delta.renamed) {
        if (mainState && mainState.requirements.has(rename.to)) {
          conflicts.push({
            capability: delta.capability,
            type: 'duplicate_target',
            detail: `RENAMED TO "${rename.to}" - target requirement already exists`,
            requirementHeader: `Requirement: ${rename.to}`
          });
        }
        if (!mainState) {
          conflicts.push({
            capability: delta.capability,
            type: 'missing_source',
            detail: `RENAMED FROM "${rename.from}" - target spec does not exist`,
            requirementHeader: `Requirement: ${rename.from}`
          });
        } else if (!mainState.requirements.has(rename.from)) {
          conflicts.push({
            capability: delta.capability,
            type: 'missing_source',
            detail: `RENAMED FROM "${rename.from}" - source requirement not found in main spec`,
            requirementHeader: `Requirement: ${rename.from}`
          });
        }
      }

      for (const removed of delta.removed) {
        if (!mainState) {
          conflicts.push({
            capability: delta.capability,
            type: 'missing_source',
            detail: `REMOVED "${removed}" - target spec does not exist`,
            requirementHeader: `Requirement: ${removed}`
          });
        } else if (!mainState.requirements.has(removed)) {
          conflicts.push({
            capability: delta.capability,
            type: 'missing_source',
            detail: `REMOVED "${removed}" - source requirement not found in main spec`,
            requirementHeader: `Requirement: ${removed}`
          });
        }
      }

      for (const modified of delta.modified) {
        const sourceName = renamedToFrom.get(`${delta.capability}:${modified.name}`) || modified.name;
        if (!mainState) {
          conflicts.push({
            capability: delta.capability,
            type: 'missing_source',
            detail: `MODIFIED "${modified.name}" - target spec does not exist`,
            requirementHeader: `Requirement: ${modified.name}`
          });
        } else if (!mainState.requirements.has(sourceName)) {
          conflicts.push({
            capability: delta.capability,
            type: 'missing_source',
            detail: `MODIFIED "${modified.name}" - source requirement not found in main spec`,
            requirementHeader: `Requirement: ${modified.name}`
          });
        }
        if (modified.scenarios.length === 0) {
          conflicts.push({
            capability: delta.capability,
            type: 'missing_scenarios',
            detail: `MODIFIED "${modified.name}" - requirement block has no scenarios`,
            requirementHeader: `Requirement: ${modified.name}`
          });
        }
      }

      for (const added of delta.added) {
        if (mainState && mainState.requirements.has(added.name)) {
          conflicts.push({
            capability: delta.capability,
            type: 'duplicate_target',
            detail: `ADDED "${added.name}" - target requirement already exists`,
            requirementHeader: `Requirement: ${added.name}`
          });
        }
        if (added.scenarios.length === 0) {
          conflicts.push({
            capability: delta.capability,
            type: 'missing_scenarios',
            detail: `ADDED "${added.name}" - requirement block has no scenarios`,
            requirementHeader: `Requirement: ${added.name}`
          });
        }
      }

      const headerCounts = new Map<string, number>();
      const renameToNames = new Set(delta.renamed.map(r => r.to));
      const allReqs: Array<{ name: string }> = [
        ...delta.added,
        ...delta.modified.filter(m => !renameToNames.has(m.name))
      ];
      for (const req of allReqs) {
        const key = `${delta.capability}:${req.name}`;
        headerCounts.set(key, (headerCounts.get(key) || 0) + 1);
      }
      for (const rename of delta.renamed) {
        const key = `${delta.capability}:${rename.to}`;
        headerCounts.set(key, (headerCounts.get(key) || 0) + 1);
      }
      for (const [key, count] of headerCounts) {
        if (count > 1) {
          const [cap, name] = key.split(':');
          conflicts.push({
            capability: cap,
            type: 'duplicate_header',
            detail: `Duplicate requirement header "${name}" in change delta`,
            requirementHeader: `Requirement: ${name}`
          });
        }
      }
    }

    if (conflicts.length > 0) {
      return {
        capabilities: deltas.map(d => d.capability),
        added: deltas.reduce((s, d) => s + d.added.length, 0),
        modified: deltas.reduce((s, d) => s + d.modified.length, 0),
        removed: deltas.reduce((s, d) => s + d.removed.length, 0),
        renamed: deltas.reduce((s, d) => s + d.renamed.length, 0),
        targetFiles: deltas.map(d => `openspec/specs/${d.capability}/spec.md`),
        conflicts,
        corrections,
        valid: false,
        errors: conflicts.map(c => `${c.capability}: ${c.detail}`),
        mode
      };
    }

    if (mode === 'apply') {
      await this.applyDeltas(deltas, mainStates, projectPath);
    }

    return {
      capabilities: deltas.map(d => d.capability),
      added: deltas.reduce((s, d) => s + d.added.length, 0),
      modified: deltas.reduce((s, d) => s + d.modified.length, 0),
      removed: deltas.reduce((s, d) => s + d.removed.length, 0),
      renamed: deltas.reduce((s, d) => s + d.renamed.length, 0),
      targetFiles: deltas.map(d => `openspec/specs/${d.capability}/spec.md`),
      conflicts: [],
      corrections,
      valid: true,
      errors: [],
      mode
    };
  }

  private parseCapabilityDelta(capability: string, content: string): CapabilityDelta {
    const delta: CapabilityDelta = { capability, added: [], modified: [], removed: [], renamed: [] };

    const addedSection = this.extractSection(content, '## ADDED Requirements');
    if (addedSection) {
      delta.added = this.parseRequirementBlocks(addedSection);
    }

    const modifiedSection = this.extractSection(content, '## MODIFIED Requirements');
    if (modifiedSection) {
      delta.modified = this.parseRequirementBlocks(modifiedSection);
    }

    const removedSection = this.extractSection(content, '## REMOVED Requirements');
    if (removedSection) {
      delta.removed = this.parseRemovedRequirements(removedSection);
    }

    const renamedSection = this.extractSection(content, '## RENAMED Requirements');
    if (renamedSection) {
      delta.renamed = this.parseRenamedRequirements(renamedSection);
    }

    return delta;
  }

  private discoverChangeSpecs(specsDir: string): ChangeSpecFile[] {
    const entries = fs.readdirSync(specsDir, { withFileTypes: true });
    const specs: ChangeSpecFile[] = [];

    for (const entry of entries) {
      if (entry.isDirectory()) {
        const specPath = path.join(specsDir, entry.name, 'spec.md');
        if (fs.existsSync(specPath)) {
          specs.push({ capability: entry.name, path: specPath });
        }
      } else if (entry.isFile() && entry.name.endsWith('.md')) {
        specs.push({
          capability: entry.name.replace(/\.md$/, ''),
          path: path.join(specsDir, entry.name)
        });
      }
    }

    return specs;
  }

  private extractSection(content: string, sectionHeader: string): string | null {
    const lines = content.split('\n');
    let startIndex = -1;
    let endIndex = lines.length;

    for (let i = 0; i < lines.length; i++) {
      const line = lines[i].trim();
      if (line === sectionHeader) {
        startIndex = i + 1;
      } else if (startIndex !== -1 && line.startsWith('## ') && line !== sectionHeader) {
        endIndex = i;
        break;
      }
    }

    if (startIndex === -1) return null;
    return lines.slice(startIndex, endIndex).join('\n');
  }

  private parseRequirementBlocks(section: string): ParsedRequirement[] {
    const blocks: ParsedRequirement[] = [];
    const lines = section.split('\n');
    let currentBlock: ParsedRequirement | null = null;
    let currentScenarios: string[] = [];

    for (const line of lines) {
      const trimmed = line.trim();
      if (trimmed.startsWith('### Requirement:')) {
        if (currentBlock) {
          currentBlock.scenarios = currentScenarios;
          blocks.push(currentBlock);
        }
        const name = trimmed.replace('### Requirement:', '').trim();
        currentBlock = { header: `Requirement: ${name}`, name, content: '', scenarios: [] };
        currentScenarios = [];
      } else if (trimmed.startsWith('#### Scenario:')) {
        const scenarioName = trimmed.replace('#### Scenario:', '').trim();
        currentScenarios.push(scenarioName);
        if (currentBlock) {
          currentBlock.content += line + '\n';
        }
      } else if (currentBlock) {
        currentBlock.content += line + '\n';
      }
    }

    if (currentBlock) {
      currentBlock.scenarios = currentScenarios;
      blocks.push(currentBlock);
    }

    return blocks;
  }

  private parseRemovedRequirements(section: string): string[] {
    const names: string[] = [];
    const lines = section.split('\n');

    for (const line of lines) {
      const trimmed = line.trim();
      if (trimmed.startsWith('### Requirement:')) {
        const name = trimmed.replace('### Requirement:', '').trim();
        names.push(name);
      }
    }

    return names;
  }

  private parseRenamedRequirements(section: string): RenameSpec[] {
    const specs: RenameSpec[] = [];
    const lines = section.split('\n');
    let currentFrom: string | null = null;

    for (const line of lines) {
      const trimmed = line.trim();
      if (trimmed.startsWith('FROM:')) {
        currentFrom = trimmed.replace('FROM:', '').trim();
      } else if (trimmed.startsWith('TO:') && currentFrom) {
        const toName = trimmed.replace('TO:', '').trim();
        specs.push({ from: currentFrom, to: toName });
        currentFrom = null;
      }
    }

    return specs;
  }

  private parseMainSpec(content: string): MainSpecState {
    const state: MainSpecState = { requirements: new Map() };
    const lines = content.split('\n');
    let currentBlock: ParsedRequirement | null = null;
    let currentScenarios: string[] = [];

    for (const line of lines) {
      const trimmed = line.trim();
      if (trimmed.startsWith('### Requirement:')) {
        if (currentBlock) {
          currentBlock.scenarios = currentScenarios;
          state.requirements.set(currentBlock.name, currentBlock);
        }
        const name = trimmed.replace('### Requirement:', '').trim();
        currentBlock = { header: `Requirement: ${name}`, name, content: '', scenarios: [] };
        currentScenarios = [];
      } else if (trimmed.startsWith('#### Scenario:')) {
        currentScenarios.push(trimmed.replace('#### Scenario:', '').trim());
        if (currentBlock) {
          currentBlock.content += line + '\n';
        }
      } else if (currentBlock) {
        currentBlock.content += line + '\n';
      }
    }

    if (currentBlock) {
      currentBlock.scenarios = currentScenarios;
      state.requirements.set(currentBlock.name, currentBlock);
    }

    return state;
  }

  private async applyDeltas(deltas: CapabilityDelta[], mainStates: Map<string, MainSpecState>, projectPath: string): Promise<void> {
    const renamedToFrom = new Map<string, string>();
    for (const delta of deltas) {
      for (const rename of delta.renamed) {
        renamedToFrom.set(`${delta.capability}:${rename.to}`, rename.from);
      }
    }

    for (const delta of deltas) {
      const mainState = mainStates.get(delta.capability);
      let newRequirements = new Map<string, ParsedRequirement>();

      if (mainState) {
        newRequirements = new Map(mainState.requirements);
      }

      for (const rename of delta.renamed) {
        const req = newRequirements.get(rename.from);
        if (req) {
          newRequirements.delete(rename.from);
          const renamedReq: ParsedRequirement = {
            header: `Requirement: ${rename.to}`,
            name: rename.to,
            content: req.content,
            scenarios: req.scenarios
          };
          newRequirements.set(rename.to, renamedReq);
        }
      }

      for (const removed of delta.removed) {
        newRequirements.delete(removed);
      }

      for (const modified of delta.modified) {
        const sourceName = renamedToFrom.get(`${delta.capability}:${modified.name}`) || modified.name;
        if (newRequirements.has(sourceName)) {
          newRequirements.delete(sourceName);
          newRequirements.set(modified.name, {
            ...modified,
            name: modified.name,
            header: `Requirement: ${modified.name}`
          });
        } else if (newRequirements.has(modified.name)) {
          newRequirements.set(modified.name, {
            ...modified,
            header: `Requirement: ${modified.name}`
          });
        }
      }

      for (const added of delta.added) {
        newRequirements.set(added.name, added);
      }

      const specPath = path.join(projectPath, 'openspec', 'specs', delta.capability, 'spec.md');
      const specDir = path.dirname(specPath);
      if (!fs.existsSync(specDir)) {
        fs.mkdirSync(specDir, { recursive: true });
      }

      let newContent = '# OpenSpec Capability: ' + delta.capability + '\n\n';
      for (const req of newRequirements.values()) {
        newContent += '### ' + req.header + '\n\n';
        newContent += req.content.trim() + '\n\n';
      }

      fs.writeFileSync(specPath, newContent, 'utf-8');
    }
  }

  private emptySummary(capabilities: string[], errors: string[], mode: 'dry-run' | 'apply'): SpecSyncSummary {
    return {
      capabilities,
      added: 0,
      modified: 0,
      removed: 0,
      renamed: 0,
      targetFiles: [],
      conflicts: [],
      corrections: [],
      valid: errors.length === 0,
      errors,
      mode
    };
  }
}
