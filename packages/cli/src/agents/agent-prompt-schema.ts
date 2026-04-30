export interface AgentPromptParts {
  role: string;
  projectContext?: string;
  rules?: string[];
  contextFiles?: Array<{ path: string; desc: string }>;
  spec?: string;
  task: string;
  contract?: string;
  template?: string;
  instruction?: string;
}

export function formatAgentPrompt(parts: AgentPromptParts): string {
  const sections: string[] = [];

  sections.push('<mohist-task>');
  sections.push('');
  sections.push(`<role>`);
  sections.push(parts.role);
  sections.push(`</role>`);

  if (parts.projectContext) {
    sections.push('');
    sections.push('<project_context>');
    sections.push('<!-- Do NOT include this section in your output. This is background context only. -->');
    sections.push(parts.projectContext);
    sections.push('</project_context>');
  }

  if (parts.rules && parts.rules.length > 0) {
    sections.push('');
    sections.push('<rules>');
    sections.push('<!-- Do NOT include this section in your output. These are constraints to follow. -->');
    for (const rule of parts.rules) {
      sections.push(`- ${rule}`);
    }
    sections.push('</rules>');
  }

  if (parts.contextFiles && parts.contextFiles.length > 0) {
    sections.push('');
    sections.push('<context-files>');
    sections.push('<!-- Read every file listed below before starting. Each provides essential context for your task. -->');
    for (const file of parts.contextFiles) {
      sections.push(`<file path="${file.path}">${file.desc}</file>`);
    }
    sections.push('</context-files>');
  }

  if (parts.spec) {
    sections.push('');
    sections.push('<spec>');
    sections.push(parts.spec);
    sections.push('</spec>');
  }

  sections.push('');
  sections.push('<task>');
  sections.push(parts.task);
  sections.push('</task>');

  if (parts.contract) {
    sections.push('');
    sections.push('<contract>');
    sections.push(parts.contract);
    sections.push('</contract>');
  }

  if (parts.template) {
    sections.push('');
    sections.push('<template>');
    sections.push(parts.template);
    sections.push('</template>');
  }

  if (parts.instruction) {
    sections.push('');
    sections.push('<instruction>');
    sections.push(parts.instruction);
    sections.push('</instruction>');
  }

  sections.push('');
  sections.push('</mohist-task>');

  return sections.join('\n');
}
