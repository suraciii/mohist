import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { server, useMswServer } from "../../../../tests/support/msw";
import {
  makeAgent,
  renderPage,
  state,
} from "../../../../tests/support/agent-session-composer-test-support";

useMswServer();

describe("AgentSessionComposerPage", () => {
  beforeEach(() => {
    state.agentsData = [];
    state.availabilityData = [];
    state.launchCalls.length = 0;
    state.taskCalls.length = 0;
    state.defaultExecutionConfig = {
      runtime: "opencode",
      model: "openai/gpt-4o",
      variant: null,
    };
    state.launchError = null;
    state.launchFailuresRemaining = -1;
    state.launchResponse = null;
  });

  afterEach(() => {
    cleanup();
  });

  /* ── Query-param parsing and pre-fill ─────────────────── */

  it("reads ?agent= to pre-select an agent", async () => {
    state.agentsData = [makeAgent("agent-1", { name: "Agent One" })];
    renderPage(["/agent-sessions/new?agent=agent-1"]);
    expect(
      await screen.findByTestId("agent-selector-trigger"),
    ).toHaveTextContent("Agent One");
  });

  it("reads ?issue= to pre-fill an issue context ref", async () => {
    state.agentsData = [makeAgent("agent-1")];
    renderPage(["/agent-sessions/new?issue=42"]);
    expect(
      await screen.findByTestId("context-ref-chip-issue"),
    ).toHaveTextContent("Issue #42");
  });

  it("reads ?epic= to pre-fill an epic context ref", async () => {
    state.agentsData = [makeAgent("agent-1")];
    renderPage(["/agent-sessions/new?epic=7"]);
    expect(
      await screen.findByTestId("context-ref-chip-epic"),
    ).toHaveTextContent("Epic: 7");
  });

  it("reads ?repo= to pre-fill a repo context ref", async () => {
    state.agentsData = [makeAgent("agent-1")];
    renderPage(["/agent-sessions/new?repo=org/repo"]);
    expect(
      await screen.findByTestId("context-ref-chip-repository"),
    ).toHaveTextContent("Repository: org/repo");
  });

  it("reads ?ws= to pre-fill a workspace path context ref", async () => {
    state.agentsData = [makeAgent("agent-1")];
    renderPage(["/agent-sessions/new?ws=/home/project"]);
    expect(
      await screen.findByTestId("context-ref-chip-workspace"),
    ).toHaveTextContent("Workspace: /home/project");
  });

  it("pre-fills multiple context refs simultaneously", async () => {
    state.agentsData = [makeAgent("agent-1")];
    renderPage(["/agent-sessions/new?issue=7&epic=3&repo=my/repo"]);
    await screen.findByTestId("context-ref-chip-repository");
    expect(screen.getByTestId("context-ref-chip-issue")).toHaveTextContent(
      "Issue #7",
    );
    expect(screen.getByTestId("context-ref-chip-epic")).toHaveTextContent(
      "Epic: 3",
    );
    expect(screen.getByTestId("context-ref-chip-repository")).toHaveTextContent(
      "Repository: my/repo",
    );
  });

  /* ── Agent selection ──────────────────────────────────── */

  it("lists agents in the selector dropdown", async () => {
    state.agentsData = [makeAgent("agent-1"), makeAgent("agent-2")];
    renderPage();
    fireEvent.click(await screen.findByTestId("agent-selector-trigger"));
    expect(screen.getByTestId("agent-option-agent-1")).toBeInTheDocument();
    expect(screen.getByTestId("agent-option-agent-2")).toBeInTheDocument();
  });

  it("selects agent from dropdown", async () => {
    state.agentsData = [makeAgent("agent-1")];
    renderPage();
    fireEvent.click(await screen.findByTestId("agent-selector-trigger"));
    fireEvent.click(screen.getByTestId("agent-option-agent-1"));
    expect(screen.getByTestId("agent-selector-trigger")).toHaveTextContent(
      "Agent agent-1",
    );
  });

  /* ── Prompt validation ────────────────────────────────── */

  it("disables launch when prompt is empty", async () => {
    state.agentsData = [makeAgent("agent-1")];
    renderPage(["/agent-sessions/new?agent=agent-1"]);
    const button = await screen.findByTestId("launch-button");
    expect(button).toBeDisabled();
  });

  it("shows prompt error when textarea is blurred with empty value", async () => {
    state.agentsData = [makeAgent("agent-1")];
    renderPage(["/agent-sessions/new?agent=agent-1"]);
    const textarea = await screen.findByTestId("prompt-textarea");
    fireEvent.focus(textarea);
    fireEvent.blur(textarea);
    expect(screen.getByTestId("prompt-error")).toBeInTheDocument();
    expect(screen.getByTestId("prompt-error")).toHaveTextContent(
      "Prompt is required",
    );
  });

  it("enables launch when prompt is filled and agent selected", async () => {
    state.agentsData = [makeAgent("agent-1")];
    renderPage(["/agent-sessions/new?agent=agent-1"]);
    const textarea = await screen.findByTestId("prompt-textarea");
    fireEvent.change(textarea, { target: { value: "Hello agent" } });
    const button = screen.getByTestId("launch-button");
    expect(button).not.toBeDisabled();
  });

  /* ── Launch call + navigation ─────────────────────────── */

  it("launches a task without an Agent selection through the task-first mutation", async () => {
    renderPage();
    const textarea = await screen.findByTestId("prompt-textarea");
    fireEvent.change(textarea, {
      target: { value: "Review the current change" },
    });
    fireEvent.click(screen.getByTestId("launch-button"));

    await waitFor(() => {
      expect(state.taskCalls).toHaveLength(1);
      expect(screen.getByTestId("current-path")).toHaveTextContent(
        "/Test/sessions/sess-123",
      );
    });
    expect(state.taskCalls[0].body).toEqual({
      prompt: "Review the current change",
      context: null,
      attachments: [],
    });
    expect(state.taskCalls[0].body).not.toHaveProperty("runtime");
    expect(state.taskCalls[0].body).not.toHaveProperty("model");
    expect(state.taskCalls[0].body).not.toHaveProperty("variant");
    expect(state.launchCalls).toHaveLength(0);
    expect(state.taskCalls[0].idempotencyKey).toBeTruthy();
  });

  it("retains the task-first idempotency key when a failed launch is retried", async () => {
    state.launchError = { error: "response lost" };
    state.launchFailuresRemaining = 1;
    renderPage();
    const textarea = await screen.findByTestId("prompt-textarea");
    fireEvent.change(textarea, { target: { value: "Retry this task" } });
    fireEvent.click(screen.getByTestId("launch-button"));
    await waitFor(() => expect(state.taskCalls).toHaveLength(1));

    fireEvent.click(screen.getByTestId("launch-button"));
    await waitFor(() => expect(state.taskCalls).toHaveLength(2));

    expect(state.taskCalls[0].idempotencyKey).toBeTruthy();
    expect(state.taskCalls[1].idempotencyKey).toBe(
      state.taskCalls[0].idempotencyKey,
    );
  });

  it("requires catalog-backed Runtime and Model when the Project has no default", async () => {
    state.defaultExecutionConfig = null;
    server.use(
      http.get("*/api/projects/:projectId/opencode/models", () =>
        HttpResponse.json({
          success: true,
          data: {
            models: ["anthropic/claude-3"],
            modelVariants: { "anthropic/claude-3": ["high", "low"] },
          },
        }),
      ),
    );
    renderPage();

    expect(
      await screen.findByTestId("execution-config-controls"),
    ).toBeInTheDocument();
    expect(screen.getByTestId("task-runtime")).toHaveValue("opencode");
    expect(screen.getByTestId("launch-button")).toBeDisabled();
    await waitFor(() =>
      expect(screen.getByRole("button", { name: "Model" })).not.toBeDisabled(),
    );

    fireEvent.click(screen.getByRole("button", { name: "Model" }));
    fireEvent.click(
      await screen.findByRole("option", { name: /anthropic\/claude-3/i }),
    );
    fireEvent.click(screen.getByRole("button", { name: "Model" }));
    fireEvent.click(
      await screen.findByTestId(
        "task-model-row-anthropic/claude-3-variant-high",
      ),
    );
    fireEvent.change(screen.getByTestId("prompt-textarea"), {
      target: { value: "Use the catalog model" },
    });
    expect(screen.getByTestId("launch-button")).not.toBeDisabled();
    fireEvent.click(screen.getByTestId("launch-button"));

    await waitFor(() => expect(state.taskCalls).toHaveLength(1));
    expect(state.taskCalls[0].body).toMatchObject({
      prompt: "Use the catalog model",
      runtime: "opencode",
      model: "anthropic/claude-3",
      variant: "high",
    });
  });

  it("shows the Project default as a recommendation and submits adjusted catalog values as hints", async () => {
    server.use(
      http.get("*/api/projects/:projectId/opencode/models", () =>
        HttpResponse.json({
          success: true,
          data: {
            models: ["anthropic/claude-3"],
            modelVariants: { "anthropic/claude-3": ["high", "low"] },
          },
        }),
      ),
    );
    renderPage();
    expect(
      await screen.findByTestId("recommended-execution-config"),
    ).toHaveTextContent(/recommended execution configuration/i);
    expect(
      screen.getByTestId("recommended-execution-config"),
    ).toHaveTextContent(/Project default for tasks/i);
    expect(
      screen.queryByTestId("execution-config-controls"),
    ).not.toBeInTheDocument();

    fireEvent.click(screen.getByTestId("adjust-execution-config"));
    expect(
      await screen.findByTestId("execution-config-controls"),
    ).toBeInTheDocument();
    await waitFor(() =>
      expect(screen.getByRole("button", { name: "Model" })).not.toBeDisabled(),
    );
    fireEvent.click(screen.getByRole("button", { name: "Model" }));
    fireEvent.click(
      await screen.findByRole("option", { name: /anthropic\/claude-3/i }),
    );
    fireEvent.change(screen.getByTestId("prompt-textarea"), {
      target: { value: "Use an adjusted model" },
    });
    fireEvent.click(screen.getByTestId("launch-button"));

    await waitFor(() => expect(state.taskCalls).toHaveLength(1));
    expect(state.taskCalls[0].body).toMatchObject({
      prompt: "Use an adjusted model",
      runtime: "opencode",
      model: "anthropic/claude-3",
    });
  });

  it("preserves task and context state when the task-first launch is rejected", async () => {
    state.launchError = {
      error: "Execution configuration is unresolved",
      code: "execution_config_unresolvable",
    };
    renderPage(["/agent-sessions/new?issue=42"]);
    const textarea = await screen.findByTestId("prompt-textarea");
    fireEvent.change(textarea, { target: { value: "Keep this task" } });
    fireEvent.click(screen.getByTestId("launch-button"));

    const feedback = await screen.findByTestId("error-execution-config");
    expect(feedback).toHaveAttribute(
      "data-feedback-kind",
      "execution-config-unresolvable",
    );
    expect(feedback).toHaveTextContent(/Runtime and Model/i);
    expect(feedback).toHaveTextContent(/Project default/i);
    expect(screen.getByTestId("prompt-textarea")).toHaveValue("Keep this task");
    expect(screen.getByTestId("context-ref-chip-issue")).toHaveTextContent(
      "Issue #42",
    );
  });

  it("calls mutate with correct args on launch when an Agent is selected", async () => {
    state.agentsData = [makeAgent("agent-1")];
    renderPage(["/agent-sessions/new?agent=agent-1"]);
    const textarea = await screen.findByTestId("prompt-textarea");
    expect(
      screen.queryByTestId("execution-config-controls"),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByTestId("recommended-execution-config"),
    ).not.toBeInTheDocument();
    fireEvent.change(textarea, { target: { value: "Hello agent" } });
    fireEvent.click(screen.getByTestId("launch-button"));
    await waitFor(() => {
      expect(state.launchCalls).toHaveLength(1);
      expect(screen.getByTestId("current-path")).toHaveTextContent(
        "/Test/sessions/sess-123",
      );
    });
    expect(state.launchCalls[0]).toMatchObject({
      agentRef: "agent-1",
      body: expect.objectContaining({ prompt: "Hello agent" }),
    });
  });

  it("passes context refs in launch body", async () => {
    state.agentsData = [makeAgent("agent-1")];
    renderPage([
      "/agent-sessions/new?agent=agent-1&issue=42&epic=7&repo=org/repo&ws=/workspace",
    ]);
    const textarea = await screen.findByTestId("prompt-textarea");
    fireEvent.change(textarea, { target: { value: "Analyze this" } });
    fireEvent.click(screen.getByTestId("launch-button"));
    await waitFor(() => {
      expect(state.launchCalls).toHaveLength(1);
      expect(screen.getByTestId("current-path")).toHaveTextContent(
        "/Test/sessions/sess-123",
      );
    });
    expect(state.launchCalls[0]).toMatchObject({
      agentRef: "agent-1",
      body: {
        prompt: "Analyze this",
        context: {
          issueNumber: 42,
          epicNumber: 7,
          repository: "org/repo",
          workspacePath: "/workspace",
        },
        attachments: [],
      },
    });
    expect(state.launchCalls[0].body).not.toHaveProperty("context.workspace");
    expect(state.launchCalls[0].body).not.toHaveProperty("runtime");
    expect(state.launchCalls[0].body).not.toHaveProperty("model");
    expect(state.launchCalls[0].body).not.toHaveProperty("variant");
    expect(state.launchCalls[0].body).not.toHaveProperty("skills");
    expect(state.launchCalls[0].body).not.toHaveProperty("maxConcurrentRuns");
  });

  it("sends attachment ids explicitly and displays mixed acceptance results", async () => {
    state.agentsData = [makeAgent("agent-1")];
    state.launchResponse = {
      attachments: [
        {
          id: "att-ok",
          name: "accepted.txt",
          contentType: "text/plain",
          size: 4,
        },
      ],
      rejectedAttachments: [
        {
          id: "att-bad",
          reason: "UnsupportedType",
          message: "Archive files are not supported.",
        },
      ],
      sessionUrl: "/Test/sessions/attachment-canonical-1",
    };
    renderPage(["/agent-sessions/new?agent=agent-1"]);
    const textarea = await screen.findByTestId("prompt-textarea");
    fireEvent.change(textarea, {
      target: {
        value: "Use [accepted.txt](att:att-ok) and [rejected.zip](att:att-bad)",
      },
    });
    fireEvent.click(screen.getByTestId("launch-button"));

    await waitFor(() =>
      expect(
        screen.getByTestId("launch-attachment-results"),
      ).toBeInTheDocument(),
    );
    expect(state.launchCalls[0].body).toMatchObject({
      attachments: ["att-ok", "att-bad"],
    });
    expect(
      screen.getByTestId("attachment-result-accepted-att-ok"),
    ).toHaveTextContent("accepted.txt");
    expect(
      screen.getByTestId("attachment-result-rejected-att-bad"),
    ).toHaveTextContent("Archive files are not supported.");

    fireEvent.click(screen.getByTestId("open-launched-session"));
    await waitFor(() =>
      expect(screen.getByTestId("current-path")).toHaveTextContent(
        "/Test/sessions/attachment-canonical-1",
      ),
    );
    expect(screen.getByTestId("current-path")).not.toHaveTextContent(
      "/Test/Test/sessions/",
    );
  });

  it("navigates to session detail on success", async () => {
    state.agentsData = [makeAgent("agent-1")];
    renderPage(["/agent-sessions/new?agent=agent-1"]);
    const textarea = await screen.findByTestId("prompt-textarea");
    fireEvent.change(textarea, { target: { value: "Hello" } });
    fireEvent.click(screen.getByTestId("launch-button"));
    await waitFor(() => {
      expect(screen.getByTestId("current-path")).toHaveTextContent(
        "/Test/sessions/sess-123",
      );
    });
  });

  it("uses the canonical session URL returned by launch", async () => {
    state.agentsData = [makeAgent("agent-1")];
    state.launchResponse = {
      sessionUrl: "/Test/sessions/canonical-1",
      sessionId: "ignored-session",
    };
    renderPage(["/agent-sessions/new?agent=agent-1"]);
    const textarea = await screen.findByTestId("prompt-textarea");
    fireEvent.change(textarea, { target: { value: "Open directly" } });
    fireEvent.click(screen.getByTestId("launch-button"));
    await waitFor(() =>
      expect(screen.getByTestId("current-path")).toHaveTextContent(
        "/Test/sessions/canonical-1",
      ),
    );
  });

  it("retains one idempotency key when the first response is lost and the launch is retried", async () => {
    state.agentsData = [makeAgent("agent-1")];
    state.launchError = { error: "response lost" };
    state.launchFailuresRemaining = 1;
    renderPage(["/agent-sessions/new?agent=agent-1"]);
    const textarea = await screen.findByTestId("prompt-textarea");
    fireEvent.change(textarea, { target: { value: "Retry me" } });
    fireEvent.click(screen.getByTestId("launch-button"));
    await waitFor(() => expect(state.launchCalls).toHaveLength(1));
    fireEvent.click(screen.getByTestId("launch-button"));
    await waitFor(() => expect(state.launchCalls).toHaveLength(2));

    expect(state.launchCalls[0].idempotencyKey).toBeTruthy();
    expect(state.launchCalls[1].idempotencyKey).toBe(
      state.launchCalls[0].idempotencyKey,
    );
  });

  /* ── Context-ref chip remove ──────────────────────────── */

  it("removes context ref chip when X is clicked", async () => {
    state.agentsData = [makeAgent("agent-1")];
    renderPage(["/agent-sessions/new?issue=42"]);
    expect(
      await screen.findByTestId("context-ref-chip-issue"),
    ).toBeInTheDocument();
    fireEvent.click(screen.getByTestId("remove-ref-issue"));
    expect(
      screen.queryByTestId("context-ref-chip-issue"),
    ).not.toBeInTheDocument();
  });

  /* ── Archived-agent launch disabling ──────────────────── */

  it("disables launch for archived agents", async () => {
    state.agentsData = [makeAgent("agent-1", { status: "archived" })];
    renderPage(["/agent-sessions/new?agent=agent-1"]);
    await screen.findByTestId("archived-warning");
    expect(screen.getByTestId("archived-warning")).toBeInTheDocument();
    const button = screen.getByTestId("launch-button");
    expect(button).toBeDisabled();
  });

  it("excludes archived agents from the launcher picker", async () => {
    state.agentsData = [
      makeAgent("agent-archived", { name: "Archived One", status: "archived" }),
      makeAgent("agent-active", { name: "Active One", status: "active" }),
    ];
    renderPage();
    fireEvent.click(await screen.findByTestId("agent-selector-trigger"));
    expect(
      screen.queryByTestId("agent-option-agent-archived"),
    ).not.toBeInTheDocument();
    expect(screen.getByTestId("agent-option-agent-active")).toBeInTheDocument();
  });

  it("shows the archived warning when ?agent= points at an archived agent even though it is not in the picker", async () => {
    state.agentsData = [makeAgent("agent-1", { status: "archived" })];
    renderPage(["/agent-sessions/new?agent=agent-1"]);
    await screen.findByTestId("archived-warning");
    expect(screen.getByTestId("archived-warning")).toBeInTheDocument();
    expect(screen.getByTestId("launch-button")).toBeDisabled();
  });

  /* ── Error states ─────────────────────────────────────── */

  it("surfaces no-available-runner error state", async () => {
    state.agentsData = [makeAgent("agent-1")];
    state.launchError = {
      error: "No available runner for selected agent",
      code: "NO_AVAILABLE_RUNNER",
    };
    renderPage(["/agent-sessions/new?agent=agent-1"]);
    const textarea = await screen.findByTestId("prompt-textarea");
    fireEvent.change(textarea, { target: { value: "Hello" } });
    fireEvent.click(screen.getByTestId("launch-button"));
    await waitFor(() => {
      expect(screen.getByTestId("error-no-runner")).toBeInTheDocument();
    });
    expect(screen.getByTestId("error-no-runner")).toHaveTextContent(
      /no available runner/i,
    );
  });

  it("surfaces external-agent-unavailable error state", async () => {
    state.agentsData = [makeAgent("agent-1")];
    state.launchError = {
      error: "External agent is unavailable",
      code: "EXTERNAL_AGENT_UNAVAILABLE",
    };
    renderPage(["/agent-sessions/new?agent=agent-1"]);
    const textarea = await screen.findByTestId("prompt-textarea");
    fireEvent.change(textarea, { target: { value: "Hello" } });
    fireEvent.click(screen.getByTestId("launch-button"));
    await waitFor(() => {
      expect(screen.getByTestId("error-external-agent")).toBeInTheDocument();
    });
    expect(screen.getByTestId("error-external-agent")).toHaveAttribute(
      "data-feedback-kind",
      "execution-unavailable",
    );
    expect(screen.getByTestId("error-external-agent")).toHaveTextContent(
      /external agent/i,
    );
    expect(screen.getByTestId("error-external-agent")).toHaveTextContent(
      /wait.*recover/i,
    );
  });

  it("surfaces capacity back-pressure with a next action", async () => {
    state.agentsData = [makeAgent("agent-1")];
    state.availabilityData = [
      {
        agentId: "agent-1",
        canStartNow: false,
        waitingReason: "concurrency-limit",
        activeRuns: 1,
        maxConcurrentRuns: 1,
        capacity: { usedSlots: 1, totalSlots: 2 },
        queuedCount: 1,
      },
    ];
    renderPage(["/agent-sessions/new?agent=agent-1"]);

    const feedback = await screen.findByTestId("agent-availability-feedback");
    expect(feedback).toHaveAttribute("data-feedback-kind", "back-pressure");
    expect(feedback).toHaveTextContent(/concurrency limit/i);
    expect(feedback).toHaveTextContent(/active run.*finish/i);
    fireEvent.change(screen.getByTestId("prompt-textarea"), {
      target: { value: "Try later" },
    });
    expect(screen.getByTestId("launch-button")).not.toBeDisabled();
  });

  it("surfaces runtime execution unavailability with recovery guidance", async () => {
    state.agentsData = [makeAgent("agent-1")];
    state.launchError = {
      error: "runtime unavailable",
      code: "runtime-unavailable",
    };
    renderPage(["/agent-sessions/new?agent=agent-1"]);
    const textarea = await screen.findByTestId("prompt-textarea");
    fireEvent.change(textarea, { target: { value: "Run this" } });
    fireEvent.click(screen.getByTestId("launch-button"));

    const feedback = await screen.findByTestId("error-execution-unavailable");
    expect(feedback).toHaveAttribute(
      "data-feedback-kind",
      "execution-unavailable",
    );
    expect(feedback).toHaveTextContent(/execution backend unavailable/i);
    expect(feedback).toHaveTextContent(/recover/i);
  });

  it("matches no-runner error by message text fallback", async () => {
    state.agentsData = [makeAgent("agent-1")];
    state.launchError = { error: "No available runner for opencode" };
    renderPage(["/agent-sessions/new?agent=agent-1"]);
    const textarea = await screen.findByTestId("prompt-textarea");
    fireEvent.change(textarea, { target: { value: "Hello" } });
    fireEvent.click(screen.getByTestId("launch-button"));
    await waitFor(() => {
      expect(screen.getByTestId("error-no-runner")).toBeInTheDocument();
    });
  });

  it("prevents launch when error is present", async () => {
    state.agentsData = [makeAgent("agent-1")];
    state.launchError = { error: "No available runner" };
    renderPage(["/agent-sessions/new?agent=agent-1"]);
    const textarea = await screen.findByTestId("prompt-textarea");
    fireEvent.change(textarea, { target: { value: "Hello" } });
    fireEvent.click(screen.getByTestId("launch-button"));
    await waitFor(() => {
      expect(screen.getByTestId("error-no-runner")).toBeInTheDocument();
    });
    fireEvent.change(screen.getByTestId("prompt-textarea"), {
      target: { value: "Hello" },
    });
    expect(screen.getByTestId("error-no-runner")).toBeInTheDocument();
  });

  /* ── Executability gating (server-projection driven, client does not synthesize) ── */

  it("blocks the launch button and lists gaps when executability is not-configured", async () => {
    state.agentsData = [
      makeAgent("agent-1", {
        executability: {
          state: "not-configured",
          gaps: [
            {
              code: "instructions-missing",
              message: "Instructions are missing.",
              nextAction: "Add instructions in Agent settings.",
              fixEntryPoint: {
                label: "Agent settings",
                path: "/agents/agent-1",
                command: "mo agent edit agent-1",
              },
            },
          ],
          pendingLaunchNote: null,
        },
      }),
    ];
    renderPage(["/agent-sessions/new?agent=agent-1"]);
    const banner = await screen.findByTestId(
      "agent-executability-not-configured",
    );
    expect(banner).toHaveTextContent(/not-configured/i);
    expect(
      screen.getByTestId("agent-executability-gap-instructions-missing"),
    ).toHaveTextContent(/Instructions are missing/i);
    const button = screen.getByTestId("launch-button");
    expect(button).toBeDisabled();
  });

  it("blocks the launch button when executability is not-executable", async () => {
    state.agentsData = [
      makeAgent("agent-1", {
        executability: {
          state: "not-executable",
          gaps: [
            {
              code: "execution-config-failure",
              message: "The configured model could not be used by the runtime.",
              nextAction:
                "Update the Agent execution settings and run it again.",
              fixEntryPoint: {
                label: "Agent settings",
                path: "/agents/agent-1",
                command: "mo agent edit agent-1",
              },
            },
          ],
          pendingLaunchNote: null,
        },
      }),
    ];
    renderPage(["/agent-sessions/new?agent=agent-1"]);

    await screen.findByTestId("agent-executability-not-executable");
    expect(screen.getByTestId("launch-button")).toBeDisabled();
  });

  it("marks the launch button executable when the server says executable", async () => {
    state.agentsData = [
      makeAgent("agent-1", {
        executability: {
          state: "executable",
          gaps: [],
          pendingLaunchNote: null,
        },
      }),
    ];
    renderPage(["/agent-sessions/new?agent=agent-1"]);
    await screen.findByTestId("agent-executability-executable");
    const textarea = screen.getByTestId("prompt-textarea");
    fireEvent.change(textarea, { target: { value: "Hello" } });
    expect(screen.getByTestId("launch-button")).not.toBeDisabled();
  });

  it("keeps unknown launchable and shows the server pending-launch note", async () => {
    state.agentsData = [
      makeAgent("agent-1", {
        executability: {
          state: "unknown",
          gaps: [],
          pendingLaunchNote:
            "No matching execution evidence exists. This launch is accepted and awaits Runner verification.",
        },
      }),
    ];
    renderPage(["/agent-sessions/new?agent=agent-1"]);
    const hint = await screen.findByTestId("agent-executability-unknown-note");
    expect(hint).toHaveTextContent(/awaits runner verification/i);
    const textarea = screen.getByTestId("prompt-textarea");
    fireEvent.change(textarea, { target: { value: "Hello" } });
    expect(screen.getByTestId("launch-button")).not.toBeDisabled();
  });

  it("surfaces 409 agent_not_configured as an error banner", async () => {
    state.agentsData = [
      makeAgent("agent-1", {
        executability: {
          state: "unknown",
          gaps: [],
          pendingLaunchNote: "Awaiting Runner verification.",
        },
      }),
    ];
    state.launchError = {
      error: "This Agent is not-configured and cannot accept new work.",
      code: "agent_not_configured",
    };
    renderPage(["/agent-sessions/new?agent=agent-1"]);
    const textarea = await screen.findByTestId("prompt-textarea");
    fireEvent.change(textarea, { target: { value: "Hello" } });
    fireEvent.click(screen.getByTestId("launch-button"));
    await waitFor(() => {
      expect(
        screen.getByTestId("error-agent-not-configured"),
      ).toBeInTheDocument();
    });
  });
});
