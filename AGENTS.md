# AGENTS.md

## Project Type

This repository is a **framework mod**. Apply the global development/tooling contract.

## Framework-Specific Rules

Framework changes may affect downstream mods and public integration contracts.

* Allow RimTest to select downstream compatibility/integration coverage.
* Do not treat framework-local tests as sufficient when affected consumers require validation.
* Preserve public APIs, schemas, serialization, hooks, and integration behavior unless the task intentionally changes them.
* Do not manually narrow conservative affected-test selection.
* Breaking compatibility must be intentional and reported clearly.

For source changes, use the normal RimTest-owned workflow defined by the global `AGENTS.md`.
