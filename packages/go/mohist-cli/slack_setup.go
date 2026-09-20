package mohistcli

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"net/http"
	"net/url"
	"path/filepath"
	"strings"
)

const defaultSlackCredentialsName = "slack-credentials.json"

type slackCredentials struct {
	ConfigurationAccessToken  string `json:"configurationAccessToken"`
	ConfigurationRefreshToken string `json:"configurationRefreshToken"`
	BotToken                  string `json:"botToken"`
	AppLevelToken             string `json:"appLevelToken"`
}

type slackSetupProgress struct {
	NextAction string `json:"nextAction"`
}

func runSlackSetup(ctx context.Context, deps Dependencies, client *client, cmd command) int {
	progress, err := client.request(ctx, http.MethodGet, "/api/slack-manager/setup/progress", nil)
	if err != nil && !isOperationCode(err, "not_found") {
		return operationExit(deps, ctx, err)
	}

	var credentials slackCredentials
	credentialsLoaded := false
	if err != nil {
		credentials, credentialsLoaded, err = loadSlackCredentials(deps, cmd, true)
		if err != nil {
			return operationExit(deps, ctx, err)
		}
		if !credentials.hasConfigurationPair() {
			return slackCredentialsError(deps, ctx,
				"configurationAccessToken and configurationRefreshToken are required to start Slack setup")
		}
		progress, err = client.request(ctx, http.MethodPost, "/api/slack-manager/setup/configuration", map[string]any{
			"configurationAccessToken":  credentials.ConfigurationAccessToken,
			"configurationRefreshToken": credentials.ConfigurationRefreshToken,
		})
		if err != nil {
			return operationExit(deps, ctx, err)
		}
	}

	nextAction, err := decodeSlackNextAction(progress)
	if err != nil {
		return operationExit(deps, ctx, err)
	}
	if nextAction == "supply_configuration" {
		if !credentialsLoaded {
			credentials, credentialsLoaded, err = loadSlackCredentials(deps, cmd, true)
			if err != nil {
				return operationExit(deps, ctx, err)
			}
		}
		if !credentials.hasConfigurationPair() {
			return slackCredentialsError(deps, ctx,
				"configurationAccessToken and configurationRefreshToken are required to continue Slack setup")
		}
		progress, err = client.request(ctx, http.MethodPost, "/api/slack-manager/setup/configuration", map[string]any{
			"configurationAccessToken":  credentials.ConfigurationAccessToken,
			"configurationRefreshToken": credentials.ConfigurationRefreshToken,
		})
		if err != nil {
			return operationExit(deps, ctx, err)
		}
		nextAction, err = decodeSlackNextAction(progress)
		if err != nil {
			return operationExit(deps, ctx, err)
		}
	}

	if isSlackSetupResumeAction(nextAction) {
		progress, err = client.request(ctx, http.MethodPost, "/api/slack-manager/setup/resume", nil)
		if err != nil {
			return operationExit(deps, ctx, err)
		}
		nextAction, err = decodeSlackNextAction(progress)
		if err != nil {
			return operationExit(deps, ctx, err)
		}
	}

	if nextAction == "approve_install" || nextAction == "supply_runtime_credentials" {
		if !credentialsLoaded {
			credentials, credentialsLoaded, err = loadSlackCredentials(deps, cmd, false)
			if err != nil {
				return operationExit(deps, ctx, err)
			}
		}
		if credentialsLoaded && credentials.hasRuntimePair() {
			progress, err = client.request(ctx, http.MethodPost, "/api/slack-manager/setup/runtime-credentials", map[string]any{
				"botToken":      credentials.BotToken,
				"appLevelToken": credentials.AppLevelToken,
			})
			if err != nil {
				return operationExit(deps, ctx, err)
			}
		}
	}

	return writeSlackOperationResult(deps, cmd, progress)
}

func isSlackSetupResumeAction(nextAction string) bool {
	return nextAction == "create_app" || nextAction == "reconcile_create" || nextAction == "apply_manifest"
}

func runSlackInstallAgent(
	ctx context.Context,
	deps Dependencies,
	client *client,
	cmd command,
	project string,
) int {
	agentID := strings.TrimSpace(argValue(cmd.args, "agent", ""))
	path := "/api/projects/" + url.PathEscape(project) + "/slack-manager/install-agent"
	progress, err := client.request(ctx, http.MethodPost, path, map[string]any{"agentId": agentID})
	if err != nil {
		return operationExit(deps, ctx, err)
	}
	nextAction, err := decodeSlackNextAction(progress)
	if err != nil {
		return operationExit(deps, ctx, err)
	}
	if nextAction != "approve_install" && nextAction != "provide_credentials" {
		return writeSlackOperationResult(deps, cmd, progress)
	}

	credentials, loaded, err := loadSlackCredentials(deps, cmd, false)
	if err != nil {
		return operationExit(deps, ctx, err)
	}
	if !loaded || !credentials.hasRuntimePair() {
		return writeSlackOperationResult(deps, cmd, progress)
	}

	_, err = client.request(ctx, http.MethodPost, path+"/credentials", map[string]any{
		"agentId":       agentID,
		"botToken":      credentials.BotToken,
		"appLevelToken": credentials.AppLevelToken,
	})
	if err != nil {
		return operationExit(deps, ctx, err)
	}
	progress, err = client.request(ctx, http.MethodPost, path, map[string]any{"agentId": agentID})
	if err != nil {
		return operationExit(deps, ctx, err)
	}
	return writeSlackOperationResult(deps, cmd, progress)
}

func loadSlackCredentials(deps Dependencies, cmd command, required bool) (slackCredentials, bool, error) {
	path, explicit, err := slackCredentialsPath(deps, cmd)
	if err != nil {
		return slackCredentials{}, false, err
	}
	text, readErr := deps.ReadFile(path)
	if readErr != nil {
		if !required && !explicit && errors.Is(readErr, fs.ErrNotExist) {
			return slackCredentials{}, false, nil
		}
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
	if credentials.hasPartialConfigurationPair() || credentials.hasPartialRuntimePair() {
		return slackCredentials{}, false, invalidSlackCredentialsFile(path)
	}
	return credentials, true, nil
}

func slackCredentialsPath(deps Dependencies, cmd command) (string, bool, error) {
	if hasArg(cmd.args, "credentials-file") {
		return strings.TrimSpace(argValue(cmd.args, "credentials-file", "")), true, nil
	}
	home, err := deps.HomeDir()
	if err != nil || strings.TrimSpace(home) == "" {
		return "", false, &operationError{
			message: "error: home directory could not be resolved [credentials_file_unavailable]",
		}
	}
	return filepath.Join(home, ".mohist", defaultSlackCredentialsName), false, nil
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

func decodeSlackNextAction(data json.RawMessage) (string, error) {
	var progress slackSetupProgress
	if json.Unmarshal(data, &progress) != nil || strings.TrimSpace(progress.NextAction) == "" {
		return "", &operationError{message: "error: Slack setup response has an invalid shape [invalid_response]"}
	}
	return progress.NextAction, nil
}

func writeSlackOperationResult(deps Dependencies, cmd command, data json.RawMessage) int {
	data = redactIntegrationSecrets(data)
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
	if len(data) == 0 {
		return ExitOK
	}
	_, _ = deps.Stdout.Write(append(data, '\n'))
	return ExitOK
}

func isOperationCode(err error, code string) bool {
	var operation *operationError
	return errors.As(err, &operation) && operation.code == code
}

func slackCredentialsError(deps Dependencies, ctx context.Context, message string) int {
	return operationExit(deps, ctx, &operationError{message: "error: " + message + " [credentials_required]"})
}
