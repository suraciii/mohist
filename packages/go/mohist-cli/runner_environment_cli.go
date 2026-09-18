package mohistcli

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"
)

const (
	runnerEnvironmentCandidateFileName   = "runner-environment.candidate.env"
	runnerEnvironmentCandidateMetaName   = "runner-environment.candidate.json"
	runnerEnvironmentPreviousFileName    = "runner-environment.previous.env"
	runnerEnvironmentApplicationMetaName = "runner-environment-application.json"
	runnerEnvironmentPollInterval        = time.Second
	runnerEnvironmentMaxPolls            = 45
)

type runnerEnvironmentCandidateMetadata struct {
	Version     string   `json:"version"`
	Variables   []string `json:"variables"`
	CapturedAt  string   `json:"capturedAt"`
	ContentHash string   `json:"contentHash"`
}

type runnerEnvironmentApplicationMetadata struct {
	UpdateID                 string `json:"updateId"`
	TargetVersion            string `json:"targetVersion"`
	BaseProcessGeneration    string `json:"baseProcessGeneration"`
	BaseConnectionGeneration string `json:"baseConnectionGeneration"`
	StartedAt                string `json:"startedAt"`
}

type runnerEnvironmentIdentity struct {
	RunnerID             string `json:"runnerId"`
	Status               string `json:"status"`
	ConnectionState      string `json:"connectionState"`
	ProcessGeneration    string `json:"processGeneration"`
	ConnectionGeneration string `json:"connectionGeneration"`
	EnvironmentVersion   string `json:"environmentVersion"`
	EnvironmentLoadedAt  string `json:"environmentLoadedAt"`
}

type runnerEnvironmentApplicationResponse struct {
	RunnerID    string                            `json:"runnerId"`
	UpdateID    string                            `json:"updateId"`
	Status      string                            `json:"status"`
	Application *runnerEnvironmentApplicationView `json:"application"`
}

type runnerEnvironmentApplicationView struct {
	UpdateID                 string                          `json:"updateId"`
	TargetVersion            string                          `json:"targetVersion"`
	PreviousVersion          string                          `json:"previousVersion"`
	Phase                    string                          `json:"phase"`
	FailureCode              string                          `json:"failureCode"`
	BaseProcessGeneration    string                          `json:"baseProcessGeneration"`
	BaseConnectionGeneration string                          `json:"baseConnectionGeneration"`
	RequestedAt              string                          `json:"requestedAt"`
	CompletedAt              string                          `json:"completedAt"`
	Settlement               runnerEnvironmentSettlementView `json:"settlement"`
}

type runnerEnvironmentSettlementView struct {
	OwnerLedgersEmpty                bool   `json:"ownerLedgersEmpty"`
	CurrentGenerationReportedSettled bool   `json:"currentGenerationReportedSettled"`
	Settled                          bool   `json:"settled"`
	ActiveWorkCount                  int    `json:"activeWorkCount"`
	InFlightCount                    int    `json:"inFlightCount"`
	AwaitingAckCount                 int    `json:"awaitingAckCount"`
	ProcessGeneration                string `json:"processGeneration"`
	ConnectionGeneration             string `json:"connectionGeneration"`
}

type runnerEnvironmentDiff struct {
	Added   []string
	Removed []string
	Changed []string
}

var errRunnerEnvironmentCandidateAbsent = errors.New("no captured Runner environment candidate")
var errRunnerEnvironmentApplicationAbsent = errors.New("no local Runner environment application")

func newRunnerEnvironmentID() string {
	var value [16]byte
	if _, err := rand.Read(value[:]); err != nil {
		panic("crypto/rand failed: " + err.Error())
	}
	value[6] = (value[6] & 0x0f) | 0x40
	value[8] = (value[8] & 0x3f) | 0x80
	encoded := make([]byte, 36)
	hex.Encode(encoded[0:8], value[0:4])
	encoded[8] = '-'
	hex.Encode(encoded[9:13], value[4:6])
	encoded[13] = '-'
	hex.Encode(encoded[14:18], value[6:8])
	encoded[18] = '-'
	hex.Encode(encoded[19:23], value[8:10])
	encoded[23] = '-'
	hex.Encode(encoded[24:36], value[10:16])
	return string(encoded)
}

func runnerEnvironmentConfigDir(deps Dependencies) (string, error) {
	home, err := deps.HomeDir()
	if err != nil || strings.TrimSpace(home) == "" {
		return "", errors.New("home directory is unavailable")
	}
	return filepath.Join(home, ".config", "mohist"), nil
}

func runnerEnvironmentPaths(deps Dependencies) (map[string]string, error) {
	root, err := runnerEnvironmentConfigDir(deps)
	if err != nil {
		return nil, err
	}
	return map[string]string{
		"active":        filepath.Join(root, "runner-environment.env"),
		"candidate":     filepath.Join(root, runnerEnvironmentCandidateFileName),
		"candidateMeta": filepath.Join(root, runnerEnvironmentCandidateMetaName),
		"previous":      filepath.Join(root, runnerEnvironmentPreviousFileName),
		"application":   filepath.Join(root, runnerEnvironmentApplicationMetaName),
	}, nil
}

func resolveRunnerEnvironmentRunnerID(deps Dependencies, cmd command) string {
	if value := strings.TrimSpace(argValue(cmd.args, "runner-id", "")); value != "" {
		return value
	}
	if value, ok := deps.Lookup("RUNNER_ID"); ok && strings.TrimSpace(value) != "" {
		return strings.TrimSpace(value)
	}
	return defaultRunnerID()
}

func readRunnerEnvironmentCandidate(deps Dependencies, paths map[string]string) (runnerEnvironmentCandidateMetadata, string, error) {
	metaText, err := deps.ReadFile(paths["candidateMeta"])
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return runnerEnvironmentCandidateMetadata{}, "", errRunnerEnvironmentCandidateAbsent
		}
		return runnerEnvironmentCandidateMetadata{}, "", fmt.Errorf("Runner environment candidate metadata could not be read: %w", err)
	}
	var metadata runnerEnvironmentCandidateMetadata
	if json.Unmarshal([]byte(metaText), &metadata) != nil || strings.TrimSpace(metadata.Version) == "" {
		return runnerEnvironmentCandidateMetadata{}, "", errors.New("Runner environment candidate metadata is invalid")
	}
	content, err := deps.ReadFile(paths["candidate"])
	if err != nil {
		return runnerEnvironmentCandidateMetadata{}, "", errors.New("Runner environment candidate file is missing")
	}
	if metadata.ContentHash != "" {
		digest := sha256.Sum256([]byte(content))
		if metadata.ContentHash != hex.EncodeToString(digest[:]) {
			return runnerEnvironmentCandidateMetadata{}, "", errors.New("Runner environment candidate content does not match its metadata")
		}
	}
	return metadata, content, nil
}

func readRunnerEnvironmentApplication(deps Dependencies, paths map[string]string) (runnerEnvironmentApplicationMetadata, bool, error) {
	text, err := deps.ReadFile(paths["application"])
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return runnerEnvironmentApplicationMetadata{}, false, errRunnerEnvironmentApplicationAbsent
		}
		return runnerEnvironmentApplicationMetadata{}, false, fmt.Errorf("Runner environment application metadata could not be read: %w", err)
	}
	var metadata runnerEnvironmentApplicationMetadata
	if json.Unmarshal([]byte(text), &metadata) != nil || strings.TrimSpace(metadata.UpdateID) == "" || strings.TrimSpace(metadata.TargetVersion) == "" {
		return runnerEnvironmentApplicationMetadata{}, true, errors.New("Runner environment application metadata is invalid")
	}
	return metadata, true, nil
}

func writeRunnerEnvironmentJSON(deps Dependencies, path string, value any) error {
	encoded, err := json.MarshalIndent(value, "", "  ")
	if err != nil {
		return err
	}
	encoded = append(encoded, '\n')
	if err := deps.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		return err
	}
	return deps.WriteFileAtomic(path, encoded, 0o600)
}

func runRunnerEnvironment(ctx context.Context, deps Dependencies, c *client, cmd command) int {
	action := strings.TrimPrefix(cmd.kind, "runner-environment-")
	if action == "capture" {
		return captureRunnerEnvironmentCandidate(deps, cmd)
	}
	if c == nil {
		writeError(deps.Stderr, errors.New("Mohist Server client is unavailable"))
		return ExitOperation
	}
	switch action {
	case "status":
		return runnerEnvironmentStatus(ctx, deps, c, cmd)
	case "apply":
		return applyRunnerEnvironment(ctx, deps, c, cmd)
	case "cancel":
		return cancelRunnerEnvironment(ctx, deps, c, cmd)
	default:
		return ExitUsage
	}
}

func captureRunnerEnvironmentCandidate(deps Dependencies, cmd command) int {
	paths, err := runnerEnvironmentPaths(deps)
	if err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	snapshot, present, err := captureRunnerEnvironment(deps.Lookup)
	if err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	if !present || strings.TrimSpace(snapshot.Version) == "" {
		writeError(deps.Stderr, errors.New("no usable allowlisted Runner environment was captured"))
		return ExitOperation
	}
	activeContent := ""
	if value, readErr := deps.ReadFile(paths["active"]); readErr == nil {
		activeContent = value
	} else if !errors.Is(readErr, os.ErrNotExist) {
		writeError(deps.Stderr, fmt.Errorf("active Runner environment snapshot could not be read: %w", readErr))
		return ExitOperation
	}
	diff := diffRunnerEnvironmentSnapshots(activeContent, snapshot.Content)
	if err := deps.MkdirAll(filepath.Dir(paths["candidate"]), 0o700); err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	if err := deps.WriteFileAtomic(paths["candidate"], []byte(snapshot.Content), 0o600); err != nil {
		writeError(deps.Stderr, fmt.Errorf("Runner environment candidate could not be written: %w", err))
		return ExitOperation
	}
	metadata := runnerEnvironmentCandidateMetadata{
		Version: snapshot.Version, Variables: append([]string(nil), snapshot.Variables...), CapturedAt: deps.Now().UTC().Format(time.RFC3339Nano),
	}
	digest := sha256.Sum256([]byte(snapshot.Content))
	metadata.ContentHash = hex.EncodeToString(digest[:])
	if err := writeRunnerEnvironmentJSON(deps, paths["candidateMeta"], metadata); err != nil {
		writeError(deps.Stderr, fmt.Errorf("Runner environment candidate metadata could not be written: %w", err))
		return ExitOperation
	}
	if len(cmd.fields) > 0 {
		return writeRunnerEnvironmentFields(deps, map[string]any{
			"candidateVersion":   snapshot.Version,
			"candidateVariables": snapshot.Variables,
			"addedVariables":     diff.Added,
			"removedVariables":   diff.Removed,
			"changedVariables":   diff.Changed,
			"status":             "candidate",
		}, cmd.fields)
	}
	fmt.Fprintf(deps.Stdout, "Captured Runner environment candidate %s (%d variables).\n", snapshot.Version, len(snapshot.Variables))
	fmt.Fprintf(deps.Stdout, "Added: %s\nRemoved: %s\nChanged: %s\n", formatRunnerEnvironmentNames(diff.Added), formatRunnerEnvironmentNames(diff.Removed), formatRunnerEnvironmentNames(diff.Changed))
	return ExitOK
}

func diffRunnerEnvironmentSnapshots(active, candidate string) runnerEnvironmentDiff {
	activeValues := runnerEnvironmentAssignments(active)
	candidateValues := runnerEnvironmentAssignments(candidate)
	diff := runnerEnvironmentDiff{Added: []string{}, Removed: []string{}, Changed: []string{}}
	for name, value := range candidateValues {
		previous, exists := activeValues[name]
		if !exists {
			diff.Added = append(diff.Added, name)
		} else if previous != value {
			diff.Changed = append(diff.Changed, name)
		}
	}
	for name := range activeValues {
		if _, exists := candidateValues[name]; !exists {
			diff.Removed = append(diff.Removed, name)
		}
	}
	sort.Strings(diff.Added)
	sort.Strings(diff.Removed)
	sort.Strings(diff.Changed)
	return diff
}

func runnerEnvironmentAssignments(content string) map[string]string {
	assignments := map[string]string{}
	for _, line := range strings.Split(strings.TrimSuffix(content, "\n"), "\n") {
		index := strings.IndexByte(line, '=')
		if index <= 0 {
			continue
		}
		assignments[line[:index]] = line[index+1:]
	}
	return assignments
}

func formatRunnerEnvironmentNames(names []string) string {
	if len(names) == 0 {
		return "none"
	}
	return strings.Join(names, ", ")
}

func runnerEnvironmentStatus(ctx context.Context, deps Dependencies, c *client, cmd command) int {
	paths, err := runnerEnvironmentPaths(deps)
	if err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	runnerID := resolveRunnerEnvironmentRunnerID(deps, cmd)
	identity, err := observeRunnerEnvironmentIdentity(ctx, c, runnerID)
	if err != nil {
		writeError(deps.Stderr, err)
		return operationCode(err)
	}
	candidate, candidateContent, candidateErr := readRunnerEnvironmentCandidate(deps, paths)
	if candidateErr != nil && !errors.Is(candidateErr, errRunnerEnvironmentCandidateAbsent) {
		writeError(deps.Stderr, candidateErr)
		return ExitOperation
	}
	applicationMeta, hasApplication, applicationErr := readRunnerEnvironmentApplication(deps, paths)
	if applicationErr != nil && !errors.Is(applicationErr, errRunnerEnvironmentApplicationAbsent) {
		writeError(deps.Stderr, applicationErr)
		return ExitOperation
	}
	result := map[string]any{
		"runnerId":             runnerID,
		"activeVersion":        identity.EnvironmentVersion,
		"candidateVersion":     nil,
		"candidateVariables":   []string{},
		"addedVariables":       []string{},
		"removedVariables":     []string{},
		"changedVariables":     []string{},
		"application":          nil,
		"processGeneration":    identity.ProcessGeneration,
		"connectionGeneration": identity.ConnectionGeneration,
		"status":               identity.Status,
	}
	if candidateErr == nil {
		result["candidateVersion"] = candidate.Version
		result["candidateVariables"] = candidate.Variables
		activeContent := ""
		if value, readErr := deps.ReadFile(paths["active"]); readErr == nil {
			activeContent = value
		} else if !errors.Is(readErr, os.ErrNotExist) {
			writeError(deps.Stderr, fmt.Errorf("active Runner environment snapshot could not be read: %w", readErr))
			return ExitOperation
		}
		diff := diffRunnerEnvironmentSnapshots(activeContent, candidateContent)
		result["addedVariables"] = diff.Added
		result["removedVariables"] = diff.Removed
		result["changedVariables"] = diff.Changed
	}
	if hasApplication {
		data, requestErr := c.get(ctx, environmentApplicationPath(runnerID, applicationMeta.UpdateID))
		if requestErr != nil {
			writeError(deps.Stderr, requestErr)
			return operationCode(requestErr)
		}
		response, decodeErr := decodeRunnerEnvironmentApplication(data, runnerID, applicationMeta.UpdateID, true)
		if decodeErr != nil {
			writeError(deps.Stderr, decodeErr)
			return ExitOperation
		}
		result["application"] = response
	}
	if len(cmd.fields) > 0 {
		return writeRunnerEnvironmentFields(deps, result, cmd.fields)
	}
	fmt.Fprintf(deps.Stdout, "Runner %s environment: active=%s candidate=%s status=%s\n", runnerID, displayString(identity.EnvironmentVersion), displayAny(result["candidateVersion"]), identity.Status)
	if app, ok := result["application"].(runnerEnvironmentApplicationResponse); ok && app.Application != nil {
		fmt.Fprintf(deps.Stdout, "Application %s: %s (%s)\n", app.UpdateID, app.Status, app.Application.Phase)
	}
	return ExitOK
}

func applyRunnerEnvironment(ctx context.Context, deps Dependencies, c *client, cmd command) int {
	paths, err := runnerEnvironmentPaths(deps)
	if err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	runnerID := resolveRunnerEnvironmentRunnerID(deps, cmd)
	targetVersion := strings.TrimSpace(argValue(cmd.args, "version", ""))
	candidate, candidateContent, err := readRunnerEnvironmentCandidate(deps, paths)
	if err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	if candidate.Version != targetVersion {
		writeError(deps.Stderr, fmt.Errorf("candidate version %s does not match --version %s", candidate.Version, targetVersion))
		return ExitUsage
	}
	activeContent, err := deps.ReadFile(paths["active"])
	if err != nil {
		writeError(deps.Stderr, errors.New("active Runner environment snapshot is unavailable; refusing to create a non-recoverable application"))
		return ExitOperation
	}
	identity, err := observeRunnerEnvironmentIdentity(ctx, c, runnerID)
	if err != nil {
		writeError(deps.Stderr, err)
		return operationCode(err)
	}
	if identity.Status != "online" || identity.ConnectionState != "connected" || identity.ProcessGeneration == "" || identity.ConnectionGeneration == "" {
		writeError(deps.Stderr, errors.New("Runner must be online with a connected process identity before apply"))
		return ExitOperation
	}
	applicationMeta, applicationState, err := readRunnerEnvironmentApplication(deps, paths)
	if err != nil {
		if !errors.Is(err, errRunnerEnvironmentApplicationAbsent) {
			writeError(deps.Stderr, err)
			return ExitOperation
		}
	}
	hasApplication := applicationState
	if hasApplication && applicationMeta.TargetVersion != targetVersion {
		writeError(deps.Stderr, errors.New("another Runner environment application is already recorded locally"))
		return ExitOperation
	}
	var existingApplication *runnerEnvironmentApplicationResponse
	if hasApplication {
		data, requestErr := c.get(ctx, environmentApplicationPath(runnerID, applicationMeta.UpdateID))
		if requestErr != nil {
			if operationErrorCode(requestErr) == "not_found" || operationErrorCode(requestErr) == "environment_application_not_found" {
				_ = deps.RemoveAll(paths["application"])
				hasApplication = false
			} else {
				writeError(deps.Stderr, requestErr)
				return operationCode(requestErr)
			}
		} else {
			var response runnerEnvironmentApplicationResponse
			if json.Unmarshal(data, &response) != nil || response.Application == nil {
				writeError(deps.Stderr, errors.New("Runner environment application response was invalid"))
				return ExitOperation
			}
			existingApplication = &response
			if response.Application.TargetVersion != targetVersion {
				writeError(deps.Stderr, errors.New("Server application target does not match the local candidate"))
				return ExitOperation
			}
		}
	}
	if !hasApplication {
		applicationMeta = runnerEnvironmentApplicationMetadata{
			UpdateID: newIDFromDependencies(deps), TargetVersion: targetVersion,
			BaseProcessGeneration: identity.ProcessGeneration, BaseConnectionGeneration: identity.ConnectionGeneration,
			StartedAt: deps.Now().UTC().Format(time.RFC3339Nano),
		}
		if err := writeRunnerEnvironmentJSON(deps, paths["application"], applicationMeta); err != nil {
			writeError(deps.Stderr, fmt.Errorf("Runner environment application metadata could not be written: %w", err))
			return ExitOperation
		}
	}
	if existingApplication == nil {
		begin, beginErr := beginRunnerEnvironmentApplication(ctx, c, runnerID, applicationMeta)
		if beginErr != nil {
			writeError(deps.Stderr, beginErr)
			return operationCode(beginErr)
		}
		if begin.Status != "waiting" && begin.Status != "already-pending" && begin.Status != "already-completed" {
			writeError(deps.Stderr, fmt.Errorf("Runner environment application was not accepted: %s", begin.Status))
			return ExitOperation
		}
		existingApplication = &begin
	}
	if existingApplication != nil && existingApplication.Application != nil {
		if handled, code := resumeRunnerEnvironmentApplication(
			ctx, deps, c, runnerID, applicationMeta, paths, activeContent, candidateContent, targetVersion, identity, existingApplication); handled {
			return code
		}
	}
	applyingIdentity, err := waitForRunnerEnvironmentApply(ctx, deps, c, runnerID, applicationMeta.UpdateID)
	if err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	if err := rotateRunnerEnvironmentFiles(deps, paths, activeContent, candidateContent); err != nil {
		return failRunnerEnvironmentApplication(ctx, deps, c, runnerID, applicationMeta, paths, activeContent, applyingIdentity, fmt.Errorf("local snapshot activation failed: %w", err))
	}
	if err := deps.Execute(ctx, "systemctl", []string{"--user", "restart", "mohist-runner.service"}); err != nil {
		return rollbackRunnerEnvironment(ctx, deps, c, runnerID, applicationMeta, paths, activeContent, applyingIdentity, fmt.Errorf("Runner service restart failed: %w", err))
	}
	activated, err := waitForRunnerEnvironmentActivation(ctx, deps, c, runnerID, applicationMeta.BaseProcessGeneration, targetVersion)
	if err != nil {
		return rollbackRunnerEnvironment(ctx, deps, c, runnerID, applicationMeta, paths, activeContent, applyingIdentity, err)
	}
	if _, err := confirmRunnerEnvironmentApplication(ctx, c, runnerID, applicationMeta.UpdateID, activated.ProcessGeneration, targetVersion); err != nil {
		return rollbackRunnerEnvironment(ctx, deps, c, runnerID, applicationMeta, paths, activeContent, activated, fmt.Errorf("Server activation confirmation failed: %w", err))
	}
	return cleanupConfirmedRunnerEnvironment(deps, paths, targetVersion, activated.ProcessGeneration)
}

func cleanupConfirmedRunnerEnvironment(deps Dependencies, paths map[string]string, version, processGeneration string) int {
	if err := deps.RemoveAll(paths["candidate"]); err != nil {
		writeError(deps.Stderr, fmt.Errorf("application confirmed but candidate cleanup failed: %w", err))
		return ExitOperation
	}
	if err := deps.RemoveAll(paths["candidateMeta"]); err != nil {
		writeError(deps.Stderr, fmt.Errorf("application confirmed but candidate metadata cleanup failed: %w", err))
		return ExitOperation
	}
	if err := deps.RemoveAll(paths["application"]); err != nil {
		writeError(deps.Stderr, fmt.Errorf("application confirmed but local transaction cleanup failed: %w", err))
		return ExitOperation
	}
	fmt.Fprintf(deps.Stdout, "Applied Runner environment %s in process generation %s.\n", version, processGeneration)
	return ExitOK
}

// resumeRunnerEnvironmentApplication handles the part of an application that
// may have completed while the local CLI was not running. In particular, a
// new process that already reports the target version is activation evidence;
// restarting it again would create a second generation and lose the witness.
func resumeRunnerEnvironmentApplication(
	ctx context.Context,
	deps Dependencies,
	c *client,
	runnerID string,
	metadata runnerEnvironmentApplicationMetadata,
	paths map[string]string,
	activeContent string,
	candidateContent string,
	targetVersion string,
	identity runnerEnvironmentIdentity,
	response *runnerEnvironmentApplicationResponse,
) (bool, int) {
	application := response.Application
	if application == nil {
		return false, ExitOK
	}
	switch application.Phase {
	case "active":
		if identity.Status == "online" && identity.ConnectionState == "connected" &&
			identity.EnvironmentVersion == targetVersion &&
			identity.ProcessGeneration != "" && identity.ProcessGeneration != metadata.BaseProcessGeneration {
			confirmed, err := confirmRunnerEnvironmentApplication(ctx, c, runnerID, metadata.UpdateID, identity.ProcessGeneration, targetVersion)
			if err != nil {
				writeError(deps.Stderr, err)
				return true, operationCode(err)
			}
			if confirmed.Status != "accepted" && confirmed.Status != "already-applied" {
				writeError(deps.Stderr, fmt.Errorf("Server activation confirmation was not accepted: %s", confirmed.Status))
				return true, ExitOperation
			}
			return true, cleanupConfirmedRunnerEnvironment(deps, paths, targetVersion, identity.ProcessGeneration)
		}
		writeError(deps.Stderr, errors.New("Server reports an active environment application without its activation witness"))
		return true, ExitOperation
	case "applying":
		if identity.ProcessGeneration == "" || identity.ProcessGeneration == metadata.BaseProcessGeneration {
			return false, ExitOK
		}
		if identity.Status != "online" || identity.ConnectionState != "connected" {
			writeError(deps.Stderr, errors.New("Runner changed process generation before environment activation was witnessed; wait for a connected Runner before recovery"))
			return true, ExitOperation
		}
		if identity.EnvironmentVersion == targetVersion {
			confirmed, err := confirmRunnerEnvironmentApplication(ctx, c, runnerID, metadata.UpdateID, identity.ProcessGeneration, targetVersion)
			if err != nil {
				writeError(deps.Stderr, err)
				return true, operationCode(err)
			}
			if confirmed.Status != "accepted" && confirmed.Status != "already-applied" {
				writeError(deps.Stderr, fmt.Errorf("Server activation confirmation was not accepted: %s", confirmed.Status))
				return true, ExitOperation
			}
			return true, cleanupConfirmedRunnerEnvironment(deps, paths, targetVersion, identity.ProcessGeneration)
		}
		rollbackContent, contentErr := runnerEnvironmentRollbackContent(deps, paths, activeContent, candidateContent)
		if contentErr != nil {
			writeError(deps.Stderr, contentErr)
			return true, ExitOperation
		}
		return true, rollbackRunnerEnvironment(
			ctx,
			deps,
			c,
			runnerID,
			metadata,
			paths,
			rollbackContent,
			identity,
			fmt.Errorf("Runner process generation %s loaded environment %s instead of target %s", identity.ProcessGeneration, displayString(identity.EnvironmentVersion), targetVersion),
		)
	case "failed", "unconfirmed":
		writeError(deps.Stderr, fmt.Errorf("local Runner environment application requires recovery from phase %s", application.Phase))
		return true, ExitOperation
	case "cancelled":
		writeError(deps.Stderr, errors.New("local Runner environment application was cancelled; capture a new candidate before applying"))
		return true, ExitOperation
	default:
		return false, ExitOK
	}
}

func runnerEnvironmentRollbackContent(deps Dependencies, paths map[string]string, activeContent, candidateContent string) (string, error) {
	if activeContent != candidateContent {
		return activeContent, nil
	}
	previous, err := deps.ReadFile(paths["previous"])
	if err != nil {
		return "", errors.New("previous Runner environment snapshot is unavailable; refusing to roll back an interrupted application")
	}
	return previous, nil
}

func cancelRunnerEnvironment(ctx context.Context, deps Dependencies, c *client, cmd command) int {
	paths, err := runnerEnvironmentPaths(deps)
	if err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	runnerID := resolveRunnerEnvironmentRunnerID(deps, cmd)
	updateID := strings.TrimSpace(argValue(cmd.args, "update-id", ""))
	if metadata, present, readErr := readRunnerEnvironmentApplication(deps, paths); readErr != nil && !errors.Is(readErr, errRunnerEnvironmentApplicationAbsent) {
		writeError(deps.Stderr, readErr)
		return ExitOperation
	} else if present && metadata.UpdateID != updateID {
		writeError(deps.Stderr, errors.New("--update-id does not match the local Runner environment application"))
		return ExitUsage
	}
	data, err := c.request(ctx, http.MethodPost, environmentApplicationPath(runnerID, updateID)+"/cancel", map[string]any{})
	if err != nil {
		writeError(deps.Stderr, err)
		return operationCode(err)
	}
	response, decodeErr := decodeRunnerEnvironmentApplication(data, runnerID, updateID, true)
	if decodeErr != nil || (response.Status != "cancelled" && response.Status != "already-cancelled") {
		writeError(deps.Stderr, errors.New("Runner environment cancellation response was invalid"))
		return ExitOperation
	}
	if err := deps.RemoveAll(paths["application"]); err != nil {
		writeError(deps.Stderr, fmt.Errorf("Runner environment was cancelled but local transaction cleanup failed: %w", err))
		return ExitOperation
	}
	fmt.Fprintf(deps.Stdout, "Cancelled Runner environment application %s.\n", updateID)
	return ExitOK
}

func observeRunnerEnvironmentIdentity(ctx context.Context, c *client, runnerID string) (runnerEnvironmentIdentity, error) {
	data, err := c.get(ctx, "/api/runner/identity?runnerId="+urlQueryEscape(runnerID))
	if err != nil {
		return runnerEnvironmentIdentity{}, err
	}
	var identity runnerEnvironmentIdentity
	if json.Unmarshal(data, &identity) != nil || identity.RunnerID != runnerID {
		return runnerEnvironmentIdentity{}, errors.New("Runner identity response was invalid")
	}
	return identity, nil
}

func decodeRunnerEnvironmentApplication(data []byte, runnerID, updateID string, requireSnapshot bool) (runnerEnvironmentApplicationResponse, error) {
	var response runnerEnvironmentApplicationResponse
	if json.Unmarshal(data, &response) != nil || response.RunnerID != runnerID || response.UpdateID != updateID {
		return runnerEnvironmentApplicationResponse{}, errors.New("Runner environment application response was invalid")
	}
	if requireSnapshot && response.Application == nil {
		return runnerEnvironmentApplicationResponse{}, errors.New("Runner environment application response was incomplete")
	}
	return response, nil
}

func beginRunnerEnvironmentApplication(ctx context.Context, c *client, runnerID string, metadata runnerEnvironmentApplicationMetadata) (runnerEnvironmentApplicationResponse, error) {
	data, err := c.request(ctx, http.MethodPost, environmentApplicationPath(runnerID, ""), map[string]string{
		"updateId": metadata.UpdateID, "targetVersion": metadata.TargetVersion,
		"processGeneration": metadata.BaseProcessGeneration, "connectionGeneration": metadata.BaseConnectionGeneration,
	})
	if err != nil {
		return runnerEnvironmentApplicationResponse{}, err
	}
	return decodeRunnerEnvironmentApplication(data, runnerID, metadata.UpdateID, true)
}

func waitForRunnerEnvironmentApply(ctx context.Context, deps Dependencies, c *client, runnerID, updateID string) (runnerEnvironmentIdentity, error) {
	var lastError error
	for attempt := 0; attempt < runnerEnvironmentMaxPolls; attempt++ {
		identity, err := observeRunnerEnvironmentIdentity(ctx, c, runnerID)
		if err != nil {
			lastError = err
		} else if identity.Status == "online" && identity.ConnectionState == "connected" && identity.ProcessGeneration != "" {
			data, requestErr := c.request(ctx, http.MethodPost, environmentApplicationPath(runnerID, updateID)+"/apply", map[string]string{"processGeneration": identity.ProcessGeneration})
			if requestErr == nil {
				response, decodeErr := decodeRunnerEnvironmentApplication(data, runnerID, updateID, true)
				if decodeErr != nil {
					return runnerEnvironmentIdentity{}, decodeErr
				}
				if response.Status != "accepted" && response.Status != "already-applied" {
					return runnerEnvironmentIdentity{}, fmt.Errorf("Runner environment apply was not accepted: %s", response.Status)
				}
				return identity, nil
			} else if operationErrorCode(requestErr) != "environment_application_not_settled" && operationErrorCode(requestErr) != "environment_application_stale" {
				return runnerEnvironmentIdentity{}, requestErr
			} else {
				lastError = requestErr
			}
		} else {
			lastError = errors.New("Runner identity is offline or disconnected while waiting for work to settle")
		}
		if attempt+1 < runnerEnvironmentMaxPolls {
			if err := deps.Wait(ctx, runnerEnvironmentPollInterval); err != nil {
				return runnerEnvironmentIdentity{}, err
			}
		}
	}
	if lastError == nil {
		lastError = errors.New("Runner environment application did not settle")
	}
	return runnerEnvironmentIdentity{}, fmt.Errorf("Runner environment application did not settle within the bounded wait: %w", lastError)
}

func rotateRunnerEnvironmentFiles(deps Dependencies, paths map[string]string, active, candidate string) error {
	if err := deps.WriteFileAtomic(paths["previous"], []byte(active), 0o600); err != nil {
		return err
	}
	if err := deps.WriteFileAtomic(paths["active"], []byte(candidate), 0o600); err != nil {
		_ = deps.WriteFileAtomic(paths["active"], []byte(active), 0o600)
		return err
	}
	return nil
}

func waitForRunnerEnvironmentActivation(ctx context.Context, deps Dependencies, c *client, runnerID, baseProcessGeneration, targetVersion string) (runnerEnvironmentIdentity, error) {
	var lastError error
	for attempt := 0; attempt < runnerEnvironmentMaxPolls; attempt++ {
		identity, err := observeRunnerEnvironmentIdentity(ctx, c, runnerID)
		if err == nil && identity.Status == "online" && identity.ConnectionState == "connected" && identity.ProcessGeneration != "" && identity.ProcessGeneration != baseProcessGeneration && identity.EnvironmentVersion == targetVersion {
			return identity, nil
		}
		if err != nil {
			lastError = err
		} else {
			lastError = errors.New("new Runner process has not reported the target environment version")
		}
		if attempt+1 < runnerEnvironmentMaxPolls {
			if err := deps.Wait(ctx, runnerEnvironmentPollInterval); err != nil {
				return runnerEnvironmentIdentity{}, err
			}
		}
	}
	return runnerEnvironmentIdentity{}, fmt.Errorf("Runner environment activation was not confirmed within the bounded wait: %w", lastError)
}

func confirmRunnerEnvironmentApplication(ctx context.Context, c *client, runnerID, updateID, processGeneration, version string) (runnerEnvironmentApplicationResponse, error) {
	data, err := c.request(ctx, http.MethodPost, environmentApplicationPath(runnerID, updateID)+"/confirm", map[string]string{"processGeneration": processGeneration, "environmentVersion": version})
	if err != nil {
		return runnerEnvironmentApplicationResponse{}, err
	}
	return decodeRunnerEnvironmentApplication(data, runnerID, updateID, true)
}

func failRunnerEnvironmentApplication(ctx context.Context, deps Dependencies, c *client, runnerID string, metadata runnerEnvironmentApplicationMetadata, paths map[string]string, activeContent string, _ runnerEnvironmentIdentity, cause error) int {
	_, failErr := c.request(ctx, http.MethodPost, environmentApplicationPath(runnerID, metadata.UpdateID)+"/fail", map[string]string{"failureCode": "local-activation-failed"})
	if failErr != nil {
		writeError(deps.Stderr, fmt.Errorf("%w; Server failure transition failed: %v", cause, failErr))
		return ExitOperation
	}
	if restoreErr := deps.WriteFileAtomic(paths["active"], []byte(activeContent), 0o600); restoreErr != nil {
		_, _ = c.request(ctx, http.MethodPost, environmentApplicationPath(runnerID, metadata.UpdateID)+"/unconfirmed", map[string]string{"failureCode": "local-restore-failed"})
		writeError(deps.Stderr, fmt.Errorf("%w; active snapshot restore failed: %v", cause, restoreErr))
		return ExitOperation
	}
	if rollbackErr := confirmRunnerEnvironmentRollback(ctx, c, runnerID, metadata.UpdateID, metadata.BaseProcessGeneration, ""); rollbackErr == nil {
		if removeErr := deps.RemoveAll(paths["application"]); removeErr != nil {
			writeError(deps.Stderr, fmt.Errorf("%w; rollback confirmed but local transaction cleanup failed: %v", cause, removeErr))
			return ExitOperation
		}
		writeError(deps.Stderr, cause)
		return ExitOperation
	}
	_, _ = c.request(ctx, http.MethodPost, environmentApplicationPath(runnerID, metadata.UpdateID)+"/unconfirmed", map[string]string{"failureCode": "local-rollback-unconfirmed"})
	writeError(deps.Stderr, cause)
	return ExitOperation
}

func rollbackRunnerEnvironment(ctx context.Context, deps Dependencies, c *client, runnerID string, metadata runnerEnvironmentApplicationMetadata, paths map[string]string, activeContent string, failedIdentity runnerEnvironmentIdentity, cause error) int {
	_, failErr := c.request(ctx, http.MethodPost, environmentApplicationPath(runnerID, metadata.UpdateID)+"/fail", map[string]string{"failureCode": "local-activation-failed"})
	if failErr != nil {
		writeError(deps.Stderr, fmt.Errorf("%w; Server failure transition failed: %v", cause, failErr))
		return ExitOperation
	}
	if err := deps.WriteFileAtomic(paths["active"], []byte(activeContent), 0o600); err != nil {
		_, _ = c.request(ctx, http.MethodPost, environmentApplicationPath(runnerID, metadata.UpdateID)+"/unconfirmed", map[string]string{"failureCode": "rollback-write-failed"})
		writeError(deps.Stderr, fmt.Errorf("%w; rollback snapshot could not be restored: %v", cause, err))
		return ExitOperation
	}
	if err := deps.Execute(ctx, "systemctl", []string{"--user", "restart", "mohist-runner.service"}); err != nil {
		_, _ = c.request(ctx, http.MethodPost, environmentApplicationPath(runnerID, metadata.UpdateID)+"/unconfirmed", map[string]string{"failureCode": "rollback-restart-failed"})
		writeError(deps.Stderr, fmt.Errorf("%w; rollback restart failed: %v", cause, err))
		return ExitOperation
	}
	previousVersion := runnerEnvironmentPreviousVersion(ctx, c, runnerID, metadata.UpdateID)
	if previousVersion == "" {
		_, _ = c.request(ctx, http.MethodPost, environmentApplicationPath(runnerID, metadata.UpdateID)+"/unconfirmed", map[string]string{"failureCode": "rollback-version-unknown"})
		writeError(deps.Stderr, fmt.Errorf("%w; previous environment version is unknown", cause))
		return ExitOperation
	}
	rollback, rollbackErr := waitForRunnerEnvironmentActivation(ctx, deps, c, runnerID, failedIdentity.ProcessGeneration, previousVersion)
	if rollbackErr == nil {
		if confirmErr := confirmRunnerEnvironmentRollback(ctx, c, runnerID, metadata.UpdateID, rollback.ProcessGeneration, previousVersion); confirmErr == nil {
			_ = deps.RemoveAll(paths["application"])
			writeError(deps.Stderr, cause)
			return ExitOperation
		}
	}
	_, _ = c.request(ctx, http.MethodPost, environmentApplicationPath(runnerID, metadata.UpdateID)+"/unconfirmed", map[string]string{"failureCode": "rollback-unconfirmed"})
	writeError(deps.Stderr, fmt.Errorf("%w; rollback could not be confirmed; application remains fenced", cause))
	return ExitOperation
}

func runnerEnvironmentPreviousVersion(ctx context.Context, c *client, runnerID, updateID string) string {
	data, err := c.get(ctx, environmentApplicationPath(runnerID, updateID))
	if err != nil {
		return ""
	}
	response, err := decodeRunnerEnvironmentApplication(data, runnerID, updateID, true)
	if err != nil || response.Application == nil {
		return ""
	}
	return response.Application.PreviousVersion
}

func confirmRunnerEnvironmentRollback(ctx context.Context, c *client, runnerID, updateID, processGeneration, version string) error {
	if strings.TrimSpace(version) == "" {
		version = runnerEnvironmentPreviousVersion(ctx, c, runnerID, updateID)
	}
	if strings.TrimSpace(version) == "" || strings.TrimSpace(processGeneration) == "" {
		return errors.New("Runner environment rollback witness is unavailable")
	}
	data, err := c.request(ctx, http.MethodPost, environmentApplicationPath(runnerID, updateID)+"/rollback-confirm", map[string]string{"processGeneration": processGeneration, "environmentVersion": version})
	if err != nil {
		return err
	}
	response, err := decodeRunnerEnvironmentApplication(data, runnerID, updateID, true)
	if err != nil {
		return err
	}
	if response.Status != "accepted" && response.Status != "already-rolled-back" {
		return fmt.Errorf("Runner environment rollback confirmation was not accepted: %s", response.Status)
	}
	return nil
}

func writeRunnerEnvironmentFields(deps Dependencies, value map[string]any, fields []string) int {
	data, err := json.Marshal(value)
	if err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	selected, err := SelectFields(data, fields, false)
	if err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	return writeJSON(deps.Stdout, json.RawMessage(selected))
}

func environmentApplicationPath(runnerID, updateID string) string {
	path := "/api/runner/" + urlPathEscape(runnerID) + "/environment/application"
	if updateID != "" {
		path += "/" + urlPathEscape(updateID)
	}
	return path
}

func urlPathEscape(value string) string {
	return url.PathEscape(value)
}

func urlQueryEscape(value string) string {
	return url.QueryEscape(value)
}

func operationCode(err error) int {
	if errors.Is(err, context.Canceled) || errors.Is(err, context.DeadlineExceeded) {
		return ExitCanceled
	}
	return ExitOperation
}

func operationErrorCode(err error) string {
	var operation *operationError
	if errors.As(err, &operation) {
		return operation.code
	}
	return ""
}

func newIDFromDependencies(deps Dependencies) string {
	if deps.NewID != nil {
		return deps.NewID()
	}
	return newRunnerEnvironmentID()
}

func displayString(value string) string {
	if value == "" {
		return "unknown"
	}
	return value
}

func displayAny(value any) string {
	if value == nil {
		return "none"
	}
	if text, ok := value.(string); ok && text == "" {
		return "none"
	}
	return fmt.Sprint(value)
}
