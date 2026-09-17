package mohistcli

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestOperationsLocalServiceDoesNotUseHTTP(t *testing.T) {
	calls := 0
	deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		calls++
		return nil, errors.New("HTTP must not be used")
	}), map[string]string{})
	deps.Execute = func(context.Context, string, []string) error { return nil }
	if code := Run(context.Background(), []string{"service", "start", "runner", "--dry-run"}, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if calls != 0 || !strings.Contains(out.String(), "Dry run: start runner") {
		t.Fatalf("calls=%d output=%q", calls, out.String())
	}
}

func TestRunnerServiceStatusIsLocalAndHTTPFree(t *testing.T) {
	calls := 0
	deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		calls++
		return nil, errors.New("HTTP must not be used")
	}), map[string]string{})
	deps.ExecuteOutput = func(_ context.Context, name string, args []string) (string, error) {
		if name != "systemctl" || strings.Join(args, " ") != "--user show --no-pager --property=Id,ActiveState,SubState,Result,ExecMainStatus mohist-runner.service" {
			t.Fatalf("command=%s %#v", name, args)
		}
		return "ActiveState=active\n", nil
	}
	if code := Run(context.Background(), []string{"service", "status", "runner"}, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if calls != 0 || out.String() != "ActiveState=active\n" || errOut.Len() != 0 {
		t.Fatalf("calls=%d stdout=%q stderr=%q", calls, out.String(), errOut.String())
	}
}

func TestServiceCommandsUseUserSystemdAndJournalctl(t *testing.T) {
	tests := []struct {
		name       string
		args       []string
		output     string
		wantOutput string
		wantName   string
		wantArgs   []string
	}{
		{name: "start", args: []string{"service", "start", "runner"}, wantName: "systemctl", wantArgs: []string{"--user", "start", "mohist-runner.service"}},
		{name: "stop", args: []string{"service", "stop", "server"}, wantName: "systemctl", wantArgs: []string{"--user", "stop", "mohist.service"}},
		{name: "restart", args: []string{"service", "restart", "slack"}, wantName: "systemctl", wantArgs: []string{"--user", "restart", "mohist-slack.service"}},
		{name: "status", args: []string{"service", "status", "runner"}, output: "ActiveState=active\n", wantOutput: "ActiveState=active\n", wantName: "systemctl", wantArgs: []string{"--user", "show", "--no-pager", "--property=Id,ActiveState,SubState,Result,ExecMainStatus", "mohist-runner.service"}},
		{name: "logs", args: []string{"service", "logs", "server", "--lines", "25", "--follow"}, output: "server log\n", wantOutput: "server log\n", wantName: "journalctl", wantArgs: []string{"--user", "-u", "mohist.service", "--no-pager", "-n", "25", "-f"}},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			deps, out, errOut := testDeps(nil, map[string]string{})
			var gotName string
			var gotArgs []string
			deps.Execute = func(_ context.Context, name string, args []string) error {
				gotName, gotArgs = name, args
				return nil
			}
			deps.ExecuteOutput = func(_ context.Context, name string, args []string) (string, error) {
				gotName, gotArgs = name, args
				return test.output, nil
			}
			if code := Run(context.Background(), test.args, deps); code != ExitOK {
				t.Fatalf("code=%d stderr=%q", code, errOut.String())
			}
			if gotName != test.wantName || strings.Join(gotArgs, "\x00") != strings.Join(test.wantArgs, "\x00") {
				t.Fatalf("command=%s %#v, want %s %#v", gotName, gotArgs, test.wantName, test.wantArgs)
			}
			wantOutput := "OK\n"
			if test.wantOutput != "" {
				wantOutput = test.wantOutput
			}
			if out.String() != wantOutput {
				t.Fatalf("stdout=%q", out.String())
			}
		})
	}
}

func TestServiceUninstallReportsCleanupFailure(t *testing.T) {
	home := t.TempDir()
	deps, _, errOut := testDeps(nil, map[string]string{})
	deps.HomeDir = func() (string, error) { return home, nil }
	var commands [][]string
	deps.Execute = func(_ context.Context, name string, args []string) error {
		commands = append(commands, append([]string{name}, args...))
		return nil
	}
	deps.RemoveAll = func(path string) error { return errors.New("cleanup failed: " + path) }
	if code := Run(context.Background(), []string{"service", "uninstall", "runner"}, deps); code != ExitOperation {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if !strings.Contains(errOut.String(), "remove managed service file") || !strings.Contains(errOut.String(), "cleanup failed") {
		t.Fatalf("stderr=%q", errOut.String())
	}
	if len(commands) != 2 || strings.Join(commands[0], " ") != "systemctl --user stop mohist-runner.service" || strings.Join(commands[1], " ") != "systemctl --user disable mohist-runner.service" {
		t.Fatalf("commands=%#v", commands)
	}
	if !strings.Contains(errOut.String(), filepath.Join(home, ".config", "systemd", "user", "mohist-runner.service")) {
		t.Fatalf("stderr does not identify the failed path: %q", errOut.String())
	}
}

func TestOperationsGithubConnectBuildsAppRequestWithoutCredential(t *testing.T) {
	var request *http.Request
	deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		request = r
		return response(http.StatusCreated, `{"success":true,"data":{"id":"gh-1","owner":"octocat","repo":"demo"}}`), nil
	}), map[string]string{"MOHIST_TOKEN": "operator-token"})
	if code := Run(context.Background(), []string{"github", "connect", "octocat/demo", "--approver", "alice", "--project", "proj"}, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if request == nil || request.Method != http.MethodPost || request.URL.Path != "/api/projects/proj/github-connections" {
		t.Fatalf("request=%v", request)
	}
	if strings.Contains(out.String(), "operator-token") || strings.Contains(errOut.String(), "operator-token") {
		t.Fatal("credential leaked in output")
	}
}

func TestOperationsDeadLetterRedeliverUsesPost(t *testing.T) {
	var request *http.Request
	deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		request = r
		return response(http.StatusOK, `{"success":true,"data":{"id":7,"delivered":true}}`), nil
	}), map[string]string{"MOHIST_TOKEN": "token"})
	if code := Run(context.Background(), []string{"event", "dead-letter", "redeliver", "7"}, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if request == nil || request.Method != http.MethodPost || request.URL.Path != "/api/events/dead-letters/7/redeliver" {
		t.Fatalf("request=%v", request)
	}
	if out.Len() == 0 {
		t.Fatal("missing result")
	}
}

func TestOperationsDiscoveryLeavesAreLocal(t *testing.T) {
	cases := []struct {
		name string
		args []string
		want string
	}{
		{name: "routing help", args: []string{"routing", "rule", "view", "--help"}, want: "JSON FIELDS"},
		{name: "routing short help", args: []string{"routing", "rule", "view", "-h"}, want: "JSON FIELDS"},
		{name: "webhook help", args: []string{"webhook", "subscription", "view", "--help"}, want: "JSON FIELDS"},
		{name: "event redeliver help", args: []string{"event", "dead-letter", "redeliver", "--help"}, want: "JSON FIELDS"},
		{name: "event redeliver short help", args: []string{"event", "dead-letter", "redeliver", "-h"}, want: "JSON FIELDS"},
		{name: "event tail short help", args: []string{"event", "tail", "-h"}, want: "JSON FIELDS"},
		{name: "otel query help", args: []string{"otel", "query", "--help"}, want: "JSON FIELDS"},
		{name: "otel query short help", args: []string{"otel", "query", "-h"}, want: "JSON FIELDS"},
		{name: "otel query catalog", args: []string{"otel", "query", "--json"}, want: "columns"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			calls := 0
			deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
				calls++
				return nil, errors.New("HTTP must not be used")
			}), map[string]string{})
			deps.ReadFile = func(string) (string, error) {
				t.Fatal("Project or config resolution must not be used")
				return "", nil
			}
			if code := Run(context.Background(), tc.args, deps); code != ExitOK {
				t.Fatalf("code=%d stderr=%q", code, errOut.String())
			}
			if calls != 0 || !strings.Contains(out.String(), tc.want) {
				t.Fatalf("calls=%d output=%q", calls, out.String())
			}
		})
	}
}

func TestOperationsNoCatalogBareJSONIsLocalUsageError(t *testing.T) {
	cases := [][]string{
		{"webhook", "event-types", "--json"},
		{"server", "status", "--json"},
	}
	for _, args := range cases {
		t.Run(strings.Join(args, "/"), func(t *testing.T) {
			calls := 0
			deps, _, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
				calls++
				return nil, errors.New("HTTP must not be used")
			}), map[string]string{})
			deps.ReadFile = func(string) (string, error) {
				t.Fatal("Project or config resolution must not be used")
				return "", nil
			}
			if code := Run(context.Background(), args, deps); code != ExitUsage || calls != 0 || errOut.Len() == 0 {
				t.Fatalf("code=%d calls=%d stderr=%q", code, calls, errOut.String())
			}
		})
	}
}

func TestOperationsEventTailUsesInjectedStreamAndCancellation(t *testing.T) {
	deps, out, errOut := testDeps(nil, map[string]string{})
	deps.EventTail = func(ctx context.Context, project string, types []string, match string, writer io.Writer) error {
		if project != "proj" || len(types) != 1 || types[0] != "issue.completed" || match != "event.issue == 1" {
			return errors.New("unexpected subscription")
		}
		_, _ = writer.Write([]byte(`{"id":"event-1"}` + "\n"))
		return context.Canceled
	}
	code := Run(context.Background(), []string{"event", "tail", "--project", "proj", "--event", "issue.completed", "--match", "event.issue == 1"}, deps)
	if code != ExitCanceled || !strings.Contains(out.String(), "event-1") || errOut.Len() == 0 {
		t.Fatalf("code=%d output=%q stderr=%q", code, out.String(), errOut.String())
	}
}

func TestOperationsSlackAnchorValidationIsLocal(t *testing.T) {
	calls := 0
	deps, _, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		calls++
		return nil, errors.New("must not call")
	}), map[string]string{"MOHIST_TOKEN": "token"})
	code := Run(context.Background(), []string{"slack", "message", "send", "--project", "proj", "--text", "hello"}, deps)
	for _, field := range []string{"--workspace", "--conversation", "--reply-to", "--connection", "--session", "--triggering-message", "--dispatch-ref"} {
		if !strings.Contains(errOut.String(), field) {
			t.Fatalf("missing %s in stderr=%q", field, errOut.String())
		}
	}
	if code != ExitUsage || calls != 0 {
		t.Fatalf("code=%d calls=%d stderr=%q", code, calls, errOut.String())
	}
}

func TestOperationsSlackStatusSendsWorkspaceQuery(t *testing.T) {
	var request *http.Request
	deps, _, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		request = r
		return response(http.StatusOK, `{"success":true,"data":{"workspaceTeamId":"T1"}}`), nil
	}), map[string]string{"MOHIST_TOKEN": "management-token"})
	if code := Run(context.Background(), []string{"slack", "status", "--workspace-team", "T1"}, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if request == nil || request.URL.Query().Get("workspaceTeamId") != "T1" || request.Header.Get("Authorization") != "Bearer management-token" {
		t.Fatalf("request=%v", request)
	}
}

func TestOperationsSlackManagerStatusUsesBrokerWithoutLocalBearer(t *testing.T) {
	var request *http.Request
	deps, _, errOut := testDeps(nil, map[string]string{"MOHIST_MANAGER_MODE": "1"})
	deps.ManagerCredentialBroker = func(_ context.Context, r *http.Request) (*http.Response, error) {
		request = r
		return response(http.StatusOK, `{"success":true,"data":{"workspaceTeamId":"T1"}}`), nil
	}
	if code := Run(context.Background(), []string{"slack", "status", "--workspace-team", "T1"}, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if request == nil || request.URL.Path != "/api/slack-manager/status" || request.URL.Query().Get("workspaceTeamId") != "T1" {
		t.Fatalf("request=%v", request)
	}
	if request.Header.Get("X-Mohist-Manager-Mode") != "1" || request.Header.Get("Authorization") != "" {
		t.Fatalf("headers=%v", request.Header)
	}
}

func TestOperationsSlackConnectionMessageMapsTextAndStdin(t *testing.T) {
	var request *http.Request
	deps, _, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		request = r
		return response(http.StatusOK, `{"success":true,"data":{"accepted":true}}`), nil
	}), map[string]string{"MOHIST_TOKEN": "connection-token"})
	deps.Input = strings.NewReader("line one\nline two\n")
	args := []string{"slack", "message", "send", "--project", "proj", "--workspace", " W1 ", "--conversation", "C1", "--reply-to", "R1", "--connection", "K1", "--session", "S1", "--triggering-message", "M1", "--dispatch-ref", "D1", "--text", "-"}
	if code := Run(context.Background(), args, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	var body map[string]any
	data, _ := io.ReadAll(request.Body)
	if err := json.Unmarshal(data, &body); err != nil {
		t.Fatal(err)
	}
	for field, want := range map[string]string{"workspaceTeamId": "W1", "conversationId": "C1", "threadTs": "R1", "connectionId": "K1", "sessionId": "S1", "triggeringMessageId": "M1", "dispatchRef": "D1", "text": "line one\nline two\n"} {
		if body[field] != want {
			t.Fatalf("body[%s]=%v, want %q; body=%s", field, body[field], want, data)
		}
	}
	if request.URL.Path != "/api/projects/proj/slack-connections/reply" || request.Header.Get("Authorization") != "Bearer connection-token" {
		t.Fatalf("request=%v", request)
	}
}

func TestOperationsSlackConnectionMessageMapsFileAndRejectsImage(t *testing.T) {
	var request *http.Request
	calls := 0
	deps, _, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		calls++
		request = r
		return response(http.StatusOK, `{"success":true,"data":{"accepted":true}}`), nil
	}), map[string]string{"MOHIST_TOKEN": "connection-token"})
	deps.ReadFile = func(path string) (string, error) {
		if path != "./picture.png" {
			t.Fatalf("read path=%q", path)
		}
		return "image-bytes", nil
	}
	args := []string{"slack", "message", "send", "--project", "proj", "--workspace", "W1", "--conversation", "C1", "--reply-to", "R1", "--connection", "K1", "--session", "S1", "--triggering-message", "M1", "--dispatch-ref", "D1", "--file", "./picture.png"}
	if code := Run(context.Background(), args, deps); code != ExitOK || request == nil {
		t.Fatalf("code=%d request=%v stderr=%q", code, request, errOut.String())
	}
	var body map[string]any
	data, _ := io.ReadAll(request.Body)
	if err := json.Unmarshal(data, &body); err != nil {
		t.Fatal(err)
	}
	if body["fileName"] != "picture.png" || body["fileContentBase64"] != "aW1hZ2UtYnl0ZXM=" || body["imageUrl"] != nil {
		t.Fatalf("body=%s", data)
	}
	if code := Run(context.Background(), append(args, "--image", "https://example.test/picture.png"), deps); code != ExitUsage || calls != 1 {
		t.Fatalf("mutually exclusive code=%d calls=%d stderr=%q", code, calls, errOut.String())
	}
}

func TestOperationsSlackConnectionMessageRejectsOversizedFileLocally(t *testing.T) {
	calls := 0
	deps, _, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		calls++
		return nil, errors.New("must not call")
	}), map[string]string{"MOHIST_TOKEN": "connection-token"})
	deps.ReadFile = func(string) (string, error) {
		return strings.Repeat("x", maxSlackReplyFileBytes+1), nil
	}
	args := []string{"slack", "message", "send", "--project", "proj", "--workspace", "W1", "--conversation", "C1", "--reply-to", "R1", "--connection", "K1", "--session", "S1", "--triggering-message", "M1", "--dispatch-ref", "D1", "--file", "./large.png"}
	if code := Run(context.Background(), args, deps); code != ExitUsage || calls != 0 {
		t.Fatalf("code=%d calls=%d stderr=%q", code, calls, errOut.String())
	}
	if !strings.Contains(errOut.String(), "at most 10 MB") {
		t.Fatalf("stderr=%q", errOut.String())
	}
}

func TestOperationsSlackConnectionMessageMapsImage(t *testing.T) {
	var request *http.Request
	deps, _, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		request = r
		return response(http.StatusOK, `{"success":true,"data":{"accepted":true}}`), nil
	}), map[string]string{"MOHIST_TOKEN": "connection-token"})
	args := []string{"slack", "message", "send", "--project", "proj", "--workspace", "W1", "--conversation", "C1", "--reply-to", "R1", "--connection", "K1", "--session", "S1", "--triggering-message", "M1", "--dispatch-ref", "D1", "--image", "https://example.test/picture.png"}
	if code := Run(context.Background(), args, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	var body map[string]any
	data, _ := io.ReadAll(request.Body)
	if err := json.Unmarshal(data, &body); err != nil {
		t.Fatal(err)
	}
	if body["imageUrl"] != "https://example.test/picture.png" || body["fileName"] != nil || body["fileContentBase64"] != nil {
		t.Fatalf("body=%s", data)
	}
}

func TestOperationsSlackManagerMessageUsesBrokerAndManagerRoute(t *testing.T) {
	var request *http.Request
	deps, _, errOut := testDeps(nil, map[string]string{"MOHIST_MANAGER_MODE": "1"})
	deps.ManagerCredentialBroker = func(_ context.Context, r *http.Request) (*http.Response, error) {
		request = r
		if r.Header.Get("X-Mohist-Manager-Mode") != "1" {
			t.Fatalf("manager marker=%q", r.Header.Get("X-Mohist-Manager-Mode"))
		}
		return response(http.StatusOK, `{"success":true,"data":{"accepted":true}}`), nil
	}
	args := []string{"slack", "message", "send", "--workspace", "W1", "--conversation", "C1", "--reply-to", "R1", "--connection", "K1", "--session", "S1", "--triggering-message", "M1", "--dispatch-ref", "D1", "--text", "hello"}
	if code := Run(context.Background(), args, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if request == nil || request.URL.Path != "/api/slack-manager/reply" || request.Header.Get("Authorization") != "" {
		t.Fatalf("request=%v", request)
	}
}

func TestOperationsNotificationUsesInjectedProbeAndWritesLocalConfig(t *testing.T) {
	deps, out, errOut := testDeps(nil, map[string]string{})
	var written string
	deps.HomeDir = func() (string, error) { return "/home/test", nil }
	deps.ReadFile = func(string) (string, error) { return "{}", nil }
	deps.WriteFile = func(_ string, value string, _ os.FileMode) error { written = value; return nil }
	deps.HealthProbe = func(context.Context, string) error { return nil }
	if code := Run(context.Background(), []string{"notification", "setup", "--health-base", "http://hermes"}, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if !strings.Contains(written, "http://hermes/webhooks/mohist") || !strings.Contains(out.String(), "Wrote Mohist") {
		t.Fatalf("written=%q output=%q", written, out.String())
	}
}

const runnerStatusListJSON = `{"success":true,"data":{"observedAt":"2026-08-01T12:00:00Z","inventory":{"state":"ready","nextActions":[]},"runners":[{"identity":{"id":"runner-1","hostname":"build-1","kind":"external","component":"mohist-runner","sourceRevision":"abc123","releaseId":"release-42","generation":7},"presence":{"state":"online","lastObservedAt":"2026-08-01T12:00:00Z"},"control":{"state":"connected","generation":"server-epoch:12"},"admission":{"state":"blocked","reasonCodes":["capacity-full"]},"capabilities":["spec/*"],"runtimes":[{"name":"pi","readiness":{"state":"ready","generation":3,"reasonCode":null},"catalog":{"complete":true,"capabilityRevision":"catalog-sha","modelCount":1,"models":["openai/gpt-5"],"variants":{},"supportsReasoningEffort":true,"reasoningEfforts":{"openai/gpt-5":["high"]}}}],"capacity":{"used":1,"total":1},"activeWorks":[{"workId":"workflow-work","ownerKind":"workflow","ownerId":"workflow-1","workType":"task","stage":"build","title":"Build"},{"workId":"agent-work","ownerKind":"agent-job","ownerId":"job-1","workType":"agent-job","stage":null,"title":null}],"drain":{"active":true,"kind":"update","updateInterruptId":"interrupt-1"},"nextActions":[{"code":"wait-for-capacity","message":"Wait for the active owner to release a Runner slot.","command":null}]}]}}`

func TestRunnerCommandsUseGlobalRoutes(t *testing.T) {
	tests := []struct {
		name   string
		args   []string
		method string
		path   string
		body   string
	}{
		{name: "list", args: []string{"runner", "list"}, method: http.MethodGet, path: "/api/runners", body: runnerStatusListJSON},
		{name: "status", args: []string{"runner", "status"}, method: http.MethodGet, path: "/api/runners", body: runnerStatusListJSON},
		{name: "view", args: []string{"runner", "view", "runner-1"}, method: http.MethodGet, path: "/api/runners/runner-1", body: `{"success":true,"data":{"observedAt":"2026-08-01T12:00:00Z","runner":{"identity":{"id":"runner-1"},"presence":{"state":"offline"},"control":{"state":"disconnected"},"admission":{"state":"blocked","reasonCodes":["presence-offline"]},"capabilities":[],"runtimes":[],"capacity":{"used":null,"total":1},"activeWorks":[],"drain":null,"nextActions":[{"code":"start-runner","message":"Start the Runner process.","command":"mo service start runner"}]}}}`},
		{name: "revoke", args: []string{"runner", "revoke", "runner-1"}, method: http.MethodDelete, path: "/api/runners/runner-1/credentials", body: `{"success":true,"data":{"runnerId":"runner-1","revokedAt":"2026-08-01T12:00:00Z"}}`},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			var request *http.Request
			deps, _, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
				request = r
				return response(http.StatusOK, test.body), nil
			}), map[string]string{"MOHIST_TOKEN": "token"})
			if code := Run(context.Background(), test.args, deps); code != ExitOK {
				t.Fatalf("code=%d stderr=%q", code, errOut.String())
			}
			if request == nil || request.Method != test.method || request.URL.Path != test.path {
				t.Fatalf("request=%v", request)
			}
		})
	}
}

func TestRunnerFieldDiscoveryAndSelectionUseCanonicalRows(t *testing.T) {
	calls := 0
	deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		calls++
		if r.URL.Path != "/api/runners" {
			t.Fatalf("path=%q", r.URL.Path)
		}
		return response(http.StatusOK, runnerStatusListJSON), nil
	}), map[string]string{"MOHIST_TOKEN": "token"})
	if code := Run(context.Background(), []string{"runner", "list", "--json"}, deps); code != ExitOK || calls != 0 {
		t.Fatalf("discovery code=%d calls=%d output=%q stderr=%q", code, calls, out.String(), errOut.String())
	}
	if out.String() != strings.Join(runnerFields, "\n")+"\n" {
		t.Fatalf("fields=%q", out.String())
	}
	*out, *errOut = strings.Builder{}, strings.Builder{}
	if code := Run(context.Background(), []string{"runner", "view", "--json"}, deps); code != ExitOK || calls != 0 || out.String() != strings.Join(runnerFields, "\n")+"\n" {
		t.Fatalf("view discovery code=%d calls=%d output=%q stderr=%q", code, calls, out.String(), errOut.String())
	}
	*out, *errOut = strings.Builder{}, strings.Builder{}
	if code := Run(context.Background(), []string{"runner", "list", "--json", "identity,activeWorks"}, deps); code != ExitOK {
		t.Fatalf("selection code=%d output=%q stderr=%q", code, out.String(), errOut.String())
	}
	if !strings.Contains(out.String(), `"identity"`) || !strings.Contains(out.String(), `"activeWorks"`) || !strings.Contains(out.String(), `"ownerKind":"workflow"`) || !strings.Contains(out.String(), `"ownerId":"job-1"`) || !strings.Contains(out.String(), `"workId":"agent-work"`) {
		t.Fatalf("selection=%q", out.String())
	}
	if strings.Contains(out.String(), `"scope"`) || strings.Contains(out.String(), `"status"`) {
		t.Fatalf("obsolete fields in selection=%q", out.String())
	}
}

func TestRunnerStatusSelectionDecodesTheListEnvelope(t *testing.T) {
	deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		if r.URL.Path != "/api/runners" {
			t.Fatalf("path=%q", r.URL.Path)
		}
		return response(http.StatusOK, runnerStatusListJSON), nil
	}), map[string]string{"MOHIST_TOKEN": "token"})
	if code := Run(context.Background(), []string{"runner", "status", "--json", "identity,activeWorks"}, deps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if !strings.HasPrefix(out.String(), "[{") || !strings.Contains(out.String(), `"identity"`) || !strings.Contains(out.String(), `"ownerKind":"agent-job"`) || !strings.Contains(out.String(), `"ownerId":"job-1"`) || !strings.Contains(out.String(), `"workId":"agent-work"`) {
		t.Fatalf("selection=%q", out.String())
	}
}

func TestRunnerViewSelectionReturnsTheCanonicalRow(t *testing.T) {
	deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		if r.URL.Path != "/api/runners/runner-1" {
			t.Fatalf("path=%q", r.URL.Path)
		}
		return response(http.StatusOK, `{"success":true,"data":{"observedAt":"2026-08-01T12:00:00Z","runner":{"identity":{"id":"runner-1"},"activeWorks":[{"workId":"work-1","ownerKind":"agent-job","ownerId":"job-1"}]}}}`), nil
	}), map[string]string{"MOHIST_TOKEN": "token"})
	if code := Run(context.Background(), []string{"runner", "view", "runner-1", "--json", "identity,activeWorks"}, deps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if strings.Contains(out.String(), `"runner"`) || !strings.Contains(out.String(), `"identity"`) || !strings.Contains(out.String(), `"ownerKind":"agent-job"`) || !strings.Contains(out.String(), `"ownerId":"job-1"`) || !strings.Contains(out.String(), `"workId":"work-1"`) {
		t.Fatalf("selection=%q", out.String())
	}
}

func TestRunnerHumanOutputKeepsIndependentFactsAndOwnerKinds(t *testing.T) {
	deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		return response(http.StatusOK, runnerStatusListJSON), nil
	}), map[string]string{"MOHIST_TOKEN": "token"})
	if code := Run(context.Background(), []string{"runner", "status"}, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	for _, expected := range []string{"presence: online", "control: connected", "admission: blocked", "capacity-full", "readiness: ready", "catalog:", "modelCount: 1", "capacity:", "active works:", "workflow owner:", "agent-job owner:", "drain: active", "wait-for-capacity"} {
		if !strings.Contains(out.String(), expected) {
			t.Errorf("output missing %q: %s", expected, out.String())
		}
	}
	if strings.Contains(out.String(), "idle") || strings.Contains(out.String(), "busy") {
		t.Fatalf("human output synthesized composite state: %q", out.String())
	}
}

func TestRunnerEmptyAndOfflineOutputUsesServerActions(t *testing.T) {
	t.Run("first install", func(t *testing.T) {
		deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
			return response(http.StatusOK, `{"success":true,"data":{"observedAt":"2026-08-01T12:00:00Z","inventory":{"state":"first-install","nextActions":[{"code":"install-runner","message":"Install and start the first Runner.","command":"mo install runner --repo-root <path>"}]},"runners":[]}}`), nil
		}), map[string]string{"MOHIST_TOKEN": "token"})
		if code := Run(context.Background(), []string{"runner", "list"}, deps); code != ExitOK {
			t.Fatalf("code=%d stderr=%q", code, errOut.String())
		}
		if out.String() != "next action: install-runner - Install and start the first Runner.\ncommand: mo install runner --repo-root <path>\n" {
			t.Fatalf("output=%q", out.String())
		}
	})

	t.Run("offline start", func(t *testing.T) {
		deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
			return response(http.StatusOK, `{"success":true,"data":{"observedAt":"2026-08-01T12:00:00Z","inventory":{"state":"ready","nextActions":[]},"runners":[{"identity":{"id":"runner-offline"},"presence":{"state":"offline","lastObservedAt":null},"control":{"state":"disconnected","generation":null},"admission":{"state":"blocked","reasonCodes":["presence-offline","control-disconnected"]},"capabilities":[],"runtimes":[],"capacity":{"used":null,"total":1},"activeWorks":[],"drain":null,"nextActions":[{"code":"start-runner","message":"Start the Runner process.","command":"mo service start runner"}]}]}}`), nil
		}), map[string]string{"MOHIST_TOKEN": "token"})
		if code := Run(context.Background(), []string{"runner", "list"}, deps); code != ExitOK {
			t.Fatalf("code=%d stderr=%q", code, errOut.String())
		}
		if !strings.Contains(out.String(), "start-runner") || !strings.Contains(out.String(), "mo service start runner") || strings.Contains(out.String(), "reenroll-runner") {
			t.Fatalf("output=%q", out.String())
		}
	})

	t.Run("confirmed re-enrollment preserves shell quoting", func(t *testing.T) {
		deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
			return response(http.StatusOK, `{"success":true,"data":{"observedAt":"2026-08-01T12:00:00Z","inventory":{"state":"ready","nextActions":[]},"runners":[{"identity":{"id":"build runner'$(touch /tmp/owned);"},"presence":{"state":"offline","lastObservedAt":null},"control":{"state":"disconnected","generation":null},"admission":{"state":"blocked","reasonCodes":["presence-offline","credential-revoked"]},"capabilities":[],"runtimes":[],"capacity":{"used":null,"total":1},"activeWorks":[],"drain":null,"nextActions":[{"code":"reenroll-runner","message":"Re-enroll the Runner credential.","command":"mo install runner --repo-root <path> --runner-id 'build runner'\"'\"'$(touch /tmp/owned);'"}]}]}}`), nil
		}), map[string]string{"MOHIST_TOKEN": "token"})
		if code := Run(context.Background(), []string{"runner", "list"}, deps); code != ExitOK {
			t.Fatalf("code=%d stderr=%q", code, errOut.String())
		}
		expectedCommand := `mo install runner --repo-root <path> --runner-id 'build runner'"'"'$(touch /tmp/owned);'`
		if !strings.Contains(out.String(), "reenroll-runner") || !strings.Contains(out.String(), expectedCommand) || strings.Contains(out.String(), "start-runner") {
			t.Fatalf("output=%q", out.String())
		}
	})
}

func TestRunnerEmptyOutputOnlyUsesTheServerInstallAction(t *testing.T) {
	deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		return response(http.StatusOK, `{"success":true,"data":{"observedAt":"2026-08-01T12:00:00Z","inventory":{"state":"first-install","nextActions":[{"code":"install-runner","message":"Install and start the first Runner.","command":"mo install runner --repo-root <path>"},{"code":"wait-for-capacity","message":"must not render","command":null}]},"runners":[]}}`), nil
	}), map[string]string{"MOHIST_TOKEN": "token"})
	if code := Run(context.Background(), []string{"runner", "list"}, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if strings.Contains(out.String(), "wait-for-capacity") || !strings.Contains(out.String(), "install-runner") {
		t.Fatalf("output=%q", out.String())
	}
}

func TestRunnerResponseFailuresDoNotRenderGenericResults(t *testing.T) {
	for _, test := range []struct {
		name   string
		body   string
		status int
		want   string
	}{
		{name: "server failure", body: `{"success":false,"error":"Runner status unavailable","code":"service_unavailable"}`, status: http.StatusServiceUnavailable, want: "service_unavailable"},
		{name: "malformed success", body: `{"success":true,"data":{"runners":[]}}`, status: http.StatusOK, want: "invalid_response"},
	} {
		t.Run(test.name, func(t *testing.T) {
			deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
				return response(test.status, test.body), nil
			}), map[string]string{"MOHIST_TOKEN": "token"})
			if code := Run(context.Background(), []string{"runner", "list"}, deps); code != ExitOperation || out.Len() != 0 || !strings.Contains(errOut.String(), test.want) || strings.Contains(errOut.String(), "No results") {
				t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
			}
		})
	}
}

func TestRunnerRevokeRequiresAnIDBeforeHTTP(t *testing.T) {
	calls := 0
	deps, _, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		calls++
		return nil, errors.New("must not call")
	}), map[string]string{"MOHIST_TOKEN": "token"})
	if code := Run(context.Background(), []string{"runner", "revoke"}, deps); code != ExitUsage || calls != 0 || !strings.Contains(errOut.String(), "resource id is required") {
		t.Fatalf("code=%d calls=%d stderr=%q", code, calls, errOut.String())
	}
}

func TestRunnerRejectsProjectAndScopeLocallyAndHelpOmitsThem(t *testing.T) {
	calls := 0
	deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		calls++
		return nil, errors.New("must not call")
	}), map[string]string{"MOHIST_TOKEN": "token"})
	for _, args := range [][]string{
		{"runner", "list", "--project", "project-1"},
		{"runner", "status", "--scope", "project"},
		{"runner", "view", "runner-1", "--project", "project-1"},
		{"runner", "revoke", "runner-1", "--scope", "project"},
	} {
		*out, *errOut = strings.Builder{}, strings.Builder{}
		if code := Run(context.Background(), args, deps); code != ExitUsage || calls != 0 || !strings.Contains(errOut.String(), "not supported") {
			t.Fatalf("args=%#v code=%d calls=%d stdout=%q stderr=%q", args, code, calls, out.String(), errOut.String())
		}
	}
	for _, args := range [][]string{{"runner", "--help"}, {"runner", "list", "--help"}, {"runner", "view", "--help"}} {
		*out, *errOut = strings.Builder{}, strings.Builder{}
		if code := Run(context.Background(), args, deps); code != ExitOK || strings.Contains(out.String(), "--project") || strings.Contains(out.String(), "--scope") {
			t.Fatalf("args=%#v code=%d help=%q stderr=%q", args, code, out.String(), errOut.String())
		}
	}
}
