package mohistcli

import (
	"context"
	"encoding/json"
	"io"
	"net/http"
	"os"
	"strings"
	"testing"
	"time"
)

type capturedSlackRequest struct {
	method string
	path   string
	query  string
	body   map[string]any
}

type slackFileInfo struct{ mode os.FileMode }

func (info slackFileInfo) Name() string { return "slack-credentials.json" }
func (info slackFileInfo) Size() int64  { return 0 }
func (info slackFileInfo) Mode() os.FileMode {
	return info.mode
}
func (info slackFileInfo) ModTime() time.Time { return time.Time{} }
func (info slackFileInfo) IsDir() bool        { return false }
func (info slackFileInfo) Sys() any           { return nil }

// slackFileStub stands in for the credentials file: the permission check reads
// the metadata first, so a refused file records no content read.
type slackFileStub struct {
	contents string
	mode     os.FileMode
	reads    int
}

func (stub *slackFileStub) read(string) (string, error) {
	stub.reads++
	return stub.contents, nil
}

func (stub *slackFileStub) stat(string) (os.FileInfo, error) {
	return slackFileInfo{mode: stub.mode}, nil
}

// slackPrompts replaces hidden terminal input: the guide reads one value per
// prompt and never sees a terminal.
func slackPrompts(answers ...string) (func() bool, func(string) (string, error), *[]string) {
	prompts := &[]string{}
	index := 0
	return func() bool { return true },
		func(prompt string) (string, error) {
			*prompts = append(*prompts, prompt)
			if index >= len(answers) {
				return "", nil
			}
			answer := answers[index]
			index++
			return answer, nil
		}, prompts
}

// slackAgentRead answers the Agent resolution the guide performs before any
// write: the caller's Project-scoped name or ID resolves to the stored Agent
// the Server installs, so no Connection, App, or Enrollment ID is ever typed.
func slackAgentRead(id string) *http.Response {
	return response(http.StatusOK, `{"success":true,"data":{"id":"`+id+`","name":"reviewer","status":"active"}}`)
}

func TestSlackSetupStartsFromOneExplicitCredentialsFileWithoutExposingSecrets(t *testing.T) {
	requests := []capturedSlackRequest{}
	deps, out, errOut := testDeps(roundTripFunc(func(request *http.Request) (*http.Response, error) {
		requests = append(requests, captureSlackRequest(t, request))
		switch len(requests) {
		case 1:
			return response(http.StatusNotFound, `{"success":false,"error":"Setup has not started.","code":"not_found"}`), nil
		case 2:
			return response(http.StatusOK, `{"success":true,"data":{"phase":"awaiting_install","primaryAction":"approve_install","installUrl":"https://api.slack.com/install","summary":"Workspace T123: The Mohist App installation needs approval in Slack.","errorClass":null}}`), nil
		default:
			t.Fatalf("unexpected request %s %s", request.Method, request.URL.Path)
			return nil, nil
		}
	}), map[string]string{"MOHIST_TOKEN": "operator"})
	file := &slackFileStub{contents: `{"configurationAccessToken":"xoxe-secret","configurationRefreshToken":"xoxr-secret"}`, mode: 0o600}
	deps.ReadFile, deps.StatFile = file.read, file.stat

	code := Run(context.Background(), []string{"slack", "setup", "--credentials-file", "/secure/slack.json"}, deps)

	if code != ExitOK || errOut.Len() != 0 {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if len(requests) != 2 || requests[0].method != http.MethodGet || requests[0].path != "/api/slack-manager/setup/progress" || requests[1].path != "/api/slack-manager/setup/configuration" {
		t.Fatalf("requests=%+v", requests)
	}
	if requests[1].body["configurationAccessToken"] != "xoxe-secret" || requests[1].body["configurationRefreshToken"] != "xoxr-secret" || len(requests[1].body) != 2 {
		t.Fatalf("configuration body=%v", requests[1].body)
	}
	if !strings.Contains(out.String(), "approve the Mohist App installation in Slack") || !strings.Contains(out.String(), "https://api.slack.com/install") {
		t.Fatalf("human summary=%q", out.String())
	}
	if strings.Contains(out.String(), "xoxe-secret") || strings.Contains(out.String(), "xoxr-secret") || strings.Contains(errOut.String(), "xoxe-secret") {
		t.Fatalf("secret leaked: stdout=%q stderr=%q", out.String(), errOut.String())
	}
}

func TestSlackSetupPromptsHiddenInputForTheMissingConfigurationPair(t *testing.T) {
	requests := []capturedSlackRequest{}
	deps, out, errOut := testDeps(roundTripFunc(func(request *http.Request) (*http.Response, error) {
		requests = append(requests, captureSlackRequest(t, request))
		if len(requests) == 1 {
			return response(http.StatusNotFound, `{"success":false,"error":"Setup has not started.","code":"not_found"}`), nil
		}
		return response(http.StatusOK, `{"success":true,"data":{"phase":"awaiting_install","primaryAction":"approve_install","installUrl":"https://api.slack.com/install","summary":"Workspace T123: The Mohist App installation needs approval in Slack."}}`), nil
	}), map[string]string{"MOHIST_TOKEN": "operator"})
	file := &slackFileStub{mode: 0o600}
	deps.ReadFile, deps.StatFile = file.read, file.stat
	interactive, readSecret, prompts := slackPrompts("xoxe-typed", "xoxr-typed")
	deps.TerminalInteractive = interactive
	deps.ReadSecretLine = readSecret

	code := Run(context.Background(), []string{"slack", "setup"}, deps)

	if code != ExitOK || errOut.Len() != 0 {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if len(*prompts) != 2 || !strings.Contains((*prompts)[0], "Configuration access token") || !strings.Contains((*prompts)[1], "Configuration refresh token") {
		t.Fatalf("prompts=%v", *prompts)
	}
	if len(requests) != 2 || requests[1].body["configurationAccessToken"] != "xoxe-typed" || requests[1].body["configurationRefreshToken"] != "xoxr-typed" {
		t.Fatalf("requests=%+v", requests)
	}
	if strings.Contains(out.String(), "xoxe-typed") || strings.Contains(errOut.String(), "xoxe-typed") {
		t.Fatalf("secret leaked: stdout=%q stderr=%q", out.String(), errOut.String())
	}
}

func TestSlackSetupNonInteractiveReportsTheStepAndContinuationWithoutPrompting(t *testing.T) {
	requests := []capturedSlackRequest{}
	deps, out, errOut := testDeps(roundTripFunc(func(request *http.Request) (*http.Response, error) {
		requests = append(requests, captureSlackRequest(t, request))
		return response(http.StatusNotFound, `{"success":false,"error":"Setup has not started.","code":"not_found"}`), nil
	}), map[string]string{"MOHIST_TOKEN": "operator"})
	file := &slackFileStub{mode: 0o600}
	deps.ReadFile, deps.StatFile = file.read, file.stat
	interactive, readSecret, prompts := slackPrompts("must-not-be-read")
	deps.TerminalInteractive = interactive
	deps.ReadSecretLine = readSecret
	deps.TerminalInteractive = func() bool { return false }

	code := Run(context.Background(), []string{"slack", "setup"}, deps)

	if code != ExitOperation || len(*prompts) != 0 || file.reads != 0 {
		t.Fatalf("code=%d prompts=%v reads=%d", code, *prompts, file.reads)
	}
	if len(requests) != 1 || requests[0].method != http.MethodGet {
		t.Fatalf("requests=%+v", requests)
	}
	if out.Len() != 0 || !strings.Contains(errOut.String(), "credentials_required") {
		t.Fatalf("stdout=%q stderr=%q", out.String(), errOut.String())
	}
	if !strings.Contains(errOut.String(), "continue: mo slack setup --credentials-file <path>") {
		t.Fatalf("continuation missing: stderr=%q", errOut.String())
	}
}

func TestSlackSetupStructuredOutputNeverPrompts(t *testing.T) {
	requests := []capturedSlackRequest{}
	deps, out, errOut := testDeps(roundTripFunc(func(request *http.Request) (*http.Response, error) {
		requests = append(requests, captureSlackRequest(t, request))
		return response(http.StatusOK, `{"success":true,"data":{"phase":"configuration_required","primaryAction":"supply_configuration","summary":"Workspace T123: The Workspace Configuration credentials are required."}}`), nil
	}), map[string]string{"MOHIST_TOKEN": "operator"})
	deps.ReadFile = func(string) (string, error) { return "", os.ErrNotExist }
	interactive, readSecret, prompts := slackPrompts("must-not-be-read")
	deps.TerminalInteractive = interactive
	deps.ReadSecretLine = readSecret

	code := Run(context.Background(), []string{"slack", "setup", "--json", "phase,primaryAction"}, deps)

	if code != ExitOperation || len(*prompts) != 0 || len(requests) != 1 {
		t.Fatalf("code=%d prompts=%v requests=%+v", code, *prompts, requests)
	}
	if !strings.Contains(out.String(), "\"phase\"") || !strings.Contains(out.String(), "supply_configuration") {
		t.Fatalf("machine output=%q", out.String())
	}
	if !strings.Contains(errOut.String(), "continue: mo slack setup --credentials-file <path>") {
		t.Fatalf("continuation missing: stderr=%q", errOut.String())
	}
}

func TestSlackSetupCarriesTheWorkspaceSelectorOnEveryRequestAndContinuation(t *testing.T) {
	requests := []capturedSlackRequest{}
	deps, out, errOut := testDeps(roundTripFunc(func(request *http.Request) (*http.Response, error) {
		requests = append(requests, captureSlackRequest(t, request))
		switch len(requests) {
		case 1:
			return response(http.StatusOK, `{"success":true,"data":{"phase":"configuration_required","primaryAction":"supply_configuration","summary":"Workspace T123: The Workspace Configuration credentials are required."}}`), nil
		default:
			return response(http.StatusOK, `{"success":true,"data":{"phase":"awaiting_install","primaryAction":"approve_install","installUrl":"https://api.slack.com/install","summary":"Workspace T123: The Mohist App installation needs approval in Slack."}}`), nil
		}
	}), map[string]string{"MOHIST_TOKEN": "operator"})
	file := &slackFileStub{contents: `{"configurationAccessToken":"xoxe-new","configurationRefreshToken":"xoxr-new"}`, mode: 0o600}
	deps.ReadFile, deps.StatFile = file.read, file.stat

	code := Run(context.Background(), []string{"slack", "setup", "--workspace-team", "T123", "--credentials-file", "/secure/slack.json"}, deps)

	if code != ExitOK || errOut.Len() != 0 {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if len(requests) != 2 || requests[0].query != "workspaceTeamId=T123" || requests[1].query != "workspaceTeamId=T123" {
		t.Fatalf("requests=%+v", requests)
	}
	if requests[1].path != "/api/slack-manager/setup/configuration" {
		t.Fatalf("requests=%+v", requests)
	}
	if !strings.Contains(out.String(), "continue: mo slack setup --workspace-team T123") {
		t.Fatalf("continuation=%q", out.String())
	}
}

func TestSlackSetupReadsNoSharedDefaultCredentialsFile(t *testing.T) {
	deps, out, errOut := testDeps(roundTripFunc(func(request *http.Request) (*http.Response, error) {
		return response(http.StatusNotFound, `{"success":false,"error":"Setup has not started.","code":"not_found"}`), nil
	}), map[string]string{"MOHIST_TOKEN": "operator"})
	deps.ReadFile = func(path string) (string, error) {
		t.Fatalf("no credentials file may be read implicitly, got %q", path)
		return "", nil
	}
	deps.HomeDir = func() (string, error) {
		t.Fatalf("the home directory is not consulted for Slack credentials")
		return "", nil
	}
	deps.TerminalInteractive = func() bool { return false }

	code := Run(context.Background(), []string{"slack", "setup"}, deps)

	if code != ExitOperation || out.Len() != 0 || !strings.Contains(errOut.String(), "credentials_required") {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
}

func TestSlackCredentialsFileRequiresOwnerOnlyPermissionsBeforeReadingContents(t *testing.T) {
	for _, testCase := range []struct {
		name string
		mode os.FileMode
	}{{"group readable", 0o644}, {"world readable", 0o604}, {"directory", os.ModeDir | 0o600}} {
		t.Run(testCase.name, func(t *testing.T) {
			calls := 0
			deps, out, errOut := testDeps(roundTripFunc(func(request *http.Request) (*http.Response, error) {
				calls++
				return response(http.StatusNotFound, `{"success":false,"error":"Setup has not started.","code":"not_found"}`), nil
			}), map[string]string{"MOHIST_TOKEN": "operator"})
			file := &slackFileStub{contents: `{"configurationAccessToken":"xoxe","configurationRefreshToken":"xoxr"}`, mode: testCase.mode}
			deps.ReadFile, deps.StatFile = file.read, file.stat

			code := Run(context.Background(), []string{"slack", "setup", "--credentials-file", "/secure/slack.json"}, deps)

			if code != ExitOperation || file.reads != 0 || calls != 1 || out.Len() != 0 {
				t.Fatalf("code=%d reads=%d calls=%d stdout=%q", code, file.reads, calls, out.String())
			}
			if !strings.Contains(errOut.String(), "credentials_file_unavailable") || !strings.Contains(errOut.String(), "0600") {
				t.Fatalf("stderr=%q", errOut.String())
			}
			if strings.Contains(errOut.String(), "xoxe") {
				t.Fatalf("secret leaked: stderr=%q", errOut.String())
			}
		})
	}
}

func TestSlackCredentialsFileRejectsUnknownOrPartialFieldsBeforeSecretPost(t *testing.T) {
	for _, contents := range []string{
		`{"configurationAccessToken":"xoxe","configurationRefreshToken":"xoxr","token":"leak-me"}`,
		`{"configurationAccessToken":"xoxe-only"}`,
		`{}`,
	} {
		t.Run(contents, func(t *testing.T) {
			calls := 0
			deps, out, errOut := testDeps(roundTripFunc(func(request *http.Request) (*http.Response, error) {
				calls++
				return response(http.StatusNotFound, `{"success":false,"error":"Setup has not started.","code":"not_found"}`), nil
			}), map[string]string{"MOHIST_TOKEN": "operator"})
			file := &slackFileStub{contents: contents, mode: 0o600}
			deps.ReadFile, deps.StatFile = file.read, file.stat

			code := Run(context.Background(), []string{"slack", "setup", "--credentials-file", "/secure/slack.json"}, deps)

			if code != ExitOperation || calls != 1 || out.Len() != 0 || !strings.Contains(errOut.String(), "invalid_credentials_file") {
				t.Fatalf("code=%d calls=%d stdout=%q stderr=%q", code, calls, out.String(), errOut.String())
			}
			if strings.Contains(errOut.String(), "leak-me") || strings.Contains(out.String(), "leak-me") {
				t.Fatalf("secret leaked: stdout=%q stderr=%q", out.String(), errOut.String())
			}
		})
	}
}

func TestSlackSetupResumesAtRuntimeCredentialsAndReportsTheStepWithoutNewInput(t *testing.T) {
	requests := []capturedSlackRequest{}
	deps, out, errOut := testDeps(roundTripFunc(func(request *http.Request) (*http.Response, error) {
		requests = append(requests, captureSlackRequest(t, request))
		return response(http.StatusOK, `{"success":true,"data":{"phase":"awaiting_install","primaryAction":"approve_install","installUrl":"https://api.slack.com/install","summary":"Workspace T123: The Mohist App installation needs approval in Slack."}}`), nil
	}), map[string]string{"MOHIST_TOKEN": "operator"})
	file := &slackFileStub{contents: `{"configurationAccessToken":"xoxe-old","configurationRefreshToken":"xoxr-old","botToken":"xoxb-manager","appLevelToken":"xapp-manager"}`, mode: 0o600}
	deps.ReadFile, deps.StatFile = file.read, file.stat

	code := Run(context.Background(), []string{"slack", "setup"}, deps)

	if code != ExitOK || errOut.Len() != 0 {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if len(requests) != 1 || requests[0].method != http.MethodGet {
		t.Fatalf("the approval step collects no credential: requests=%+v", requests)
	}
	if !strings.Contains(out.String(), "approve the Mohist App installation in Slack") {
		t.Fatalf("stdout=%q", out.String())
	}
	if strings.Contains(out.String(), "xoxe-old") || strings.Contains(out.String(), "xoxb-manager") {
		t.Fatalf("secret leaked: %q", out.String())
	}
}

func TestSlackSetupSuppliesTheRuntimePairTheCurrentStepNeeds(t *testing.T) {
	requests := []capturedSlackRequest{}
	deps, out, errOut := testDeps(roundTripFunc(func(request *http.Request) (*http.Response, error) {
		requests = append(requests, captureSlackRequest(t, request))
		if len(requests) == 1 {
			return response(http.StatusOK, `{"success":true,"data":{"phase":"failed","primaryAction":"supply_runtime_credentials","summary":"Workspace T123: The last setup step failed.","errorClass":"runtime_credential_mismatch"}}`), nil
		}
		return response(http.StatusOK, `{"success":true,"data":{"phase":"awaiting_socket_validation","primaryAction":"await_socket_verification","summary":"Workspace T123: The Mohist App Socket identity is being verified."}}`), nil
	}), map[string]string{"MOHIST_TOKEN": "operator"})
	file := &slackFileStub{contents: `{"botToken":"xoxb-manager","appLevelToken":"xapp-manager"}`, mode: 0o600}
	deps.ReadFile, deps.StatFile = file.read, file.stat

	code := Run(context.Background(), []string{"slack", "setup", "--credentials-file", "/secure/slack.json"}, deps)

	if code != ExitOK || errOut.Len() != 0 {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if len(requests) != 2 || requests[1].path != "/api/slack-manager/setup/runtime-credentials" {
		t.Fatalf("requests=%+v", requests)
	}
	if requests[1].body["botToken"] != "xoxb-manager" || requests[1].body["appLevelToken"] != "xapp-manager" || len(requests[1].body) != 2 {
		t.Fatalf("runtime body=%v", requests[1].body)
	}
	if !strings.Contains(out.String(), "wait for Mohist to verify the Socket identity") {
		t.Fatalf("stdout=%q", out.String())
	}
	if strings.Contains(out.String(), "xoxb-manager") || strings.Contains(out.String(), "xapp-manager") {
		t.Fatalf("secret leaked: %q", out.String())
	}
}

func TestSlackSetupPromptsHiddenInputForTheMissingRuntimePair(t *testing.T) {
	requests := []capturedSlackRequest{}
	deps, out, errOut := testDeps(roundTripFunc(func(request *http.Request) (*http.Response, error) {
		requests = append(requests, captureSlackRequest(t, request))
		if len(requests) == 1 {
			return response(http.StatusOK, `{"success":true,"data":{"phase":"failed","primaryAction":"supply_runtime_credentials","summary":"Workspace T123: The last setup step failed.","errorClass":"runtime_credential_mismatch"}}`), nil
		}
		return response(http.StatusOK, `{"success":true,"data":{"phase":"awaiting_socket_validation","primaryAction":"await_socket_verification","summary":"Workspace T123: The Mohist App Socket identity is being verified."}}`), nil
	}), map[string]string{"MOHIST_TOKEN": "operator"})
	file := &slackFileStub{mode: 0o600}
	deps.ReadFile, deps.StatFile = file.read, file.stat
	interactive, readSecret, prompts := slackPrompts("xoxb-typed", "xapp-typed")
	deps.TerminalInteractive = interactive
	deps.ReadSecretLine = readSecret

	code := Run(context.Background(), []string{"slack", "setup"}, deps)

	if code != ExitOK || errOut.Len() != 0 {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if len(*prompts) != 2 || !strings.Contains((*prompts)[0], "Mohist App Bot token") || !strings.Contains((*prompts)[1], "App-level token") {
		t.Fatalf("prompts=%v", *prompts)
	}
	if len(requests) != 2 || requests[1].path != "/api/slack-manager/setup/runtime-credentials" {
		t.Fatalf("requests=%+v", requests)
	}
	if requests[1].body["botToken"] != "xoxb-typed" || requests[1].body["appLevelToken"] != "xapp-typed" || len(requests[1].body) != 2 {
		t.Fatalf("runtime body=%v", requests[1].body)
	}
	if strings.Contains(out.String(), "xoxb-typed") || strings.Contains(errOut.String(), "xapp-typed") {
		t.Fatalf("secret leaked: stdout=%q stderr=%q", out.String(), errOut.String())
	}
}

func TestSlackSetupRotatesAnExplicitReplacementPairOnAReadyInstallation(t *testing.T) {
	requests := []capturedSlackRequest{}
	deps, out, errOut := testDeps(roundTripFunc(func(request *http.Request) (*http.Response, error) {
		requests = append(requests, captureSlackRequest(t, request))
		if len(requests) == 1 {
			return response(http.StatusOK, `{"success":true,"data":{"phase":"ready","primaryAction":"ready","summary":"Workspace T123: The Mohist App is ready in this Workspace."}}`), nil
		}
		return response(http.StatusOK, `{"success":true,"data":{"phase":"awaiting_socket_validation","primaryAction":"await_socket_verification","summary":"Workspace T123: The Mohist App Socket identity is being verified."}}`), nil
	}), map[string]string{"MOHIST_TOKEN": "operator"})
	file := &slackFileStub{contents: `{"botToken":"xoxb-replacement","appLevelToken":"xapp-replacement"}`, mode: 0o600}
	deps.ReadFile, deps.StatFile = file.read, file.stat

	code := Run(context.Background(), []string{"slack", "setup", "--credentials-file", "/secure/slack.json"}, deps)

	if code != ExitOK || errOut.Len() != 0 {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if len(requests) != 2 || requests[1].path != "/api/slack-manager/setup/runtime-credentials" {
		t.Fatalf("requests=%+v", requests)
	}
	if requests[1].body["botToken"] != "xoxb-replacement" {
		t.Fatalf("replacement body=%v", requests[1].body)
	}
	if strings.Contains(out.String(), "xoxb-replacement") {
		t.Fatalf("secret leaked: %q", out.String())
	}
}

func TestSlackSetupRerunOnAReadyInstallationSubmitsNoConsumedPair(t *testing.T) {
	requests := []capturedSlackRequest{}
	deps, out, errOut := testDeps(roundTripFunc(func(request *http.Request) (*http.Response, error) {
		requests = append(requests, captureSlackRequest(t, request))
		return response(http.StatusOK, `{"success":true,"data":{"phase":"ready","primaryAction":"ready","summary":"Workspace T123: The Mohist App is ready in this Workspace."}}`), nil
	}), map[string]string{"MOHIST_TOKEN": "operator"})
	deps.ReadFile = func(string) (string, error) { return "", os.ErrNotExist }
	deps.TerminalInteractive = func() bool { return true }
	deps.ReadSecretLine = func(string) (string, error) {
		t.Fatalf("a ready installation must not prompt for a consumed pair")
		return "", nil
	}

	code := Run(context.Background(), []string{"slack", "setup"}, deps)

	if code != ExitOK || errOut.Len() != 0 {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if len(requests) != 1 || requests[0].method != http.MethodGet {
		t.Fatalf("requests=%+v", requests)
	}
	if !strings.Contains(out.String(), "the Workspace is ready") || strings.Contains(out.String(), "continue:") {
		t.Fatalf("stdout=%q", out.String())
	}
}

func TestSlackSetupResumesNonSecretRecoveryWithoutRotatingConfiguration(t *testing.T) {
	requests := []capturedSlackRequest{}
	deps, out, errOut := testDeps(roundTripFunc(func(request *http.Request) (*http.Response, error) {
		requests = append(requests, captureSlackRequest(t, request))
		switch len(requests) {
		case 1:
			return response(http.StatusOK, `{"success":true,"data":{"phase":"create_unknown","primaryAction":"rerun_setup","summary":"Workspace T123: The Mohist App create result is unknown and must be reconciled.","errorClass":"transport_error"}}`), nil
		default:
			return response(http.StatusOK, `{"success":true,"data":{"phase":"awaiting_install","primaryAction":"approve_install","installUrl":"https://api.slack.com/apps/A1/oauth","summary":"Workspace T123: The Mohist App installation needs approval in Slack."}}`), nil
		}
	}), map[string]string{"MOHIST_TOKEN": "operator"})
	deps.ReadFile = func(string) (string, error) { return "", os.ErrNotExist }

	code := Run(context.Background(), []string{"slack", "setup"}, deps)

	if code != ExitOK || errOut.Len() != 0 {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if len(requests) != 2 || requests[0].method != http.MethodGet || requests[1].method != http.MethodPost || requests[1].path != "/api/slack-manager/setup/resume" || requests[1].body != nil {
		t.Fatalf("requests=%+v", requests)
	}
	if !strings.Contains(out.String(), "approve the Mohist App installation in Slack") {
		t.Fatalf("stdout=%q", out.String())
	}
}

func TestSlackStatusReportsAnIncompleteInstallationAndExitsZero(t *testing.T) {
	var request *http.Request
	deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		request = r
		return response(http.StatusOK, `{"success":true,"data":{"phase":"awaiting_install","primaryAction":"approve_install","installUrl":"https://api.slack.com/install","summary":"Workspace T123: The Mohist App installation needs approval in Slack."}}`), nil
	}), map[string]string{"MOHIST_TOKEN": "management-token"})

	code := Run(context.Background(), []string{"slack", "status"}, deps)

	if code != ExitOK || errOut.Len() != 0 {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if request == nil || request.URL.Path != "/api/slack-manager/setup/progress" || request.URL.RawQuery != "" {
		t.Fatalf("request=%v", request)
	}
	if !strings.Contains(out.String(), "Workspace T123") || !strings.Contains(out.String(), "approve the Mohist App installation in Slack") {
		t.Fatalf("stdout=%q", out.String())
	}
	if !strings.Contains(out.String(), "continue: mo slack setup") {
		t.Fatalf("next action missing: stdout=%q", out.String())
	}
}

func TestSlackStatusReportsANotStartedSetupAndExitsZero(t *testing.T) {
	deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		return response(http.StatusNotFound, `{"success":false,"error":"The workspace has not started setup.","code":"not_found"}`), nil
	}), map[string]string{"MOHIST_TOKEN": "management-token"})

	code := Run(context.Background(), []string{"slack", "status"}, deps)

	if code != ExitOK || errOut.Len() != 0 {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if !strings.Contains(out.String(), "has not started") || !strings.Contains(out.String(), "continue: mo slack setup") {
		t.Fatalf("stdout=%q", out.String())
	}
}

func TestSlackStatusExitsNonzeroOnADefiniteFailure(t *testing.T) {
	deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		return response(http.StatusOK, `{"success":true,"data":{"phase":"failed","primaryAction":"supply_runtime_credentials","summary":"Workspace T123: The last setup step failed.","errorClass":"runtime_credential_mismatch"}}`), nil
	}), map[string]string{"MOHIST_TOKEN": "management-token"})

	code := Run(context.Background(), []string{"slack", "status"}, deps)

	if code != ExitOperation || errOut.Len() != 0 {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if !strings.Contains(out.String(), "Workspace T123") || !strings.Contains(out.String(), "supply the Mohist App Bot token and App-level token") {
		t.Fatalf("the truthful report stays on stdout: %q", out.String())
	}
	if !strings.Contains(out.String(), "reason: runtime_credential_mismatch") {
		t.Fatalf("reason missing: %q", out.String())
	}
}

func TestSlackStatusExitsZeroWhileAnOutcomeIsUnknown(t *testing.T) {
	deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		return response(http.StatusOK, `{"success":true,"data":{"phase":"create_unknown","primaryAction":"rerun_setup","summary":"Workspace T123: The Mohist App create result is unknown and must be reconciled.","errorClass":"transport_error"}}`), nil
	}), map[string]string{"MOHIST_TOKEN": "management-token"})

	code := Run(context.Background(), []string{"slack", "status"}, deps)

	if code != ExitOK || errOut.Len() != 0 {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if !strings.Contains(out.String(), "unknown") || !strings.Contains(out.String(), "continue: mo slack setup") {
		t.Fatalf("stdout=%q", out.String())
	}
}

func TestSlackSetupSubmitsAnExplicitConfigurationPairOncePerRun(t *testing.T) {
	requests := []capturedSlackRequest{}
	deps, out, errOut := testDeps(roundTripFunc(func(request *http.Request) (*http.Response, error) {
		requests = append(requests, captureSlackRequest(t, request))
		if len(requests) == 1 {
			return response(http.StatusOK, `{"success":true,"data":{"phase":"configuration_required","primaryAction":"supply_configuration","summary":"Workspace T123: The Workspace Configuration credentials are required."}}`), nil
		}
		return response(http.StatusOK, `{"success":true,"data":{"phase":"ready","primaryAction":"ready","summary":"Workspace T123: The Mohist App is ready in this Workspace."}}`), nil
	}), map[string]string{"MOHIST_TOKEN": "operator"})
	file := &slackFileStub{contents: `{"configurationAccessToken":"xoxe","configurationRefreshToken":"xoxr"}`, mode: 0o600}
	deps.ReadFile, deps.StatFile = file.read, file.stat

	code := Run(context.Background(), []string{"slack", "setup", "--credentials-file", "/secure/slack.json"}, deps)

	if code != ExitOK || errOut.Len() != 0 {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if len(requests) != 2 || requests[1].path != "/api/slack-manager/setup/configuration" {
		t.Fatalf("a consumed pair is submitted once: requests=%+v", requests)
	}
}

func TestSlackStatusAmbiguousSelectionNamesTheSelectorAndTheEnrolledWorkspaces(t *testing.T) {
	deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		return response(http.StatusConflict, `{"success":false,"error":"More than one Slack workspace is enrolled; select one with --workspace-team <team-id>.","code":"workspace_selection_required","details":[{"teamId":"T111","name":"Slack workspace T111","phase":"awaiting_install"},{"teamId":"T222","name":"Slack workspace T222","phase":"ready"}]}`), nil
	}), map[string]string{"MOHIST_TOKEN": "management-token"})

	code := Run(context.Background(), []string{"slack", "status"}, deps)

	if code != ExitOperation || out.Len() != 0 {
		t.Fatalf("code=%d stdout=%q", code, out.String())
	}
	if !strings.Contains(errOut.String(), "--workspace-team <team-id>") {
		t.Fatalf("selector missing: stderr=%q", errOut.String())
	}
	if !strings.Contains(errOut.String(), "--workspace-team T111") || !strings.Contains(errOut.String(), "--workspace-team T222") {
		t.Fatalf("choices missing: stderr=%q", errOut.String())
	}
}

func TestSlackStatusRejectsASelectorThatNamesNoEnrolledWorkspace(t *testing.T) {
	var request *http.Request
	deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		request = r
		return response(http.StatusConflict, `{"success":false,"error":"No active Slack workspace enrollment matches team T999.","code":"workspace_not_enrolled"}`), nil
	}), map[string]string{"MOHIST_TOKEN": "management-token"})

	code := Run(context.Background(), []string{"slack", "status", "--workspace-team", "T999"}, deps)

	if code != ExitOperation || out.Len() != 0 || !strings.Contains(errOut.String(), "workspace_not_enrolled") {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if request == nil || request.URL.RawQuery != "workspaceTeamId=T999" {
		t.Fatalf("request=%v", request)
	}
}

func TestSlackInstallAgentUsesAgentIdentityAndReturnsRefreshedPublicProgress(t *testing.T) {
	requests := []capturedSlackRequest{}
	deps, out, errOut := testDeps(roundTripFunc(func(request *http.Request) (*http.Response, error) {
		requests = append(requests, captureSlackRequest(t, request))
		switch len(requests) {
		case 1:
			return slackAgentRead("agent_reviewer"), nil
		case 2:
			return response(http.StatusOK, `{"success":true,"data":{"connection":{"id":"connection-1","projectId":"proj","agentId":"agent_reviewer"},"agentApp":{"installUrl":"https://api.slack.com/install"},"nextAction":"provide_credentials"}}`), nil
		case 3:
			return response(http.StatusOK, `{"success":true,"data":{"accepted":true,"runtimeCredentialValidationState":"candidate"}}`), nil
		default:
			return response(http.StatusOK, `{"success":true,"data":{"connection":{"id":"connection-1","projectId":"proj","agentId":"agent_reviewer"},"agentApp":{"runtimeCredentialValidationState":"candidate"},"nextAction":"provide_credentials"}}`), nil
		}
	}), map[string]string{"MOHIST_TOKEN": "operator"})
	file := &slackFileStub{contents: `{"botToken":"xoxb-agent","appLevelToken":"xapp-agent"}`, mode: 0o600}
	deps.ReadFile, deps.StatFile = file.read, file.stat

	code := Run(context.Background(), []string{"slack", "install-agent", "reviewer", "--project", "proj", "--credentials-file", "/secure/agent.json"}, deps)

	if code != ExitOK || errOut.Len() != 0 {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if len(requests) != 4 || requests[0].path != "/api/projects/proj/agents/by-name/reviewer" || requests[1].path != "/api/projects/proj/slack-manager/install-agent" || requests[2].path != "/api/projects/proj/slack-manager/install-agent/credentials" || requests[3].path != requests[1].path {
		t.Fatalf("requests=%+v", requests)
	}
	for _, index := range []int{1, 2, 3} {
		if requests[index].body["agentId"] != "agent_reviewer" {
			t.Fatalf("request %d body=%v", index, requests[index].body)
		}
	}
	if requests[2].body["botToken"] != "xoxb-agent" || requests[2].body["appLevelToken"] != "xapp-agent" {
		t.Fatalf("credentials body=%v", requests[2].body)
	}
	if !strings.Contains(out.String(), "supply the Agent App Bot token and App-level token") {
		t.Fatalf("human summary=%q", out.String())
	}
	if strings.Contains(out.String(), "xoxb-agent") || strings.Contains(out.String(), "xapp-agent") {
		t.Fatalf("secret leaked: %q", out.String())
	}
}

func TestSlackInstallAgentResolvesTheSelectedWorkspaceBeforeAnyWrite(t *testing.T) {
	requests := []capturedSlackRequest{}
	deps, out, errOut := testDeps(roundTripFunc(func(request *http.Request) (*http.Response, error) {
		requests = append(requests, captureSlackRequest(t, request))
		if requests[len(requests)-1].path == "/api/slack-manager/setup/progress" {
			return response(http.StatusOK, `{"success":true,"data":{"phase":"ready","primaryAction":"ready","summary":"Workspace T123: The Mohist App is ready in this Workspace."}}`), nil
		}
		if requests[len(requests)-1].path == "/api/projects/proj/agents/by-name/reviewer" {
			return slackAgentRead("agent_reviewer"), nil
		}
		return response(http.StatusOK, `{"success":true,"data":{"connection":{"id":"connection-1","projectId":"proj","agentId":"agent_reviewer","setupProgress":"create_app_credentials"},"agentApp":{"installUrl":"https://api.slack.com/install"},"nextAction":"approve_install"}}`), nil
	}), map[string]string{"MOHIST_TOKEN": "operator"})
	deps.ReadFile = func(string) (string, error) { return "", os.ErrNotExist }

	code := Run(context.Background(), []string{"slack", "install-agent", "reviewer", "--project", "proj", "--workspace-team", "T123"}, deps)

	if code != ExitOK || errOut.Len() != 0 {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if len(requests) != 3 || requests[0].path != "/api/projects/proj/agents/by-name/reviewer" || requests[1].path != "/api/slack-manager/setup/progress" || requests[1].query != "workspaceTeamId=T123" {
		t.Fatalf("requests=%+v", requests)
	}
	// The write carries the same selector, so the Server installs into the
	// selected Workspace instead of the Agent's first existing Connection.
	if requests[2].path != "/api/projects/proj/slack-manager/install-agent" || requests[2].query != "workspaceTeamId=T123" {
		t.Fatalf("install write=%+v", requests[2])
	}
	if !strings.Contains(out.String(), "continue: mo slack install-agent reviewer --project proj --workspace-team T123") {
		t.Fatalf("continuation=%q", out.String())
	}
}

func TestSlackInstallAgentEndsAtOwnerClaimWithTheExplicitClaimCommand(t *testing.T) {
	requests := []capturedSlackRequest{}
	deps, out, errOut := testDeps(roundTripFunc(func(request *http.Request) (*http.Response, error) {
		requests = append(requests, captureSlackRequest(t, request))
		if requests[len(requests)-1].path == "/api/projects/proj/agents/by-name/reviewer" {
			return slackAgentRead("agent_reviewer"), nil
		}
		return response(http.StatusOK, `{"success":true,"data":{"connection":{"id":"connection-1","projectId":"proj","agentId":"agent_reviewer","setupProgress":"claim_owner"},"agentApp":{"installUrl":"https://api.slack.com/install"},"nextAction":"claim_owner"}}`), nil
	}), map[string]string{"MOHIST_TOKEN": "operator"})
	deps.ReadFile = func(string) (string, error) { return "", os.ErrNotExist }

	code := Run(context.Background(), []string{"slack", "install-agent", "reviewer", "--project", "proj", "--workspace-team", "T123"}, deps)

	if code != ExitOK || errOut.Len() != 0 {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if len(requests) != 3 || requests[2].query != "workspaceTeamId=T123" {
		t.Fatalf("requests=%+v", requests)
	}
	// Owner claim is the one next primary action, and the only way to obtain a
	// code is the explicit claim command, whose response carries the exact Bot
	// DM destination. The guide prints neither a code nor a destination itself.
	if !strings.Contains(out.String(), "next: claim Owner for the Agent App") {
		t.Fatalf("next action=%q", out.String())
	}
	if !strings.Contains(out.String(), "claim: mo slack claim-owner connection-1 --project proj") {
		t.Fatalf("claim command=%q", out.String())
	}
	if !strings.Contains(out.String(), "continue: mo slack install-agent reviewer --project proj --workspace-team T123") {
		t.Fatalf("continuation=%q", out.String())
	}
	if strings.Contains(out.String(), "code") {
		t.Fatalf("the guide must not print a claim code: %q", out.String())
	}
}

func TestSlackInstallAgentRejectedCredentialsExitNonzeroWithTheServersReason(t *testing.T) {
	requests := []capturedSlackRequest{}
	deps, out, errOut := testDeps(roundTripFunc(func(request *http.Request) (*http.Response, error) {
		requests = append(requests, captureSlackRequest(t, request))
		if requests[len(requests)-1].path == "/api/projects/proj/agents/by-name/reviewer" {
			return slackAgentRead("agent_reviewer"), nil
		}
		if requests[len(requests)-1].path == "/api/projects/proj/slack-manager/install-agent/credentials" {
			return response(http.StatusOK, `{"success":true,"data":{"accepted":false,"runtimeCredentialValidationState":"not_provided","errorClass":"identity_mismatch"}}`), nil
		}
		return response(http.StatusOK, `{"success":true,"data":{"connection":{"id":"connection-1","projectId":"proj","agentId":"agent_reviewer","setupProgress":"create_app_credentials"},"agentApp":{"installUrl":"https://api.slack.com/install"},"nextAction":"provide_credentials"}}`), nil
	}), map[string]string{"MOHIST_TOKEN": "operator"})
	file := &slackFileStub{contents: `{"botToken":"xoxb-wrong-team","appLevelToken":"xapp-wrong-team"}`, mode: 0o600}
	deps.ReadFile, deps.StatFile = file.read, file.stat

	code := Run(context.Background(), []string{"slack", "install-agent", "reviewer", "--project", "proj", "--credentials-file", "/secure/agent.json"}, deps)

	if code != ExitOperation {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if len(requests) != 3 {
		t.Fatalf("a rejected pair is not followed by another install write: requests=%+v", requests)
	}
	if !strings.Contains(errOut.String(), "identity_mismatch") {
		t.Fatalf("reason missing: stderr=%q", errOut.String())
	}
	if !strings.Contains(errOut.String(), "continue: mo slack install-agent reviewer --project proj --credentials-file <path>") {
		t.Fatalf("continuation missing: stderr=%q", errOut.String())
	}
	if strings.Contains(out.String(), "xoxb-wrong-team") || strings.Contains(errOut.String(), "xoxb-wrong-team") {
		t.Fatalf("secret leaked: stdout=%q stderr=%q", out.String(), errOut.String())
	}
}

func TestSlackInstallAgentRefusesASelectorThatNamesNoEnrolledWorkspace(t *testing.T) {
	requests := []capturedSlackRequest{}
	deps, _, errOut := testDeps(roundTripFunc(func(request *http.Request) (*http.Response, error) {
		requests = append(requests, captureSlackRequest(t, request))
		if requests[len(requests)-1].path == "/api/projects/proj/agents/by-name/reviewer" {
			return slackAgentRead("agent_reviewer"), nil
		}
		return response(http.StatusConflict, `{"success":false,"error":"No active Slack workspace enrollment matches team T999.","code":"workspace_not_enrolled"}`), nil
	}), map[string]string{"MOHIST_TOKEN": "operator"})

	code := Run(context.Background(), []string{"slack", "install-agent", "reviewer", "--project", "proj", "--workspace-team", "T999"}, deps)

	if code != ExitOperation || len(requests) != 2 {
		t.Fatalf("code=%d requests=%+v", code, requests)
	}
	if requests[1].path != "/api/slack-manager/setup/progress" || requests[1].query != "workspaceTeamId=T999" {
		t.Fatalf("selector read=%+v", requests[1])
	}
	if !strings.Contains(errOut.String(), "workspace_not_enrolled") {
		t.Fatalf("stderr=%q", errOut.String())
	}
}

func TestSlackInstallAgentNonInteractiveReportsTheCredentialStepAndContinuation(t *testing.T) {
	requests := []capturedSlackRequest{}
	deps, out, errOut := testDeps(roundTripFunc(func(request *http.Request) (*http.Response, error) {
		requests = append(requests, captureSlackRequest(t, request))
		if requests[len(requests)-1].path == "/api/projects/proj/agents/by-name/reviewer" {
			return slackAgentRead("agent_reviewer"), nil
		}
		return response(http.StatusOK, `{"success":true,"data":{"connection":{"id":"connection-1","projectId":"proj","agentId":"agent_reviewer","setupProgress":"create_app_credentials"},"agentApp":{"installUrl":"https://api.slack.com/install"},"nextAction":"provide_credentials"}}`), nil
	}), map[string]string{"MOHIST_TOKEN": "operator"})
	deps.ReadFile = func(string) (string, error) { return "", os.ErrNotExist }
	interactive, readSecret, prompts := slackPrompts("must-not-be-read")
	deps.TerminalInteractive = interactive
	deps.ReadSecretLine = readSecret
	deps.TerminalInteractive = func() bool { return false }

	code := Run(context.Background(), []string{"slack", "install-agent", "reviewer", "--project", "proj"}, deps)

	if code != ExitOperation || len(*prompts) != 0 || len(requests) != 2 {
		t.Fatalf("code=%d prompts=%v requests=%+v", code, *prompts, requests)
	}
	if out.Len() != 0 || !strings.Contains(errOut.String(), "credentials_required") {
		t.Fatalf("stdout=%q stderr=%q", out.String(), errOut.String())
	}
	if !strings.Contains(errOut.String(), "continue: mo slack install-agent reviewer --project proj --credentials-file <path>") {
		t.Fatalf("continuation missing: stderr=%q", errOut.String())
	}
}

func TestSlackInstallWithoutCredentialsFileStillReturnsInstallStep(t *testing.T) {
	requests := 0
	deps, out, errOut := testDeps(roundTripFunc(func(request *http.Request) (*http.Response, error) {
		requests++
		if request.URL.Path == "/api/projects/proj/agents/by-name/reviewer" {
			return slackAgentRead("agent_reviewer"), nil
		}
		return response(http.StatusOK, `{"success":true,"data":{"connection":{"id":"connection-1","projectId":"proj","agentId":"agent_reviewer","setupProgress":"create_app_credentials"},"agentApp":{"installUrl":"https://api.slack.com/install"},"nextAction":"approve_install"}}`), nil
	}), map[string]string{"MOHIST_TOKEN": "operator"})
	deps.ReadFile = func(string) (string, error) { return "", os.ErrNotExist }

	code := Run(context.Background(), []string{"slack", "install-agent", "reviewer", "--project", "proj"}, deps)

	if code != ExitOK || requests != 2 || errOut.Len() != 0 {
		t.Fatalf("code=%d requests=%d stderr=%q", code, requests, errOut.String())
	}
	if !strings.Contains(out.String(), "approve the Agent App installation in Slack") || !strings.Contains(out.String(), "https://api.slack.com/install") {
		t.Fatalf("stdout=%q", out.String())
	}
}

func TestSlackInstallAgentPromptsHiddenInputForTheMissingAgentPair(t *testing.T) {
	requests := []capturedSlackRequest{}
	deps, out, errOut := testDeps(roundTripFunc(func(request *http.Request) (*http.Response, error) {
		requests = append(requests, captureSlackRequest(t, request))
		if requests[len(requests)-1].path == "/api/projects/proj/agents/by-name/reviewer" {
			return slackAgentRead("agent_reviewer"), nil
		}
		if requests[len(requests)-1].path == "/api/projects/proj/slack-manager/install-agent/credentials" {
			return response(http.StatusOK, `{"success":true,"data":{"accepted":true,"runtimeCredentialValidationState":"candidate"}}`), nil
		}
		if len(requests) == 2 {
			return response(http.StatusOK, `{"success":true,"data":{"connection":{"id":"connection-1","projectId":"proj","agentId":"agent_reviewer","setupProgress":"create_app_credentials"},"agentApp":{"installUrl":"https://api.slack.com/install"},"nextAction":"provide_credentials"}}`), nil
		}
		return response(http.StatusOK, `{"success":true,"data":{"connection":{"id":"connection-1","projectId":"proj","agentId":"agent_reviewer","setupProgress":"claim_owner"},"nextAction":"claim_owner"}}`), nil
	}), map[string]string{"MOHIST_TOKEN": "operator"})
	deps.ReadFile = func(string) (string, error) { return "", os.ErrNotExist }
	interactive, readSecret, prompts := slackPrompts("xoxb-typed", "xapp-typed")
	deps.TerminalInteractive = interactive
	deps.ReadSecretLine = readSecret

	code := Run(context.Background(), []string{"slack", "install-agent", "reviewer", "--project", "proj"}, deps)

	if code != ExitOK || errOut.Len() != 0 {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if len(*prompts) != 2 || !strings.Contains((*prompts)[0], "Agent App Bot token") || !strings.Contains((*prompts)[1], "Agent App App-level token") {
		t.Fatalf("prompts=%v", *prompts)
	}
	if len(requests) != 4 || requests[2].body["botToken"] != "xoxb-typed" || requests[2].body["appLevelToken"] != "xapp-typed" {
		t.Fatalf("requests=%+v", requests)
	}
	if !strings.Contains(out.String(), "claim: mo slack claim-owner connection-1 --project proj") {
		t.Fatalf("claim command=%q", out.String())
	}
	if strings.Contains(out.String(), "xoxb-typed") || strings.Contains(errOut.String(), "xapp-typed") {
		t.Fatalf("secret leaked: stdout=%q stderr=%q", out.String(), errOut.String())
	}
}

func TestSlackInstallAgentRequiresAnAgentTargetBeforeAnyRequest(t *testing.T) {
	calls := 0
	deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		calls++
		return response(http.StatusOK, `{"success":true,"data":{}}`), nil
	}), map[string]string{"MOHIST_TOKEN": "operator"})

	code := Run(context.Background(), []string{"slack", "install-agent", "--project", "proj"}, deps)

	if code != ExitUsage || calls != 0 || out.Len() != 0 {
		t.Fatalf("code=%d calls=%d stdout=%q", code, calls, out.String())
	}
	if !strings.Contains(errOut.String(), "Agent is required") || !strings.Contains(errOut.String(), "mo slack install-agent <agent>") {
		t.Fatalf("stderr=%q", errOut.String())
	}
}

func TestSlackInstallAgentExitsNonzeroWhenTheSlackServiceReportsTheConnectionBroken(t *testing.T) {
	requests := []capturedSlackRequest{}
	deps, out, errOut := testDeps(roundTripFunc(func(request *http.Request) (*http.Response, error) {
		requests = append(requests, captureSlackRequest(t, request))
		if requests[len(requests)-1].path == "/api/projects/proj/agents/by-name/reviewer" {
			return slackAgentRead("agent_reviewer"), nil
		}
		return response(http.StatusOK, `{"success":true,"data":{"connection":{"id":"connection-1","projectId":"proj","agentId":"agent_reviewer","setupProgress":"fix_slack_setup","connectionHealth":"degraded","healthReason":"socket_lease_stale"},"agentApp":{},"nextAction":"rerun_install"}}`), nil
	}), map[string]string{"MOHIST_TOKEN": "operator"})
	deps.ReadFile = func(string) (string, error) { return "", os.ErrNotExist }

	code := Run(context.Background(), []string{"slack", "install-agent", "reviewer", "--project", "proj"}, deps)

	if code != ExitOperation || errOut.Len() != 0 {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if !strings.Contains(out.String(), "rerun the guide to continue") {
		t.Fatalf("stdout=%q", out.String())
	}
}

func TestSlackStatusStructuredOutputKeepsTheFailureExitCode(t *testing.T) {
	deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		return response(http.StatusOK, `{"success":true,"data":{"phase":"failed","primaryAction":"supply_runtime_credentials","summary":"Workspace T123: The last setup step failed.","errorClass":"runtime_credential_mismatch"}}`), nil
	}), map[string]string{"MOHIST_TOKEN": "management-token"})

	code := Run(context.Background(), []string{"slack", "status", "--json", "phase"}, deps)

	if code != ExitOperation || errOut.Len() != 0 {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if !strings.Contains(out.String(), "\"phase\"") || !strings.Contains(out.String(), "failed") {
		t.Fatalf("machine output=%q", out.String())
	}
}

func captureSlackRequest(t *testing.T, request *http.Request) capturedSlackRequest {
	t.Helper()
	captured := capturedSlackRequest{method: request.Method, path: request.URL.Path, query: request.URL.RawQuery}
	if request.Body == nil {
		return captured
	}
	data, err := io.ReadAll(request.Body)
	if err != nil {
		t.Fatalf("read body: %v", err)
	}
	if len(data) > 0 {
		if err := json.Unmarshal(data, &captured.body); err != nil {
			t.Fatalf("decode body %q: %v", data, err)
		}
	}
	return captured
}
