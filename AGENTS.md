# Deferred Reality Framework

- Package ID: `lan.deferredreality.framework`.
- Adapter source: `DevTools/BridgeAdapter/DeferredRealityBridgeAdapter.cs`; package output: `DevTools/BridgeAdapters`.
- Build: `DevTools\Build-BridgeAdapter.ps1`; validate: `DevTools\Test-BridgeAdapter.ps1`; pure tests: `DeferredReality.Tests\DeferredReality.PureTests.csproj`.
- Query fresh live Dev Bridge context before runtime tests with the Dev Bridge checkout's `DevTools\devbridge.ps1`.
- Reload only the cooperative adapter for adapter-only changes. Gameplay, defs, Harmony, serialized types, or core changes require a full restart.
- Deferred Reality and Dev Bridge are mutually optional; the gameplay mod does not depend on the bridge.
- Full workflow: `DevTools/DEVBRIDGE_AGENT.md`.
