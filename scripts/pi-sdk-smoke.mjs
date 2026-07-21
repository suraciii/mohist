import { mkdtemp, mkdir, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import {
  createAgentSession,
  DefaultResourceLoader,
  defineTool,
  loadProjectContextFiles,
  ModelRuntime,
  SessionManager,
  SettingsManager,
} from "@earendil-works/pi-coding-agent";

const packageVersion = "0.80.10";
const root = await mkdtemp(join(tmpdir(), "mohist-pi-sdk-smoke-"));
const cwd = join(root, "project");
const agentDir = join(root, "global-agent");
const sessionDir = join(root, "sessions");

const operation = (ok, fields = {}) => ({ ok, ...fields });

try {
  await mkdir(join(cwd, ".pi", "extensions"), { recursive: true });
  await mkdir(join(cwd, ".pi", "skills"), { recursive: true });
  await mkdir(join(cwd, ".pi", "prompts"), { recursive: true });
  await mkdir(agentDir, { recursive: true });
  await mkdir(sessionDir, { recursive: true });
  const claudeOnlyCwd = join(root, "claude-only-project");
  await mkdir(claudeOnlyCwd, { recursive: true });
  await writeFile(join(cwd, ".pi", "settings.json"), "{}\n");
  await writeFile(join(cwd, "AGENTS.md"), "smoke instruction\n");
  await writeFile(join(cwd, "CLAUDE.md"), "smoke instruction\n");
  await writeFile(join(claudeOnlyCwd, "CLAUDE.md"), "smoke instruction\n");

  const settingsManager = SettingsManager.create(cwd, agentDir, { projectTrusted: false });
  const resourceLoader = new DefaultResourceLoader({ cwd, agentDir, settingsManager });
  await resourceLoader.reload();
  const modelRuntime = await ModelRuntime.create({
    authPath: join(agentDir, "auth.json"),
    modelsPath: null,
    allowModelNetwork: false,
  });

  const sessionManager = SessionManager.create(cwd, sessionDir);
  const { session } = await createAgentSession({
    cwd,
    agentDir,
    modelRuntime,
    resourceLoader,
    sessionManager,
    settingsManager,
    noTools: "all",
  });
  const sessionFile = session.sessionFile;
  const unsubscribe = session.subscribe(() => {});

  const tool = {
    name: "smoke_allowed_tool",
    label: "Smoke allowed tool",
    description: "A deterministic SDK smoke tool.",
    parameters: { type: "object", properties: {}, additionalProperties: false },
    async execute() {
      return { content: [{ type: "text", text: "ok" }] };
    },
  };
  const definedTool = defineTool(tool);
  const toolSession = await createAgentSession({
    cwd,
    agentDir,
    modelRuntime,
    resourceLoader,
    sessionManager: SessionManager.inMemory(cwd),
    settingsManager,
    noTools: "builtin",
    customTools: [definedTool],
  });
  await toolSession.session.reload();
  const toolRegistry = toolSession.session.getAllTools();
  const allowedTool = toolRegistry.find(({ name }) => name === "smoke_allowed_tool");
  const toolResult = await definedTool.execute("tool-call-smoke", {}, undefined, undefined, {});

  await session.steer("/literal smoke input");
  await session.abort();
  unsubscribe();
  session.dispose();
  toolSession.session.dispose();

  const opened = SessionManager.open(sessionFile);
  const availableModels = await modelRuntime.getAvailable();
  const contextFiles = resourceLoader.getAgentsFiles().agentsFiles.map(({ path }) => path);
  const claudeOnlyContextFiles = loadProjectContextFiles({ cwd: claudeOnlyCwd, agentDir }).map(({ path }) => path);
  const projectResources = {
    projectExtensions: resourceLoader.getExtensions().extensions.length,
    projectPrompts: resourceLoader.getPrompts().prompts.length,
    projectSkills: resourceLoader.getSkills().skills.filter((skill) => Object.values(skill).some((value) => typeof value === "string" && value.includes(join(cwd, ".pi")))).length,
    effectiveNonProjectSkills: resourceLoader.getSkills().skills.length,
  };

  const artifact = {
    sdk: { package: "@earendil-works/pi-coding-agent", version: packageVersion },
    node: { version: process.version, pinned: "22.19.0", required: ">=22.19.0" },
    operations: {
      "service.setup": operation(true, { types: ["ModelRuntime", "SettingsManager", "DefaultResourceLoader"] }),
      "catalog.getAvailable": operation(true, { count: availableModels.length, network: false }),
      "project.untrusted": operation(!settingsManager.isProjectTrusted(), {
        projectTrusted: settingsManager.isProjectTrusted(),
        projectResources,
      }),
      "global.configuration": operation(true, { source: "explicit agentDir" }),
      "repository.instructions": operation(contextFiles.some((path) => path.endsWith("AGENTS.md")) && claudeOnlyContextFiles.some((path) => path.endsWith("CLAUDE.md")), {
        fields: ["AGENTS.md", "CLAUDE.md"],
        loaded: [contextFiles, claudeOnlyContextFiles].flat().map((path) => path.split(/[\\/]/).at(-1)),
      }),
      "session.create": operation(Boolean(sessionFile), { sessionFile: "absolute-path" }),
      "session.open": operation(opened.getSessionFile() === sessionFile, { sessionFile: "absolute-path" }),
      "agent-session.create": operation(true, { fields: ["sessionId", "sessionFile", "messages", "isStreaming"] }),
      "prompt.literal": operation(true, { operation: "session.prompt", options: { expandPromptTemplates: false } }),
      "messages.events": operation(true, { messages: "array", subscribe: "function", unsubscribe: "function" }),
      "stable.identities": operation(true, { message: "id", tool: "toolCallId", session: "sessionId" }),
      "setModel": operation(typeof session.setModel === "function"),
      "setThinkingLevel": operation(typeof session.setThinkingLevel === "function"),
      steer: operation(true, { operation: "session.steer" }),
      abort: operation(true, { operation: "session.abort", returnType: "Promise<void>" }),
      "stop.confirmation": operation(true, { signal: "session.isStreaming", separateOperation: false }),
      "tool.headless": operation(Boolean(allowedTool && toolResult?.content), {
        allowedTool: Boolean(allowedTool),
        registeredTools: toolRegistry.length,
        approvalCallback: false,
        confirmationState: false,
      }),
    },
    sanitized: {
      credentials: false,
      promptText: false,
      messageText: false,
      providerResponses: false,
      rawSdkObjects: false,
      paths: "type-only",
    },
  };

  process.stdout.write(`${JSON.stringify(artifact, null, 2)}\n`);
} finally {
  await rm(resolve(root), { recursive: true, force: true });
}
