---
name: install-dotnet-sdk
description: Use this skill when `dotnet` is not found (exit 127) before building or testing.
---

# Install .NET SDK

## Check

Run:

```bash
command -v dotnet && dotnet --list-sdks
"$HOME/.dotnet/dotnet" --list-sdks
```

If a channel 10.0 SDK is listed by the first check, stop — nothing to install. If an SDK is listed only by the second check, it is on disk but not on `PATH`; only the Environment step is needed.

## Install (once per container)

Download the installer to a file so download failures are visible, then run it:

```bash
curl -fsSL --retry 5 --retry-delay 2 -o /tmp/dotnet-install.sh https://dot.net/v1/dotnet-install.sh && bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet"
```

## Environment

CopilotHive worker images already set `DOTNET_ROOT=/root/.dotnet` and put `/root/.dotnet` and `/root/.dotnet/tools` on `PATH`, so no export is needed there. Each `execute_bash_command` call is a fresh shell. In other environments, prefix every `dotnet` command in the same call with:

```bash
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH";
```

## Verify

In a new shell call, run:

```bash
dotnet --version
dotnet --list-sdks
```
