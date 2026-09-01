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
	"path/filepath"
	"strings"
)

const (
	DefaultServerURL = "http://localhost:3456"
	DefaultOperatorID = "mohist-cli"
	operatorIDHeader = "X-Mohist-Operator-Id"
)

const (
	ExitOK = 0
	ExitOperation = 1
	ExitUsage = 2
	ExitCanceled = 130
)

type EnvLookup func(string) (string, bool)
type ReadFile func(string) (string, error)

// Dependencies makes process boundaries explicit and keeps command tests local.
type Dependencies struct {
	HTTPClient *http.Client
	Stdout io.Writer
	Stderr io.Writer
	Lookup EnvLookup
	ReadFile ReadFile
	HomeDir func() (string, error)
}

type Config struct {
	ServerURL string
	OperatorToken string
	OperatorID string
}

func defaultDependencies() Dependencies {
	return Dependencies{
		HTTPClient: http.DefaultClient,
		Stdout: os.Stdout,
		Stderr: os.Stderr,
		Lookup: os.LookupEnv,
		ReadFile: func(path string) (string, error) { b, err := os.ReadFile(path); return string(b), err },
		HomeDir: os.UserHomeDir,
	}
}

func ResolveConfig(deps Dependencies) (Config, error) {
	if deps.Lookup == nil { deps.Lookup = os.LookupEnv }
	if deps.ReadFile == nil { deps.ReadFile = func(path string) (string, error) { b, err := os.ReadFile(path); return string(b), err } }
	if deps.HomeDir == nil { deps.HomeDir = os.UserHomeDir }
	cfg := Config{ServerURL: DefaultServerURL, OperatorID: DefaultOperatorID}
	if value, ok := deps.Lookup("MOHIST_SERVER_URL"); ok && strings.TrimSpace(value) != "" { cfg.ServerURL = strings.TrimSpace(value) }
	if value, ok := deps.Lookup("MOHIST_OPERATOR_ID"); ok && strings.TrimSpace(value) != "" { cfg.OperatorID = strings.TrimSpace(value) }
	if value, ok := deps.Lookup("MOHIST_OPERATOR_TOKEN"); ok && strings.TrimSpace(value) != "" {
		cfg.OperatorToken = strings.TrimSpace(value)
		return cfg, validateConfig(cfg)
	}
	path := strings.TrimSpace(lookup(deps.Lookup, "MOHIST_OPERATOR_TOKEN_PATH"))
	if path == "" {
		home, err := deps.HomeDir()
		if err != nil { return Config{}, errors.New("Mohist operator credential could not be resolved") }
		path = filepath.Join(home, ".mohist", "operator-token")
	}
	value, err := deps.ReadFile(path)
	if err != nil { return Config{}, errors.New("Mohist operator credential file could not be read") }
	cfg.OperatorToken = strings.TrimSpace(value)
	if cfg.OperatorToken == "" { return Config{}, errors.New("Mohist operator credential is blank") }
	return cfg, validateConfig(cfg)
}

func validateConfig(cfg Config) error {
	if strings.TrimSpace(cfg.ServerURL) == "" { return errors.New("Mohist server URL is blank") }
	parsed, err := url.Parse(cfg.ServerURL)
	if err != nil || (parsed.Scheme != "http" && parsed.Scheme != "https") || parsed.Host == "" { return errors.New("Mohist server URL must be an absolute HTTP URL") }
	if strings.TrimSpace(cfg.OperatorToken) == "" { return errors.New("Mohist operator credential is blank") }
	return nil
}

func lookup(lookup EnvLookup, name string) string { value, _ := lookup(name); return value }

type usageError struct { message string }
func (e *usageError) Error() string { return e.message }

type operationError struct { message string }
func (e *operationError) Error() string { return e.message }

// Run executes one CLI invocation and returns its process exit code.
func Run(ctx context.Context, args []string, deps Dependencies) int {
	if ctx == nil { ctx = context.Background() }
	if deps.Stdout == nil || deps.Stderr == nil || deps.HTTPClient == nil || deps.Lookup == nil || deps.ReadFile == nil || deps.HomeDir == nil {
		defaults := defaultDependencies()
		if deps.Stdout == nil { deps.Stdout = defaults.Stdout }; if deps.Stderr == nil { deps.Stderr = defaults.Stderr }; if deps.HTTPClient == nil { deps.HTTPClient = defaults.HTTPClient }
		if deps.Lookup == nil { deps.Lookup = defaults.Lookup }; if deps.ReadFile == nil { deps.ReadFile = defaults.ReadFile }; if deps.HomeDir == nil { deps.HomeDir = defaults.HomeDir }
	}
	if err := ctx.Err(); err != nil { writeError(deps.Stderr, err); return ExitCanceled }
	command, err := parse(args)
	if err != nil { writeError(deps.Stderr, err); return ExitUsage }
	if command.help { fmt.Fprintln(deps.Stdout, rootUsage()); return ExitOK }
	if command.fieldsOnly { fmt.Fprintln(deps.Stdout, strings.Join(command.catalog, "\n")); return ExitOK }
	cfg, err := ResolveConfig(deps)
	if err != nil { writeError(deps.Stderr, err); return ExitOperation }
	client, err := newClient(cfg, deps.HTTPClient)
	if err != nil { writeError(deps.Stderr, err); return ExitOperation }
	data, err := client.get(ctx, command.path)
	if err != nil {
		if errors.Is(err, context.Canceled) || errors.Is(ctx.Err(), context.Canceled) { writeError(deps.Stderr, err); return ExitCanceled }
		writeError(deps.Stderr, err); return ExitOperation
	}
	if err := render(deps.Stdout, command, data); err != nil { writeError(deps.Stderr, err); return ExitOperation }
	if command.kind == "doctor" && doctorFailed(data) { return ExitOperation }
	return ExitOK
}

type command struct { kind, path string; fields []string; catalog []string; fieldsOnly, help bool }
var diagnosisFields = []string{"workflowRunId", "status", "failure", "tasks", "dispatch", "events"}
var doctorFields = []string{"name", "status", "detail", "nextAction"}

func parse(args []string) (command, error) {
	if len(args) == 0 { return command{}, &usageError{message: rootUsage()} }
	if args[0] == "--help" || args[0] == "-h" { return command{help: true}, nil }
	if args[0] == "doctor" { return parseLeaf("doctor", args[1:], "/api/doctor/checks", doctorFields, "mo doctor") }
	if args[0] != "run" { return command{}, &usageError{message: "error: unknown command\nusage: mo run why <run-ref> [--json [fields]] | mo doctor"} }
	if len(args) < 2 || args[1] != "why" { return command{}, &usageError{message: "error: incomplete command\nusage: mo run why <run-ref> [--json [fields]]"} }
	return parseLeaf("why", args[2:], "", diagnosisFields, "mo run why <run-ref>")
}

func parseLeaf(kind string, args []string, path string, catalog []string, usage string) (command, error) {
	c := command{kind: kind, path: path, catalog: catalog}
	positionals := []string{}
	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--help", "-h": return command{help: true}, nil
		case "--json":
			c.fieldsOnly = true
			if i+1 < len(args) && !strings.HasPrefix(args[i+1], "-") { c.fieldsOnly = false; c.fields = strings.Split(args[i+1], ","); i++ }
		default:
			if strings.HasPrefix(args[i], "-") { return command{}, &usageError{message: "error: unknown option " + args[i] + "\nusage: " + usage} }
			positionals = append(positionals, args[i])
		}
	}
	if kind == "why" {
		if c.fieldsOnly && len(positionals) == 0 { return command{kind: kind, catalog: catalog, fieldsOnly: true}, nil }
		if len(positionals) != 1 { return command{}, &usageError{message: "error: run reference is required\nusage: " + usage} }
		c.path = "/api/runs/" + url.PathEscape(positionals[0]) + "/diagnosis"
	} else if len(positionals) != 0 { return command{}, &usageError{message: "error: doctor does not accept positional arguments\nusage: " + usage} }
	if len(c.fields) > 0 { for _, field := range c.fields { if !contains(catalog, field) { return command{}, &usageError{message: fmt.Sprintf("error: unknown JSON field %q; discover fields with %s --json", field, usage)} } } }
	return c, nil
}

func contains(values []string, wanted string) bool { for _, value := range values { if value == wanted { return true } }; return false }
func rootUsage() string { return "usage: mo run why <run-ref> [--json [fields]] | mo doctor" }

type client struct { http *http.Client; base *url.URL; token, operatorID string }
func newClient(cfg Config, httpClient *http.Client) (*client, error) { base, err := url.Parse(strings.TrimRight(cfg.ServerURL, "/")); if err != nil { return nil, errors.New("Mohist server URL is invalid") }; return &client{http: httpClient, base: base, token: cfg.OperatorToken, operatorID: cfg.OperatorID}, nil }

type envelope struct { Success bool `json:"success"`; Data json.RawMessage `json:"data"`; Error string `json:"error"`; Code string `json:"code"` }
func (c *client) get(ctx context.Context, path string) (json.RawMessage, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, c.base.String()+path, nil); if err != nil { return nil, &operationError{message: "error: request could not be created [request_error]"} }
	req.Header.Set("Authorization", "Bearer "+c.token); req.Header.Set(operatorIDHeader, c.operatorID); req.Header.Set("Accept", "application/json")
	resp, err := c.http.Do(req); if err != nil { return nil, &operationError{message: "error: Mohist Server request failed [service_unavailable]"} }; defer resp.Body.Close()
	body, err := io.ReadAll(resp.Body); if err != nil { return nil, &operationError{message: "error: Mohist Server response could not be read [response_error]"} }
	var result envelope; if err := json.Unmarshal(body, &result); err != nil { return nil, &operationError{message: "error: Mohist Server returned malformed JSON [invalid_response]"} }
	if resp.StatusCode < 200 || resp.StatusCode >= 300 || !result.Success { code := result.Code; if code == "" { code = "service_error" }; message := result.Error; if message == "" { message = "Mohist Server request failed" }; return nil, &operationError{message: "error: " + message + " [" + code + "]"} }
	if len(result.Data) == 0 || string(result.Data) == "null" { return nil, &operationError{message: "error: Mohist Server returned no data [invalid_response]"} }
	return result.Data, nil
}

func render(out io.Writer, c command, data json.RawMessage) error {
	if len(c.fields) > 0 { return project(out, data, c.fields, c.kind == "doctor") }
	if c.kind == "doctor" { var checks []map[string]any; if err := json.Unmarshal(data, &checks); err != nil { return errors.New("error: Doctor response has an invalid shape [invalid_response]") }; for _, check := range checks { fmt.Fprintf(out, "%v: %v\n", check["name"], check["status"]); if detail, ok := check["detail"].(string); ok && detail != "" { fmt.Fprintln(out, "  "+detail) }; if next, ok := check["nextAction"].(string); ok && next != "" { fmt.Fprintln(out, "  next: "+next) } }; return nil }
	var diagnosis map[string]any; if err := json.Unmarshal(data, &diagnosis); err != nil { return errors.New("error: diagnosis response has an invalid shape [invalid_response]") }; fmt.Fprintf(out, "run: %v\nstatus: %v\n", diagnosis["workflowRunId"], diagnosis["status"]); if failure, ok := diagnosis["failure"].(map[string]any); ok { fmt.Fprintf(out, "failure: %v\n", failure["message"]) }; return nil
}

func project(out io.Writer, data json.RawMessage, fields []string, collection bool) error {
	if collection { var values []map[string]json.RawMessage; if err := json.Unmarshal(data, &values); err != nil { return errors.New("error: response has an invalid shape [invalid_response]") }; projected := make([]map[string]json.RawMessage, 0, len(values)); for _, value := range values { projected = append(projected, pick(value, fields)) }; return json.NewEncoder(out).Encode(projected) }
	var value map[string]json.RawMessage; if err := json.Unmarshal(data, &value); err != nil { return errors.New("error: response has an invalid shape [invalid_response]") }; return json.NewEncoder(out).Encode(pick(value, fields))
}
func pick(value map[string]json.RawMessage, fields []string) map[string]json.RawMessage { result := make(map[string]json.RawMessage, len(fields)); for _, field := range fields { result[field] = value[field] }; return result }
func doctorFailed(data json.RawMessage) bool { var checks []struct{ Status string `json:"status"` }; if json.Unmarshal(data, &checks) != nil { return true }; for _, check := range checks { if check.Status == "fail" { return true } }; return false }
func writeError(out io.Writer, err error) { if err != nil { fmt.Fprintln(out, err.Error()) } }
