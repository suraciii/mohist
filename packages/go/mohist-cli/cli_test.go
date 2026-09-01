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

func TestRunWhyRendersLiveDiagnosisAndSeparatesStreams(t *testing.T) {
	body := `{"success":true,"data":{"workflowRunId":"wr-live","status":"Running","failure":null,"tasks":[{"taskId":"build","attempt":1,"uses":"shell","workspace":{"path":"workspace/demo","binding":"named","branch":"main"},"recovery":{"remaining":2}}],"dispatch":{"status":"present"},"events":[{"id":7,"eventId":"evt-7","type":"TaskStarted","source":"server","time":"2026-09-02T00:00:00Z"}]}}`
	deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) { return response(200, body), nil }), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})
	if code := Run(context.Background(), []string{"run", "why", "wr-live"}, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	for _, expected := range []string{"run: wr-live", "status: Running", "tasks:", "build (attempt 1)", "workspace:", "dispatch:", "events:", "TaskStarted"} {
		if !strings.Contains(out.String(), expected) {
			t.Errorf("output missing %q: %s", expected, out.String())
		}
	}
	if errOut.Len() != 0 {
		t.Fatalf("unexpected stderr=%q", errOut.String())
	}
}

func TestRunWhyRendersFailureChainWithoutProcessPaths(t *testing.T) {
	body := `{"success":true,"data":{"workflowRunId":"wr-failed","status":"Failed","failure":{"reason":"TaskFailed","stage":"verify","taskId":"check","message":"command failed","error":{"code":"exit_code","message":"exit 1"}},"tasks":[{"taskId":"check","attempt":2,"uses":"verify","workspace":{"path":"/proc/41/fd/5","binding":"named","branch":"mohist/run-wr-failed"},"exitCode":1,"error":{"code":"exit_code","message":"exit 1"},"recovery":{"budget":3,"remaining":1,"handlers":[{"when":"TaskFailed","retrySelf":true,"taskIds":["check"]}]}}],"dispatch":{"status":"present","snapshot":{"command":"run verification","path":"/proc/41/fd/6"}},"events":[{"id":9,"eventId":"evt-9","type":"TaskFailed","source":"runner","time":"2026-09-02T00:00:00Z","data":{"path":"/proc/41/fd/7"}}]}}`
	deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) { return response(200, body), nil }), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})
	if code := Run(context.Background(), []string{"run", "why", "wr-failed"}, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	for _, expected := range []string{"failure:", "TaskFailed", "command failed", "error:", "recovery:", "remaining", "TaskFailed"} {
		if !strings.Contains(out.String(), expected) {
			t.Errorf("output missing %q: %s", expected, out.String())
		}
	}
	if strings.Contains(out.String(), "/proc/") || strings.Contains(out.String(), "/fd/") {
		t.Fatalf("process path leaked: %s", out.String())
	}
	if errOut.Len() != 0 {
		t.Fatalf("unexpected stderr=%q", errOut.String())
	}
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

func TestDoctorAllChecksRenderInServerOrderAndPass(t *testing.T) {
	body := `{"success":true,"data":[{"name":"revision-alignment","status":"ok","detail":"aligned","nextAction":null},{"name":"migrations","status":"ok","detail":"current","nextAction":null}]}`
	calls := 0
	deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		calls++
		if r.URL.Path != "/api/doctor/checks" {
			t.Fatalf("path=%q", r.URL.Path)
		}
		return response(200, body), nil
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})
	if code := Run(context.Background(), []string{"doctor"}, deps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if calls != 1 || strings.Index(out.String(), "revision-alignment") > strings.Index(out.String(), "migrations") {
		t.Fatalf("calls=%d output=%q", calls, out.String())
	}
	for _, expected := range []string{"name: revision-alignment", "status: ok", "detail: aligned", "name: migrations", "detail: current"} {
		if !strings.Contains(out.String(), expected) {
			t.Errorf("output missing %q: %s", expected, out.String())
		}
	}
	if strings.Contains(out.String(), "next action:") || errOut.Len() != 0 {
		t.Fatalf("unexpected output=%q stderr=%q", out.String(), errOut.String())
	}
}

func TestDoctorMixedChecksRenderFailedNextActionAndFail(t *testing.T) {
	body := `{"success":true,"data":[{"name":"first","status":"ok","detail":"fine","nextAction":null},{"name":"second","status":"fail","detail":"broken","nextAction":"repair it"},{"name":"third","status":"fail","detail":"also broken","nextAction":"retry"}]}`
	deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) { return response(200, body), nil }), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})
	if code := Run(context.Background(), []string{"doctor"}, deps); code != ExitOperation {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if strings.Count(out.String(), "next action:") != 2 || strings.Contains(out.String(), "fine\nnext action:") {
		t.Fatalf("next-action rendering=%q", out.String())
	}
	if errOut.Len() != 0 {
		t.Fatalf("unexpected stderr=%q", errOut.String())
	}
}

func TestDoctorAllFailingChecksReturnFailureAfterRenderingAll(t *testing.T) {
	body := `{"success":true,"data":[{"name":"first","status":"fail","detail":"one","nextAction":"fix one"},{"name":"second","status":"fail","detail":"two","nextAction":"fix two"}]}`
	deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) { return response(200, body), nil }), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})
	if code := Run(context.Background(), []string{"doctor"}, deps); code != ExitOperation {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	for _, expected := range []string{"name: first", "detail: one", "next action: fix one", "name: second", "detail: two", "next action: fix two"} {
		if !strings.Contains(out.String(), expected) {
			t.Errorf("output missing %q: %s", expected, out.String())
		}
	}
}

func TestDoctorJSONDiscoveryAndProjection(t *testing.T) {
	calls := 0
	deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		calls++
		return response(200, `{"success":true,"data":[{"name":"migrations","status":"fail","detail":"pending","nextAction":"migrate"}]}`), nil
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})
	if code := Run(context.Background(), []string{"doctor", "--json"}, deps); code != ExitOK || calls != 0 || errOut.Len() != 0 {
		t.Fatalf("discovery code=%d calls=%d stdout=%q stderr=%q", code, calls, out.String(), errOut.String())
	}
	if out.String() != "name\nstatus\ndetail\nnextAction\n" {
		t.Fatalf("fields=%q", out.String())
	}
	*out, *errOut = strings.Builder{}, strings.Builder{}
	if code := Run(context.Background(), []string{"doctor", "--json", "name,nextAction"}, deps); code != ExitOperation || calls != 1 || errOut.Len() != 0 {
		t.Fatalf("projection code=%d calls=%d stdout=%q stderr=%q", code, calls, out.String(), errOut.String())
	}
	if out.String() != `[{"name":"migrations","nextAction":"migrate"}]`+"\n" {
		t.Fatalf("projection=%q", out.String())
	}
}

func TestDoctorRejectsMalformedChecksWithoutRendering(t *testing.T) {
	for _, body := range []string{
		`{"success":true,"data":{}}`,
		`{"success":true,"data":[null]}`,
		`{"success":true,"data":[{"name":"migrations","status":"unknown","detail":"bad","nextAction":null}]}`,
		`{"success":true,"data":[{"name":"migrations","status":"fail","detail":"bad"}]}`,
	} {
		deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) { return response(200, body), nil }), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})
		if code := Run(context.Background(), []string{"doctor"}, deps); code != ExitOperation || out.Len() != 0 || !strings.Contains(errOut.String(), "invalid_response") {
			t.Fatalf("body=%s code=%d stdout=%q stderr=%q", body, code, out.String(), errOut.String())
		}
	}
}

func TestClientRejectsMalformedAndFailedResponses(t *testing.T) {
	for _, body := range []string{"not-json", `{"success":false,"error":"nope","code":"forbidden"}`} {
		deps, _, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) { return response(500, body), nil }), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})
		if code := Run(context.Background(), []string{"doctor"}, deps); code != ExitOperation || errOut.Len() == 0 { t.Fatalf("body=%q code=%d stderr=%q", body, code, errOut.String()) }
	}
}

func TestRunWhyRejectsUnknownFieldBeforeHTTP(t *testing.T) {
	calls := 0
	deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) { calls++; return nil, errors.New("must not call") }), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})
	if code := Run(context.Background(), []string{"run", "why", "wr-1", "--json", "unknown"}, deps); code != ExitUsage {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if calls != 0 || out.Len() != 0 || !strings.Contains(errOut.String(), "run why <run-ref> --json") {
		t.Fatalf("calls=%d stdout=%q stderr=%q", calls, out.String(), errOut.String())
	}
}

func TestRunWhyMapsNotFoundAndMalformedPayloads(t *testing.T) {
	for _, test := range []struct {
		name string
		body string
		code int
		want string
	}{
		{name: "not found", body: `{"success":false,"error":"Workflow run 'missing' not found","code":"not_found"}`, code: ExitOperation, want: "not_found"},
		{name: "malformed diagnosis", body: `{"success":true,"data":[]}`, code: ExitOperation, want: "invalid_response"},
	} {
		t.Run(test.name, func(t *testing.T) {
			status := 404
			if test.name == "malformed diagnosis" {
				status = 200
			}
			deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) { return response(status, test.body), nil }), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})
			if code := Run(context.Background(), []string{"run", "why", "wr-1"}, deps); code != test.code {
				t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
			}
			if out.Len() != 0 || !strings.Contains(errOut.String(), test.want) {
				t.Fatalf("stdout=%q stderr=%q", out.String(), errOut.String())
			}
		})
	}
}
