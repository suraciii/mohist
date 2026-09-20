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
	"strings"
	"syscall"
)

// The setup projection is Server-owned: one phase, one primary action, the
// install URL, one human summary, and one error class. The terminal renders it
// and never derives a competing next action.
type slackSetupProjection struct {
	Phase         string `json:"phase"`
	PrimaryAction string `json:"primaryAction"`
	InstallURL    string `json:"installUrl"`
	Summary       string `json:"summary"`
	ErrorClass    string `json:"errorClass"`
}

type slackCredentials struct {
	ConfigurationAccessToken  string `json:"configurationAccessToken"`
	ConfigurationRefreshToken string `json:"configurationRefreshToken"`
	BotToken                  string `json:"botToken"`
	AppLevelToken             string `json:"appLevelToken"`
}

const (
	slackActionSupplyConfiguration      = "supply_configuration"
	slackActionApproveInstall           = "approve_install"
	slackActionSupplyRuntimeCredentials = "supply_runtime_credentials"
	slackActionProvideCredentials       = "provide_credentials"
	slackActionRerunSetup               = "rerun_setup"
	slackPhaseReady                     = "ready"
)

// slackCredentialStep names the pair one step needs, so the guide collects only
// that pair and reports the same step when the input is missing.
type slackCredentialStep struct {
	action string
	name   string
	labels [2]string
}

var (
	slackManagerConfigurationStep = slackCredentialStep{
		action: slackActionSupplyConfiguration,
		name:   "the Slack Configuration access and refresh tokens",
		labels: [2]string{"Slack Configuration access token", "Slack Configuration refresh token"},
	}
	slackManagerRuntimeStep = slackCredentialStep{
		action: slackActionSupplyRuntimeCredentials,
		name:   "the Mohist App Bot token and App-level token",
		labels: [2]string{"Mohist App Bot token", "Mohist App App-level token"},
	}
	slackAgentRuntimeStep = slackCredentialStep{
		action: slackActionProvideCredentials,
		name:   "the Agent App Bot token and App-level token",
		labels: [2]string{"Agent App Bot token", "Agent App App-level token"},
	}
)

func runSlackSetup(ctx context.Context, deps Dependencies, client *client, cmd command) int {
	selector := slackWorkspaceSelector(cmd)
	progress, err := client.request(ctx, http.MethodGet, slackSetupPath("progress", selector), nil)
	if err != nil && !isOperationCode(err, "not_found") {
		return slackSetupFailure(deps, ctx, err)
	}
	started := err == nil

	credentials, explicit, err := loadSlackCredentials(deps, cmd)
	if err != nil {
		return operationExit(deps, ctx, err)
	}

	action := slackSetupAction(progress)
	if !started {
		action = slackActionSupplyConfiguration
	}
	submittedConfiguration, submittedRuntime := false, false

	if action == slackActionSupplyConfiguration {
		if !credentials.hasConfigurationPair() {
			prompted, supplied, promptErr := promptSlackCredentials(deps, cmd, slackManagerConfigurationStep)
			if promptErr != nil {
				return operationExit(deps, ctx, promptErr)
			}
			if !supplied {
				return slackSetupStopped(deps, ctx, cmd, progress, selector, slackManagerConfigurationStep)
			}
			credentials = prompted
		}
		progress, err = client.request(ctx, http.MethodPost, slackSetupPath("configuration", selector), map[string]any{
			"configurationAccessToken":  credentials.ConfigurationAccessToken,
			"configurationRefreshToken": credentials.ConfigurationRefreshToken,
		})
		if err != nil {
			return slackSetupFailure(deps, ctx, err)
		}
		submittedConfiguration = true
		action = slackSetupAction(progress)
	}

	if action == slackActionRerunSetup {
		progress, err = client.request(ctx, http.MethodPost, slackSetupPath("resume", selector), nil)
		if err != nil {
			return slackSetupFailure(deps, ctx, err)
		}
		action = slackSetupAction(progress)
	}

	if action == slackActionSupplyRuntimeCredentials {
		if !credentials.hasRuntimePair() {
			prompted, supplied, promptErr := promptSlackCredentials(deps, cmd, slackManagerRuntimeStep)
			if promptErr != nil {
				return operationExit(deps, ctx, promptErr)
			}
			if !supplied {
				return slackSetupStopped(deps, ctx, cmd, progress, selector, slackManagerRuntimeStep)
			}
			credentials = prompted
		}
		progress, err = client.request(ctx, http.MethodPost, slackSetupPath("runtime-credentials", selector), map[string]any{
			"botToken":      credentials.BotToken,
			"appLevelToken": credentials.AppLevelToken,
		})
		if err != nil {
			return slackSetupFailure(deps, ctx, err)
		}
		submittedRuntime = true
	}

	// A ready installation accepts an explicit replacement pair as rotation. A
	// rerun with no new input changes nothing and never resubmits a pair an
	// earlier step already consumed.
	if explicit && slackSetupPhase(progress) == slackPhaseReady {
		if !submittedConfiguration && credentials.hasConfigurationPair() {
			progress, err = client.request(ctx, http.MethodPost, slackSetupPath("configuration", selector), map[string]any{
				"configurationAccessToken":  credentials.ConfigurationAccessToken,
				"configurationRefreshToken": credentials.ConfigurationRefreshToken,
			})
			if err != nil {
				return slackSetupFailure(deps, ctx, err)
			}
			submittedConfiguration = true
		}
		if !submittedRuntime && slackSetupPhase(progress) == slackPhaseReady && credentials.hasRuntimePair() {
			progress, err = client.request(ctx, http.MethodPost, slackSetupPath("runtime-credentials", selector), map[string]any{
				"botToken":      credentials.BotToken,
				"appLevelToken": credentials.AppLevelToken,
			})
			if err != nil {
				return slackSetupFailure(deps, ctx, err)
			}
		}
	}

	return writeSlackSetupResult(deps, cmd, progress)
}

func runSlackStatus(ctx context.Context, deps Dependencies, client *client, cmd command) int {
	selector := slackWorkspaceSelector(cmd)
	progress, err := client.request(ctx, http.MethodGet, slackSetupPath("progress", selector), nil)
	if err != nil {
		if !isOperationCode(err, "not_found") {
			return slackSetupFailure(deps, ctx, err)
		}
		// A host that never started setup is truthfully incomplete rather than a
		// failed read: status reports the first step and succeeds.
		progress = slackSetupNotStartedProjection()
	}
	return writeSlackSetupResult(deps, cmd, progress)
}

func runSlackInstallAgent(
	ctx context.Context,
	deps Dependencies,
	client *client,
	cmd command,
	project string,
) int {
	agentID := strings.TrimSpace(argValue(cmd.args, "agent", ""))
	selector := slackWorkspaceSelector(cmd)
	if selector != "" {
		// The selector resolves before any write, so a selector naming no
		// enrolled Workspace can never fall back to the first enrolled record.
		if _, err := client.request(ctx, http.MethodGet, slackSetupPath("progress", selector), nil); err != nil {
			return slackSetupFailure(deps, ctx, err)
		}
	}
	path := "/api/projects/" + url.PathEscape(project) + "/slack-manager/install-agent"
	progress, err := client.request(ctx, http.MethodPost, path, map[string]any{"agentId": agentID})
	if err != nil {
		return slackSetupFailure(deps, ctx, err)
	}
	nextAction, err := decodeSlackNextAction(progress)
	if err != nil {
		return operationExit(deps, ctx, err)
	}
	if nextAction != slackActionProvideCredentials {
		return writeSlackInstallResult(deps, cmd, progress, agentID, project, selector, nextAction)
	}

	credentials, _, err := loadSlackCredentials(deps, cmd)
	if err != nil {
		return operationExit(deps, ctx, err)
	}
	if !credentials.hasRuntimePair() {
		prompted, supplied, promptErr := promptSlackCredentials(deps, cmd, slackAgentRuntimeStep)
		if promptErr != nil {
			return operationExit(deps, ctx, promptErr)
		}
		if !supplied {
			return slackInstallStopped(deps, ctx, cmd, progress, agentID, project, selector, slackAgentRuntimeStep)
		}
		credentials = prompted
	}

	_, err = client.request(ctx, http.MethodPost, path+"/credentials", map[string]any{
		"agentId":       agentID,
		"botToken":      credentials.BotToken,
		"appLevelToken": credentials.AppLevelToken,
	})
	if err != nil {
		return slackSetupFailure(deps, ctx, err)
	}
	progress, err = client.request(ctx, http.MethodPost, path, map[string]any{"agentId": agentID})
	if err != nil {
		return slackSetupFailure(deps, ctx, err)
	}
	nextAction, err = decodeSlackNextAction(progress)
	if err != nil {
		return operationExit(deps, ctx, err)
	}
	return writeSlackInstallResult(deps, cmd, progress, agentID, project, selector, nextAction)
}

// Only an explicit --credentials-file supplies credentials: no shared default
// file is read, so an automation path never becomes the normal journey.
func loadSlackCredentials(deps Dependencies, cmd command) (slackCredentials, bool, error) {
	if !hasArg(cmd.args, "credentials-file") {
		return slackCredentials{}, false, nil
	}
	path := strings.TrimSpace(argValue(cmd.args, "credentials-file", ""))
	if err := checkSlackCredentialsFile(deps, path); err != nil {
		return slackCredentials{}, false, err
	}
	text, err := deps.ReadFile(path)
	if err != nil {
		return slackCredentials{}, false, &operationError{
			message: fmt.Sprintf("error: could not read Slack credentials file %q [credentials_file_unavailable]", path),
		}
	}
	decoder := json.NewDecoder(strings.NewReader(text))
	decoder.DisallowUnknownFields()
	var credentials slackCredentials
	if decoder.Decode(&credentials) != nil {
		return slackCredentials{}, false, invalidSlackCredentialsFile(path)
	}
	if decodeErr := decoder.Decode(&struct{}{}); !errors.Is(decodeErr, io.EOF) {
		return slackCredentials{}, false, invalidSlackCredentialsFile(path)
	}
	credentials.trim()
	if !credentials.hasConfigurationPair() && !credentials.hasRuntimePair() {
		return slackCredentials{}, false, invalidSlackCredentialsFile(path)
	}
	if credentials.hasPartialConfigurationPair() || credentials.hasPartialRuntimePair() {
		return slackCredentials{}, false, invalidSlackCredentialsFile(path)
	}
	return credentials, true, nil
}

// The permission check runs before any content is read: a credentials file that
// another local account can read or replace is refused instead of parsed.
func checkSlackCredentialsFile(deps Dependencies, path string) error {
	info, err := deps.StatFile(path)
	if err != nil || !info.Mode().IsRegular() || info.Mode().Perm() != 0o600 || !slackCredentialsFileOwned(info) {
		return &operationError{
			message: fmt.Sprintf(
				"error: Slack credentials file %q must be a regular file owned by the current user with mode 0600 [credentials_file_unavailable]",
				path),
		}
	}
	return nil
}

func slackCredentialsFileOwned(info os.FileInfo) bool {
	stat, ok := info.Sys().(*syscall.Stat_t)
	return !ok || stat.Uid == uint32(os.Getuid())
}

func (credentials *slackCredentials) trim() {
	credentials.ConfigurationAccessToken = strings.TrimSpace(credentials.ConfigurationAccessToken)
	credentials.ConfigurationRefreshToken = strings.TrimSpace(credentials.ConfigurationRefreshToken)
	credentials.BotToken = strings.TrimSpace(credentials.BotToken)
	credentials.AppLevelToken = strings.TrimSpace(credentials.AppLevelToken)
}

func (credentials slackCredentials) hasConfigurationPair() bool {
	return credentials.ConfigurationAccessToken != "" && credentials.ConfigurationRefreshToken != ""
}

func (credentials slackCredentials) hasRuntimePair() bool {
	return credentials.BotToken != "" && credentials.AppLevelToken != ""
}

func (credentials slackCredentials) hasPartialConfigurationPair() bool {
	return (credentials.ConfigurationAccessToken == "") != (credentials.ConfigurationRefreshToken == "")
}

func (credentials slackCredentials) hasPartialRuntimePair() bool {
	return (credentials.BotToken == "") != (credentials.AppLevelToken == "")
}

func invalidSlackCredentialsFile(path string) error {
	return &operationError{
		message: fmt.Sprintf(
			"error: Slack credentials file %q must be one JSON object containing only configurationAccessToken, configurationRefreshToken, botToken, and appLevelToken pairs [invalid_credentials_file]",
			path),
	}
}

// promptSlackCredentials reads the pair one step needs through hidden input. It
// reports a missing pair instead of blocking, so a non-interactive caller never
// waits for a terminal.
func promptSlackCredentials(deps Dependencies, cmd command, step slackCredentialStep) (slackCredentials, bool, error) {
	if cmd.fieldsOnly || len(cmd.fields) > 0 || !deps.TerminalInteractive() {
		return slackCredentials{}, false, nil
	}
	first, err := deps.ReadSecretLine(step.labels[0] + ": ")
	if err != nil {
		return slackCredentials{}, false, &operationError{message: "error: " + err.Error() + " [credentials_required]"}
	}
	second, err := deps.ReadSecretLine(step.labels[1] + ": ")
	if err != nil {
		return slackCredentials{}, false, &operationError{message: "error: " + err.Error() + " [credentials_required]"}
	}
	credentials := slackCredentials{}
	if step.action == slackActionSupplyConfiguration {
		credentials.ConfigurationAccessToken = first
		credentials.ConfigurationRefreshToken = second
	} else {
		credentials.BotToken = first
		credentials.AppLevelToken = second
	}
	credentials.trim()
	if !credentials.hasConfigurationPair() && !credentials.hasRuntimePair() {
		return slackCredentials{}, false, nil
	}
	return credentials, true, nil
}

// A missing required input stops the guide with the current stage, the exact
// continuation, and a nonzero exit.
func slackSetupStopped(
	deps Dependencies,
	ctx context.Context,
	cmd command,
	progress json.RawMessage,
	selector string,
	step slackCredentialStep,
) int {
	if len(progress) == 0 {
		progress = slackSetupNotStartedProjection()
	}
	writeSlackStoppedProjection(deps, cmd, redactIntegrationSecrets(progress), func(out io.Writer, data json.RawMessage) {
		writeSlackSetupSummary(out, data, "")
	})
	fmt.Fprintln(deps.Stderr, "continue: "+slackSetupContinuation(selector, true))
	return operationExit(deps, ctx, &operationError{
		message: fmt.Sprintf(
			"error: Slack setup needs %s; pass --credentials-file <path> or rerun in an interactive terminal [credentials_required]",
			step.name),
		code: "credentials_required",
	})
}

func slackInstallStopped(
	deps Dependencies,
	ctx context.Context,
	cmd command,
	progress json.RawMessage,
	agentID, project, selector string,
	step slackCredentialStep,
) int {
	writeSlackStoppedProjection(deps, cmd, redactIntegrationSecrets(progress), func(out io.Writer, data json.RawMessage) {
		writeSlackInstallSummary(out, data, "")
	})
	fmt.Fprintln(deps.Stderr, "continue: "+slackInstallContinuation(agentID, project, selector, true))
	return operationExit(deps, ctx, &operationError{
		message: fmt.Sprintf(
			"error: Slack install-agent needs %s; pass --credentials-file <path> or rerun in an interactive terminal [credentials_required]",
			step.name),
		code: "credentials_required",
	})
}

// A structured-output caller keeps the machine projection of the stopped step;
// a human caller reads the stage on stderr. Both then read the continuation.
func writeSlackStoppedProjection(
	deps Dependencies,
	cmd command,
	data json.RawMessage,
	human func(io.Writer, json.RawMessage),
) {
	switch {
	case len(cmd.fields) > 0:
		if selected, err := SelectFields(data, cmd.fields, false); err == nil {
			writeJSON(deps.Stdout, selected)
		}
	case cmd.fieldsOnly:
		if len(data) > 0 {
			fmt.Fprintln(deps.Stdout, string(data))
		}
	default:
		human(deps.Stderr, data)
	}
}

func slackSetupFailure(deps Dependencies, ctx context.Context, err error) int {
	if isOperationCode(err, "workspace_selection_required") {
		writeSlackWorkspaceChoices(deps, err)
	}
	return operationExit(deps, ctx, err)
}

// An ambiguous selection fails closed: the error names the selector and the
// enrolled Workspaces it accepts, and no record is chosen for the caller.
func writeSlackWorkspaceChoices(deps Dependencies, err error) {
	var operation *operationError
	if !errors.As(err, &operation) {
		return
	}
	var choices []struct {
		TeamID string `json:"teamId"`
		Name   string `json:"name"`
		Phase  string `json:"phase"`
	}
	if json.Unmarshal(operation.details, &choices) != nil {
		return
	}
	fmt.Fprintln(deps.Stderr, "enrolled Workspaces:")
	for _, choice := range choices {
		fmt.Fprintf(deps.Stderr, "  --workspace-team %s  %s (%s)\n", choice.TeamID, choice.Name, choice.Phase)
	}
}

// A setup that never started projects no phase, so the first credential step is
// the reported stage.
func slackSetupNotStartedProjection() json.RawMessage {
	data, err := json.Marshal(slackSetupProjection{
		Phase:         "not_started",
		PrimaryAction: slackActionSupplyConfiguration,
		Summary:       "Slack setup has not started for this Workspace.",
	})
	if err != nil {
		return nil
	}
	return data
}

func writeSlackSetupResult(deps Dependencies, cmd command, progress json.RawMessage) int {
	data := redactIntegrationSecrets(progress)
	if cmd.fieldsOnly {
		for _, field := range cmd.catalog {
			fmt.Fprintln(deps.Stdout, field)
		}
		return ExitOK
	}
	if len(cmd.fields) > 0 {
		selected, err := SelectFields(data, cmd.fields, false)
		if err != nil {
			writeError(deps.Stderr, err)
			return ExitOperation
		}
		return writeJSON(deps.Stdout, selected)
	}
	continuation := ""
	if slackSetupPhase(data) != slackPhaseReady {
		continuation = slackSetupContinuation(slackWorkspaceSelector(cmd), false)
	}
	writeSlackSetupSummary(deps.Stdout, data, continuation)
	return ExitOK
}

// The human projection states the current stage, the one next action, and the
// exact continuation. Internal protocol work never becomes a human task.
func writeSlackSetupSummary(out io.Writer, data json.RawMessage, continuation string) {
	var projection slackSetupProjection
	if json.Unmarshal(data, &projection) != nil || strings.TrimSpace(projection.Phase) == "" {
		return
	}
	summary := strings.TrimSpace(projection.Summary)
	if summary == "" {
		summary = "Slack setup phase: " + projection.Phase
	}
	fmt.Fprintln(out, summary)
	if text := slackSetupNextActionText(projection.PrimaryAction); text != "" {
		fmt.Fprintln(out, "next: "+text)
	}
	if installURL := strings.TrimSpace(projection.InstallURL); installURL != "" {
		fmt.Fprintln(out, "install: "+installURL)
	}
	if class := strings.TrimSpace(projection.ErrorClass); class != "" {
		fmt.Fprintln(out, "reason: "+class)
	}
	if continuation != "" {
		fmt.Fprintln(out, "continue: "+continuation)
	}
}

func slackSetupNextActionText(action string) string {
	switch action {
	case slackActionSupplyConfiguration:
		return "supply the Slack Configuration access and refresh tokens"
	case slackActionApproveInstall:
		return "approve the Mohist App installation in Slack"
	case slackActionSupplyRuntimeCredentials:
		return "supply the Mohist App Bot token and App-level token"
	case "await_socket_verification":
		return "wait for Mohist to verify the Socket identity"
	case slackActionRerunSetup:
		return "rerun the guide to continue"
	case slackPhaseReady:
		return "the Workspace is ready"
	default:
		return ""
	}
}

func writeSlackInstallResult(
	deps Dependencies,
	cmd command,
	progress json.RawMessage,
	agentID, project, selector, nextAction string,
) int {
	data := redactIntegrationSecrets(progress)
	if cmd.fieldsOnly {
		for _, field := range cmd.catalog {
			fmt.Fprintln(deps.Stdout, field)
		}
		return ExitOK
	}
	if len(cmd.fields) > 0 {
		selected, err := SelectFields(data, cmd.fields, false)
		if err != nil {
			writeError(deps.Stderr, err)
			return ExitOperation
		}
		return writeJSON(deps.Stdout, selected)
	}
	continuation := ""
	if nextAction != slackPhaseReady {
		continuation = slackInstallContinuation(agentID, project, selector, false)
	}
	writeSlackInstallSummary(deps.Stdout, data, continuation)
	return ExitOK
}

func writeSlackInstallSummary(out io.Writer, data json.RawMessage, continuation string) {
	var projection struct {
		AgentApp struct {
			InstallURL string `json:"installUrl"`
		} `json:"agentApp"`
		NextAction string `json:"nextAction"`
		ErrorClass string `json:"errorClass"`
	}
	if json.Unmarshal(data, &projection) != nil {
		return
	}
	fmt.Fprintln(out, "next: "+slackInstallNextActionText(projection.NextAction))
	if installURL := strings.TrimSpace(projection.AgentApp.InstallURL); installURL != "" {
		fmt.Fprintln(out, "install: "+installURL)
	}
	if class := strings.TrimSpace(projection.ErrorClass); class != "" {
		fmt.Fprintln(out, "reason: "+class)
	}
	if continuation != "" {
		fmt.Fprintln(out, "continue: "+continuation)
	}
}

func slackInstallNextActionText(action string) string {
	switch action {
	case slackActionApproveInstall:
		return "approve the Agent App installation in Slack"
	case slackActionProvideCredentials:
		return "supply the Agent App Bot token and App-level token"
	case slackPhaseReady:
		return "the Agent App is ready"
	default:
		return "rerun the guide to continue"
	}
}

func slackSetupContinuation(selector string, credentialsFile bool) string {
	command := "mo slack setup"
	if selector != "" {
		command += " --workspace-team " + selector
	}
	if credentialsFile {
		command += " --credentials-file <path>"
	}
	return command
}

func slackInstallContinuation(agentID, project, selector string, credentialsFile bool) string {
	command := "mo slack install-agent " + agentID
	if project != "" {
		command += " --project " + project
	}
	if selector != "" {
		command += " --workspace-team " + selector
	}
	if credentialsFile {
		command += " --credentials-file <path>"
	}
	return command
}

func slackSetupPath(action, selector string) string {
	path := "/api/slack-manager/setup/" + action
	if selector != "" {
		path += "?workspaceTeamId=" + url.QueryEscape(selector)
	}
	return path
}

func slackWorkspaceSelector(cmd command) string {
	return strings.TrimSpace(argValue(cmd.args, "workspace-team", ""))
}

func slackSetupAction(data json.RawMessage) string {
	var projection slackSetupProjection
	if json.Unmarshal(data, &projection) != nil {
		return ""
	}
	return strings.TrimSpace(projection.PrimaryAction)
}

func slackSetupPhase(data json.RawMessage) string {
	var projection slackSetupProjection
	if json.Unmarshal(data, &projection) != nil {
		return ""
	}
	return strings.TrimSpace(projection.Phase)
}

func decodeSlackNextAction(data json.RawMessage) (string, error) {
	var progress struct {
		NextAction string `json:"nextAction"`
	}
	if json.Unmarshal(data, &progress) != nil || strings.TrimSpace(progress.NextAction) == "" {
		return "", &operationError{message: "error: Slack setup response has an invalid shape [invalid_response]"}
	}
	return progress.NextAction, nil
}

func isOperationCode(err error, code string) bool {
	var operation *operationError
	return errors.As(err, &operation) && operation.code == code
}
