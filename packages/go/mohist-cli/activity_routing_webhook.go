package mohistcli

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/url"
	"strconv"
	"strings"
)

var activityFields = []string{"id", "provenance", "scope", "kind", "time", "title", "description", "eventType", "issueNumber", "workflowRunId", "sessionId", "runnerId", "status"}
var routingRuleFields = []string{"id", "projectId", "name", "position", "match", "agentId", "responsePrompt", "continue", "status", "createdAt", "updatedAt"}
var webhookSubscriptionFields = []string{"id", "projectId", "name", "match", "targetUrl", "status", "eventSelectionMode", "eventTypes", "authType", "hasSecret", "createdAt", "updatedAt"}
var webhookFailureFields = []string{"id", "projectId", "subscriptionId", "eventId", "eventType", "targetUrl", "responseStatus", "durationMs", "errorSummary", "occurredAt"}

func parseActivity(args []string) (command, error) {
	if len(args) == 0 || args[0] == "--help" || args[0] == "-h" {
		return command{help: true, helpText: "USAGE\n    mo activity <list> [flags]\n\nInspect bounded Project activity evidence.\n\nActions: list"}, nil
	}
	if args[0] != "list" {
		return command{}, usage("unknown activity command")
	}
	c := command{kind: "activity-list", catalog: activityFields, args: []string{"project-required", "true", "collection", "true"}}
	for i := 1; i < len(args); i++ {
		switch args[i] {
		case "--project", "--limit":
			if i+1 >= len(args) {
				return command{}, usage(args[i] + " requires a value")
			}
			c.args = append(c.args, strings.TrimPrefix(args[i], "--"), args[i+1])
			i++
		case "--json":
			var err error
			i, err = jsonFlag(args, i, &c)
			if err != nil {
				return command{}, err
			}
		case "--help", "-h":
			return command{help: true, helpText: "USAGE\n    mo activity list [--project <ref>] [--limit <1-200>] [--json [fields]]\n\nList bounded recorded and snapshot activity evidence.\n\nJSON FIELDS\n" + strings.Join(activityFields, "\n")}, nil
		default:
			return command{}, usage("unknown option " + args[i])
		}
	}
	if !c.fieldsOnly {
		if n, err := strconv.Atoi(argValue(c.args, "limit", "100")); err != nil || n < 1 || n > 200 {
			return command{}, usage("--limit must be between 1 and 200")
		}
	}
	return c, validateFields(c.fields, activityFields, "mo activity list")
}

func parseRouting(args []string) (command, error) {
	if len(args) == 0 || args[0] == "--help" || args[0] == "-h" {
		return command{help: true, helpText: "USAGE\n    mo routing <rule|test> [flags]\n\nManage ordered Project routing rules and evaluate the complete routing table.\n\nActions: rule, test"}, nil
	}
	if args[0] == "test" {
		return parseRoutingTest(args[1:])
	}
	if args[0] != "rule" {
		return command{}, usage("unknown routing command")
	}
	if len(args) < 2 || args[1] == "--help" || args[1] == "-h" {
		return command{help: true, helpText: "USAGE\n    mo routing rule <list|view|create|edit|archive|move> [flags]\n\nManage ordered routing rules."}, nil
	}
	action := args[1]
	if !contains([]string{"list", "view", "create", "edit", "archive", "move"}, action) {
		return command{}, usage("unknown routing rule command")
	}
	c := command{kind: "routing-rule-" + action, catalog: routingRuleFields, args: []string{"project-required", "true"}}
	if action == "list" {
		c.args = append(c.args, "collection", "true")
	}
	start := 2
	if action == "view" || action == "edit" || action == "archive" || action == "move" {
		if len(args) <= start {
			return command{}, usage("rule id or name is required")
		}
		c.args = append(c.args, "rule", args[start])
		start++
	}
	for i := start; i < len(args); i++ {
		switch args[i] {
		case "--project", "--name", "--match", "--agent", "--response-prompt", "--before", "--after", "--continue":
			if i+1 >= len(args) {
				if args[i] == "--continue" {
					c.args = append(c.args, "continue", "true")
					continue
				}
				return command{}, usage(args[i] + " requires a value")
			}
			if args[i] == "--continue" && strings.HasPrefix(args[i+1], "-") {
				c.args = append(c.args, "continue", "true")
				continue
			}
			c.args = append(c.args, strings.TrimPrefix(args[i], "--"), args[i+1])
			i++
		case "--json":
			var err error
			i, err = jsonFlag(args, i, &c)
			if err != nil {
				return command{}, err
			}
		case "--help", "-h":
			return command{help: true, helpText: leafHelp("routing-rule-"+action, routingRuleFields)}, nil
		default:
			return command{}, usage("unknown option " + args[i])
		}
	}
	if action == "create" && (!hasArg(c.args, "name") || !hasArg(c.args, "match") || !hasArg(c.args, "agent") || !hasArg(c.args, "response-prompt")) {
		return command{}, usage("create requires --name, --match, --agent, and --response-prompt")
	}
	if action == "edit" && !hasAnyArg(c.args, "name", "match", "agent", "response-prompt", "continue") {
		return command{}, usage("at least one update option is required")
	}
	if hasArg(c.args, "before") && hasArg(c.args, "after") {
		return command{}, usage("--before and --after are mutually exclusive")
	}
	return c, validateFields(c.fields, routingRuleFields, "mo routing rule "+action)
}

func parseRoutingTest(args []string) (command, error) {
	c := command{kind: "routing-test", catalog: routingRuleFields, args: []string{"project-required", "true"}}
	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--project", "--limit":
			if i+1 >= len(args) {
				return command{}, usage(args[i] + " requires a value")
			}
			c.args = append(c.args, strings.TrimPrefix(args[i], "--"), args[i+1])
			i++
		case "--json":
			var err error
			i, err = jsonFlag(args, i, &c)
			if err != nil {
				return command{}, err
			}
		case "--help", "-h":
			return command{help: true, helpText: leafHelp("routing-test", routingRuleFields)}, nil
		default:
			return command{}, usage("unknown option " + args[i])
		}
	}
	if limit := argValue(c.args, "limit", ""); limit != "" {
		if n, err := strconv.Atoi(limit); err != nil || n < 1 {
			return command{}, usage("--limit must be a positive integer")
		}
	}
	return c, validateFields(c.fields, routingRuleFields, "mo routing test")
}

func parseWebhook(args []string) (command, error) {
	if len(args) == 0 || args[0] == "--help" || args[0] == "-h" {
		return command{help: true, helpText: "USAGE\n    mo webhook <event-types|subscription> [flags]\n\nManage Project outbound webhook subscriptions."}, nil
	}
	if args[0] == "event-types" {
		return parseWebhookEventTypes(args[1:])
	}
	if args[0] != "subscription" {
		return command{}, usage("unknown webhook command")
	}
	if len(args) < 2 || args[1] == "--help" || args[1] == "-h" {
		return command{help: true, helpText: "USAGE\n    mo webhook subscription <list|view|create|edit|enable|disable|delete|rotate-secret|failures> [flags]"}, nil
	}
	action := args[1]
	if !contains([]string{"list", "view", "create", "edit", "enable", "disable", "delete", "rotate-secret", "failures"}, action) {
		return command{}, usage("unknown webhook subscription command")
	}
	catalog := webhookSubscriptionFields
	if action == "failures" {
		catalog = webhookFailureFields
	}
	c := command{kind: "webhook-subscription-" + action, catalog: catalog, args: []string{"project-required", "true"}}
	start := 2
	if action == "view" || action == "edit" || action == "enable" || action == "disable" || action == "delete" || action == "rotate-secret" {
		if len(args) <= start {
			return command{}, usage("subscription id is required")
		}
		c.args = append(c.args, "subscription", args[start])
		start++
	}
	if action == "create" {
		if len(args) <= start {
			return command{}, usage("subscription name is required")
		}
		c.args = append(c.args, "name", args[start])
		start++
	}
	for i := start; i < len(args); i++ {
		switch args[i] {
		case "--project", "--target-url", "--match", "--secret", "--name", "--event", "--auth-type", "--auth-token", "--auth-user", "--auth-password", "--auth-header", "--subscription-id":
			if i+1 >= len(args) {
				return command{}, usage(args[i] + " requires a value")
			}
			c.args = append(c.args, strings.TrimPrefix(args[i], "--"), args[i+1])
			i++
		case "--all":
			c.args = append(c.args, "all", "true")
		case "--yes":
			c.args = append(c.args, "yes", "true")
		case "--json":
			var err error
			i, err = jsonFlag(args, i, &c)
			if err != nil {
				return command{}, err
			}
		case "--help", "-h":
			return command{help: true, helpText: leafHelp("webhook-subscription-"+action, catalog)}, nil
		default:
			return command{}, usage("unknown option " + args[i])
		}
	}
	if action == "create" && !hasArg(c.args, "target-url") {
		return command{}, usage("create requires --target-url")
	}
	if action == "edit" && !hasAnyArg(c.args, "name", "target-url", "event", "match", "auth-type", "auth-token", "auth-user", "auth-password", "auth-header") {
		return command{}, usage("at least one update option is required")
	}
	if action == "rotate-secret" && strings.TrimSpace(argValue(c.args, "secret", "")) == "" {
		return command{}, usage("--secret is required")
	}
	if action == "delete" && !hasArg(c.args, "yes") {
		return command{}, usage("--yes is required to delete a webhook subscription")
	}
	return c, validateFields(c.fields, catalog, "mo webhook subscription "+action)
}

func parseWebhookEventTypes(args []string) (command, error) {
	c := command{kind: "webhook-event-types", args: []string{"project-required", "true"}}
	for i := 0; i < len(args); i++ {
		if args[i] == "--project" {
			if i+1 >= len(args) {
				return command{}, usage("--project requires a value")
			}
			c.args = append(c.args, "project", args[i+1])
			i++
		} else if args[i] == "--help" || args[i] == "-h" {
			return command{help: true, helpText: "USAGE\n    mo webhook event-types [--project <ref>]\n\nList event types available for webhook subscriptions."}, nil
		} else {
			return command{}, usage("unknown option " + args[i])
		}
	}
	return c, nil
}

func hasAnyArg(args []string, names ...string) bool {
	for _, name := range names {
		if hasArg(args, name) {
			return true
		}
	}
	return false
}

func runActivityRoutingWebhook(ctx context.Context, deps Dependencies, c *client, cmd command) int {
	project, ok := resolveProject(deps, argValue(cmd.args, "project", ""))
	if !ok {
		writeError(deps.Stderr, errors.New("Run 'mo project use <name-or-id>' or pass --project <name-or-id>"))
		return ExitOperation
	}
	base := "/api/projects/" + url.PathEscape(project)
	switch cmd.kind {
	case "activity-list":
		return secureResourceRequest(ctx, deps, c, http.MethodGet, base+"/activity?limit="+argValue(cmd.args, "limit", "100"), nil, cmd, true)
	case "routing-rule-list":
		return resourceRequest(ctx, deps, c, http.MethodGet, base+"/routing/rules", nil, cmd, true)
	case "routing-rule-view":
		return resourceRequest(ctx, deps, c, http.MethodGet, base+"/routing/rules/"+url.PathEscape(argValue(cmd.args, "rule", "")), nil, cmd, false)
	case "routing-rule-create":
		id, code := resolveAgent(ctx, deps, c, project, argValue(cmd.args, "agent", ""))
		if code != ExitOK {
			return code
		}
		path := base + "/routing/rules" + positionQuery(cmd.args)
		body := map[string]any{"name": argValue(cmd.args, "name", ""), "match": argValue(cmd.args, "match", ""), "agentId": id, "responsePrompt": argValue(cmd.args, "response-prompt", ""), "continue": boolArg(cmd.args, "continue")}
		return resourceRequest(ctx, deps, c, http.MethodPost, path, body, cmd, false)
	case "routing-rule-edit":
		body := map[string]any{}
		for _, pair := range [][2]string{{"name", "name"}, {"match", "match"}, {"response-prompt", "responsePrompt"}} {
			if hasArg(cmd.args, pair[0]) {
				body[pair[1]] = argValue(cmd.args, pair[0], "")
			}
		}
		if hasArg(cmd.args, "continue") {
			body["continue"] = boolArg(cmd.args, "continue")
		}
		if hasArg(cmd.args, "agent") {
			id, code := resolveAgent(ctx, deps, c, project, argValue(cmd.args, "agent", ""))
			if code != ExitOK {
				return code
			}
			body["agentId"] = id
		}
		return resourceRequest(ctx, deps, c, http.MethodPatch, base+"/routing/rules/"+url.PathEscape(argValue(cmd.args, "rule", "")), body, cmd, false)
	case "routing-rule-archive":
		return resourceRequest(ctx, deps, c, http.MethodPost, base+"/routing/rules/"+url.PathEscape(argValue(cmd.args, "rule", ""))+"/archive", map[string]any{}, cmd, false)
	case "routing-rule-move":
		body := map[string]any{"before": nullableArg(cmd.args, "before"), "after": nullableArg(cmd.args, "after")}
		return resourceRequest(ctx, deps, c, http.MethodPost, base+"/routing/rules/"+url.PathEscape(argValue(cmd.args, "rule", ""))+"/move", body, cmd, false)
	case "routing-test":
		path := base + "/routing/test"
		if limit := argValue(cmd.args, "limit", ""); limit != "" {
			path += "?limit=" + url.QueryEscape(limit)
		}
		return resourceRequest(ctx, deps, c, http.MethodGet, path, nil, cmd, false)
	case "webhook-event-types":
		return resourceRequest(ctx, deps, c, http.MethodGet, base+"/webhook/event-types", nil, cmd, false)
	}
	return runWebhookSubscription(ctx, deps, c, base, cmd)
}

func runWebhookSubscription(ctx context.Context, deps Dependencies, c *client, base string, cmd command) int {
	action := strings.TrimPrefix(cmd.kind, "webhook-subscription-")
	path := base + "/webhook/subscriptions"
	method := http.MethodGet
	var body any
	collection := false
	if action == "list" {
		collection = true
		if hasArg(cmd.args, "all") {
			path += "?all=true"
		}
	} else if action == "failures" {
		if id := argValue(cmd.args, "subscription-id", ""); id != "" {
			path += "/" + url.PathEscape(id)
		}
		path += "/failures"
		collection = true
	} else {
		if action != "create" {
			path += "/" + url.PathEscape(argValue(cmd.args, "subscription", ""))
		}
		switch action {
		case "create":
			method, body = http.MethodPost, webhookCreateBody(cmd)
		case "edit":
			method, body = http.MethodPatch, webhookEditBody(cmd)
		case "enable", "disable", "delete":
			method = http.MethodPost
			suffix := action
			if action == "delete" {
				suffix = "archive"
			}
			path += "/" + suffix
			body = map[string]any{}
		case "rotate-secret":
			method, body = http.MethodPost, map[string]any{"secret": argValue(cmd.args, "secret", "")}
		}
	}
	return secureResourceRequest(ctx, deps, c, method, path, body, cmd, collection)
}

func webhookCreateBody(cmd command) map[string]any {
	body := map[string]any{"name": argValue(cmd.args, "name", ""), "targetUrl": argValue(cmd.args, "target-url", "")}
	if hasArg(cmd.args, "match") {
		body["match"] = argValue(cmd.args, "match", "")
	}
	events := valuesFor(cmd.args, "event")
	if len(events) > 0 {
		body["eventSelectionMode"], body["eventTypes"] = "selected", events
	} else {
		body["eventSelectionMode"], body["eventTypes"] = "all", []string{}
	}
	addWebhookAuth(body, cmd)
	if hasArg(cmd.args, "secret") {
		body["secret"] = argValue(cmd.args, "secret", "")
	}
	return body
}

func webhookEditBody(cmd command) map[string]any {
	body := map[string]any{}
	for _, pair := range [][2]string{{"name", "name"}, {"target-url", "targetUrl"}, {"match", "match"}} {
		if hasArg(cmd.args, pair[0]) {
			body[pair[1]] = argValue(cmd.args, pair[0], "")
		}
	}
	if events := valuesFor(cmd.args, "event"); len(events) > 0 {
		body["eventSelectionMode"], body["eventTypes"] = "selected", events
	}
	addWebhookAuth(body, cmd)
	return body
}

func addWebhookAuth(body map[string]any, cmd command) {
	if !hasArg(cmd.args, "auth-type") {
		return
	}
	typ := argValue(cmd.args, "auth-type", "")
	body["authType"] = typ
	switch typ {
	case "bearer":
		if hasArg(cmd.args, "auth-token") {
			body["authToken"] = argValue(cmd.args, "auth-token", "")
		}
	case "basic":
		body["authBasic"] = map[string]any{"user": argValue(cmd.args, "auth-user", ""), "password": argValue(cmd.args, "auth-password", "")}
	case "custom":
		headers := map[string]string{}
		for _, raw := range valuesFor(cmd.args, "auth-header") {
			if i := strings.IndexByte(raw, '='); i > 0 {
				headers[strings.TrimSpace(raw[:i])] = raw[i+1:]
			}
		}
		body["authHeaders"] = headers
	}
}

func boolArg(args []string, name string) bool {
	v := argValue(args, name, "false")
	return v == "true" || v == "1"
}

func nullableArg(args []string, name string) any {
	if !hasArg(args, name) {
		return nil
	}
	return argValue(args, name, "")
}

func positionQuery(args []string) string {
	if value := argValue(args, "before", ""); value != "" {
		return "?before=" + url.QueryEscape(value)
	}
	if value := argValue(args, "after", ""); value != "" {
		return "?after=" + url.QueryEscape(value)
	}
	return ""
}

func secureResourceRequest(ctx context.Context, deps Dependencies, c *client, method, path string, body any, cmd command, collection bool) int {
	data, err := c.request(ctx, method, path, body)
	if err != nil {
		return operationExit(deps, ctx, err)
	}
	if len(data) > 0 && !json.Valid(data) {
		writeError(deps.Stderr, errors.New("error: response has an invalid shape [invalid_response]"))
		return ExitOperation
	}
	if cmd.fieldsOnly {
		for _, field := range cmd.catalog {
			fmt.Fprintln(deps.Stdout, field)
		}
		return ExitOK
	}
	if len(cmd.fields) > 0 {
		selected, err := SelectFields(data, cmd.fields, collection)
		if err != nil {
			writeError(deps.Stderr, errors.New("error: response has an invalid shape [invalid_response]"))
			return ExitOperation
		}
		return writeJSON(deps.Stdout, json.RawMessage(selected))
	}
	if collection {
		var values []map[string]json.RawMessage
		if json.Unmarshal(data, &values) != nil || values == nil {
			writeError(deps.Stderr, errors.New("error: response has an invalid shape [invalid_response]"))
			return ExitOperation
		}
		if len(values) == 0 {
			messages := map[string]string{"activity-list": "No activity", "routing-rule-list": "No routing rules", "webhook-subscription-list": "No webhook subscriptions", "webhook-subscription-failures": "No webhook delivery failures"}
			if message := messages[cmd.kind]; message != "" {
				fmt.Fprintln(deps.Stdout, message)
				return ExitOK
			}
		}
	}
	if strings.HasPrefix(cmd.kind, "webhook-") {
		data = redactWebhookSecrets(data)
	}
	if renderHumanResource(deps.Stdout, cmd.kind, data) {
		return ExitOK
	}
	return writeJSON(deps.Stdout, json.RawMessage(data))
}

func renderHumanResource(out interface{ Write([]byte) (int, error) }, kind string, data json.RawMessage) bool {
	var values []map[string]json.RawMessage
	if kind == "activity-list" || kind == "routing-rule-list" || kind == "webhook-subscription-list" || kind == "webhook-subscription-failures" {
		if json.Unmarshal(data, &values) != nil {
			return false
		}
	}
	switch kind {
	case "activity-list":
		fmt.Fprintln(out, "provenance  scope  kind  time  title")
		for _, value := range values {
			fmt.Fprintf(out, "%s  %s  %s  %s  %s\n", rawString(value["provenance"]), rawString(value["scope"]), rawString(value["kind"]), rawString(value["time"]), rawString(value["title"]))
		}
		return true
	case "routing-rule-list":
		fmt.Fprintln(out, "position  name  agent  status  continue")
		for _, value := range values {
			agent := rawString(value["agentName"])
			if agent == "" {
				agent = rawString(value["agentId"])
			}
			fmt.Fprintf(out, "%s  %s  %s  %s  %s\n", rawString(value["position"]), rawString(value["name"]), agent, rawString(value["status"]), rawString(value["continue"]))
		}
		return true
	case "webhook-subscription-list":
		fmt.Fprintln(out, "name  status  target url  has secret  id")
		for _, value := range values {
			fmt.Fprintf(out, "%s  %s  %s  %s  %s\n", rawString(value["name"]), rawString(value["status"]), rawString(value["targetUrl"]), rawString(value["hasSecret"]), rawString(value["id"]))
		}
		return true
	case "webhook-subscription-failures":
		fmt.Fprintln(out, "occurred at  event type  status  error summary")
		for _, value := range values {
			fmt.Fprintf(out, "%s  %s  %s  %s\n", rawString(value["occurredAt"]), rawString(value["eventType"]), rawString(value["responseStatus"]), rawString(value["errorSummary"]))
		}
		return true
	case "routing-test":
		var root map[string]json.RawMessage
		if json.Unmarshal(data, &root) != nil {
			return false
		}
		var events []map[string]json.RawMessage
		if json.Unmarshal(root["events"], &events) != nil {
			return false
		}
		for _, event := range events {
			fmt.Fprintf(out, "Event %s\n", firstRawString(event, "eventId", "id"))
			var outcomes []map[string]json.RawMessage
			if json.Unmarshal(event["outcomes"], &outcomes) != nil {
				continue
			}
			for _, outcome := range outcomes {
				fmt.Fprintf(out, "  %s: %s -> %s\n", firstRawString(outcome, "ruleName", "ruleId"), firstRawString(outcome, "decision", "outcome"), firstRawString(outcome, "agentName", "resolvedAgentName"))
			}
		}
		return true
	}
	return false
}

func rawString(raw json.RawMessage) string {
	var value string
	if json.Unmarshal(raw, &value) == nil {
		return value
	}
	if len(raw) == 0 || string(raw) == "null" {
		return ""
	}
	return string(raw)
}

func firstRawString(value map[string]json.RawMessage, fields ...string) string {
	for _, field := range fields {
		if result := rawString(value[field]); result != "" {
			return result
		}
	}
	return "-"
}

func redactWebhookSecrets(data json.RawMessage) json.RawMessage {
	var value any
	if json.Unmarshal(data, &value) != nil {
		return data
	}
	var clean func(any) any
	clean = func(input any) any {
		switch typed := input.(type) {
		case map[string]any:
			for _, key := range []string{"secret", "authToken", "authPassword", "password", "authHeaders"} {
				delete(typed, key)
			}
			for key, nested := range typed {
				typed[key] = clean(nested)
			}
			return typed
		case []any:
			for i, nested := range typed {
				typed[i] = clean(nested)
			}
			return typed
		default:
			return input
		}
	}
	encoded, err := json.Marshal(clean(value))
	if err != nil {
		return data
	}
	return encoded
}
