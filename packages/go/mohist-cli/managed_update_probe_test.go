package mohistcli

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"reflect"
	"strings"
	"testing"
)

func TestRealManagedControlPlaneObserveRunnerUsesRequiredEscapedRunnerID(t *testing.T) {
	runnerID := "runner/pluto+ops /?"
	var request *http.Request
	control := newManagedProbeTestControl(t, func(got *http.Request) (*http.Response, error) {
		request = got
		return managedProbeTestResponse(`{
  "runnerId": "runner/pluto+ops /?",
  "buildGitHash": "build-0123",
  "component": "runner",
  "version": "0.0.0+0123",
  "sourceRevision": "source-0123",
  "treeHash": "tree-0123",
  "artifactDigest": "digest-0123",
  "releaseId": "mohist-runner-0123",
  "generation": 41,
  "status": "online",
  "connectionState": "connected",
  "connectionGeneration": "connection-41"
}`), nil
	})

	got, err := control.ObserveRunner(context.Background(), runnerID)
	if err != nil {
		t.Fatalf("ObserveRunner() error = %v", err)
	}
	want := managedRuntimeObservation{
		Identity: managedRuntimeIdentity{
			Component: "runner", Version: "0.0.0+0123", SourceRevision: "source-0123",
			TreeHash: "tree-0123", ArtifactDigest: "digest-0123", ReleaseID: "mohist-runner-0123",
			Generation: 41, RunnerID: runnerID, BuildGitHash: "build-0123",
		},
		Status: "online", ConnectionState: "connected", ConnectionGeneration: "connection-41",
	}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("observation = %#v, want %#v", got, want)
	}
	if request == nil {
		t.Fatal("ObserveRunner() did not make the expected request")
	}
	wantPath := "/api/runner/identity?runnerId=" + url.QueryEscape(runnerID)
	gotPath := request.URL.EscapedPath() + "?" + request.URL.RawQuery
	if gotPath != wantPath {
		t.Fatalf("request path = %q, want exact %q", gotPath, wantPath)
	}
	if request.Method != http.MethodGet {
		t.Fatalf("request method = %q, want GET", request.Method)
	}
}

func TestRealManagedControlPlaneObserveRunnerRejectsBlankIDWithoutHTTP(t *testing.T) {
	for _, runnerID := range []string{"", "   \t"} {
		t.Run(map[string]string{"": "empty", "   \t": "whitespace"}[runnerID], func(t *testing.T) {
			calls := 0
			control := newManagedProbeTestControl(t, func(*http.Request) (*http.Response, error) {
				calls++
				return nil, errors.New("unexpected HTTP request")
			})

			_, err := control.ObserveRunner(context.Background(), runnerID)
			if err == nil || !strings.Contains(err.Error(), "identity is unavailable") {
				t.Fatalf("ObserveRunner(%q) error = %v, want local blank-ID rejection", runnerID, err)
			}
			if calls != 0 {
				t.Fatalf("HTTP calls for blank Runner ID = %d, want 0", calls)
			}
		})
	}
}

func TestRealManagedControlPlaneUsesManagerCredentialBrokerForReadAndMutation(t *testing.T) {
	directCalls := 0
	brokerCalls := []string{}
	deps := Dependencies{
		HTTPClient: &http.Client{Transport: managedProbeTestRoundTripper(func(*http.Request) (*http.Response, error) {
			directCalls++
			return nil, errors.New("direct HTTP transport must not be used")
		})},
		Lookup: func(name string) (string, bool) {
			values := map[string]string{
				"MOHIST_SERVER_URL":     "http://mohist.test",
				"MOHIST_OPERATOR_TOKEN": "local-secret",
				"MOHIST_OPERATOR_ID":    "manager-probe",
				"MOHIST_MANAGER_MODE":   "1",
			}
			value, ok := values[name]
			return value, ok
		},
		ReadFile: func(string) (string, error) { return "", errors.New("unexpected credential file read") },
		HomeDir:  func() (string, error) { return "/home/test", nil },
		Stdout:   io.Discard,
		Stderr:   io.Discard,
	}
	deps.ManagerCredentialBroker = func(_ context.Context, request *http.Request) (*http.Response, error) {
		if request.Header.Get("X-Mohist-Manager-Mode") != "1" {
			t.Fatalf("manager marker = %q, want 1", request.Header.Get("X-Mohist-Manager-Mode"))
		}
		if authorization := request.Header.Get("Authorization"); authorization != "" {
			t.Fatalf("manager request leaked local bearer credential: %q", authorization)
		}
		brokerCalls = append(brokerCalls, request.Method+" "+request.URL.EscapedPath())
		switch request.URL.EscapedPath() {
		case "/api/health":
			return managedProbeTestResponse(`{"status":"ok"}`), nil
		case "/api/runner/runner-pluto/update-interrupt":
			return managedProbeTestResponse(`{"runnerId":"runner-pluto","status":"draining","updateInterruptId":"interrupt-1","activeWorkCount":0}`), nil
		case "/api/runner/runner-pluto/update-interrupt/interrupt-1/cancel":
			return managedProbeTestResponse(`{"runnerId":"runner-pluto","status":"cancelled","updateInterruptId":"interrupt-1"}`), nil
		default:
			return nil, fmt.Errorf("unexpected broker request: %s", request.URL.EscapedPath())
		}
	}

	control, err := newRealManagedControlPlane(deps)
	if err != nil {
		t.Fatalf("newRealManagedControlPlane() error = %v", err)
	}
	if _, err := control.ObserveServer(context.Background()); err != nil {
		t.Fatalf("ObserveServer() error = %v", err)
	}
	if _, err := control.BeginRunnerInterrupt(context.Background(), "runner-pluto", "interrupt-1"); err != nil {
		t.Fatalf("BeginRunnerInterrupt() error = %v", err)
	}
	if err := control.CancelRunnerInterrupt(context.Background(), "runner-pluto", "interrupt-1"); err != nil {
		t.Fatalf("CancelRunnerInterrupt() error = %v", err)
	}

	if directCalls != 0 {
		t.Fatalf("direct HTTP calls = %d, want 0", directCalls)
	}
	wantCalls := []string{
		"GET /api/health",
		"POST /api/runner/runner-pluto/update-interrupt",
		"POST /api/runner/runner-pluto/update-interrupt/interrupt-1/cancel",
	}
	if !reflect.DeepEqual(brokerCalls, wantCalls) {
		t.Fatalf("broker calls = %v, want %v", brokerCalls, wantCalls)
	}
}

func TestRealManagedControlPlaneManagerModeRequiresBroker(t *testing.T) {
	deps := Dependencies{
		HTTPClient: http.DefaultClient,
		Lookup: func(name string) (string, bool) {
			values := map[string]string{
				"MOHIST_SERVER_URL":   "http://mohist.test",
				"MOHIST_MANAGER_MODE": "1",
			}
			value, ok := values[name]
			return value, ok
		},
		ReadFile: func(string) (string, error) { return "", errors.New("not found") },
		HomeDir:  func() (string, error) { return "/home/test", nil },
	}

	_, err := newRealManagedControlPlane(deps)
	if err == nil || err.Error() != "Manager credential broker is unavailable" {
		t.Fatalf("newRealManagedControlPlane() error = %v", err)
	}
}

func TestVerifyManagedObservationFailsClosedForRunnerIdentityAndConnection(t *testing.T) {
	expected := managedProbeTestIdentity()
	tests := []struct {
		name    string
		mutate  func(*managedRuntimeObservation)
		wantErr string
	}{
		{
			name: "missing runner id",
			mutate: func(observation *managedRuntimeObservation) {
				observation.Identity.RunnerID = ""
			},
			wantErr: "runnerId",
		},
		{
			name: "different runner id",
			mutate: func(observation *managedRuntimeObservation) {
				observation.Identity.RunnerID = "runner-other"
			},
			wantErr: "runnerId",
		},
		{
			name: "missing connection generation",
			mutate: func(observation *managedRuntimeObservation) {
				observation.ConnectionGeneration = ""
			},
			wantErr: "new connection generation",
		},
		{
			name: "reused connection generation",
			mutate: func(observation *managedRuntimeObservation) {
				observation.ConnectionGeneration = "connection-old"
			},
			wantErr: "new connection generation",
		},
		{
			name:   "valid new connection",
			mutate: func(*managedRuntimeObservation) {},
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			observation := managedRuntimeObservation{
				Identity: expected, Status: "online", ConnectionState: "connected", ConnectionGeneration: "connection-new",
			}
			test.mutate(&observation)
			err := verifyManagedObservation("runner", observation, expected, "connection-old")
			if test.wantErr == "" {
				if err != nil {
					t.Fatalf("verifyManagedObservation() error = %v", err)
				}
				return
			}
			if err == nil || !strings.Contains(err.Error(), test.wantErr) {
				t.Fatalf("verifyManagedObservation() error = %v, want %q", err, test.wantErr)
			}
		})
	}
}

func managedProbeTestIdentity() managedRuntimeIdentity {
	return managedRuntimeIdentity{
		Component: "runner", Version: "0.0.0+0123", SourceRevision: "source-0123",
		TreeHash: "tree-0123", ArtifactDigest: "digest-0123", ReleaseID: "mohist-runner-0123",
		Generation: 41, RunnerID: "runner-pluto", BuildGitHash: "build-0123",
	}
}

func newManagedProbeTestControl(t *testing.T, roundTrip func(*http.Request) (*http.Response, error)) *realManagedControlPlane {
	t.Helper()
	deps := Dependencies{
		HTTPClient: &http.Client{Transport: managedProbeTestRoundTripper(roundTrip)},
		Lookup: func(name string) (string, bool) {
			values := map[string]string{
				"MOHIST_SERVER_URL":     "http://mohist.test",
				"MOHIST_OPERATOR_TOKEN": "test-token",
				"MOHIST_OPERATOR_ID":    "probe-test",
			}
			value, ok := values[name]
			return value, ok
		},
		ReadFile: func(string) (string, error) { return "", errors.New("unexpected credential file read") },
		HomeDir:  func() (string, error) { return "/home/test", nil },
		Stdout:   io.Discard,
		Stderr:   io.Discard,
	}
	control, err := newRealManagedControlPlane(deps)
	if err != nil {
		t.Fatalf("newRealManagedControlPlane() error = %v", err)
	}
	return control
}

type managedProbeTestRoundTripper func(*http.Request) (*http.Response, error)

func (roundTrip managedProbeTestRoundTripper) RoundTrip(request *http.Request) (*http.Response, error) {
	return roundTrip(request)
}

func managedProbeTestResponse(runnerJSON string) *http.Response {
	payload, err := json.Marshal(struct {
		Success bool            `json:"success"`
		Data    json.RawMessage `json:"data"`
	}{Success: true, Data: json.RawMessage(runnerJSON)})
	if err != nil {
		panic(err)
	}
	return &http.Response{
		StatusCode: http.StatusOK,
		Header:     make(http.Header),
		Body:       io.NopCloser(strings.NewReader(string(payload))),
	}
}
