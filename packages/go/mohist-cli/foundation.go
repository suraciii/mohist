package mohistcli

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/url"
	"path/filepath"
	"runtime"
	"strings"
	"time"
)

type storedSession struct {
	Server           string `json:"server"`
	AccessToken      string `json:"accessToken"`
	RefreshToken     string `json:"refreshToken"`
	AccessExpiresAt  string `json:"accessExpiresAt"`
	RefreshExpiresAt string `json:"refreshExpiresAt"`
}

type sessionFile struct {
	Servers []storedSession `json:"servers"`
}

func sessionPath(deps Dependencies) string {
	home, err := deps.HomeDir()
	if err != nil {
		return ""
	}
	return filepath.Join(home, ".mohist", "credentials.json")
}

func normalizeOrigin(value string) string { return strings.TrimRight(strings.TrimSpace(value), "/") }

func loadSession(deps Dependencies, server string) (*storedSession, error) {
	path := sessionPath(deps)
	if path == "" {
		return nil, nil
	}
	text, err := deps.ReadFile(path)
	if err != nil {
		return nil, nil
	}
	var file sessionFile
	if json.Unmarshal([]byte(text), &file) != nil {
		return nil, nil
	}
	wanted := normalizeOrigin(server)
	for i := range file.Servers {
		if normalizeOrigin(file.Servers[i].Server) == wanted {
			return &file.Servers[i], nil
		}
	}
	return nil, nil
}

func saveSession(deps Dependencies, session storedSession) error {
	path := sessionPath(deps)
	if path == "" {
		return errors.New("home directory could not be resolved")
	}
	file := sessionFile{}
	if text, err := deps.ReadFile(path); err == nil {
		_ = json.Unmarshal([]byte(text), &file)
	}
	for i := range file.Servers {
		if normalizeOrigin(file.Servers[i].Server) == normalizeOrigin(session.Server) {
			file.Servers[i] = session
			goto write
		}
	}
	file.Servers = append(file.Servers, session)
write:
	data, err := json.MarshalIndent(file, "", "  ")
	if err != nil {
		return err
	}
	return deps.WriteFile(path, string(data)+"\n", 0o600)
}

func removeSession(deps Dependencies, server string) error {
	path := sessionPath(deps)
	if path == "" {
		return nil
	}
	text, err := deps.ReadFile(path)
	if err != nil {
		return nil
	}
	var file sessionFile
	if json.Unmarshal([]byte(text), &file) != nil {
		return nil
	}
	kept := file.Servers[:0]
	for _, item := range file.Servers {
		if normalizeOrigin(item.Server) != normalizeOrigin(server) {
			kept = append(kept, item)
		}
	}
	file.Servers = kept
	data, err := json.MarshalIndent(file, "", "  ")
	if err != nil {
		return err
	}
	return deps.WriteFile(path, string(data)+"\n", 0o600)
}

func parseHelp(args []string) (command, error) {
	if len(args) == 0 || (len(args) == 1 && (args[0] == "--help" || args[0] == "-h")) {
		return command{help: true, helpText: "USAGE\n    mo help <output|environment|exit-codes>\n\nRead shared CLI rules."}, nil
	}
	if len(args) != 1 {
		return command{}, &usageError{message: "error: help topic is required\nUSAGE\n    mo help <output|environment|exit-codes>"}
	}
	text := map[string]string{
		"output":      "OUTPUT\n\nHuman results go to stdout. --json performs field selection; bare --json discovers fields locally. Errors, hints, confirmation, and progress go to stderr.",
		"environment": "ENVIRONMENT\n\nMOHIST_SERVER_URL selects the Server. MOHIST_TOKEN is the preferred credential. MOHIST_ADMIN_TOKEN and ~/.mohist/admin-token are machine-local. MOHIST_PROMPT_DISABLED=1 disables prompts.",
		"exit-codes":  "EXIT CODES\n\n0 success\n1 operation failure\n2 usage failure\n130 cancelled",
	}
	if value, ok := text[args[0]]; ok {
		return command{help: true, helpText: value}, nil
	}
	return command{}, &usageError{message: "error: unknown help topic\nUSAGE\n    mo help <output|environment|exit-codes>"}
}

func parseInfo(args []string) (command, error) {
	c := command{kind: "info", catalog: infoFields}
	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--help", "-h":
			return command{help: true, helpText: "USAGE\n    mo info [--verbose] [--json [fields]]\n\nShow local CLI, installation, and effective environment information.\n\nJSON FIELDS\n" + strings.Join(infoFields, "\n")}, nil
		case "--verbose", "-v":
			c.args = append(c.args, "verbose")
		case "--json":
			if i+1 < len(args) && !strings.HasPrefix(args[i+1], "-") {
				c.fields = strings.Split(args[i+1], ",")
				i++
			} else {
				c.fieldsOnly = true
			}
		default:
			return command{}, &usageError{message: "error: unknown option " + args[i] + "\nusage: mo info [--verbose] [--json [fields]]"}
		}
	}
	if len(c.fields) > 0 {
		if err := validateFields(c.fields, c.catalog, "mo info"); err != nil {
			return command{}, err
		}
	}
	return c, nil
}

var infoFields = []string{"cli", "server", "runner", "project", "dataDir", "platformNotice", "skills", "gitRemote", "opencodeRuntime", "envVars", "osRuntime", "capacity", "diskUsage"}

// ProjectReference carries only the local spelling of a Project identity. The
// Server remains authoritative for resolving names and IDs.
type ProjectReference struct{ Value string }

func ResolveProjectReference(value string) (ProjectReference, error) {
	value = strings.TrimSpace(value)
	if value == "" {
		return ProjectReference{}, errors.New("project reference is required")
	}
	return ProjectReference{Value: value}, nil
}

func SelectFields(data json.RawMessage, fields []string, collection bool) (json.RawMessage, error) {
	if collection {
		var values []map[string]json.RawMessage
		if err := json.Unmarshal(data, &values); err != nil || values == nil {
			return nil, errors.New("response has an invalid shape")
		}
		result := make([]map[string]json.RawMessage, 0, len(values))
		for _, value := range values {
			result = append(result, pick(value, fields))
		}
		return json.Marshal(result)
	}
	var value map[string]json.RawMessage
	if err := json.Unmarshal(data, &value); err != nil || value == nil {
		return nil, errors.New("response has an invalid shape")
	}
	return json.Marshal(pick(value, fields))
}

func validateFields(fields, catalog []string, usage string) error {
	for _, field := range fields {
		if !contains(catalog, field) {
			return &usageError{message: fmt.Sprintf("error: unknown JSON field %q; discover fields with %s --json", field, usage)}
		}
	}
	return nil
}

func parseAuth(args []string) (command, error) {
	if len(args) == 0 || (len(args) == 1 && (args[0] == "--help" || args[0] == "-h")) {
		return command{help: true, helpText: authHelp()}, nil
	}
	if len(args) == 1 && args[0] == "login" {
		return command{kind: "auth-login"}, nil
	}
	if len(args) == 2 && args[0] == "login" && (args[1] == "--help" || args[1] == "-h") {
		return command{help: true, helpText: "USAGE\n    mo auth login\n\nSign in with device authorization and store a local session."}, nil
	}
	if len(args) == 1 && args[0] == "status" {
		return command{kind: "auth-status"}, nil
	}
	if len(args) == 2 && args[0] == "status" && (args[1] == "--help" || args[1] == "-h") {
		return command{help: true, helpText: "USAGE\n    mo auth status\n\nShow credential source and session state."}, nil
	}
	if len(args) == 1 && args[0] == "logout" {
		return command{kind: "auth-logout"}, nil
	}
	if len(args) == 2 && args[0] == "logout" && (args[1] == "--help" || args[1] == "-h") {
		return command{help: true, helpText: "USAGE\n    mo auth logout\n\nRevoke and clear the local session."}, nil
	}
	if args[0] != "token" {
		return command{}, &usageError{message: "error: unknown auth command\n" + authHelp()}
	}
	if len(args) == 2 && (args[1] == "--help" || args[1] == "-h") {
		return command{help: true, helpText: "USAGE\n    mo auth token <create|list|revoke> [flags]"}, nil
	}
	if len(args) < 2 {
		return command{}, &usageError{message: "error: token action is required\nusage: mo auth token <create|list|revoke>"}
	}
	switch args[1] {
	case "list":
		if len(args) == 3 && (args[2] == "--help" || args[2] == "-h") {
			return command{help: true, helpText: "USAGE\n    mo auth token list\n\nList token names and recognizable prefixes; full values are never shown."}, nil
		}
		return command{kind: "auth-token-list", path: "/api/auth/tokens"}, nil
	case "revoke":
		if len(args) == 3 && (args[2] == "--help" || args[2] == "-h") {
			return command{help: true, helpText: "USAGE\n    mo auth token revoke <name>"}, nil
		}
		if len(args) != 3 {
			return command{}, &usageError{message: "error: token name is required\nusage: mo auth token revoke <name>"}
		}
		return command{kind: "auth-token-revoke", args: args[2:]}, nil
	case "create":
		return parseTokenCreate(args[2:])
	default:
		return command{}, &usageError{message: "error: unknown token action\nusage: mo auth token <create|list|revoke>"}
	}
}

func authHelp() string {
	return "USAGE\n    mo auth <login|status|logout|token>\n\nAuthentication: device login, local session, and personal access tokens.\n\nActions: login, status, logout, token"
}

func parseTokenCreate(args []string) (command, error) {
	c := command{kind: "auth-token-create"}
	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--name":
			if i+1 >= len(args) {
				return command{}, &usageError{message: "error: --name requires a value"}
			}
			c.args = append(c.args, "name", args[i+1])
			i++
		case "--scope":
			if i+1 >= len(args) {
				return command{}, &usageError{message: "error: --scope requires a value"}
			}
			c.args = append(c.args, "scope", args[i+1])
			i++
		case "--ttl":
			if i+1 >= len(args) {
				return command{}, &usageError{message: "error: --ttl requires a value"}
			}
			c.args = append(c.args, "ttl", args[i+1])
			i++
		case "--project":
			if i+1 >= len(args) {
				return command{}, &usageError{message: "error: --project requires a value"}
			}
			c.args = append(c.args, "project", args[i+1])
			i++
		case "--all-projects":
			c.args = append(c.args, "all-projects", "true")
		case "--help", "-h":
			return command{help: true, helpText: "USAGE\n    mo auth token create --name <name> [--scope operator|readonly] [--ttl <hours>] [--project <id>]... [--all-projects]"}, nil
		default:
			return command{}, &usageError{message: "error: unknown option " + args[i] + "\nusage: mo auth token create --name <name> [flags]"}
		}
	}
	return c, nil
}

func runInfo(deps Dependencies, c command) int {
	values := map[string]any{
		"cli":    map[string]any{"version": "dev", "path": deps.Executable()},
		"server": map[string]any{"url": lookup(deps.Lookup, "MOHIST_SERVER_URL")},
		"runner": map[string]any{"status": "unknown"}, "project": nil,
		"dataDir": nil, "platformNotice": nil, "skills": nil, "gitRemote": nil,
		"opencodeRuntime": nil, "envVars": nil, "osRuntime": runtime.GOOS, "capacity": nil, "diskUsage": nil,
	}
	if values["server"].(map[string]any)["url"] == "" {
		values["server"].(map[string]any)["url"] = DefaultServerURL
	}
	if c.fieldsOnly {
		for _, f := range infoFields {
			fmt.Fprintln(deps.Stdout, f)
		}
		return ExitOK
	}
	if len(c.fields) > 0 {
		selected := map[string]any{}
		for _, f := range c.fields {
			selected[f] = values[f]
		}
		return writeJSON(deps.Stdout, selected)
	}
	fmt.Fprintf(deps.Stdout, "CLI: dev\nServer: %s\nRunner: unknown\nProject: not selected\n", values["server"].(map[string]any)["url"])
	if contains(c.args, "verbose") {
		fmt.Fprintln(deps.Stdout, "Platform: "+runtime.GOOS)
		fmt.Fprintln(deps.Stdout, "Data directory: unknown")
	}
	return ExitOK
}

func writeJSON(out interface{ Write([]byte) (int, error) }, value any) int {
	data, err := json.Marshal(value)
	if err != nil {
		return ExitOperation
	}
	_, err = out.Write(append(data, '\n'))
	if err != nil {
		return ExitOperation
	}
	return ExitOK
}

func argValue(args []string, name, fallback string) string {
	for i := 0; i+1 < len(args); i += 2 {
		if args[i] == name {
			return args[i+1]
		}
	}
	return fallback
}
func hasArg(args []string, name string) bool {
	for i := 0; i < len(args); i += 2 {
		if args[i] == name {
			return true
		}
	}
	return false
}
func valuesFor(args []string, name string) []string {
	out := []string{}
	for i := 0; i+1 < len(args); i += 2 {
		if args[i] == name {
			out = append(out, args[i+1])
		}
	}
	return out
}

func runAuth(ctx context.Context, deps Dependencies, client *client, cfg Config, c command) int {
	switch c.kind {
	case "auth-login":
		return authLogin(ctx, deps, client)
	case "auth-status":
		return authStatus(ctx, deps, client, cfg)
	case "auth-logout":
		return authLogout(ctx, deps, client, cfg)
	case "auth-token-list":
		return authTokenList(ctx, deps, client)
	case "auth-token-revoke":
		return authTokenRevoke(ctx, deps, client, c.args[0])
	case "auth-token-create":
		return authTokenCreate(ctx, deps, client, c.args)
	default:
		return ExitUsage
	}
}

func postJSON(ctx context.Context, deps Dependencies, c *client, path string, body any) (json.RawMessage, string, error) {
	b, _ := json.Marshal(body)
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, c.base.String()+path, strings.NewReader(string(b)))
	if err != nil {
		return nil, "", err
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Accept", "application/json")
	req.Header.Set(operatorIDHeader, c.operatorID)
	if c.token != "" && (!c.machineLocal || isLoopback(c.base)) {
		req.Header.Set("Authorization", "Bearer "+c.token)
	}
	resp, err := c.http.Do(req)
	if err != nil {
		return nil, "", errors.New("error: Mohist Server request failed [service_unavailable]")
	}
	defer resp.Body.Close()
	var env envelope
	if err = json.NewDecoder(resp.Body).Decode(&env); err != nil {
		return nil, "", errors.New("error: Mohist Server returned malformed JSON [invalid_response]")
	}
	success := (env.Success == nil && resp.StatusCode >= 200 && resp.StatusCode < 300) || (env.Success != nil && *env.Success)
	if !success || resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return nil, env.Code, &operationError{message: "error: " + env.Error + " [" + env.Code + "]"}
	}
	return env.Data, "", nil
}

func authLogin(ctx context.Context, deps Dependencies, c *client) int {
	data, _, err := postJSON(ctx, deps, c, "/api/auth/device/code", map[string]string{"name": filepath.Base(deps.Executable())})
	if err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	var flow struct {
		DeviceCode, UserCode, VerificationURIComplete string
		Interval                                      int
	}
	if json.Unmarshal(data, &flow) != nil || flow.DeviceCode == "" || flow.UserCode == "" {
		writeError(deps.Stderr, errors.New("The server returned an invalid device authorization response."))
		return ExitOperation
	}
	fmt.Fprintf(deps.Stdout, "Open the confirmation page in your browser:\n  %s\n\nEnter this code on the confirmation page:\n\n  %s\n\n", flow.VerificationURIComplete, displayCode(flow.UserCode))
	if deps.OpenBrowser != nil {
		_ = deps.OpenBrowser(ctx, "xdg-open", []string{flow.VerificationURIComplete})
	}
	interval := flow.Interval
	if interval <= 0 {
		interval = 5
	}
	firstPoll := true
	for {
		if !firstPoll && deps.Wait != nil {
			if err := deps.Wait(ctx, time.Duration(interval)*time.Second); err != nil {
				return ExitCanceled
			}
		}
		firstPoll = false
		data, code, err := postJSON(ctx, deps, c, "/api/auth/token", map[string]string{"grant_type": "urn:ietf:params:oauth:grant-type:device_code", "device_code": flow.DeviceCode})
		if err == nil {
			var tokens storedSession
			if json.Unmarshal(data, &tokens) != nil || tokens.AccessToken == "" || tokens.RefreshToken == "" {
				writeError(deps.Stderr, errors.New("The server returned an invalid session response."))
				return ExitOperation
			}
			tokens.Server = normalizeOrigin(c.base.String())
			if e := saveSession(deps, tokens); e != nil {
				writeError(deps.Stderr, errors.New("The session could not be stored"))
				return ExitOperation
			}
			fmt.Fprintf(deps.Stdout, "Logged in to %s.\n", tokens.Server)
			return ExitOK
		}
		switch code {
		case "authorization_pending":
			continue
		case "slow_down":
			interval += 5
			continue
		case "expired_token":
			writeError(deps.Stderr, errors.New("The authorization code expired. Run 'mo auth login' again."))
			return ExitOperation
		case "access_denied":
			writeError(deps.Stderr, errors.New("The authorization was denied."))
			return ExitOperation
		default:
			writeError(deps.Stderr, err)
			return ExitOperation
		}
	}
}

func displayCode(value string) string {
	if len(value) == 8 {
		return value[:4] + "-" + value[4:]
	}
	return value
}

func authStatus(ctx context.Context, deps Dependencies, c *client, cfg Config) int {
	server := normalizeOrigin(c.base.String())
	if cfg.CredentialSource == "" {
		fmt.Fprintf(deps.Stdout, "Not signed in to %s.\nRun 'mo auth login' to sign in.\n", server)
		return ExitOperation
	}
	fmt.Fprintf(deps.Stdout, "Server: %s\nIdentity: admin\nCredential: %s\n", server, cfg.CredentialSource)
	req, _ := http.NewRequestWithContext(ctx, http.MethodGet, c.base.String()+"/api/auth/session", nil)
	req.Header.Set(operatorIDHeader, c.operatorID)
	if c.token != "" && (!c.machineLocal || isLoopback(c.base)) {
		req.Header.Set("Authorization", "Bearer "+c.token)
	}
	resp, err := c.http.Do(req)
	if err != nil {
		fmt.Fprintln(deps.Stdout, "Session: server unreachable")
	} else if resp.StatusCode >= 200 && resp.StatusCode < 300 {
		fmt.Fprintln(deps.Stdout, "Session: active")
	} else {
		fmt.Fprintln(deps.Stdout, "Session: expired - run 'mo auth login' to sign in again")
	}
	if resp != nil {
		resp.Body.Close()
	}
	return ExitOK
}
func authLogout(ctx context.Context, deps Dependencies, c *client, cfg Config) int {
	server := normalizeOrigin(c.base.String())
	s, _ := loadSession(deps, server)
	if s == nil {
		fmt.Fprintf(deps.Stdout, "No local session for %s.\n", server)
		return ExitOK
	}
	_, _, _ = postJSON(ctx, deps, c, "/api/auth/logout", map[string]string{"refreshToken": s.RefreshToken})
	if err := removeSession(deps, server); err != nil {
		writeError(deps.Stderr, errors.New("The session could not be cleared"))
		return ExitOperation
	}
	fmt.Fprintf(deps.Stdout, "Logged out of %s.\n", server)
	return ExitOK
}
func authTokenList(ctx context.Context, deps Dependencies, c *client) int {
	data, _, err := getData(ctx, c, "/api/auth/tokens")
	if err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	var value struct {
		Tokens []struct {
			Name, Prefix string
			Scopes       []string
			ExpiresAt    string
			RevokedAt    *string `json:"revokedAt"`
		} `json:"tokens"`
	}
	if json.Unmarshal(data, &value) != nil {
		return ExitOperation
	}
	if len(value.Tokens) == 0 {
		fmt.Fprintln(deps.Stdout, "No tokens")
		return ExitOK
	}
	fmt.Fprintln(deps.Stdout, "NAME  PREFIX  SCOPE  EXPIRES  STATUS")
	for _, t := range value.Tokens {
		status := "active"
		if t.RevokedAt != nil {
			status = "revoked"
		}
		fmt.Fprintf(deps.Stdout, "%s  %s  %s  %s  %s\n", t.Name, t.Prefix, strings.Join(t.Scopes, ","), t.ExpiresAt, status)
	}
	return ExitOK
}
func authTokenRevoke(ctx context.Context, deps Dependencies, c *client, name string) int {
	_, _, err := postJSON(ctx, deps, c, "/api/auth/tokens/"+url.PathEscape(name)+"/revoke", map[string]any{})
	if err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	return ExitOK
}
func authTokenCreate(ctx context.Context, deps Dependencies, c *client, args []string) int {
	name := strings.TrimSpace(argValue(args, "name", ""))
	if name == "" {
		writeError(deps.Stderr, errors.New("--name is required"))
		return ExitUsage
	}
	scope := strings.ToLower(argValue(args, "scope", "operator"))
	if scope != "operator" && scope != "readonly" {
		writeError(deps.Stderr, errors.New("--scope must be 'operator' or 'readonly'"))
		return ExitUsage
	}
	projects := valuesFor(args, "project")
	all := hasArg(args, "all-projects")
	if len(projects) > 0 && all {
		writeError(deps.Stderr, errors.New("--project cannot be combined with --all-projects"))
		return ExitUsage
	}
	if all && scope != "operator" {
		writeError(deps.Stderr, errors.New("--all-projects requires --scope operator"))
		return ExitUsage
	}
	body := map[string]any{"name": name, "scope": scope, "allProjects": all}
	if len(projects) > 0 {
		body["projectIds"] = projects
	}
	if ttl := argValue(args, "ttl", ""); ttl != "" {
		var n int
		if _, e := fmt.Sscan(ttl, &n); e != nil || n < 1 || n > 8760 {
			writeError(deps.Stderr, errors.New("--ttl must be between 1 and 8760 hours"))
			return ExitUsage
		}
		body["ttlHours"] = n
	}
	data, _, err := postJSON(ctx, deps, c, "/api/auth/tokens", body)
	if err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	var result struct {
		Token string `json:"token"`
	}
	if json.Unmarshal(data, &result) == nil && result.Token != "" {
		fmt.Fprintln(deps.Stdout, result.Token)
	} else {
		_, _ = deps.Stdout.Write(append(data, '\n'))
	}
	return ExitOK
}

func getData(ctx context.Context, c *client, path string) (json.RawMessage, string, error) {
	req, _ := http.NewRequestWithContext(ctx, http.MethodGet, c.base.String()+path, nil)
	req.Header.Set(operatorIDHeader, c.operatorID)
	if c.token != "" && (!c.machineLocal || isLoopback(c.base)) {
		req.Header.Set("Authorization", "Bearer "+c.token)
	}
	req.Header.Set("Accept", "application/json")
	resp, err := c.http.Do(req)
	if err != nil {
		return nil, "", errors.New("error: Mohist Server request failed [service_unavailable]")
	}
	defer resp.Body.Close()
	var env envelope
	if err = json.NewDecoder(resp.Body).Decode(&env); err != nil {
		return nil, "", errors.New("error: Mohist Server returned malformed JSON [invalid_response]")
	}
	success := (env.Success == nil && resp.StatusCode >= 200 && resp.StatusCode < 300) || (env.Success != nil && *env.Success)
	if !success {
		return nil, env.Code, &operationError{message: "error: " + env.Error + " [" + env.Code + "]"}
	}
	return env.Data, "", nil
}

func isLoopback(value *url.URL) bool {
	host := strings.ToLower(value.Hostname())
	return host == "localhost" || host == "127.0.0.1" || host == "::1"
}
