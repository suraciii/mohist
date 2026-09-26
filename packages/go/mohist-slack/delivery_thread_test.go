package mohistslack

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"testing"
)

// threadTransport serves the Slack Web calls reconciliation can make, so these
// tests exercise the real SlackWeb caller and the request paths instead of a
// hand-written WebClient. It records every path and the chat.postMessage form
// bodies, which is how the tests prove that an inconclusive read mutates
// nothing.
type threadTransport struct {
	historyResponses []string
	repliesResponses []string
	paths            []string
	posts            []string
	replyQueries     []url.Values
}

func (transport *threadTransport) RoundTrip(request *http.Request) (*http.Response, error) {
	transport.paths = append(transport.paths, request.URL.Path)
	var body string
	switch request.URL.Path {
	case "/conversations.history":
		if err := request.ParseForm(); err != nil {
			return nil, err
		}
		body = transport.next(&transport.historyResponses, `{"ok":true,"messages":[],"has_more":false}`)
	case "/conversations.replies":
		if err := request.ParseForm(); err != nil {
			return nil, err
		}
		transport.replyQueries = append(transport.replyQueries, request.PostForm)
		body = transport.next(&transport.repliesResponses, `{"ok":true,"messages":[],"has_more":false}`)
	case "/chat.postMessage":
		if err := request.ParseForm(); err != nil {
			return nil, err
		}
		transport.posts = append(transport.posts, request.PostForm.Encode())
		body = `{"ok":true,"ts":"1800.000009"}`
	default:
		body = `{"ok":false,"error":"unexpected_method"}`
	}
	return &http.Response{
		StatusCode: http.StatusOK,
		Header:     make(http.Header),
		Body:       io.NopCloser(strings.NewReader(body)),
		Request:    request,
	}, nil
}

func (transport *threadTransport) next(responses *[]string, fallback string) string {
	if len(*responses) == 0 {
		return fallback
	}
	response := (*responses)[0]
	*responses = (*responses)[1:]
	return response
}

func newThreadWeb(transport *threadTransport) *SlackWeb {
	return newSlackWebWithAPIBaseURL("xoxb-thread-test", &http.Client{Transport: transport}, "https://slack.invalid")
}

func threadedDelivery(payload string) *Delivery {
	thread := "1700.000001"
	return &Delivery{ID: "d-thread", ConversationID: "C_THREAD", ThreadTs: &thread, PayloadJSON: payload}
}

// A threaded mutation appears in conversations.replies and never in the
// channel history; finding the existing reply must settle delivered without
// any further provider mutation.
func TestThreadedReconcileFindsExistingReplyInThreadAndNeverReposts(t *testing.T) {
	transport := &threadTransport{
		historyResponses: []string{`{"ok":true,"messages":[{"ts":"1700.000001","text":"parent","reply_count":1}],"has_more":false}`},
		repliesResponses: []string{`{"ok":true,"messages":[{"ts":"1700.000001","text":"parent"},{"ts":"1700.000002","thread_ts":"1700.000001","client_msg_id":"cmid-thread-reply","text":"already visible answer"}],"has_more":false}`},
	}
	web := newThreadWeb(transport)
	delivery := threadedDelivery(`{"operation":"post_message","text":"already visible answer","clientMessageId":"cmid-thread-reply"}`)

	ack, err := Reconcile(context.Background(), web, delivery, func() {})
	if err != nil {
		t.Fatal(err)
	}
	if ack.Outcome != OutcomeDelivered || ack.ProviderMessageIdentity == nil || ack.ProviderMessageIdentity.MessageTs != "1700.000002" {
		t.Fatalf("ack = %+v", ack)
	}
	if len(transport.posts) != 0 {
		t.Fatalf("existing thread reply was posted again: posts=%d paths=%v", len(transport.posts), transport.paths)
	}
	if len(transport.paths) != 1 || transport.paths[0] != "/conversations.replies" {
		t.Fatalf("reconciliation paths = %v", transport.paths)
	}
	if len(transport.replyQueries) != 1 || transport.replyQueries[0].Get("channel") != "C_THREAD" || transport.replyQueries[0].Get("ts") != "1700.000001" {
		t.Fatalf("reply queries = %+v", transport.replyQueries)
	}
}

// Only a complete thread read may conclude absence: then the retry is
// authorized and the mutation lands in the original thread exactly once.
func TestThreadedReconcileCompleteAbsenceAllowsOneThreadedPost(t *testing.T) {
	transport := &threadTransport{
		repliesResponses: []string{`{"ok":true,"messages":[{"ts":"1700.000001","text":"parent"}],"has_more":false}`},
	}
	web := newThreadWeb(transport)
	delivery := threadedDelivery(`{"operation":"post_message","text":"answer","clientMessageId":"cmid-absent"}`)

	ack, err := Reconcile(context.Background(), web, delivery, func() {})
	if err != nil {
		t.Fatal(err)
	}
	if ack.Outcome != OutcomeRetry || ack.Reason != providerMutationAbsent {
		t.Fatalf("complete thread ack = %+v", ack)
	}
	if len(transport.posts) != 0 {
		t.Fatalf("reconciliation posted %d messages", len(transport.posts))
	}

	mutation, err := MutateDelivery(context.Background(), web, delivery, func() {})
	if err != nil {
		t.Fatal(err)
	}
	if mutation.Outcome != OutcomeDelivered {
		t.Fatalf("mutation ack = %+v", mutation)
	}
	if len(transport.posts) != 1 {
		t.Fatalf("posts = %d, want 1", len(transport.posts))
	}
	form, err := url.ParseQuery(transport.posts[0])
	if err != nil {
		t.Fatal(err)
	}
	if form.Get("channel") != "C_THREAD" || form.Get("thread_ts") != "1700.000001" || form.Get("client_msg_id") != "cmid-absent" {
		t.Fatalf("post form = %v", form)
	}
}

// A forum where the thread cannot be read (no permission) must not degrade
// into a blind re-post.
func TestThreadedReconcileUnauthorizedReadStaysUncertain(t *testing.T) {
	transport := &threadTransport{
		repliesResponses: []string{`{"ok":false,"error":"not_in_channel"}`},
	}
	web := newThreadWeb(transport)
	delivery := threadedDelivery(`{"operation":"post_message","text":"answer","clientMessageId":"cmid-denied"}`)

	ack, err := Reconcile(context.Background(), web, delivery, func() {})
	if err != nil {
		t.Fatal(err)
	}
	if ack.Outcome != OutcomeUncertain || ack.Reason != "not_in_channel" {
		t.Fatalf("unauthorized read ack = %+v", ack)
	}
	if len(transport.posts) != 0 {
		t.Fatalf("unauthorized read posted %d messages", len(transport.posts))
	}
}

// Pagination that cannot be completed — a stuck cursor or the page budget —
// is inconclusive evidence, not absence.
func TestThreadedReconcileIncompletePaginationStaysUncertain(t *testing.T) {
	stuck := &threadTransport{
		repliesResponses: []string{`{"ok":true,"messages":[],"has_more":true,"response_metadata":{"next_cursor":""}}`},
	}
	web := newThreadWeb(stuck)
	delivery := threadedDelivery(`{"operation":"post_message","text":"answer","clientMessageId":"cmid-stuck"}`)
	ack, err := Reconcile(context.Background(), web, delivery, func() {})
	if err != nil {
		t.Fatal(err)
	}
	if ack.Outcome != OutcomeUncertain || ack.Reason != providerHistoryIncomplete {
		t.Fatalf("stuck cursor ack = %+v", ack)
	}
	if len(stuck.posts) != 0 {
		t.Fatalf("stuck cursor posted %d messages", len(stuck.posts))
	}

	exhausted := &threadTransport{}
	for page := range historyPageBudget {
		exhausted.repliesResponses = append(exhausted.repliesResponses,
			fmt.Sprintf(`{"ok":true,"messages":[{"ts":"1700.%06d"}],"has_more":true,"response_metadata":{"next_cursor":"page-%d"}}`, page, page+2))
	}
	web = newThreadWeb(exhausted)
	ack, err = Reconcile(context.Background(), web,
		threadedDelivery(`{"operation":"post_message","text":"answer","clientMessageId":"cmid-budget"}`), func() {})
	if err != nil {
		t.Fatal(err)
	}
	if ack.Outcome != OutcomeUncertain || ack.Reason != providerHistoryIncomplete {
		t.Fatalf("exhausted budget ack = %+v", ack)
	}
	if len(exhausted.posts) != 0 || len(exhausted.replyQueries) != historyPageBudget {
		t.Fatalf("exhausted budget posts=%d pages=%d", len(exhausted.posts), len(exhausted.replyQueries))
	}
}

// Thread pagination is followed like the history scan: a reply on a later
// page is still found, and the cursor from the previous page is used.
func TestThreadedReconcileFollowsThreadPagination(t *testing.T) {
	transport := &threadTransport{
		repliesResponses: []string{
			`{"ok":true,"messages":[{"ts":"1700.000001","text":"parent"}],"has_more":true,"response_metadata":{"next_cursor":"page-2"}}`,
			`{"ok":true,"messages":[{"ts":"1700.000002","client_msg_id":"cmid-page-2","text":"answer"}],"has_more":false}`,
		},
	}
	web := newThreadWeb(transport)
	delivery := threadedDelivery(`{"operation":"post_message","text":"answer","clientMessageId":"cmid-page-2"}`)

	ack, err := Reconcile(context.Background(), web, delivery, func() {})
	if err != nil {
		t.Fatal(err)
	}
	if ack.Outcome != OutcomeDelivered || ack.ProviderMessageIdentity == nil || ack.ProviderMessageIdentity.MessageTs != "1700.000002" {
		t.Fatalf("paginated thread ack = %+v", ack)
	}
	if len(transport.posts) != 0 {
		t.Fatalf("paginated thread posted %d messages", len(transport.posts))
	}
	if len(transport.replyQueries) != 2 || transport.replyQueries[1].Get("cursor") != "page-2" {
		t.Fatalf("reply queries = %+v", transport.replyQueries)
	}
}

// A top-level delivery still reconciles against the conversation history; the
// thread scope applies only where the mutation actually posts.
func TestTopLevelReconcileKeepsReadingConversationHistory(t *testing.T) {
	transport := &threadTransport{
		historyResponses: []string{`{"ok":true,"messages":[{"ts":"1700.000003","client_msg_id":"cmid-top"}],"has_more":false}`},
	}
	web := newThreadWeb(transport)
	delivery := &Delivery{ID: "d-top", ConversationID: "C_THREAD", PayloadJSON: `{"operation":"post_message","text":"answer","clientMessageId":"cmid-top"}`}

	ack, err := Reconcile(context.Background(), web, delivery, func() {})
	if err != nil {
		t.Fatal(err)
	}
	if ack.Outcome != OutcomeDelivered || ack.ProviderMessageIdentity == nil || ack.ProviderMessageIdentity.MessageTs != "1700.000003" {
		t.Fatalf("top-level ack = %+v", ack)
	}
	if len(transport.paths) != 1 || transport.paths[0] != "/conversations.history" {
		t.Fatalf("top-level paths = %v", transport.paths)
	}
}

// The server-authored delivery notice must reach the provider as a real blocks
// message: the statement is the message body, and the top-level text stays the
// notification fallback. This is the transport end of the notice contract.
func TestDeliveryNoticePayloadSendsStatementBlocksToTransport(t *testing.T) {
	const statement = "Delivery notice: The Agent reply may already be visible in this thread; Mohist could not confirm that it reached Slack. Check the thread and the Session before sending it again; nothing is re-run automatically."
	transport := &threadTransport{}
	web := newThreadWeb(transport)
	payload := `{"operation":"post_message","text":"` + statement + `",` +
		`"clientMessageId":"slack-delivery-notice:slkout_1",` +
		`"blocks":[{"type":"section","text":{"type":"plain_text","text":"Session: session-1"}},` +
		`{"type":"section","text":{"type":"mrkdwn","text":"` + statement + `"}}],` +
		`"notice":"delivery_uncertain","possiblyDelivered":true}`
	delivery := &Delivery{ID: "d-notice", ConversationID: "C_THREAD", ThreadTs: strPtr("1700.000001"), PayloadJSON: payload}

	ack, err := MutateDelivery(context.Background(), web, delivery, func() {})
	if err != nil {
		t.Fatal(err)
	}
	if ack.Outcome != OutcomeDelivered {
		t.Fatalf("ack = %+v", ack)
	}
	if len(transport.posts) != 1 {
		t.Fatalf("posts = %d", len(transport.posts))
	}
	form, err := url.ParseQuery(transport.posts[0])
	if err != nil {
		t.Fatal(err)
	}
	if form.Get("text") != statement {
		t.Fatalf("fallback text = %q", form.Get("text"))
	}
	if form.Get("thread_ts") != "1700.000001" {
		t.Fatalf("thread_ts = %q", form.Get("thread_ts"))
	}
	var blocks []map[string]any
	if err := json.Unmarshal([]byte(form.Get("blocks")), &blocks); err != nil {
		t.Fatalf("blocks were not sent as JSON: %v (%q)", err, form.Get("blocks"))
	}
	found := false
	for _, block := range blocks {
		text, ok := block["text"].(map[string]any)
		if !ok {
			continue
		}
		if text["type"] == "mrkdwn" && strings.Contains(text["text"].(string), "Delivery notice:") {
			found = true
		}
	}
	if !found {
		t.Fatalf("statement missing from posted blocks: %q", form.Get("blocks"))
	}
}
