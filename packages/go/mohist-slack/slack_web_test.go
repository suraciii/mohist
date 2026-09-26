package mohistslack

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestSlackWebPostMessageSendsIdentityAndFormFields(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost || r.URL.Path != "/chat.postMessage" {
			t.Errorf("request = %s %s", r.Method, r.URL.Path)
		}
		if auth := r.Header.Get("Authorization"); auth != "Bearer xoxb-test" {
			t.Errorf("authorization = %q", auth)
		}
		if err := r.ParseForm(); err != nil {
			t.Error(err)
			return
		}
		for key, want := range map[string]string{
			"channel":       "C1",
			"text":          "hello",
			"thread_ts":     "1700.1",
			"client_msg_id": "dispatch-1",
		} {
			if got := r.Form.Get(key); got != want {
				t.Errorf("form[%s] = %q, want %q", key, got, want)
			}
		}
		var blocks []map[string]any
		if err := json.Unmarshal([]byte(r.Form.Get("blocks")), &blocks); err != nil {
			t.Error(err)
			return
		}
		if len(blocks) != 1 || blocks[0]["type"] != "section" {
			t.Errorf("blocks = %#v", blocks)
		}
		_, _ = w.Write([]byte(`{"ok":true,"ts":"1700.2"}`))
	}))
	defer server.Close()
	web := newSlackWebWithAPIBaseURL("xoxb-test", server.Client(), server.URL)

	ts, err := web.PostMessage(context.Background(), PostMessageInput{
		Channel:     "C1",
		Text:        "hello",
		ThreadTs:    "1700.1",
		ClientMsgID: "dispatch-1",
		Blocks:      []map[string]any{{"type": "section"}},
	})

	if err != nil {
		t.Fatal(err)
	}
	if ts != "1700.2" {
		t.Fatalf("timestamp = %q", ts)
	}
}

func TestSlackWebUpdateMessageUsesInjectedAPIBaseURL(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/chat.update" {
			t.Errorf("path = %q", r.URL.Path)
		}
		if err := r.ParseForm(); err != nil {
			t.Error(err)
			return
		}
		if r.Form.Get("channel") != "C1" || r.Form.Get("ts") != "1700.1" || r.Form.Get("text") != "updated" {
			t.Errorf("form = %v", r.Form)
		}
		if r.Form.Has("client_msg_id") {
			t.Errorf("chat.update unexpectedly sent client_msg_id: %v", r.Form)
		}
		_, _ = w.Write([]byte(`{"ok":true,"ts":"1700.1"}`))
	}))
	defer server.Close()
	web := newSlackWebWithAPIBaseURL("xoxb-test", server.Client(), server.URL)

	ts, err := web.UpdateMessage(context.Background(), UpdateMessageInput{
		Channel: "C1",
		TS:      "1700.1",
		Text:    "updated",
	})

	if err != nil || ts != "1700.1" {
		t.Fatalf("UpdateMessage() = (%q, %v)", ts, err)
	}
}

func TestSlackWebGetConversationRepliesScopesThreadAndPagination(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/conversations.replies" {
			t.Errorf("path = %q", r.URL.Path)
		}
		if err := r.ParseForm(); err != nil {
			t.Error(err)
			return
		}
		for key, want := range map[string]string{
			"channel": "C1",
			"ts":      "1700.1",
			"cursor":  "page-2",
			"limit":   "200",
		} {
			if got := r.PostForm.Get(key); got != want {
				t.Errorf("form[%s] = %q, want %q", key, got, want)
			}
		}
		_, _ = w.Write([]byte(`{"ok":true,"messages":[{"ts":"1700.2","client_msg_id":"cmid","text":"hi"}],"has_more":true,"response_metadata":{"next_cursor":"page-3"}}`))
	}))
	defer server.Close()
	web := newSlackWebWithAPIBaseURL("xoxb-test", server.Client(), server.URL)

	page, err := web.GetConversationReplies(context.Background(), RepliesInput{Channel: "C1", TS: "1700.1", Cursor: "page-2", Limit: 200})
	if err != nil {
		t.Fatal(err)
	}
	if len(page.Messages) != 1 || page.Messages[0].ClientMsgID != "cmid" || page.Messages[0].TS != "1700.2" {
		t.Fatalf("messages = %+v", page.Messages)
	}
	if !page.HasMore || page.NextCursor != "page-3" {
		t.Fatalf("page = %+v", page)
	}
}

func TestSlackWebGetConversationRepliesRejectionReturnsCodedError(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		_, _ = w.Write([]byte(`{"ok":false,"error":"thread_not_found"}`))
	}))
	defer server.Close()
	web := newSlackWebWithAPIBaseURL("xoxb-test", server.Client(), server.URL)

	_, err := web.GetConversationReplies(context.Background(), RepliesInput{Channel: "C1", TS: "missing", Limit: 200})

	// The typed slack-go face returns its own rejection type; SlackErrorCode
	// is the normalization every caller uses, so assert through it.
	if code := SlackErrorCode(err); code != "thread_not_found" {
		t.Fatalf("error = %#v (code %q)", err, code)
	}
}

func TestSlackWebChatRejectionReturnsCodedError(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		_, _ = w.Write([]byte(`{"ok":false,"error":"channel_not_found"}`))
	}))
	defer server.Close()
	web := newSlackWebWithAPIBaseURL("xoxb-test", server.Client(), server.URL)

	_, err := web.PostMessage(context.Background(), PostMessageInput{Channel: "missing", Text: "hello"})

	var slackErr *SlackError
	if !errors.As(err, &slackErr) || slackErr.Code != "channel_not_found" {
		t.Fatalf("error = %#v", err)
	}
}
