package mohistcli

import (
	"context"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"path/filepath"
	"strings"
)

var runnerFields = []string{"id", "kind", "hostname", "scope", "status", "registeredAt", "lastHeartbeatAt", "connectionState", "capabilities", "coderModels", "coderModelCount", "capacity", "activeWorks"}
var auditFields = []string{"id", "subjectId", "eventType", "targetKind", "targetId", "occurredAt", "metadata"}
var otelQueryFields = []string{"columns", "rows", "truncated", "truncate_reason"}
var otelTraceFields = []string{"trace_id", "service_name", "start_time", "end_time", "span_count"}
var githubFields = []string{"id", "projectId", "owner", "repo", "repositoryName", "approvers", "status", "installationId", "repositoryNodeId", "reconnectRequired", "needsAttention", "needsReprojection", "lastError", "webhookSecret", "ingressUrl", "createdAt", "updatedAt"}
var slackFields = []string{"id", "projectId", "agentId", "workspaceTeamId", "status", "connectionState", "botName", "owner", "accessPolicy", "nextAction", "createdAt", "updatedAt"}

const maxSlackReplyFileBytes = 10 * 1024 * 1024

func parseOperations(area string, args []string) (command, error) {
	if len(args) == 0 || args[0] == "--help" || args[0] == "-h" {
		return command{help: true, helpText: operationsHelp(area)}, nil
	}
	if len(args) == 2 && (args[1] == "--help" || args[1] == "-h") && area == "service" {
		return command{help: true, helpText: "USAGE\n    mo service <start|stop|restart|status|logs|uninstall> <server|runner|slack> [flags]\n\nOperate local service-manager processes; application logs are provided by mo server logs."}, nil
	}
	if area == "service" {
		return parseService(args)
	}
	if area == "notification" {
		return parseNotification(args)
	}
	if area == "event" {
		return parseEvent(args)
	}
	if area == "otel" {
		return parseOtel(args)
	}
	action := args[0]
	allowed := map[string][]string{
		"runner": {"list", "view", "status", "revoke"},
		"server": {"status", "health", "info", "logs"},
		"audit":  {"list"},
		"github": {"connect", "list", "view", "update", "enable", "disable"},
		"slack":  {"setup", "status", "install-agent", "create", "list", "view", "diagnostics", "claim-owner", "edit", "transfer-owner", "enable", "disable", "remove-binding", "permanent-delete", "deliveries", "resend-delivery", "clear-gap", "reconcile-create", "reconcile-delete", "message"},
	}
	if !contains(allowed[area], action) {
		return command{}, usage("unknown " + area + " command")
	}
	if len(args) == 2 && (args[1] == "--help" || args[1] == "-h") {
		return command{help: true, helpText: opsLeafHelp("ops-"+area+"-"+action, fieldsFor(area))}, nil
	}
	if action == "message" {
		if len(args) < 2 || args[1] != "send" {
			return command{}, usage("message action must be send")
		}
		action = "message-send"
	} else if len(args) > 1 && (args[1] == "--help" || args[1] == "-h") {
		return command{help: true, helpText: opsLeafHelp("ops-"+area+"-"+action, fieldsFor(area))}, nil
	}
	c := command{kind: "ops-" + area + "-" + action, catalog: fieldsFor(area)}
	if action == "list" || action == "status" || action == "logs" || action == "health" || action == "info" || action == "setup" {
		c.args = append(c.args, "collection", "false")
	}
	start := 1
	if action == "message-send" {
		start = 2
	}
	if area == "runner" && (action == "view" || action == "revoke") || area == "github" && contains([]string{"view", "update", "enable", "disable"}, action) || area == "slack" && contains([]string{"view", "diagnostics", "claim-owner", "edit", "transfer-owner", "enable", "disable", "remove-binding", "permanent-delete", "deliveries", "resend-delivery", "clear-gap", "reconcile-create", "reconcile-delete"}, action) {
		if len(args) <= 1 {
			return command{}, usage("resource id is required")
		}
		c.args = append(c.args, "id", args[1])
		start = 2
	}
	if area == "github" && action == "connect" {
		if len(args) <= 1 {
			return command{}, usage("owner/repo is required")
		}
		c.args = append(c.args, "repository", args[1])
		start = 2
	}
	if area == "slack" && contains([]string{"list", "install-agent", "create"}, action) && len(args) > 1 && !strings.HasPrefix(args[1], "--") {
		c.args = append(c.args, "agent", args[1])
		start = 2
	}
	for i := start; i < len(args); i++ {
		if args[i] == "--help" || args[i] == "-h" {
			return command{help: true, helpText: opsLeafHelp(c.kind, c.catalog)}, nil
		}
		if args[i] == "--json" {
			var err error
			i, err = jsonFlag(args, i, &c)
			if err != nil {
				return command{}, err
			}
			continue
		}
		if !strings.HasPrefix(args[i], "--") {
			return command{}, usage("unexpected argument " + args[i])
		}
		name := strings.TrimPrefix(args[i], "--")
		if name == "yes" || name == "follow" {
			c.args = append(c.args, name, "true")
			continue
		}
		if i+1 >= len(args) {
			return command{}, usage(args[i] + " requires a value")
		}
		c.args = append(c.args, name, args[i+1])
		if area == "slack" && contains([]string{"bot-token", "app-token", "configuration-token", "configuration-refresh-token", "token"}, name) {
			return command{}, usage("Slack credentials must be supplied through a protected credentials file")
		}
		i++
	}
	if area == "runner" && action == "list" {
		scope := argValue(c.args, "scope", "all")
		if !contains([]string{"all", "global", "project"}, scope) {
			return command{}, usage("--scope must be all, global, or project")
		}
	}
	if area == "github" && action == "connect" {
		parts := strings.Split(argValue(c.args, "repository", ""), "/")
		if len(parts) != 2 || parts[0] == "" || parts[1] == "" {
			return command{}, usage("repository must be owner/repo")
		}
	}
	if area == "slack" && action == "permanent-delete" && !hasArg(c.args, "yes") {
		return command{}, usage("--yes is required for permanent deletion")
	}
	if area == "slack" && action == "message-send" {
		missing := []string{}
		for _, required := range []string{"workspace", "conversation", "reply-to", "connection", "session", "triggering-message", "dispatch-ref"} {
			if strings.TrimSpace(argValue(c.args, required, "")) == "" {
				missing = append(missing, "--"+required)
			}
		}
		if len(missing) > 0 {
			return command{}, usage("message send requires non-blank anchor fields: " + strings.Join(missing, ", "))
		}
		if !hasArg(c.args, "text") && !hasArg(c.args, "image") && !hasArg(c.args, "file") {
			return command{}, usage("message send requires --text, --image, or --file")
		}
		if hasArg(c.args, "image") && hasArg(c.args, "file") {
			return command{}, usage("--image and --file are mutually exclusive")
		}
	}
	if area == "slack" && action == "status" && strings.TrimSpace(argValue(c.args, "workspace-team", "")) == "" {
		return command{}, usage("slack status requires non-blank --workspace-team")
	}
	return c, validateFields(c.fields, c.catalog, "mo "+area+" "+strings.ReplaceAll(action, "-", " "))
}

func fieldsFor(area string) []string {
	switch area {
	case "runner":
		return runnerFields
	case "github":
		return githubFields
	case "slack":
		return slackFields
	case "otel":
		return otelQueryFields
	case "audit":
		return auditFields
	default:
		return nil
	}
}
func operationsHelp(area string) string {
	actions := map[string]string{"runner": "list, view, status, revoke", "server": "status, health, info, logs", "audit": "list", "github": "connect, list, view, update, enable, disable", "slack": "setup, status, install-agent, list, view, claim-owner, edit, transfer-owner, enable, disable, remove-binding, permanent-delete, message, deliveries, resend-delivery, clear-gap, reconcile-create, reconcile-delete"}
	return "USAGE\n    mo " + area + " <action> [flags]\n\nOperations and integrations.\n\nActions: " + actions[area]
}
func opsLeafHelp(kind string, fields []string) string {
	return "USAGE\n    mo " + strings.Replace(strings.TrimPrefix(kind, "ops-"), "-", " ", 1) + " [flags]\n\nJSON FIELDS\n" + strings.Join(fields, "\n")
}

func parseService(args []string) (command, error) {
	if len(args) < 2 {
		return command{}, usage("service action and target are required")
	}
	action, target := args[0], strings.ToLower(args[1])
	if !contains([]string{"start", "stop", "restart", "status", "logs", "uninstall"}, action) || !contains([]string{"server", "runner", "slack"}, target) {
		return command{}, usage("service target must be server, runner, or slack")
	}
	c := command{kind: "ops-service", args: []string{"action", action, "target", target}}
	for i := 2; i < len(args); i++ {
		switch args[i] {
		case "--help", "-h":
			return command{help: true, helpText: "USAGE\n    mo service <start|stop|restart|status|logs|uninstall> <server|runner|slack> [--lines N] [--follow] [--dry-run] [--unit-dir PATH]\n\nOperate local service-manager processes; application logs are provided by mo server logs."}, nil
		case "--follow", "--dry-run":
			c.args = append(c.args, strings.TrimPrefix(args[i], "--"), "true")
		case "--lines", "--unit-dir":
			if i+1 >= len(args) {
				return command{}, usage(args[i] + " requires a value")
			}
			c.args = append(c.args, strings.TrimPrefix(args[i], "--"), args[i+1])
			i++
		default:
			return command{}, usage("unknown option " + args[i])
		}
	}
	return c, nil
}
func parseNotification(args []string) (command, error) {
	if args[0] != "setup" {
		return command{}, usage("unknown notification command")
	}
	c := command{kind: "ops-notification"}
	for i := 1; i < len(args); i++ {
		if args[i] == "--help" || args[i] == "-h" {
			return command{help: true, helpText: "USAGE\n    mo notification setup [--health-base URL] [--webhook-url URL] [--platform NAME]\n\nConfigure local Hermes notifications without contacting the Server."}, nil
		}
		if i+1 >= len(args) || !strings.HasPrefix(args[i], "--") {
			return command{}, usage("notification option requires a value")
		}
		c.args = append(c.args, strings.TrimPrefix(args[i], "--"), args[i+1])
		i++
	}
	return c, nil
}
func parseEvent(args []string) (command, error) {
	if len(args) == 0 || args[0] == "--help" || args[0] == "-h" {
		return command{help: true, helpText: "USAGE\n    mo event tail [--project REF] [--event TYPE] [--match EXPR]\n    mo event dead-letter <list|redeliver> [flags]\n\nTail events as NDJSON or recover dead-letter deliveries."}, nil
	}
	if args[0] == "dead-letter" {
		if len(args) < 2 {
			return command{}, usage("dead-letter action is required")
		}
		c := command{kind: "ops-event-dead-letter-" + args[1], catalog: []string{"id", "type", "handler", "status", "attempts", "deadLetteredAt", "error"}}
		if args[1] == "list" {
			c.args = append(c.args, "collection", "true")
		} else if args[1] == "redeliver" {
			if len(args) < 3 {
				return command{}, usage("dead-letter id is required")
			}
			c.args = append(c.args, "id", args[2])
		} else {
			return command{}, usage("unknown dead-letter command")
		}
		start := 2
		if args[1] == "redeliver" {
			start = 3
		}
		for i := start; i < len(args); i++ {
			if args[i] == "--json" {
				var e error
				i, e = jsonFlag(args, i, &c)
				if e != nil {
					return command{}, e
				}
			} else if args[i] == "--limit" || args[i] == "--handler" {
				if i+1 >= len(args) {
					return command{}, usage(args[i] + " requires a value")
				}
				c.args = append(c.args, strings.TrimPrefix(args[i], "--"), args[i+1])
				i++
			} else if args[i] != "--help" {
				return command{}, usage("unknown option " + args[i])
			}
		}
		return c, validateFields(c.fields, c.catalog, "mo event dead-letter "+args[1])
	}
	if args[0] != "tail" {
		return command{}, usage("unknown event command")
	}
	c := command{kind: "ops-event-tail", catalog: []string{"specversion", "id", "source", "type", "subject", "time", "data", "projectid", "issue", "parent", "githubrepo", "githubissue"}}
	for i := 1; i < len(args); i++ {
		if args[i] == "--project" || args[i] == "--match" || args[i] == "--event" {
			if i+1 >= len(args) {
				return command{}, usage(args[i] + " requires a value")
			}
			c.args = append(c.args, strings.TrimPrefix(args[i], "--"), args[i+1])
			i++
		} else if args[i] == "--json" {
			var e error
			i, e = jsonFlag(args, i, &c)
			if e != nil {
				return command{}, e
			}
		} else if args[i] != "--help" {
			return command{}, usage("unknown option " + args[i])
		}
	}
	return c, validateFields(c.fields, c.catalog, "mo event tail")
}
func parseOtel(args []string) (command, error) {
	if len(args) == 0 || args[0] == "--help" || args[0] == "-h" {
		return command{help: true, helpText: "USAGE\n    mo otel <status|query|traces> [flags]\n\nQuery OpenTelemetry through the Server."}, nil
	}
	action := args[0]
	if !contains([]string{"status", "query", "traces"}, action) {
		return command{}, usage("unknown otel command")
	}
	catalog := otelTraceFields
	if action == "query" {
		catalog = otelQueryFields
	}
	c := command{kind: "ops-otel-" + action, catalog: catalog}
	start := 1
	if action == "query" && len(args) > 1 && !strings.HasPrefix(args[1], "-") {
		c.args = append(c.args, "sql", args[1])
		start = 2
	}
	for i := start; i < len(args); i++ {
		if args[i] == "--json" {
			var e error
			i, e = jsonFlag(args, i, &c)
			if e != nil {
				return command{}, e
			}
		} else if args[i] == "--service" || args[i] == "--limit" {
			if i+1 >= len(args) {
				return command{}, usage(args[i] + " requires a value")
			}
			c.args = append(c.args, strings.TrimPrefix(args[i], "--"), args[i+1])
			i++
		} else if args[i] != "--help" {
			return command{}, usage("unknown option " + args[i])
		}
	}
	if action == "query" && !c.fieldsOnly && argValue(c.args, "sql", "") == "" {
		return command{}, usage("SQL query is required")
	}
	return c, validateFields(c.fields, catalog, "mo otel "+action)
}

func runOperations(ctx context.Context, deps Dependencies, c *client, cmd command) int {
	if cmd.kind == "ops-service" {
		return runLocalService(ctx, deps, cmd)
	}
	if cmd.kind == "ops-notification" {
		return runLocalNotification(ctx, deps, cmd)
	}
	if cmd.kind == "ops-event-tail" {
		p := argValue(cmd.args, "project", "")
		if p == "" {
			p, _ = resolveProject(deps, "")
		}
		if p == "" {
			writeError(deps.Stderr, errors.New("project is required; pass --project"))
			return ExitOperation
		}
		if deps.EventTail == nil {
			writeError(deps.Stderr, errors.New("event stream is unavailable [stream_unavailable]"))
			return ExitOperation
		}
		if err := deps.EventTail(ctx, p, valuesFor(cmd.args, "event"), argValue(cmd.args, "match", ""), deps.Stdout); err != nil {
			return operationExit(deps, ctx, err)
		}
		return ExitOK
	}
	if strings.HasPrefix(cmd.kind, "ops-event-dead-letter-") {
		action := strings.TrimPrefix(cmd.kind, "ops-event-dead-letter-")
		path := "/api/events/dead-letters"
		method := http.MethodGet
		if action == "redeliver" {
			path += "/" + url.PathEscape(argValue(cmd.args, "id", "")) + "/redeliver"
			method = http.MethodPost
		}
		if action == "list" {
			q := url.Values{}
			for _, n := range []string{"limit", "handler"} {
				if v := argValue(cmd.args, n, ""); v != "" {
					q.Set(n, v)
				}
			}
			if len(q) > 0 {
				path += "?" + q.Encode()
			}
		}
		return remoteOperation(ctx, deps, c, method, path, nil, cmd, action == "list")
	}
	if strings.HasPrefix(cmd.kind, "ops-otel-") {
		return runOtel(ctx, deps, c, cmd)
	}
	return runRemoteOperations(ctx, deps, c, cmd)
}

func runLocalService(ctx context.Context, deps Dependencies, cmd command) int {
	action, target := argValue(cmd.args, "action", ""), argValue(cmd.args, "target", "")
	if hasArg(cmd.args, "dry-run") {
		fmt.Fprintf(deps.Stdout, "Dry run: %s %s\n", action, target)
		return ExitOK
	}
	if err := deps.Execute(ctx, "mohist-service", []string{action, target}); err != nil {
		return operationExit(deps, ctx, err)
	}
	fmt.Fprintln(deps.Stdout, "OK")
	return ExitOK
}
func runLocalNotification(ctx context.Context, deps Dependencies, cmd command) int {
	base := argValue(cmd.args, "health-base", "http://127.0.0.1:8644")
	if err := deps.HealthProbe(ctx, base); err != nil {
		writeError(deps.Stderr, errors.New("Hermes webhook platform is not started [notification_unavailable]"))
		return ExitOperation
	}
	path := filepath.Join(func() string { h, _ := deps.HomeDir(); return h }(), ".mohist", "config.jsonc")
	if v := argValue(cmd.args, "config-file", ""); v != "" {
		path = v
	}
	old, _ := deps.ReadFile(path)
	if old == "" {
		old = "{}"
	}
	var root map[string]any
	if json.Unmarshal([]byte(old), &root) != nil {
		writeError(deps.Stderr, errors.New("Could not parse Mohist config file"))
		return ExitOperation
	}
	secret := argValue(cmd.args, "secret", "generated-secret")
	if root["Mohist"] == nil {
		root["Mohist"] = map[string]any{}
	}
	m, ok := root["Mohist"].(map[string]any)
	if !ok {
		writeError(deps.Stderr, errors.New("Mohist must be a JSON object"))
		return ExitOperation
	}
	webhookURL := strings.TrimRight(base, "/") + "/webhooks/mohist"
	if value := argValue(cmd.args, "webhook-url", ""); value != "" {
		webhookURL = value
	}
	m["Notifications"] = map[string]any{"Hermes": map[string]any{"WebhookUrl": webhookURL, "Secret": secret, "EnabledTypes": []string{"approval_requested", "workflow_failed", "issue_completed"}}}
	b, _ := json.MarshalIndent(root, "", "  ")
	if err := deps.WriteFile(path, string(b)+"\n", 0600); err != nil {
		return operationExit(deps, ctx, err)
	}
	fmt.Fprintf(deps.Stdout, "Wrote Mohist:Notifications:Hermes\nhermes webhook subscribe mohist --secret %s\n", secret)
	return ExitOK
}

func runRemoteOperations(ctx context.Context, deps Dependencies, c *client, cmd command) int {
	area := strings.Split(cmd.kind, "-")[1]
	action := strings.TrimPrefix(cmd.kind, "ops-"+area+"-")
	project := argValue(cmd.args, "project", "")
	needsProject := contains([]string{"runner", "github"}, area)
	if area == "slack" && !contains([]string{"setup", "status"}, action) {
		needsProject = !(isManagerMode(deps.Lookup) && action == "message-send")
	}
	if needsProject {
		if project == "" {
			project, _ = resolveProject(deps, "")
		}
		if project == "" {
			writeError(deps.Stderr, errors.New("project is required; pass --project"))
			return ExitOperation
		}
	}
	path := ""
	method := http.MethodGet
	var body any
	var err error
	collection := false
	if area == "server" {
		path = map[string]string{"status": "/api/status?all=true", "health": "/api/health", "info": "/api/system/info", "logs": "/api/logs/tail"}[action]
	} else if area == "audit" {
		path = "/api/audit/events"
		q := url.Values{}
		for _, n := range []string{"kind", "since", "limit"} {
			if v := argValue(cmd.args, n, ""); v != "" {
				q.Set(n, v)
			}
		}
		if len(q) > 0 {
			path += "?" + q.Encode()
		}
		collection = true
	} else if area == "runner" {
		path = "/api/projects/" + url.PathEscape(project) + "/runners"
		collection = action == "list" || action == "status"
		if action == "view" {
			path += "/" + url.PathEscape(argValue(cmd.args, "id", ""))
		}
		if action == "revoke" {
			path = "/api/runners/" + url.PathEscape(argValue(cmd.args, "id", "")) + "/credentials"
			method = http.MethodDelete
		}
	} else if area == "github" {
		path = "/api/projects/" + url.PathEscape(project) + "/github-connections"
		collection = action == "list"
		if action == "connect" {
			method = http.MethodPost
			parts := strings.Split(argValue(cmd.args, "repository", ""), "/")
			body = map[string]any{"owner": parts[0], "repo": parts[1], "approvers": valuesFor(cmd.args, "approver")}
		} else {
			if action != "list" {
				path += "/" + url.PathEscape(argValue(cmd.args, "id", ""))
			}
			if contains([]string{"enable", "disable"}, action) {
				path += "/" + action
				method = http.MethodPost
			} else if action == "update" {
				method = http.MethodPatch
				body = map[string]any{"approvers": valuesFor(cmd.args, "approver")}
			}
		}
	} else if area == "slack" {
		path = "/api/projects/" + url.PathEscape(project) + "/slack-connections"
		collection = action == "list"
		if action == "setup" || action == "status" {
			path = "/api/slack-manager/" + action
			if action == "status" {
				q := url.Values{}
				workspace := strings.TrimSpace(argValue(cmd.args, "workspace-team", ""))
				if workspace == "" {
					writeError(deps.Stderr, errors.New("--workspace-team is required for slack status"))
					return ExitUsage
				}
				q.Set("workspaceTeamId", workspace)
				path += "?" + q.Encode()
			}
		} else if action == "install-agent" || action == "create" {
			path = "/api/projects/" + url.PathEscape(project) + "/slack-manager/install-agent"
			method = http.MethodPost
			body = map[string]any{"agent": argValue(cmd.args, "agent", "")}
		} else if action == "message-send" {
			path = "/api/projects/" + url.PathEscape(project) + "/slack-connections/reply"
			method = http.MethodPost
			body, err = slackMessageBody(deps, cmd)
			if err != nil {
				writeError(deps.Stderr, err)
				if _, ok := err.(*usageError); ok {
					return ExitUsage
				}
				return ExitOperation
			}
			if isManagerMode(deps.Lookup) {
				path = "/api/slack-manager/reply"
			}
		} else if action != "list" {
			path += "/" + url.PathEscape(argValue(cmd.args, "id", ""))
		}
		if contains([]string{"enable", "disable", "claim-owner", "transfer-owner", "remove-binding", "permanent-delete", "resend-delivery", "clear-gap", "reconcile-create", "reconcile-delete"}, action) {
			path += "/" + action
			method = http.MethodPost
		}
	}
	if action == "list" {
		collection = true
	}
	return remoteOperation(ctx, deps, c, method, path, body, cmd, collection)
}

func slackMessageBody(deps Dependencies, cmd command) (map[string]any, error) {
	body := map[string]any{
		"workspaceTeamId":     strings.TrimSpace(argValue(cmd.args, "workspace", "")),
		"conversationId":      strings.TrimSpace(argValue(cmd.args, "conversation", "")),
		"threadTs":            strings.TrimSpace(argValue(cmd.args, "reply-to", "")),
		"connectionId":        strings.TrimSpace(argValue(cmd.args, "connection", "")),
		"sessionId":           strings.TrimSpace(argValue(cmd.args, "session", "")),
		"triggeringMessageId": strings.TrimSpace(argValue(cmd.args, "triggering-message", "")),
		"dispatchRef":         strings.TrimSpace(argValue(cmd.args, "dispatch-ref", "")),
	}
	if hasArg(cmd.args, "text") {
		text := argValue(cmd.args, "text", "")
		if text == "-" {
			data, err := io.ReadAll(deps.Input)
			if err != nil {
				return nil, errors.New("could not read reply text from stdin")
			}
			text = string(data)
		}
		body["text"] = text
	}
	if image := strings.TrimSpace(argValue(cmd.args, "image", "")); image != "" {
		body["imageUrl"] = image
	}
	if file := strings.TrimSpace(argValue(cmd.args, "file", "")); file != "" {
		data, err := deps.ReadFile(file)
		if err != nil {
			return nil, errors.New("could not read reply file")
		}
		if len(data) > maxSlackReplyFileBytes {
			return nil, usage("message send file must be at most 10 MB")
		}
		body["fileName"] = filepath.Base(file)
		body["fileContentBase64"] = base64.StdEncoding.EncodeToString([]byte(data))
	}
	text, _ := body["text"].(string)
	if strings.TrimSpace(text) == "" && body["imageUrl"] == nil && body["fileContentBase64"] == nil {
		return nil, usage("message send requires non-blank text, image, or file content")
	}
	return body, nil
}

func runOtel(ctx context.Context, deps Dependencies, c *client, cmd command) int {
	action := strings.TrimPrefix(cmd.kind, "ops-otel-")
	if action == "query" {
		return remoteOperation(ctx, deps, c, http.MethodPost, "/otel/api/query", map[string]any{"sql": argValue(cmd.args, "sql", "")}, cmd, false)
	}
	path := "/otel/api/" + action
	if action == "traces" {
		q := url.Values{}
		for _, n := range []string{"service", "limit"} {
			if v := argValue(cmd.args, n, ""); v != "" {
				q.Set(n, v)
			}
		}
		if len(q) > 0 {
			path += "?" + q.Encode()
		}
		return remoteOperation(ctx, deps, c, http.MethodGet, path, nil, cmd, true)
	}
	return remoteOperation(ctx, deps, c, http.MethodGet, path, nil, cmd, false)
}
func remoteOperation(ctx context.Context, deps Dependencies, c *client, method, path string, body any, cmd command, collection bool) int {
	data, err := c.request(ctx, method, path, body)
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
		v, e := SelectFields(data, cmd.fields, collection)
		if e != nil {
			writeError(deps.Stderr, e)
			return ExitOperation
		}
		return writeJSON(deps.Stdout, v)
	}
	if collection {
		var list []any
		if json.Unmarshal(data, &list) == nil && len(list) == 0 {
			fmt.Fprintln(deps.Stdout, "No results")
			return ExitOK
		}
	}
	if action := strings.TrimPrefix(cmd.kind, "ops-"); strings.HasPrefix(action, "audit-") {
		var root map[string]json.RawMessage
		if json.Unmarshal(data, &root) == nil {
			if ev := root["events"]; ev != nil {
				data = ev
			}
		}
	}
	if strings.HasPrefix(cmd.kind, "ops-slack-") || strings.HasPrefix(cmd.kind, "ops-github-") {
		data = redactIntegrationSecrets(data)
	}
	if len(data) == 0 {
		return ExitOK
	}
	_, _ = deps.Stdout.Write(append(data, '\n'))
	return ExitOK
}

func redactIntegrationSecrets(data json.RawMessage) json.RawMessage {
	var value any
	if json.Unmarshal(data, &value) != nil {
		return data
	}
	var clean func(any) any
	clean = func(input any) any {
		switch typed := input.(type) {
		case map[string]any:
			for key := range typed {
				lower := strings.ToLower(key)
				if strings.Contains(lower, "token") || strings.Contains(lower, "secret") || strings.Contains(lower, "password") {
					delete(typed, key)
					continue
				}
				typed[key] = clean(typed[key])
			}
		case []any:
			for i := range typed {
				typed[i] = clean(typed[i])
			}
		}
		return input
	}
	encoded, err := json.Marshal(clean(value))
	if err != nil {
		return data
	}
	return encoded
}
