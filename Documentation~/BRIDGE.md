# Unity Editor Bridge

`com.limen.editor-bridge` is a standalone command bridge for Unity 2022.3
projects. It keeps one warm Editor available to scripts
and agents, avoiding repeated batch-mode launches and UI automation for routine
engineering operations.

The bridge is intentionally narrower than arbitrary C# evaluation. It exposes a
typed command surface, validates mutation preconditions, and executes Unity APIs
on the Editor main thread.

## Location

- Unity package: `~/Projects/UnityEditorBridge/`
- Shell client: `Tools~/unity-bridge`
- Runtime discovery: `<project>/Library/LimenBridge/`

Install this folder as a local UPM dependency. It is Editor-only and
targets Unity 2022.3. Installation and compilation have been verified with
Unity 2022.3.62f3; newer Editor versions have not been verified.

## Starting the bridge

Open the target Unity project in Unity 2022.3 and wait for script compilation. The package then:

1. Binds the first available loopback port from `49222` through `49231`.
2. Generates or reuses a 64-character project-local token.
3. Writes `port`, `token`, and `instance.json` under
   `<project>/Library/LimenBridge/`.
4. Restarts automatically after an Editor domain reload.

The Editor update hook also performs a bounded listener health check. If a
one-shot startup callback is lost during chained imports or the HTTP listener
stops unexpectedly, the bridge retries startup once per second until it is
available again.

The server never binds a non-loopback interface. Requests require the token in
the `X-Limen-Bridge-Token` header. Discovery and job files live under Unity's
ignored `Library/` directory and must not be committed.

## Shell client

Pass the target Unity project explicitly:

```sh
~/Projects/UnityEditorBridge/Tools~/unity-bridge --project /path/to/UnityProject status
~/Projects/UnityEditorBridge/Tools~/unity-bridge --project /path/to/UnityProject wait-ready 120
~/Projects/UnityEditorBridge/Tools~/unity-bridge --project /path/to/UnityProject commands
~/Projects/UnityEditorBridge/Tools~/unity-bridge --project /path/to/UnityProject hierarchy
~/Projects/UnityEditorBridge/Tools~/unity-bridge --project /path/to/UnityProject selection
~/Projects/UnityEditorBridge/Tools~/unity-bridge --project /path/to/UnityProject logs 0 100
~/Projects/UnityEditorBridge/Tools~/unity-bridge --project /path/to/UnityProject assets-find 't:Scene Menu' Assets/Levels
~/Projects/UnityEditorBridge/Tools~/unity-bridge --project /path/to/UnityProject refresh
~/Projects/UnityEditorBridge/Tools~/unity-bridge --project /path/to/UnityProject job JOB_ID
~/Projects/UnityEditorBridge/Tools~/unity-bridge --project /path/to/UnityProject play play
~/Projects/UnityEditorBridge/Tools~/unity-bridge --project /path/to/UnityProject play stop
~/Projects/UnityEditorBridge/Tools~/unity-bridge --project /path/to/UnityProject menu 'Tools/Limen/Your Project Command'
~/Projects/UnityEditorBridge/Tools~/unity-bridge --project /path/to/UnityProject scene-open Assets/Levels/Menu/Menu.unity
~/Projects/UnityEditorBridge/Tools~/unity-bridge --project /path/to/UnityProject scene-save
~/Projects/UnityEditorBridge/Tools~/unity-bridge --project /path/to/UnityProject save-all
~/Projects/UnityEditorBridge/Tools~/unity-bridge --project /path/to/UnityProject objects-find Camera
~/Projects/UnityEditorBridge/Tools~/unity-bridge --project /path/to/UnityProject object-get 'Environment/Terminal'
~/Projects/UnityEditorBridge/Tools~/unity-bridge --project /path/to/UnityProject object-create 'Contract Terminal' 'Environment'
~/Projects/UnityEditorBridge/Tools~/unity-bridge --project /path/to/UnityProject component-add 'Environment/Contract Terminal' UnityEngine.BoxCollider
~/Projects/UnityEditorBridge/Tools~/unity-bridge --project /path/to/UnityProject properties 'Environment/Contract Terminal' UnityEngine.BoxCollider
~/Projects/UnityEditorBridge/Tools~/unity-bridge --project /path/to/UnityProject compile-errors 0 100
~/Projects/UnityEditorBridge/Tools~/unity-bridge --project /path/to/UnityProject component-types UIDocument
~/Projects/UnityEditorBridge/Tools~/unity-bridge --project /path/to/UnityProject asset-dependencies Assets/UI/MainMenu/MainMenu.uxml reverse
~/Projects/UnityEditorBridge/Tools~/unity-bridge --project /path/to/UnityProject prefab-inspect Assets/Prefabs/Terminal.prefab
~/Projects/UnityEditorBridge/Tools~/unity-bridge --project /path/to/UnityProject ui-tree 'Main Menu UI' contract
```

The client prints the bridge's JSON response without requiring `jq`. Callers may
pipe the result into their preferred JSON processor.

`wait-ready` requires a successful status response as well as idle compilation and
import flags. A main-thread timeout's default false flags do not establish readiness;
the client keeps polling the same Editor until a successful response or its deadline.

`call` exposes new typed routes without requiring a shell-client release:

```sh
~/Projects/UnityEditorBridge/Tools~/unity-bridge --project /path/to/UnityProject call POST /v1/objects/update \
  '{"objectPath":"Environment/Contract Terminal","hasLocalPosition":true,"localPosition":[2,1,4]}'
```

Use `globalObjectId` returned by object queries when duplicate hierarchy names
make a path ambiguous. Paths remain convenient for simple hierarchies.

## HTTP API

Every request must include:

```text
X-Limen-Bridge-Token: <contents of <project>/Library/LimenBridge/token>
```

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/v1/status` | Unity version, project, active scene, compilation, update, and Play Mode state |
| `GET` | `/v1/commands` | Discover the bridge API version and command catalog |
| `GET` | `/v1/logs?after=0&limit=100` | Bridge-captured Editor logs since package initialization |
| `GET` | `/v1/diagnostics/compilation?after=0&limit=100` | Compilation state and error-class logs after a cursor |
| `GET` | `/v1/jobs/{id}` | Persistent asynchronous job status |
| `GET` | `/v1/scenes/hierarchy` | Flat active-scene hierarchy with component type names |
| `GET` | `/v1/selection` | Active Editor selection |
| `POST` | `/v1/assets/find` | Search the Asset Database with a Unity search filter |
| `POST` | `/v1/assets/refresh` | Queue an Asset Database refresh job |
| `POST` | `/v1/assets/dependencies` | Find bounded direct, recursive, or reverse asset dependencies |
| `POST` | `/v1/types/components` | Discover component types, assemblies, and addability constraints |
| `POST` | `/v1/batch` | Apply or dry-run atomic Undo-grouped scene mutations |
| `POST` | `/v1/objects/find` | Find objects in all loaded scenes by name/path substring and optional component type |
| `POST` | `/v1/objects/get` | Inspect an object and obtain its stable GlobalObjectId |
| `POST` | `/v1/objects/create` | Create and optionally parent/position an Undo-aware GameObject |
| `POST` | `/v1/objects/update` | Rename, activate, or change the local transform of an object |
| `POST` | `/v1/objects/delete` | Delete a GameObject through Unity Undo |
| `POST` | `/v1/components/list` | List component types and duplicate-component indices |
| `POST` | `/v1/components/add` | Add a component through Unity Undo |
| `POST` | `/v1/components/remove` | Remove a component through Unity Undo |
| `POST` | `/v1/properties/get` | Read visible serialized properties on a component |
| `POST` | `/v1/properties/set` | Set a supported serialized property value |
| `POST` | `/v1/prefabs/inspect` | Inspect a prefab asset hierarchy without reading YAML |
| `POST` | `/v1/prefabs/instance` | Inspect a scene object's prefab source and status |
| `POST` | `/v1/prefabs/instantiate` | Instantiate a prefab in the active scene |
| `POST` | `/v1/prefabs/save` | Save and connect a scene object as a prefab |
| `POST` | `/v1/prefabs/apply` | Apply an instance to its prefab asset |
| `POST` | `/v1/prefabs/revert` | Revert a prefab instance through Unity APIs |
| `POST` | `/v1/ui-toolkit/tree` | Inspect a bounded live or source-cloned UIDocument visual tree |
| `POST` | `/v1/playmode` | Request `play`, `stop`, or `toggle` |
| `POST` | `/v1/menu/execute` | Execute an allowlisted `Tools/Limen/*` menu command |
| `POST` | `/v1/scenes/open` | Open a validated `Assets/*.unity` scene |
| `POST` | `/v1/scenes/save` | Save the active scene |
| `POST` | `/v1/save-all` | Save all loaded scenes and assets |

Example without the shell client:

```sh
unity_project="/absolute/path/to/UnityProject"
bridge_token=$(<"$unity_project/Library/LimenBridge/token")
bridge_port=$(<"$unity_project/Library/LimenBridge/port")
curl --noproxy 127.0.0.1 -sS \
  -H "X-Limen-Bridge-Token: $bridge_token" \
  "http://127.0.0.1:$bridge_port/v1/status"
```

### Request bodies

Asset search:

```json
{
  "filter": "t:Scene Menu",
  "folders": ["Assets/Levels"],
  "limit": 100
}
```

Play Mode:

```json
{ "state": "play" }
```

Menu command (requires a matching command supplied by your project; none are bundled):

```json
{ "path": "Tools/Limen/Your Project Command" }
```

Scene open:

```json
{
  "path": "Assets/Levels/Menu/Menu.unity",
  "additive": false
}
```

### Scene-object targeting

Every object/component/property command accepts either:

```json
{ "globalObjectId": "GlobalObjectId_V1-..." }
```

or:

```json
{ "objectPath": "Environment/Contract Terminal" }
```

GlobalObjectIds are preferred after discovery because hierarchy paths can be
ambiguous when siblings have identical names. A newly created object in an
unsaved scene may not have a durable GlobalObjectId until the scene is saved.

Object search:

```json
{
  "filter": "Terminal",
  "componentType": "UnityEngine.BoxCollider",
  "limit": 100
}
```

Object creation and update:

```json
{
  "name": "Contract Terminal",
  "parentPath": "Environment",
  "hasActive": true,
  "active": true,
  "hasLocalPosition": true,
  "localPosition": [2, 1, 4],
  "hasLocalEulerAngles": true,
  "localEulerAngles": [0, 90, 0],
  "hasLocalScale": true,
  "localScale": [1, 1, 1]
}
```

For updates, provide the target plus only the fields to change. `newName`
renames an existing object. The `has*` fields distinguish an omitted value from
an intentional `false` or zero vector. `expectedName`, `hasExpectedActive`, and
`expectedActive` provide optimistic preconditions for updates and deletes.

### Components and serialized properties

Component commands use the fully-qualified C# type name. When an object has
multiple components of the same type, use the zero-based `componentIndex`
reported by `/v1/components/list`.

```json
{
  "objectPath": "Environment/Contract Terminal",
  "componentType": "UnityEngine.BoxCollider",
  "componentIndex": 0
}
```

Property setting uses the same target fields plus `propertyPath`. Supported
serialized values are Boolean (`boolValue`), Integer and Enum (`intValue`, or
an enum name in `value`), Float (`value` using invariant decimal notation),
String (`value`), Vector2/3/4 and Color (`values`), and ObjectReference
(`assetPath` or a GlobalObjectId in `value`). Complex arrays and managed
references remain read-only.

```json
{
  "objectPath": "Environment/Contract Terminal",
  "componentType": "UnityEngine.BoxCollider",
  "propertyPath": "m_IsTrigger",
  "boolValue": true
}
```

Use `/v1/properties/get` before setting a field; Unity's serialized property
path can differ from the public C# member name. Supply `propertyPath` to return
exactly one property, or `filter` to return only matching paths/display names.
Property results include editability, tooltip, array metadata, and enum choices.
Property writes may include `expectedValue`, using the string returned by the
property query, to reject a stale overwrite.

### Atomic batches and dry runs

`POST /v1/batch` accepts at most 100 scene mutations. Supported commands are
`object.create`, `object.update`, `object.delete`, `component.add`,
`component.remove`, and `property.set`. A successful batch becomes one Unity
Undo group. Any failed operation rolls back the whole group.

Created objects may declare `referenceId`; later operations target them with an
`objectPath` or `parentPath` such as `$terminal`. Set `dryRun` to execute the
same validation and Unity operations and then roll the Undo group back.

```json
{
  "undoName": "Create contract terminal",
  "dryRun": true,
  "operations": [
    {
      "command": "object.create",
      "referenceId": "terminal",
      "name": "Contract Terminal",
      "parentPath": "Environment"
    },
    {
      "command": "component.add",
      "objectPath": "$terminal",
      "componentType": "UnityEngine.BoxCollider"
    },
    {
      "command": "property.set",
      "objectPath": "$terminal",
      "componentType": "UnityEngine.BoxCollider",
      "propertyPath": "m_IsTrigger",
      "boolValue": true
    }
  ]
}
```

Prefab asset mutation is deliberately excluded from batches because applying or
overwriting a prefab is a different persistence boundary from scene Undo.

### Prefabs and asset references

Prefab inspection returns a bounded object/component hierarchy directly from
Unity's loaded prefab asset. Prefab instance inspection reports its status and
nearest source asset. Saving refuses to overwrite an existing prefab unless
`overwrite` is explicitly true; applying and reverting require an existing
scene instance.

Asset dependency lookup accepts `assetPath`, `recursive`, `reverse`, `folders`,
and `limit`. Reverse lookup can be expensive, so scope `folders` whenever
possible. Responses remain bounded and return GUID, path, and main asset type.

Component type discovery accepts `filter` and `limit`, and reports assembly,
namespace, whether the type is safely addable, `DisallowMultipleComponent`, and
required component constraints. Inspect a real component's filtered serialized
properties to discover instance-specific property paths and values.

### UI Toolkit logical inspection

`POST /v1/ui-toolkit/tree` targets either the GameObject containing a
`UIDocument` by `globalObjectId`/`objectPath`, or an imported `Assets/*.uxml`
`VisualTreeAsset` by `assetPath`. It returns a bounded logical element tree with
names, types, classes, short text, child counts, picking mode, visibility,
opacity, and layout/world bounds.

When a live panel exists, `uiLive` is true and resolved layout comes from that
panel. In Edit Mode, if no live root exists, the bridge clones the assigned
`VisualTreeAsset` and reports its logical structure with `uiLive=false`; detached
layout values are not authoritative. Use `filter` and `limit` to avoid dumping a
large visual tree. This is structural inspection only and is not visual approval.

## Recommended Codex iteration loop

1. Keep the target project open in Unity 2022.3.
2. Modify C#, UXML, USS, or other source assets normally.
3. Run `~/Projects/UnityEditorBridge/Tools~/unity-bridge --project /path/to/UnityProject refresh` only when Unity has not already
   noticed the filesystem change. Do not force a build after every edit.
4. Run `~/Projects/UnityEditorBridge/Tools~/unity-bridge --project /path/to/UnityProject wait-ready 120` before the next live mutation.
5. Inspect logs, then use object/component/property commands against the live
   scene.
6. Save explicitly after a coherent set of mutations.

This bridge supplies typed live-Editor operations independently of the official
Unity CLI. It does not require a Unity CLI installation or a Unity 6 connected
Editor pipeline. Project-specific validation helpers are not bundled; the menu
route only invokes existing commands under its `Tools/Limen/*` allowlist.
Use filtered object, asset, component, property, and log queries for discovery.

## Refresh jobs and domain reloads

`POST /v1/assets/refresh` returns a job ID before starting the refresh. Its job
journal is written to `Library/LimenBridge/jobs.json`. Refresh execution uses a
bridge-owned next-update queue rather than `EditorApplication.delayCall`, whose
one-shot callback can be lost during chained imports in Unity 2022.3.

Only one refresh may be active or queued. Additional requests return the same
job instead of creating duplicates. After package/domain initialization, the
bridge recovers an existing persisted `queued` refresh automatically. A single
refresh satisfies every older queued refresh record, so a historical backlog is
advanced together rather than replaying redundant imports.

`wait-ready` tolerates the temporary loss and recreation of the discovery files
and HTTP listener during a domain reload. It rereads the active port and token
on every retry, so it can reconnect even if the bridge moves to another port.

Before calling `AssetDatabase.Refresh`, the job becomes
`awaiting-domain-reload`. If compilation reloads the Editor domain, the newly
loaded bridge marks that job complete. If no reload occurs, the original bridge
marks it complete after `AssetDatabase.Refresh` returns. Poll `/v1/jobs/{id}`
after reconnecting to the port recorded in the discovery directory.

## Safety rules

- There is no arbitrary C# or shell evaluation endpoint.
- Unity API work is queued onto `EditorApplication.update`.
- Main-thread requests time out after 30 seconds rather than blocking forever.
- Request bodies are capped at 64 KiB.
- Non-additive scene opening is rejected while any loaded scene is dirty.
- Scene paths must be normalized `Assets/*.unity` paths and cannot traverse
  directories.
- Menu execution is restricted to `Tools/Limen/*`.
- Refresh, scene mutation, and menu commands reject unsafe Play Mode states.
- Scene mutations participate in Unity Undo and mark their owning scene dirty.
- Stable GlobalObjectIds are returned for targeting; hierarchy paths are a convenience fallback.
- Serialized property writes are limited to explicit, supported value types.
- Batches contain at most 100 scene operations and roll back as a unit on error
  or dry-run completion.
- Prefab overwrites require an explicit flag and prefab asset mutations are not
  mixed into scene batches.
- UI Toolkit, dependency, type, property, and diagnostic responses are bounded
  and support targeted filters or cursors.
- The hierarchy response is capped at 5,000 objects and reports truncation.
- Asset searches return at most 1,000 results.

The bridge deliberately provides no screenshot or visual-approval command.
Visual checks remain a developer-owned step.

## Extending the bridge

Add a request field or response DTO in `BridgeDtos.cs`, implement the operation
in the relevant `Bridge*Operations.cs` module, and register an explicit
method/route pair in `LimenEditorBridge.HandleContextAsync`.

New mutating commands should:

1. Reject compilation, updating, or Play Mode states that make the operation
   unsafe.
2. Validate all asset paths and object targets before mutation.
3. Refuse to discard unsaved scene data.
4. Return a typed JSON response.
5. Use a persistent job when an operation may trigger domain reload.
6. Remain bounded to project operations; do not add general evaluation.

When the public command surface, safety contract, discovery format, or job
lifecycle changes, update this document in the same change.

## Troubleshooting

If the client reports that the bridge is unavailable:

- Confirm the open Editor is Unity 2022.3 and its project path matches `--project`.
- Fix compiler errors; Editor packages cannot load while compilation is broken.
- Wait for the current import/domain reload to finish.
- Check the Unity Console for `Limen Editor Bridge` startup errors.
- Confirm `<project>/Library/LimenBridge/port` and `token` exist.

If a request returns a main-thread timeout, check whether Unity is displaying a
modal dialog or is blocked by an import. Resolve it in the Editor and retry.
