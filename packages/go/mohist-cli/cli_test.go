package mohistcli

import (
	"context"
	"errors"
	"io"
	"net/http"
	"strings"
	"testing"
)

func testDeps(transport http.RoundTripper, env map[string]string) (Dependencies, *strings.Builder, *strings.Builder) {
	out, errOut := &strings.Builder{}, &strings.Builder{}
	return Dependencies{
		HTTPClient: &http.Client{Transport: transport}, Stdout: out, Stderr: errOut,
		Lookup: func(name string) (string, bool) { value, ok := env[name]; return value, ok },
		ReadFile: func(string) (string, error) { return "file-token\n", nil },
		HomeDir: func() (string, error) { return "/home/test", nil },
	}, out, errOut
}

type roundTripFunc func(*http.Request) (*http.Response, error)
func (f roundTripFunc) RoundTrip(r *http.Request) (*http.Response, error) { return f(r) }
func response(status int, body string) *http.Response { return &http.Response{StatusCode: status, Body: io.NopCloser(strings.NewReader(body)), Header: make(http.Header)} }

func TestResolveConfigPrecedenceAndSafeFailures(t *testing.T) {
	deps, _, _ := testDeps(nil, map[string]string{"MOHIST_SERVER_URL": "http://server/", "MOHIST_OPERATOR_TOKEN": " direct ", "MOHIST_OPERATOR_TOKEN_PATH": "/ignored", "MOHIST_OPERATOR_ID": "operator"})
	cfg, err := ResolveConfig(deps)
	if err != nil || cfg.ServerURL != "http://server/" || cfg.OperatorToken != "direct" || cfg.OperatorID != "operator" { t.Fatalf("config = %+v, err = %v", cfg, err) }
	deps, _, _ = testDeps(nil, map[string]string{"MOHIST_OPERATOR_TOKEN_PATH": "/token"})
	cfg, err = ResolveConfig(deps)
	if err != nil || cfg.OperatorToken != "file-token" { t.Fatalf("file config = %+v, err = %v", cfg, err) }
	deps.ReadFile = func(string) (string, error) { return "", errors.New("secret path") }
	_, err = ResolveConfig(deps)
	if err == nil || strings.Contains(err.Error(), "secret") || strings.Contains(err.Error(), "file-token") { t.Fatalf("credential error leaked secret: %v", err) }
	deps.ReadFile = func(string) (string, error) { return "  ", nil }
	_, err = ResolveConfig(deps)
	if err == nil { t.Fatal("blank credential accepted") }
}

func TestRunCommandShapeAndFieldDiscoveryDoNotCallHTTP(t *testing.T) {
	calls := 0
	deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) { calls++; return nil, errors.New("called") }), map[string]string{})
	if code := Run(context.Background(), []string{"wat"}, deps); code != ExitUsage || errOut.Len() == 0 { t.Fatalf("unknown command code=%d stderr=%q", code, errOut.String()) }
	*out, *errOut = strings.Builder{}, strings.Builder{}
	if code := Run(context.Background(), []string{"run", "why", "--json"}, deps); code != ExitOK || !strings.Contains(out.String(), "workflowRunId") || calls != 0 { t.Fatalf("field discovery code=%d output=%q calls=%d", code, out.String(), calls) }
	*out, *errOut = strings.Builder{}, strings.Builder{}
	if code := Run(context.Background(), []string{"doctor", "extra"}, deps); code != ExitUsage { t.Fatalf("incomplete doctor code=%d stderr=%q", code, errOut.String()) }
}

func TestRunSendsAuthenticatedRequestAndProjectsJSON(t *testing.T) {
	var got *http.Request
	deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) { got = r; return response(200, `{"success":true,"data":{"workflowRunId":"wr-1","status":"Failed"}}`), nil }), map[string]string{"MOHIST_SERVER_URL": "http://server", "MOHIST_OPERATOR_TOKEN": "token", "MOHIST_OPERATOR_ID": "cli-test"})
	if code := Run(context.Background(), []string{"run", "why", "wr/1", "--json", "workflowRunId,status"}, deps); code != ExitOK { t.Fatalf("code=%d stderr=%q", code, errOut.String()) }
	if got.URL.EscapedPath() != "/api/runs/wr%2F1/diagnosis" || got.Header.Get("Authorization") != "Bearer token" || got.Header.Get(operatorIDHeader) != "cli-test" || got.Header.Get("Accept") != "application/json" { t.Fatalf("request = %v headers=%v", got.URL, got.Header) }
	if out.String() != `{"status":"Failed","workflowRunId":"wr-1"}`+"\n" { t.Fatalf("output=%q", out.String()) }
}

func TestRunMapsTransportAndCancellation(t *testing.T) {
	deps, _, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) { return nil, errors.New("network secret") }), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})
	if code := Run(context.Background(), []string{"doctor"}, deps); code != ExitOperation || strings.Contains(errOut.String(), "network secret") { t.Fatalf("transport code=%d stderr=%q", code, errOut.String()) }
	ctx, cancel := context.WithCancel(context.Background()); cancel()
	if code := Run(ctx, []string{"doctor"}, deps); code != ExitCanceled { t.Fatalf("cancel code=%d", code) }
}

func TestDoctorFailureReturnsOperationExit(t *testing.T) {
	deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) { return response(200, `{"success":true,"data":[{"name":"migrations","status":"fail","detail":"pending","nextAction":"migrate"}]}`), nil }), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})
	if code := Run(context.Background(), []string{"doctor"}, deps); code != ExitOperation || !strings.Contains(out.String(), "migrations") || errOut.Len() != 0 { t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String()) }
}

func TestClientRejectsMalformedAndFailedResponses(t *testing.T) {
	for _, body := range []string{"not-json", `{"success":false,"error":"nope","code":"forbidden"}`} {
		deps, _, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) { return response(500, body), nil }), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})
		if code := Run(context.Background(), []string{"doctor"}, deps); code != ExitOperation || errOut.Len() == 0 { t.Fatalf("body=%q code=%d stderr=%q", body, code, errOut.String()) }
	}
}
