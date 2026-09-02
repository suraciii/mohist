package mohistcli

import (
	"context"
	"errors"
	"net/http"
	"strings"
	"testing"
)

func organizationDeps(transport http.RoundTripper) (Dependencies, *strings.Builder, *strings.Builder) {
	out, errOut := &strings.Builder{}, &strings.Builder{}
	return Dependencies{
		HTTPClient: &http.Client{Transport: transport}, Stdout: out, Stderr: errOut,
		Lookup: func(name string) (string, bool) {
			if name == "MOHIST_TOKEN" {
				return "token", true
			}
			if name == "MOHIST_SERVER_URL" {
				return "http://server", true
			}
			return "", false
		},
		ReadFile: func(path string) (string, error) {
			if path == "/work/tree/.mohist/cli-state.json" {
				return `{"activeProjectId":"proj"}`, nil
			}
			return "body", nil
		},
		HomeDir: func() (string, error) { return "/home/test", nil }, CurrentDirectory: func() string { return "/work/tree" },
	}, out, errOut
}

func TestIssueLifecycleUsesOwningIssueRoute(t *testing.T) {
	var got *http.Request
	deps, out, errOut := organizationDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		got = r
		return response(http.StatusOK, `{"success":true,"data":{"number":42,"status":"Done"}}`), nil
	}))
	if code := Run(context.Background(), []string{"issue", "done", "42", "--project", "proj", "--json", "number,status"}, deps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if got == nil || got.Method != http.MethodPost || got.URL.Path != "/api/projects/proj/issues/42/done" {
		t.Fatalf("request=%v", got)
	}
	if out.String() != `{"number":42,"status":"Done"}`+"\n" {
		t.Fatalf("output=%q", out.String())
	}
}

func TestIssueInvalidFlagsDoNotCallHTTP(t *testing.T) {
	calls := 0
	deps, _, _ := organizationDeps(roundTripFunc(func(*http.Request) (*http.Response, error) { calls++; return nil, errors.New("called") }))
	if code := Run(context.Background(), []string{"issue", "list", "--all", "--archived", "--project", "proj"}, deps); code != ExitUsage {
		t.Fatalf("code=%d", code)
	}
	if calls != 0 {
		t.Fatalf("calls=%d", calls)
	}
}

func TestEpicEmptyListIsSuccessful(t *testing.T) {
	deps, out, errOut := organizationDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		return response(http.StatusOK, `{"success":true,"data":[]}`), nil
	}))
	if code := Run(context.Background(), []string{"epic", "list", "--project", "proj"}, deps); code != ExitOK || out.String() != "No epics\n" || errOut.Len() != 0 {
		t.Fatalf("code=%d out=%q err=%q", code, out.String(), errOut.String())
	}
}

func TestLabelMalformedResponseIsRejected(t *testing.T) {
	deps, _, errOut := organizationDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		return response(http.StatusOK, `{"success":true,"data":{}}`), nil
	}))
	if code := Run(context.Background(), []string{"label", "list", "--project", "proj", "--json", "key"}, deps); code != ExitOperation || !strings.Contains(errOut.String(), "invalid shape") {
		t.Fatalf("code=%d err=%q", code, errOut.String())
	}
}

func TestIssueCancellationReturns130(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	deps, _, _ := organizationDeps(roundTripFunc(func(*http.Request) (*http.Response, error) { return nil, context.Canceled }))
	if code := Run(ctx, []string{"issue", "view", "42", "--project", "proj"}, deps); code != ExitCanceled {
		t.Fatalf("code=%d", code)
	}
}

func TestIssueViewRendersBoundedHumanOutput(t *testing.T) {
	deps, out, errOut := organizationDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		return response(http.StatusOK, `{"success":true,"data":{"number":42,"title":"Ship it","status":"Ready","workflowStatus":"Running","workflowStage":"verify","workflowRunId":"wr-42","priority":"high","repositoryName":"mohist","blocker":"waiting on review","body":"first line\nsecond line","comments":[{"body":"secret"}],"feedback":[{"text":"secret"}],"children":[{"number":43}],"attachments":[{"url":"secret"}],"labels":["bug"]}}`), nil
	}))
	if code := Run(context.Background(), []string{"issue", "view", "42", "--project", "proj"}, deps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	for _, expected := range []string{"Number: 42", "Title: Ship it", "Status: Ready", "Workflow:", "Priority: high", "Repository: mohist", "Blocker: waiting on review", "Body:\nfirst line\nsecond line", "Comments: 1", "Feedback: 1", "Children: 1", "Attachments: 1", "Labels: 1"} {
		if !strings.Contains(out.String(), expected) {
			t.Errorf("output missing %q: %s", expected, out.String())
		}
	}
	if strings.Contains(out.String(), `{"body":"secret"}`) || errOut.Len() != 0 {
		t.Fatalf("unbounded or stderr output: stdout=%q stderr=%q", out.String(), errOut.String())
	}
}

func TestIssueViewOmitsMissingOptionalFieldsAndSanitizesText(t *testing.T) {
	deps, out, errOut := organizationDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		return response(http.StatusOK, `{"success":true,"data":{"number":42,"title":"\u001b[31mShip\u001b[0m","status":"Ready","body":"line\u001b[2K\nnext","comments":null,"children":[]}}`), nil
	}))
	if code := Run(context.Background(), []string{"issue", "view", "42", "--project", "proj"}, deps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if strings.Contains(out.String(), "\x1b") || strings.Contains(out.String(), "Priority:") || strings.Contains(out.String(), "Blocker:") || strings.Contains(out.String(), "Comments:") || strings.Contains(out.String(), "Children:") {
		t.Fatalf("unexpected output=%q", out.String())
	}
	if !strings.Contains(out.String(), "Title: Ship") || !strings.Contains(out.String(), "Body:\nline\nnext") || errOut.Len() != 0 {
		t.Fatalf("output=%q stderr=%q", out.String(), errOut.String())
	}
}

func TestIssueViewMalformedTopLevelResponseFails(t *testing.T) {
	deps, out, errOut := organizationDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		return response(http.StatusOK, `{"success":true,"data":[]}`), nil
	}))
	if code := Run(context.Background(), []string{"issue", "view", "42", "--project", "proj"}, deps); code != ExitOperation || out.Len() != 0 || !strings.Contains(errOut.String(), "invalid_response") {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
}

func TestIssueViewSelectedJSONRemainsJSON(t *testing.T) {
	deps, out, errOut := organizationDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		return response(http.StatusOK, `{"success":true,"data":{"number":42,"title":"Ship it","comments":[{"body":"secret"}]}}`), nil
	}))
	if code := Run(context.Background(), []string{"issue", "view", "42", "--project", "proj", "--json", "number,title"}, deps); code != ExitOK || out.String() != `{"number":42,"title":"Ship it"}`+"\n" || errOut.Len() != 0 {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
}

func TestIssueViewBareJSONListsLocalCatalogWithoutHTTP(t *testing.T) {
	calls := 0
	deps, out, errOut := organizationDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		calls++
		return nil, errors.New("must not call")
	}))
	if code := Run(context.Background(), []string{"issue", "view", "42", "--project", "proj", "--json"}, deps); code != ExitOK || calls != 0 || !strings.Contains(out.String(), "comments") || errOut.Len() != 0 {
		t.Fatalf("code=%d calls=%d stdout=%q stderr=%q", code, calls, out.String(), errOut.String())
	}
}
