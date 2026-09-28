package mohistcli

import (
	"context"
	"embed"
	"encoding/json"
	"io"
	"net/http"
	"os"
	"reflect"
	"strings"
	"testing"
	"time"
)

// The fixtures are embedded so parity tests cannot accidentally depend on the
// checkout, current directory, or a generated service artifact.
//
//go:embed testdata/parity
var parityFixtures embed.FS

type parityContract struct {
	Command      string        `json:"command"`
	Catalog      []string      `json:"catalog"`
	HelpArgs     []string      `json:"helpArgs"`
	HumanArgs    []string      `json:"humanArgs"`
	SelectedArgs []string      `json:"selectedArgs"`
	UsageArgs    []string      `json:"usageArgs"`
	Request      parityRequest `json:"request"`
}

type parityRequest struct {
	Method  string      `json:"method"`
	Path    string      `json:"path"`
	Headers http.Header `json:"headers"`
	Body    string      `json:"body"`
}

type parityTransport struct {
	responseBody string
	requests     []*http.Request
}

func (t *parityTransport) RoundTrip(request *http.Request) (*http.Response, error) {
	t.requests = append(t.requests, request)
	return &http.Response{
		StatusCode: http.StatusOK,
		Header:     make(http.Header),
		Body:       io.NopCloser(strings.NewReader(t.responseBody)),
		Request:    request,
	}, nil
}

func readParityFixture(t *testing.T, dir, name string) []byte {
	t.Helper()
	data, err := parityFixtures.ReadFile("testdata/parity/" + dir + "/" + name)
	if err != nil {
		t.Fatalf("read parity fixture %q: %v", name, err)
	}
	return data
}

func parityDeps(transport *parityTransport) (Dependencies, *strings.Builder, *strings.Builder) {
	stdout, stderr := &strings.Builder{}, &strings.Builder{}
	return Dependencies{
		HTTPClient: &http.Client{Transport: transport},
		Stdout:     stdout,
		Stderr:     stderr,
		Lookup: func(name string) (string, bool) {
			values := map[string]string{
				"MOHIST_SERVER_URL":  "http://parity.example",
				"MOHIST_TOKEN":       "parity-token",
				"MOHIST_OPERATOR_ID": "parity-test",
			}
			value, ok := values[name]
			return value, ok
		},
		ReadFile:    func(string) (string, error) { return "", io.EOF },
		HomeDir:     func() (string, error) { return "/parity-home", nil },
		WriteFile:   func(string, string, os.FileMode) error { return nil },
		Execute:     func(context.Context, string, []string) error { return nil },
		OpenBrowser: func(context.Context, string, []string) error { return nil },
		Input:       strings.NewReader(""),
		Now:         func() time.Time { return time.Unix(0, 0) },
		Wait:        func(context.Context, time.Duration) error { return nil },
		Executable:  func() string { return "mo" },
	}, stdout, stderr
}

func TestRunWhyParityContract(t *testing.T) {
	var contract parityContract
	if err := json.Unmarshal(readParityFixture(t, "run-why", "contract.json"), &contract); err != nil {
		t.Fatalf("decode contract: %v", err)
	}
	if contract.Command != "run why" {
		t.Fatalf("unexpected parity registration: %q", contract.Command)
	}
	if !reflect.DeepEqual(contract.Catalog, diagnosisFields) {
		t.Fatalf("field catalog = %v, want %v", contract.Catalog, diagnosisFields)
	}

	transport := &parityTransport{responseBody: string(readParityFixture(t, "run-why", "response.json"))}
	deps, stdout, stderr := parityDeps(transport)

	if code := Run(context.Background(), contract.HelpArgs, deps); code != ExitOK {
		t.Fatalf("help exit code = %d", code)
	}
	if stdout.String() != string(readParityFixture(t, "run-why", "help.stdout")) || stderr.Len() != 0 {
		t.Fatalf("help output stdout=%q stderr=%q", stdout.String(), stderr.String())
	}
	if len(transport.requests) != 0 {
		t.Fatalf("help issued %d requests", len(transport.requests))
	}

	*stdout, *stderr = strings.Builder{}, strings.Builder{}
	if code := Run(context.Background(), contract.HumanArgs, deps); code != ExitOK {
		t.Fatalf("human exit code = %d", code)
	}
	if stdout.String() != string(readParityFixture(t, "run-why", "human.stdout")) || stderr.Len() != 0 {
		t.Fatalf("human output stdout=%q stderr=%q", stdout.String(), stderr.String())
	}
	assertParityRequest(t, transport, contract.Request)

	*stdout, *stderr = strings.Builder{}, strings.Builder{}
	if code := Run(context.Background(), contract.SelectedArgs, deps); code != ExitOK {
		t.Fatalf("selected JSON exit code = %d", code)
	}
	if stdout.String() != string(readParityFixture(t, "run-why", "selected.stdout")) || stderr.Len() != 0 {
		t.Fatalf("selected output stdout=%q stderr=%q", stdout.String(), stderr.String())
	}
	if len(transport.requests) != 2 {
		t.Fatalf("selected JSON request count = %d, want 2", len(transport.requests))
	}

	*stdout, *stderr = strings.Builder{}, strings.Builder{}
	if code := Run(context.Background(), contract.UsageArgs, deps); code != ExitUsage {
		t.Fatalf("usage exit code = %d", code)
	}
	if stdout.Len() != 0 || stderr.String() != string(readParityFixture(t, "run-why", "usage.stderr")) {
		t.Fatalf("usage output stdout=%q stderr=%q", stdout.String(), stderr.String())
	}
	if len(transport.requests) != 2 {
		t.Fatalf("usage issued a request; count = %d", len(transport.requests))
	}
}

// TestRunWhyDispatchActiveWorkParity pins the human rendering of the
// persisted active-work observation for the three dispatch states the
// diagnosis contract distinguishes: snapshot missing with matching active
// work, snapshot missing with divergent active work, and neither snapshot
// nor active work.
func TestRunWhyDispatchActiveWorkParity(t *testing.T) {
	for _, scenario := range []string{"active-match", "active-mismatch", "active-none"} {
		t.Run(scenario, func(t *testing.T) {
			dir := "run-why-dispatch-" + scenario
			transport := &parityTransport{responseBody: string(readParityFixture(t, dir, "response.json"))}
			deps, stdout, stderr := parityDeps(transport)

			if code := Run(context.Background(), []string{"run", "why", "wr-1"}, deps); code != ExitOK {
				t.Fatalf("human exit code = %d stderr=%q", code, stderr.String())
			}
			if stdout.String() != string(readParityFixture(t, dir, "human.stdout")) || stderr.Len() != 0 {
				t.Fatalf("human output stdout=%q stderr=%q", stdout.String(), stderr.String())
			}
			if len(transport.requests) != 1 || transport.requests[0].URL.EscapedPath() != "/api/runs/wr-1/diagnosis" {
				t.Fatalf("requests = %v", transport.requests)
			}
		})
	}
}

func assertParityRequest(t *testing.T, transport *parityTransport, want parityRequest) {
	t.Helper()
	if len(transport.requests) == 0 {
		t.Fatal("command issued no request")
	}
	got := transport.requests[len(transport.requests)-1]
	body, err := io.ReadAll(got.Body)
	if err != nil {
		t.Fatalf("read request body: %v", err)
	}
	if got.Method != want.Method || got.URL.EscapedPath() != want.Path || !reflect.DeepEqual(got.Header, want.Headers) || string(body) != want.Body {
		t.Fatalf("request = method %q path %q headers %v body %q; want method %q path %q headers %v body %q", got.Method, got.URL.EscapedPath(), got.Header, string(body), want.Method, want.Path, want.Headers, want.Body)
	}
}
