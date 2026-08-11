# Deferred Reality Framework

- Package ID: `lan.deferredreality.framework`.
- Adapter source: `DevTools/BridgeAdapter/DeferredRealityBridgeAdapter.cs`; package output: `DevTools/BridgeAdapters`.
- Build: `DevTools\Build-BridgeAdapter.ps1`; validate: `DevTools\Test-BridgeAdapter.ps1`; pure tests: `DeferredReality.Tests\DeferredReality.PureTests.csproj`.
- DevBridge2 is the only supported live-test coordinator: use `C:\Games\Steam\steamapps\common\RimWorld\Mods\DevBridge2\DevBridge.cmd` for status, leases, restart, and readiness.
- DevBridge2 has no adapter-registration or adapter-reload protocol. The historical adapter is not a release input.
- Gameplay, defs, Harmony, serialized types, or core changes require a full DevBridge2 restart followed by wait-ready.
- Deferred Reality and DevBridge2 are mutually optional; the gameplay mod does not depend on either.
- Full workflow: `DevTools/DEVBRIDGE2_AGENT.md` in the consuming mod repository.
