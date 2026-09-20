package mohistcli

import (
	"context"
	"encoding/json"
	"io"
	"net/http"
	"os"
	"strings"
	"testing"
)

type capturedSlackRequest struct {
	method string
	path   string
	body   map[string]any
}

func TestSlackSetupStartsFromOneStrictCredentialsFileWithoutExposingSecrets(t *testing.T) {
	requests := []capturedSlackRequest{}
	deps, out, errOut := testDeps(roundTripFunc(func(request *http.Request) (*http.Response, error) {
		requests = append(requests, captureSlackRequest(t, request))
		switch len(requests) {
		case 1:
			return response(http.StatusNotFound, `{"success":false,"error":"Setup has not started.","code":"not_found"}`), nil
		case 2:
			return response(http.StatusOK, `{"success":true,"data":{"phase":"awaiting_install","nextAction":"approve_install","installUrl":"https://api.slack.com/install"}}`), nil
		default:
			t.Fatalf("unexpected request %s %s", request.Method, request.URL.Path)
			return nil, nil
		}
	}), map[string]string{"MOHIST_TOKEN": "operator"})
	deps.ReadFile = func(path string) (string, error) {
		if path != "/secure/slack.json" {
			t.Fatalf("credentials path=%q", path)
		}
		return `{"configurationAccessToken":"xoxe-secret","configurationRefreshToken":"xoxr-secret"}`, nil
	}

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
	if strings.Contains(out.String(), "xoxe-secret") || strings.Contains(out.String(), "xoxr-secret") || strings.Contains(errOut.String(), "xoxe-secret") {
		t.Fatalf("secret leaked: stdout=%q stderr=%q", out.String(), errOut.String())
	}
}

func TestSlackSetupResumesAtRuntimeCredentialsWithoutRotatingConfigurationAgain(t *testing.T) {
	requests := []capturedSlackRequest{}
	deps, out, errOut := testDeps(roundTripFunc(func(request *http.Request) (*http.Response, error) {
		requests = append(requests, captureSlackRequest(t, request))
		switch len(requests) {
		case 1:
			return response(http.StatusOK, `{"success":true,"data":{"phase":"awaiting_install","nextAction":"approve_install","installUrl":"https://api.slack.com/install"}}`), nil
		case 2:
			return response(http.StatusOK, `{"success":true,"data":{"phase":"awaiting_socket_validation","nextAction":"report_socket_hello"}}`), nil
		default:
			t.Fatalf("unexpected request %s %s", request.Method, request.URL.Path)
			return nil, nil
		}
	}), map[string]string{"MOHIST_TOKEN": "operator"})
	deps.ReadFile = func(string) (string, error) {
		return `{"configurationAccessToken":"xoxe-old","configurationRefreshToken":"xoxr-old","botToken":"xoxb-manager","appLevelToken":"xapp-manager"}`, nil
	}

	code := Run(context.Background(), []string{"slack", "setup"}, deps)

	if code != ExitOK || errOut.Len() != 0 {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if len(requests) != 2 || requests[1].path != "/api/slack-manager/setup/runtime-credentials" {
		t.Fatalf("requests=%+v", requests)
	}
	if requests[1].body["botToken"] != "xoxb-manager" || requests[1].body["appLevelToken"] != "xapp-manager" || len(requests[1].body) != 2 {
		t.Fatalf("runtime body=%v", requests[1].body)
	}
	if strings.Contains(out.String(), "xoxb-manager") || strings.Contains(out.String(), "xapp-manager") {
		t.Fatalf("secret leaked: %q", out.String())
	}
}

func TestSlackSetupResumesNonSecretRecoveryWithoutRotatingConfiguration(t *testing.T) {
	requests := []capturedSlackRequest{}
	deps, out, errOut := testDeps(roundTripFunc(func(request *http.Request) (*http.Response, error) {
		requests = append(requests, captureSlackRequest(t, request))
		switch len(requests) {
		case 1:
			return response(http.StatusOK, `{"success":true,"data":{"phase":"create_unknown","nextAction":"reconcile_create","errorClass":"transport_error"}}`), nil
		case 2:
			return response(http.StatusOK, `{"success":true,"data":{"phase":"awaiting_install","nextAction":"approve_install","installUrl":"https://api.slack.com/apps/A1/oauth"}}`), nil
		default:
			t.Fatalf("unexpected request %s %s", request.Method, request.URL.Path)
			return nil, nil
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
	if strings.Contains(out.String(), "transport_error") || !strings.Contains(out.String(), "approve_install") {
		t.Fatalf("stdout=%q", out.String())
	}
}

func TestSlackInstallAgentUsesAgentIdentityAndReturnsRefreshedPublicProgress(t *testing.T) {
	requests := []capturedSlackRequest{}
	deps, out, errOut := testDeps(roundTripFunc(func(request *http.Request) (*http.Response, error) {
		requests = append(requests, captureSlackRequest(t, request))
		switch len(requests) {
		case 1:
			return response(http.StatusOK, `{"success":true,"data":{"connection":{"id":"connection-1","projectId":"proj","agentId":"reviewer"},"agentApp":{"installUrl":"https://api.slack.com/install"},"nextAction":"approve_install"}}`), nil
		case 2:
			return response(http.StatusOK, `{"success":true,"data":{"accepted":true,"runtimeCredentialValidationState":"candidate"}}`), nil
		case 3:
			return response(http.StatusOK, `{"success":true,"data":{"connection":{"id":"connection-1","projectId":"proj","agentId":"reviewer"},"agentApp":{"runtimeCredentialValidationState":"candidate"},"nextAction":"provide_credentials"}}`), nil
		default:
			t.Fatalf("unexpected request %s %s", request.Method, request.URL.Path)
			return nil, nil
		}
	}), map[string]string{"MOHIST_TOKEN": "operator"})
	deps.ReadFile = func(string) (string, error) {
		return `{"botToken":"xoxb-agent","appLevelToken":"xapp-agent"}`, nil
	}

	code := Run(context.Background(), []string{"slack", "install-agent", "reviewer", "--project", "proj"}, deps)

	if code != ExitOK || errOut.Len() != 0 {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if len(requests) != 3 || requests[0].path != "/api/projects/proj/slack-manager/install-agent" || requests[1].path != "/api/projects/proj/slack-manager/install-agent/credentials" || requests[2].path != requests[0].path {
		t.Fatalf("requests=%+v", requests)
	}
	for _, index := range []int{0, 1, 2} {
		if requests[index].body["agentId"] != "reviewer" {
			t.Fatalf("request %d body=%v", index, requests[index].body)
		}
	}
	if _, present := requests[0].body["agent"]; present {
		t.Fatalf("old agent field sent: %v", requests[0].body)
	}
	if strings.Contains(out.String(), "xoxb-agent") || strings.Contains(out.String(), "xapp-agent") {
		t.Fatalf("secret leaked: %q", out.String())
	}
}

func TestSlackCredentialsFileRejectsUnknownOrPartialFieldsBeforeSecretPost(t *testing.T) {
	for _, contents := range []string{
		`{"configurationAccessToken":"xoxe","configurationRefreshToken":"xoxr","token":"leak-me"}`,
		`{"configurationAccessToken":"xoxe-only"}`,
	} {
		t.Run(contents, func(t *testing.T) {
			calls := 0
			deps, out, errOut := testDeps(roundTripFunc(func(request *http.Request) (*http.Response, error) {
				calls++
				return response(http.StatusNotFound, `{"success":false,"error":"Setup has not started.","code":"not_found"}`), nil
			}), map[string]string{"MOHIST_TOKEN": "operator"})
			deps.ReadFile = func(string) (string, error) { return contents, nil }

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

func TestSlackInstallWithoutRuntimeFileStillReturnsInstallStep(t *testing.T) {
	requests := 0
	deps, out, errOut := testDeps(roundTripFunc(func(request *http.Request) (*http.Response, error) {
		requests++
		return response(http.StatusOK, `{"success":true,"data":{"connection":{"id":"connection-1","projectId":"proj","agentId":"reviewer"},"agentApp":{"installUrl":"https://api.slack.com/install"},"nextAction":"approve_install"}}`), nil
	}), map[string]string{"MOHIST_TOKEN": "operator"})
	deps.ReadFile = func(string) (string, error) { return "", os.ErrNotExist }

	code := Run(context.Background(), []string{"slack", "install-agent", "reviewer", "--project", "proj"}, deps)

	if code != ExitOK || requests != 1 || errOut.Len() != 0 || !strings.Contains(out.String(), "approve_install") {
		t.Fatalf("code=%d requests=%d stdout=%q stderr=%q", code, requests, out.String(), errOut.String())
	}
}

func captureSlackRequest(t *testing.T, request *http.Request) capturedSlackRequest {
	t.Helper()
	captured := capturedSlackRequest{method: request.Method, path: request.URL.Path}
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
