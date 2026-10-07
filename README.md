# Unity Editor Bridge

Standalone local UPM package extracted from BackroomsOnline's Unity 2022.3 bridge.
Package identity (`com.limen.editor-bridge`), assembly names, HTTP routes, authentication
header, and discovery format are preserved for compatibility. No Limen gameplay
assemblies are required. The package is Editor-only and is not included in player builds.

## Install

In Unity's Package Manager, use **Add package from git URL** with:

```text
https://github.com/JingchengYang4/Unity2022Bridge.git
```

In Unity's Package Manager, use **Add package from disk** and select this folder's
`package.json`. Programmatic installation uses `UnityEditor.PackageManager.Client.Add`
with `file:/absolute/path/to/UnityEditorBridge`.

IL89 uses the shared package at `/Users/jingchengyang/Projects/UnityEditorBridge`.

## Use

```sh
~/Projects/UnityEditorBridge/Tools~/unity-bridge --project ~/Projects/IL89 status
~/Projects/UnityEditorBridge/Tools~/unity-bridge --project ~/Projects/IL89 wait-ready 60
~/Projects/UnityEditorBridge/Tools~/unity-bridge --project ~/Projects/IL89 commands
~/Projects/UnityEditorBridge/Tools~/unity-bridge --project ~/Projects/IL89 compile-errors 0 50
```

`--project` overrides `UNITY_BRIDGE_PROJECT`. Without either, the client locates the
Unity project containing the working directory, including the original `Limen/`
repository layout. Explicit project selection is recommended with multiple Editors.

The Editor binds loopback only, chooses a free port in 49222–49231, and stores its
project-local token and discovery files under `<project>/Library/LimenBridge`.
The client rereads discovery on every request and reconnects across domain reloads.
The token is never printed. Keep discovery out of source control.

The API provides filtered object/component/property and asset queries, Undo-aware
mutations, bounded logs and compilation diagnostics, and asynchronous refresh jobs.
Scene saves and Play Mode changes are explicit commands. Arbitrary C# and shell
execution are unavailable. For compatibility, the optional menu route retains its
`Tools/Limen/*` allowlist; the typed APIs work in any Unity project.

See [the complete bridge reference](Documentation~/BRIDGE.md) for routes, request
bodies, safety rules, and troubleshooting.
