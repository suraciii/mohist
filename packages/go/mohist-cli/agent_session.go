package mohistcli

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"
)

var agentFields = []string{"id", "projectId", "name", "avatar", "purpose", "description", "instructions", "agentConfig", "effectiveExecutionConfig", "skills", "permissions", "allowedSubagentAgentIds", "maxConcurrentRuns", "status", "createdAt", "updatedAt", "executability", "origin", "overridesBuiltIn"}
var agentLaunchFields = []string{"jobId", "sessionId", "inputId", "turnId", "agentId", "agentName", "workspaceId", "targetId", "origin", "status", "attachments", "rejectedAttachments", "sessionUrl", "transcriptUrl", "jobUrl", "observationUrl"}
var agentSpawnFields = []string{"jobId", "sessionId", "turnId", "parentSessionId", "edgeId"}
var agentJobListFields = []string{"jobId", "agentId", "agentName", "status", "submittedAt", "terminalAt", "failureReason", "recoveryDeadlineAt"}
var agentJobFields = []string{"jobId", "status", "message", "output", "artifactUploadIds", "failureReason", "exitCode", "executionDefinition", "recoveryDeadlineAt"}
var observationFields = []string{"jobId", "jobStatus", "jobMessage", "jobOutput", "jobArtifactUploadIds", "jobFailureReason", "jobExitCode", "sessionId", "sessionActivity", "sessionRuntime", "transcriptUrl", "inputId", "inputAcceptance", "turnId", "turnStatus", "turnResult", "observationUrl", "recoveryDeadlineAt"}
var subscriptionFields = []string{"id", "projectId", "agentId", "name", "match", "responsePrompt", "continue", "position", "status", "createdAt", "updatedAt"}
var subscriptionListFields = []string{"subscriptions", "state", "agentStatus", "executability", "connection"}
var sessionListFields = []string{"id", "source", "runtimeSessionId", "runtime", "activity", "createdAt", "lastActivityAt", "model", "agentId", "agentName", "workflowRunId", "sessionName", "origin", "targetId", "contextRefs"}
var sessionFields = []string{"id", "source", "runtimeSessionId", "runtime", "activity", "createdAt", "lastActivityAt", "model", "resolvedModel", "appliedReasoningEffort", "failureCategory", "failureReason", "toolCallCount", "toolErrorCount", "agentId", "agentName", "workflowRunId", "sessionName", "origin", "targetId", "contextRefs", "usage", "recoveryAvailable", "currentTurnId", "inputs", "turns", "recoveryHistory"}
var sessionTreeFields = []string{"root", "revision", "nodes", "edges", "continuation"}
var transcriptFields = []string{"turns", "partCount", "lastActivityAt", "activity", "status"}
var followupFields = []string{"sessionId", "status", "inputId", "turnId", "inputAcceptance", "turnStatus", "error", "code", "attachments", "rejectedAttachments"}
var stopFields = []string{"state", "interruptUnconfirmed", "operationId", "rootSessionId", "status", "admissionFenceActive", "graphRevision", "membership", "targets"}
var detachFields = []string{"state", "childSessionId", "parentSessionId", "edgeId", "childLaunchJobId", "attachedRevision", "detachedRevision", "historic", "reason"}
var scheduleFields = []string{"scheduleId", "status", "dueAt", "text", "inputId", "createdAt", "idempotencyKey", "cancelledAt"}
var recoveryFields = []string{"id", "status", "contextWindowSize", "contextWindowUsed", "contextUsagePercent", "contextWindowUsedBefore", "operation", "wasCompacted"}
var modelFields = []string{"models", "modelVariants", "reasoningEfforts"}

// canonicalReasoningEfforts is the Server's write-surface vocabulary for
// reasoningEffort. A task-first launch validates it locally so a typo cannot
// be accepted as Runtime behavior.
var canonicalReasoningEfforts = []string{"off", "minimal", "low", "medium", "high", "xhigh", "max"}

func parseAgent(args []string) (command, error) {
	if len(args) == 0 || args[0] == "--help" || args[0] == "-h" {
		return command{help: true, helpText: agentHelp()}, nil
	}
	if args[0] == "job" || args[0] == "subscription" || args[0] == "model" {
		return parseAgentNested(args[0], args[1:])
	}
	action := args[0]
	if !contains([]string{"list", "view", "create", "edit", "archive", "restore", "start", "launch", "spawn", "install"}, action) {
		return command{}, usage("unknown agent command")
	}
	c := command{kind: "agent-" + action, catalog: agentFields}
	if action == "launch" || action == "start" {
		c.catalog = agentLaunchFields
	} else if action == "spawn" {
		c.catalog = agentSpawnFields
	}
	if discovered, ok, err := discoverLeaf(args[1:], c.kind, c.catalog, leafHelp(c.kind, c.catalog)); ok {
		return discovered, err
	}
	start := 1
	if action == "list" {
		c.catalog = agentFields
	} else if action == "create" {
		if len(args) > 1 && !strings.HasPrefix(args[1], "-") {
			c.args = append(c.args, "name", args[1])
			start = 2
		}
	} else if contains([]string{"view", "edit", "archive", "restore", "launch"}, action) {
		if len(args) <= 1 || strings.HasPrefix(args[1], "-") {
			return command{}, usage("agent name or id is required")
		}
		c.args = append(c.args, "agent", args[1])
		start = 2
	} else if action == "spawn" {
		if len(args) <= 1 || strings.HasPrefix(args[1], "-") {
			return command{}, usage("agent ref is required")
		}
		c.args = append(c.args, "agent-ref", args[1])
		start = 2
	} else if action == "install" {
		if len(args) <= 1 || strings.HasPrefix(args[1], "-") {
			return command{}, usage("preset is required")
		}
		c.args = append(c.args, "target", args[1])
		start = 2
	}
	return parseAgentFlags(c, action, args[start:])
}

func parseAgentNested(area string, args []string) (command, error) {
	if len(args) == 0 || args[0] == "--help" || args[0] == "-h" {
		return command{help: true, helpText: "USAGE\n    mo agent " + area + " <action> [flags]"}, nil
	}
	action := args[0]
	c := command{kind: "agent-" + area + "-" + action}
	start := 1
	switch area {
	case "job":
		if action != "list" && action != "view" && action != "observation" {
			return command{}, usage("unknown agent job command")
		}
		c.catalog = agentJobListFields
		if action == "view" {
			c.catalog = agentJobFields
		}
		if action == "observation" {
			c.catalog = observationFields
		}
		if discovered, ok, err := discoverLeaf(args[1:], c.kind, c.catalog, leafHelp(c.kind, c.catalog)); ok {
			return discovered, err
		}
		if len(args) <= 1 || isControlToken(args[1]) {
			return command{}, usage("agent or job id is required")
		}
		c.args = append(c.args, "target", args[1])
		start = 2
	case "model":
		if action != "list" {
			return command{}, usage("unknown agent model command")
		}
		c.catalog = modelFields
		if discovered, ok, err := discoverLeaf(args[1:], c.kind, c.catalog, leafHelp(c.kind, c.catalog)); ok {
			return discovered, err
		}
	case "subscription":
		if !contains([]string{"list", "create", "edit", "delete"}, action) {
			return command{}, usage("unknown agent subscription command")
		}
		c.catalog = subscriptionListFields
		if action != "list" {
			c.catalog = subscriptionFields
		}
		if discovered, ok, err := discoverLeaf(args[1:], c.kind, c.catalog, leafHelp(c.kind, c.catalog)); ok {
			return discovered, err
		}
		if len(args) <= 1 || isControlToken(args[1]) {
			return command{}, usage("agent name or id is required")
		}
		c.args = append(c.args, "agent", args[1])
		start = 2
		if action == "edit" || action == "delete" {
			if len(args) <= start || isControlToken(args[start]) {
				return command{}, usage("subscription id is required")
			}
			c.args = append(c.args, "subscription", args[start])
			start++
		}
	}
	return parseAgentFlags(c, action, args[start:])
}

func parseAgentFlags(c command, action string, args []string) (command, error) {
	for i := 0; i < len(args); i++ {
		arg := canonicalFlag(args[i])
		if arg == "--help" || arg == "-h" {
			return command{help: true, helpText: leafHelp(c.kind, c.catalog)}, nil
		}
		if arg == "--json" {
			var err error
			i, err = jsonFlag(args, i, &c)
			if err != nil {
				return command{}, err
			}
			continue
		}
		if strings.HasPrefix(arg, "--clear-") && !isKnownAgentClearFlag(arg) {
			return command{}, usage("unknown option " + args[i])
		}
		if arg == "--all" || arg == "--continue" || strings.HasPrefix(arg, "--clear-") || arg == "--yes" {
			if arg == "--continue" && action == "edit" && i+1 < len(args) && (args[i+1] == "true" || args[i+1] == "false") {
				c.args = append(c.args, "continue", args[i+1])
				i++
				continue
			}
			c.args = append(c.args, strings.TrimPrefix(arg, "--"), "true")
			continue
		}
		if i+1 >= len(args) {
			return command{}, usage(arg + " requires a value")
		}
		name := strings.TrimPrefix(arg, "--")
		switch arg {
		case "--project", "--status", "--runtime", "--model", "--variant", "--reasoning-effort", "--name", "--description", "--purpose", "--instructions", "--instructions-file", "--avatar-file", "--skills", "--permissions", "--max-concurrent-runs", "--allowed-subagent", "--parent-session", "--prompt", "--prompt-file", "--workspace", "--issue", "--epic", "--repo", "--idempotency-key", "--response-prompt", "--match", "--at", "--text":
			c.args = append(c.args, name, args[i+1])
			i++
		default:
			return command{}, usage("unknown option " + args[i])
		}
	}
	if err := validateFields(c.fields, c.catalog, "mo "+strings.ReplaceAll(c.kind, "-", " ")); err != nil {
		return command{}, err
	}
	if action == "create" && c.kind == "agent-create" && strings.TrimSpace(argValue(c.args, "name", "")) == "" {
		return command{}, usage("--name or agent name is required")
	}
	if action == "edit" {
		for _, key := range []string{"runtime", "model", "variant", "reasoning-effort"} {
			if hasArg(c.args, key) && hasArg(c.args, "clear-"+key) {
				return command{}, usage("--" + key + " cannot be used with --clear-" + key)
			}
		}
	}
	if action == "start" && !hasArg(c.args, "prompt") && !hasArg(c.args, "prompt-file") {
		return command{}, usage("--prompt or --prompt-file is required")
	}
	if action == "start" && hasArg(c.args, "reasoning-effort") && !contains(canonicalReasoningEfforts, argValue(c.args, "reasoning-effort", "")) {
		return command{}, usage("--reasoning-effort must be one of " + strings.Join(canonicalReasoningEfforts, ", "))
	}
	if action == "launch" && !hasArg(c.args, "prompt") && !hasArg(c.args, "prompt-file") {
		return command{}, usage("--prompt or --prompt-file is required")
	}
	if action == "spawn" {
		for _, required := range []string{"project", "parent-session", "prompt", "idempotency-key"} {
			if strings.TrimSpace(argValue(c.args, required, "")) == "" {
				return command{}, usage("--" + required + " is required")
			}
		}
		if hasArg(c.args, "workspace") {
			return command{}, usage("--workspace was retired: child sessions inherit the parent workspace")
		}
	}
	if strings.HasPrefix(c.kind, "agent-subscription-create") {
		for _, required := range []string{"name", "match", "response-prompt"} {
			if strings.TrimSpace(argValue(c.args, required, "")) == "" {
				return command{}, usage("--" + required + " is required")
			}
		}
	}
	if strings.HasPrefix(c.kind, "agent-subscription-edit") && len(c.args) == 4 {
		return command{}, usage("at least one editable option is required")
	}
	return c, nil
}

func isKnownAgentClearFlag(flag string) bool {
	switch flag {
	case "--clear-description", "--clear-purpose", "--clear-runtime", "--clear-model",
		"--clear-variant", "--clear-reasoning-effort", "--clear-avatar", "--clear-skills",
		"--clear-permissions", "--clear-allowed-subagents", "--clear-max-concurrent-runs":
		return true
	default:
		return false
	}
}

func parseSession(args []string) (command, error) {
	if len(args) == 0 || args[0] == "--help" || args[0] == "-h" {
		return command{help: true, helpText: sessionHelp()}, nil
	}
	if args[0] == "schedule" {
		return parseSchedule(args[1:])
	}
	action := args[0]
	if !contains([]string{"list", "tree", "view", "transcript", "followup", "compact", "reset", "stop", "detach"}, action) {
		return command{}, usage("unknown session command")
	}
	c := command{kind: "session-" + action, catalog: sessionFields}
	if action == "list" {
		c.catalog = sessionListFields
	} else if action == "tree" {
		c.catalog = sessionTreeFields
	} else if action == "transcript" {
		c.catalog = transcriptFields
	} else if action == "followup" {
		c.catalog = followupFields
	} else if action == "stop" {
		c.catalog = stopFields
	} else if action == "detach" {
		c.catalog = detachFields
	} else if action == "compact" || action == "reset" {
		c.catalog = recoveryFields
	}
	if discovered, ok, err := discoverLeaf(args[1:], c.kind, c.catalog, leafHelp(c.kind, c.catalog)); ok {
		return discovered, err
	}
	if action != "list" {
		if len(args) < 2 || strings.HasPrefix(args[1], "-") {
			return command{}, usage("session id is required")
		}
		c.args = append(c.args, "session", args[1])
	}
	start := 1
	if action != "list" {
		start = 2
	}
	for i := start; i < len(args); i++ {
		arg := canonicalFlag(args[i])
		if arg == "--help" || arg == "-h" {
			return command{help: true, helpText: leafHelp(c.kind, c.catalog)}, nil
		}
		if arg == "--json" {
			var err error
			i, err = jsonFlag(args, i, &c)
			if err != nil {
				return command{}, err
			}
			continue
		}
		if arg == "--raw" || arg == "--yes" {
			c.args = append(c.args, strings.TrimPrefix(arg, "--"), "true")
			continue
		}
		if i+1 >= len(args) {
			return command{}, usage(arg + " requires a value")
		}
		name := strings.TrimPrefix(arg, "--")
		switch arg {
		case "--project", "--agent", "--issue", "--run", "--workspace", "--limit", "--continuation", "--text", "--text-file", "--attach", "--idempotency-key", "--turn-id":
			c.args = append(c.args, name, args[i+1])
			i++
		default:
			return command{}, usage("unknown option " + args[i])
		}
	}
	if action == "list" {
		count := 0
		for _, key := range []string{"agent", "issue", "run", "workspace"} {
			if hasArg(c.args, key) {
				count++
			}
		}
		if count != 1 {
			return command{}, usage("exactly one of --agent, --issue, --run, or --workspace is required")
		}
	}
	if action == "followup" && !hasArg(c.args, "text") && !hasArg(c.args, "text-file") && !hasArg(c.args, "attach") {
		return command{}, usage("--text or --text-file is required")
	}
	if action == "stop" && strings.TrimSpace(argValue(c.args, "idempotency-key", "")) == "" {
		return command{}, usage("--idempotency-key is required")
	}
	return c, validateFields(c.fields, c.catalog, "mo session "+action)
}

func parseSchedule(args []string) (command, error) {
	if len(args) == 0 || args[0] == "--help" || args[0] == "-h" {
		return command{help: true, helpText: "USAGE\n    mo session schedule <create|list|cancel> [flags]\n\nActions: create, list, cancel"}, nil
	}
	action := args[0]
	if !contains([]string{"create", "list", "cancel"}, action) {
		return command{}, usage("unknown session schedule command")
	}
	c := command{kind: "session-schedule-" + action, catalog: scheduleFields}
	if discovered, ok, err := discoverLeaf(args[1:], c.kind, c.catalog, leafHelp(c.kind, c.catalog)); ok {
		return discovered, err
	}
	start := 1
	if len(args) <= 1 || strings.HasPrefix(args[1], "-") {
		return command{}, usage("session id is required")
	}
	c.args = append(c.args, "session", args[1])
	start = 2
	if action == "cancel" {
		if len(args) <= start || strings.HasPrefix(args[start], "-") {
			return command{}, usage("schedule id is required")
		}
		c.args = append(c.args, "schedule", args[start])
		start++
	}
	for i := start; i < len(args); i++ {
		if args[i] == "--help" || args[i] == "-h" {
			return command{help: true, helpText: leafHelp(c.kind, c.catalog)}, nil
		}
		if args[i] == "--json" {
			var err error
			i, err = jsonFlag(args, i, &c)
			if err != nil {
				return command{}, err
			}
			continue
		}
		if i+1 >= len(args) {
			return command{}, usage(args[i] + " requires a value")
		}
		switch args[i] {
		case "--project", "--at", "--text", "--idempotency-key":
			c.args = append(c.args, strings.TrimPrefix(args[i], "--"), args[i+1])
			i++
		default:
			return command{}, usage("unknown option " + args[i])
		}
	}
	if action == "create" {
		if strings.TrimSpace(argValue(c.args, "at", "")) == "" {
			return command{}, usage("--at is required")
		}
		if strings.TrimSpace(argValue(c.args, "text", "")) == "" {
			return command{}, usage("--text is required")
		}
		if !rfc3339WithOffset(argValue(c.args, "at", "")) {
			return command{}, usage("--at must be an RFC 3339 timestamp with a timezone offset")
		}
	}
	return c, validateFields(c.fields, c.catalog, "mo session schedule "+action)
}

func agentHelp() string {
	return "USAGE\n    mo agent <action> [flags]\n\nManage Agents, AgentJobs, and launches.\n\nActions: list, view, create, edit, archive, restore, start, launch, spawn, install, job, subscription, model"
}
func sessionHelp() string {
	return "USAGE\n    mo session <action> [flags]\n\nManage AgentSessions by stable Session ID.\n\nActions: list, tree, view, transcript, followup, compact, reset, stop, detach, schedule"
}

func sessionCatalog(action string) []string {
	switch action {
	case "list":
		return sessionListFields
	case "tree":
		return sessionTreeFields
	case "transcript":
		return transcriptFields
	case "followup":
		return followupFields
	case "stop":
		return stopFields
	case "detach":
		return detachFields
	case "compact", "reset":
		return recoveryFields
	default:
		return sessionFields
	}
}

func rfc3339WithOffset(value string) bool {
	_, err := time.Parse(time.RFC3339Nano, value)
	return err == nil && (strings.HasSuffix(value, "Z") || strings.Contains(value[len(value)-6:], "+") || strings.Contains(value[len(value)-6:], "-"))
}

func runAgentSession(ctx context.Context, deps Dependencies, c *client, cmd command) int {
	if cmd.fieldsOnly {
		for _, field := range cmd.catalog {
			fmt.Fprintln(deps.Stdout, field)
		}
		return ExitOK
	}
	if strings.HasPrefix(cmd.kind, "agent-") {
		return runAgent(ctx, deps, c, cmd)
	}
	return runSession(ctx, deps, c, cmd)
}

func projectPath(project, suffix string) string {
	return "/api/projects/" + url.PathEscape(project) + suffix
}
func agentPath(project, suffix string) string { return projectPath(project, "/agents"+suffix) }
func sessionResourcePath(project, suffix string) string {
	return projectPath(project, "/sessions"+suffix)
}
func agentSessionPath(project, suffix string) string {
	return projectPath(project, "/agent-sessions"+suffix)
}

func runAgent(ctx context.Context, deps Dependencies, c *client, cmd command) int {
	// Agent start and launch preflight their --prompt / --prompt-file text
	// carrier before Project-state lookup so a missing, permission, or
	// arbitrary read failure stops the command locally with ExitUsage=2
	// instead of falling through to an HTTP request against an implicit
	// Project. The resolved value is stored on cmd.preflightedInput and
	// reused by runLaunch so stdin is consumed at most once per command.
	if cmd.kind == "agent-start" || cmd.kind == "agent-launch" {
		value, err := resolveTextInput(deps, cmd, "prompt", "prompt-file")
		if err != nil {
			writeError(deps.Stderr, err)
			return ExitUsage
		}
		if strings.TrimSpace(value) == "" {
			writeError(deps.Stderr, errors.New("--prompt must not be blank"))
			return ExitUsage
		}
		cmd.preflightedInput = value
	}
	project, ok := resolveProject(deps, argValue(cmd.args, "project", ""))
	if !ok {
		writeError(deps.Stderr, errors.New("Run 'mo project use <name-or-id>' or pass --project <name-or-id>"))
		return ExitOperation
	}
	action := strings.TrimPrefix(cmd.kind, "agent-")
	if strings.HasPrefix(action, "job-") {
		return runAgentJob(ctx, deps, c, project, cmd)
	}
	if strings.HasPrefix(action, "subscription-") {
		return runSubscription(ctx, deps, c, project, cmd)
	}
	if action == "model-list" {
		path := projectPath(project, "/opencode/models")
		if runtime := argValue(cmd.args, "runtime", ""); runtime != "" {
			path += "?runtime=" + url.QueryEscape(runtime)
		}
		return runAgentModelList(ctx, deps, c, path, cmd)
	}
	if action == "list" {
		path := agentPath(project, "")
		q := []string{}
		if hasArg(cmd.args, "all") {
			q = append(q, "all=true")
		}
		if status := argValue(cmd.args, "status", ""); status != "" {
			q = append(q, "status="+url.QueryEscape(status))
		}
		if len(q) > 0 {
			path += "?" + strings.Join(q, "&")
		}
		return agentResourceRequest(ctx, deps, c, path, cmd, true)
	}
	if action == "create" {
		body := agentBody(cmd.args, nil)
		return resourceRequest(ctx, deps, c, http.MethodPost, agentPath(project, ""), body, cmd, false)
	}
	if action == "view" || action == "edit" || action == "archive" || action == "restore" {
		agent, code := resolveAgentRecord(ctx, deps, c, project, argValue(cmd.args, "agent", ""))
		if code != ExitOK {
			return code
		}
		id, _ := agent["id"].(string)
		name, _ := agent["name"].(string)
		if id == "" || name == "" {
			writeError(deps.Stderr, errors.New("error: invalid agent response [invalid_response]"))
			return ExitOperation
		}
		if action == "view" {
			// The by-name read already returned the effective definition, so a
			// built-in view needs no stored id and no second request.
			data, marshalErr := json.Marshal(agent)
			if marshalErr != nil {
				writeError(deps.Stderr, errors.New("error: invalid agent response [invalid_response]"))
				return ExitOperation
			}
			return renderAgentPayload(deps, cmd, data, false)
		}
		if agent["origin"] == "built-in" {
			// A built-in has no stored definition: edit materializes the
			// same-name Project override, and the other mutations have nothing
			// to act on.
			if action == "edit" {
				return runBuiltInAgentEdit(ctx, deps, c, project, name, cmd)
			}
			writeError(deps.Stderr, fmt.Errorf("error: built-in Agent %q has no stored definition; create a Project override with 'mo agent edit %s' first", name, name))
			return ExitOperation
		}
		path := agentPath(project, "/"+url.PathEscape(id))
		if action == "archive" {
			return resourceRequest(ctx, deps, c, http.MethodDelete, path, nil, cmd, false)
		}
		if action == "restore" {
			return resourceRequest(ctx, deps, c, http.MethodPost, path+"/restore", nil, cmd, false)
		}
		body := agentBody(cmd.args, agentConfig(agent))
		for _, key := range []string{"description", "purpose", "runtime", "model", "variant", "reasoning-effort", "skills", "permissions", "max-concurrent-runs"} {
			if hasArg(cmd.args, "clear-"+key) {
				if key == "runtime" || key == "model" || key == "variant" || key == "reasoning-effort" {
					continue
				}
				delete(body, agentJSONNameOrSelf(key))
				body[agentJSONNameOrSelf(key)] = nil
			}
		}
		if len(body) == 0 {
			return ExitUsage
		}
		return resourceRequest(ctx, deps, c, http.MethodPatch, path, body, cmd, false)
	}
	if action == "launch" || action == "start" {
		return runLaunch(ctx, deps, c, project, cmd)
	}
	if action == "spawn" {
		return runSpawn(ctx, deps, c, project, cmd)
	}
	if action == "install" {
		return resourceRequest(ctx, deps, c, http.MethodPost, agentPath(project, "/install"), map[string]any{"preset": argValue(cmd.args, "target", argValue(cmd.args, "agent", ""))}, cmd, false)
	}
	return ExitUsage
}

// agentModelCatalog keeps the runtime/model/variant/reasoning relationships as
// structured values; the Server aggregates them per connected Runner.
type agentModelCatalog struct {
	Models           []string            `json:"models"`
	ModelVariants    map[string][]string `json:"modelVariants"`
	ReasoningEfforts map[string][]string `json:"reasoningEfforts"`
}

// The model catalog endpoint returns one object, not a collection, so the
// response is decoded strictly: missing keys or malformed values must fail
// closed instead of rendering a catalog that looks authoritative.
func runAgentModelList(ctx context.Context, deps Dependencies, c *client, path string, cmd command) int {
	data, err := c.request(ctx, http.MethodGet, path, nil)
	if err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	if cmd.fieldsOnly {
		for _, field := range cmd.catalog {
			fmt.Fprintln(deps.Stdout, field)
		}
		return ExitOK
	}
	var response map[string]json.RawMessage
	if err := json.Unmarshal(data, &response); err != nil {
		writeError(deps.Stderr, errors.New("error: Agent model catalog response has an invalid shape [invalid_response]"))
		return ExitOperation
	}
	for _, key := range []string{"models", "modelVariants", "reasoningEfforts"} {
		if _, ok := response[key]; !ok {
			writeError(deps.Stderr, fmt.Errorf("error: Agent model catalog response is missing %q [invalid_response]", key))
			return ExitOperation
		}
	}
	var catalog agentModelCatalog
	if err := json.Unmarshal(data, &catalog); err != nil {
		writeError(deps.Stderr, errors.New("error: Agent model catalog response has an invalid shape [invalid_response]"))
		return ExitOperation
	}
	if len(cmd.fields) > 0 {
		response = pick(response, cmd.fields)
	}
	return writeJSON(deps.Stdout, response)
}

func agentJSONName(name string) string {
	return map[string]string{
		"reasoning-effort":    "reasoningEffort",
		"max-concurrent-runs": "maxConcurrentRuns",
		"response-prompt":     "responsePrompt",
		"allowed-subagent":    "allowedSubagentAgentIds",
		"allowed-subagents":   "allowedSubagentAgentIds",
		"agent-config":        "agentConfig",
	}[name]
}

func agentBody(args []string, currentConfig map[string]any) map[string]any {
	body := map[string]any{}
	for _, key := range []string{"name", "description", "purpose", "instructions", "skills", "permissions", "max-concurrent-runs"} {
		if hasArg(args, key) {
			value := argValue(args, key, "")
			switch key {
			case "skills", "permissions":
				body[agentJSONNameOrSelf(key)] = splitValues(value)
			case "max-concurrent-runs":
				body[agentJSONNameOrSelf(key)] = integerValue(value)
			default:
				body[agentJSONNameOrSelf(key)] = value
			}
		}
	}
	config := map[string]any{}
	for _, key := range []string{"runtime", "model", "variant", "reasoningEffort"} {
		if value, exists := currentConfig[key]; exists {
			config[key] = value
		}
	}
	configChanged := false
	for _, key := range []string{"runtime", "model", "variant", "reasoning-effort"} {
		jsonKey := agentJSONNameOrSelf(key)
		if hasArg(args, "clear-"+key) {
			delete(config, jsonKey)
			configChanged = true
		}
		if hasArg(args, key) {
			config[jsonKey] = argValue(args, key, "")
			configChanged = true
		}
	}
	if configChanged {
		if len(config) == 0 {
			body["agentConfig"] = nil
		} else {
			body["agentConfig"] = config
		}
	}
	return body
}

func agentConfig(agent map[string]any) map[string]any {
	config, _ := agent["agentConfig"].(map[string]any)
	return config
}

func agentJSONNameOrSelf(name string) string {
	if value := agentJSONName(name); value != "" {
		return value
	}
	return name
}

// agentResourceRequest reads one Agent resource and renders it. Agent list and
// view own a human presentation in addition to the selected-JSON contract, so
// they cannot go through the shared resourceRequest renderer.
func agentResourceRequest(ctx context.Context, deps Dependencies, c *client, path string, cmd command, collection bool) int {
	data, err := c.request(ctx, http.MethodGet, path, nil)
	if err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	return renderAgentPayload(deps, cmd, data, collection)
}

func renderAgentPayload(deps Dependencies, cmd command, data json.RawMessage, collection bool) int {
	if cmd.fieldsOnly {
		for _, field := range cmd.catalog {
			fmt.Fprintln(deps.Stdout, field)
		}
		return ExitOK
	}
	if len(cmd.fields) > 0 {
		selected, err := SelectFields(data, cmd.fields, collection)
		if err != nil {
			writeError(deps.Stderr, err)
			return ExitOperation
		}
		return writeJSON(deps.Stdout, json.RawMessage(selected))
	}
	if renderAgentHuman(deps.Stdout, cmd.kind, data) {
		return ExitOK
	}
	return writeJSON(deps.Stdout, json.RawMessage(data))
}

// renderAgentHuman renders the Agent list and detail views for people. It
// reports false on a shape it does not own so the caller falls back to raw
// JSON instead of inventing a value.
func renderAgentHuman(out io.Writer, kind string, data json.RawMessage) bool {
	switch kind {
	case "agent-list":
		var agents []map[string]json.RawMessage
		if json.Unmarshal(data, &agents) != nil || agents == nil {
			return false
		}
		if len(agents) == 0 {
			fmt.Fprintln(out, "No Agents")
			return true
		}
		fmt.Fprintln(out, "name  origin  runtime  model  status")
		for _, agent := range agents {
			runtime, model := agentEffectiveConfig(agent)
			fmt.Fprintf(out, "%s  %s  %s  %s  %s\n",
				rawString(agent["name"]), agentOrigin(agent), runtime, model, rawString(agent["status"]))
		}
		return true
	case "agent-view":
		var agent map[string]json.RawMessage
		if json.Unmarshal(data, &agent) != nil || agent == nil {
			return false
		}
		runtime, model := agentEffectiveConfig(agent)
		fmt.Fprintln(out, "name: "+rawString(agent["name"]))
		fmt.Fprintln(out, "origin: "+agentOrigin(agent))
		fmt.Fprintln(out, "status: "+rawString(agent["status"]))
		fmt.Fprintln(out, "runtime: "+runtime)
		fmt.Fprintln(out, "model: "+model)
		fmt.Fprintln(out, "variant: "+agentEffectiveVariant(agent))
		renderAgentReadiness(out, agent)
		return true
	}
	return false
}

func agentOrigin(agent map[string]json.RawMessage) string {
	origin := rawString(agent["origin"])
	if origin == "" {
		return "-"
	}
	if rawBool(agent["overridesBuiltIn"]) {
		return origin + " (overrides built-in)"
	}
	return origin
}

func agentEffectiveConfig(agent map[string]json.RawMessage) (string, string) {
	runtime := agentEffectiveField(agent, "runtime")
	if runtime == "" {
		runtime = "-"
	}
	model := agentEffectiveField(agent, "model")
	if model == "" {
		// An unset Model is the Runtime's own choice, never a borrowed value.
		model = "Runtime default"
	}
	return runtime, model
}

func agentEffectiveVariant(agent map[string]json.RawMessage) string {
	if variant := agentEffectiveField(agent, "variant"); variant != "" {
		return variant
	}
	return "-"
}

func agentEffectiveField(agent map[string]json.RawMessage, name string) string {
	var effective map[string]json.RawMessage
	if json.Unmarshal(agent["effectiveExecutionConfig"], &effective) != nil {
		return ""
	}
	return rawString(effective[name])
}

func renderAgentReadiness(out io.Writer, agent map[string]json.RawMessage) {
	var executability map[string]json.RawMessage
	if json.Unmarshal(agent["executability"], &executability) != nil || executability == nil {
		return
	}
	if state := rawString(executability["state"]); state != "" {
		fmt.Fprintln(out, "readiness: "+state)
	}
	var gaps []map[string]json.RawMessage
	if json.Unmarshal(executability["gaps"], &gaps) != nil {
		return
	}
	for _, gap := range gaps {
		if message := rawString(gap["message"]); message != "" {
			fmt.Fprintln(out, "gap: "+message)
		}
		if nextAction := rawString(gap["nextAction"]); nextAction != "" {
			fmt.Fprintln(out, "next action: "+nextAction)
		}
	}
	if note := rawString(executability["pendingLaunchNote"]); note != "" {
		fmt.Fprintln(out, "pending launch: "+note)
	}
}

func rawBool(raw json.RawMessage) bool {
	var value bool
	return json.Unmarshal(raw, &value) == nil && value
}

func resolveAgent(ctx context.Context, deps Dependencies, c *client, project, ref string) (string, int) {
	agent, code := resolveAgentRecord(ctx, deps, c, project, ref)
	if code != ExitOK {
		return "", code
	}
	id, _ := agent["id"].(string)
	return id, ExitOK
}

func resolveAgentRecord(ctx context.Context, deps Dependencies, c *client, project, ref string) (map[string]any, int) {
	path := agentPath(project, "/"+url.PathEscape(ref))
	byID := strings.HasPrefix(ref, "agent_")
	if !byID {
		// Names resolve through the catch-all by-name route, which applies the
		// same case-insensitive resolution as launch: a stored Project Agent
		// shadows a built-in of the same name, and built-in names contain a
		// path separator that the id route cannot carry.
		path = agentPath(project, "/by-name/"+agentNamePath(ref))
	}
	data, err := c.request(ctx, http.MethodGet, path, nil)
	if err != nil {
		return nil, operationExit(deps, ctx, err)
	}
	var agent map[string]any
	if json.Unmarshal(data, &agent) != nil || agent == nil {
		writeError(deps.Stderr, errors.New("error: invalid agent response [invalid_response]"))
		return nil, ExitOperation
	}
	id, _ := agent["id"].(string)
	if id == "" || (byID && id != ref) {
		writeError(deps.Stderr, errors.New("error: invalid agent response [invalid_response]"))
		return nil, ExitOperation
	}
	return agent, ExitOK
}

// agentNamePath encodes one Agent name for the by-name route. Built-in names
// contain '/', which is a path separator, so each segment is escaped
// separately and the separators stay literal.
func agentNamePath(name string) string {
	segments := strings.Split(name, "/")
	for i, segment := range segments {
		segments[i] = url.PathEscape(segment)
	}
	return strings.Join(segments, "/")
}

// runBuiltInAgentEdit materializes a built-in Workflow Agent as a same-name
// Project Agent through the Server's override operation. The Server copies the
// built-in definition; the CLI sends only the caller's changes.
func runBuiltInAgentEdit(ctx context.Context, deps Dependencies, c *client, project, name string, cmd command) int {
	if flag := builtInOverrideUnsupportedFlag(cmd.args); flag != "" {
		if strings.HasPrefix(flag, "--clear-") {
			writeError(deps.Stderr, fmt.Errorf("error: %s cannot be used when editing built-in Agent %q; an override starts from the built-in definition, so there is nothing to clear", flag, name))
		} else {
			writeError(deps.Stderr, fmt.Errorf("error: %s is not supported when editing built-in Agent %q; an override carries execution configuration, --description, and --skills", flag, name))
		}
		return ExitUsage
	}
	body := agentBody(cmd.args, nil)
	if len(body) == 0 {
		writeError(deps.Stderr, errors.New("error: at least one editable option is required"))
		return ExitUsage
	}
	body["name"] = name
	data, err := c.request(ctx, http.MethodPost, agentPath(project, "/overrides"), body)
	if err != nil {
		return builtInOverrideFailure(deps, ctx, name, err)
	}
	created := map[string]any{}
	if json.Unmarshal(data, &created) != nil {
		created = nil
	}
	if id, _ := created["id"].(string); id != "" {
		fmt.Fprintf(deps.Stderr, "Override created: built-in Agent %q is now overridden by Project Agent %s.\n", name, id)
	} else {
		fmt.Fprintf(deps.Stderr, "Override created: built-in Agent %q now has a Project override.\n", name)
	}
	return renderAgentPayload(deps, cmd, data, false)
}

// builtInOverrideUnsupportedFlag returns the first caller flag an override
// cannot carry. The override request accepts only name, description,
// agentConfig, and skills, so a flag outside that set is rejected locally
// instead of being dropped or flattened by the Server boundary.
func builtInOverrideUnsupportedFlag(args []string) string {
	for _, key := range []string{"name", "purpose", "instructions", "instructions-file", "avatar-file", "permissions", "max-concurrent-runs", "allowed-subagent"} {
		if hasArg(args, key) {
			return "--" + key
		}
	}
	for i := 0; i+1 < len(args); i += 2 {
		if strings.HasPrefix(args[i], "clear-") {
			return "--" + args[i]
		}
	}
	return ""
}

// builtInOverrideFailure renders the override operation's named repair cases
// from the Server envelope. Anything else keeps the standard error envelope.
func builtInOverrideFailure(deps Dependencies, ctx context.Context, name string, err error) int {
	var opErr *operationError
	if errors.As(err, &opErr) {
		switch opErr.code {
		case "agent_override_conflict":
			existing := overrideAgentLabel(name, errorDetailString(opErr.details, "agentId"))
			writeError(deps.Stderr, fmt.Errorf("error: %s already overrides the built-in Agent; edit it instead with 'mo agent edit %s' [agent_override_conflict]", existing, name))
			return ExitOperation
		case "agent_override_archived":
			archived := overrideAgentLabel(name, errorDetailString(opErr.details, "agentId"))
			writeError(deps.Stderr, fmt.Errorf("error: %s is archived and shadows the built-in Agent; restore or rename it before creating an override [agent_override_archived]", archived))
			return ExitOperation
		}
	}
	return operationExit(deps, ctx, err)
}

func overrideAgentLabel(name, id string) string {
	label := fmt.Sprintf("Project Agent %q", name)
	if id != "" {
		label += " (" + id + ")"
	}
	return label
}

func errorDetailString(details json.RawMessage, name string) string {
	var values map[string]any
	if len(details) == 0 || json.Unmarshal(details, &values) != nil {
		return ""
	}
	value, _ := values[name].(string)
	return value
}
func runAgentJob(ctx context.Context, deps Dependencies, c *client, project string, cmd command) int {
	action := strings.TrimPrefix(cmd.kind, "agent-job-")
	target := argValue(cmd.args, "target", "")
	if action == "list" {
		id, code := resolveAgent(ctx, deps, c, project, target)
		if code != ExitOK {
			return code
		}
		path := agentPath(project, "/"+url.PathEscape(id)+"/jobs")
		if s := argValue(cmd.args, "status", ""); s != "" {
			path += "?status=" + url.QueryEscape(s)
		}
		return resourceRequest(ctx, deps, c, http.MethodGet, path, nil, cmd, true)
	}
	suffix := ""
	if action == "observation" {
		suffix = "/launch-observation"
	}
	return resourceRequest(ctx, deps, c, http.MethodGet, projectPath(project, "/agent-jobs/"+url.PathEscape(target)+suffix), nil, cmd, false)
}
func runSubscription(ctx context.Context, deps Dependencies, c *client, project string, cmd command) int {
	agent, code := resolveAgent(ctx, deps, c, project, argValue(cmd.args, "agent", ""))
	if code != ExitOK {
		return code
	}
	action := strings.TrimPrefix(cmd.kind, "agent-subscription-")
	base := agentPath(project, "/"+url.PathEscape(agent)+"/subscriptions")
	if action == "list" {
		return resourceRequest(ctx, deps, c, http.MethodGet, base, nil, cmd, false)
	}
	id := argValue(cmd.args, "subscription", "")
	path := base
	method := http.MethodPost
	body := map[string]any{}
	if action == "edit" {
		method = http.MethodPatch
		path += "/" + url.PathEscape(id)
		for _, k := range []string{"name", "match", "response-prompt", "continue"} {
			if hasArg(cmd.args, k) {
				body[subscriptionJSONName(k)] = subscriptionValue(k, argValue(cmd.args, k, ""))
			}
		}
	}
	if action == "delete" {
		method = http.MethodDelete
		path += "/" + url.PathEscape(id)
		return requestAndRender(ctx, deps, c, method, path, nil, cmd, false, "")
	}
	if action == "create" {
		for _, k := range []string{"name", "match", "response-prompt", "continue"} {
			if hasArg(cmd.args, k) {
				body[subscriptionJSONName(k)] = subscriptionValue(k, argValue(cmd.args, k, ""))
			}
		}
	}
	return requestAndRender(ctx, deps, c, method, path, body, cmd, method == http.MethodPost || method == http.MethodPatch, argValue(cmd.args, "idempotency-key", ""))
}

func splitValues(value string) []string {
	parts := strings.Split(value, ",")
	result := make([]string, 0, len(parts))
	for _, part := range parts {
		if value := strings.TrimSpace(part); value != "" {
			result = append(result, value)
		}
	}
	return result
}

func integerValue(value string) any {
	parsed, err := strconv.Atoi(value)
	if err != nil {
		return value
	}
	return parsed
}

func subscriptionJSONName(name string) string {
	if name == "response-prompt" {
		return "responsePrompt"
	}
	return name
}

func subscriptionValue(name, value string) any {
	if name == "continue" {
		return value == "true"
	}
	return value
}
func runLaunch(ctx context.Context, deps Dependencies, c *client, project string, cmd command) int {
	action := strings.TrimPrefix(cmd.kind, "agent-")
	// The carrier was already resolved by runAgent before Project-state
	// lookup; reuse the preflighted value so stdin is consumed at most once.
	prompt := cmd.preflightedInput
	if prompt == "" {
		return ExitUsage
	}
	body := map[string]any{"prompt": prompt}
	if hasArg(cmd.args, "workspace") || hasArg(cmd.args, "issue") || hasArg(cmd.args, "epic") || hasArg(cmd.args, "repo") {
		body["context"] = map[string]any{"workspace": argValue(cmd.args, "workspace", ""), "issueNumber": argValue(cmd.args, "issue", ""), "epicNumber": argValue(cmd.args, "epic", ""), "repository": argValue(cmd.args, "repo", "")}
	}
	// Task-first creation accepts the effort hint and freezes it on the
	// created Agent definition. Definition-first launch takes no execution
	// hint, so the flag stays task-first only.
	if action == "start" && hasArg(cmd.args, "reasoning-effort") {
		body["reasoningEffort"] = argValue(cmd.args, "reasoning-effort", "")
	}
	path := agentPath(project, "/"+url.PathEscape(argValue(cmd.args, "agent", ""))+"/sessions")
	if action == "start" {
		path = projectPath(project, "/agent-tasks")
	}
	return requestAndRender(ctx, deps, c, http.MethodPost, path, body, cmd, true, argValue(cmd.args, "idempotency-key", ""))
}
func runSpawn(ctx context.Context, deps Dependencies, c *client, project string, cmd command) int {
	body := map[string]any{"targetAgentRef": argValue(cmd.args, "agent-ref", argValue(cmd.args, "agent", "")), "prompt": argValue(cmd.args, "prompt", "")}
	return requestAndRender(ctx, deps, c, http.MethodPost, agentSessionPath(project, "/"+url.PathEscape(argValue(cmd.args, "parent-session", ""))+"/spawns"), body, cmd, true, argValue(cmd.args, "idempotency-key", ""))
}

func runSession(ctx context.Context, deps Dependencies, c *client, cmd command) int {
	// Session follow-up preflights its --text / --text-file text carrier
	// before Project-state lookup so a missing, permission, or arbitrary
	// read failure stops the command locally with ExitUsage=2 instead of
	// falling through to an HTTP request against an implicit Project. The
	// resolved value is stored on cmd.preflightedInput so the body builder
	// below reuses it instead of reading the carrier a second time.
	if cmd.kind == "session-followup" {
		value, err := resolveTextInput(deps, cmd, "text", "text-file")
		if err != nil {
			writeError(deps.Stderr, err)
			return ExitUsage
		}
		if strings.TrimSpace(value) == "" && !hasArg(cmd.args, "attach") {
			writeError(deps.Stderr, errors.New("--text must not be blank"))
			return ExitUsage
		}
		cmd.preflightedInput = value
	}
	project, ok := resolveProject(deps, argValue(cmd.args, "project", ""))
	if !ok {
		writeError(deps.Stderr, errors.New("Run 'mo project use <name-or-id>' or pass --project <name-or-id>"))
		return ExitOperation
	}
	action := strings.TrimPrefix(cmd.kind, "session-")
	if strings.HasPrefix(action, "schedule-") {
		return runSessionSchedule(ctx, deps, c, project, cmd)
	}
	if action == "list" {
		path := sessionResourcePath(project, "")
		key := "agent"
		for _, k := range []string{"agent", "issue", "run", "workspace"} {
			if hasArg(cmd.args, k) {
				key = k
				break
			}
		}
		path += "?" + key + "=" + url.QueryEscape(argValue(cmd.args, key, ""))
		if l := argValue(cmd.args, "limit", ""); l != "" {
			path += "&limit=" + url.QueryEscape(l)
		}
		return resourceRequest(ctx, deps, c, http.MethodGet, path, nil, cmd, true)
	}
	sid := url.PathEscape(argValue(cmd.args, "session", ""))
	if action == "view" {
		return resourceRequest(ctx, deps, c, http.MethodGet, sessionResourcePath(project, "/"+sid), nil, cmd, false)
	}
	if action == "transcript" {
		path := sessionResourcePath(project, "/"+sid+"/transcript")
		if hasArg(cmd.args, "raw") {
			path += "?view=raw"
		}
		return resourceRequest(ctx, deps, c, http.MethodGet, path, nil, cmd, false)
	}
	if action == "tree" {
		return resourceRequest(ctx, deps, c, http.MethodGet, agentSessionPath(project, "/"+sid+"/tree"), nil, cmd, false)
	}
	if action == "detach" {
		return requestAndRender(ctx, deps, c, http.MethodPost, agentSessionPath(project, "/"+sid+"/detach"), nil, cmd, false, "")
	}
	if action == "followup" {
		body := map[string]any{"text": cmd.preflightedInput}
		return requestAndRender(ctx, deps, c, http.MethodPost, agentSessionPath(project, "/"+sid+"/followup"), body, cmd, true, argValue(cmd.args, "idempotency-key", ""))
	}
	if action == "compact" || action == "reset" {
		return requestAndRender(ctx, deps, c, http.MethodPost, agentSessionPath(project, "/"+sid+"/"+action), map[string]any{}, cmd, false, argValue(cmd.args, "idempotency-key", ""))
	}
	if action == "stop" {
		body := map[string]any{}
		if hasArg(cmd.args, "turn-id") {
			body["turnId"] = argValue(cmd.args, "turn-id", "")
		}
		return requestAndRender(ctx, deps, c, http.MethodPost, agentSessionPath(project, "/"+sid+"/stop"), body, cmd, true, argValue(cmd.args, "idempotency-key", ""))
	}
	return ExitUsage
}

func runSchedule(ctx context.Context, deps Dependencies, c *client, cmd command) int {
	return ExitUsage
}

func runSessionSchedule(ctx context.Context, deps Dependencies, c *client, project string, cmd command) int {
	action := strings.TrimPrefix(cmd.kind, "session-schedule-")
	base := agentSessionPath(project, "/"+url.PathEscape(argValue(cmd.args, "session", ""))+"/schedules")
	if action == "list" {
		return resourceRequest(ctx, deps, c, http.MethodGet, base, nil, cmd, true)
	}
	if action == "cancel" {
		return requestAndRender(ctx, deps, c, http.MethodPost, base+"/"+url.PathEscape(argValue(cmd.args, "schedule", ""))+"/cancel", map[string]any{}, cmd, false, "")
	}
	if action == "create" {
		at := argValue(cmd.args, "at", "")
		parsed, err := time.Parse(time.RFC3339Nano, at)
		if err != nil || !parsed.After(deps.Now()) {
			writeError(deps.Stderr, errors.New("--at must be later than the current time"))
			return ExitUsage
		}
		key := argValue(cmd.args, "idempotency-key", "")
		if key == "" {
			key = fmt.Sprintf("%d", deps.Now().UnixNano())
			fmt.Fprintln(deps.Stdout, "Idempotency-Key: "+key)
		}
		return requestAndRender(ctx, deps, c, http.MethodPost, base, map[string]any{"text": argValue(cmd.args, "text", ""), "dueAt": at}, cmd, true, key)
	}
	return ExitUsage
}

func requestAndRender(ctx context.Context, deps Dependencies, c *client, method, path string, body any, cmd command, retry bool, key string) int {
	data, err := c.requestHeaders(ctx, method, path, body, map[string]string{"Idempotency-Key": key}, retry)
	if err != nil {
		return operationExit(deps, ctx, err)
	}
	if cmd.fieldsOnly {
		for _, f := range cmd.catalog {
			fmt.Fprintln(deps.Stdout, f)
		}
		return ExitOK
	}
	if len(cmd.fields) > 0 {
		selected, e := SelectFields(data, cmd.fields, false)
		if e != nil {
			writeError(deps.Stderr, e)
			return ExitOperation
		}
		return writeJSON(deps.Stdout, json.RawMessage(selected))
	}
	if len(data) == 0 || string(data) == "null" || string(data) == "{}" {
		fmt.Fprintln(deps.Stdout, "OK")
		return ExitOK
	}
	return writeJSON(deps.Stdout, json.RawMessage(data))
}

func (c *client) requestHeaders(ctx context.Context, method, path string, body any, headers map[string]string, retry bool) (json.RawMessage, error) {
	keyed := headers["Idempotency-Key"] != ""
	var last error
	attempts := 1
	if retry {
		attempts = 2
	}
	for range attempts {
		var reader io.Reader
		if body != nil {
			b, e := json.Marshal(body)
			if e != nil {
				return nil, classifyFailure(&operationError{message: "error: request could not be created [request_error]"}, method, keyed, failureLocal, 0)
			}
			reader = strings.NewReader(string(b))
		}
		req, e := http.NewRequestWithContext(ctx, method, c.base.String()+path, reader)
		if e != nil {
			return nil, classifyFailure(&operationError{message: "error: request could not be created [request_error]"}, method, keyed, failureLocal, 0)
		}
		req.Header.Set("Accept", "application/json")
		req.Header.Set(operatorIDHeader, c.operatorID)
		if body != nil {
			req.Header.Set("Content-Type", "application/json")
		}
		if c.token != "" && (!c.machineLocal || isLoopback(c.base)) {
			req.Header.Set("Authorization", "Bearer "+c.token)
		}
		for k, v := range headers {
			if v != "" {
				req.Header.Set(k, v)
			}
		}
		resp, e := c.http.Do(req)
		if e != nil {
			if errors.Is(e, context.Canceled) || errors.Is(e, context.DeadlineExceeded) {
				return nil, e
			}
			last = classifyFailure(&operationError{message: "error: Mohist Server request failed [service_unavailable]", code: "service_unavailable"}, method, keyed, failureSubmit, 0)
			continue
		}
		b, e := io.ReadAll(resp.Body)
		resp.Body.Close()
		if e != nil {
			return nil, classifyFailure(&operationError{message: "error: Mohist Server response could not be read [response_error]", code: "response_error"}, method, keyed, failureResponse, resp.StatusCode)
		}
		var env envelope
		if json.Unmarshal(b, &env) != nil {
			return nil, classifyFailure(responseStatusFailure(resp.StatusCode), method, keyed, failureResponse, resp.StatusCode)
		}
		success := (env.Success == nil && resp.StatusCode >= 200 && resp.StatusCode < 300) || (env.Success != nil && *env.Success)
		if !success || resp.StatusCode < 200 || resp.StatusCode >= 300 {
			code := env.Code
			if code == "" {
				code = statusCodeName(resp.StatusCode)
			}
			message := env.Error
			if message == "" {
				message = "Mohist Server request failed"
			}
			return nil, classifyFailure(&operationError{message: "error: " + message + " [" + code + "]", code: code, details: env.Details, effect: env.Effect, retrySafe: env.RetrySafe, nextAction: env.NextAction}, method, keyed, failureServer, resp.StatusCode)
		}
		return env.Data, nil
	}
	return nil, last
}
