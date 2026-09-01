package mohistcli

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"path/filepath"
	"strings"
)

var projectFields = []string{"id", "name", "createdAt", "updatedAt", "repositories", "variables", "defaultRepository", "defaultExecutionConfig", "verificationCommand"}
var repoFields = []string{"name", "gitUrl", "baseBranch", "isDefault", "resolvedBaseBranch"}
var workspaceFields = []string{"projectId", "name", "origin", "repositories", "status", "home", "createdAt", "archivedAt", "boundSessionCount", "sessions"}
var projectWorkflowFields = []string{"projectId", "profileId"}
var promptFields = []string{"key", "displayName", "description", "tags", "stage", "body", "source"}
var previewFields = []string{"rendered", "missingVariables", "depth", "errors"}
var variableFields = []string{"vars", "stages"}

func parseProjectSpace(group string, args []string) (command, error) {
	if len(args) == 0 || (len(args) == 1 && (args[0] == "--help" || args[0] == "-h")) {
		return command{help: true, helpText: projectSpaceHelp(group)}, nil
	}
	if args[0] == "--help" || args[0] == "-h" {
		return command{help: true, helpText: projectSpaceHelp(group)}, nil
	}
	if group == "project" {
		return parseProject(args)
	}
	if group == "repo" {
		return parseRepo(args)
	}
	return parseWorkspace(args)
}

func projectSpaceHelp(group string) string {
	switch group {
	case "project":
		return "USAGE\n    mo project <list|view|create|use|delete|repo|workflow|variable> [flags]\n\nManage Projects and their state.\n\nActions: list, view, create, use, delete, repo, workflow, variable"
	case "repo":
		return "USAGE\n    mo repo <list|create|edit|delete> [flags]\n\nManage Repositories inside the active Project.\n\nActions: list, create, edit, delete"
	default:
		return "USAGE\n    mo workspace <list|view|create|close|repo> [flags]\n\nManage named workspaces in the active Project.\n\nActions: list, view, create, close, repo"
	}
}

func parseProject(args []string) (command, error) {
	switch args[0] {
	case "list":
		return parseSpaceFlags("project-list", args[1:], "/api/projects", projectFields, true, false)
	case "view":
		return parseNamedSpace("project-view", args[1:], projectFields, "project", false)
	case "use":
		return parseNamedSpace("project-use", args[1:], nil, "project", false)
	case "delete":
		return parseNamedSpace("project-delete", args[1:], nil, "project", false)
	case "create":
		return parseProjectCreate(args[1:])
	case "repo":
		if len(args) == 1 && (args[0] == "--help" || args[0] == "-h") {
			return command{help: true, helpText: "USAGE\n    mo project repo set-default <name> [flags]\n\nSet a Repository as the Project default."}, nil
		}
		if len(args) < 2 || args[1] != "set-default" {
			return command{}, usage("unknown project repo command")
		}
		return parseRepoMutation("project-repo-default", args[2:], "set-default")
	case "workflow":
		return parseProjectWorkflow(args[1:])
	case "variable":
		return parseVariable("project", args[1:])
	default:
		return command{}, usage("unknown project command")
	}
}

func parseRepo(args []string) (command, error) {
	switch args[0] {
	case "list":
		return parseSpaceFlags("repo-list", args[1:], "", repoFields, true, true)
	case "create":
		return parseRepoMutation("repo-create", args[1:], "create")
	case "edit":
		return parseRepoMutation("repo-edit", args[1:], "edit")
	case "delete":
		return parseRepoMutation("repo-delete", args[1:], "delete")
	default:
		return command{}, usage("unknown repo command")
	}
}

func parseWorkspace(args []string) (command, error) {
	switch args[0] {
	case "list":
		return parseWorkspaceList(args[1:])
	case "view":
		return parseNamedSpace("workspace-view", args[1:], workspaceFields, "workspace", true)
	case "create":
		return parseWorkspaceCreate(args[1:])
	case "close":
		return parseNamedSpace("workspace-close", args[1:], nil, "workspace", true)
	case "repo":
		if len(args) == 2 && (args[1] == "--help" || args[1] == "-h") {
			return command{help: true, helpText: "USAGE\n    mo workspace repo <add|remove> <name> <repo> [flags]"}, nil
		}
		if len(args) < 2 || (args[1] != "add" && args[1] != "remove") {
			return command{}, usage("unknown workspace repo command")
		}
		return parseWorkspaceRepo(args[1], args[2:])
	default:
		return command{}, usage("unknown workspace command")
	}
}

func parseSpaceFlags(kind string, args []string, path string, catalog []string, collection, projectRequired bool) (command, error) {
	c := command{kind: kind, path: path, catalog: catalog}
	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--json":
			var err error
			i, err = jsonFlag(args, i, &c)
			if err != nil {
				return command{}, err
			}
		case "--project":
			if i+1 >= len(args) {
				return command{}, usage("--project requires a value")
			}
			c.args = append(c.args, "project", args[i+1])
			i++
		case "--status", "--origin":
			if i+1 >= len(args) {
				return command{}, usage(args[i] + " requires a value")
			}
			c.args = append(c.args, strings.TrimPrefix(args[i], "--"), args[i+1])
			i++
		case "--help", "-h":
			return command{help: true, helpText: leafHelp(kind, catalog)}, nil
		default:
			return command{}, usage("unknown option " + args[i])
		}
	}
	if projectRequired {
		c.args = append(c.args, "project-required", "true")
	}
	if err := validateFields(c.fields, catalog, "mo "+strings.ReplaceAll(kind, "-", " ")); err != nil {
		return command{}, err
	}
	_ = collection
	return c, nil
}

func parseNamedSpace(kind string, args []string, catalog []string, noun string, projectRequired bool) (command, error) {
	c := command{kind: kind, catalog: catalog}
	if len(args) == 0 {
		return command{}, usage(noun + " name is required")
	}
	c.args = append(c.args, "name", args[0])
	for i := 1; i < len(args); i++ {
		switch args[i] {
		case "--json":
			var err error
			i, err = jsonFlag(args, i, &c)
			if err != nil {
				return command{}, err
			}
		case "--project":
			if i+1 >= len(args) {
				return command{}, usage("--project requires a value")
			}
			c.args = append(c.args, "project", args[i+1])
			i++
		case "--help", "-h":
			return command{help: true, helpText: leafHelp(kind, catalog)}, nil
		default:
			return command{}, usage("unknown option " + args[i])
		}
	}
	if projectRequired {
		c.args = append(c.args, "project-required", "true")
	}
	if catalog != nil {
		if err := validateFields(c.fields, catalog, "mo "+strings.ReplaceAll(kind, "-", " ")); err != nil {
			return command{}, err
		}
	}
	return c, nil
}

func parseRepoMutation(kind string, args []string, action string) (command, error) {
	c := command{kind: kind, catalog: repoFields}
	if action == "create" || action == "edit" || action == "delete" || action == "set-default" {
		if len(args) == 0 {
			return command{}, usage("repository name is required")
		}
		c.args = append(c.args, "name", args[0])
	}
	for i := 1; i < len(args); i++ {
		switch args[i] {
		case "--git-url", "--base-branch", "--project":
			if i+1 >= len(args) {
				return command{}, usage(args[i] + " requires a value")
			}
			c.args = append(c.args, strings.TrimPrefix(args[i], "--"), args[i+1])
			i++
		case "--set-default":
			c.args = append(c.args, "set-default", "true")
		case "--json":
			var err error
			i, err = jsonFlag(args, i, &c)
			if err != nil {
				return command{}, err
			}
		case "--help", "-h":
			return command{help: true, helpText: leafHelp(kind, repoFields)}, nil
		default:
			return command{}, usage("unknown option " + args[i])
		}
	}
	if action == "create" && argValue(c.args, "git-url", "") == "" {
		return command{}, usage("--git-url is required to create a repository")
	}
	if action == "edit" && !hasArg(c.args, "git-url") && !hasArg(c.args, "base-branch") {
		return command{}, usage("repository requires --git-url and/or --base-branch to update")
	}
	if err := validateFields(c.fields, repoFields, "mo repo "+action); err != nil {
		return command{}, err
	}
	c.args = append(c.args, "project-required", "true", "action", action)
	return c, nil
}

func parseWorkspaceList(args []string) (command, error) {
	c, err := parseSpaceFlags("workspace-list", args, "", workspaceFields, true, true)
	if err != nil {
		return command{}, err
	}
	return c, nil
}

func parseWorkspaceCreate(args []string) (command, error) {
	if len(args) == 0 {
		return command{}, usage("workspace name is required")
	}
	c := command{kind: "workspace-create", catalog: workspaceFields, args: []string{"name", args[0], "project-required", "true"}}
	for i := 1; i < len(args); i++ {
		switch args[i] {
		case "--repo":
			if i+1 >= len(args) {
				return command{}, usage("--repo requires a value")
			}
			c.args = append(c.args, "repo", args[i+1])
			i++
		case "--project":
			if i+1 >= len(args) {
				return command{}, usage("--project requires a value")
			}
			c.args = append(c.args, "project", args[i+1])
			i++
		case "--json":
			var e error
			i, e = jsonFlag(args, i, &c)
			if e != nil {
				return command{}, e
			}
		case "--help", "-h":
			return command{help: true, helpText: leafHelp("workspace-create", workspaceFields)}, nil
		default:
			return command{}, usage("unknown option " + args[i])
		}
	}
	return c, validateFields(c.fields, workspaceFields, "mo workspace create")
}

func parseWorkspaceRepo(action string, args []string) (command, error) {
	if len(args) < 2 {
		return command{}, usage("workspace name and repository are required")
	}
	c := command{kind: "workspace-repo-" + action, args: []string{"name", args[0], "repo", args[1], "project-required", "true"}}
	for i := 2; i < len(args); i++ {
		if args[i] == "--project" && i+1 < len(args) {
			c.args = append(c.args, "project", args[i+1])
			i++
			continue
		}
		if args[i] == "--help" || args[i] == "-h" {
			return command{help: true, helpText: "USAGE\n    mo workspace repo " + action + " <name> <repo> [--project <ref>]"}, nil
		}
		return command{}, usage("unknown option " + args[i])
	}
	return c, nil
}

func parseProjectWorkflow(args []string) (command, error) {
	if len(args) == 0 {
		return command{}, usage("workflow action is required")
	}
	if args[0] == "--help" || args[0] == "-h" {
		return command{help: true, helpText: "USAGE\n    mo project workflow <set-default|verification|prompt> [flags]\n\nManage Project Workflow references and Prompts."}, nil
	}
	if args[0] == "set-default" {
		c, err := parseRepoMutation("project-workflow-default", args[1:], "set-default")
		if err != nil {
			return command{}, err
		}
		c.catalog = projectWorkflowFields
		if err := validateFields(c.fields, c.catalog, "mo project workflow set-default"); err != nil {
			return command{}, err
		}
		c.args = append(c.args, "profile", argValue(c.args, "name", ""))
		c.args = removeArg(c.args, "name")
		return c, nil
	}
	if args[0] == "verification" {
		return parseVerification(args[1:])
	}
	if args[0] == "prompt" {
		return parsePrompt(args[1:])
	}
	return command{}, usage("unknown project workflow command")
}

func parseVerification(args []string) (command, error) {
	if len(args) == 0 {
		return command{}, usage("verification action is required")
	}
	if args[0] == "--help" || args[0] == "-h" {
		return command{help: true, helpText: "USAGE\n    mo project workflow verification <set|view> [flags]"}, nil
	}
	if args[0] == "view" {
		c, err := parseNamedSpace("project-workflow-verification-view", args[1:], projectFields, "project", true)
		return c, err
	}
	if args[0] != "set" {
		return command{}, usage("unknown verification command")
	}
	c := command{kind: "project-workflow-verification-set", catalog: projectFields, args: []string{"project-required", "true"}}
	for i := 1; i < len(args); i++ {
		switch args[i] {
		case "--command", "--command-file", "--project":
			if i+1 >= len(args) {
				return command{}, usage(args[i] + " requires a value")
			}
			c.args = append(c.args, strings.TrimPrefix(args[i], "--"), args[i+1])
			i++
		case "--json":
			var e error
			i, e = jsonFlag(args, i, &c)
			if e != nil {
				return command{}, e
			}
		case "--help", "-h":
			return command{help: true, helpText: leafHelp(c.kind, projectFields)}, nil
		default:
			return command{}, usage("unknown option " + args[i])
		}
	}
	if hasArg(c.args, "command") == hasArg(c.args, "command-file") {
		return command{}, usage("exactly one of --command or --command-file is required")
	}
	return c, validateFields(c.fields, projectFields, "mo project workflow verification set")
}

func parsePrompt(args []string) (command, error) {
	if len(args) == 0 {
		return command{}, usage("prompt action is required")
	}
	if args[0] == "--help" || args[0] == "-h" {
		return command{help: true, helpText: "USAGE\n    mo project workflow prompt <get|set|clear|preview> [flags]"}, nil
	}
	action := args[0]
	if action != "get" && action != "set" && action != "clear" && action != "preview" {
		return command{}, usage("unknown prompt command")
	}
	catalog := promptFields
	if action == "preview" {
		catalog = previewFields
	}
	c := command{kind: "project-workflow-prompt-" + action, catalog: catalog, args: []string{"project-required", "true"}}
	start := 1
	if action != "get" {
		if len(args) < 2 {
			return command{}, usage("prompt key is required")
		}
		c.args = append(c.args, "key", args[1])
		start = 2
	}
	for i := start; i < len(args); i++ {
		switch args[i] {
		case "--project":
			if i+1 >= len(args) {
				return command{}, usage("--project requires a value")
			}
			c.args = append(c.args, "project", args[i+1])
			i++
		case "--body", "--body-file":
			if i+1 >= len(args) {
				return command{}, usage(args[i] + " requires a value")
			}
			c.args = append(c.args, strings.TrimPrefix(args[i], "--"), args[i+1])
			i++
		case "--json":
			var e error
			i, e = jsonFlag(args, i, &c)
			if e != nil {
				return command{}, e
			}
		case "--help", "-h":
			return command{help: true, helpText: leafHelp(c.kind, catalog)}, nil
		default:
			return command{}, usage("unknown option " + args[i])
		}
	}
	if action == "set" && (hasArg(c.args, "body") == hasArg(c.args, "body-file")) {
		return command{}, usage("exactly one of --body or --body-file is required")
	}
	return c, validateFields(c.fields, catalog, "mo project workflow prompt "+action)
}

func parseProjectCreate(args []string) (command, error) {
	if len(args) == 0 {
		return command{}, usage("project name is required")
	}
	c := command{kind: "project-create", args: []string{"name", args[0]}}
	for i := 1; i < len(args); i++ {
		switch args[i] {
		case "--path", "--verification-command", "--verification-command-file":
			if i+1 >= len(args) {
				return command{}, usage(args[i] + " requires a value")
			}
			c.args = append(c.args, strings.TrimPrefix(args[i], "--"), args[i+1])
			i++
		case "--help", "-h":
			return command{help: true, helpText: "USAGE\n    mo project create <name> --path <dir> (--verification-command <cmd> | --verification-command-file <file|->)"}, nil
		default:
			return command{}, usage("unknown option " + args[i])
		}
	}
	if argValue(c.args, "path", "") == "" {
		return command{}, usage("--path is required")
	}
	if hasArg(c.args, "verification-command") == hasArg(c.args, "verification-command-file") {
		return command{}, usage("exactly one verification command source is required")
	}
	return c, nil
}

func parseVariable(scope string, args []string) (command, error) {
	if len(args) == 0 || args[0] == "--help" || args[0] == "-h" {
		return command{help: true, helpText: "USAGE\n    mo " + scope + " variable <list|get|set|unset> [flags]\n\nActions: list, get, set, unset\nOptions: --stage <stage>, --value-json <json>, --project <ref>, --json [fields]"}, nil
	}
	verb := args[0]
	if verb != "list" && verb != "get" && verb != "set" && verb != "unset" {
		return command{}, usage("unknown variable command")
	}
	c := command{kind: scope + "-variable-" + verb, catalog: variableFields, args: []string{"project-required", "true"}}
	start := 1
	if verb == "get" || verb == "set" || verb == "unset" {
		if len(args) <= start {
			return command{}, usage("variable key is required")
		}
		c.args = append(c.args, "key", strings.TrimSpace(args[start]))
		start++
		if verb == "set" && start < len(args) && !strings.HasPrefix(args[start], "-") {
			c.args = append(c.args, "value", args[start])
			start++
		}
	}
	for i := start; i < len(args); i++ {
		switch args[i] {
		case "--project", "--stage", "--value-json":
			if i+1 >= len(args) {
				return command{}, usage(args[i] + " requires a value")
			}
			c.args = append(c.args, strings.TrimPrefix(args[i], "--"), args[i+1])
			i++
		case "--json":
			var e error
			i, e = jsonFlag(args, i, &c)
			if e != nil {
				return command{}, e
			}
		case "--help", "-h":
			return command{help: true, helpText: leafHelp(c.kind, variableFields)}, nil
		default:
			return command{}, usage("unknown option " + args[i])
		}
	}
	key := argValue(c.args, "key", "")
	if verb != "list" {
		if key == "" {
			return command{}, usage("variable key is required")
		}
		for _, part := range strings.Split(key, ".") {
			if strings.TrimSpace(part) == "" {
				return command{}, usage("variable key contains an empty segment")
			}
		}
	}
	if verb == "set" && hasArg(c.args, "value-json") && hasArg(c.args, "value") {
		return command{}, usage("positional value and --value-json are mutually exclusive")
	}
	if verb == "set" && !hasArg(c.args, "value-json") && !hasArg(c.args, "value") {
		return command{}, usage("set requires a value")
	}
	return c, validateFields(c.fields, variableFields, "mo "+scope+" variable "+verb)
}

func jsonFlag(args []string, i int, c *command) (int, error) {
	c.fieldsOnly = true
	if i+1 < len(args) && !strings.HasPrefix(args[i+1], "-") {
		c.fieldsOnly = false
		c.fields = strings.Split(args[i+1], ",")
		i++
	}
	return i, nil
}
func leafHelp(kind string, fields []string) string {
	return "USAGE\n    mo " + strings.ReplaceAll(kind, "-", " ") + " [flags]\n\nJSON FIELDS\n" + strings.Join(fields, "\n")
}
func usage(message string) error { return &usageError{message: "error: " + message} }
func removeArg(args []string, name string) []string {
	out := args[:0]
	for i := 0; i < len(args); i += 2 {
		if i+1 < len(args) && args[i] == name {
			continue
		}
		out = append(out, args[i], args[i+1])
	}
	return out
}

func runProjectSpace(ctx context.Context, deps Dependencies, c *client, cmd command) int {
	if cmd.kind == "project-use" {
		return projectUse(ctx, deps, c, argValue(cmd.args, "name", ""))
	}
	if cmd.kind == "project-create" {
		return projectCreate(ctx, deps, c, cmd)
	}
	if cmd.kind == "project-list" {
		return resourceRequest(ctx, deps, c, http.MethodGet, "/api/projects", nil, cmd, true)
	}
	if cmd.kind == "project-view" {
		return resourceRequest(ctx, deps, c, http.MethodGet, "/api/projects/"+url.PathEscape(argValue(cmd.args, "name", "")), nil, cmd, false)
	}
	if cmd.kind == "project-delete" {
		return resourceRequest(ctx, deps, c, http.MethodDelete, "/api/projects/"+url.PathEscape(argValue(cmd.args, "name", "")), nil, cmd, false)
	}
	project, ok := resolveProject(deps, argValue(cmd.args, "project", ""))
	if !ok {
		writeError(deps.Stderr, errors.New("Run 'mo project use <name-or-id>' or pass --project <name-or-id>"))
		return ExitOperation
	}
	name := argValue(cmd.args, "name", "")
	base := "/api/projects/" + url.PathEscape(project)
	switch cmd.kind {
	case "repo-list":
		return resourceRequest(ctx, deps, c, http.MethodGet, base+"/repositories", nil, cmd, true)
	case "repo-create":
		body := map[string]any{"name": name, "gitUrl": argValue(cmd.args, "git-url", ""), "baseBranch": argValue(cmd.args, "base-branch", "main")}
		if hasArg(cmd.args, "set-default") {
			body["setDefault"] = true
		}
		return resourceRequest(ctx, deps, c, http.MethodPost, base+"/repositories", body, cmd, false)
	case "repo-edit":
		body := map[string]any{}
		if hasArg(cmd.args, "git-url") {
			body["gitUrl"] = argValue(cmd.args, "git-url", "")
		}
		if hasArg(cmd.args, "base-branch") {
			body["baseBranch"] = argValue(cmd.args, "base-branch", "")
		}
		return resourceRequest(ctx, deps, c, http.MethodPatch, base+"/repositories/"+url.PathEscape(name), body, cmd, false)
	case "repo-delete":
		return resourceRequest(ctx, deps, c, http.MethodDelete, base+"/repositories/"+url.PathEscape(name), nil, cmd, false)
	case "project-repo-default":
		return resourceRequest(ctx, deps, c, http.MethodPatch, base+"/repositories/"+url.PathEscape(name), map[string]any{"setDefault": true}, cmd, false)
	case "project-workflow-default":
		return resourceRequest(ctx, deps, c, http.MethodPut, base+"/workflow-profile/default", map[string]any{"profileId": argValue(cmd.args, "profile", "")}, cmd, false)
	case "project-workflow-verification-view":
		return resourceRequest(ctx, deps, c, http.MethodGet, base, nil, cmd, false)
	case "project-workflow-verification-set":
		return resourceRequest(ctx, deps, c, http.MethodPut, base+"/verification-command", map[string]any{"command": inputValue(deps, cmd, "command", "command-file")}, cmd, false)
	case "project-workflow-prompt-get":
		return resourceRequest(ctx, deps, c, http.MethodGet, base+"/workflow-profile/prompts", nil, cmd, true)
	case "project-workflow-prompt-set":
		return resourceRequest(ctx, deps, c, http.MethodPut, base+"/workflow-profile/prompts/"+url.PathEscape(argValue(cmd.args, "key", "")), map[string]any{"body": inputValue(deps, cmd, "body", "body-file")}, cmd, false)
	case "project-workflow-prompt-clear":
		return resourceRequest(ctx, deps, c, http.MethodDelete, base+"/workflow-profile/prompts/"+url.PathEscape(argValue(cmd.args, "key", "")), nil, cmd, false)
	case "project-workflow-prompt-preview":
		return resourceRequest(ctx, deps, c, http.MethodPost, base+"/workflow-profile/prompts/"+url.PathEscape(argValue(cmd.args, "key", ""))+"/preview", map[string]any{}, cmd, false)
	case "workspace-list":
		path := base + "/workspaces"
		q := url.Values{}
		if v := argValue(cmd.args, "status", ""); v != "" {
			q.Set("status", v)
		}
		if v := argValue(cmd.args, "origin", ""); v != "" {
			q.Set("origin", v)
		}
		if s := q.Encode(); s != "" {
			path += "?" + s
		}
		return resourceRequest(ctx, deps, c, http.MethodGet, path, nil, cmd, true)
	case "workspace-view":
		return resourceRequest(ctx, deps, c, http.MethodGet, base+"/workspaces/"+url.PathEscape(name), nil, cmd, false)
	case "workspace-create":
		body := map[string]any{"name": name}
		repos := valuesFor(cmd.args, "repo")
		if len(repos) > 0 {
			body["repos"] = repos
		}
		return resourceRequest(ctx, deps, c, http.MethodPost, base+"/workspaces", body, cmd, false)
	case "workspace-close":
		return resourceRequest(ctx, deps, c, http.MethodPost, base+"/workspaces/"+url.PathEscape(name)+"/close", nil, cmd, false)
	case "workspace-repo-add":
		return resourceRequest(ctx, deps, c, http.MethodPost, base+"/workspaces/"+url.PathEscape(name)+"/repo", map[string]any{"repo": argValue(cmd.args, "repo", "")}, cmd, false)
	case "workspace-repo-remove":
		return resourceRequest(ctx, deps, c, http.MethodDelete, base+"/workspaces/"+url.PathEscape(name)+"/repo?repo="+url.QueryEscape(argValue(cmd.args, "repo", "")), nil, cmd, false)
	case "project-variable-list", "project-variable-get", "project-variable-set", "project-variable-unset":
		return runVariables(ctx, deps, c, cmd, project)
	}
	return ExitUsage
}

func inputValue(deps Dependencies, c command, plain, file string) string {
	if hasArg(c.args, plain) {
		return argValue(c.args, plain, "")
	}
	if argValue(c.args, file, "") == "-" {
		b, _ := io.ReadAll(deps.Input)
		return string(b)
	}
	v, e := deps.ReadFile(argValue(c.args, file, ""))
	if e != nil {
		return ""
	}
	return strings.TrimSuffix(v, "\n")
}

func resourceRequest(ctx context.Context, deps Dependencies, c *client, method, path string, body any, cmd command, collection bool) int {
	data, err := c.request(ctx, method, path, body)
	if err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	if cmd.fieldsOnly {
		for _, f := range cmd.catalog {
			fmt.Fprintln(deps.Stdout, f)
		}
		return ExitOK
	}
	if len(cmd.fields) > 0 {
		selected, e := SelectFields(data, cmd.fields, collection)
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
	var v any
	if json.Unmarshal(data, &v) == nil {
		if collection {
			if list, ok := v.([]any); ok && len(list) == 0 {
				message := "No results"
				switch cmd.kind { case "project-list": message = "No projects"; case "repo-list": message = "No repositories found"; case "workspace-list": message = "No workspaces" }
				fmt.Fprintln(deps.Stdout, message)
				return ExitOK
			}
		}
		return writeJSON(deps.Stdout, v)
	}
	_, _ = deps.Stdout.Write(append(data, '\n'))
	return ExitOK
}

func (c *client) request(ctx context.Context, method, path string, body any) (json.RawMessage, error) {
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
	resp, e := c.http.Do(req)
	if e != nil {
		return nil, &operationError{message: "error: Mohist Server request failed [service_unavailable]"}
	}
	defer resp.Body.Close()
	b, e := io.ReadAll(resp.Body)
	if e != nil {
		return nil, e
	}
	if len(b) == 0 && resp.StatusCode >= 200 && resp.StatusCode < 300 {
		return nil, nil
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

func resolveProject(deps Dependencies, explicit string) (string, bool) {
	if strings.TrimSpace(explicit) != "" {
		return strings.TrimSpace(explicit), true
	}
	dirs := []string{}
	dir := deps.CurrentDirectory()
	for dir != "" {
		dirs = append(dirs, dir)
		parent := filepath.Dir(dir)
		if parent == dir {
			break
		}
		dir = parent
	}
	for _, d := range dirs {
		if p, ok := readProjectState(deps, filepath.Join(d, ".mohist", "cli-state.json")); ok {
			return p, true
		}
		if stateExists(deps, filepath.Join(d, ".mohist", "cli-state.json")) {
			return "", false
		}
	}
	home, e := deps.HomeDir()
	if e != nil {
		return "", false
	}
	p, ok := readProjectState(deps, filepath.Join(home, ".mohist", "cli-state.json"))
	return p, ok
}
func stateExists(deps Dependencies, path string) bool { _, e := deps.ReadFile(path); return e == nil }
func readProjectState(deps Dependencies, path string) (string, bool) {
	text, e := deps.ReadFile(path)
	if e != nil {
		return "", false
	}
	var v map[string]any
	if json.Unmarshal([]byte(text), &v) != nil || len(v) != 1 {
		return "", false
	}
	p, ok := v["activeProjectId"].(string)
	return strings.TrimSpace(p), ok && strings.TrimSpace(p) != ""
}

func projectUse(ctx context.Context, deps Dependencies, c *client, ref string) int {
	data, e := c.request(ctx, http.MethodPost, "/api/projects/"+url.PathEscape(ref)+"/use", map[string]any{})
	if e != nil {
		writeError(deps.Stderr, e)
		return ExitOperation
	}
	var p struct {
		ID   string `json:"id"`
		Name string `json:"name"`
	}
	if json.Unmarshal(data, &p) != nil || p.ID == "" {
		writeError(deps.Stderr, errors.New("error: invalid project response [invalid_response]"))
		return ExitOperation
	}
	home, e := deps.HomeDir()
	if e != nil {
		writeError(deps.Stderr, e)
		return ExitOperation
	}
	text := fmt.Sprintf("{\"activeProjectId\":%q}\n", p.ID)
	if e = deps.WriteFile(filepath.Join(home, ".mohist", "cli-state.json"), text, 0600); e != nil {
		writeError(deps.Stderr, e)
		return ExitOperation
	}
	if dir := deps.CurrentDirectory(); dir != "" {
		if e = deps.WriteFile(filepath.Join(dir, ".mohist", "cli-state.json"), text, 0600); e != nil {
			writeError(deps.Stderr, e)
			return ExitOperation
		}
	}
	fmt.Fprintf(deps.Stdout, "Active project: %s (%s)\n", p.Name, p.ID)
	return ExitOK
}

func projectCreate(ctx context.Context, deps Dependencies, c *client, cmd command) int {
	path := argValue(cmd.args, "path", "")
	name := argValue(cmd.args, "name", "")
	verification := inputValue(deps, cmd, "verification-command", "verification-command-file")
	repo := filepath.Base(filepath.Clean(path))
	if repo == "." || repo == string(filepath.Separator) {
		writeError(deps.Stderr, errors.New("--path produced an empty repository resource name"))
		return ExitOperation
	}
	body := map[string]any{"name": name, "verificationCommand": verification, "repository": map[string]any{"name": repo, "gitUrl": "", "baseBranch": "main"}}
	return resourceRequest(ctx, deps, c, http.MethodPost, "/api/projects", body, cmd, false)
}

func runVariables(ctx context.Context, deps Dependencies, c *client, cmd command, project string) int {
	path := "/api/projects/" + url.PathEscape(project) + "/variables"
	key := argValue(cmd.args, "key", "")
	switch cmd.kind {
	case "project-variable-list":
		return resourceRequest(ctx, deps, c, http.MethodGet, path, nil, cmd, true)
	case "project-variable-get":
		data, e := c.request(ctx, http.MethodGet, path, nil)
		if e != nil {
			writeError(deps.Stderr, e)
			return ExitOperation
		}
		return printVariableValue(deps, data, key)
	case "project-variable-set", "project-variable-unset":
		if strings.Contains(key, ".") {
			data, e := c.request(ctx, http.MethodGet, path, nil)
			if e != nil {
				writeError(deps.Stderr, e)
				return ExitOperation
			}
			if e = validateVariableIntermediate(data, key, argValue(cmd.args, "stage", "")); e != nil {
				writeError(deps.Stderr, e)
				return ExitUsage
			}
		}
		var value any = nil
		if cmd.kind == "project-variable-set" {
			if hasArg(cmd.args, "value-json") {
				if json.Unmarshal([]byte(argValue(cmd.args, "value-json", "")), &value) != nil {
					writeError(deps.Stderr, errors.New("invalid JSON value"))
					return ExitUsage
				}
			} else {
				value = argValue(cmd.args, "value", "")
			}
		}
		overlay := nestedVariableValue(key, value)
		body := map[string]any{}
		if stage := argValue(cmd.args, "stage", ""); stage != "" {
			body["stages"] = map[string]any{stage: map[string]any{"vars": overlay}}
		} else {
			body["vars"] = overlay
		}
		return resourceRequest(ctx, deps, c, http.MethodPatch, path, body, cmd, false)
	}
	return ExitUsage
}
func printVariableValue(deps Dependencies, data json.RawMessage, key string) int {
	var root map[string]any
	if json.Unmarshal(data, &root) != nil {
		writeError(deps.Stderr, errors.New("error: invalid variables response [invalid_response]"))
		return ExitOperation
	}
	v := any(root)
	if vars, ok := root["vars"]; ok {
		v = vars
	}
	for _, part := range strings.Split(key, ".") {
		m, ok := v.(map[string]any)
		if !ok {
			fmt.Fprintln(deps.Stdout, "(absent)")
			return ExitOK
		}
		v, ok = m[part]
		if !ok {
			fmt.Fprintln(deps.Stdout, "(absent)")
			return ExitOK
		}
	}
	return writeJSON(deps.Stdout, v)
}
func nestedVariableValue(key string, value any) map[string]any {
	parts := strings.Split(key, ".")
	root := map[string]any{}
	current := root
	for _, part := range parts[:len(parts)-1] {
		next := map[string]any{}
		current[part] = next
		current = next
	}
	current[parts[len(parts)-1]] = value
	return root
}
func validateVariableIntermediate(data json.RawMessage, key, stage string) error {
	var root map[string]any
	if json.Unmarshal(data, &root) != nil {
		return errors.New("error: invalid variables response [invalid_response]")
	}
	value := any(root)
	if stage != "" {
		stages, ok := root["stages"].(map[string]any)
		if !ok {
			return nil
		}
		value = stages[stage]
		if value == nil {
			return nil
		}
	}
	m, ok := value.(map[string]any)
	if !ok {
		return errors.New("variable path intermediate is not a JSON object")
	}
	if vars, ok := m["vars"]; ok {
		value = vars
	}
	for _, part := range strings.Split(key, ".")[:len(strings.Split(key, "."))-1] {
		m, ok := value.(map[string]any)
		if !ok {
			return errors.New("variable path intermediate is not a JSON object")
		}
		next, exists := m[part]
		if !exists {
			return nil
		}
		value = next
	}
	return nil
}
