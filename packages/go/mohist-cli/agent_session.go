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

var agentFields = []string{"id", "projectId", "name", "avatar", "purpose", "description", "instructions", "agentConfig", "effectiveExecutionConfig", "skills", "permissions", "allowedSubagentAgentIds", "maxConcurrentRuns", "status", "createdAt", "updatedAt", "executability"}
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
var modelFields = []string{"id"}

func parseAgent(args []string) (command, error) {
	if len(args) == 0 || args[0] == "--help" || args[0] == "-h" {
		return command{help: true, helpText: agentHelp()}, nil
	}
	if args[0] == "job" || args[0] == "subscription" || args[0] == "model" {
		return parseAgentNested(args[0], args[1:])
	}
	action := args[0]
	if len(args) == 2 && (args[1] == "--help" || args[1] == "-h") {
		catalog := agentFields
		if action == "launch" || action == "start" {
			catalog = agentLaunchFields
		}
		if action == "spawn" {
			catalog = agentSpawnFields
		}
		return command{help: true, helpText: leafHelp("agent-"+action, catalog)}, nil
	}
	if !contains([]string{"list", "view", "create", "edit", "archive", "restore", "start", "launch", "spawn", "install"}, action) {
		return command{}, usage("unknown agent command")
	}
	c := command{kind: "agent-" + action, catalog: agentFields}
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
	if len(args) == 2 && (args[1] == "--help" || args[1] == "-h") {
		catalog := agentJobListFields
		if area == "job" && action == "view" {
			catalog = agentJobFields
		}
		if area == "job" && action == "observation" {
			catalog = observationFields
		}
		if area == "model" {
			catalog = modelFields
		}
		if area == "subscription" && action != "list" {
			catalog = subscriptionFields
		}
		return command{help: true, helpText: leafHelp("agent-"+area+"-"+action, catalog)}, nil
	}
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
		if len(args) <= 1 {
			return command{}, usage("agent or job id is required")
		}
		c.args = append(c.args, "target", args[1])
		start = 2
	case "model":
		if action != "list" {
			return command{}, usage("unknown agent model command")
		}
		c.catalog = modelFields
	case "subscription":
		if !contains([]string{"list", "create", "edit", "delete"}, action) {
			return command{}, usage("unknown agent subscription command")
		}
		c.catalog = subscriptionListFields
		if action != "list" {
			c.catalog = subscriptionFields
		}
		if len(args) <= 1 {
			return command{}, usage("agent name or id is required")
		}
		c.args = append(c.args, "agent", args[1])
		start = 2
		if action == "edit" || action == "delete" {
			if len(args) <= start {
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
		arg := args[i]
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
			return command{}, usage("unknown option " + arg)
		}
	}
	if err := validateFields(c.fields, c.catalog, "mo "+strings.ReplaceAll(c.kind, "-", " ")); err != nil {
		return command{}, err
	}
	if action == "create" && c.kind == "agent-create" && strings.TrimSpace(argValue(c.args, "name", "")) == "" {
		return command{}, usage("--name or agent name is required")
	}
	if action == "start" && !hasArg(c.args, "prompt") && !hasArg(c.args, "prompt-file") {
		return command{}, usage("--prompt or --prompt-file is required")
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
	if len(args) == 2 && (args[1] == "--help" || args[1] == "-h") {
		return command{help: true, helpText: leafHelp("session-"+action, sessionCatalog(action))}, nil
	}
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
		arg := args[i]
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
			return command{}, usage("unknown option " + arg)
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
	if len(args) == 2 && (args[1] == "--help" || args[1] == "-h") {
		return command{help: true, helpText: leafHelp("session-schedule-"+action, scheduleFields)}, nil
	}
	c := command{kind: "session-schedule-" + action, catalog: scheduleFields}
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
		return resourceRequest(ctx, deps, c, http.MethodGet, path, nil, cmd, true)
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
		return resourceRequest(ctx, deps, c, http.MethodGet, path, nil, cmd, true)
	}
	if action == "create" {
		body := agentBody(cmd.args)
		return resourceRequest(ctx, deps, c, http.MethodPost, agentPath(project, ""), body, cmd, false)
	}
	if action == "view" || action == "edit" || action == "archive" || action == "restore" {
		id, code := resolveAgent(ctx, deps, c, project, argValue(cmd.args, "agent", ""))
		if code != ExitOK {
			return code
		}
		path := agentPath(project, "/"+url.PathEscape(id))
		if action == "view" {
			return resourceRequest(ctx, deps, c, http.MethodGet, path, nil, cmd, false)
		}
		if action == "archive" {
			return resourceRequest(ctx, deps, c, http.MethodDelete, path, nil, cmd, false)
		}
		if action == "restore" {
			return resourceRequest(ctx, deps, c, http.MethodPost, path+"/restore", nil, cmd, false)
		}
		body := agentBody(cmd.args)
		for _, key := range []string{"description", "purpose", "runtime", "model", "variant", "reasoning-effort", "skills", "permissions", "max-concurrent-runs"} {
			if hasArg(cmd.args, "clear-"+key) {
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

func agentBody(args []string) map[string]any {
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
	for _, key := range []string{"runtime", "model", "variant", "reasoning-effort"} {
		if hasArg(args, key) {
			config[agentJSONNameOrSelf(key)] = argValue(args, key, "")
		}
	}
	if len(config) > 0 {
		body["agentConfig"] = config
	}
	return body
}

func agentJSONNameOrSelf(name string) string {
	if value := agentJSONName(name); value != "" {
		return value
	}
	return name
}

func resolveAgent(ctx context.Context, deps Dependencies, c *client, project, ref string) (string, int) {
	if strings.HasPrefix(ref, "agent_") {
		if _, err := c.request(ctx, http.MethodGet, agentPath(project, "/"+url.PathEscape(ref)), nil); err != nil {
			writeError(deps.Stderr, err)
			return "", ExitOperation
		}
		return ref, ExitOK
	}
	data, err := c.request(ctx, http.MethodGet, agentPath(project, "?all=true"), nil)
	if err != nil {
		return "", operationExit(deps, ctx, err)
	}
	var list []map[string]any
	if json.Unmarshal(data, &list) != nil {
		writeError(deps.Stderr, errors.New("error: invalid agent response [invalid_response]"))
		return "", ExitOperation
	}
	for _, item := range list {
		if item["name"] == ref {
			if id, ok := item["id"].(string); ok {
				return id, ExitOK
			}
		}
	}
	writeError(deps.Stderr, fmt.Errorf("Agent %q not found", ref))
	return "", ExitOperation
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
	prompt := argValue(cmd.args, "prompt", "")
	if prompt == "" && hasArg(cmd.args, "prompt-file") {
		prompt = inputValue(deps, cmd, "prompt", "prompt-file")
	}
	if prompt == "" {
		return ExitUsage
	}
	body := map[string]any{"prompt": prompt}
	if hasArg(cmd.args, "workspace") || hasArg(cmd.args, "issue") || hasArg(cmd.args, "epic") || hasArg(cmd.args, "repo") {
		body["context"] = map[string]any{"workspace": argValue(cmd.args, "workspace", ""), "issueNumber": argValue(cmd.args, "issue", ""), "epicNumber": argValue(cmd.args, "epic", ""), "repository": argValue(cmd.args, "repo", "")}
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
		body := map[string]any{"text": argValue(cmd.args, "text", "")}
		if body["text"] == "" && hasArg(cmd.args, "text-file") {
			body["text"] = inputValue(deps, cmd, "text", "text-file")
		}
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
	var last error
	attempts := 1
	if retry {
		attempts = 2
	}
	for i := 0; i < attempts; i++ {
		var reader io.Reader
		if body != nil {
			b, e := json.Marshal(body)
			if e != nil {
				return nil, e
			}
			reader = strings.NewReader(string(b))
		}
		req, e := http.NewRequestWithContext(ctx, method, c.base.String()+path, reader)
		if e != nil {
			return nil, e
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
			last = &operationError{message: "error: Mohist Server request failed [service_unavailable]"}
			continue
		}
		b, e := io.ReadAll(resp.Body)
		resp.Body.Close()
		if e != nil {
			return nil, e
		}
		var env envelope
		if json.Unmarshal(b, &env) != nil {
			return nil, &operationError{message: responseStatusError(resp.StatusCode)}
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
			return nil, &operationError{message: "error: " + message + " [" + code + "]"}
		}
		return env.Data, nil
	}
	return nil, last
}
