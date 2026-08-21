// Package mohistslack implements the Mohist Slack adapter's Server transport.
//
// The wire contract mirrors the Node adapter in packages/mohist-slack: every
// response is a {success, code, data} envelope, runtime leases are exclusive,
// and lease_stale_or_expired never retries inline. See
// design/slack-go-port.md for the port contract.
package mohistslack

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
)

// LeaseKind selects one phase of the two-phase lease protocol.
type LeaseKind string

const (
	LeaseValidation LeaseKind = "validation"
	LeaseRuntime    LeaseKind = "runtime"
)

// HelloOutcome is the result of reporting the connected Slack app identity.
type HelloOutcome string

const (
	HelloVerified      HelloOutcome = "verified"
	HelloAppIDMismatch HelloOutcome = "app_id_mismatch"
	HelloLeaseStale    HelloOutcome = "lease_stale_or_expired"
)

// SenderKind classifies the author of an inbound message.
type SenderKind string

const (
	SenderHuman   SenderKind = "human"
	SenderBot     SenderKind = "bot"
	SenderUnknown SenderKind = "unknown"
)

// Delivery outcomes understood by the Server's settlement endpoint.
const (
	OutcomeDelivered = "delivered"
	OutcomeRetry     = "retry"
	OutcomeUncertain = "uncertain"
)

// Target is one Slack integration the adapter can serve. It is either a
// ConnectionTarget or a ManagerTarget; the concrete type selects the routes.
type Target interface {
	// Key identifies the target across discovery cycles.
	Key() string
}

// ConnectionTarget is a project-scoped Slack connection.
type ConnectionTarget struct {
	ProjectID    string
	ConnectionID string
}

// Key implements Target.
func (t ConnectionTarget) Key() string {
	return "connection:" + t.ProjectID + ":" + t.ConnectionID
}

// ManagerTarget is an enrollment-scoped Slack manager workspace.
type ManagerTarget struct {
	EnrollmentID    string
	WorkspaceTeamID string
}

// Key implements Target.
func (t ManagerTarget) Key() string {
	return "manager:" + t.EnrollmentID
}

// ValidationLease gates the Socket hello probe for one target.
type ValidationLease struct {
	LeaseID       string
	AppToken      string
	ExpectedAppID string
	ExpiresAt     string
	Generation    int
}

func (ValidationLease) lease() {}

// RuntimeLease gates ingress, interactions, and deliveries.
type RuntimeLease struct {
	LeaseID    string
	AppToken   string
	BotToken   string
	ExpiresAt  string
	Generation int
}

func (RuntimeLease) lease() {}

// Lease is either a ValidationLease or a RuntimeLease.
type Lease interface{ lease() }

// LeaseRenewal extends a runtime lease without rotating its tokens.
type LeaseRenewal struct {
	LeaseID    string
	Kind       LeaseKind
	Generation int
	ExpiresAt  string
}

// MessageIdentity locates one message on the Slack side.
type MessageIdentity struct {
	ConversationID string `json:"conversationId"`
	MessageTs      string `json:"messageTs"`
}

// FileRef describes one file attached to an inbound message.
type FileRef struct {
	ID       string `json:"id"`
	Name     string `json:"name"`
	Mimetype string `json:"mimetype"`
	Size     int64  `json:"size"`
}

// BotAuthor carries bot authorship metadata, including a conflict marker when
// event-level and profile-level identities disagree.
type BotAuthor struct {
	AppID            *string `json:"appId"`
	BotID            *string `json:"botId"`
	BotUserID        *string `json:"botUserId"`
	IdentityConflict bool    `json:"identityConflict"`
}

// Envelope is the normalized inbound message posted to a connection's
// ingress route. Nullable fields marshal as null, matching the Node shape.
// The Server dereferences the list fields without a null guard, so Ingress
// always serializes them as arrays even for zero-value envelopes.
type Envelope struct {
	EventType         string     `json:"eventType"`
	APIAppID          string     `json:"apiAppId"`
	IsDirectMessage   bool       `json:"isDirectMessage"`
	TeamID            string     `json:"teamId"`
	ConversationID    string     `json:"conversationId"`
	MessageTs         string     `json:"messageTs"`
	ThreadTs          *string    `json:"threadTs"`
	MentionedUserIDs  []string   `json:"mentionedUserIds"`
	SenderSlackUserID *string    `json:"senderSlackUserId"`
	SenderKind        SenderKind `json:"senderKind"`
	AuthorBot         *BotAuthor `json:"authorBot"`
	Text              *string    `json:"text"`
	Files             []FileRef  `json:"files"`
}

// InteractionEnvelope is the normalized block_actions payload posted to a
// connection's interactions route.
type InteractionEnvelope struct {
	EventType        string  `json:"eventType"`
	APIAppID         string  `json:"apiAppId"`
	InteractionID    string  `json:"interactionId"`
	TeamID           string  `json:"teamId"`
	ConversationID   string  `json:"conversationId"`
	MessageTs        string  `json:"messageTs"`
	ThreadTs         *string `json:"threadTs"`
	ActorSlackUserID string  `json:"actorSlackUserId"`
	ActionID         string  `json:"actionId"`
	ActionValue      string  `json:"actionValue"`
}

// IngressResult is the Server's disposition of one inbound message.
type IngressResult struct {
	Kind   string
	Reason *string
}

// InteractionResult is the Server's disposition of one interaction.
type InteractionResult struct {
	State string
}

// Delivery is one outbound mutation claimed from the Server.
type Delivery struct {
	ID             string
	OwnerKind      string // "", "connection", or "manager"
	ConversationID string
	ThreadTs       *string
	PayloadJSON    string
}

// DeliveryAck settles one delivery.
type DeliveryAck struct {
	ID                      string           `json:"id"`
	Outcome                 string           `json:"outcome"`
	AdapterID               string           `json:"adapterId,omitempty"`
	Reason                  string           `json:"reason,omitempty"`
	ProviderMessageIdentity *MessageIdentity `json:"providerMessageIdentity,omitempty"`
}

// APIError is a structured failure reported through the response envelope.
type APIError struct {
	Status int
	Code   string
}

// Error implements error.
func (e *APIError) Error() string {
	if e.Code == "" {
		return fmt.Sprintf("slack adapter request failed: %d", e.Status)
	}
	return fmt.Sprintf("slack adapter request failed: %d (%s)", e.Status, e.Code)
}

const operatorIDHeader = "x-mohist-operator-id"

// ServerAPI speaks the adapter-side HTTP contract of the Mohist Server.
type ServerAPI struct {
	client     *http.Client
	baseURL    *url.URL
	token      string
	operatorID string
}

// Option customizes a ServerAPI.
type Option func(*ServerAPI)

// WithHTTPClient replaces the underlying HTTP client, primarily in tests.
func WithHTTPClient(client *http.Client) Option {
	return func(s *ServerAPI) { s.client = client }
}

// NewServerAPI validates that serverURL is loopback and returns a client.
func NewServerAPI(serverURL, operatorToken, operatorID string, opts ...Option) (*ServerAPI, error) {
	base, err := loopbackBaseURL(serverURL)
	if err != nil {
		return nil, err
	}
	s := &ServerAPI{
		client:     http.DefaultClient,
		baseURL:    base,
		token:      operatorToken,
		operatorID: operatorID,
	}
	for _, opt := range opts {
		opt(s)
	}
	return s, nil
}

func loopbackBaseURL(raw string) (*url.URL, error) {
	parsed, err := url.Parse(raw)
	if err != nil || (parsed.Scheme != "http" && parsed.Scheme != "https") || !isLoopbackHost(parsed.Hostname()) {
		return nil, errors.New("Slack adapter Server URL must be loopback")
	}
	parsed.Path = strings.TrimRight(parsed.Path, "/")
	parsed.RawPath = ""
	return parsed, nil
}

func isLoopbackHost(hostname string) bool {
	if hostname == "localhost" || hostname == "::1" {
		return true
	}
	octets := strings.Split(hostname, ".")
	if len(octets) != 4 || octets[0] != "127" {
		return false
	}
	// Stricter than the Node guard, which accepts any all-digit octet up to
	// 255 including zero padding; rejecting those here is a safe delta.
	for _, octet := range octets {
		if octet == "" || len(octet) > 3 {
			return false
		}
		for _, digit := range octet {
			if digit < '0' || digit > '9' {
				return false
			}
		}
		if len(octet) > 1 && octet[0] == '0' {
			return false
		}
		value := 0
		for _, digit := range octet {
			value = value*10 + int(digit-'0')
		}
		if value > 255 {
			return false
		}
	}
	return true
}

type envelopeResponse struct {
	Success bool            `json:"success"`
	Code    string          `json:"code"`
	Data    json.RawMessage `json:"data"`
}

// call performs one request and decodes the response envelope. A successful
// envelope returns its raw data; a failed one returns an *APIError.
func (s *ServerAPI) call(ctx context.Context, method, path string, body any) (envelopeResponse, error) {
	var reader io.Reader
	if body != nil {
		encoded, err := json.Marshal(body)
		if err != nil {
			return envelopeResponse{}, err
		}
		reader = bytes.NewReader(encoded)
	}
	req, err := http.NewRequestWithContext(ctx, method, s.baseURL.String()+path, reader)
	if err != nil {
		return envelopeResponse{}, err
	}
	req.Header.Set("Authorization", "Bearer "+s.token)
	req.Header.Set(operatorIDHeader, s.operatorID)
	if body != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	resp, err := s.client.Do(req)
	if err != nil {
		return envelopeResponse{}, err
	}
	defer resp.Body.Close()
	text, err := io.ReadAll(resp.Body)
	if err != nil {
		return envelopeResponse{}, err
	}
	var probe any
	if err := json.Unmarshal(text, &probe); err != nil {
		return envelopeResponse{}, fmt.Errorf("Slack adapter returned an invalid response (%d)", resp.StatusCode)
	}
	if _, ok := probe.(map[string]any); !ok {
		return envelopeResponse{}, fmt.Errorf("Slack adapter returned an invalid response (%d)", resp.StatusCode)
	}
	var payload envelopeResponse
	if err := json.Unmarshal(text, &payload); err != nil {
		return envelopeResponse{}, fmt.Errorf("Slack adapter returned an invalid response (%d)", resp.StatusCode)
	}
	if !payload.Success {
		return payload, &APIError{Status: resp.StatusCode, Code: payload.Code}
	}
	return payload, nil
}

func targetBody(target Target) map[string]any {
	switch v := target.(type) {
	case ConnectionTarget:
		return map[string]any{"kind": "connection", "projectId": v.ProjectID, "connectionId": v.ConnectionID}
	case ManagerTarget:
		return map[string]any{"kind": "manager", "enrollmentId": v.EnrollmentID, "workspaceTeamId": v.WorkspaceTeamID}
	default:
		panic(fmt.Sprintf("unknown target type %T", target))
	}
}

func stringValue(record map[string]any, key string) (string, bool) {
	value, ok := record[key].(string)
	return value, ok && value != ""
}

func intValue(record map[string]any, key string) (int, bool) {
	value, ok := record[key].(float64)
	return int(value), ok
}

func decodeTarget(record map[string]any) (Target, error) {
	switch record["kind"] {
	case "manager":
		enrollmentID, enrollmentOK := stringValue(record, "enrollmentId")
		workspaceTeamID, teamOK := stringValue(record, "workspaceTeamId")
		if !enrollmentOK || !teamOK {
			return nil, errors.New("Slack adapter discovery returned an invalid Manager target")
		}
		return ManagerTarget{EnrollmentID: enrollmentID, WorkspaceTeamID: workspaceTeamID}, nil
	case "connection":
		projectID, projectOK := stringValue(record, "projectId")
		connectionID, connectionOK := stringValue(record, "connectionId")
		if !projectOK || !connectionOK {
			return nil, errors.New("Slack adapter discovery returned an invalid target")
		}
		return ConnectionTarget{ProjectID: projectID, ConnectionID: connectionID}, nil
	default:
		return nil, errors.New("Slack adapter discovery returned an invalid target kind")
	}
}

func decodeLease(kind LeaseKind, data json.RawMessage) (Lease, error) {
	var record map[string]any
	if err := json.Unmarshal(data, &record); err != nil {
		return nil, errors.New("Slack adapter returned an invalid lease response")
	}
	leaseID, leaseOK := stringValue(record, "leaseId")
	appToken, appOK := stringValue(record, "appToken")
	expiresAt, expiresOK := stringValue(record, "expiresAt")
	generation, generationOK := intValue(record, "generation")
	if !leaseOK || !appOK || !expiresOK || !generationOK {
		return nil, errors.New("Slack adapter returned an invalid lease response")
	}
	switch kind {
	case LeaseValidation:
		expectedAppID, expectedOK := stringValue(record, "expectedAppId")
		if !expectedOK {
			return nil, errors.New("Slack adapter returned an invalid validation lease response")
		}
		return ValidationLease{
			LeaseID:       leaseID,
			AppToken:      appToken,
			ExpectedAppID: expectedAppID,
			ExpiresAt:     expiresAt,
			Generation:    generation,
		}, nil
	case LeaseRuntime:
		botToken, botOK := stringValue(record, "botToken")
		if !botOK {
			return nil, errors.New("Slack adapter returned an invalid runtime lease response")
		}
		return RuntimeLease{
			LeaseID:    leaseID,
			AppToken:   appToken,
			BotToken:   botToken,
			ExpiresAt:  expiresAt,
			Generation: generation,
		}, nil
	default:
		return nil, fmt.Errorf("unknown lease kind %q", kind)
	}
}

// Discover lists the targets the adapter should currently serve.
func (s *ServerAPI) Discover(ctx context.Context) ([]Target, error) {
	payload, err := s.call(ctx, http.MethodGet, "/api/slack-adapter/leases/targets", nil)
	if err != nil {
		return nil, err
	}
	var raw []any
	if len(payload.Data) == 0 || json.Unmarshal(payload.Data, &raw) != nil {
		return nil, errors.New("Slack adapter discovery returned an invalid response")
	}
	targets := make([]Target, 0, len(raw))
	for _, item := range raw {
		record, ok := item.(map[string]any)
		if !ok {
			return nil, errors.New("Slack adapter discovery returned an invalid response")
		}
		target, err := decodeTarget(record)
		if err != nil {
			return nil, err
		}
		targets = append(targets, target)
	}
	return targets, nil
}

// AcquireLease acquires one lease phase for the target. It returns nil when
// the Server reports the lease as not acquirable or has no lease to give.
func (s *ServerAPI) AcquireLease(ctx context.Context, target Target, kind LeaseKind, adapterID string) (Lease, error) {
	payload, err := s.call(ctx, http.MethodPost, "/api/slack-adapter/leases/acquire", map[string]any{
		"kind":      string(kind),
		"target":    targetBody(target),
		"adapterId": adapterID,
	})
	if err != nil {
		var apiErr *APIError
		if errors.As(err, &apiErr) && apiErr.Code == "lease_not_acquirable" {
			return nil, nil
		}
		return nil, err
	}
	if len(payload.Data) == 0 || string(payload.Data) == "null" {
		return nil, nil
	}
	return decodeLease(kind, payload.Data)
}

// RenewLease renews a runtime lease. It returns nil when the Server reports
// the lease stale or expired, or when no renewal is available; both cases
// make the caller drop the runtime.
func (s *ServerAPI) RenewLease(ctx context.Context, target Target, leaseID, adapterID string) (*LeaseRenewal, error) {
	payload, err := s.call(ctx, http.MethodPost, "/api/slack-adapter/leases/renew", map[string]any{
		"target":    targetBody(target),
		"leaseId":   leaseID,
		"adapterId": adapterID,
	})
	if err != nil {
		var apiErr *APIError
		if errors.As(err, &apiErr) && apiErr.Code == "lease_stale_or_expired" {
			return nil, nil
		}
		return nil, err
	}
	if len(payload.Data) == 0 || string(payload.Data) == "null" {
		return nil, nil
	}
	var record map[string]any
	if err := json.Unmarshal(payload.Data, &record); err != nil {
		return nil, errors.New("Slack adapter returned an invalid lease renewal response")
	}
	renewalLeaseID, idOK := stringValue(record, "leaseId")
	kind, kindOK := stringValue(record, "kind")
	expiresAt, expiresOK := stringValue(record, "expiresAt")
	generation, generationOK := intValue(record, "generation")
	if !idOK || !kindOK || !expiresOK || !generationOK {
		return nil, errors.New("Slack adapter returned an invalid lease renewal response")
	}
	// The Node implementation coerces any non-validation kind to runtime.
	if kind != string(LeaseValidation) {
		kind = string(LeaseRuntime)
	}
	return &LeaseRenewal{LeaseID: renewalLeaseID, Kind: LeaseKind(kind), Generation: generation, ExpiresAt: expiresAt}, nil
}

// ReportHello reports the app identity observed on the probe socket.
func (s *ServerAPI) ReportHello(ctx context.Context, target Target, leaseID, appID string) (HelloOutcome, error) {
	payload, err := s.call(ctx, http.MethodPost, "/api/slack-adapter/leases/hello", map[string]any{
		"target":  targetBody(target),
		"leaseId": leaseID,
		"appId":   appID,
	})
	if err != nil {
		var apiErr *APIError
		if errors.As(err, &apiErr) {
			switch apiErr.Code {
			case string(HelloAppIDMismatch), string(HelloLeaseStale):
				return HelloOutcome(apiErr.Code), nil
			}
		}
		return "", err
	}
	// Any missing or non-object data decays to stale: the Node
	// implementation treats a non-record outcome as unverified.
	var record map[string]any
	_ = json.Unmarshal(payload.Data, &record)
	outcome, _ := stringValue(record, "outcome")
	if outcome == string(HelloVerified) {
		return HelloVerified, nil
	}
	return HelloLeaseStale, nil
}

func connectionRoute(target ConnectionTarget) string {
	return "/api/projects/" + url.PathEscape(target.ProjectID) +
		"/slack-connections/" + url.PathEscape(target.ConnectionID)
}

func deliveryRoute(target Target, action string) (string, bool) {
	switch v := target.(type) {
	case ManagerTarget:
		return "/api/slack-manager/adapter/" + url.PathEscape(v.EnrollmentID) + "/deliveries/" + action, true
	case ConnectionTarget:
		return connectionRoute(v) + "/deliveries/" + action, true
	default:
		return "", false
	}
}

type leaseBody struct {
	LeaseID   string `json:"leaseId"`
	AdapterID string `json:"adapterId"`
}

// Ingress forwards one normalized message upstream.
func (s *ServerAPI) Ingress(ctx context.Context, target Target, envelope Envelope, leaseID, adapterID string) (IngressResult, error) {
	var path string
	var body any
	switch v := target.(type) {
	case ManagerTarget:
		path = "/api/slack-manager/ingress"
		body = map[string]any{
			"appId":             envelope.APIAppID,
			"workspaceTeamId":   v.WorkspaceTeamID,
			"conversationId":    envelope.ConversationID,
			"messageTs":         envelope.MessageTs,
			"senderSlackUserId": envelope.SenderSlackUserID,
			"senderKind":        string(envelope.SenderKind),
			"authorBot":         envelope.AuthorBot,
			"text":              envelope.Text,
			"isDirectMessage":   envelope.IsDirectMessage,
			"threadTs":          envelope.ThreadTs,
			"leaseId":           leaseID,
			"adapterId":         adapterID,
		}
	case ConnectionTarget:
		path = connectionRoute(v) + "/ingress"
		if envelope.MentionedUserIDs == nil {
			envelope.MentionedUserIDs = []string{}
		}
		if envelope.Files == nil {
			envelope.Files = []FileRef{}
		}
		body = struct {
			Envelope
			LeaseID   string `json:"leaseId"`
			AdapterID string `json:"adapterId"`
		}{envelope, leaseID, adapterID}
	default:
		return IngressResult{}, fmt.Errorf("unknown target type %T", target)
	}
	payload, err := s.call(ctx, http.MethodPost, path, body)
	if err != nil {
		return IngressResult{}, err
	}
	var record map[string]any
	if err := json.Unmarshal(payload.Data, &record); err != nil {
		return IngressResult{}, errors.New("Slack adapter returned an invalid ingress result")
	}
	result := IngressResult{}
	if kind, ok := stringValue(record, "kind"); ok {
		result.Kind = kind
	}
	if reason, ok := record["reason"].(string); ok {
		result.Reason = &reason
	}
	return result, nil
}

// Interaction forwards one block_actions payload upstream. Manager targets do
// not expose interactions.
func (s *ServerAPI) Interaction(ctx context.Context, target Target, envelope InteractionEnvelope, leaseID, adapterID string) (InteractionResult, error) {
	connection, ok := target.(ConnectionTarget)
	if !ok {
		return InteractionResult{}, errors.New("Slack Manager targets do not expose interactions")
	}
	payload, err := s.call(ctx, http.MethodPost, connectionRoute(connection)+"/interactions", struct {
		InteractionEnvelope
		LeaseID   string `json:"leaseId"`
		AdapterID string `json:"adapterId"`
	}{envelope, leaseID, adapterID})
	if err != nil {
		return InteractionResult{}, err
	}
	var record map[string]any
	if err := json.Unmarshal(payload.Data, &record); err != nil {
		return InteractionResult{}, errors.New("Slack adapter returned an invalid interaction result")
	}
	state, _ := stringValue(record, "state")
	return InteractionResult{State: state}, nil
}

func (s *ServerAPI) claim(ctx context.Context, target Target, action string, leaseID, adapterID string) (*Delivery, error) {
	path, ok := deliveryRoute(target, action)
	if !ok {
		return nil, fmt.Errorf("unknown target type %T", target)
	}
	payload, err := s.call(ctx, http.MethodPost, path, leaseBody{LeaseID: leaseID, AdapterID: adapterID})
	if err != nil {
		return nil, err
	}
	if len(payload.Data) == 0 || string(payload.Data) == "null" {
		return nil, nil
	}
	var record map[string]any
	if err := json.Unmarshal(payload.Data, &record); err != nil {
		return nil, errors.New("Slack adapter returned an invalid delivery")
	}
	id, idOK := stringValue(record, "id")
	conversationID, conversationOK := stringValue(record, "conversationId")
	payloadJSON, payloadOK := stringValue(record, "payloadJson")
	if !idOK || !conversationOK || !payloadOK {
		return nil, errors.New("Slack adapter returned an invalid delivery")
	}
	delivery := &Delivery{
		ID:             id,
		ConversationID: conversationID,
		PayloadJSON:    payloadJSON,
	}
	switch record["ownerKind"] {
	case "connection", "manager":
		delivery.OwnerKind = record["ownerKind"].(string)
	}
	// The Node guard maps empty strings to null.
	if threadTs, ok := record["threadTs"].(string); ok && threadTs != "" {
		delivery.ThreadTs = &threadTs
	}
	return delivery, nil
}

// ClaimDelivery claims the next pending delivery, or returns nil when none.
func (s *ServerAPI) ClaimDelivery(ctx context.Context, target Target, leaseID, adapterID string) (*Delivery, error) {
	return s.claim(ctx, target, "claim", leaseID, adapterID)
}

// ClaimUncertainDelivery claims the next uncertain delivery, or nil.
func (s *ServerAPI) ClaimUncertainDelivery(ctx context.Context, target Target, leaseID, adapterID string) (*Delivery, error) {
	return s.claim(ctx, target, "claim-uncertain", leaseID, adapterID)
}

// AckDelivery settles one delivery.
func (s *ServerAPI) AckDelivery(ctx context.Context, target Target, ack DeliveryAck, leaseID string) error {
	path, ok := deliveryRoute(target, "ack")
	if !ok {
		return fmt.Errorf("unknown target type %T", target)
	}
	_, err := s.call(ctx, http.MethodPost, path, struct {
		DeliveryAck
		LeaseID string `json:"leaseId"`
	}{ack, leaseID})
	return err
}
