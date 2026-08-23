package mohistslack

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"reflect"
	"strings"
	"testing"
)

type capturedRequest struct {
	Method      string
	Path        string
	Auth        string
	OperatorID  string
	ContentType string
	Body        map[string]any
}

func serveAPI(t *testing.T, status int, response string, captured *capturedRequest) *ServerAPI {
	t.Helper()
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		body, _ := io.ReadAll(r.Body)
		captured.Method = r.Method
		captured.Path = requestPath(r)
		captured.Auth = r.Header.Get("Authorization")
		captured.OperatorID = r.Header.Get("x-mohist-operator-id")
		captured.ContentType = r.Header.Get("Content-Type")
		if len(body) > 0 {
			if err := json.Unmarshal(body, &captured.Body); err != nil {
				t.Errorf("request body is not JSON: %v", err)
			}
		}
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(status)
		_, _ = w.Write([]byte(response))
	}))
	t.Cleanup(server.Close)
	api, err := NewServerAPI(server.URL, "op-token", "op-id")
	if err != nil {
		t.Fatalf("NewServerAPI() error = %v", err)
	}
	return api
}

func strPointer(value string) *string { return &value }

// requestPath reports the escaped wire path; net/url decodes Path even for
// segments containing an escaped slash.
func requestPath(r *http.Request) string {
	if r.URL.RawPath != "" {
		return r.URL.RawPath
	}
	return r.URL.Path
}

func TestNewServerAPIRejectsNonLoopbackServers(t *testing.T) {
	cases := []struct {
		name    string
		url     string
		accepts bool
	}{
		{"localhost", "http://localhost:3456", true},
		{"loopback ipv4", "http://127.0.0.1:3456", true},
		{"loopback range", "http://127.9.9.9", true},
		{"ipv6 loopback", "http://[::1]:3456", true},
		{"https allowed", "https://localhost:3456", true},
		{"remote host", "http://example.com", false},
		{"public ipv4", "http://10.0.0.1", false},
		{"ftp scheme", "ftp://localhost", false},
		{"unparseable", "://", false},
	}
	for _, testCase := range cases {
		t.Run(testCase.name, func(t *testing.T) {
			_, err := NewServerAPI(testCase.url, "token", "id")
			if testCase.accepts && err != nil {
				t.Fatalf("NewServerAPI(%q) error = %v, want nil", testCase.url, err)
			}
			if !testCase.accepts && err == nil {
				t.Fatalf("NewServerAPI(%q) accepted a non-loopback server", testCase.url)
			}
		})
	}
}

func TestDiscoverRequestsTargetsAndParsesBothKinds(t *testing.T) {
	var captured capturedRequest
	response := `{"success":true,"data":[` +
		`{"kind":"connection","projectId":"proj_1","connectionId":"conn_1"},` +
		`{"kind":"manager","enrollmentId":"enr_1","workspaceTeamId":"T123"}]}`
	api := serveAPI(t, http.StatusOK, response, &captured)

	targets, err := api.Discover(context.Background())
	if err != nil {
		t.Fatalf("Discover() error = %v", err)
	}
	if captured.Method != http.MethodGet || captured.Path != "/api/slack-adapter/leases/targets" {
		t.Fatalf("Discover() sent %s %s", captured.Method, captured.Path)
	}
	if captured.Auth != "Bearer op-token" || captured.OperatorID != "op-id" {
		t.Fatalf("Discover() sent auth=%q operator=%q", captured.Auth, captured.OperatorID)
	}
	if len(targets) != 2 {
		t.Fatalf("Discover() returned %d targets, want 2", len(targets))
	}
	connection, ok := targets[0].(ConnectionTarget)
	if !ok || connection.ProjectID != "proj_1" || connection.ConnectionID != "conn_1" {
		t.Fatalf("targets[0] = %#v, want ConnectionTarget", targets[0])
	}
	if targets[0].Key() != "connection:proj_1:conn_1" {
		t.Fatalf("connection key = %q", targets[0].Key())
	}
	manager, ok := targets[1].(ManagerTarget)
	if !ok || manager.EnrollmentID != "enr_1" || manager.WorkspaceTeamID != "T123" {
		t.Fatalf("targets[1] = %#v, want ManagerTarget", targets[1])
	}
	if targets[1].Key() != "manager:enr_1" {
		t.Fatalf("manager key = %q", targets[1].Key())
	}
}

func TestDiscoverRejectsInvalidPayloads(t *testing.T) {
	cases := []struct {
		name     string
		response string
		wantText string
	}{
		{"data not array", `{"success":true,"data":{"kind":"connection"}}`, "invalid response"},
		{"unknown kind", `{"success":true,"data":[{"kind":"other"}]}`, "invalid target kind"},
		{"manager missing team", `{"success":true,"data":[{"kind":"manager","enrollmentId":"e"}]}`, "invalid Manager target"},
		{"connection missing project", `{"success":true,"data":[{"kind":"connection","connectionId":"c"}]}`, "invalid target"},
	}
	for _, testCase := range cases {
		t.Run(testCase.name, func(t *testing.T) {
			var captured capturedRequest
			api := serveAPI(t, http.StatusOK, testCase.response, &captured)
			if _, err := api.Discover(context.Background()); err == nil || !strings.Contains(err.Error(), testCase.wantText) {
				t.Fatalf("Discover() error = %v, want containing %q", err, testCase.wantText)
			}
		})
	}
}

func TestAcquireLeaseParsesBothPhases(t *testing.T) {
	var captured capturedRequest
	response := `{"success":true,"data":{"leaseId":"lease_1","appToken":"xapp_1","botToken":"xoxb_1","expiresAt":"2026-08-21T00:00:00Z","generation":3}}`
	api := serveAPI(t, http.StatusOK, response, &captured)
	target := ConnectionTarget{ProjectID: "p", ConnectionID: "c"}

	lease, err := api.AcquireLease(context.Background(), target, LeaseRuntime, "adapter-1")
	if err != nil {
		t.Fatalf("AcquireLease() error = %v", err)
	}
	runtime, ok := lease.(RuntimeLease)
	if !ok || runtime.LeaseID != "lease_1" || runtime.AppToken != "xapp_1" || runtime.BotToken != "xoxb_1" || runtime.Generation != 3 {
		t.Fatalf("lease = %#v, want RuntimeLease", lease)
	}
	if captured.Path != "/api/slack-adapter/leases/acquire" {
		t.Fatalf("path = %q", captured.Path)
	}
	if captured.Body["kind"] != string(LeaseRuntime) || captured.Body["adapterId"] != "adapter-1" {
		t.Fatalf("body = %v", captured.Body)
	}
	requestTarget, ok := captured.Body["target"].(map[string]any)
	if !ok || requestTarget["kind"] != "connection" || requestTarget["projectId"] != "p" {
		t.Fatalf("target body = %v", captured.Body["target"])
	}

	validationResponse := `{"success":true,"data":{"leaseId":"lease_2","appToken":"xapp_2","expectedAppId":"A1","expiresAt":"2026-08-21T00:00:00Z","generation":1}}`
	api = serveAPI(t, http.StatusOK, validationResponse, &captured)
	lease, err = api.AcquireLease(context.Background(), target, LeaseValidation, "adapter-1")
	if err != nil {
		t.Fatalf("AcquireLease(validation) error = %v", err)
	}
	validation, ok := lease.(ValidationLease)
	if !ok || validation.ExpectedAppID != "A1" {
		t.Fatalf("lease = %#v, want ValidationLease", lease)
	}
}

func TestAcquireLeaseReturnsNilWhenNotAcquirable(t *testing.T) {
	var captured capturedRequest
	api := serveAPI(t, http.StatusConflict, `{"success":false,"code":"lease_not_acquirable"}`, &captured)

	lease, err := api.AcquireLease(context.Background(), ConnectionTarget{ProjectID: "p", ConnectionID: "c"}, LeaseRuntime, "a")
	if err != nil || lease != nil {
		t.Fatalf("AcquireLease() = (%v, %v), want (nil, nil)", lease, err)
	}
}

func TestAcquireLeaseRejectsMalformedLeases(t *testing.T) {
	cases := []struct {
		name     string
		response string
	}{
		{"runtime missing bot token", `{"success":true,"data":{"leaseId":"l","appToken":"xapp","expiresAt":"e","generation":1}}`},
		{"validation missing expected app", `{"success":true,"data":{"leaseId":"l","appToken":"xapp","expiresAt":"e","generation":1}}`},
		{"missing generation", `{"success":true,"data":{"leaseId":"l","appToken":"xapp","botToken":"b","expiresAt":"e"}}`},
		{"data is object fragment", `{"success":true,"data":{}}`},
	}
	for _, testCase := range cases {
		t.Run(testCase.name, func(t *testing.T) {
			var captured capturedRequest
			api := serveAPI(t, http.StatusOK, testCase.response, &captured)
			kind := LeaseRuntime
			if strings.Contains(testCase.name, "validation") {
				kind = LeaseValidation
			}
			if _, err := api.AcquireLease(context.Background(), ConnectionTarget{ProjectID: "p", ConnectionID: "c"}, kind, "a"); err == nil {
				t.Fatalf("AcquireLease() accepted a malformed lease")
			}
		})
	}
}

func TestRenewLeaseMapsStaleAndEmptyToNil(t *testing.T) {
	var captured capturedRequest
	api := serveAPI(t, http.StatusConflict, `{"success":false,"code":"lease_stale_or_expired"}`, &captured)
	target := ConnectionTarget{ProjectID: "p", ConnectionID: "c"}

	renewal, err := api.RenewLease(context.Background(), target, "lease_1", "a")
	if err != nil || renewal != nil {
		t.Fatalf("RenewLease(stale) = (%v, %v), want (nil, nil)", renewal, err)
	}

	api = serveAPI(t, http.StatusOK, `{"success":true,"data":null}`, &captured)
	if renewal, err = api.RenewLease(context.Background(), target, "lease_1", "a"); err != nil || renewal != nil {
		t.Fatalf("RenewLease(empty) = (%v, %v), want (nil, nil)", renewal, err)
	}

	api = serveAPI(t, http.StatusOK, `{"success":true,"data":{"leaseId":"lease_1","kind":"runtime","generation":4,"expiresAt":"2026-08-21T01:00:00Z"}}`, &captured)
	if renewal, err = api.RenewLease(context.Background(), target, "lease_1", "a"); err != nil {
		t.Fatalf("RenewLease() error = %v", err)
	}
	if renewal == nil || renewal.LeaseID != "lease_1" || renewal.Kind != LeaseRuntime || renewal.Generation != 4 {
		t.Fatalf("renewal = %#v", renewal)
	}
	if captured.Body["leaseId"] != "lease_1" || captured.Body["adapterId"] != "a" {
		t.Fatalf("body = %v", captured.Body)
	}
}

func TestReportHelloOutcomes(t *testing.T) {
	cases := []struct {
		name     string
		status   int
		response string
		want     HelloOutcome
		wantErr  bool
	}{
		{"verified", http.StatusOK, `{"success":true,"data":{"outcome":"verified"}}`, HelloVerified, false},
		{"unverified outcome degrades to stale", http.StatusOK, `{"success":true,"data":{"outcome":"weird"}}`, HelloLeaseStale, false},
		{"missing data degrades to stale", http.StatusOK, `{"success":true}`, HelloLeaseStale, false},
		{"non-object data degrades to stale", http.StatusOK, `{"success":true,"data":[1]}`, HelloLeaseStale, false},
		{"null data degrades to stale", http.StatusOK, `{"success":true,"data":null}`, HelloLeaseStale, false},
		{"app mismatch", http.StatusConflict, `{"success":false,"code":"app_id_mismatch"}`, HelloAppIDMismatch, false},
		{"stale", http.StatusConflict, `{"success":false,"code":"lease_stale_or_expired"}`, HelloLeaseStale, false},
		{"unexpected code", http.StatusInternalServerError, `{"success":false,"code":"boom"}`, "", true},
	}
	for _, testCase := range cases {
		t.Run(testCase.name, func(t *testing.T) {
			var captured capturedRequest
			api := serveAPI(t, testCase.status, testCase.response, &captured)
			outcome, err := api.ReportHello(context.Background(), ManagerTarget{EnrollmentID: "e", WorkspaceTeamID: "t"}, "lease_1", "A1")
			if testCase.wantErr {
				if err == nil {
					t.Fatalf("ReportHello() = (%q, nil), want error", outcome)
				}
				return
			}
			if err != nil || outcome != testCase.want {
				t.Fatalf("ReportHello() = (%q, %v), want (%q, nil)", outcome, err, testCase.want)
			}
			if captured.Body["appId"] != "A1" || captured.Body["leaseId"] != "lease_1" {
				t.Fatalf("body = %v", captured.Body)
			}
			requestTarget, ok := captured.Body["target"].(map[string]any)
			if !ok || requestTarget["kind"] != "manager" || requestTarget["enrollmentId"] != "e" {
				t.Fatalf("target body = %v", captured.Body["target"])
			}
		})
	}
}

func TestIngressDecodesResponseOwnersAndLegacyFallback(t *testing.T) {
	tests := []struct {
		name          string
		response      string
		wantOwner     ResponseOwner
		wantReason    string
		wantReasonNil bool
	}{
		{"none", `{"success":true,"data":{"kind":"accepted","responseOwner":"none"}}`, ResponseOwnerNone, "", true},
		{"server", `{"success":true,"data":{"kind":"rejected","responseOwner":"server","reason":"durable nudge"}}`, ResponseOwnerServer, "durable nudge", false},
		{"adapter", `{"success":true,"data":{"kind":"backpressured","responseOwner":"adapter","reason":"retry"}}`, ResponseOwnerAdapter, "retry", false},
		{"legacy backpressure", `{"success":true,"data":{"kind":"backpressured","reason":"legacy retry"}}`, ResponseOwnerAdapter, "legacy retry", false},
		{"legacy other result", `{"success":true,"data":{"kind":"rejected","reason":"legacy rejection"}}`, ResponseOwnerNone, "legacy rejection", false},
	}
	for _, testCase := range tests {
		t.Run(testCase.name, func(t *testing.T) {
			var captured capturedRequest
			api := serveAPI(t, http.StatusOK, testCase.response, &captured)
			result, err := api.Ingress(context.Background(), ConnectionTarget{ProjectID: "p", ConnectionID: "c"}, Envelope{SenderKind: SenderUnknown}, "l", "a")
			if err != nil || result.ResponseOwner != testCase.wantOwner {
				t.Fatalf("Ingress() = (%#v, %v), want owner %q", result, err, testCase.wantOwner)
			}
			if testCase.wantReasonNil != (result.Reason == nil) || result.Reason != nil && *result.Reason != testCase.wantReason {
				t.Fatalf("Ingress() reason = %v, want %q / nil=%t", result.Reason, testCase.wantReason, testCase.wantReasonNil)
			}
		})
	}
}

func TestIngressRejectsInvalidResponseOwnersAndMalformedResults(t *testing.T) {
	tests := []string{
		`{"success":true,"data":{"kind":"rejected","responseOwner":"unknown"}}`,
		`{"success":true,"data":{"kind":"rejected","responseOwner":null}}`,
		`{"success":true,"data":{"responseOwner":"server"}}`,
		`{"success":true,"data":{"kind":"rejected","reason":42}}`,
		`{"success":true,"data":{"kind":"rejected","reason":null}}`,
		`{"success":true,"data":{"kind":"backpressured"}}`,
	}
	for _, response := range tests {
		t.Run(response, func(t *testing.T) {
			var captured capturedRequest
			api := serveAPI(t, http.StatusOK, response, &captured)
			if _, err := api.Ingress(context.Background(), ConnectionTarget{ProjectID: "p", ConnectionID: "c"}, Envelope{SenderKind: SenderUnknown}, "l", "a"); err == nil {
				t.Fatalf("Ingress() accepted malformed result")
			}
		})
	}
}

func TestIngressRoutesByTargetKind(t *testing.T) {
	envelope := Envelope{
		EventType:        "message",
		APIAppID:         "A1",
		IsDirectMessage:  true,
		TeamID:           "T123",
		ConversationID:   "D123",
		MessageTs:        "1710000000.000100",
		ThreadTs:         strPointer("1710000000.000050"),
		MentionedUserIDs: []string{"U1"},
		SenderKind:       SenderHuman,
		Text:             strPointer("hello"),
		Files:            []FileRef{{ID: "f1", Name: "a.txt", Mimetype: "text/plain", Size: 3}},
	}

	t.Run("connection uses the full envelope", func(t *testing.T) {
		var captured capturedRequest
		api := serveAPI(t, http.StatusOK, `{"success":true,"data":{"kind":"accepted"}}`, &captured)
		result, err := api.Ingress(context.Background(), ConnectionTarget{ProjectID: "proj/1", ConnectionID: "conn 1"}, envelope, "lease_1", "a")
		if err != nil {
			t.Fatalf("Ingress() error = %v", err)
		}
		if result.Kind != "accepted" {
			t.Fatalf("result = %#v", result)
		}
		if captured.Path != "/api/projects/proj%2F1/slack-connections/conn%201/ingress" {
			t.Fatalf("path = %q", captured.Path)
		}
		for _, key := range []string{"eventType", "apiAppId", "teamId", "conversationId", "messageTs", "threadTs", "mentionedUserIds", "senderKind", "text", "files", "leaseId", "adapterId"} {
			if _, present := captured.Body[key]; !present {
				t.Fatalf("body missing %q: %v", key, captured.Body)
			}
		}
	})

	t.Run("manager uses the flattened body", func(t *testing.T) {
		var captured capturedRequest
		api := serveAPI(t, http.StatusOK, `{"success":true,"data":{"kind":"claimed"}}`, &captured)
		result, err := api.Ingress(context.Background(), ManagerTarget{EnrollmentID: "enr", WorkspaceTeamID: "T123"}, envelope, "lease_1", "a")
		if err != nil || result.Kind != "claimed" {
			t.Fatalf("Ingress() = (%#v, %v)", result, err)
		}
		if captured.Path != "/api/slack-manager/ingress" {
			t.Fatalf("path = %q", captured.Path)
		}
		if captured.Body["workspaceTeamId"] != "T123" || captured.Body["appId"] != "A1" || captured.Body["isDirectMessage"] != true {
			t.Fatalf("body = %v", captured.Body)
		}
		if _, present := captured.Body["eventType"]; present {
			t.Fatalf("flattened manager body must not carry the full envelope: %v", captured.Body)
		}
	})
}

func TestIngressSurfacesResponseOwnershipAndBackpressureReason(t *testing.T) {
	var captured capturedRequest
	api := serveAPI(t, http.StatusOK, `{"success":true,"data":{"kind":"backpressured","responseOwner":"adapter","reason":"drain first"}}`, &captured)
	result, err := api.Ingress(context.Background(), ConnectionTarget{ProjectID: "p", ConnectionID: "c"}, Envelope{SenderKind: SenderUnknown}, "l", "a")
	if err != nil || result.Kind != "backpressured" || result.ResponseOwner != ResponseOwnerAdapter || result.Reason == nil || *result.Reason != "drain first" {
		t.Fatalf("Ingress() = (%#v, %v)", result, err)
	}
	// The Server dereferences the list fields without a null guard, so a
	// zero-value envelope must still carry arrays on the wire.
	for _, key := range []string{"files", "mentionedUserIds"} {
		if _, ok := captured.Body[key].([]any); !ok {
			t.Fatalf("body[%q] = %v, want an array", key, captured.Body[key])
		}
	}
}

func TestInteractionRejectsManagerTargets(t *testing.T) {
	var captured capturedRequest
	api := serveAPI(t, http.StatusOK, `{"success":true,"data":{"state":"ok"}}`, &captured)
	envelope := InteractionEnvelope{EventType: "block_actions", ActionID: "act", ActionValue: "v"}
	if _, err := api.Interaction(context.Background(), ManagerTarget{EnrollmentID: "e", WorkspaceTeamID: "t"}, envelope, "l", "a"); err == nil {
		t.Fatalf("Interaction(manager) succeeded, want error")
	}
}

func TestInteractionPostsAndParsesState(t *testing.T) {
	for _, testCase := range []struct {
		actionID    string
		actionValue string
	}{
		{actionID: "mohist_stop_turn", actionValue: "stop-signed-value"},
		{actionID: "mohist_retry_turn", actionValue: "retry-signed-value"},
	} {
		t.Run(testCase.actionID, func(t *testing.T) {
			var captured capturedRequest
			api := serveAPI(t, http.StatusOK, `{"success":true,"data":{"state":"recorded"}}`, &captured)
			envelope := InteractionEnvelope{
				EventType:        "block_actions",
				APIAppID:         "A1",
				InteractionID:    "i1",
				TeamID:           "T123",
				ConversationID:   "C1",
				MessageTs:        "1.2",
				ThreadTs:         strPointer("1.1"),
				ActorSlackUserID: "U1",
				ActionID:         testCase.actionID,
				ActionValue:      testCase.actionValue,
			}
			result, err := api.Interaction(context.Background(), ConnectionTarget{ProjectID: "p", ConnectionID: "c"}, envelope, "l", "a")
			if err != nil || result.State != "recorded" {
				t.Fatalf("Interaction() = (%#v, %v)", result, err)
			}
			if captured.Path != "/api/projects/p/slack-connections/c/interactions" {
				t.Fatalf("path = %q", captured.Path)
			}
			expectedBody := map[string]any{
				"eventType":        "block_actions",
				"apiAppId":         "A1",
				"interactionId":    "i1",
				"teamId":           "T123",
				"conversationId":   "C1",
				"messageTs":        "1.2",
				"threadTs":         "1.1",
				"actorSlackUserId": "U1",
				"actionId":         testCase.actionID,
				"actionValue":      testCase.actionValue,
				"leaseId":          "l",
				"adapterId":        "a",
			}
			if !reflect.DeepEqual(captured.Body, expectedBody) {
				t.Fatalf("body = %v, want %v", captured.Body, expectedBody)
			}
		})
	}
}

func TestClaimDeliveryRoutesAndParses(t *testing.T) {
	t.Run("no work yields nil", func(t *testing.T) {
		var captured capturedRequest
		api := serveAPI(t, http.StatusOK, `{"success":true,"data":null}`, &captured)
		delivery, err := api.ClaimDelivery(context.Background(), ConnectionTarget{ProjectID: "p", ConnectionID: "c"}, "l", "a")
		if err != nil || delivery != nil {
			t.Fatalf("ClaimDelivery() = (%v, %v), want (nil, nil)", delivery, err)
		}
	})

	t.Run("parses the delivery and routes by kind", func(t *testing.T) {
		var captured capturedRequest
		response := `{"success":true,"data":{"id":"d1","ownerKind":"manager","conversationId":"C1","threadTs":"1.1","payloadJson":"{\"operation\":\"post_message\"}"}}`
		api := serveAPI(t, http.StatusOK, response, &captured)
		delivery, err := api.ClaimUncertainDelivery(context.Background(), ManagerTarget{EnrollmentID: "enr", WorkspaceTeamID: "t"}, "l", "a")
		if err != nil {
			t.Fatalf("ClaimUncertainDelivery() error = %v", err)
		}
		if delivery.ID != "d1" || delivery.OwnerKind != "manager" || delivery.ConversationID != "C1" || delivery.ThreadTs == nil || *delivery.ThreadTs != "1.1" {
			t.Fatalf("delivery = %#v", delivery)
		}
		if captured.Path != "/api/slack-manager/adapter/enr/deliveries/claim-uncertain" {
			t.Fatalf("path = %q", captured.Path)
		}
	})

	t.Run("unknown owner kinds are dropped", func(t *testing.T) {
		var captured capturedRequest
		api := serveAPI(t, http.StatusOK, `{"success":true,"data":{"id":"d1","ownerKind":"mystery","conversationId":"C1","payloadJson":"{}"}}`, &captured)
		delivery, err := api.ClaimDelivery(context.Background(), ConnectionTarget{ProjectID: "p", ConnectionID: "c"}, "l", "a")
		if err != nil || delivery.OwnerKind != "" {
			t.Fatalf("ClaimDelivery() = (%#v, %v)", delivery, err)
		}
	})

	t.Run("missing required fields fail", func(t *testing.T) {
		var captured capturedRequest
		api := serveAPI(t, http.StatusOK, `{"success":true,"data":{"id":"d1"}}`, &captured)
		if _, err := api.ClaimDelivery(context.Background(), ConnectionTarget{ProjectID: "p", ConnectionID: "c"}, "l", "a"); err == nil {
			t.Fatalf("ClaimDelivery() accepted an incomplete delivery")
		}
	})
}

func TestAckDeliveryMergesLeaseIntoTheAck(t *testing.T) {
	var captured capturedRequest
	api := serveAPI(t, http.StatusOK, `{"success":true,"data":null}`, &captured)
	ack := DeliveryAck{
		ID:                      "d1",
		Outcome:                 OutcomeDelivered,
		AdapterID:               "a",
		ProviderMessageIdentity: &MessageIdentity{ConversationID: "C1", MessageTs: "1.2"},
	}
	if err := api.AckDelivery(context.Background(), ConnectionTarget{ProjectID: "p", ConnectionID: "c"}, ack, "l"); err != nil {
		t.Fatalf("AckDelivery() error = %v", err)
	}
	if captured.Path != "/api/projects/p/slack-connections/c/deliveries/ack" {
		t.Fatalf("path = %q", captured.Path)
	}
	if captured.Body["outcome"] != OutcomeDelivered || captured.Body["leaseId"] != "l" {
		t.Fatalf("body = %v", captured.Body)
	}
	identity, ok := captured.Body["providerMessageIdentity"].(map[string]any)
	if !ok || identity["messageTs"] != "1.2" {
		t.Fatalf("identity = %v", captured.Body["providerMessageIdentity"])
	}
}

func TestFailedEnvelopesBecomeTypedErrors(t *testing.T) {
	t.Run("generic failure keeps status and code", func(t *testing.T) {
		var captured capturedRequest
		api := serveAPI(t, http.StatusInternalServerError, `{"success":false,"code":"boom"}`, &captured)
		_, err := api.Discover(context.Background())
		var apiErr *APIError
		if !errors.As(err, &apiErr) || apiErr.Status != http.StatusInternalServerError || apiErr.Code != "boom" {
			t.Fatalf("err = %v, want *APIError", err)
		}
		if apiErr.Error() != "slack adapter request failed: 500 (boom)" {
			t.Fatalf("Error() = %q", apiErr.Error())
		}
	})

	t.Run("failure without a code omits the parens", func(t *testing.T) {
		var captured capturedRequest
		api := serveAPI(t, http.StatusBadRequest, `{"success":false}`, &captured)
		_, err := api.Discover(context.Background())
		var apiErr *APIError
		if !errors.As(err, &apiErr) || apiErr.Error() != "slack adapter request failed: 400" {
			t.Fatalf("err = %v", err)
		}
	})

	t.Run("non-object bodies are invalid responses", func(t *testing.T) {
		var captured capturedRequest
		api := serveAPI(t, http.StatusOK, `[1,2,3]`, &captured)
		if _, err := api.Discover(context.Background()); err == nil || !strings.Contains(err.Error(), "invalid response (200)") {
			t.Fatalf("err = %v", err)
		}
	})
}
