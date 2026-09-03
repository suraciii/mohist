// Package mohistcli implements the diagnostic portion of the Mohist CLI.
package mohistcli

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strings"
	"time"
)

const (
	DefaultServerURL  = "http://localhost:3456"
	DefaultOperatorID = "mohist-cli"
	operatorIDHeader  = "X-Mohist-Operator-Id"
)

const (
	ExitOK        = 0
	ExitOperation = 1
	ExitUsage     = 2
	ExitCanceled  = 130
)

type EnvLookup func(string) (string, bool)
type ReadFile func(string) (string, error)
type WriteFile func(string, string, os.FileMode) error
type Execute func(context.Context, string, []string) error
type Wait func(context.Context, time.Duration) error
type EventTail func(context.Context, string, []string, string, io.Writer) error
type HealthProbe func(context.Context, string) error

// Dependencies makes process boundaries explicit and keeps command tests local.
type Dependencies struct {
	HTTPClient       *http.Client
	Stdout           io.Writer
	Stderr           io.Writer
	Lookup           EnvLookup
	ReadFile         ReadFile
	WriteFile        WriteFile
	HomeDir          func() (string, error)
	Execute          Execute
	OpenBrowser      Execute
	Input            io.Reader
	Now              func() time.Time
	Wait             Wait
	Executable       func() string
	CurrentDirectory func() string
	EventTail        EventTail
	HealthProbe      HealthProbe
	MkdirAll         func(string, os.FileMode) error
	RemoveAll        func(string) error
	Rename           func(string, string) error
	Chmod            func(string, os.FileMode) error
}

type Config struct {
	ServerURL        string
	OperatorToken    string
	OperatorID       string
	CredentialSource string
	RefreshToken     string
	SessionServer    string
}

func defaultDependencies() Dependencies {
	return Dependencies{
		HTTPClient: http.DefaultClient,
		Stdout:     os.Stdout,
		Stderr:     os.Stderr,
		Lookup:     os.LookupEnv,
		ReadFile: func(path string) (string, error) {
			b, err := os.ReadFile(path)
			return string(b), err
		},
		WriteFile: func(path, value string, mode os.FileMode) error {
			if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
				return err
			}
			if err := os.WriteFile(path, []byte(value), mode); err != nil {
				return err
			}
			return os.Chmod(path, mode)
		},
		HomeDir: os.UserHomeDir,
		Execute: func(ctx context.Context, name string, args []string) error {
			cmd := exec.CommandContext(ctx, name, args...)
			return cmd.Run()
		},
		OpenBrowser: func(ctx context.Context, name string, args []string) error {
			cmd := exec.CommandContext(ctx, name, args...)
			return cmd.Run()
		},
		Input: os.Stdin,
		Now:   time.Now,
		Wait: func(ctx context.Context, d time.Duration) error {
			timer := time.NewTimer(d)
			defer timer.Stop()
			select {
			case <-ctx.Done():
				return ctx.Err()
			case <-timer.C:
				return nil
			}
		},
		Executable:       func() string { return os.Args[0] },
		CurrentDirectory: func() string { value, _ := os.Getwd(); return value },
	}
}

func ResolveConfig(deps Dependencies) (Config, error) {
	defaults := defaultDependencies()
	if deps.Lookup == nil {
		deps.Lookup = os.LookupEnv
	}
	if deps.ReadFile == nil {
		deps.ReadFile = func(path string) (string, error) {
			b, err := os.ReadFile(path)
			return string(b), err
		}
	}
	if deps.HomeDir == nil {
		deps.HomeDir = os.UserHomeDir
	}
	if deps.WriteFile == nil {
		deps.WriteFile = defaults.WriteFile
	}
	if deps.Execute == nil {
		deps.Execute = defaults.Execute
	}
	if deps.OpenBrowser == nil {
		deps.OpenBrowser = defaults.OpenBrowser
	}
	if deps.Input == nil {
		deps.Input = defaults.Input
	}
	if deps.Now == nil {
		deps.Now = defaults.Now
	}
	if deps.Wait == nil {
		deps.Wait = defaults.Wait
	}
	if deps.Executable == nil {
		deps.Executable = defaults.Executable
	}
	if deps.HealthProbe == nil {
		deps.HealthProbe = func(ctx context.Context, address string) error {
			req, err := http.NewRequestWithContext(ctx, http.MethodGet, strings.TrimRight(address, "/")+"/health", nil)
			if err != nil {
				return err
			}
			resp, err := deps.HTTPClient.Do(req)
			if err != nil {
				return err
			}
			defer resp.Body.Close()
			if resp.StatusCode < 200 || resp.StatusCode >= 300 {
				return errors.New("health probe failed")
			}
			return nil
		}
	}
	if deps.CurrentDirectory == nil {
		deps.CurrentDirectory = defaults.CurrentDirectory
	}
	if deps.MkdirAll == nil {
		deps.MkdirAll = os.MkdirAll
	}
	if deps.RemoveAll == nil {
		deps.RemoveAll = os.RemoveAll
	}
	if deps.Rename == nil {
		deps.Rename = os.Rename
	}
	if deps.Chmod == nil {
		deps.Chmod = os.Chmod
	}

	cfg := Config{ServerURL: DefaultServerURL, OperatorID: DefaultOperatorID}
	if value, ok := deps.Lookup("MOHIST_SERVER_URL"); ok && strings.TrimSpace(value) != "" {
		cfg.ServerURL = strings.TrimSpace(value)
	}
	if value, ok := deps.Lookup("MOHIST_OPERATOR_ID"); ok && strings.TrimSpace(value) != "" {
		cfg.OperatorID = strings.TrimSpace(value)
	}
	if value, ok := deps.Lookup("MOHIST_TOKEN"); ok && strings.TrimSpace(value) != "" {
		cfg.OperatorToken = strings.TrimSpace(value)
		cfg.CredentialSource = "MOHIST_TOKEN"
		return cfg, validateConfig(cfg)
	}

	// Keep the original bootstrap variable names for local installations while
	// preferring the public authentication contract above.
	if value, ok := deps.Lookup("MOHIST_OPERATOR_TOKEN"); ok && strings.TrimSpace(value) != "" {
		cfg.OperatorToken = strings.TrimSpace(value)
		cfg.CredentialSource = "MOHIST_OPERATOR_TOKEN"
		return cfg, validateConfig(cfg)
	}
	if session, err := loadSession(deps, cfg.ServerURL); err == nil && session != nil {
		cfg.OperatorToken, cfg.RefreshToken = session.AccessToken, session.RefreshToken
		cfg.CredentialSource, cfg.SessionServer = "credentials.json", session.Server
		return cfg, validateConfig(cfg)
	}
	if value, ok := deps.Lookup("MOHIST_ADMIN_TOKEN"); ok && strings.TrimSpace(value) != "" {
		cfg.OperatorToken = strings.TrimSpace(value)
		cfg.CredentialSource = "machine-local admin credential"
		return cfg, validateConfig(cfg)
	}

	path := strings.TrimSpace(lookup(deps.Lookup, "MOHIST_ADMIN_TOKEN_PATH"))
	if path == "" {
		path = strings.TrimSpace(lookup(deps.Lookup, "MOHIST_OPERATOR_TOKEN_PATH"))
	}
	explicitPath := path != ""
	if path == "" {
		home, err := deps.HomeDir()
		if err != nil {
			return Config{}, errors.New("Mohist operator credential could not be resolved")
		}
		path = filepath.Join(home, ".mohist", "admin-token")
		if _, err := deps.ReadFile(path); err != nil {
			path = filepath.Join(home, ".mohist", "operator-token")
		}
	}
	value, err := deps.ReadFile(path)
	if err != nil {
		if explicitPath {
			return Config{}, errors.New("Mohist operator credential file could not be read")
		}
		return cfg, validateConfig(cfg)
	}
	cfg.OperatorToken = strings.TrimSpace(value)
	if cfg.OperatorToken == "" && explicitPath {
		return Config{}, errors.New("Mohist operator credential is blank")
	}
	if cfg.OperatorToken != "" {
		cfg.CredentialSource = "machine-local admin credential"
	}
	return cfg, validateConfig(cfg)
}

func validateConfig(cfg Config) error {
	if strings.TrimSpace(cfg.ServerURL) == "" {
		return errors.New("Mohist server URL is blank")
	}
	parsed, err := url.Parse(cfg.ServerURL)
	if err != nil || (parsed.Scheme != "http" && parsed.Scheme != "https") || parsed.Host == "" {
		return errors.New("Mohist server URL must be an absolute HTTP URL")
	}
	return nil
}

func lookup(lookup EnvLookup, name string) string {
	value, _ := lookup(name)
	return value
}

type usageError struct{ message string }

func (e *usageError) Error() string { return e.message }

type operationError struct{ message string }

func (e *operationError) Error() string { return e.message }

// Run executes one CLI invocation and returns its process exit code.
func Run(ctx context.Context, args []string, deps Dependencies) int {
	if ctx == nil {
		ctx = context.Background()
	}
	defaults := defaultDependencies()
	if deps.MkdirAll == nil {
		deps.MkdirAll = os.MkdirAll
	}
	if deps.RemoveAll == nil {
		deps.RemoveAll = os.RemoveAll
	}
	if deps.Rename == nil {
		deps.Rename = os.Rename
	}
	if deps.Chmod == nil {
		deps.Chmod = os.Chmod
	}
	if deps.Stdout == nil {
		deps.Stdout = defaults.Stdout
	}
	if deps.Stderr == nil {
		deps.Stderr = defaults.Stderr
	}
	if deps.HTTPClient == nil {
		deps.HTTPClient = defaults.HTTPClient
	}
	if deps.Lookup == nil {
		deps.Lookup = defaults.Lookup
	}
	if deps.ReadFile == nil {
		deps.ReadFile = defaults.ReadFile
	}
	if deps.HomeDir == nil {
		deps.HomeDir = defaults.HomeDir
	}
	if deps.WriteFile == nil {
		deps.WriteFile = defaults.WriteFile
	}
	if deps.Execute == nil {
		deps.Execute = defaults.Execute
	}
	if deps.OpenBrowser == nil {
		deps.OpenBrowser = defaults.OpenBrowser
	}
	if deps.Input == nil {
		deps.Input = defaults.Input
	}
	if deps.Now == nil {
		deps.Now = defaults.Now
	}
	if deps.Wait == nil {
		deps.Wait = defaults.Wait
	}
	if deps.Executable == nil {
		deps.Executable = defaults.Executable
	}
	if deps.CurrentDirectory == nil {
		deps.CurrentDirectory = defaults.CurrentDirectory
	}
	if deps.HealthProbe == nil {
		deps.HealthProbe = func(ctx context.Context, address string) error {
			req, err := http.NewRequestWithContext(ctx, http.MethodGet, strings.TrimRight(address, "/")+"/health", nil)
			if err != nil {
				return err
			}
			resp, err := deps.HTTPClient.Do(req)
			if err != nil {
				return err
			}
			defer resp.Body.Close()
			if resp.StatusCode < 200 || resp.StatusCode >= 300 {
				return errors.New("health probe failed")
			}
			return nil
		}
	}

	if err := ctx.Err(); err != nil {
		writeError(deps.Stderr, err)
		return ExitCanceled
	}
	command, err := parse(args)
	if err != nil {
		writeError(deps.Stderr, err)
		return ExitUsage
	}
	if command.help {
		fmt.Fprintln(deps.Stdout, command.helpText)
		return ExitOK
	}
	if command.fieldsOnly {
		fmt.Fprintln(deps.Stdout, strings.Join(command.catalog, "\n"))
		return ExitOK
	}
	if command.kind == "info" {
		return runInfo(deps, command)
	}
	if strings.HasPrefix(command.kind, "skill-") || strings.HasPrefix(command.kind, "install-") || strings.HasPrefix(command.kind, "update-") {
		return runMaintenance(ctx, deps, command)
	}
	if command.kind == "ops-service" || command.kind == "ops-notification" {
		return runOperations(ctx, deps, nil, command)
	}

	cfg, err := ResolveConfig(deps)
	if err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	client, err := newClient(cfg, deps.HTTPClient)
	if err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	client.deps = deps
	if strings.HasPrefix(command.kind, "auth-") {
		return runAuth(ctx, deps, client, cfg, command)
	}
	if strings.HasPrefix(command.kind, "project-") || strings.HasPrefix(command.kind, "repo-") || strings.HasPrefix(command.kind, "workspace-") {
		return runProjectSpace(ctx, deps, client, command)
	}
	if strings.HasPrefix(command.kind, "issue-") || strings.HasPrefix(command.kind, "epic-") || strings.HasPrefix(command.kind, "label-") {
		return runOrganization(ctx, deps, client, command)
	}
	if strings.HasPrefix(command.kind, "workflow-") || strings.HasPrefix(command.kind, "run-") {
		return runWorkflow(ctx, deps, client, command)
	}
	if strings.HasPrefix(command.kind, "agent-") || strings.HasPrefix(command.kind, "session-") {
		return runAgentSession(ctx, deps, client, command)
	}
	if strings.HasPrefix(command.kind, "activity-") || strings.HasPrefix(command.kind, "routing-") || strings.HasPrefix(command.kind, "webhook-") {
		return runActivityRoutingWebhook(ctx, deps, client, command)
	}
	if strings.HasPrefix(command.kind, "ops-") {
		return runOperations(ctx, deps, client, command)
	}
	data, err := client.get(ctx, command.path)
	if err != nil {
		if errors.Is(err, context.Canceled) || errors.Is(ctx.Err(), context.Canceled) {
			writeError(deps.Stderr, err)
			return ExitCanceled
		}
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	if err := render(deps.Stdout, command, data); err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	if command.kind == "doctor" && doctorFailed(data) {
		return ExitOperation
	}
	return ExitOK
}

type command struct {
	kind, path       string
	fields, catalog  []string
	fieldsOnly, help bool
	helpText         string
	args             []string
}

var diagnosisFields = []string{"workflowRunId", "status", "failure", "tasks", "dispatch", "events"}
var doctorFields = []string{"name", "status", "detail", "nextAction"}

const maxDiagnosisEvents = 200

func parse(args []string) (command, error) {
	if len(args) == 0 {
		return command{}, &usageError{message: rootUsage()}
	}
	if args[0] == "--help" || args[0] == "-h" {
		return command{help: true, helpText: rootUsage()}, nil
	}
	if args[0] == "help" {
		return parseHelp(args[1:])
	}
	if args[0] == "info" {
		return parseInfo(args[1:])
	}
	if args[0] == "skill" || args[0] == "install" || args[0] == "update" {
		return parseMaintenance(args[0], args[1:])
	}
	if args[0] == "auth" {
		return parseAuth(args[1:])
	}
	if args[0] == "doctor" {
		c, err := parseLeaf("doctor", args[1:], "/api/doctor/checks", doctorFields, "mo doctor")
		if c.help {
			c.helpText = doctorHelp()
		}
		return c, err
	}
	if args[0] == "project" || args[0] == "repo" || args[0] == "workspace" {
		return parseProjectSpace(args[0], args[1:])
	}
	if args[0] == "issue" || args[0] == "epic" || args[0] == "label" {
		return parseOrganization(args[0], args[1:])
	}
	if args[0] == "workflow" {
		return parseWorkflow(args[1:])
	}
	if args[0] == "agent" {
		return parseAgent(args[1:])
	}
	if args[0] == "session" {
		return parseSession(args[1:])
	}
	if args[0] == "activity" {
		return parseActivity(args[1:])
	}
	if args[0] == "routing" {
		return parseRouting(args[1:])
	}
	if args[0] == "webhook" {
		return parseWebhook(args[1:])
	}
	if contains([]string{"runner", "server", "service", "event", "audit", "github", "slack", "notification", "otel"}, args[0]) {
		return parseOperations(args[0], args[1:])
	}
	if args[0] != "run" {
		if len(args) == 2 && (args[1] == "--help" || args[1] == "-h") && contains(rootGroups(), args[0]) {
			return command{help: true, helpText: groupHelp(args[0])}, nil
		}
		return command{}, &usageError{message: "error: unknown command\n" + rootUsage()}
	}
	if len(args) == 2 && (args[1] == "--help" || args[1] == "-h") {
		return command{help: true, helpText: runGroupHelp()}, nil
	}
	if len(args) < 2 {
		return command{}, usage("run action is required")
	}
	if args[1] != "why" {
		return parseRun(args[1:])
	}
	c, err := parseLeaf("why", args[2:], "", diagnosisFields, "run why <run-ref>")
	if c.help {
		c.helpText = "USAGE\n    mo run why <run-ref> [--json [fields]]\n\nJSON FIELDS\n" + strings.Join(diagnosisFields, "\n")
	}
	return c, err
}

func parseLeaf(kind string, args []string, path string, catalog []string, usage string) (command, error) {
	c := command{kind: kind, path: path, catalog: catalog}
	positionals := []string{}
	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--help", "-h":
			return command{help: true, helpText: usage + "\n\nJSON FIELDS\n" + strings.Join(catalog, "\n")}, nil
		case "--json":
			c.fieldsOnly = true
			if i+1 < len(args) && !strings.HasPrefix(args[i+1], "-") {
				c.fieldsOnly = false
				c.fields = strings.Split(args[i+1], ",")
				i++
			}
		default:
			if strings.HasPrefix(args[i], "-") {
				return command{}, &usageError{message: "error: unknown option " + args[i] + "\nusage: " + usage}
			}
			positionals = append(positionals, args[i])
		}
	}

	if kind == "why" {
		if c.fieldsOnly && len(positionals) == 0 {
			return command{kind: kind, catalog: catalog, fieldsOnly: true}, nil
		}
		if len(positionals) != 1 {
			return command{}, &usageError{message: "error: run reference is required\nusage: " + usage}
		}
		c.path = "/api/runs/" + url.PathEscape(positionals[0]) + "/diagnosis"
	} else if len(positionals) != 0 {
		return command{}, &usageError{message: "error: doctor does not accept positional arguments\nusage: " + usage}
	}
	if len(c.fields) > 0 {
		for _, field := range c.fields {
			if !contains(catalog, field) {
				return command{}, &usageError{message: fmt.Sprintf("error: unknown JSON field %q; discover fields with %s --json", field, usage)}
			}
		}
	}
	return c, nil
}

func contains(values []string, wanted string) bool {
	for _, value := range values {
		if value == wanted {
			return true
		}
	}
	return false
}

func rootGroups() []string {
	return []string{"project", "repo", "workspace", "issue", "epic", "label", "workflow", "run", "agent", "session", "activity", "routing", "webhook", "runner", "audit", "auth", "server", "service", "event", "github", "slack", "notification", "otel", "skill", "install", "update", "info", "help", "doctor"}
}

func rootUsage() string {
	return "USAGE\n    mo <command> [subcommand] [flags]\n\nMohist CLI\n\nWork\n  project  Manage Projects\n  repo  Manage repositories\n  workspace  Manage workspaces\n  issue  Manage Issues\n  epic  Manage Epics\n  label  Manage labels\n\nAutomation\n  workflow  Manage Workflow Profiles\n  run  Manage WorkflowRuns\n  agent  Manage Agents\n  session  Manage AgentSessions\n  activity  Trace Project activity\n  routing  Manage routing rules\n  webhook  Manage webhook subscriptions\n\nOperations\n  runner  Manage runners\n  server  Manage the Server\n  service  Manage local services\n  event  Inspect event delivery\n  audit  Read audit records\n  auth  Manage credentials and sessions\n  github  Manage GitHub integration\n  slack  Manage Slack integration\n  notification  Configure notifications\n  otel  Inspect telemetry\n\nTools\n  help  Read shared CLI rules\n  skill  Manage Skills\n  install  Install components\n  update  Update components\n  info  Show local CLI information\n  doctor  Check Server readiness\n\nExamples:\n  mo project --help\n  mo run --help\n  mo help output"
}

func groupHelp(name string) string {
	if name == "project" || name == "repo" || name == "workspace" {
		return projectSpaceHelp(name)
	}
	if name == "workflow" {
		return "USAGE\n    mo workflow [<action>] [flags]\n\nProject-scoped Workflow Profiles.\n\nActions: list, view, create, edit, delete, validate\nSee also: mo run --help"
	}
	if name == "run" {
		return runGroupHelp()
	}
	return "USAGE\n    mo " + name + " [<action>] [flags]\n\nManage " + name + " resources locally through the Mohist Server.\n\nUse a leaf command with --help for its complete arguments."
}

func runGroupHelp() string {
	return "USAGE\n    mo run [<action>] [flags]\n\nWorkflowRuns and their controls.\n\nActions: list, view, watch, approve, request-changes, retry, rerun, pause, resume, stop\nCommon scope: --issue <number>\nSee also: mo run why <run-ref>"
}

func doctorHelp() string {
	return "USAGE\n    mo doctor\n\nCheck Server readiness and show the next action for failed checks.\n\nJSON FIELDS\n" + strings.Join(doctorFields, "\n")
}

type client struct {
	http         *http.Client
	base         *url.URL
	token        string
	operatorID   string
	machineLocal bool
	refreshToken string
	deps         Dependencies
}

func newClient(cfg Config, httpClient *http.Client) (*client, error) {
	base, err := url.Parse(strings.TrimRight(cfg.ServerURL, "/"))
	if err != nil || base.Scheme == "" || base.Host == "" {
		return nil, errors.New("Mohist server URL is invalid")
	}
	base.RawPath = ""
	return &client{http: httpClient, base: base, token: cfg.OperatorToken, operatorID: cfg.OperatorID, machineLocal: cfg.CredentialSource == "machine-local admin credential", refreshToken: cfg.RefreshToken}, nil
}

type envelope struct {
	Success *bool           `json:"success"`
	Data    json.RawMessage `json:"data"`
	Error   string          `json:"error"`
	Code    string          `json:"code"`
}

func (c *client) get(ctx context.Context, path string) (json.RawMessage, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, c.base.String()+path, strings.NewReader(""))
	if err != nil {
		return nil, &operationError{message: "error: request could not be created [request_error]"}
	}
	if c.token != "" && (!c.machineLocal || isLoopback(c.base)) {
		req.Header.Set("Authorization", "Bearer "+c.token)
	}
	req.Header.Set(operatorIDHeader, c.operatorID)
	req.Header.Set("Accept", "application/json")

	resp, err := c.http.Do(req)
	if err != nil {
		if errors.Is(err, context.Canceled) || errors.Is(err, context.DeadlineExceeded) {
			return nil, err
		}
		return nil, &operationError{message: "error: Mohist Server request failed [service_unavailable]"}
	}
	defer resp.Body.Close()
	body, err := io.ReadAll(resp.Body)
	if err != nil {
		return nil, &operationError{message: "error: Mohist Server response could not be read [response_error]"}
	}
	if resp.StatusCode == http.StatusUnauthorized && c.refreshToken != "" {
		if c.refresh() {
			refresh := c.refreshToken
			c.refreshToken = ""
			data, retryErr := c.get(ctx, path)
			c.refreshToken = refresh
			return data, retryErr
		}
		if c.deps.Stderr != nil {
			fmt.Fprintln(c.deps.Stderr, "Session expired. Run 'mo auth login' to sign in again.")
		}
	}

	var result envelope
	if err := json.Unmarshal(body, &result); err != nil {
		return nil, &operationError{message: responseStatusError(resp.StatusCode)}
	}
	success := result.Success == nil && resp.StatusCode >= 200 && resp.StatusCode < 300 || result.Success != nil && *result.Success
	if resp.StatusCode < 200 || resp.StatusCode >= 300 || !success {
		code := result.Code
		if code == "" {
			code = statusCodeName(resp.StatusCode)
		}
		message := result.Error
		if message == "" {
			message = "Mohist Server request failed"
		}
		return nil, &operationError{message: "error: " + message + " [" + code + "]"}
	}
	if len(result.Data) == 0 || string(result.Data) == "null" {
		return nil, &operationError{message: "error: Mohist Server returned no data [invalid_response]"}
	}
	return result.Data, nil
}

func (c *client) refresh() bool {
	payload := strings.NewReader(`{"grant_type":"refresh_token","refresh_token":"` + c.refreshToken + `"}`)
	req, err := http.NewRequest(http.MethodPost, c.base.String()+"/api/auth/token", payload)
	if err != nil {
		return false
	}
	req.Header.Set("Content-Type", "application/json")
	resp, err := c.http.Do(req)
	if err != nil {
		return false
	}
	defer resp.Body.Close()
	var env envelope
	if json.NewDecoder(resp.Body).Decode(&env) != nil || env.Data == nil {
		return false
	}
	if env.Success != nil && !*env.Success {
		return false
	}
	var next storedSession
	if json.Unmarshal(env.Data, &next) != nil || next.AccessToken == "" || next.RefreshToken == "" {
		return false
	}
	next.Server = normalizeOrigin(c.base.String())
	c.token, c.refreshToken = next.AccessToken, next.RefreshToken
	if c.deps.WriteFile != nil {
		_ = saveSession(c.deps, next)
	}
	return true
}

func responseStatusError(status int) string {
	if status == http.StatusUnauthorized || status == http.StatusForbidden {
		return "error: Mohist Server authentication failed [" + statusCodeName(status) + "]"
	}
	if status == http.StatusNotFound {
		return "error: Mohist Server could not find the requested resource [not_found]"
	}
	return "error: Mohist Server returned malformed JSON (HTTP " + fmt.Sprint(status) + ") [invalid_response]"
}

func statusCodeName(status int) string {
	switch status {
	case http.StatusUnauthorized:
		return "unauthorized"
	case http.StatusForbidden:
		return "forbidden"
	case http.StatusNotFound:
		return "not_found"
	default:
		return "service_error"
	}
}

func render(out io.Writer, c command, data json.RawMessage) error {
	if c.kind == "why" {
		if err := validateDiagnosis(data); err != nil {
			return err
		}
	} else if c.kind == "doctor" {
		if _, err := decodeDoctorChecks(data); err != nil {
			return err
		}
	}
	if len(c.fields) > 0 {
		return project(out, data, c.fields, c.kind == "doctor")
	}
	if c.kind == "doctor" {
		return renderDoctor(out, data)
	}
	return renderDiagnosis(out, data)
}

func renderDoctor(out io.Writer, data json.RawMessage) error {
	checks, err := decodeDoctorChecks(data)
	if err != nil {
		return err
	}
	for _, check := range checks {
		fmt.Fprintf(out, "name: %s\nstatus: %s\ndetail: %s\n", check.Name, check.Status, check.Detail)
		if check.Status == "fail" && check.NextAction != nil && *check.NextAction != "" {
			fmt.Fprintln(out, "next action: "+*check.NextAction)
		}
		fmt.Fprintln(out)
	}
	return nil
}

type doctorCheck struct {
	Name       string
	Status     string
	Detail     string
	NextAction *string
}

func decodeDoctorChecks(data json.RawMessage) ([]doctorCheck, error) {
	var rawChecks []json.RawMessage
	if err := json.Unmarshal(data, &rawChecks); err != nil || rawChecks == nil {
		return nil, errors.New("error: Doctor response has an invalid shape [invalid_response]")
	}

	checks := make([]doctorCheck, 0, len(rawChecks))
	for _, rawCheck := range rawChecks {
		var fields map[string]json.RawMessage
		if err := json.Unmarshal(rawCheck, &fields); err != nil || fields == nil {
			return nil, errors.New("error: Doctor response has an invalid shape [invalid_response]")
		}

		name, ok := doctorStringField(fields, "name")
		if !ok {
			return nil, errors.New("error: Doctor response has an invalid shape [invalid_response]")
		}
		status, ok := doctorStringField(fields, "status")
		if !ok || (status != "ok" && status != "fail") {
			return nil, errors.New("error: Doctor response has an invalid shape [invalid_response]")
		}
		detail, ok := doctorStringField(fields, "detail")
		if !ok {
			return nil, errors.New("error: Doctor response has an invalid shape [invalid_response]")
		}
		nextAction, ok := doctorNullableStringField(fields, "nextAction")
		if !ok {
			return nil, errors.New("error: Doctor response has an invalid shape [invalid_response]")
		}
		checks = append(checks, doctorCheck{Name: name, Status: status, Detail: detail, NextAction: nextAction})
	}
	return checks, nil
}

func doctorStringField(fields map[string]json.RawMessage, name string) (string, bool) {
	raw, present := fields[name]
	if !present || strings.TrimSpace(string(raw)) == "null" {
		return "", false
	}
	var value string
	if json.Unmarshal(raw, &value) != nil {
		return "", false
	}
	return value, true
}

func doctorNullableStringField(fields map[string]json.RawMessage, name string) (*string, bool) {
	raw, present := fields[name]
	if !present {
		return nil, false
	}
	if strings.TrimSpace(string(raw)) == "null" {
		return nil, true
	}
	value, ok := doctorStringField(fields, name)
	if !ok {
		return nil, false
	}
	return &value, true
}

func renderDiagnosis(out io.Writer, data json.RawMessage) error {
	var diagnosis map[string]json.RawMessage
	if err := json.Unmarshal(data, &diagnosis); err != nil || diagnosis == nil {
		return errors.New("error: diagnosis response has an invalid shape [invalid_response]")
	}

	value := func(field string) any {
		var result any
		if json.Unmarshal(diagnosis[field], &result) != nil {
			return nil
		}
		return result
	}
	stringValue := func(field string) string {
		result, _ := value(field).(string)
		return result
	}

	fmt.Fprintf(out, "run: %s\n", stringValue("workflowRunId"))
	fmt.Fprintf(out, "status: %s\n", stringValue("status"))

	if failure, ok := value("failure").(map[string]any); ok && failure != nil {
		fmt.Fprintln(out, "failure:")
		printFields(out, failure, 1, []string{"reason", "stage", "taskId", "checkName", "message", "error"})
	}
	if tasks, ok := value("tasks").([]any); ok && len(tasks) > 0 {
		fmt.Fprintln(out, "tasks:")
		for _, task := range tasks {
			if taskMap, ok := task.(map[string]any); ok {
				printTask(out, taskMap)
			}
		}
	}
	if dispatch, ok := value("dispatch").(map[string]any); ok && dispatch != nil {
		fmt.Fprintln(out, "dispatch:")
		printFields(out, dispatch, 1, []string{"status", "snapshot"})
	}
	if events, ok := value("events").([]any); ok && len(events) > 0 {
		fmt.Fprintln(out, "events:")
		if len(events) > maxDiagnosisEvents {
			events = events[len(events)-maxDiagnosisEvents:]
		}
		for _, event := range events {
			if eventMap, ok := event.(map[string]any); ok {
				printEvent(out, eventMap)
			}
		}
	}
	return nil
}

func validateDiagnosis(data json.RawMessage) error {
	var diagnosis map[string]json.RawMessage
	if err := json.Unmarshal(data, &diagnosis); err != nil || diagnosis == nil {
		return errors.New("error: diagnosis response has an invalid shape [invalid_response]")
	}
	for _, field := range []string{"workflowRunId", "status", "tasks", "dispatch", "events"} {
		if _, ok := diagnosis[field]; !ok {
			return errors.New("error: diagnosis response has an invalid shape [invalid_response]")
		}
	}
	var runID, status string
	var tasks, events []json.RawMessage
	var dispatch map[string]json.RawMessage
	if json.Unmarshal(diagnosis["workflowRunId"], &runID) != nil || runID == "" ||
		json.Unmarshal(diagnosis["status"], &status) != nil || status == "" ||
		json.Unmarshal(diagnosis["tasks"], &tasks) != nil || tasks == nil ||
		json.Unmarshal(diagnosis["dispatch"], &dispatch) != nil || dispatch == nil ||
		json.Unmarshal(diagnosis["events"], &events) != nil || events == nil {
		return errors.New("error: diagnosis response has an invalid shape [invalid_response]")
	}
	return nil
}

func printTask(out io.Writer, task map[string]any) {
	label := firstString(task, "taskId", "id")
	if label == "" {
		label = "unknown"
	}
	fmt.Fprintf(out, "  %s (attempt %v)\n", label, display(task["attempt"]))
	printFields(out, task, 2, []string{"uses", "renderedWith", "workspace", "exitCode", "error", "recovery"})
}

func printEvent(out io.Writer, event map[string]any) {
	label := firstString(event, "type", "eventId")
	if label == "" {
		label = "event"
	}
	fmt.Fprintf(out, "  %s", label)
	if subject := firstString(event, "subject"); subject != "" {
		fmt.Fprintf(out, " subject=%s", subject)
	}
	if time := firstString(event, "time"); time != "" {
		fmt.Fprintf(out, " time=%s", time)
	}
	fmt.Fprintln(out)
	printFields(out, event, 2, []string{"id", "source", "specVersion", "dataContentType", "data", "extensions"})
}

func printFields(out io.Writer, object map[string]any, indent int, fields []string) {
	prefix := strings.Repeat("  ", indent)
	for _, field := range fields {
		value, present := object[field]
		if !present || value == nil || value == "" {
			continue
		}
		value = sanitizeHumanValue(value)
		if value == nil {
			continue
		}
		if nested, ok := value.(map[string]any); ok {
			fmt.Fprintln(out, prefix+field+":")
			printFields(out, nested, indent+1, sortedKeys(nested))
			continue
		}
		if list, ok := value.([]any); ok {
			fmt.Fprintf(out, "%s%s: %s\n", prefix, field, compactJSON(list))
			continue
		}
		fmt.Fprintf(out, "%s%s: %s\n", prefix, field, display(value))
	}
}

func sortedKeys(values map[string]any) []string {
	keys := make([]string, 0, len(values))
	for key := range values {
		keys = append(keys, key)
	}
	sort.Strings(keys)
	return keys
}

func firstString(values map[string]any, fields ...string) string {
	for _, field := range fields {
		if value, ok := values[field].(string); ok && value != "" {
			return value
		}
	}
	return ""
}

func display(value any) string {
	if value == nil {
		return ""
	}
	if text, ok := value.(string); ok {
		return text
	}
	return compactJSON(value)
}

func compactJSON(value any) string {
	encoded, err := json.Marshal(value)
	if err != nil {
		return "<invalid>"
	}
	return string(encoded)
}

func isProcessScopedPath(value any) bool {
	text, ok := value.(string)
	return ok && strings.Contains(text, "/proc/") && strings.Contains(text, "/fd/")
}

func sanitizeHumanValue(value any) any {
	if isProcessScopedPath(value) {
		return nil
	}
	switch typed := value.(type) {
	case map[string]any:
		clean := make(map[string]any, len(typed))
		for key, nested := range typed {
			if sanitized := sanitizeHumanValue(nested); sanitized != nil {
				clean[key] = sanitized
			}
		}
		return clean
	case []any:
		clean := make([]any, 0, len(typed))
		for _, nested := range typed {
			if sanitized := sanitizeHumanValue(nested); sanitized != nil {
				clean = append(clean, sanitized)
			}
		}
		return clean
	default:
		return value
	}
}

func project(out io.Writer, data json.RawMessage, fields []string, collection bool) error {
	if collection {
		var values []map[string]json.RawMessage
		if err := json.Unmarshal(data, &values); err != nil || values == nil {
			return errors.New("error: response has an invalid shape [invalid_response]")
		}
		projected := make([]map[string]json.RawMessage, 0, len(values))
		for _, value := range values {
			projected = append(projected, pick(value, fields))
		}
		return json.NewEncoder(out).Encode(projected)
	}
	var value map[string]json.RawMessage
	if err := json.Unmarshal(data, &value); err != nil || value == nil {
		return errors.New("error: response has an invalid shape [invalid_response]")
	}
	return json.NewEncoder(out).Encode(pick(value, fields))
}

func pick(value map[string]json.RawMessage, fields []string) map[string]json.RawMessage {
	result := make(map[string]json.RawMessage, len(fields))
	for _, field := range fields {
		if raw, ok := value[field]; ok {
			result[field] = raw
		} else {
			result[field] = json.RawMessage("null")
		}
	}
	return result
}

func doctorFailed(data json.RawMessage) bool {
	checks, err := decodeDoctorChecks(data)
	if err != nil {
		return true
	}
	for _, check := range checks {
		if check.Status == "fail" {
			return true
		}
	}
	return false
}

func writeError(out io.Writer, err error) {
	if err != nil {
		fmt.Fprintln(out, err.Error())
	}
}
