package mohistcli

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"path/filepath"
	"runtime"
	"sort"
	"strings"
)

func stageManagedTargets(
	ctx context.Context,
	env managedUpdateEnvironment,
	source managedSource,
	transactionRoot string,
	runtimeRoot string,
	generation int64,
	components []string,
	runnerID string,
) (map[string]*managedRuntimeTarget, error) {
	releaseScope := components[0]
	if len(components) > 1 {
		releaseScope = "full"
	}
	if result := env.commands.Run(ctx, managedCommand{
		Name: "npm", Args: []string{"ci", "--include=dev"}, Dir: source.BuildRoot,
	}); result.ExitCode != 0 {
		return nil, fmt.Errorf("npm dependency preparation failed with exit code %d", result.ExitCode)
	}

	targets := make(map[string]*managedRuntimeTarget, len(components))
	for _, component := range components {
		candidateRoot := filepath.Join(transactionRoot, "candidate", component)
		if err := env.files.MkdirAll(candidateRoot, 0o700); err != nil {
			return nil, fmt.Errorf("prepare %s candidate: %w", component, err)
		}
		var entryRelative string
		switch component {
		case "server":
			entryRelative = "Mohist.Server"
			project := filepath.Join(source.BuildRoot, "packages", "server", "src", "Mohist.Server", "Mohist.Server.csproj")
			result := env.commands.Run(ctx, managedCommand{
				Name: "dotnet",
				Args: []string{
					"publish", project, "-c", "Release", "-r", managedRuntimeIdentifier(),
					"--self-contained", "true", "/p:PublishSingleFile=true",
					"/p:InformationalVersion=0.0.0+" + source.Commit,
					"/p:SourceRevisionId=" + source.Commit, "-o", candidateRoot,
				},
				Dir: source.BuildRoot,
			})
			if result.ExitCode != 0 {
				return nil, fmt.Errorf("server publish failed with exit code %d", result.ExitCode)
			}
		case "runner":
			if runnerID == "" {
				return nil, fmt.Errorf("runner update requires the installed Runner identity")
			}
			entryRelative = filepath.Join("dist", "cli.js")
			result := env.commands.Run(ctx, managedCommand{
				Name: "npm", Args: []string{"run", "build", "-w", "packages/runner"}, Dir: source.BuildRoot,
			})
			if result.ExitCode != 0 {
				return nil, fmt.Errorf("runner build failed with exit code %d", result.ExitCode)
			}
			copies := []struct{ from, to string }{
				{filepath.Join(source.BuildRoot, "packages", "runner", "dist") + string(filepath.Separator) + ".", filepath.Join(candidateRoot, "dist")},
				{filepath.Join(source.BuildRoot, "packages", "runner", "package.json"), filepath.Join(candidateRoot, "package.json")},
				{filepath.Join(source.BuildRoot, "node_modules"), filepath.Join(candidateRoot, "node_modules")},
				{filepath.Join(source.BuildRoot, "packages", "runner", "node_modules") + string(filepath.Separator) + ".", filepath.Join(candidateRoot, "node_modules")},
			}
			for _, copy := range copies {
				result := env.commands.Run(ctx, managedCommand{
					Name: "cp", Args: []string{"-RLp", copy.from, copy.to}, Dir: source.BuildRoot,
				})
				if result.ExitCode != 0 {
					return nil, fmt.Errorf("runner dependency staging failed with exit code %d", result.ExitCode)
				}
			}
		default:
			return nil, fmt.Errorf("unsupported managed component %q", component)
		}

		if !env.files.Exists(filepath.Join(candidateRoot, entryRelative)) {
			return nil, fmt.Errorf("%s publish did not produce its required entrypoint", component)
		}
		digest, err := managedArtifactDigest(env.files, candidateRoot)
		if err != nil {
			return nil, fmt.Errorf("hash %s candidate: %w", component, err)
		}
		identity := managedRuntimeIdentity{
			Component: component, Version: "0.0.0+" + source.Commit,
			SourceRevision: source.Commit, TreeHash: source.TreeHash, ArtifactDigest: digest,
			ReleaseID: "mohist-" + releaseScope + "-" + source.Commit, Generation: generation,
			RunnerID: runnerID, IsComplete: true,
		}
		if component == "runner" {
			identity.BuildGitHash = source.Commit
		} else {
			identity.RunnerID = ""
		}
		if err := writeManagedMetadata(env.files, candidateRoot, source, identity); err != nil {
			return nil, err
		}
		releaseRoot := filepath.Join(runtimeRoot, "releases", fmt.Sprintf("mohist-%s-%s-g%d", component, source.Commit, generation), component)
		entrypoint := filepath.Join(releaseRoot, entryRelative)
		target := &managedRuntimeTarget{
			Component: component, Entrypoint: entrypoint, WorkingDirectory: releaseRoot,
			Arguments: []string{}, RuntimeIdentifier: managedRuntimeIdentifier(), Identity: identity,
			LaunchMode: 0, IsAbsoluteTarget: true, UsesCanonicalEntrypoint: true,
		}
		if component == "runner" {
			node, err := resolveManagedNode(ctx, env.commands, source.BuildRoot)
			if err != nil {
				return nil, err
			}
			target.NodeExecutable = &node
			target.DependencyRoot = &releaseRoot
			target.LaunchMode = 1
		}
		targets[component] = target
	}
	return targets, nil
}

func writeManagedMetadata(files managedFileSystem, root string, source managedSource, identity managedRuntimeIdentity) error {
	identityValue, err := json.MarshalIndent(identity, "", "  ")
	if err != nil {
		return err
	}
	if err := files.WriteFileAtomic(filepath.Join(root, "runtime-identity.json"), append(identityValue, '\n'), 0o600); err != nil {
		return err
	}
	release := struct {
		Identity     managedRuntimeIdentity `json:"identity"`
		SourceRoot   string                 `json:"sourceRoot"`
		SnapshotRoot string                 `json:"snapshotRoot"`
	}{identity, source.RepositoryRoot, source.SnapshotRoot}
	releaseValue, err := json.MarshalIndent(release, "", "  ")
	if err != nil {
		return err
	}
	if err := files.WriteFileAtomic(filepath.Join(root, "release.json"), append(releaseValue, '\n'), 0o600); err != nil {
		return err
	}
	if identity.Component == "runner" {
		buildInfo := struct {
			Component      string `json:"component"`
			Version        string `json:"version"`
			GitHash        string `json:"gitHash"`
			SourceRevision string `json:"sourceRevision"`
			TreeHash       string `json:"treeHash"`
			ArtifactDigest string `json:"artifactDigest"`
			ReleaseID      string `json:"releaseId"`
			RunnerID       string `json:"runnerId"`
			Generation     int64  `json:"generation"`
		}{"runner", identity.Version, identity.SourceRevision, identity.SourceRevision, identity.TreeHash,
			identity.ArtifactDigest, identity.ReleaseID, identity.RunnerID, identity.Generation}
		value, err := json.MarshalIndent(buildInfo, "", "  ")
		if err != nil {
			return err
		}
		if err := files.WriteFileAtomic(filepath.Join(root, "dist", "build-info.json"), append(value, '\n'), 0o600); err != nil {
			return err
		}
	}
	return nil
}

func managedArtifactDigest(files managedFileSystem, root string) (string, error) {
	paths, err := files.WalkFiles(root)
	if err != nil {
		return "", err
	}
	metadataPaths := map[string]bool{
		"runtime-identity.json": true,
		"release.json":          true,
		"dist/build-info.json":  true,
	}
	filtered := paths[:0]
	for _, path := range paths {
		relative := filepath.ToSlash(path)
		if !metadataPaths[relative] {
			filtered = append(filtered, relative)
		}
	}
	sort.Strings(filtered)
	hash := sha256.New()
	for _, relative := range filtered {
		value, _, err := files.ReadFile(filepath.Join(root, filepath.FromSlash(relative)))
		if err != nil {
			return "", err
		}
		_, _ = fmt.Fprintf(hash, "%s\n%d\n", relative, len(value))
		_, _ = hash.Write(value)
	}
	return hex.EncodeToString(hash.Sum(nil)), nil
}

func resolveManagedNode(ctx context.Context, commands managedCommandRunner, directory string) (string, error) {
	result := commands.Run(ctx, managedCommand{Name: "sh", Args: []string{"-c", "command -v node"}, Dir: directory})
	value := strings.TrimSpace(result.Stdout)
	if result.ExitCode != 0 || !filepath.IsAbs(value) || strings.ContainsAny(value, "\r\n") {
		return "", fmt.Errorf("node executable could not be resolved to an absolute path")
	}
	return value, nil
}

func managedRuntimeIdentifier() string {
	osName := runtime.GOOS
	architecture := map[string]string{"amd64": "x64", "arm64": "arm64"}[runtime.GOARCH]
	if architecture == "" {
		architecture = runtime.GOARCH
	}
	if osName == "darwin" {
		osName = "osx"
	}
	return osName + "-" + architecture
}
