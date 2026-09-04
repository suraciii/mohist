package mohistcli

import (
	"context"
	"fmt"
	"path/filepath"
	"strings"
)

type managedGitIdentity struct {
	commit   string
	treeHash string
}

func captureManagedSource(
	ctx context.Context,
	env managedUpdateEnvironment,
	repoRoot string,
	runtimeRoot string,
	transactionID string,
) (managedSource, error) {
	if env.commands == nil {
		return managedSource{}, fmt.Errorf("managed update command runner is unavailable")
	}
	if env.files == nil {
		return managedSource{}, fmt.Errorf("managed update filesystem is unavailable")
	}

	root, err := resolveManagedRepositoryRoot(ctx, env, repoRoot)
	if err != nil {
		return managedSource{}, err
	}
	identity, err := readManagedGitIdentity(ctx, env, root)
	if err != nil {
		return managedSource{}, err
	}

	transactionRoot, err := managedTransactionRoot(runtimeRoot, transactionID)
	if err != nil {
		return managedSource{}, err
	}
	snapshotRoot := filepath.Join(transactionRoot, "snapshot")
	buildContainerRoot := filepath.Join(transactionRoot, "build")
	buildRoot := filepath.Join(buildContainerRoot, "source")
	archivePath := filepath.Join(buildContainerRoot, "source.tar")

	for _, directory := range []string{transactionRoot, snapshotRoot, buildContainerRoot, buildRoot} {
		if err := env.files.MkdirAll(directory, 0o700); err != nil {
			return managedSource{}, fmt.Errorf("managed update workspace could not be prepared at %q: %w", transactionRoot, err)
		}
	}

	if _, err := runManagedSourceCommand(
		ctx,
		env,
		managedCommand{
			Name: "git",
			Args: []string{"archive", "--format=tar", "--output=" + archivePath, identity.commit},
			Dir:  root,
		},
		"source snapshot archive",
	); err != nil {
		return managedSource{}, err
	}

	if _, err := runManagedSourceCommand(
		ctx,
		env,
		managedCommand{
			Name: "tar",
			Args: []string{"-xf", archivePath, "-C", snapshotRoot},
			Dir:  buildContainerRoot,
		},
		"source snapshot extraction",
	); err != nil {
		return managedSource{}, err
	}
	if _, err := runManagedSourceCommand(
		ctx,
		env,
		managedCommand{
			Name: "tar",
			Args: []string{"-xf", archivePath, "-C", buildRoot},
			Dir:  buildContainerRoot,
		},
		"source build workspace extraction",
	); err != nil {
		return managedSource{}, err
	}
	if _, err := runManagedSourceCommand(
		ctx,
		env,
		managedCommand{
			Name: "chmod",
			Args: []string{"-R", "a-w", snapshotRoot},
			Dir:  transactionRoot,
		},
		"source snapshot read-only protection",
	); err != nil {
		return managedSource{}, err
	}

	return managedSource{
		RepositoryRoot: root,
		Commit:         identity.commit,
		TreeHash:       identity.treeHash,
		SnapshotRoot:   filepath.ToSlash(snapshotRoot),
		BuildRoot:      filepath.ToSlash(buildRoot),
	}, nil
}

func resolveManagedRepositoryRoot(
	ctx context.Context,
	env managedUpdateEnvironment,
	repoRoot string,
) (string, error) {
	if env.commands == nil {
		return "", fmt.Errorf("managed update command runner is unavailable")
	}
	if env.files == nil {
		return "", fmt.Errorf("managed update filesystem is unavailable")
	}

	explicitRoot := strings.TrimSpace(repoRoot)
	commandRoot := explicitRoot
	if explicitRoot != "" {
		absoluteRoot, err := filepath.Abs(explicitRoot)
		if err != nil {
			return "", fmt.Errorf("repository root %q could not be resolved", explicitRoot)
		}
		commandRoot = filepath.Clean(absoluteRoot)
	}

	result, err := runManagedSourceCommand(
		ctx,
		env,
		managedCommand{Name: "git", Args: []string{"rev-parse", "--show-toplevel"}, Dir: commandRoot},
		"source Git top-level discovery",
	)
	if err != nil {
		return "", err
	}
	root, err := parseManagedRepositoryRoot(result.Stdout)
	if err != nil {
		return "", err
	}
	if explicitRoot != "" && !sameManagedPath(commandRoot, root) {
		return "", fmt.Errorf("repository root %q is not the Git top-level %q", filepath.ToSlash(commandRoot), root)
	}
	if !env.files.Exists(filepath.Join(filepath.FromSlash(root), "Mohist.sln")) {
		return "", fmt.Errorf("repository root %q does not contain Mohist.sln", root)
	}
	return root, nil
}

func readManagedGitIdentity(
	ctx context.Context,
	env managedUpdateEnvironment,
	repositoryRoot string,
) (managedGitIdentity, error) {
	commitResult, err := runManagedSourceCommand(
		ctx,
		env,
		managedCommand{Name: "git", Args: []string{"rev-parse", "--verify", "HEAD"}, Dir: repositoryRoot},
		"source Git commit inspection",
	)
	if err != nil {
		return managedGitIdentity{}, err
	}
	commit, err := parseManagedObjectID(commitResult.Stdout, "source Git commit")
	if err != nil {
		return managedGitIdentity{}, err
	}

	treeResult, err := runManagedSourceCommand(
		ctx,
		env,
		managedCommand{Name: "git", Args: []string{"rev-parse", "--verify", "HEAD^{tree}"}, Dir: repositoryRoot},
		"source Git tree inspection",
	)
	if err != nil {
		return managedGitIdentity{}, err
	}
	treeHash, err := parseManagedObjectID(treeResult.Stdout, "source Git tree")
	if err != nil {
		return managedGitIdentity{}, err
	}

	status, err := runManagedSourceCommand(
		ctx,
		env,
		managedCommand{
			Name: "git",
			Args: []string{"status", "--porcelain", "--untracked-files=all"},
			Dir:  repositoryRoot,
		},
		"source Git cleanliness inspection",
	)
	if err != nil {
		return managedGitIdentity{}, err
	}
	if strings.TrimSpace(status.Stdout) != "" {
		return managedGitIdentity{}, fmt.Errorf("source repository %q is dirty; commit or remove changes before updating", repositoryRoot)
	}

	return managedGitIdentity{commit: commit, treeHash: treeHash}, nil
}

func verifyManagedSourceUnchanged(
	ctx context.Context,
	env managedUpdateEnvironment,
	source managedSource,
) error {
	root, err := resolveManagedRepositoryRoot(ctx, env, source.RepositoryRoot)
	if err != nil {
		return fmt.Errorf("source identity could not be read back after staging: %w", err)
	}
	identity, err := readManagedGitIdentity(ctx, env, root)
	if err != nil {
		return fmt.Errorf("source identity could not be read back after staging: %w", err)
	}
	if identity.commit != source.Commit || identity.treeHash != source.TreeHash {
		return fmt.Errorf(
			"source changed after target capture (expected %s/%s, observed %s/%s)",
			source.Commit,
			source.TreeHash,
			identity.commit,
			identity.treeHash,
		)
	}
	return nil
}

func managedTransactionRoot(runtimeRoot string, transactionID string) (string, error) {
	root := strings.TrimSpace(runtimeRoot)
	if root == "" {
		return "", fmt.Errorf("managed runtime root is unavailable")
	}
	id := strings.TrimSpace(transactionID)
	if id == "" || id == "." || id == ".." || strings.ContainsAny(id, `/\\`) {
		return "", fmt.Errorf("managed update transaction ID is invalid")
	}
	return filepath.Join(root, "transactions", id), nil
}

func parseManagedRepositoryRoot(output string) (string, error) {
	root := strings.TrimSpace(output)
	if root == "" || strings.ContainsAny(root, "\r\n") || !filepath.IsAbs(root) {
		return "", fmt.Errorf("source Git top-level is invalid")
	}
	return filepath.ToSlash(filepath.Clean(root)), nil
}

func parseManagedObjectID(output string, description string) (string, error) {
	identity := strings.ToLower(strings.TrimSpace(output))
	if strings.ContainsAny(identity, "\r\n") || (len(identity) != 40 && len(identity) != 64) {
		return "", fmt.Errorf("%s is invalid", description)
	}
	for _, character := range identity {
		if (character < '0' || character > '9') && (character < 'a' || character > 'f') {
			return "", fmt.Errorf("%s is invalid", description)
		}
	}
	return identity, nil
}

func sameManagedPath(left string, right string) bool {
	return filepath.Clean(filepath.FromSlash(left)) == filepath.Clean(filepath.FromSlash(right))
}

func runManagedSourceCommand(
	ctx context.Context,
	env managedUpdateEnvironment,
	command managedCommand,
	stage string,
) (managedCommandResult, error) {
	if env.commands == nil {
		return managedCommandResult{}, fmt.Errorf("managed update command runner is unavailable")
	}
	result := env.commands.Run(ctx, command)
	if result.ExitCode != 0 {
		return managedCommandResult{}, fmt.Errorf("%s failed with exit code %d", stage, result.ExitCode)
	}
	return result, nil
}
