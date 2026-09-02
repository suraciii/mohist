package mohistcli

import (
	"context"
	"errors"
	"io"
	"net/http"
	"os"
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
	if code != ExitUsage || calls != 0 || !strings.Contains(errOut.String(), "--workspace") {
		t.Fatalf("code=%d calls=%d stderr=%q", code, calls, errOut.String())
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
