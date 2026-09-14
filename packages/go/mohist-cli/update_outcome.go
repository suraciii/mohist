package mohistcli

import (
	"context"
	"path/filepath"
	"strings"
	"time"
)

type updateOutcomeReporter struct {
	client     *client
	jobID      string
	sourcePath string
	now        func() time.Time
	stageLogs  []cliOutcomeLog
	finished   bool
}

type cliOutcomeLog struct {
	Stage   string
	Message string
}

func newUpdateOutcomeReporter(ctx context.Context, deps Dependencies, c command, sourcePath string) *updateOutcomeReporter {
	_ = ctx
	now := deps.Now
	if now == nil {
		now = time.Now
	}
	reporter := &updateOutcomeReporter{
		jobID:      newManagedUpdateID(),
		sourcePath: sourcePath,
		now:        now,
	}
	cfg, err := ResolveConfig(deps)
	if err != nil {
		return reporter
	}
	if explicit := strings.TrimSpace(argValue(c.args, "server-url", "")); explicit != "" {
		cfg.ServerURL = explicit
	}
	reporter.client, _ = newClient(cfg, deps.HTTPClient)
	return reporter
}

func resolveUpdateOutcomeSource(deps Dependencies, c command) string {
	root := strings.TrimSpace(argValue(c.args, "repo-root", ""))
	if root == "" {
		root = deps.CurrentDirectory()
	}
	absolute, err := filepath.Abs(root)
	if err != nil {
		return root
	}
	return absolute
}

func (r *updateOutcomeReporter) stage(ctx context.Context, deps Dependencies, stage, message string) {
	if r == nil || r.finished {
		return
	}
	r.stageLogs = append(r.stageLogs, cliOutcomeLog{Stage: stage, Message: message})
	r.post(ctx, deps, "running", "")
}

func (r *updateOutcomeReporter) finish(ctx context.Context, deps Dependencies, code int) {
	if code == ExitOK {
		r.finishStatus(ctx, deps, "succeeded", "succeeded")
		return
	}
	r.finishStatus(ctx, deps, "failed", "failed")
}

func (r *updateOutcomeReporter) finishStatus(ctx context.Context, deps Dependencies, status, outcome string) {
	if r == nil || r.finished {
		return
	}
	r.finished = true
	r.post(ctx, deps, status, outcome)
}

func (r *updateOutcomeReporter) post(ctx context.Context, deps Dependencies, status, outcome string) {
	if r == nil || r.client == nil {
		return
	}
	body := map[string]any{
		"jobId":      r.jobID,
		"status":     status,
		"stage":      "Ready",
		"outcome":    nil,
		"sourcePath": r.sourcePath,
	}
	if len(r.stageLogs) > 0 {
		entry := r.stageLogs[len(r.stageLogs)-1]
		body["stage"] = entry.Stage
		body["logs"] = []map[string]string{{
			"at":      r.now().UTC().Format("2006-01-02T15:04:05.9999999Z07:00"),
			"stage":   entry.Stage,
			"message": entry.Message,
		}}
	}
	if outcome != "" {
		body["outcome"] = outcome
	}
	_, _, _ = postJSON(ctx, deps, r.client, "/api/system/update/outcome", body)
}

func runInstallUpdateWithOutcome(ctx context.Context, deps Dependencies, c command) int {
	if c.outcome != nil || !strings.HasPrefix(c.kind, "update-") {
		return runInstallUpdateInternal(ctx, deps, c)
	}
	c.outcome = newUpdateOutcomeReporter(ctx, deps, c, resolveUpdateOutcomeSource(deps, c))
	c.outcome.stage(ctx, deps, "Preparing update", "CLI update started")
	code := runInstallUpdateInternal(ctx, deps, c)
	c.outcome.finish(ctx, deps, code)
	return code
}

func runInstallUpdateInternal(ctx context.Context, deps Dependencies, c command) int {
	return runInstallUpdateOriginal(ctx, deps, c)
}
