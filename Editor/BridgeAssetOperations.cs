using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Limen.EditorBridge
{
    internal static class BridgeAssetOperations
    {
        private const int MaximumAssets = 1000;
        private const int MaximumComponentTypes = 1000;
        private const int MaximumPrefabObjects = 1000;

        public static BridgeResponse GetDependencies(BridgeRequest request)
        {
            string assetPath = NormalizeAssetPath(request.assetPath ?? request.path);
            if (assetPath == null || AssetDatabase.LoadMainAssetAtPath(assetPath) == null)
                return BridgeResponse.Failure("A valid existing Assets path is required.");

            int limit = Mathf.Clamp(request.limit <= 0 ? 100 : request.limit, 1, MaximumAssets);
            IEnumerable<string> paths = request.reverse
                ? FindReverseDependencies(assetPath, request.folders)
                : AssetDatabase.GetDependencies(assetPath, request.recursive).Where(path => path != assetPath);
            List<string> matches = paths.Distinct().OrderBy(path => path, StringComparer.Ordinal).Take(limit + 1).ToList();
            BridgeResponse response = BridgeResponse.Success(request.reverse ? "Reverse dependencies found." : "Dependencies found.");
            response.truncated = matches.Count > limit;
            response.assets = matches.Take(limit).Select(ToAssetInfo).ToArray();
            return response;
        }

        public static BridgeResponse FindComponentTypes(BridgeRequest request)
        {
            string filter = request.filter?.Trim() ?? string.Empty;
            int limit = Mathf.Clamp(request.limit <= 0 ? 100 : request.limit, 1, MaximumComponentTypes);
            List<Type> matches = TypeCache.GetTypesDerivedFrom<Component>()
                .Where(type => string.IsNullOrEmpty(filter) ||
                    type.FullName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .Take(limit + 1)
                .ToList();

            BridgeResponse response = BridgeResponse.Success();
            response.truncated = matches.Count > limit;
            response.componentTypes = matches.Take(limit).Select(ToComponentTypeInfo).ToArray();
            return response;
        }

        public static BridgeResponse InspectPrefab(BridgeRequest request)
        {
            string assetPath = NormalizePrefabPath(request.assetPath ?? request.path);
            GameObject prefab = assetPath == null ? null : AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null) return BridgeResponse.Failure("A valid existing prefab asset path is required.");
            int limit = Mathf.Clamp(request.limit <= 0 ? 100 : request.limit, 1, MaximumPrefabObjects);
            List<GameObject> objects = BridgeSceneOperations.DescendantsAndSelf(prefab.transform).Take(limit + 1).ToList();
            BridgeResponse response = BridgeResponse.Success();
            response.truncated = objects.Count > limit;
            response.prefab = new BridgePrefabInfo
            {
                assetPath = assetPath,
                guid = AssetDatabase.AssetPathToGUID(assetPath),
                instanceStatus = "Asset",
                sourceAssetPath = assetPath,
                root = BridgeSceneOperations.ToObjectInfo(prefab),
                objects = objects.Take(limit).Select(BridgeSceneOperations.ToObjectInfo).ToArray()
            };
            return response;
        }

        public static BridgeResponse InspectPrefabInstance(BridgeRequest request)
        {
            GameObject gameObject = BridgeSceneOperations.ResolveGameObject(request);
            if (gameObject == null) return BridgeResponse.Failure("Scene object was not found.");
            GameObject root = PrefabUtility.GetNearestPrefabInstanceRoot(gameObject);
            if (root == null) return BridgeResponse.Failure("The target is not part of a prefab instance.");
            string sourcePath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root) ?? string.Empty;
            BridgeResponse response = BridgeResponse.Success();
            response.prefab = new BridgePrefabInfo
            {
                assetPath = sourcePath,
                guid = AssetDatabase.AssetPathToGUID(sourcePath),
                instanceStatus = PrefabUtility.GetPrefabInstanceStatus(root).ToString(),
                sourceAssetPath = sourcePath,
                root = BridgeSceneOperations.ToObjectInfo(root),
                objects = Array.Empty<BridgeObjectInfo>()
            };
            return response;
        }

        public static BridgeResponse InstantiatePrefab(BridgeRequest request)
        {
            BridgeResponse unsafeResponse = BridgeSceneOperations.RejectUnsafeMutation();
            if (unsafeResponse != null) return unsafeResponse;
            string assetPath = NormalizePrefabPath(request.assetPath ?? request.path);
            GameObject prefab = assetPath == null ? null : AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null) return BridgeResponse.Failure("A valid existing prefab asset path is required.");
            Transform parent = BridgeSceneOperations.ResolveParent(request);
            if ((!string.IsNullOrWhiteSpace(request.parentGlobalObjectId) || !string.IsNullOrWhiteSpace(request.parentPath)) && parent == null)
                return BridgeResponse.Failure("Parent object was not found.");

            GameObject instance = parent == null
                ? PrefabUtility.InstantiatePrefab(prefab) as GameObject
                : PrefabUtility.InstantiatePrefab(prefab, parent) as GameObject;
            if (instance == null) return BridgeResponse.Failure("Unity could not instantiate the prefab.");
            Undo.RegisterCreatedObjectUndo(instance, "Limen Bridge Instantiate Prefab");
            BridgeSceneOperations.ApplyTransform(request, instance.transform);
            BridgeSceneOperations.MarkChanged(instance);
            Selection.activeGameObject = instance;
            BridgeResponse response = BridgeResponse.Success($"Instantiated {assetPath}.");
            response.sceneObject = BridgeSceneOperations.ToObjectInfo(instance);
            return response;
        }

        public static BridgeResponse SavePrefab(BridgeRequest request)
        {
            BridgeResponse unsafeResponse = BridgeSceneOperations.RejectUnsafeMutation();
            if (unsafeResponse != null) return unsafeResponse;
            GameObject gameObject = BridgeSceneOperations.ResolveGameObject(request);
            if (gameObject == null) return BridgeResponse.Failure("Scene object was not found.");
            string assetPath = NormalizePrefabPath(request.assetPath);
            if (assetPath == null) return BridgeResponse.Failure("Destination must be a normalized Assets/*.prefab path.");
            string folder = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(folder) || !AssetDatabase.IsValidFolder(folder))
                return BridgeResponse.Failure($"Prefab destination folder does not exist: {folder}");
            if (!request.overwrite && AssetDatabase.LoadMainAssetAtPath(assetPath) != null)
                return BridgeResponse.Failure("The prefab already exists; set overwrite=true to replace it.");

            GameObject saved = PrefabUtility.SaveAsPrefabAssetAndConnect(gameObject, assetPath, InteractionMode.UserAction);
            if (saved == null) return BridgeResponse.Failure("Unity could not save the prefab.");
            AssetDatabase.SaveAssets();
            BridgeResponse response = BridgeResponse.Success($"Saved and connected prefab {assetPath}.");
            response.sceneObject = BridgeSceneOperations.ToObjectInfo(gameObject);
            return response;
        }

        public static BridgeResponse ApplyPrefab(BridgeRequest request)
        {
            BridgeResponse unsafeResponse = BridgeSceneOperations.RejectUnsafeMutation();
            if (unsafeResponse != null) return unsafeResponse;
            GameObject target = BridgeSceneOperations.ResolveGameObject(request);
            GameObject root = target == null ? null : PrefabUtility.GetNearestPrefabInstanceRoot(target);
            if (root == null) return BridgeResponse.Failure("The target is not part of a prefab instance.");
            PrefabUtility.ApplyPrefabInstance(root, InteractionMode.UserAction);
            AssetDatabase.SaveAssets();
            return BridgeResponse.Success($"Applied prefab instance {BridgeSceneOperations.GetHierarchyPath(root.transform)}.");
        }

        public static BridgeResponse RevertPrefab(BridgeRequest request)
        {
            BridgeResponse unsafeResponse = BridgeSceneOperations.RejectUnsafeMutation();
            if (unsafeResponse != null) return unsafeResponse;
            GameObject target = BridgeSceneOperations.ResolveGameObject(request);
            GameObject root = target == null ? null : PrefabUtility.GetNearestPrefabInstanceRoot(target);
            if (root == null) return BridgeResponse.Failure("The target is not part of a prefab instance.");
            PrefabUtility.RevertPrefabInstance(root, InteractionMode.UserAction);
            BridgeSceneOperations.MarkChanged(root);
            BridgeResponse response = BridgeResponse.Success($"Reverted prefab instance {BridgeSceneOperations.GetHierarchyPath(root.transform)}.");
            response.sceneObject = BridgeSceneOperations.ToObjectInfo(root);
            return response;
        }

        private static IEnumerable<string> FindReverseDependencies(string assetPath, string[] requestedFolders)
        {
            string[] folders = requestedFolders == null || requestedFolders.Length == 0
                ? new[] { "Assets/" }
                : requestedFolders.Select(NormalizeFolder).Where(folder => folder != null).ToArray();
            if (folders.Length == 0) yield break;
            foreach (string candidate in AssetDatabase.GetAllAssetPaths())
            {
                if (candidate == assetPath || !folders.Any(folder => candidate.StartsWith(folder, StringComparison.Ordinal))) continue;
                if (AssetDatabase.GetMainAssetTypeAtPath(candidate) == null) continue;
                if (AssetDatabase.GetDependencies(candidate, false).Contains(assetPath)) yield return candidate;
            }
        }

        private static BridgeComponentTypeInfo ToComponentTypeInfo(Type type)
        {
            var required = new List<string>();
            foreach (object attribute in type.GetCustomAttributes(typeof(RequireComponent), true))
            {
                foreach (FieldInfo field in attribute.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (field.FieldType == typeof(Type) && field.GetValue(attribute) is Type requiredType && requiredType != null)
                        required.Add(requiredType.FullName);
                }
            }
            return new BridgeComponentTypeInfo
            {
                type = type.FullName,
                assembly = type.Assembly.GetName().Name,
                nameSpace = type.Namespace ?? string.Empty,
                addable = !type.IsAbstract && !type.ContainsGenericParameters && !typeof(Transform).IsAssignableFrom(type),
                disallowMultiple = Attribute.IsDefined(type, typeof(DisallowMultipleComponent), true),
                requiredComponents = required.Distinct().OrderBy(name => name, StringComparer.Ordinal).ToArray()
            };
        }

        private static BridgeAssetInfo ToAssetInfo(string path)
        {
            Type type = AssetDatabase.GetMainAssetTypeAtPath(path);
            return new BridgeAssetInfo
            {
                guid = AssetDatabase.AssetPathToGUID(path),
                path = path,
                type = type?.FullName ?? string.Empty
            };
        }

        private static string NormalizeFolder(string path)
        {
            string normalized = NormalizeAssetPath(path);
            if (normalized == null || !AssetDatabase.IsValidFolder(normalized)) return null;
            return normalized.TrimEnd('/') + "/";
        }

        private static string NormalizePrefabPath(string path)
        {
            string normalized = NormalizeAssetPath(path);
            return normalized != null && normalized.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) ? normalized : null;
        }

        private static string NormalizeAssetPath(string path)
        {
            string normalized = path?.Trim().Replace('\\', '/');
            if (string.IsNullOrEmpty(normalized) || !normalized.StartsWith("Assets/", StringComparison.Ordinal) ||
                normalized.Contains("../") || normalized.EndsWith("/..", StringComparison.Ordinal))
                return null;
            return normalized;
        }
    }
}
