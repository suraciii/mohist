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

var runnerFields = []string{"identity", "presence", "control", "admission", "capabilities", "runtimes", "capacity", "activeWorks", "drain", "nextActions"}
var runnerEnvironmentFields = []string{"runnerId", "activeVersion", "candidateVersion", "candidateVariables", "addedVariables", "removedVariables", "changedVariables", "application", "processGeneration", "connectionGeneration", "status"}
var auditFields = []string{"id", "subjectId", "eventType", "targetKind", "targetId", "occurredAt", "metadata"}
var otelQueryFields = []string{"columns", "rows", "truncated", "truncate_reason"}
var otelTraceFields = []string{"trace_id", "service_name", "start_time", "end_time", "span_count"}
var githubFields = []string{"id", "projectId", "owner", "repo", "repositoryName", "approvers", "status", "installationId", "repositoryNodeId", "reconnectRequired", "needsAttention", "needsReprojection", "lastError", "webhookSecret", "ingressUrl", "createdAt", "updatedAt"}
var slackFields = []string{"id", "projectId", "agentId", "workspaceTeamId", "status", "connectionState", "botName", "owner", "accessPolicy", "nextAction", "createdAt", "updatedAt"}

// slackEditFields mirrors the manage-access response envelope, not the flat
// Connection projection used by `slack list`/`slack view`.
var slackEditFields = []string{"connection", "accessPolicy", "allowMembers", "anyoneDisclosure"}

const maxSlackReplyFileBytes = 10 * 1024 * 1024

const notificationSetupUsage = "USAGE\n    mo notification setup [--health-base URL] [--webhook-url URL] [--secret VALUE] [--config-file PATH]\n\nConfigure local Hermes notifications without contacting the Server."

// Tie accepted flags to execution inputs so unsupported options cannot silently succeed.
type flagShape int

const (
	flagValue flagShape = iota
	flagBool
)

var operationsFlags = map[string]map[string]map[string]flagShape{
	"runner": {
		"list":   {},
		"view":   {},
		"status": {},
		"revoke": {},
	},
	"server": {
		"status": {},
		"health": {},
		"info":   {},
		"logs":   {},
	},
	"audit": {
		"list": {"kind": flagValue, "since": flagValue, "limit": flagValue},
	},
	"github": {
		// --repo deliberately omitted; connect reads owner/repo from the
		// positional argument only.
		"connect": {"approver": flagValue, "project": flagValue},
		"list":    {"project": flagValue},
		"view":    {"project": flagValue},
		"update":  {"approver": flagValue, "clear-approvers": flagBool, "project": flagValue},
		"enable":  {"project": flagValue},
		"disable": {"project": flagValue},
	},
	"slack": {
		"setup":            {},
		"status":           {"workspace-team": flagValue},
		"install-agent":    {"agent": flagValue, "project": flagValue},
		"create":           {"agent": flagValue, "project": flagValue},
		"list":             {"project": flagValue},
		"view":             {"project": flagValue},
		"diagnostics":      {"project": flagValue},
		"claim-owner":      {"project": flagValue},
		"edit":             {"project": flagValue, "access-policy": flagValue, "allow-member": flagValue},
		"transfer-owner":   {"project": flagValue},
		"enable":           {"project": flagValue},
		"disable":          {"project": flagValue},
		"remove-binding":   {"project": flagValue},
		"permanent-delete": {"yes": flagBool, "project": flagValue},
		"deliveries":       {"project": flagValue},
		"resend-delivery":  {"project": flagValue},
		"clear-gap":        {"project": flagValue},
		"reconcile-create": {"project": flagValue},
		"reconcile-delete": {"project": flagValue},
		"message-send": {
			"workspace":          flagValue,
			"conversation":       flagValue,
			"reply-to":           flagValue,
			"connection":         flagValue,
			"session":            flagValue,
			"triggering-message": flagValue,
			"dispatch-ref":       flagValue,
			"text":               flagValue,
			"image":              flagValue,
			"file":               flagValue,
			"project":            flagValue,
		},
	},
	"notification": {
		"setup": {
			// --platform deliberately omitted; runLocalNotification does not
			// read it and never sends it.
			"health-base": flagValue,
			"webhook-url": flagValue,
			"secret":      flagValue,
			"config-file": flagValue,
		},
	},
}

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
	if area == "runner" && len(args) > 0 && args[0] == "environment" {
		return parseRunnerEnvironment(args[1:])
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
		return command{help: true, helpText: opsLeafHelp("ops-"+area+"-"+action, catalogFor(area, action))}, nil
	}
	if action == "message" {
		if len(args) < 2 || args[1] != "send" {
			return command{}, usage("message action must be send")
		}
		action = "message-send"
	} else if len(args) > 1 && (args[1] == "--help" || args[1] == "-h") {
		return command{help: true, helpText: opsLeafHelp("ops-"+area+"-"+action, catalogFor(area, action))}, nil
	}
	c := command{kind: "ops-" + area + "-" + action, catalog: catalogFor(area, action)}
	if discovered, ok, err := discoverLeaf(args[1:], c.kind, c.catalog, opsLeafHelp(c.kind, c.catalog)); ok {
		return discovered, err
	}
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
		if area == "runner" && (args[1] == "--project" || args[1] == "--scope") {
			return command{}, usage(args[1] + " is not supported by mo runner; Runner status is global")
		}
		if isControlToken(args[1]) {
			return command{}, usage("resource id is required")
		}
		c.args = append(c.args, "id", args[1])
		start = 2
	}
	if area == "github" && action == "connect" {
		if len(args) <= 1 || isControlToken(args[1]) {
			return command{}, usage("owner/repo is required")
		}
		c.args = append(c.args, "repository", args[1])
		start = 2
	}
	if area == "slack" && contains([]string{"list", "install-agent", "create"}, action) && len(args) > 1 && !isControlToken(args[1]) {
		c.args = append(c.args, "agent", args[1])
		start = 2
	}
	leafUsage := opsLeafHelp(c.kind, c.catalog)
	leaf := operationsFlags[area][action]
	for i := start; i < len(args); i++ {
		arg := canonicalFlag(args[i])
		if arg == "--help" || arg == "-h" {
			return command{help: true, helpText: leafUsage}, nil
		}
		if arg == "--json" {
			var err error
			i, err = jsonFlag(args, i, &c)
			if err != nil {
				return command{}, err
			}
			continue
		}
		if !strings.HasPrefix(arg, "--") {
			return command{}, usageWithLeaf("unexpected argument "+args[i], leafUsage)
		}
		name := strings.TrimPrefix(arg, "--")
		if area == "slack" && contains([]string{"bot-token", "app-token", "configuration-token", "configuration-refresh-token", "token"}, name) {
			return command{}, usage("Slack credentials must be supplied through a protected credentials file")
		}
		if area == "runner" && (name == "project" || name == "scope") {
			return command{}, usageWithLeaf("unknown option "+args[i]+": --"+name+" is not supported by mo runner; Runner status is global", leafUsage)
		}
		shape, ok := leaf[name]
		if !ok {
			return command{}, usageWithLeaf("unknown option "+args[i], leafUsage)
		}
		if shape == flagBool {
			c.args = append(c.args, name, "true")
			continue
		}
		if i+1 >= len(args) {
			return command{}, usageWithLeaf(args[i]+" requires a value", leafUsage)
		}
		c.args = append(c.args, name, args[i+1])
		i++
	}
	if area == "github" && action == "update" {
		approvers := valuesFor(c.args, "approver")
		for _, approver := range approvers {
			if strings.TrimSpace(approver) == "" {
				return command{}, usageWithLeaf("--approver values must be non-blank", leafUsage)
			}
		}
		clear := hasArg(c.args, "clear-approvers")
		if clear && len(approvers) > 0 {
			return command{}, usageWithLeaf("--approver and --clear-approvers are mutually exclusive", leafUsage)
		}
		if !clear && len(approvers) == 0 {
			return command{}, usageWithLeaf("github update requires --approver or --clear-approvers", leafUsage)
		}
	}
	if area == "github" && action == "connect" {
		parts := strings.Split(argValue(c.args, "repository", ""), "/")
		if len(parts) != 2 || parts[0] == "" || parts[1] == "" {
			return command{}, usage("repository must be owner/repo")
		}
	}
	if area == "slack" && action == "edit" {
		// Selected JSON fields are part of the edit leaf contract, so reject an
		// unknown field before validating the editable payload.
		if err := validateFields(c.fields, c.catalog, "mo slack edit"); err != nil {
			return command{}, err
		}
		policy := strings.ToLower(strings.TrimSpace(argValue(c.args, "access-policy", "")))
		if policy == "" {
			return command{}, usageWithLeaf("slack edit requires --access-policy", leafUsage)
		}
		if policy != "owner_only" && policy != "allowlist" && policy != "anyone" {
			return command{}, usageWithLeaf("--access-policy must be owner_only, allowlist, or anyone", leafUsage)
		}
		members := valuesFor(c.args, "allow-member")
		for _, member := range members {
			if strings.TrimSpace(member) == "" {
				return command{}, usageWithLeaf("--allow-member values must be non-blank", leafUsage)
			}
		}
		if policy != "allowlist" && len(members) > 0 {
			return command{}, usageWithLeaf("--allow-member is only allowed with --access-policy allowlist", leafUsage)
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

func catalogFor(area, action string) []string {
	if area == "slack" && action == "edit" {
		return slackEditFields
	}
	return fieldsFor(area)
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

func parseRunnerEnvironment(args []string) (command, error) {
	usageText := "USAGE\n    mo runner environment <capture|status|apply|cancel> [flags]\n\nManage the local Runner environment transaction. Values remain on the host."
	if len(args) == 0 || args[0] == "--help" || args[0] == "-h" {
		return command{help: true, helpText: usageText}, nil
	}
	action := args[0]
	if !contains([]string{"capture", "status", "apply", "cancel"}, action) {
		return command{}, usage("unknown runner environment command")
	}
	leaf := "mo runner environment " + action
	if action == "capture" {
		leaf += " [--runner-id <runner-id>]"
	} else if action == "status" {
		leaf += " [--runner-id <runner-id>] [--json [fields]]"
	} else if action == "apply" {
		leaf += " --version <candidate-version> [--runner-id <runner-id>]"
	} else {
		leaf += " --update-id <update-id> [--runner-id <runner-id>]"
	}
	c := command{kind: "runner-environment-" + action, catalog: runnerEnvironmentFields}
	seenFlags := map[string]bool{}
	for i := 1; i < len(args); i++ {
		arg := canonicalFlag(args[i])
		switch arg {
		case "--help", "-h":
			return command{help: true, helpText: "USAGE\n    " + leaf + "\n\nValues and raw snapshot contents remain local.\n\nJSON FIELDS\n" + strings.Join(runnerEnvironmentFields, "\n")}, nil
		case "--json":
			var err error
			i, err = jsonFlag(args, i, &c)
			if err != nil {
				return command{}, err
			}
		case "--runner-id", "--version", "--update-id":
			name := strings.TrimPrefix(arg, "--")
			if seenFlags[name] {
				return command{}, usageWithLeaf(arg+" may be specified only once", "USAGE\n    "+leaf)
			}
			seenFlags[name] = true
			if i+1 >= len(args) || isControlToken(args[i+1]) {
				return command{}, usageWithLeaf(arg+" requires a value", "USAGE\n    "+leaf)
			}
			c.args = append(c.args, name, args[i+1])
			i++
		default:
			return command{}, usageWithLeaf("unknown option "+args[i], "USAGE\n    "+leaf)
		}
	}
	if action == "apply" && strings.TrimSpace(argValue(c.args, "version", "")) == "" {
		return command{}, usageWithLeaf("apply requires --version", "USAGE\n    "+leaf)
	}
	if action == "cancel" && strings.TrimSpace(argValue(c.args, "update-id", "")) == "" {
		return command{}, usageWithLeaf("cancel requires --update-id", "USAGE\n    "+leaf)
	}
	if len(c.fields) > 0 && action != "capture" && action != "status" {
		return command{}, usageWithLeaf("--json is supported for capture and status only", "USAGE\n    "+leaf)
	}
	return c, validateFields(c.fields, runnerEnvironmentFields, leaf)
}

func operationsHelp(area string) string {
	if area == "runner" {
		return "USAGE\n    mo runner <list|status|view|revoke> [flags]\n    mo runner environment <capture|status|apply|cancel> [flags]\n\nRead and manage Server-global Runner resources. Environment capture and apply are local transactions coordinated with Server.\n\nActions: list, view, status, revoke, environment"
	}
	actions := map[string]string{"server": "status, health, info, logs", "audit": "list", "github": "connect, list, view, update, enable, disable", "slack": "setup, status, install-agent, list, view, claim-owner, edit, transfer-owner, enable, disable, remove-binding, permanent-delete, message, deliveries, resend-delivery, clear-gap, reconcile-create, reconcile-delete"}
	return "USAGE\n    mo " + area + " <action> [flags]\n\nOperations and integrations.\n\nActions: " + actions[area]
}
func opsLeafHelp(kind string, fields []string) string {
	if strings.HasPrefix(kind, "ops-runner-") {
		action := strings.TrimPrefix(kind, "ops-runner-")
		if action == "revoke" {
			return "USAGE\n    mo runner revoke <runner-id>\n\nRevoke the credential for a Server-global Runner resource."
		}
		usage := "mo runner " + action + " [--json [fields]]"
		if action == "view" {
			usage = "mo runner view <runner-id> [--json [fields]]"
		}
		return "USAGE\n    " + usage + "\n\nRead Server-global Runner status.\n\nJSON FIELDS\n" + strings.Join(fields, "\n")
	}
	path := strings.TrimPrefix(kind, "ops-")
	if strings.HasPrefix(path, "event-dead-letter-") {
		path = "event dead-letter " + strings.TrimPrefix(path, "event-dead-letter-")
	} else {
		path = strings.Replace(path, "-", " ", 1)
	}
	return "USAGE\n    mo " + path + " [flags]\n\nJSON FIELDS\n" + strings.Join(fields, "\n")
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
	leafUsage := "USAGE\n    mo service " + action + " " + target + " [--dry-run]"
	if action == "logs" {
		leafUsage += " [-n, --lines N] [-f, --follow]"
	} else if action == "uninstall" {
		leafUsage += " [--unit-dir PATH]"
	}
	for i := 2; i < len(args); i++ {
		arg := canonicalFlag(args[i])
		switch arg {
		case "--help", "-h":
			return command{help: true, helpText: leafUsage + "\n\nOperate local service-manager processes; application logs are provided by mo server logs."}, nil
		case "--follow", "--dry-run":
			if arg == "--follow" && action != "logs" {
				return command{}, usageWithLeaf("unknown option "+arg, leafUsage)
			}
			c.args = append(c.args, strings.TrimPrefix(arg, "--"), "true")
		case "--lines", "--unit-dir":
			if arg == "--lines" && action != "logs" || arg == "--unit-dir" && action != "uninstall" {
				return command{}, usageWithLeaf("unknown option "+arg, leafUsage)
			}
			if i+1 >= len(args) {
				return command{}, usageWithLeaf(arg+" requires a value", leafUsage)
			}
			c.args = append(c.args, strings.TrimPrefix(arg, "--"), args[i+1])
			i++
		default:
			return command{}, usageWithLeaf("unknown option "+arg, leafUsage)
		}
	}
	return c, nil
}
func parseNotification(args []string) (command, error) {
	if args[0] != "setup" {
		return command{}, usage("unknown notification command")
	}
	c := command{kind: "ops-notification"}
	leaf := operationsFlags["notification"]["setup"]
	for i := 1; i < len(args); i++ {
		if args[i] == "--help" || args[i] == "-h" {
			return command{help: true, helpText: notificationSetupUsage}, nil
		}
		if !strings.HasPrefix(args[i], "--") {
			return command{}, usageWithLeaf("unexpected argument "+args[i], notificationSetupUsage)
		}
		name := strings.TrimPrefix(args[i], "--")
		shape, ok := leaf[name]
		if !ok {
			return command{}, usageWithLeaf("unknown option "+args[i], notificationSetupUsage)
		}
		if shape == flagBool {
			c.args = append(c.args, name, "true")
			continue
		}
		if i+1 >= len(args) {
			return command{}, usageWithLeaf(args[i]+" requires a value", notificationSetupUsage)
		}
		c.args = append(c.args, name, args[i+1])
		i++
	}
	return c, nil
}
func parseEvent(args []string) (command, error) {
	if len(args) == 0 || args[0] == "--help" || args[0] == "-h" {
		return command{help: true, helpText: "USAGE\n    mo event tail [--project REF] [--event TYPE] [--match EXPR]\n    mo event dead-letter <list|redeliver> [flags]\n\nTail events as NDJSON or recover dead-letter deliveries."}, nil
	}
	if args[0] == "dead-letter" {
		if len(args) >= 2 && (args[1] == "--help" || args[1] == "-h") {
			return command{help: true, helpText: "USAGE\n    mo event dead-letter <list|redeliver> [flags]"}, nil
		}
		if len(args) < 2 {
			return command{}, usage("dead-letter action is required")
		}
		if args[1] != "list" && args[1] != "redeliver" {
			return command{}, usage("unknown dead-letter command")
		}
		c := command{kind: "ops-event-dead-letter-" + args[1], catalog: []string{"id", "type", "handler", "status", "attempts", "deadLetteredAt", "error"}}
		if discovered, ok, err := discoverLeaf(args[2:], c.kind, c.catalog, opsLeafHelp(c.kind, c.catalog)); ok {
			return discovered, err
		}
		if args[1] == "list" {
			c.args = append(c.args, "collection", "true")
		} else if args[1] == "redeliver" {
			if len(args) < 3 || isControlToken(args[2]) {
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
			} else if args[1] == "list" && (args[i] == "--limit" || args[i] == "--handler") {
				if i+1 >= len(args) {
					return command{}, usage(args[i] + " requires a value")
				}
				c.args = append(c.args, strings.TrimPrefix(args[i], "--"), args[i+1])
				i++
			} else if args[i] == "--help" || args[i] == "-h" {
				return command{help: true, helpText: opsLeafHelp(c.kind, c.catalog)}, nil
			} else {
				return command{}, usageWithLeaf("unknown option "+args[i], opsLeafHelp(c.kind, c.catalog))
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
		} else if args[i] == "--help" || args[i] == "-h" {
			return command{help: true, helpText: opsLeafHelp(c.kind, c.catalog)}, nil
		} else {
			return command{}, usageWithLeaf("unknown option "+args[i], opsLeafHelp(c.kind, c.catalog))
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
	if discovered, ok, err := discoverLeaf(args[1:], c.kind, c.catalog, opsLeafHelp(c.kind, c.catalog)); ok {
		return discovered, err
	}
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
		} else if action == "traces" && (args[i] == "--service" || args[i] == "--limit") {
			if i+1 >= len(args) {
				return command{}, usage(args[i] + " requires a value")
			}
			c.args = append(c.args, strings.TrimPrefix(args[i], "--"), args[i+1])
			i++
		} else if args[i] == "--help" || args[i] == "-h" {
			return command{help: true, helpText: opsLeafHelp(c.kind, c.catalog)}, nil
		} else {
			return command{}, usageWithLeaf("unknown option "+args[i], opsLeafHelp(c.kind, c.catalog))
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
	units := map[string]string{"server": "mohist.service", "runner": "mohist-runner.service", "slack": "mohist-slack.service"}
	unit := units[target]
	if action == "status" {
		output, err := deps.ExecuteOutput(ctx, "systemctl", []string{"--user", "show", "--no-pager", "--property=Id,ActiveState,SubState,Result,ExecMainStatus", unit})
		if err != nil {
			if output != "" {
				fmt.Fprint(deps.Stderr, output)
			}
			return operationExit(deps, ctx, err)
		}
		fmt.Fprint(deps.Stdout, output)
		return ExitOK
	}
	if action == "logs" {
		args := []string{"--user", "-u", unit, "--no-pager"}
		if lines := argValue(cmd.args, "lines", ""); lines != "" {
			args = append(args, "-n", lines)
		}
		if hasArg(cmd.args, "follow") {
			args = append(args, "-f")
		}
		output, err := deps.ExecuteOutput(ctx, "journalctl", args)
		if err != nil {
			if output != "" {
				fmt.Fprint(deps.Stderr, output)
			}
			return operationExit(deps, ctx, err)
		}
		fmt.Fprint(deps.Stdout, output)
		return ExitOK
	}
	if action == "uninstall" {
		if err := deps.Execute(ctx, "systemctl", []string{"--user", "stop", unit}); err != nil {
			return operationExit(deps, ctx, err)
		}
		if err := deps.Execute(ctx, "systemctl", []string{"--user", "disable", unit}); err != nil {
			return operationExit(deps, ctx, err)
		}
		home, err := deps.HomeDir()
		if err != nil {
			return operationExit(deps, ctx, err)
		}
		unitDir := argValue(cmd.args, "unit-dir", "")
		if unitDir == "" {
			unitDir = filepath.Join(home, ".config", "systemd", "user")
		}
		paths := []string{filepath.Join(unitDir, unit)}
		if target == "runner" {
			paths = append(paths,
				filepath.Join(home, ".config", "mohist", "runner.env"),
				filepath.Join(home, ".config", "mohist", "runner-managed.env"))
		}
		for _, path := range paths {
			if err := deps.RemoveAll(path); err != nil {
				return operationExit(deps, ctx, fmt.Errorf("remove managed service file %s: %w", path, err))
			}
		}
		if err := deps.Execute(ctx, "systemctl", []string{"--user", "daemon-reload"}); err != nil {
			return operationExit(deps, ctx, err)
		}
		fmt.Fprintln(deps.Stdout, "OK")
		return ExitOK
	}
	if err := deps.Execute(ctx, "systemctl", []string{"--user", action, unit}); err != nil {
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
	if area == "runner" {
		return runRemoteRunnerOperations(ctx, deps, c, action, cmd)
	}
	project := argValue(cmd.args, "project", "")
	needsProject := area == "github"
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
				if hasArg(cmd.args, "clear-approvers") {
					body = map[string]any{"approvers": []string{}}
				} else {
					body = map[string]any{"approvers": valuesFor(cmd.args, "approver")}
				}
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
		} else if action == "edit" {
			path += "/" + url.PathEscape(argValue(cmd.args, "id", "")) + "/manage-access"
			method = http.MethodPost
			body = slackEditBody(cmd)
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

type runnerInventoryResponse struct {
	State       string                     `json:"state"`
	NextActions []runnerNextActionResponse `json:"nextActions"`
}

type runnerNextActionResponse struct {
	Code    string  `json:"code"`
	Message string  `json:"message"`
	Command *string `json:"command"`
}

type runnerListResponse struct {
	ObservedAt string                       `json:"observedAt"`
	Inventory  runnerInventoryResponse      `json:"inventory"`
	Runners    []map[string]json.RawMessage `json:"runners"`
}

type runnerDetailResponse struct {
	ObservedAt string                     `json:"observedAt"`
	Runner     map[string]json.RawMessage `json:"runner"`
}

func runRemoteRunnerOperations(ctx context.Context, deps Dependencies, c *client, action string, cmd command) int {
	path := "/api/runners"
	method := http.MethodGet
	if action == "view" {
		path += "/" + url.PathEscape(argValue(cmd.args, "id", ""))
	} else if action == "revoke" {
		path += "/" + url.PathEscape(argValue(cmd.args, "id", "")) + "/credentials"
		method = http.MethodDelete
	}

	if action == "revoke" {
		return remoteOperation(ctx, deps, c, method, path, nil, cmd, false)
	}

	if cmd.fieldsOnly {
		for _, field := range cmd.catalog {
			fmt.Fprintln(deps.Stdout, field)
		}
		return ExitOK
	}

	data, err := c.request(ctx, method, path, nil)
	if err != nil {
		return operationExit(deps, ctx, err)
	}
	if len(cmd.fields) > 0 {
		var selected json.RawMessage
		if action == "view" {
			response, decodeErr := decodeRunnerDetailResponse(data)
			if decodeErr != nil {
				return operationExit(deps, ctx, decodeErr)
			}
			row, marshalErr := json.Marshal(response.Runner)
			if marshalErr != nil {
				return operationExit(deps, ctx, runnerResponseError())
			}
			selected, err = SelectFields(row, cmd.fields, false)
		} else {
			response, decodeErr := decodeRunnerListResponse(data)
			if decodeErr != nil {
				return operationExit(deps, ctx, decodeErr)
			}
			rows, marshalErr := json.Marshal(response.Runners)
			if marshalErr != nil {
				return operationExit(deps, ctx, runnerResponseError())
			}
			selected, err = SelectFields(rows, cmd.fields, true)
		}
		if err != nil {
			writeError(deps.Stderr, err)
			return ExitOperation
		}
		return writeJSON(deps.Stdout, selected)
	}

	if err := renderRunnerResponse(deps.Stdout, data, action); err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	return ExitOK
}

func decodeRunnerListResponse(data json.RawMessage) (runnerListResponse, error) {
	var response runnerListResponse
	if json.Unmarshal(data, &response) != nil || response.ObservedAt == "" || response.Inventory.State == "" || response.Inventory.NextActions == nil || response.Runners == nil {
		return runnerListResponse{}, runnerResponseError()
	}
	return response, nil
}

func decodeRunnerDetailResponse(data json.RawMessage) (runnerDetailResponse, error) {
	var response runnerDetailResponse
	if json.Unmarshal(data, &response) != nil || response.ObservedAt == "" || response.Runner == nil {
		return runnerDetailResponse{}, runnerResponseError()
	}
	return response, nil
}

func runnerResponseError() error {
	return errors.New("error: Runner response has an invalid shape [invalid_response]")
}

func renderRunnerResponse(out io.Writer, data json.RawMessage, action string) error {
	if action == "view" {
		response, err := decodeRunnerDetailResponse(data)
		if err != nil {
			return err
		}
		row, err := runnerRowValues(response.Runner)
		if err != nil {
			return err
		}
		fmt.Fprintln(out, "observed at: "+response.ObservedAt)
		return renderRunnerRow(out, row)
	}

	response, err := decodeRunnerListResponse(data)
	if err != nil {
		return err
	}
	if len(response.Runners) == 0 {
		if response.Inventory.State != "first-install" {
			return runnerResponseError()
		}
		for _, action := range response.Inventory.NextActions {
			if action.Code == "install-runner" {
				return renderRunnerActions(out, []runnerNextActionResponse{action})
			}
		}
		return runnerResponseError()
	}
	fmt.Fprintln(out, "observed at: "+response.ObservedAt)
	for index, rawRow := range response.Runners {
		if index > 0 {
			fmt.Fprintln(out)
		}
		row, rowErr := runnerRowValues(rawRow)
		if rowErr != nil {
			return rowErr
		}
		if rowErr := renderRunnerRow(out, row); rowErr != nil {
			return rowErr
		}
	}
	return nil
}

func runnerRowValues(raw map[string]json.RawMessage) (map[string]any, error) {
	data, err := json.Marshal(raw)
	if err != nil {
		return nil, runnerResponseError()
	}
	var row map[string]any
	if json.Unmarshal(data, &row) != nil || row == nil {
		return nil, runnerResponseError()
	}
	return row, nil
}

func renderRunnerActions(out io.Writer, actions []runnerNextActionResponse) error {
	for _, action := range actions {
		if action.Code == "" && action.Message == "" {
			return runnerResponseError()
		}
		if action.Code == "" {
			fmt.Fprintln(out, "next action: "+action.Message)
		} else if action.Message == "" {
			fmt.Fprintln(out, "next action: "+action.Code)
		} else {
			fmt.Fprintf(out, "next action: %s - %s\n", action.Code, action.Message)
		}
		if action.Command != nil && strings.TrimSpace(*action.Command) != "" {
			fmt.Fprintln(out, "command: "+*action.Command)
		}
	}
	return nil
}

func renderRunnerRow(out io.Writer, row map[string]any) error {
	identity := runnerObject(row, "identity")
	if identity == nil || runnerString(identity, "id") == "" {
		return runnerResponseError()
	}
	fmt.Fprintln(out, "runner: "+runnerString(identity, "id"))
	fmt.Fprintln(out, "identity:")
	printFields(out, identity, 1, []string{"hostname", "kind", "component", "sourceRevision", "releaseId", "generation"})

	presence := runnerObject(row, "presence")
	fmt.Fprintln(out, "presence: "+runnerString(presence, "state"))
	if value, ok := runnerValue(presence, "lastObservedAt"); ok && value != nil {
		fmt.Fprintln(out, "  lastObservedAt: "+display(value))
	}

	control := runnerObject(row, "control")
	fmt.Fprintln(out, "control: "+runnerString(control, "state"))
	if value, ok := runnerValue(control, "generation"); ok && value != nil {
		fmt.Fprintln(out, "  generation: "+display(value))
	}

	admission := runnerObject(row, "admission")
	fmt.Fprintln(out, "admission: "+runnerString(admission, "state"))
	if value, ok := runnerValue(admission, "reasonCodes"); ok {
		if reasons, isList := value.([]any); isList && len(reasons) == 0 {
			fmt.Fprintln(out, "  reasonCodes: none")
		} else {
			fmt.Fprintln(out, "  reasonCodes: "+display(value))
		}
	}

	if value, ok := runnerValue(row, "capabilities"); ok {
		fmt.Fprintln(out, "capabilities: "+display(value))
	}
	renderRunnerRuntimes(out, row["runtimes"])
	renderRunnerCapacity(out, row["capacity"])
	renderRunnerActiveWorks(out, row["activeWorks"])
	renderRunnerDrain(out, row["drain"])
	renderRunnerNextActions(out, row["nextActions"])
	return nil
}

func renderRunnerRuntimes(out io.Writer, value any) {
	runtimes, ok := value.([]any)
	if !ok {
		return
	}
	fmt.Fprintln(out, "runtimes:")
	if len(runtimes) == 0 {
		fmt.Fprintln(out, "  none")
		return
	}
	for _, value := range runtimes {
		runtime, ok := value.(map[string]any)
		if !ok {
			continue
		}
		name := runnerString(runtime, "name")
		if name == "" {
			name = "unknown"
		}
		fmt.Fprintln(out, "  "+name+":")
		readiness := runnerObject(runtime, "readiness")
		fmt.Fprintln(out, "    readiness: "+runnerString(readiness, "state"))
		printFields(out, readiness, 2, []string{"generation", "reasonCode"})
		catalog := runnerObject(runtime, "catalog")
		if catalog == nil {
			fmt.Fprintln(out, "    catalog: unavailable")
			continue
		}
		fmt.Fprintln(out, "    catalog:")
		printFields(out, catalog, 3, []string{"complete", "capabilityRevision", "modelCount", "models", "variants", "supportsReasoningEffort", "reasoningEfforts"})
	}
}

func renderRunnerCapacity(out io.Writer, value any) {
	capacity, ok := value.(map[string]any)
	if !ok {
		return
	}
	fmt.Fprintln(out, "capacity:")
	if used, present := capacity["used"]; present {
		if used == nil {
			fmt.Fprintln(out, "  used: unknown")
		} else {
			fmt.Fprintln(out, "  used: "+display(used))
		}
	}
	if total, present := capacity["total"]; present {
		fmt.Fprintln(out, "  total: "+display(total))
	}
}

func renderRunnerActiveWorks(out io.Writer, value any) {
	works, ok := value.([]any)
	if !ok {
		return
	}
	fmt.Fprintln(out, "active works:")
	if len(works) == 0 {
		fmt.Fprintln(out, "  none")
		return
	}
	for _, value := range works {
		work, ok := value.(map[string]any)
		if !ok {
			continue
		}
		kind := runnerString(work, "ownerKind")
		if kind == "" {
			kind = "unknown"
		}
		ownerID := runnerString(work, "ownerId")
		workID := runnerString(work, "workId")
		fmt.Fprintf(out, "  %s owner: %s (workId: %s)\n", kind, ownerID, workID)
		printFields(out, work, 2, []string{"workType", "stage", "title", "issue"})
	}
}

func renderRunnerDrain(out io.Writer, value any) {
	if value == nil {
		fmt.Fprintln(out, "drain: none")
		return
	}
	drain, ok := value.(map[string]any)
	if !ok {
		return
	}
	fmt.Fprintln(out, "drain: active")
	printFields(out, drain, 1, []string{"kind", "updateInterruptId"})
}

func renderRunnerNextActions(out io.Writer, value any) {
	actions, ok := value.([]any)
	if !ok {
		return
	}
	fmt.Fprintln(out, "next actions:")
	if len(actions) == 0 {
		fmt.Fprintln(out, "  none")
		return
	}
	for _, value := range actions {
		action, ok := value.(map[string]any)
		if !ok {
			continue
		}
		code := runnerString(action, "code")
		message := runnerString(action, "message")
		if code == "" {
			fmt.Fprintln(out, "  "+message)
		} else {
			fmt.Fprintf(out, "  %s: %s\n", code, message)
		}
		if command := runnerString(action, "command"); command != "" {
			fmt.Fprintln(out, "    command: "+command)
		}
	}
}

func runnerObject(value any, key string) map[string]any {
	object, _ := runnerValue(value, key)
	result, _ := object.(map[string]any)
	return result
}

func runnerValue(value any, key string) (any, bool) {
	object, ok := value.(map[string]any)
	if !ok {
		return nil, false
	}
	result, present := object[key]
	return result, present
}

func runnerString(value map[string]any, key string) string {
	result, _ := runnerValue(value, key)
	text, _ := result.(string)
	return text
}

func slackEditBody(cmd command) map[string]any {
	members := []string{}
	seen := map[string]bool{}
	for _, value := range valuesFor(cmd.args, "allow-member") {
		member := strings.TrimSpace(value)
		if seen[member] {
			continue
		}
		seen[member] = true
		members = append(members, member)
	}
	return map[string]any{
		"accessPolicy": strings.ToLower(strings.TrimSpace(argValue(cmd.args, "access-policy", ""))),
		"allowMembers": members,
	}
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
