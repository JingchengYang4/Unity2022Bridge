using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Limen.EditorBridge
{
    [InitializeOnLoad]
    internal static class LimenEditorBridge
    {
        private const int FirstPort = 49222;
        private const int PortAttempts = 10;
        private const int MaximumRequestBytes = 64 * 1024;
        private const int MainThreadTimeoutMilliseconds = 30000;

        private sealed class MainThreadWork
        {
            public Func<BridgeResponse> execute;
            public TaskCompletionSource<BridgeResponse> completion;
        }

        private static readonly ConcurrentQueue<MainThreadWork> MainThreadQueue =
            new ConcurrentQueue<MainThreadWork>();
        private static readonly ConcurrentQueue<string> AssetRefreshQueue =
            new ConcurrentQueue<string>();

        private static HttpListener listener;
        private static CancellationTokenSource cancellation;
        private static string stateDirectory;
        private static string token;
        private static int port;
        private static bool shuttingDown;
        private static double nextStartAttempt;

        static LimenEditorBridge()
        {
            EditorApplication.delayCall += Start;
            EditorApplication.update += DrainMainThreadQueue;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
            Application.logMessageReceivedThreaded += BridgeState.RecordLog;
        }

        private static void Start()
        {
            if (shuttingDown || Application.isBatchMode) return;
            if (listener != null && listener.IsListening) return;

            if (listener != null)
            {
                try { listener.Close(); }
                catch (ObjectDisposedException) { }
                listener = null;
            }

            // A chained AssetDatabase refresh can consume the one-shot delayCall
            // used during domain initialization. DrainMainThreadQueue retries this
            // at a bounded cadence until the listener has actually started.
            nextStartAttempt = EditorApplication.timeSinceStartup + 1.0;

            string projectPath = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectPath)) return;
            stateDirectory = Path.Combine(projectPath, "Library", "LimenBridge");
            Directory.CreateDirectory(stateDirectory);
            BridgeState.Initialize(stateDirectory);
            string queuedRefresh = BridgeState.GetLatestJobId("assets.refresh", "queued");
            if (!string.IsNullOrEmpty(queuedRefresh)) AssetRefreshQueue.Enqueue(queuedRefresh);
            token = LoadOrCreateToken(Path.Combine(stateDirectory, "token"));

            for (int candidate = FirstPort; candidate < FirstPort + PortAttempts; candidate++)
            {
                var candidateListener = new HttpListener();
                candidateListener.Prefixes.Add($"http://127.0.0.1:{candidate}/");
                try
                {
                    candidateListener.Start();
                    listener = candidateListener;
                    port = candidate;
                    break;
                }
                catch (HttpListenerException)
                {
                    candidateListener.Close();
                }
                catch (SocketException)
                {
                    candidateListener.Close();
                }
            }

            if (listener == null)
            {
                Debug.LogError($"Limen Editor Bridge could not bind ports {FirstPort}-{FirstPort + PortAttempts - 1}.");
                return;
            }

            File.WriteAllText(Path.Combine(stateDirectory, "port"), port.ToString());
            File.WriteAllText(
                Path.Combine(stateDirectory, "instance.json"),
                $"{{\"port\":{port},\"processId\":{System.Diagnostics.Process.GetCurrentProcess().Id},\"projectPath\":\"{EscapeJson(projectPath)}\"}}");
            cancellation = new CancellationTokenSource();
            _ = Task.Run(() => AcceptLoopAsync(cancellation.Token));
            Debug.Log($"Limen Editor Bridge listening on 127.0.0.1:{port}.");
        }

        private static void Stop()
        {
            shuttingDown = true;
            cancellation?.Cancel();
            cancellation?.Dispose();
            cancellation = null;
            if (listener != null)
            {
                try { listener.Stop(); }
                catch (ObjectDisposedException) { }
                listener.Close();
                listener = null;
            }

            if (!string.IsNullOrEmpty(stateDirectory))
            {
                DeleteIfExists(Path.Combine(stateDirectory, "port"));
                DeleteIfExists(Path.Combine(stateDirectory, "instance.json"));
            }
        }

        private static async Task AcceptLoopAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested && listener != null && listener.IsListening)
            {
                try
                {
                    HttpListenerContext context = await listener.GetContextAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HandleContextAsync(context), stoppingToken);
                }
                catch (Exception exception) when (
                    stoppingToken.IsCancellationRequested ||
                    exception is HttpListenerException ||
                    exception is ObjectDisposedException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }

        private static async Task HandleContextAsync(HttpListenerContext context)
        {
            try
            {
                if (!IsAuthorized(context.Request))
                {
                    await WriteResponseAsync(context.Response, 401, BridgeResponse.Failure("Unauthorized.")).ConfigureAwait(false);
                    return;
                }

                if (context.Request.ContentLength64 > MaximumRequestBytes)
                {
                    await WriteResponseAsync(context.Response, 413, BridgeResponse.Failure("Request body is too large.")).ConfigureAwait(false);
                    return;
                }

                BridgeRequest request = await ReadRequestAsync(context.Request).ConfigureAwait(false);
                string path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;
                string method = context.Request.HttpMethod.ToUpperInvariant();
                BridgeResponse response;

                if (method == "GET" && path == "/v1/status")
                    response = await OnMainThreadAsync(GetStatus).ConfigureAwait(false);
                else if (method == "GET" && path == "/v1/commands")
                    response = GetCommands();
                else if (method == "GET" && path == "/v1/logs")
                    response = GetLogs(context.Request);
                else if (method == "GET" && path == "/v1/diagnostics/compilation")
                    response = await OnMainThreadAsync(() => GetCompilationDiagnostics(context.Request)).ConfigureAwait(false);
                else if (method == "GET" && path.StartsWith("/v1/jobs/", StringComparison.Ordinal))
                    response = GetJob(path.Substring("/v1/jobs/".Length));
                else if (method == "GET" && path == "/v1/scenes/hierarchy")
                    response = await OnMainThreadAsync(GetHierarchy).ConfigureAwait(false);
                else if (method == "GET" && path == "/v1/selection")
                    response = await OnMainThreadAsync(GetSelection).ConfigureAwait(false);
                else if (method == "POST" && path == "/v1/assets/find")
                    response = await OnMainThreadAsync(() => FindAssets(request)).ConfigureAwait(false);
                else if (method == "POST" && path == "/v1/assets/refresh")
                    response = await OnMainThreadAsync(QueueAssetRefresh).ConfigureAwait(false);
                else if (method == "POST" && path == "/v1/assets/dependencies")
                    response = await OnMainThreadAsync(() => BridgeAssetOperations.GetDependencies(request)).ConfigureAwait(false);
                else if (method == "POST" && path == "/v1/types/components")
                    response = await OnMainThreadAsync(() => BridgeAssetOperations.FindComponentTypes(request)).ConfigureAwait(false);
                else if (method == "POST" && path == "/v1/batch")
                    response = await OnMainThreadAsync(() => BridgeBatchOperations.Execute(request)).ConfigureAwait(false);
                else if (method == "POST" && path == "/v1/objects/find")
                    response = await OnMainThreadAsync(() => BridgeSceneOperations.FindObjects(request)).ConfigureAwait(false);
                else if (method == "POST" && path == "/v1/objects/get")
                    response = await OnMainThreadAsync(() => BridgeSceneOperations.GetObject(request)).ConfigureAwait(false);
                else if (method == "POST" && path == "/v1/objects/create")
                    response = await OnMainThreadAsync(() => BridgeSceneOperations.CreateObject(request)).ConfigureAwait(false);
                else if (method == "POST" && path == "/v1/objects/update")
                    response = await OnMainThreadAsync(() => BridgeSceneOperations.UpdateObject(request)).ConfigureAwait(false);
                else if (method == "POST" && path == "/v1/objects/delete")
                    response = await OnMainThreadAsync(() => BridgeSceneOperations.DeleteObject(request)).ConfigureAwait(false);
                else if (method == "POST" && path == "/v1/components/list")
                    response = await OnMainThreadAsync(() => BridgeSceneOperations.ListComponents(request)).ConfigureAwait(false);
                else if (method == "POST" && path == "/v1/components/add")
                    response = await OnMainThreadAsync(() => BridgeSceneOperations.AddComponent(request)).ConfigureAwait(false);
                else if (method == "POST" && path == "/v1/components/remove")
                    response = await OnMainThreadAsync(() => BridgeSceneOperations.RemoveComponent(request)).ConfigureAwait(false);
                else if (method == "POST" && path == "/v1/properties/get")
                    response = await OnMainThreadAsync(() => BridgeSceneOperations.GetProperties(request)).ConfigureAwait(false);
                else if (method == "POST" && path == "/v1/properties/set")
                    response = await OnMainThreadAsync(() => BridgeSceneOperations.SetProperty(request)).ConfigureAwait(false);
                else if (method == "POST" && path == "/v1/prefabs/inspect")
                    response = await OnMainThreadAsync(() => BridgeAssetOperations.InspectPrefab(request)).ConfigureAwait(false);
                else if (method == "POST" && path == "/v1/prefabs/instance")
                    response = await OnMainThreadAsync(() => BridgeAssetOperations.InspectPrefabInstance(request)).ConfigureAwait(false);
                else if (method == "POST" && path == "/v1/prefabs/instantiate")
                    response = await OnMainThreadAsync(() => BridgeAssetOperations.InstantiatePrefab(request)).ConfigureAwait(false);
                else if (method == "POST" && path == "/v1/prefabs/save")
                    response = await OnMainThreadAsync(() => BridgeAssetOperations.SavePrefab(request)).ConfigureAwait(false);
                else if (method == "POST" && path == "/v1/prefabs/apply")
                    response = await OnMainThreadAsync(() => BridgeAssetOperations.ApplyPrefab(request)).ConfigureAwait(false);
                else if (method == "POST" && path == "/v1/prefabs/revert")
                    response = await OnMainThreadAsync(() => BridgeAssetOperations.RevertPrefab(request)).ConfigureAwait(false);
                else if (method == "POST" && path == "/v1/ui-toolkit/tree")
                    response = await OnMainThreadAsync(() => BridgeUiToolkitOperations.InspectTree(request)).ConfigureAwait(false);
                else if (method == "POST" && path == "/v1/playmode")
                    response = await OnMainThreadAsync(() => SetPlayMode(request)).ConfigureAwait(false);
                else if (method == "POST" && path == "/v1/menu/execute")
                    response = await OnMainThreadAsync(() => ExecuteMenu(request)).ConfigureAwait(false);
                else if (method == "POST" && path == "/v1/scenes/open")
                    response = await OnMainThreadAsync(() => OpenScene(request)).ConfigureAwait(false);
                else if (method == "POST" && path == "/v1/scenes/save")
                    response = await OnMainThreadAsync(SaveActiveScene).ConfigureAwait(false);
                else if (method == "POST" && path == "/v1/save-all")
                    response = await OnMainThreadAsync(SaveAll).ConfigureAwait(false);
                else
                {
                    await WriteResponseAsync(context.Response, 404, BridgeResponse.Failure("Unknown bridge route.")).ConfigureAwait(false);
                    return;
                }

                await WriteResponseAsync(context.Response, response.ok ? 200 : 400, response).ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                await WriteResponseAsync(context.Response, 503, BridgeResponse.Failure(exception.Message)).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await WriteResponseAsync(context.Response, 500, BridgeResponse.Failure(exception.Message)).ConfigureAwait(false);
            }
        }

        private static async Task<BridgeRequest> ReadRequestAsync(HttpListenerRequest request)
        {
            if (!request.HasEntityBody) return new BridgeRequest();
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
            {
                string json = await reader.ReadToEndAsync().ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(json)
                    ? new BridgeRequest()
                    : JsonUtility.FromJson<BridgeRequest>(json) ?? new BridgeRequest();
            }
        }

        private static async Task<BridgeResponse> OnMainThreadAsync(Func<BridgeResponse> operation)
        {
            var completion = new TaskCompletionSource<BridgeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            MainThreadQueue.Enqueue(new MainThreadWork { execute = operation, completion = completion });
            Task finished = await Task.WhenAny(completion.Task, Task.Delay(MainThreadTimeoutMilliseconds)).ConfigureAwait(false);
            if (finished != completion.Task) throw new TimeoutException("Unity main-thread command timed out.");
            return await completion.Task.ConfigureAwait(false);
        }

        private static void DrainMainThreadQueue()
        {
            if (!shuttingDown &&
                (listener == null || !listener.IsListening) &&
                EditorApplication.timeSinceStartup >= nextStartAttempt)
            {
                Start();
            }

            if (AssetRefreshQueue.TryDequeue(out string refreshJobId))
            {
                while (AssetRefreshQueue.TryDequeue(out _)) { }
                RunAssetRefresh(refreshJobId);
            }

            int remaining = 32;
            while (remaining-- > 0 && MainThreadQueue.TryDequeue(out MainThreadWork work))
            {
                try { work.completion.TrySetResult(work.execute()); }
                catch (Exception exception) { work.completion.TrySetResult(BridgeResponse.Failure(exception.Message)); }
            }
        }

        private static BridgeResponse GetStatus()
        {
            Scene active = SceneManager.GetActiveScene();
            BridgeResponse response = BridgeResponse.Success("Unity Editor is reachable.");
            response.unityVersion = Application.unityVersion;
            response.projectPath = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
            response.activeScene = active.IsValid() ? active.path : string.Empty;
            response.isPlaying = EditorApplication.isPlaying;
            response.isPaused = EditorApplication.isPaused;
            response.isCompiling = EditorApplication.isCompiling;
            response.isUpdating = EditorApplication.isUpdating;
            response.apiVersion = "3";
            return response;
        }

        private static BridgeResponse GetCommands()
        {
            BridgeResponse response = BridgeResponse.Success("Limen Editor Bridge command catalog.");
            response.apiVersion = "3";
            response.commands = new[]
            {
                Command("GET", "/v1/status", false, "Editor, scene, compilation, and Play Mode state."),
                Command("GET", "/v1/commands", false, "Typed command discovery."),
                Command("GET", "/v1/logs", false, "Captured Editor logs."),
                Command("GET", "/v1/diagnostics/compilation", false, "Compilation state and error-class logs after a cursor."),
                Command("GET", "/v1/jobs/{id}", false, "Persistent asynchronous job status."),
                Command("GET", "/v1/scenes/hierarchy", false, "Active-scene hierarchy."),
                Command("GET", "/v1/selection", false, "Current Editor selection."),
                Command("POST", "/v1/assets/find", false, "Asset Database search."),
                Command("POST", "/v1/assets/refresh", true, "Refresh the Asset Database."),
                Command("POST", "/v1/assets/dependencies", false, "Find bounded direct, recursive, or reverse asset dependencies."),
                Command("POST", "/v1/types/components", false, "Discover component types and addability constraints."),
                Command("POST", "/v1/batch", true, "Apply or dry-run scene mutations as one atomic Undo group."),
                Command("POST", "/v1/objects/find", false, "Find objects in loaded scenes."),
                Command("POST", "/v1/objects/get", false, "Inspect one scene object."),
                Command("POST", "/v1/objects/create", true, "Create an Undo-aware scene object."),
                Command("POST", "/v1/objects/update", true, "Update object state and local transform."),
                Command("POST", "/v1/objects/delete", true, "Delete an object through Unity Undo."),
                Command("POST", "/v1/components/list", false, "List components and duplicate indices."),
                Command("POST", "/v1/components/add", true, "Add a component through Unity Undo."),
                Command("POST", "/v1/components/remove", true, "Remove a component through Unity Undo."),
                Command("POST", "/v1/properties/get", false, "Read visible serialized component properties."),
                Command("POST", "/v1/properties/set", true, "Set an allowlisted serialized value type."),
                Command("POST", "/v1/prefabs/inspect", false, "Inspect a prefab asset without reading YAML."),
                Command("POST", "/v1/prefabs/instance", false, "Inspect the prefab source and status of a scene instance."),
                Command("POST", "/v1/prefabs/instantiate", true, "Instantiate a prefab into the active scene."),
                Command("POST", "/v1/prefabs/save", true, "Save a scene object as a connected prefab."),
                Command("POST", "/v1/prefabs/apply", true, "Apply a prefab instance to its source asset."),
                Command("POST", "/v1/prefabs/revert", true, "Revert a prefab instance through Unity APIs."),
                Command("POST", "/v1/ui-toolkit/tree", false, "Inspect a bounded live or source-cloned UIDocument visual tree."),
                Command("POST", "/v1/playmode", true, "Change Play Mode state."),
                Command("POST", "/v1/menu/execute", true, "Execute an allowlisted Tools/Limen menu item."),
                Command("POST", "/v1/scenes/open", true, "Open a validated scene asset."),
                Command("POST", "/v1/scenes/save", true, "Save the active scene."),
                Command("POST", "/v1/save-all", true, "Save all loaded scenes and assets.")
            };
            return response;
        }

        private static BridgeCommandInfo Command(string method, string route, bool mutating, string description) =>
            new BridgeCommandInfo { method = method, route = route, mutating = mutating, description = description };

        private static BridgeResponse GetLogs(HttpListenerRequest request)
        {
            long.TryParse(request.QueryString["after"], out long after);
            int.TryParse(request.QueryString["limit"], out int limit);
            BridgeResponse response = BridgeResponse.Success();
            response.logs = BridgeState.GetLogs(after, limit, out long latestId);
            response.latestLogId = latestId;
            return response;
        }

        private static BridgeResponse GetCompilationDiagnostics(HttpListenerRequest request)
        {
            long.TryParse(request.QueryString["after"], out long after);
            int.TryParse(request.QueryString["limit"], out int requestedLimit);
            int limit = Mathf.Clamp(requestedLimit <= 0 ? 100 : requestedLimit, 1, 500);
            BridgeLogEntry[] logs = BridgeState.GetLogs(after, 500, out long latestId)
                .Where(entry => entry.type == LogType.Error.ToString() ||
                                entry.type == LogType.Exception.ToString() ||
                                entry.type == LogType.Assert.ToString())
                .Take(limit)
                .ToArray();
            BridgeResponse response = BridgeResponse.Success(EditorApplication.isCompiling
                ? "Unity is compiling."
                : logs.Length == 0 ? "Unity is not compiling and no new error-class logs were captured." : "Compilation diagnostics contain errors.");
            response.isCompiling = EditorApplication.isCompiling;
            response.isUpdating = EditorApplication.isUpdating;
            response.latestLogId = latestId;
            response.logs = logs;
            return response;
        }

        private static BridgeResponse GetJob(string id)
        {
            BridgeJobRecord job = BridgeState.GetJob(id);
            if (job == null) return BridgeResponse.Failure("Job was not found.");
            BridgeResponse response = BridgeResponse.Success();
            response.job = job;
            return response;
        }

        private static BridgeResponse QueueAssetRefresh()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return BridgeResponse.Failure("Asset refresh is unavailable while entering or running Play Mode.");

            BridgeJobRecord job = BridgeState.GetLatestActiveJob("assets.refresh");
            bool coalesced = job != null;
            if (job == null)
            {
                job = BridgeState.CreateJob("assets.refresh");
                AssetRefreshQueue.Enqueue(job.id);
            }
            BridgeResponse response = BridgeResponse.Success(coalesced
                ? "Asset refresh is already queued; returning the existing job."
                : "Asset refresh queued for the next Editor update.");
            response.jobId = job.id;
            response.job = job;
            return response;
        }

        private static void RunAssetRefresh(string jobId)
        {
            try
            {
                int jobs = BridgeState.UpdateJobs(
                    "assets.refresh",
                    "queued",
                    "awaiting-domain-reload",
                    "Refreshing the Asset Database.");
                if (jobs == 0) return;
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                BridgeState.UpdateJobs(
                    "assets.refresh",
                    "awaiting-domain-reload",
                    "complete",
                    "Asset Database refresh completed without a domain reload.");
            }
            catch (Exception exception)
            {
                BridgeState.UpdateJobs("assets.refresh", "awaiting-domain-reload", "failed", exception.Message);
                Debug.LogException(exception);
            }
        }

        private static BridgeResponse SetPlayMode(BridgeRequest request)
        {
            string state = (request.state ?? string.Empty).Trim().ToLowerInvariant();
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return BridgeResponse.Failure("Play Mode cannot change while Unity is compiling or updating.");
            if (state == "play") EditorApplication.isPlaying = true;
            else if (state == "stop") EditorApplication.isPlaying = false;
            else if (state == "toggle") EditorApplication.isPlaying = !EditorApplication.isPlaying;
            else return BridgeResponse.Failure("State must be 'play', 'stop', or 'toggle'.");
            return BridgeResponse.Success($"Play Mode command '{state}' accepted.");
        }

        private static BridgeResponse ExecuteMenu(BridgeRequest request)
        {
            string menuPath = request.path?.Trim() ?? string.Empty;
            if (!menuPath.StartsWith("Tools/Limen/", StringComparison.Ordinal))
                return BridgeResponse.Failure("Only Tools/Limen menu commands are allowed.");
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return BridgeResponse.Failure("Menu commands are unavailable while entering or running Play Mode.");
            return EditorApplication.ExecuteMenuItem(menuPath)
                ? BridgeResponse.Success($"Executed '{menuPath}'.")
                : BridgeResponse.Failure($"Unity could not execute '{menuPath}'.");
        }

        private static BridgeResponse OpenScene(BridgeRequest request)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                return BridgeResponse.Failure("Scenes cannot be opened while Unity is playing or compiling.");
            string assetPath = NormalizeAssetPath(request.path);
            if (assetPath == null || !assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                return BridgeResponse.Failure("Scene path must be a normalized Assets/*.unity path.");
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(assetPath) == null)
                return BridgeResponse.Failure($"Scene does not exist: {assetPath}");
            if (!request.additive && HasDirtyScenes())
                return BridgeResponse.Failure("A loaded scene is dirty; save it before opening another scene.");

            Scene opened = EditorSceneManager.OpenScene(
                assetPath,
                request.additive ? OpenSceneMode.Additive : OpenSceneMode.Single);
            return BridgeResponse.Success($"Opened {opened.path}.");
        }

        private static BridgeResponse SaveActiveScene()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return BridgeResponse.Failure("Scenes cannot be saved during Play Mode.");
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || string.IsNullOrEmpty(scene.path))
                return BridgeResponse.Failure("The active scene has no saved asset path.");
            return EditorSceneManager.SaveScene(scene)
                ? BridgeResponse.Success($"Saved {scene.path}.")
                : BridgeResponse.Failure($"Unity could not save {scene.path}.");
        }

        private static BridgeResponse SaveAll()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return BridgeResponse.Failure("Assets and scenes cannot be saved during Play Mode.");
            EditorSceneManager.SaveOpenScenes();
            AssetDatabase.SaveAssets();
            return BridgeResponse.Success("Saved open scenes and assets.");
        }

        private static BridgeResponse GetHierarchy()
        {
            const int maximumNodes = 5000;
            var items = new List<BridgeHierarchyItem>();
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid()) return BridgeResponse.Failure("There is no active scene.");
            foreach (GameObject root in scene.GetRootGameObjects())
                AddHierarchy(root.transform, root.name, items, maximumNodes);
            BridgeResponse response = BridgeResponse.Success();
            response.hierarchy = items.ToArray();
            response.truncated = items.Count >= maximumNodes;
            return response;
        }

        private static void AddHierarchy(Transform transform, string path, List<BridgeHierarchyItem> items, int maximum)
        {
            if (items.Count >= maximum) return;
            items.Add(new BridgeHierarchyItem
            {
                path = path,
                activeSelf = transform.gameObject.activeSelf,
                activeInHierarchy = transform.gameObject.activeInHierarchy,
                components = transform.gameObject.GetComponents<Component>()
                    .Select(component => component == null ? "<Missing Script>" : component.GetType().FullName)
                    .ToArray()
            });
            foreach (Transform child in transform)
                AddHierarchy(child, path + "/" + child.name, items, maximum);
        }

        private static BridgeResponse FindAssets(BridgeRequest request)
        {
            string filter = request.filter?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(filter)) return BridgeResponse.Failure("Asset search filter is required.");
            string[] folders = request.folders == null || request.folders.Length == 0
                ? new[] { "Assets" }
                : request.folders.Select(NormalizeAssetPath).Where(path => path != null).ToArray();
            if (folders.Length == 0) return BridgeResponse.Failure("No valid Assets folders were supplied.");

            int limit = Mathf.Clamp(request.limit <= 0 ? 100 : request.limit, 1, 1000);
            string[] guids = AssetDatabase.FindAssets(filter, folders);
            BridgeResponse response = BridgeResponse.Success();
            response.truncated = guids.Length > limit;
            response.assets = guids.Take(limit).Select(guid =>
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                Type type = AssetDatabase.GetMainAssetTypeAtPath(path);
                return new BridgeAssetInfo { guid = guid, path = path, type = type?.FullName ?? string.Empty };
            }).ToArray();
            return response;
        }

        private static BridgeResponse GetSelection()
        {
            UnityEngine.Object selected = Selection.activeObject;
            BridgeResponse response = BridgeResponse.Success();
            GameObject selectedGameObject = selected as GameObject ?? (selected as Component)?.gameObject;
            response.selection = selected == null ? null : new BridgeSelectionInfo
            {
                instanceId = selected.GetInstanceID(),
                name = selected.name,
                type = selected.GetType().FullName,
                assetPath = AssetDatabase.GetAssetPath(selected) ?? string.Empty,
                globalObjectId = selectedGameObject == null ? string.Empty : GlobalObjectId.GetGlobalObjectIdSlow(selectedGameObject).ToString(),
                objectPath = selectedGameObject == null ? string.Empty : GetObjectPath(selectedGameObject.transform)
            };
            return response;
        }

        private static string GetObjectPath(Transform transform)
        {
            var names = new Stack<string>();
            while (transform != null)
            {
                names.Push(transform.name);
                transform = transform.parent;
            }
            return string.Join("/", names.ToArray());
        }

        private static bool IsAuthorized(HttpListenerRequest request)
        {
            string supplied = request.Headers["X-Limen-Bridge-Token"];
            if (string.IsNullOrEmpty(supplied) || string.IsNullOrEmpty(token) || supplied.Length != token.Length)
                return false;
            int difference = 0;
            for (int index = 0; index < token.Length; index++) difference |= token[index] ^ supplied[index];
            return difference == 0;
        }

        private static async Task WriteResponseAsync(HttpListenerResponse response, int statusCode, BridgeResponse payload)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(payload));
            response.StatusCode = statusCode;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentEncoding = Encoding.UTF8;
            response.ContentLength64 = bytes.Length;
            response.Headers["Cache-Control"] = "no-store";
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            response.OutputStream.Close();
        }

        private static string LoadOrCreateToken(string path)
        {
            if (File.Exists(path))
            {
                string existing = File.ReadAllText(path).Trim();
                if (existing.Length >= 32) return existing;
            }
            string created = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            File.WriteAllText(path, created);
            return created;
        }

        private static bool HasDirtyScenes()
        {
            for (int index = 0; index < SceneManager.sceneCount; index++)
                if (SceneManager.GetSceneAt(index).isDirty) return true;
            return false;
        }

        private static string NormalizeAssetPath(string path)
        {
            string normalized = path?.Trim().Replace('\\', '/');
            if (string.IsNullOrEmpty(normalized) || !normalized.StartsWith("Assets/", StringComparison.Ordinal) ||
                normalized.Contains("../") || normalized.EndsWith("/..", StringComparison.Ordinal))
                return null;
            return normalized;
        }

        private static string EscapeJson(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static void DeleteIfExists(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
