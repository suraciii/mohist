package mohistcli

import (
	"errors"
	"os"
	"path/filepath"
	"syscall"
)

func acquireUserTransactionLock(home string) (func(), bool, error) {
	lockDir := filepath.Join(home, ".mohist")
	if err := os.MkdirAll(lockDir, 0o700); err != nil {
		return nil, false, err
	}
	file, err := os.OpenFile(filepath.Join(lockDir, "update.lock"), os.O_CREATE|os.O_RDWR, 0o600)
	if err != nil {
		return nil, false, err
	}
	if err := syscall.Flock(int(file.Fd()), syscall.LOCK_EX|syscall.LOCK_NB); err != nil {
		_ = file.Close()
		if errors.Is(err, syscall.EWOULDBLOCK) || errors.Is(err, syscall.EAGAIN) {
			return nil, false, nil
		}
		return nil, false, err
	}
	return func() {
		_ = syscall.Flock(int(file.Fd()), syscall.LOCK_UN)
		_ = file.Close()
	}, true, nil
}
