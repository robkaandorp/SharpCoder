---
name: build
description: How to build the project. Use this when you need to compile or build the codebase.
---

# Build Skill

## How to Build

**Prerequisite:** If `command -v dotnet` fails, load and follow the `install-dotnet-sdk` skill first; do not record the exit-127 run as a build result.

Build the solution:

```bash
dotnet build SharpCoder.slnx
```

If the build fails, read the error messages carefully and fix the issues before proceeding.
