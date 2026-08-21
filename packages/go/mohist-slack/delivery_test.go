package mohistslack

import (
	"context"
	"errors"
	"fmt"
	"sync"
	"testing"
)

type postCall struct {
	channel string
	text    string
	opts    PostOptions
}

type updateCall struct {
	channel string
	ts      string
	text    string
	blocks  []map[string]any
}

type reactionCall struct {
	channel   string
	name      string
	timestamp string
}

type getCall struct {
	channel   string
	timestamp string
}

type uploadCall struct {
	upload FileUpload
}

type historyCall struct {
	query HistoryQuery
}

// fakeSlack records every Web API call. Per-call hooks decide results;
// defaults succeed with generated timestamps.
type fakeSlack struct {
	mu        sync.Mutex
	seq       int
	posts     []postCall
	updates   []updateCall
	adds      []reactionCall
	removes   []reactionCall
	gets      []getCall
	uploads   []uploadCall
	histories []historyCall

	postFn    func(postCall) (*PostedMessage, error)
	updateFn  func(updateCall) (*PostedMessage, error)
	addFn     func(reactionCall) error
	removeFn  func(reactionCall) error
	getFn     func(getCall) ([]ReactionSummary, error)
	uploadFn  func(uploadCall) (*UploadResult, error)
	historyFn func(HistoryQuery) ([]HistoryMessage, error)
}

func (f *fakeSlack) nextTs() string {
	f.seq++
	return fmt.Sprintf("1700000000.%06d", f.seq)
}

func (f *fakeSlack) PostMessage(_ context.Context, channel, text string, opts PostOptions) (*PostedMessage, error) {
	f.mu.Lock()
	call := postCall{channel: channel, text: text, opts: opts}
	f.posts = append(f.posts, call)
	fn := f.postFn
	f.mu.Unlock()
	if fn != nil {
		return fn(call)
	}
	return &PostedMessage{Ts: f.nextTs()}, nil
}

func (f *fakeSlack) UpdateMessage(_ context.Context, channel, ts, text string, blocks []map[string]any) (*PostedMessage, error) {
	f.mu.Lock()
	call := updateCall{channel: channel, ts: ts, text: text, blocks: blocks}
	f.updates = append(f.updates, call)
	fn := f.updateFn
	f.mu.Unlock()
	if fn != nil {
		return fn(call)
	}
	return &PostedMessage{Ts: call.ts}, nil
}

func (f *fakeSlack) AddReaction(_ context.Context, channel, name, timestamp string) error {
	f.mu.Lock()
	call := reactionCall{channel: channel, name: name, timestamp: timestamp}
	f.adds = append(f.adds, call)
	fn := f.addFn
	f.mu.Unlock()
	if fn != nil {
		return fn(call)
	}
	return nil
}

func (f *fakeSlack) RemoveReaction(_ context.Context, channel, name, timestamp string) error {
	f.mu.Lock()
	call := reactionCall{channel: channel, name: name, timestamp: timestamp}
	f.removes = append(f.removes, call)
	fn := f.removeFn
	f.mu.Unlock()
	if fn != nil {
		return fn(call)
	}
	return nil
}

func (f *fakeSlack) Reactions(_ context.Context, channel, timestamp string) ([]ReactionSummary, error) {
	f.mu.Lock()
	call := getCall{channel: channel, timestamp: timestamp}
	f.gets = append(f.gets, call)
	fn := f.getFn
	f.mu.Unlock()
	if fn != nil {
		return fn(call)
	}
	return nil, nil
}

func (f *fakeSlack) UploadFile(_ context.Context, upload FileUpload) (*UploadResult, error) {
	f.mu.Lock()
	call := uploadCall{upload: upload}
	f.uploads = append(f.uploads, call)
	fn := f.uploadFn
	f.mu.Unlock()
	if fn != nil {
		return fn(call)
	}
	return &UploadResult{FileID: "F1"}, nil
}

func (f *fakeSlack) History(_ context.Context, query HistoryQuery) ([]HistoryMessage, error) {
	f.mu.Lock()
	f.histories = append(f.histories, historyCall{query: query})
	fn := f.historyFn
	f.mu.Unlock()
	if fn != nil {
		return fn(query)
	}
	return nil, nil
}

func slackErr(code string) error { return &SlackError{Code: code} }

func deliveryWith(payloadJSON string) *Delivery {
	return &Delivery{ID: "d1", ConversationID: "C1", PayloadJSON: payloadJSON}
}

func noopCurrent() error { return nil }

func TestParseDeliveryPayloadRejectsNonObjects(t *testing.T) {
	for _, payload := range []string{"not-json", "[1]", `"str"`, ""} {
		if _, err := ParseDeliveryPayload(payload); err == nil {
			t.Fatalf("ParseDeliveryPayload(%q) accepted a non-object", payload)
		}
	}
	if _, err := ParseDeliveryPayload(`{"operation":"post_message"}`); err != nil {
		t.Fatalf("ParseDeliveryPayload(valid) error = %v", err)
	}
}

func TestMutatePostMessagePostsAndDelivers(t *testing.T) {
	client := &fakeSlack{}
	thread := "1.5"
	delivery := deliveryWith(`{"text":"hello","clientMessageId":"cm-1"}`)
	delivery.ThreadTs = &thread

	ack, err := MutateDelivery(context.Background(), client, delivery, noopCurrent)
	if err != nil {
		t.Fatalf("MutateDelivery() error = %v", err)
	}
	if len(client.posts) != 1 {
		t.Fatalf("posts = %d, want 1", len(client.posts))
	}
	call := client.posts[0]
	if call.channel != "C1" || call.text != "hello" || call.opts.ThreadTs != "1.5" || call.opts.ClientMsgID != "cm-1" {
		t.Fatalf("post = %#v", call)
	}
	if ack.Outcome != OutcomeDelivered || ack.ProviderMessageIdentity == nil || ack.ProviderMessageIdentity.MessageTs == "" {
		t.Fatalf("ack = %#v", ack)
	}
}

func TestMutatePostMessageAllowsBlocksWithoutText(t *testing.T) {
	client := &fakeSlack{}
	payload := `{"blocks":[{"type":"section"}]}`
	if _, err := MutateDelivery(context.Background(), client, deliveryWith(payload), noopCurrent); err != nil {
		t.Fatalf("MutateDelivery() error = %v", err)
	}
	if len(client.posts) != 1 || client.posts[0].text != "" {
		t.Fatalf("posts = %#v", client.posts)
	}
}

func TestMutatePostMessageWithoutTextOrBlocksFails(t *testing.T) {
	client := &fakeSlack{}
	if _, err := MutateDelivery(context.Background(), client, deliveryWith(`{}`), noopCurrent); err == nil {
		t.Fatalf("MutateDelivery() accepted a textless payload")
	}
}

func TestMutatePostMessageStatusRefUpdatesExistingMessage(t *testing.T) {
	client := &fakeSlack{historyFn: func(HistoryQuery) ([]HistoryMessage, error) {
		return []HistoryMessage{{Ts: "1.9", ClientMsgID: "status-1"}}, nil
	}}
	payload := `{"text":"progress","statusDispatchRef":"status-1"}`
	ack, err := MutateDelivery(context.Background(), client, deliveryWith(payload), noopCurrent)
	if err != nil {
		t.Fatalf("MutateDelivery() error = %v", err)
	}
	if len(client.updates) != 1 || client.updates[0].ts != "1.9" || client.updates[0].text != "progress" {
		t.Fatalf("updates = %#v", client.updates)
	}
	if len(client.posts) != 0 {
		t.Fatalf("unexpected posts: %#v", client.posts)
	}
	if ack.ProviderMessageIdentity == nil || ack.ProviderMessageIdentity.MessageTs != "1.9" {
		t.Fatalf("ack = %#v", ack)
	}
}

func TestMutateChatUpdatePaths(t *testing.T) {
	t.Run("delivered with response timestamp", func(t *testing.T) {
		client := &fakeSlack{updateFn: func(updateCall) (*PostedMessage, error) {
			return &PostedMessage{Ts: "2.2"}, nil
		}}
		payload := `{"operation":"chat_update","text":"edited","providerMessageIdentity":{"conversationId":"C1","messageTs":"1.1"}}`
		ack, err := MutateDelivery(context.Background(), client, deliveryWith(payload), noopCurrent)
		if err != nil {
			t.Fatalf("MutateDelivery() error = %v", err)
		}
		if ack.ProviderMessageIdentity.MessageTs != "2.2" {
			t.Fatalf("ack = %#v", ack)
		}
	})

	t.Run("missing identity fails", func(t *testing.T) {
		client := &fakeSlack{}
		payload := `{"operation":"chat_update","text":"edited"}`
		if _, err := MutateDelivery(context.Background(), client, deliveryWith(payload), noopCurrent); err == nil {
			t.Fatalf("chat_update without identity succeeded")
		}
	})

	t.Run("failure with fallback posts it", func(t *testing.T) {
		client := &fakeSlack{updateFn: func(updateCall) (*PostedMessage, error) {
			return nil, slackErr("cant_edit_message")
		}}
		payload := `{"operation":"chat_update","text":"edited","providerMessageIdentity":{"conversationId":"C1","messageTs":"1.1"},` +
			`"fallbackText":"fallback","fallbackDispatchRef":"fb-1"}`
		ack, err := MutateDelivery(context.Background(), client, deliveryWith(payload), noopCurrent)
		if err != nil {
			t.Fatalf("MutateDelivery() error = %v", err)
		}
		if len(client.posts) != 1 || client.posts[0].opts.ClientMsgID != "fb-1" || client.posts[0].text != "fallback" {
			t.Fatalf("posts = %#v", client.posts)
		}
		if ack.Outcome != OutcomeDelivered {
			t.Fatalf("ack = %#v", ack)
		}
	})

	t.Run("failure without fallback retries", func(t *testing.T) {
		client := &fakeSlack{updateFn: func(updateCall) (*PostedMessage, error) {
			return nil, slackErr("rate_limited")
		}}
		payload := `{"operation":"chat_update","text":"edited","providerMessageIdentity":{"conversationId":"C1","messageTs":"1.1"}}`
		ack, err := MutateDelivery(context.Background(), client, deliveryWith(payload), noopCurrent)
		if err != nil || ack.Outcome != OutcomeRetry || ack.Reason != "rate_limited" {
			t.Fatalf("ack = (%#v, %v)", ack, err)
		}
	})
}

func TestMutateReactionPaths(t *testing.T) {
	base := `{"operation":"reaction_add","reaction":"eyes","targetMessageIdentity":{"conversationId":"C1","messageTs":"1.1"},` +
		`"statusDispatchRef":"status-1","fallbackText":"fallback","fallbackDispatchRef":"fb-1"}`

	t.Run("success delivers", func(t *testing.T) {
		client := &fakeSlack{}
		ack, err := MutateDelivery(context.Background(), client, deliveryWith(base), noopCurrent)
		if err != nil || ack.Outcome != OutcomeDelivered {
			t.Fatalf("ack = (%#v, %v)", ack, err)
		}
	})

	t.Run("unsupported code retargets the status message", func(t *testing.T) {
		attempts := 0
		client := &fakeSlack{
			addFn: func(reactionCall) error {
				attempts++
				if attempts == 1 {
					return slackErr("message_not_found")
				}
				return nil
			},
			historyFn: func(HistoryQuery) ([]HistoryMessage, error) {
				return []HistoryMessage{{Ts: "3.3", ClientMsgID: "status-1"}}, nil
			},
		}
		ack, err := MutateDelivery(context.Background(), client, deliveryWith(base), noopCurrent)
		if err != nil || ack.Outcome != OutcomeDelivered {
			t.Fatalf("ack = (%#v, %v)", ack, err)
		}
		if attempts != 2 || client.adds[1].timestamp != "3.3" {
			t.Fatalf("adds = %#v", client.adds)
		}
	})

	t.Run("non-unsupported code retries", func(t *testing.T) {
		client := &fakeSlack{addFn: func(reactionCall) error { return slackErr("rate_limited") }}
		ack, err := MutateDelivery(context.Background(), client, deliveryWith(base), noopCurrent)
		if err != nil || ack.Outcome != OutcomeRetry || ack.Reason != "rate_limited" {
			t.Fatalf("ack = (%#v, %v)", ack, err)
		}
	})

	t.Run("missing scope falls back", func(t *testing.T) {
		client := &fakeSlack{addFn: func(reactionCall) error { return slackErr("missing_scope") }}
		ack, err := MutateDelivery(context.Background(), client, deliveryWith(base), noopCurrent)
		if err != nil || ack.Outcome != OutcomeDelivered {
			t.Fatalf("ack = (%#v, %v)", ack, err)
		}
		if len(client.posts) != 1 || client.posts[0].text != "fallback" {
			t.Fatalf("posts = %#v", client.posts)
		}
	})

	t.Run("remove treats unsupported as delivered", func(t *testing.T) {
		client := &fakeSlack{removeFn: func(reactionCall) error { return slackErr("message_not_found") }}
		payload := `{"operation":"reaction_remove","reaction":"eyes","targetMessageIdentity":{"conversationId":"C1","messageTs":"1.1"}}`
		ack, err := MutateDelivery(context.Background(), client, deliveryWith(payload), noopCurrent)
		if err != nil || ack.Outcome != OutcomeDelivered {
			t.Fatalf("ack = (%#v, %v)", ack, err)
		}
	})
}

func TestMutateUploadFile(t *testing.T) {
	t.Run("share timestamp identifies the message", func(t *testing.T) {
		client := &fakeSlack{uploadFn: func(uploadCall) (*UploadResult, error) {
			return &UploadResult{FileID: "F1", ShareTs: "4.4"}, nil
		}}
		payload := `{"operation":"upload_file","fileName":"log.txt","fileContentBase64":"aGk=","text":"here"}`
		ack, err := MutateDelivery(context.Background(), client, deliveryWith(payload), noopCurrent)
		if err != nil {
			t.Fatalf("MutateDelivery() error = %v", err)
		}
		if client.uploads[0].upload.FileName != "log.txt" || string(client.uploads[0].upload.Content) != "hi" {
			t.Fatalf("upload = %#v", client.uploads[0])
		}
		if ack.ProviderMessageIdentity.MessageTs != "4.4" {
			t.Fatalf("ack = %#v", ack)
		}
	})

	t.Run("history scan recovers the share", func(t *testing.T) {
		client := &fakeSlack{
			uploadFn: func(uploadCall) (*UploadResult, error) { return &UploadResult{FileID: "F9"}, nil },
			historyFn: func(HistoryQuery) ([]HistoryMessage, error) {
				return []HistoryMessage{{Ts: "5.5", Files: []string{"F9"}}}, nil
			},
		}
		payload := `{"operation":"upload_file","fileName":"log.txt","fileContentBase64":"aGk="}`
		ack, err := MutateDelivery(context.Background(), client, deliveryWith(payload), noopCurrent)
		if err != nil || ack.ProviderMessageIdentity.MessageTs != "5.5" {
			t.Fatalf("ack = (%#v, %v)", ack, err)
		}
	})

	t.Run("missing payload fails", func(t *testing.T) {
		client := &fakeSlack{}
		if _, err := MutateDelivery(context.Background(), client, deliveryWith(`{"operation":"upload_file"}`), noopCurrent); err == nil {
			t.Fatalf("upload without payload succeeded")
		}
	})
}

func TestDeliverSegmentsStampsOnlyTheFirstSegment(t *testing.T) {
	client := &fakeSlack{}
	thread := "1.5"
	delivery := deliveryWith(`{"segments":["one","two","three"],"clientMessageId":"cm-1"}`)
	delivery.ThreadTs = &thread

	ack, err := MutateDelivery(context.Background(), client, delivery, noopCurrent)
	if err != nil {
		t.Fatalf("MutateDelivery() error = %v", err)
	}
	if len(client.posts) != 3 {
		t.Fatalf("posts = %d, want 3", len(client.posts))
	}
	for index, call := range client.posts {
		wantID := ""
		if index == 0 {
			wantID = "cm-1"
		}
		if call.opts.ClientMsgID != wantID || call.text != []string{"one", "two", "three"}[index] {
			t.Fatalf("post[%d] = %#v", index, call)
		}
	}
	if ack.ProviderMessageIdentity == nil {
		t.Fatalf("ack missing first-segment identity: %#v", ack)
	}
}

func TestReconcileReactionChecksProviderState(t *testing.T) {
	target := `"targetMessageIdentity":{"conversationId":"C1","messageTs":"1.1"}`
	cases := []struct {
		name      string
		payload   string
		reactions []ReactionSummary
		getErr    error
		want      string
		wantAckID bool
	}{
		{"add present", `{"operation":"reaction_add","reaction":"eyes",` + target + `}`, []ReactionSummary{{Name: "eyes"}}, nil, OutcomeDelivered, true},
		{"add absent", `{"operation":"reaction_add","reaction":"eyes",` + target + `}`, nil, nil, OutcomeRetry, false},
		{"remove absent", `{"operation":"reaction_remove","reaction":"eyes",` + target + `}`, nil, nil, OutcomeDelivered, true},
		{"get transport failure propagates", `{"operation":"reaction_add","reaction":"eyes",` + target + `}`, nil, errors.New("network down"), "", false},
	}
	for _, testCase := range cases {
		t.Run(testCase.name, func(t *testing.T) {
			client := &fakeSlack{getFn: func(getCall) ([]ReactionSummary, error) {
				return testCase.reactions, testCase.getErr
			}}
			ack, err := ReconcileDelivery(context.Background(), client, deliveryWith(testCase.payload), noopCurrent)
			if testCase.want == "" {
				if !errors.Is(err, testCase.getErr) {
					t.Fatalf("err = %v, want the transport failure propagated", err)
				}
				return
			}
			if err != nil {
				t.Fatalf("ReconcileDelivery() error = %v", err)
			}
			if ack.Outcome != testCase.want {
				t.Fatalf("ack = %#v, want %q", ack, testCase.want)
			}
			hasIdentity := ack.ProviderMessageIdentity != nil
			if hasIdentity != testCase.wantAckID {
				t.Fatalf("ack identity = %v", ack.ProviderMessageIdentity)
			}
		})
	}
}

func TestReconcileMessagesMatchesByIdentity(t *testing.T) {
	t.Run("found by timestamp delivers", func(t *testing.T) {
		client := &fakeSlack{historyFn: func(HistoryQuery) ([]HistoryMessage, error) {
			return []HistoryMessage{{Ts: "1.1", Text: "edited"}}, nil
		}}
		payload := `{"operation":"chat_update","text":"edited","providerMessageIdentity":{"conversationId":"C1","messageTs":"1.1"}}`
		ack, err := ReconcileDelivery(context.Background(), client, deliveryWith(payload), noopCurrent)
		if err != nil || ack.Outcome != OutcomeDelivered {
			t.Fatalf("ack = (%#v, %v)", ack, err)
		}
		if client.histories[0].query.AroundTs != "1.1" {
			t.Fatalf("query = %#v", client.histories[0])
		}
	})

	t.Run("chat_update text mismatch retries", func(t *testing.T) {
		client := &fakeSlack{historyFn: func(HistoryQuery) ([]HistoryMessage, error) {
			return []HistoryMessage{{Ts: "1.1", Text: "old"}}, nil
		}}
		payload := `{"operation":"chat_update","text":"edited","providerMessageIdentity":{"conversationId":"C1","messageTs":"1.1"}}`
		ack, err := ReconcileDelivery(context.Background(), client, deliveryWith(payload), noopCurrent)
		if err != nil || ack.Outcome != OutcomeRetry || ack.Reason != "provider_mutation_absent" {
			t.Fatalf("ack = (%#v, %v)", ack, err)
		}
	})

	t.Run("missing update falls back", func(t *testing.T) {
		client := &fakeSlack{historyFn: func(HistoryQuery) ([]HistoryMessage, error) {
			return nil, nil
		}}
		payload := `{"operation":"chat_update","text":"edited","fallbackText":"fb","fallbackDispatchRef":"fb-1",` +
			`"providerMessageIdentity":{"conversationId":"C1","messageTs":"1.1"}}`
		ack, err := ReconcileDelivery(context.Background(), client, deliveryWith(payload), noopCurrent)
		if err != nil || ack.Outcome != OutcomeDelivered {
			t.Fatalf("ack = (%#v, %v)", ack, err)
		}
		if len(client.posts) != 1 {
			t.Fatalf("posts = %#v", client.posts)
		}
	})

	t.Run("nothing found retries", func(t *testing.T) {
		client := &fakeSlack{}
		ack, err := ReconcileDelivery(context.Background(), client, deliveryWith(`{"text":"hi"}`), noopCurrent)
		if err != nil || ack.Outcome != OutcomeRetry || ack.Reason != "provider_mutation_absent" {
			t.Fatalf("ack = (%#v, %v)", ack, err)
		}
	})
}

func TestMutationPropagatesStaleRuntime(t *testing.T) {
	client := &fakeSlack{}
	stale := errors.New("stale runtime")
	if _, err := MutateDelivery(context.Background(), client, deliveryWith(`{"text":"hi"}`), func() error { return stale }); !errors.Is(err, stale) {
		t.Fatalf("err = %v, want stale propagated", err)
	}
	if _, err := ReconcileDelivery(context.Background(), client, deliveryWith(`{"text":"hi"}`), func() error { return stale }); !errors.Is(err, stale) {
		t.Fatalf("err = %v, want stale propagated", err)
	}
	if len(client.posts) != 0 {
		t.Fatalf("posts = %#v", client.posts)
	}
}

func TestRedactTokensMasksAllShapes(t *testing.T) {
	message := "xapp-1A2b xoxb-9-8-7 failed for XOXE.tok and plain xoxp.secret"
	want := "<redacted> <redacted> failed for <redacted> and plain <redacted>"
	if got := RedactTokens(message); got != want {
		t.Fatalf("RedactTokens() = %q, want %q", got, want)
	}
}
