package mohistcli

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"reflect"
	"sort"
	"strings"
	"time"
)

type managedUpdater struct {
	env managedUpdateEnvironment
}

func runManagedRuntimeUpdate(ctx context.Context, deps Dependencies, request ManagedUpdateRequest) int {
	updater := deps.ManagedUpdate
	if updater == nil {
		control, err := newRealManagedControlPlane(deps)
		if err != nil && !request.DryRun {
			writeError(deps.Stderr, err)
			return ExitOperation
		}
		updater = &managedUpdater{env: managedUpdateEnvironment{
			files: realManagedFiles{}, commands: realManagedCommands{}, control: control,
			now: deps.Now, wait: deps.Wait, newID: newManagedUpdateID,
			homeDir: deps.HomeDir, stdout: deps.Stdout, stderr: deps.Stderr,
		}}
	}
	if err := updater.Update(ctx, request); err != nil {
		if errors.Is(err, context.Canceled) || errors.Is(err, context.DeadlineExceeded) {
			writeError(deps.Stderr, err)
			return ExitCanceled
		}
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	return ExitOK
}

func (updater *managedUpdater) Update(ctx context.Context, request ManagedUpdateRequest) error {
	components, err := normalizeManagedComponents(request.Components)
	if err != nil {
		return err
	}
	env := updater.env
	if env.files == nil || env.commands == nil || env.homeDir == nil || env.now == nil || env.wait == nil || env.newID == nil {
		return errors.New("managed update dependencies are incomplete")
	}
	home, err := env.homeDir()
	if err != nil || strings.TrimSpace(home) == "" {
		return errors.New("managed update home directory is unavailable")
	}
	runtimeRoot := filepath.Join(home, ".local", "share", "mohist", "runtime")
	unitDir, err := resolveManagedUnitDirectory(home, request.UnitDir)
	if err != nil {
		return err
	}

	repositoryRoot, err := resolveManagedRepositoryRoot(ctx, env, request.RepoRoot)
	if err != nil {
		return err
	}
	identity, err := readManagedGitIdentity(ctx, env, repositoryRoot)
	if err != nil {
		return err
	}
	if request.DryRun {
		fmt.Fprintf(env.stdout, "Dry run: source %s at %s would update %s.\n", identity.commit, repositoryRoot, strings.Join(components, ", "))
		return nil
	}
	if env.control == nil {
		return errors.New("managed update control-plane client is unavailable")
	}
	lock, err := env.files.OpenLock(filepath.Join(runtimeRoot, "update.lock"))
	if err != nil {
		return err
	}
	defer lock.Close()
	if env.files.Exists(filepath.Join(runtimeRoot, "pending.json")) {
		if err := updater.recoverPendingManagedUpdate(ctx, runtimeRoot); err != nil {
			return fmt.Errorf("managed update is blocked by an unresolved transaction: %w", err)
		}
	}

	active, activeBytes, activeMode, err := readManagedPointer(env.files, filepath.Join(runtimeRoot, "active.json"))
	if err != nil {
		return fmt.Errorf("managed active runtime is unavailable: %w", err)
	}
	if pointerText(active, "status") != "verified" {
		return errors.New("managed active runtime is not verified")
	}
	verified, verifiedBytes, verifiedMode, err := readManagedPointer(env.files, filepath.Join(runtimeRoot, "verified.json"))
	if err != nil || pointerText(verified, "status") != "verified" {
		return errors.New("managed verified runtime is unavailable")
	}
	for _, component := range components {
		activeTarget, activeErr := pointerTarget(active, component)
		verifiedTarget, verifiedErr := pointerTarget(verified, component)
		if activeErr != nil || verifiedErr != nil || !reflect.DeepEqual(activeTarget, verifiedTarget) {
			return fmt.Errorf("managed %s active and verified targets do not agree", component)
		}
	}

	transactionID := env.newID()
	transactionRoot := filepath.Join(runtimeRoot, "transactions", transactionID)
	source, err := captureManagedSource(ctx, env, repositoryRoot, runtimeRoot, transactionID)
	if err != nil {
		return err
	}
	if source.Commit != identity.commit || source.TreeHash != identity.treeHash {
		return errors.New("source identity changed during capture")
	}
	if err := env.files.MkdirAll(filepath.Join(transactionRoot, "snapshots"), 0o700); err != nil {
		return err
	}
	if err := env.files.WriteFileAtomic(filepath.Join(transactionRoot, "snapshots", "active.json"), activeBytes, 0o600); err != nil {
		return errors.New("active runtime snapshot could not be persisted")
	}
	if err := env.files.WriteFileAtomic(filepath.Join(transactionRoot, "snapshots", "verified.json"), verifiedBytes, 0o600); err != nil {
		return errors.New("verified runtime snapshot could not be persisted")
	}

	generation := managedNextGeneration(active)
	services := map[string]*managedServiceSnapshot{}
	previousObservations := map[string]managedRuntimeObservation{}
	for _, component := range components {
		previousTarget, err := pointerTarget(active, component)
		if err != nil {
			return fmt.Errorf("managed %s target is unavailable: %w", component, err)
		}
		snapshot, err := captureManagedService(
			ctx, env, component, unitDir,
			filepath.Join(transactionRoot, "snapshots", component+".service"), previousTarget,
		)
		if err != nil {
			return err
		}
		if !snapshot.WasActive {
			return fmt.Errorf("managed %s service must be active before update", component)
		}
		services[component] = snapshot
		observation, err := observeManagedRuntime(ctx, env.control, component, previousTarget.Identity.RunnerID)
		if err != nil {
			return fmt.Errorf("managed %s runtime could not be observed before update", component)
		}
		if differences := managedIdentityDifferences(observation.Identity, previousTarget.Identity); len(differences) > 0 {
			return fmt.Errorf("managed %s runtime does not match the verified target in %s", component, strings.Join(differences, ", "))
		}
		previousObservations[component] = observation
	}

	runnerID := ""
	if runner := services["runner"]; runner != nil && runner.PreviousTarget != nil {
		runnerID = runner.PreviousTarget.Identity.RunnerID
	}
	targets, err := stageManagedTargets(
		ctx, env, source, transactionRoot, runtimeRoot, generation, components, runnerID,
	)
	if err != nil {
		return err
	}
	for _, component := range components {
		targets[component].Arguments = append([]string(nil), services[component].PreviousTarget.Arguments...)
	}
	if err := verifyManagedSourceUnchanged(ctx, env, source); err != nil {
		return err
	}

	transaction := managedTransaction{
		ID: transactionID, Status: "prepared", Generation: generation, Source: source,
		UnitDirectory: unitDir, Targets: targets, Services: services, CreatedAt: env.now(), UpdatedAt: env.now(),
	}
	statePath := filepath.Join(transactionRoot, "state.json")
	if err := writeManagedTransaction(env.files, statePath, &transaction); err != nil {
		return err
	}
	pendingValue, _ := json.MarshalIndent(map[string]string{"transactionId": transactionID, "statePath": statePath}, "", "  ")
	if err := env.files.WriteFileAtomic(filepath.Join(runtimeRoot, "pending.json"), append(pendingValue, '\n'), 0o600); err != nil {
		return errors.New("managed update pending marker could not be persisted")
	}
	if err := promoteManagedTargets(env, transactionRoot, targets); err != nil {
		transaction.Status = "recovery-failed"
		transaction.Failure = err.Error()
		transaction.UpdatedAt = env.now()
		if stateErr := writeManagedTransaction(env.files, statePath, &transaction); stateErr != nil {
			return fmt.Errorf("%w; promotion failure state could not be persisted", err)
		}
		return fmt.Errorf("%w; managed update remains pending", err)
	}

	transaction.Status = "activating"
	transaction.UpdatedAt = env.now()
	if err := writeManagedTransaction(env.files, statePath, &transaction); err != nil {
		return updater.abortPrepared(ctx, runtimeRoot, statePath, &transaction, activeBytes, activeMode, verifiedBytes, verifiedMode, err)
	}
	activated := []string{}
	interrupt := managedRunnerInterrupt{}
	for _, component := range components {
		if component == "runner" {
			transaction.InterruptID = env.newID()
			transaction.UpdatedAt = env.now()
			if err := writeManagedTransaction(env.files, statePath, &transaction); err != nil {
				transaction.InterruptID = ""
				return updater.rollback(ctx, runtimeRoot, statePath, &transaction, activated, interrupt, activeBytes, activeMode, verifiedBytes, verifiedMode, previousObservations, err)
			}
			interrupt, err = env.control.BeginRunnerInterrupt(ctx, targets[component].Identity.RunnerID, transaction.InterruptID)
			if err != nil {
				return updater.rollback(ctx, runtimeRoot, statePath, &transaction, activated, interrupt, activeBytes, activeMode, verifiedBytes, verifiedMode, previousObservations, err)
			}
			if interrupt.ActiveWorkCount != 0 {
				return updater.rollback(
					ctx, runtimeRoot, statePath, &transaction, activated, interrupt,
					activeBytes, activeMode, verifiedBytes, verifiedMode, previousObservations,
					fmt.Errorf("Runner update refused while %d active work item(s) are draining", interrupt.ActiveWorkCount),
				)
			}
		}
		started, err := activateManagedService(ctx, env, services[component], targets[component])
		if started {
			activated = append(activated, component)
		}
		if err != nil {
			return updater.rollback(ctx, runtimeRoot, statePath, &transaction, activated, interrupt, activeBytes, activeMode, verifiedBytes, verifiedMode, previousObservations, err)
		}
		previousConnection := previousObservations[component].ConnectionGeneration
		if err := waitForManagedRuntime(ctx, env, component, targets[component].Identity, previousConnection); err != nil {
			return updater.rollback(ctx, runtimeRoot, statePath, &transaction, activated, interrupt, activeBytes, activeMode, verifiedBytes, verifiedMode, previousObservations, err)
		}
	}

	candidate := updateManagedPointer(active, transactionID, generation, targets)
	if err := writeManagedPointer(env.files, filepath.Join(runtimeRoot, "verified.json"), candidate, verifiedMode); err != nil {
		return updater.rollback(ctx, runtimeRoot, statePath, &transaction, activated, interrupt, activeBytes, activeMode, verifiedBytes, verifiedMode, previousObservations, err)
	}
	if err := writeManagedPointer(env.files, filepath.Join(runtimeRoot, "active.json"), candidate, activeMode); err != nil {
		return updater.rollback(ctx, runtimeRoot, statePath, &transaction, activated, interrupt, activeBytes, activeMode, verifiedBytes, verifiedMode, previousObservations, err)
	}
	transaction.Status = "verified"
	transaction.UpdatedAt = env.now()
	if err := writeManagedTransaction(env.files, statePath, &transaction); err != nil {
		return updater.rollback(ctx, runtimeRoot, statePath, &transaction, activated, interrupt, activeBytes, activeMode, verifiedBytes, verifiedMode, previousObservations, err)
	}
	if err := env.files.RemoveAll(filepath.Join(runtimeRoot, "pending.json")); err != nil {
		return errors.New("managed update committed but its pending marker could not be cleared")
	}
	fmt.Fprintf(env.stdout, "Managed %s update committed for source %s.\n", strings.Join(components, "+"), source.Commit)
	return nil
}

func resolveManagedUnitDirectory(home string, requested string) (string, error) {
	unitDirectory := strings.TrimSpace(requested)
	if unitDirectory == "" {
		unitDirectory = filepath.Join(home, ".config", "systemd", "user")
	}
	absolute, err := filepath.Abs(unitDirectory)
	if err != nil {
		return "", errors.New("managed service unit directory is invalid")
	}
	return filepath.Clean(absolute), nil
}

func normalizeManagedComponents(values []string) ([]string, error) {
	seen := map[string]bool{}
	components := []string{}
	for _, value := range values {
		if value != "server" && value != "runner" {
			return nil, fmt.Errorf("unsupported managed component %q", value)
		}
		if !seen[value] {
			seen[value] = true
			components = append(components, value)
		}
	}
	if len(components) == 0 {
		return nil, errors.New("managed update requires server or runner")
	}
	return components, nil
}

func observeManagedRuntime(ctx context.Context, control managedControlPlane, component, runnerID string) (managedRuntimeObservation, error) {
	if component == "server" {
		return control.ObserveServer(ctx)
	}
	return control.ObserveRunner(ctx, runnerID)
}

func waitForManagedRuntime(
	ctx context.Context,
	env managedUpdateEnvironment,
	component string,
	expected managedRuntimeIdentity,
	previousConnection string,
) error {
	deadline := env.now().Add(45 * time.Second)
	var last error
	for attempt := 0; attempt < 180 && env.now().Before(deadline); attempt++ {
		observation, err := observeManagedRuntime(ctx, env.control, component, expected.RunnerID)
		if err == nil {
			err = verifyManagedObservation(component, observation, expected, previousConnection)
		}
		if err == nil {
			return nil
		}
		last = err
		if err := env.wait(ctx, 250*time.Millisecond); err != nil {
			return err
		}
	}
	if last == nil {
		last = errors.New("no readiness evidence")
	}
	return fmt.Errorf("managed %s runtime did not become ready: %w", component, last)
}

func promoteManagedTargets(env managedUpdateEnvironment, transactionRoot string, targets map[string]*managedRuntimeTarget) error {
	components := make([]string, 0, len(targets))
	for component := range targets {
		components = append(components, component)
	}
	sort.Strings(components)
	for _, component := range components {
		target := targets[component]
		if env.files.Exists(target.WorkingDirectory) {
			return fmt.Errorf("managed %s release already exists", component)
		}
		if err := env.files.MkdirAll(filepath.Dir(target.WorkingDirectory), 0o700); err != nil {
			return err
		}
		if err := env.files.Rename(filepath.Join(transactionRoot, "candidate", component), target.WorkingDirectory); err != nil {
			return fmt.Errorf("managed %s release could not be installed", component)
		}
	}
	return nil
}

func readManagedPointer(files managedFileSystem, path string) (managedPointer, []byte, os.FileMode, error) {
	value, mode, err := files.ReadFile(path)
	if err != nil {
		return nil, nil, 0, err
	}
	var pointer managedPointer
	if err := json.Unmarshal(value, &pointer); err != nil {
		return nil, nil, 0, errors.New("runtime pointer is invalid")
	}
	return pointer, value, mode, nil
}

func pointerText(pointer managedPointer, key string) string {
	var value string
	_ = json.Unmarshal(pointer[key], &value)
	return value
}

func pointerTarget(pointer managedPointer, component string) (*managedRuntimeTarget, error) {
	value := pointer[component]
	if len(value) == 0 || string(value) == "null" {
		return nil, errors.New("installed target is missing")
	}
	var target managedRuntimeTarget
	if err := json.Unmarshal(value, &target); err != nil || target.Identity.SourceRevision == "" || !target.Identity.IsComplete {
		return nil, errors.New("installed target identity is incomplete")
	}
	return &target, nil
}

func managedNextGeneration(pointer managedPointer) int64 {
	maximum := int64(0)
	_ = json.Unmarshal(pointer["generation"], &maximum)
	for _, component := range []string{"cli", "server", "runner"} {
		var target managedRuntimeTarget
		if json.Unmarshal(pointer[component], &target) == nil && target.Identity.Generation > maximum {
			maximum = target.Identity.Generation
		}
	}
	return maximum + 1
}

func updateManagedPointer(pointer managedPointer, transactionID string, generation int64, targets map[string]*managedRuntimeTarget) managedPointer {
	result := managedPointer{}
	for key, value := range pointer {
		result[key] = append(json.RawMessage(nil), value...)
	}
	set := func(key string, value any) {
		encoded, _ := json.Marshal(value)
		result[key] = encoded
	}
	set("status", "verified")
	set("generation", generation)
	set("transactionId", transactionID)
	for component, target := range targets {
		set(component, target)
	}
	set("previous", nil)
	set("activationLease", nil)
	set("sourceSnapshot", nil)
	set("recoveryDiagnostic", nil)
	set("recovery", nil)
	return result
}

func writeManagedPointer(files managedFileSystem, path string, pointer managedPointer, mode os.FileMode) error {
	value, err := json.MarshalIndent(pointer, "", "  ")
	if err != nil {
		return err
	}
	if mode == 0 {
		mode = 0o600
	}
	return files.WriteFileAtomic(path, append(value, '\n'), mode)
}

func writeManagedTransaction(files managedFileSystem, path string, transaction *managedTransaction) error {
	value, err := json.MarshalIndent(transaction, "", "  ")
	if err != nil {
		return err
	}
	return files.WriteFileAtomic(path, append(value, '\n'), 0o600)
}

func (updater *managedUpdater) abortPrepared(
	_ context.Context,
	runtimeRoot string,
	statePath string,
	transaction *managedTransaction,
	active []byte,
	activeMode os.FileMode,
	verified []byte,
	verifiedMode os.FileMode,
	cause error,
) error {
	recoveryErrors := []string{}
	if transaction.InterruptID != "" {
		if runner := transaction.Targets["runner"]; runner != nil {
			if err := updater.env.control.CancelRunnerInterrupt(context.Background(), runner.Identity.RunnerID, transaction.InterruptID); err != nil {
				recoveryErrors = append(recoveryErrors, "Runner interrupt")
			}
		}
	}
	if err := updater.env.files.WriteFileAtomic(filepath.Join(runtimeRoot, "active.json"), active, activeMode); err != nil {
		recoveryErrors = append(recoveryErrors, "active pointer")
	}
	if err := updater.env.files.WriteFileAtomic(filepath.Join(runtimeRoot, "verified.json"), verified, verifiedMode); err != nil {
		recoveryErrors = append(recoveryErrors, "verified pointer")
	}
	transaction.Failure = cause.Error()
	transaction.UpdatedAt = updater.env.now()
	if len(recoveryErrors) > 0 {
		transaction.Status = "recovery-failed"
		if err := writeManagedTransaction(updater.env.files, statePath, transaction); err != nil {
			return fmt.Errorf("%w; managed runtime recovery failed for %s and its state could not be persisted", cause, strings.Join(recoveryErrors, ", "))
		}
		return fmt.Errorf("%w; managed runtime recovery failed for %s", cause, strings.Join(recoveryErrors, ", "))
	}
	transaction.Status = "rolled-back"
	if err := writeManagedTransaction(updater.env.files, statePath, transaction); err != nil {
		return fmt.Errorf("%w; managed runtime recovery state could not be persisted", cause)
	}
	if err := updater.env.files.RemoveAll(filepath.Join(runtimeRoot, "pending.json")); err != nil {
		return fmt.Errorf("%w; managed runtime recovery marker could not be cleared", cause)
	}
	return cause
}

func (updater *managedUpdater) rollback(
	ctx context.Context,
	runtimeRoot string,
	statePath string,
	transaction *managedTransaction,
	activated []string,
	interrupt managedRunnerInterrupt,
	active []byte,
	activeMode os.FileMode,
	verified []byte,
	verifiedMode os.FileMode,
	previous map[string]managedRuntimeObservation,
	cause error,
) error {
	recoveryContext := context.Background()
	recoveryErrors := []string{}
	if interrupt.InterruptID == "" && transaction.InterruptID != "" {
		if runner := transaction.Targets["runner"]; runner != nil {
			interrupt = managedRunnerInterrupt{
				RunnerID: runner.Identity.RunnerID, InterruptID: transaction.InterruptID,
			}
		}
	}
	if interrupt.InterruptID != "" {
		if err := updater.env.control.CancelRunnerInterrupt(recoveryContext, interrupt.RunnerID, interrupt.InterruptID); err != nil {
			recoveryErrors = append(recoveryErrors, "Runner interrupt")
		}
	}
	for index := len(activated) - 1; index >= 0; index-- {
		component := activated[index]
		if err := restoreManagedService(recoveryContext, updater.env, transaction.Services[component]); err != nil {
			recoveryErrors = append(recoveryErrors, component+" service")
		}
	}
	for _, component := range activated {
		old := transaction.Services[component].PreviousTarget
		if old == nil {
			recoveryErrors = append(recoveryErrors, component+" target")
			continue
		}
		priorConnection := previous[component].ConnectionGeneration
		if err := waitForManagedRuntime(recoveryContext, updater.env, component, old.Identity, priorConnection); err != nil {
			recoveryErrors = append(recoveryErrors, component+" readiness")
		}
	}
	if err := updater.env.files.WriteFileAtomic(filepath.Join(runtimeRoot, "active.json"), active, activeMode); err != nil {
		recoveryErrors = append(recoveryErrors, "active pointer")
	}
	if err := updater.env.files.WriteFileAtomic(filepath.Join(runtimeRoot, "verified.json"), verified, verifiedMode); err != nil {
		recoveryErrors = append(recoveryErrors, "verified pointer")
	}
	transaction.Failure = cause.Error()
	transaction.UpdatedAt = updater.env.now()
	if len(recoveryErrors) > 0 {
		transaction.Status = "recovery-failed"
		_ = writeManagedTransaction(updater.env.files, statePath, transaction)
		return fmt.Errorf("%w; managed runtime recovery failed for %s", cause, strings.Join(recoveryErrors, ", "))
	}
	transaction.Status = "rolled-back"
	if err := writeManagedTransaction(updater.env.files, statePath, transaction); err != nil {
		return fmt.Errorf("%w; managed runtime recovery state could not be persisted", cause)
	}
	if err := updater.env.files.RemoveAll(filepath.Join(runtimeRoot, "pending.json")); err != nil {
		return fmt.Errorf("%w; managed runtime recovery marker could not be cleared", cause)
	}
	return cause
}
