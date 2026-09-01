package main

import (
	"context"
	"os"
	"os/signal"
	"syscall"

	mohistcli "github.com/suraciii/mohist/packages/go/mohist-cli"
)

func main() {
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	os.Exit(mohistcli.Run(ctx, os.Args[1:], mohistcli.Dependencies{}))
}
