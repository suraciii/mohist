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
