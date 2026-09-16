package mohistcli

import (
	"context"
	"strings"
	"testing"
)

func TestOperationsRejectUnmappedFlagsBeforeDependencies(t *testing.T) {
	cases := []struct {
		args []string
		leaf string
	}{
		{[]string{"runner", "list", "--scope", "global"}, "runner list"},
		{[]string{"service", "start", "runner", "--follow"}, "service start runner"},
		{[]string{"service", "status", "server", "--lines", "5"}, "service status server"},
		{[]string{"service", "logs", "runner", "--unit-dir", "/units"}, "service logs runner"},
		{[]string{"service", "restart", "server", "--bogus"}, "service restart server"},
		{[]string{"event", "tail", "--bogus"}, "event tail"},
		{[]string{"event", "dead-letter", "list", "--bogus"}, "event dead-letter list"},
		{[]string{"event", "dead-letter", "redeliver", "7", "--handler", "dispatch"}, "event dead-letter redeliver"},
		{[]string{"event", "dead-letter", "redeliver", "7", "--limit", "5"}, "event dead-letter redeliver"},
		{[]string{"otel", "status", "--service", "runner"}, "otel status"},
		{[]string{"otel", "query", "select 1", "--limit", "5"}, "otel query"},
		{[]string{"otel", "traces", "--bogus"}, "otel traces"},
	}
	for _, tc := range cases {
		t.Run(strings.Join(tc.args, " "), func(t *testing.T) {
			probe := discoveryProbe{}
			out, errOut := &strings.Builder{}, &strings.Builder{}
			code := Run(context.Background(), tc.args, probe.deps(out, errOut))
			if code != ExitUsage || out.Len() != 0 || !strings.Contains(errOut.String(), "unknown option") || !strings.Contains(errOut.String(), "USAGE\n    mo "+tc.leaf) {
				t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
			}
			probe.assertUnused(t)
		})
	}
}
