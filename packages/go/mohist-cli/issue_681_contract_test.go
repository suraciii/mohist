package mohistcli

import (
	"context"
	"io"
	"net/http"
	"strings"
	"testing"
)

const manageAccessResponse = `{"success":true,"data":{"connection":{"id":"s1","projectId":"proj"},"accessPolicy":"allowlist","allowMembers":["U1","U2"],"anyoneDisclosure":"disclosure"}}`

func TestIssue681SlackEditPostsManageAccessAllowlist(t *testing.T) {
	var got *http.Request
	calls := 0
	deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		calls++
		got = r
		return response(http.StatusOK, manageAccessResponse), nil
	}), map[string]string{"MOHIST_TOKEN": "operator-token"})
	args := []string{"slack", "edit", "s1", "--project", "proj", "--access-policy", "allowlist", "--allow-member", "U1", "--allow-member", "U2"}
	if code := Run(context.Background(), args, deps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if calls != 1 {
		t.Fatalf("HTTP calls=%d, want exactly one POST", calls)
	}
	if got == nil || got.Method != http.MethodPost || got.URL.Path != "/api/projects/proj/slack-connections/s1/manage-access" {
		t.Fatalf("request=%v, want POST /api/projects/proj/slack-connections/s1/manage-access", got)
	}
	if got.Header.Get("Authorization") != "Bearer operator-token" {
		t.Fatalf("headers=%v, want local Authorization", got.Header)
	}
	data, err := io.ReadAll(got.Body)
	if err != nil || string(data) != `{"accessPolicy":"allowlist","allowMembers":["U1","U2"]}` {
		t.Fatalf("body=%q err=%v, want allowlist body", data, err)
	}
	if !strings.Contains(out.String(), `"accessPolicy":"allowlist"`) || !strings.Contains(out.String(), `"allowMembers":["U1","U2"]`) {
		t.Fatalf("stdout=%q, want returned manage-access state", out.String())
	}
}

func TestIssue681SlackEditOwnerOnlySendsEmptyMembers(t *testing.T) {
	for _, policy := range []string{"owner_only", "anyone"} {
		t.Run(policy, func(t *testing.T) {
			var body string
			deps, _, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
				data, _ := io.ReadAll(r.Body)
				body = string(data)
				return response(http.StatusOK, `{"success":true,"data":{"connection":{"id":"s1"},"accessPolicy":"`+policy+`","allowMembers":[],"anyoneDisclosure":"d"}}`), nil
			}), map[string]string{"MOHIST_TOKEN": "operator-token"})
			args := []string{"slack", "edit", "s1", "--project", "proj", "--access-policy", policy}
			if code := Run(context.Background(), args, deps); code != ExitOK {
				t.Fatalf("code=%d stderr=%q", code, errOut.String())
			}
			if body != `{"accessPolicy":"`+policy+`","allowMembers":[]}` {
				t.Fatalf("body=%q, want empty allowMembers array", body)
			}
		})
	}
}

func TestIssue681SlackEditAllowlistWithoutMembersClears(t *testing.T) {
	var body string
	calls := 0
	deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		calls++
		data, _ := io.ReadAll(r.Body)
		body = string(data)
		return response(http.StatusOK, manageAccessResponse), nil
	}), map[string]string{"MOHIST_TOKEN": "operator-token"})
	args := []string{"slack", "edit", "s1", "--project", "proj", "--access-policy", "allowlist"}
	if code := Run(context.Background(), args, deps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if calls != 1 || body != `{"accessPolicy":"allowlist","allowMembers":[]}` {
		t.Fatalf("calls=%d body=%q, want one clear-the-list request", calls, body)
	}
}

func TestIssue681SlackEditNormalizesPolicyAndMembersOnce(t *testing.T) {
	var body string
	deps, _, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		data, _ := io.ReadAll(r.Body)
		body = string(data)
		return response(http.StatusOK, manageAccessResponse), nil
	}), map[string]string{"MOHIST_TOKEN": "operator-token"})
	args := []string{"slack", "edit", "s1", "--project", "proj", "--access-policy", " AllowList ", "--allow-member", " U1 ", "--allow-member", "U1"}
	if code := Run(context.Background(), args, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if body != `{"accessPolicy":"allowlist","allowMembers":["U1"]}` {
		t.Fatalf("body=%q, want canonical lowercase and de-duplicated members", body)
	}
}

func TestIssue681SlackEditRejectsMissingPolicyLocally(t *testing.T) {
	cases := []struct {
		name string
		args []string
	}{
		{name: "no-project", args: []string{"slack", "edit", "s1"}},
		{name: "with-project", args: []string{"slack", "edit", "s1", "--project", "proj"}},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			probe := discoveryProbe{}
			out, errOut := &strings.Builder{}, &strings.Builder{}
			if code := Run(context.Background(), tc.args, probe.deps(out, errOut)); code != ExitUsage {
				t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
			}
			if out.Len() != 0 {
				t.Fatalf("stdout=%q, want empty", out.String())
			}
			if !strings.Contains(errOut.String(), "--access-policy") {
				t.Fatalf("stderr=%q, want --access-policy diagnostic", errOut.String())
			}
			probe.assertUnused(t)
		})
	}
}

func TestIssue681SlackEditInvalidInputsAreLocal(t *testing.T) {
	cases := []struct {
		name string
		args []string
	}{
		{name: "unknown-policy", args: []string{"slack", "edit", "s1", "--project", "proj", "--access-policy", "everyone"}},
		{name: "blank-policy", args: []string{"slack", "edit", "s1", "--project", "proj", "--access-policy", "   "}},
		{name: "blank-member", args: []string{"slack", "edit", "s1", "--project", "proj", "--access-policy", "allowlist", "--allow-member", "   "}},
		{name: "owner-member", args: []string{"slack", "edit", "s1", "--project", "proj", "--access-policy", "owner_only", "--allow-member", "U1"}},
		{name: "anyone-member", args: []string{"slack", "edit", "s1", "--project", "proj", "--access-policy", "anyone", "--allow-member", "U1"}},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			probe := discoveryProbe{}
			out, errOut := &strings.Builder{}, &strings.Builder{}
			if code := Run(context.Background(), tc.args, probe.deps(out, errOut)); code != ExitUsage {
				t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
			}
			if !strings.Contains(errOut.String(), "USAGE") {
				t.Fatalf("stderr=%q, want leaf USAGE block", errOut.String())
			}
			probe.assertUnused(t)
		})
	}
}

func TestIssue681SlackEditUndeclaredFlagsAreLocal(t *testing.T) {
	for _, flag := range []string{"--bot-name", "--avatar-hash"} {
		t.Run(flag, func(t *testing.T) {
			probe := discoveryProbe{}
			out, errOut := &strings.Builder{}, &strings.Builder{}
			args := []string{"slack", "edit", "s1", "--project", "proj", "--access-policy", "allowlist", flag, "value"}
			if code := Run(context.Background(), args, probe.deps(out, errOut)); code != ExitUsage {
				t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
			}
			if !strings.Contains(errOut.String(), "unknown option "+flag) {
				t.Fatalf("stderr=%q, want unknown option %s", errOut.String(), flag)
			}
			probe.assertUnused(t)
		})
	}
}

func TestIssue681SlackEditDiscoveryIsLocal(t *testing.T) {
	for _, args := range [][]string{
		{"slack", "edit", "s1", "--help"},
		{"slack", "edit", "s1", "--json"},
	} {
		t.Run(strings.Join(args, " "), func(t *testing.T) {
			probe := discoveryProbe{}
			out, errOut := &strings.Builder{}, &strings.Builder{}
			if code := Run(context.Background(), args, probe.deps(out, errOut)); code != ExitOK {
				t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
			}
			if out.Len() == 0 || errOut.Len() != 0 {
				t.Fatalf("stdout=%q stderr=%q, want local output without editable field", out.String(), errOut.String())
			}
			for _, field := range slackEditFields {
				if !strings.Contains(out.String(), field) {
					t.Fatalf("stdout=%q, want edit field %q", out.String(), field)
				}
			}
			if strings.Contains(out.String(), "projectId") || strings.Contains(out.String(), "workspaceTeamId") {
				t.Fatalf("stdout=%q, want only manage-access envelope fields", out.String())
			}
			probe.assertUnused(t)
		})
	}
}

func TestIssue681SlackEditServerRejectionIsNotSuccess(t *testing.T) {
	for _, status := range []int{http.StatusBadRequest, http.StatusNotFound, http.StatusConflict} {
		t.Run(http.StatusText(status), func(t *testing.T) {
			deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
				return response(status, `{"success":false,"error":"rejected","code":"rejected"}`), nil
			}), map[string]string{"MOHIST_TOKEN": "operator-token"})
			args := []string{"slack", "edit", "s1", "--project", "proj", "--access-policy", "allowlist"}
			if code := Run(context.Background(), args, deps); code != ExitOperation {
				t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
			}
			if out.Len() != 0 {
				t.Fatalf("stdout=%q, want empty for rejected mutation", out.String())
			}
			if errOut.Len() == 0 {
				t.Fatal("stderr empty, want server rejection diagnostic")
			}
		})
	}
}

func TestIssue681SlackEditManagerModeUsesBrokerAndDirectRoute(t *testing.T) {
	var got *http.Request
	calls := 0
	deps, out, errOut := testDeps(nil, map[string]string{"MOHIST_MANAGER_MODE": "1"})
	deps.ManagerCredentialBroker = func(_ context.Context, r *http.Request) (*http.Response, error) {
		calls++
		got = r
		return response(http.StatusOK, manageAccessResponse), nil
	}
	args := []string{"slack", "edit", "s1", "--project", "proj", "--access-policy", "allowlist", "--allow-member", "U1"}
	if code := Run(context.Background(), args, deps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if calls != 1 {
		t.Fatalf("broker calls=%d, want exactly one", calls)
	}
	if got == nil || got.Method != http.MethodPost || got.URL.Path != "/api/projects/proj/slack-connections/s1/manage-access" {
		t.Fatalf("request=%v, want direct project-scoped manage-access route", got)
	}
	if got.Header.Get("X-Mohist-Manager-Mode") != "1" {
		t.Fatalf("headers=%v, want X-Mohist-Manager-Mode 1", got.Header)
	}
	if got.Header.Get("Authorization") != "" {
		t.Fatalf("headers=%v, want no local Authorization in manager mode", got.Header)
	}
}

func TestIssue681SlackEditPreservesOwnerMember(t *testing.T) {
	var body string
	deps, _, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		data, _ := io.ReadAll(r.Body)
		body = string(data)
		return response(http.StatusOK, manageAccessResponse), nil
	}), map[string]string{"MOHIST_TOKEN": "operator-token"})
	// The CLI never filters the Owner; the Server owns Owner semantics.
	args := []string{"slack", "edit", "s1", "--project", "proj", "--access-policy", "allowlist", "--allow-member", "UOWNER", "--allow-member", "U2"}
	if code := Run(context.Background(), args, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if body != `{"accessPolicy":"allowlist","allowMembers":["UOWNER","U2"]}` {
		t.Fatalf("body=%q, want Owner passed through unfiltered and in order", body)
	}
}

func TestIssue681SlackEditJSONCatalog(t *testing.T) {
	t.Run("bare-json-lists-edit-fields", func(t *testing.T) {
		probe := discoveryProbe{}
		out, errOut := &strings.Builder{}, &strings.Builder{}
		if code := Run(context.Background(), []string{"slack", "edit", "s1", "--json"}, probe.deps(out, errOut)); code != ExitOK {
			t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
		}
		want := strings.Join(slackEditFields, "\n") + "\n"
		if out.String() != want {
			t.Fatalf("stdout=%q, want exactly %q", out.String(), want)
		}
		probe.assertUnused(t)
	})

	t.Run("selected-fields-are-real", func(t *testing.T) {
		deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
			return response(http.StatusOK, manageAccessResponse), nil
		}), map[string]string{"MOHIST_TOKEN": "operator-token"})
		args := []string{"slack", "edit", "s1", "--project", "proj", "--access-policy", "allowlist", "--json", "accessPolicy,allowMembers"}
		if code := Run(context.Background(), args, deps); code != ExitOK {
			t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
		}
		if !strings.Contains(out.String(), `"accessPolicy":"allowlist"`) || !strings.Contains(out.String(), `"allowMembers":["U1","U2"]`) {
			t.Fatalf("stdout=%q, want selected real values", out.String())
		}
		if strings.Contains(out.String(), `"connection"`) {
			t.Fatalf("stdout=%q, want unselected fields omitted", out.String())
		}
	})

	t.Run("id-is-unknown", func(t *testing.T) {
		probe := discoveryProbe{}
		out, errOut := &strings.Builder{}, &strings.Builder{}
		args := []string{"slack", "edit", "s1", "--json", "id"}
		if code := Run(context.Background(), args, probe.deps(out, errOut)); code != ExitUsage {
			t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
		}
		if out.Len() != 0 {
			t.Fatalf("stdout=%q, want empty", out.String())
		}
		if !strings.Contains(errOut.String(), `unknown JSON field "id"`) {
			t.Fatalf("stderr=%q, want unknown JSON field id", errOut.String())
		}
		probe.assertUnused(t)
	})
}
