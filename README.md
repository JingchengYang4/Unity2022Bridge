# Unity2022Bridge

A standalone Unity 2022.3 Editor bridge for Codex and other coding agents.

Unity2022Bridge aims to provide a Codex live-Editor workflow for projects that stay
on Unity 2022.3. It lets an agent inspect the open project, edit scene objects,
query assets, and check compilation through a typed local HTTP API. Keep the
Editor running while your agent works, with automatic reconnection after domain
reloads.

## Features

- Inspect scenes, GameObjects, components, and serialized properties.
- Search assets, trace dependencies, and inspect prefabs.
- Create and update scene objects with Unity Undo support.
- Run batches of scene edits, including dry runs and rollback on failure.
- Read bounded Editor logs and compilation diagnostics.
- Refresh assets and track work across domain reloads.
- Inspect UI Toolkit trees and discover available component types.
- Explicitly open or save scenes and enter or exit Play Mode.

The package runs only in the Editor and is excluded from player builds.

## Requirements

- Unity 2022.3; verified with **2022.3.62f3**.
- An open Editor with script compilation complete.
- `zsh` and `curl` for the bundled shell client. Other clients can use the HTTP API directly.

## Installation

In Unity, open **Window → Package Manager**, select **+ → Add package from git URL**,
and enter:

```text
https://github.com/JingchengYang4/Unity2022Bridge.git
```

Wait for Unity to import and compile the package. The bridge starts automatically.

For local development, clone the repository and use **Add package from disk** to
select its `package.json`. This keeps Unity connected to your working checkout.

## Quick start

Clone the repository to get the shell client:

```sh
git clone https://github.com/JingchengYang4/Unity2022Bridge.git
cd Unity2022Bridge

./Tools~/unity-bridge --project /path/to/YourUnityProject status
./Tools~/unity-bridge --project /path/to/YourUnityProject wait-ready 60
./Tools~/unity-bridge --project /path/to/YourUnityProject commands
./Tools~/unity-bridge --project /path/to/YourUnityProject compile-errors 0 50
```

Point `--project` at the Unity project containing `Assets` and `ProjectSettings`.
It selects the intended Editor when multiple projects are open.

You can also set the project once for your shell session:

```sh
export UNITY_BRIDGE_PROJECT="/path/to/YourUnityProject"
./Tools~/unity-bridge status
```

`--project` takes precedence over the environment variable. Without either, the
client searches for a Unity project in the working directory and its ancestors.

## Using it with Codex

Give Codex the repository path and the target Unity project path. For example:

> Use `/path/to/Unity2022Bridge/Tools~/unity-bridge --project /path/to/YourUnityProject`
> to work with the running Unity 2022.3 Editor. Check `status` and `commands` first.
> Wait for compilation and imports before making scene edits, then check
> `compile-errors`. Save scenes only when requested.

The bridge provides typed operations, with no arbitrary C# or shell evaluation
endpoint. Codex can edit source files through its normal workspace tools, then use
the bridge to refresh assets, inspect the live Editor, and check diagnostics.

## Connection and behavior

The server listens only on loopback, choosing a free port from **49222–49231**.
Requests authenticate with a project-local token. Discovery files live in
`<project>/Library/LimenBridge/`; keep them out of source control. The shell client
reads discovery on every request so it can reconnect after domain reloads.

Scene mutations use Unity Undo, validate their targets, and reject unsafe Editor
states. Scene saves and Play Mode changes are explicit operations. The optional
menu endpoint is restricted to existing commands under `Tools/Limen/*`.

See the [API reference](Documentation~/BRIDGE.md) for routes, request bodies,
refresh jobs, safety rules, and troubleshooting.
