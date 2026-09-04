package mohistcli

import (
	"bytes"
	"context"
	"crypto/rand"
	"encoding/hex"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"syscall"
)

type realManagedCommands struct{}

func (realManagedCommands) Run(ctx context.Context, spec managedCommand) managedCommandResult {
	command := exec.CommandContext(ctx, spec.Name, spec.Args...)
	command.Dir = spec.Dir
	var stdout bytes.Buffer
	var stderr bytes.Buffer
	command.Stdout = &stdout
	command.Stderr = &stderr
	err := command.Run()
	if err == nil {
		return managedCommandResult{Stdout: stdout.String(), Stderr: stderr.String()}
	}
	if exit, ok := err.(*exec.ExitError); ok {
		return managedCommandResult{ExitCode: exit.ExitCode(), Stdout: stdout.String(), Stderr: stderr.String()}
	}
	return managedCommandResult{ExitCode: -1, Stdout: stdout.String(), Stderr: stderr.String()}
}

func (realManagedFiles) WriteFileAtomic(path string, value []byte, mode os.FileMode) error {
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		return err
	}
	temporary, err := os.CreateTemp(filepath.Dir(path), ".mohist-write-*")
	if err != nil {
		return err
	}
	temporaryPath := temporary.Name()
	remove := true
	defer func() {
		_ = temporary.Close()
		if remove {
			_ = os.Remove(temporaryPath)
		}
	}()
	if err := temporary.Chmod(mode); err != nil {
		return err
	}
	if _, err := temporary.Write(value); err != nil {
		return err
	}
	if err := temporary.Sync(); err != nil {
		return err
	}
	if err := temporary.Close(); err != nil {
		return err
	}
	if err := os.Rename(temporaryPath, path); err != nil {
		return err
	}
	remove = false
	directory, err := os.Open(filepath.Dir(path))
	if err != nil {
		return err
	}
	defer directory.Close()
	return directory.Sync()
}

type managedLock struct {
	file *os.File
}

func (lock *managedLock) Close() error {
	unlockError := syscall.Flock(int(lock.file.Fd()), syscall.LOCK_UN)
	closeError := lock.file.Close()
	if unlockError != nil {
		return unlockError
	}
	return closeError
}

func (realManagedFiles) OpenLock(path string) (io.Closer, error) {
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		return nil, err
	}
	file, err := os.OpenFile(path, os.O_CREATE|os.O_RDWR, 0o600)
	if err != nil {
		return nil, err
	}
	if err := syscall.Flock(int(file.Fd()), syscall.LOCK_EX|syscall.LOCK_NB); err != nil {
		_ = file.Close()
		if err == syscall.EWOULDBLOCK {
			return nil, fmt.Errorf("another Mohist install or update is running")
		}
		return nil, err
	}
	return &managedLock{file: file}, nil
}

func newManagedUpdateID() string {
	buffer := make([]byte, 16)
	if _, err := rand.Read(buffer); err != nil {
		panic("crypto/rand failed: " + err.Error())
	}
	return hex.EncodeToString(buffer)
}
