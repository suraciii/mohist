import * as fs from 'node:fs';
import * as path from 'node:path';

export const LABEL_TO_TEMPLATE: Record<string, string> = {
  bug: 'product',
  feature: 'product',
  improvement: 'product',
  refactor: 'refactor',
  design: 'design',
  docs: 'docs',
  'ui-feature': 'ui',
  'ui-improvement': 'ui',
};

export const VALID_LABELS = Object.keys(LABEL_TO_TEMPLATE);

export function getTemplateForLabel(label: string): string | null {
  const normalized = label.toLowerCase().trim();
  return LABEL_TO_TEMPLATE[normalized] ?? null;
}

export function getAvailableTemplates(): Array<{ template: string; labels: string[] }> {
  const templateToLabels: Record<string, string[]> = {};

  for (const [label, template] of Object.entries(LABEL_TO_TEMPLATE)) {
    if (!templateToLabels[template]) {
      templateToLabels[template] = [];
    }
    templateToLabels[template].push(label);
  }

  return Object.entries(templateToLabels).map(([template, labels]) => ({
    template,
    labels: labels.sort(),
  }));
}

function getTemplatePath(): string {
  return path.join(__dirname, 'issue-templates.md');
}

function parseTemplatesFile(content: string): Map<string, string> {
  const templates = new Map<string, string>();
  const lines = content.split('\n');

  let currentTemplate: string | null = null;
  let currentContent: string[] = [];

  for (const line of lines) {
    if (line.startsWith('## Template: ')) {
      if (currentTemplate !== null) {
        templates.set(currentTemplate, currentContent.join('\n').trim());
      }
      currentTemplate = line.slice('## Template: '.length).trim();
      currentContent = [];
    } else {
      currentContent.push(line);
    }
  }

  if (currentTemplate !== null) {
    templates.set(currentTemplate, currentContent.join('\n').trim());
  }

  return templates;
}

export interface TemplateResult {
  template: string;
  labels: string[];
  content: string;
}

export function getTemplateContent(label: string): TemplateResult | null {
  const templateName = getTemplateForLabel(label);
  if (!templateName) {
    return null;
  }

  const filePath = getTemplatePath();
  if (!fs.existsSync(filePath)) {
    return null;
  }

  const content = fs.readFileSync(filePath, 'utf-8');
  const templates = parseTemplatesFile(content);
  const templateContent = templates.get(templateName);

  if (!templateContent) {
    return null;
  }

  const labels = Object.entries(LABEL_TO_TEMPLATE)
    .filter(([, t]) => t === templateName)
    .map(([l]) => l);

  return {
    template: templateName,
    labels,
    content: templateContent,
  };
}
