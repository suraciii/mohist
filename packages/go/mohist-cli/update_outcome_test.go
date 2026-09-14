package mohistcli

import (
	"context"
	"encoding/json"
	"net/http"
	"testing"
	"time"
)

func TestUpdateOutcomeReporterPostsStagesAndTerminalOutcome(t *testing.T) {
	var requests []map[string]any
	deps, _, _ := testDeps(roundTripFunc(func(request *http.Request) (*http.Response, error) {
		var body map[string]any
		if err := json.NewDecoder(request.Body).Decode(&body); err != nil {
			return nil, err
		}
		requests = append(requests, body)
		return response(http.StatusOK, `{"success":true,"data":{}}`), nil
	}), map[string]string{"MOHIST_SERVER_URL": "http://server", "MOHIST_TOKEN": "operator"})
	deps.Now = func() time.Time { return time.Date(2026, 1, 1, 0, 0, 0, 0, time.UTC) }
	reporter := newUpdateOutcomeReporter(context.Background(), deps, command{kind: "update-server"}, "/repo")
	reporter.stage(context.Background(), deps, "Building server", "candidate staged")
	reporter.stage(context.Background(), deps, "Verifying runtime", "identity checked")
	reporter.finish(context.Background(), deps, ExitOK)

	if len(requests) != 3 {
		t.Fatalf("requests = %d, want 3", len(requests))
	}
	if requests[0]["status"] != "running" || requests[1]["status"] != "running" || requests[2]["status"] != "succeeded" {
		t.Fatalf("statuses = %#v", requests)
	}
	if requests[2]["outcome"] != "succeeded" || requests[2]["sourcePath"] != "/repo" {
		t.Fatalf("terminal request = %#v", requests[2])
	}
}
