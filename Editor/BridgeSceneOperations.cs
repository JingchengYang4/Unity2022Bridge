using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Limen.EditorBridge
{
    internal static class BridgeSceneOperations
    {
        private const int MaximumObjects = 1000;
        private const int MaximumProperties = 500;

        public static BridgeResponse FindObjects(BridgeRequest request)
        {
            string query = request.filter?.Trim() ?? string.Empty;
            string requiredType = request.componentType?.Trim() ?? string.Empty;
            int limit = Mathf.Clamp(request.limit <= 0 ? 100 : request.limit, 1, MaximumObjects);
            Type componentType = string.IsNullOrEmpty(requiredType) ? null : ResolveComponentType(requiredType);
            if (!string.IsNullOrEmpty(requiredType) && componentType == null)
                return BridgeResponse.Failure($"Component type was not found: {requiredType}");

            IEnumerable<GameObject> candidates = LoadedSceneObjects();
            if (!string.IsNullOrEmpty(query))
            {
                candidates = candidates.Where(gameObject =>
                    gameObject.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    GetHierarchyPath(gameObject.transform).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            if (componentType != null)
                candidates = candidates.Where(gameObject => gameObject.GetComponent(componentType) != null);

            List<GameObject> matches = candidates.Take(limit + 1).ToList();
            BridgeResponse response = BridgeResponse.Success();
            response.truncated = matches.Count > limit;
            response.sceneObjects = matches.Take(limit).Select(ToObjectInfo).ToArray();
            return response;
        }

        public static BridgeResponse GetObject(BridgeRequest request)
        {
            GameObject gameObject = ResolveGameObject(request);
            if (gameObject == null) return BridgeResponse.Failure(TargetNotFound(request));
            BridgeResponse response = BridgeResponse.Success();
            response.sceneObject = ToObjectInfo(gameObject);
            return response;
        }

        public static BridgeResponse CreateObject(BridgeRequest request)
        {
            BridgeResponse unsafeResponse = RejectUnsafeMutation();
            if (unsafeResponse != null) return unsafeResponse;
            string name = request.name?.Trim();
            if (string.IsNullOrEmpty(name)) return BridgeResponse.Failure("Object name is required.");

            Transform parent = ResolveParent(request);
            if ((!string.IsNullOrWhiteSpace(request.parentGlobalObjectId) || !string.IsNullOrWhiteSpace(request.parentPath)) && parent == null)
                return BridgeResponse.Failure("Parent object was not found.");

            var gameObject = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(gameObject, "Limen Bridge Create GameObject");
            if (parent != null)
                Undo.SetTransformParent(gameObject.transform, parent, "Limen Bridge Parent GameObject");
            else
                SceneManager.MoveGameObjectToScene(gameObject, SceneManager.GetActiveScene());

            ApplyTransform(request, gameObject.transform);
            if (request.hasActive) gameObject.SetActive(request.active);
            MarkChanged(gameObject);
            Selection.activeGameObject = gameObject;

            BridgeResponse response = BridgeResponse.Success($"Created {GetHierarchyPath(gameObject.transform)}.");
            response.sceneObject = ToObjectInfo(gameObject);
            return response;
        }

        public static BridgeResponse UpdateObject(BridgeRequest request)
        {
            BridgeResponse unsafeResponse = RejectUnsafeMutation();
            if (unsafeResponse != null) return unsafeResponse;
            GameObject gameObject = ResolveGameObject(request);
            if (gameObject == null) return BridgeResponse.Failure(TargetNotFound(request));
            string preconditionError = CheckObjectPreconditions(gameObject, request);
            if (preconditionError != null) return BridgeResponse.Failure(preconditionError);

            Undo.RecordObject(gameObject, "Limen Bridge Update GameObject");
            Undo.RecordObject(gameObject.transform, "Limen Bridge Update Transform");
            if (!string.IsNullOrWhiteSpace(request.newName)) gameObject.name = request.newName.Trim();
            if (request.hasActive) gameObject.SetActive(request.active);
            ApplyTransform(request, gameObject.transform);
            MarkChanged(gameObject);

            BridgeResponse response = BridgeResponse.Success($"Updated {GetHierarchyPath(gameObject.transform)}.");
            response.sceneObject = ToObjectInfo(gameObject);
            return response;
        }

        public static BridgeResponse DeleteObject(BridgeRequest request)
        {
            BridgeResponse unsafeResponse = RejectUnsafeMutation();
            if (unsafeResponse != null) return unsafeResponse;
            GameObject gameObject = ResolveGameObject(request);
            if (gameObject == null) return BridgeResponse.Failure(TargetNotFound(request));
            string preconditionError = CheckObjectPreconditions(gameObject, request);
            if (preconditionError != null) return BridgeResponse.Failure(preconditionError);
            string path = GetHierarchyPath(gameObject.transform);
            Undo.DestroyObjectImmediate(gameObject);
            return BridgeResponse.Success($"Deleted {path}. Use Unity Undo to restore it.");
        }

        public static BridgeResponse ListComponents(BridgeRequest request)
        {
            GameObject gameObject = ResolveGameObject(request);
            if (gameObject == null) return BridgeResponse.Failure(TargetNotFound(request));
            Component[] components = gameObject.GetComponents<Component>();
            var typeCounts = new Dictionary<string, int>();
            BridgeResponse response = BridgeResponse.Success();
            response.components = components.Select(component =>
            {
                string type = component == null ? "<Missing Script>" : component.GetType().FullName;
                typeCounts.TryGetValue(type, out int index);
                typeCounts[type] = index + 1;
                return new BridgeComponentInfo { type = type, index = index, missing = component == null };
            }).ToArray();
            return response;
        }

        public static BridgeResponse AddComponent(BridgeRequest request)
        {
            BridgeResponse unsafeResponse = RejectUnsafeMutation();
            if (unsafeResponse != null) return unsafeResponse;
            GameObject gameObject = ResolveGameObject(request);
            if (gameObject == null) return BridgeResponse.Failure(TargetNotFound(request));
            Type type = ResolveComponentType(request.componentType);
            if (type == null) return BridgeResponse.Failure($"Component type was not found: {request.componentType}");
            Component component = Undo.AddComponent(gameObject, type);
            if (component == null) return BridgeResponse.Failure($"Unity could not add {type.FullName}.");
            MarkChanged(gameObject);
            return BridgeResponse.Success($"Added {type.FullName} to {GetHierarchyPath(gameObject.transform)}.");
        }

        public static BridgeResponse RemoveComponent(BridgeRequest request)
        {
            BridgeResponse unsafeResponse = RejectUnsafeMutation();
            if (unsafeResponse != null) return unsafeResponse;
            GameObject gameObject = ResolveGameObject(request);
            if (gameObject == null) return BridgeResponse.Failure(TargetNotFound(request));
            Component component = ResolveComponent(gameObject, request.componentType, request.componentIndex);
            if (component == null) return BridgeResponse.Failure("Component was not found.");
            if (component is Transform) return BridgeResponse.Failure("Transform components cannot be removed.");
            string typeName = component.GetType().FullName;
            Undo.DestroyObjectImmediate(component);
            MarkChanged(gameObject);
            return BridgeResponse.Success($"Removed {typeName} from {GetHierarchyPath(gameObject.transform)}.");
        }

        public static BridgeResponse GetProperties(BridgeRequest request)
        {
            GameObject gameObject = ResolveGameObject(request);
            if (gameObject == null) return BridgeResponse.Failure(TargetNotFound(request));
            Component component = ResolveComponent(gameObject, request.componentType, request.componentIndex);
            if (component == null) return BridgeResponse.Failure("Component was not found.");

            var serializedObject = new SerializedObject(component);
            if (!string.IsNullOrWhiteSpace(request.propertyPath))
            {
                SerializedProperty selected = serializedObject.FindProperty(request.propertyPath.Trim());
                if (selected == null) return BridgeResponse.Failure($"Serialized property was not found: {request.propertyPath}");
                BridgeResponse selectedResponse = BridgeResponse.Success();
                selectedResponse.properties = new[] { ToPropertyInfo(selected) };
                return selectedResponse;
            }

            string filter = request.filter?.Trim() ?? string.Empty;
            SerializedProperty iterator = serializedObject.GetIterator();
            var properties = new List<BridgePropertyInfo>();
            bool enterChildren = true;
            while (iterator.NextVisible(enterChildren) && properties.Count < MaximumProperties)
            {
                enterChildren = false;
                if (string.IsNullOrEmpty(filter) ||
                    iterator.propertyPath.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    iterator.displayName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    properties.Add(ToPropertyInfo(iterator));
            }

            BridgeResponse response = BridgeResponse.Success();
            response.properties = properties.ToArray();
            response.truncated = properties.Count >= MaximumProperties;
            return response;
        }

        public static BridgeResponse SetProperty(BridgeRequest request)
        {
            BridgeResponse unsafeResponse = RejectUnsafeMutation();
            if (unsafeResponse != null) return unsafeResponse;
            GameObject gameObject = ResolveGameObject(request);
            if (gameObject == null) return BridgeResponse.Failure(TargetNotFound(request));
            Component component = ResolveComponent(gameObject, request.componentType, request.componentIndex);
            if (component == null) return BridgeResponse.Failure("Component was not found.");
            if (string.IsNullOrWhiteSpace(request.propertyPath)) return BridgeResponse.Failure("Property path is required.");

            var serializedObject = new SerializedObject(component);
            SerializedProperty property = serializedObject.FindProperty(request.propertyPath.Trim());
            if (property == null) return BridgeResponse.Failure($"Serialized property was not found: {request.propertyPath}");
            if (property.propertyPath == "m_Script") return BridgeResponse.Failure("The script reference cannot be changed.");
            if (request.expectedValue != null && PropertyValue(property) != request.expectedValue)
                return BridgeResponse.Failure($"Property precondition failed: expected '{request.expectedValue}', found '{PropertyValue(property)}'.");
            Undo.RecordObject(component, "Limen Bridge Set Property");
            string error = AssignProperty(property, request);
            if (error != null) return BridgeResponse.Failure(error);
            serializedObject.ApplyModifiedProperties();
            PrefabUtility.RecordPrefabInstancePropertyModifications(component);
            MarkChanged(gameObject);
            return BridgeResponse.Success($"Set {request.propertyPath} on {component.GetType().FullName}.");
        }

        internal static BridgeResponse RejectUnsafeMutation()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return BridgeResponse.Failure("Scene mutation is unavailable while entering or running Play Mode.");
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return BridgeResponse.Failure("Scene mutation is unavailable while Unity is compiling or updating.");
            if (!SceneManager.GetActiveScene().IsValid())
                return BridgeResponse.Failure("There is no active scene.");
            return null;
        }

        internal static void ApplyTransform(BridgeRequest request, Transform transform)
        {
            if (request.hasLocalPosition && TryVector3(request.localPosition, out Vector3 position)) transform.localPosition = position;
            if (request.hasLocalEulerAngles && TryVector3(request.localEulerAngles, out Vector3 rotation)) transform.localEulerAngles = rotation;
            if (request.hasLocalScale && TryVector3(request.localScale, out Vector3 scale)) transform.localScale = scale;
            PrefabUtility.RecordPrefabInstancePropertyModifications(transform);
        }

        private static bool TryVector3(float[] values, out Vector3 result)
        {
            if (values != null && values.Length == 3)
            {
                result = new Vector3(values[0], values[1], values[2]);
                return true;
            }
            result = default;
            return false;
        }

        private static string AssignProperty(SerializedProperty property, BridgeRequest request)
        {
            float[] values = request.values ?? Array.Empty<float>();
            switch (property.propertyType)
            {
                case SerializedPropertyType.Boolean: property.boolValue = request.boolValue; return null;
                case SerializedPropertyType.Integer: property.intValue = request.intValue; return null;
                case SerializedPropertyType.Float:
                    if (!float.TryParse(request.value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number))
                        return "Float properties require value as an invariant-culture number string.";
                    property.floatValue = number; return null;
                case SerializedPropertyType.String: property.stringValue = request.value ?? string.Empty; return null;
                case SerializedPropertyType.Enum:
                    if (!string.IsNullOrEmpty(request.value))
                    {
                        int enumIndex = Array.FindIndex(property.enumNames, item => item == request.value);
                        if (enumIndex < 0) return $"Unknown enum value '{request.value}'.";
                        property.enumValueIndex = enumIndex;
                    }
                    else property.enumValueIndex = request.intValue;
                    return null;
                case SerializedPropertyType.Vector2:
                    if (values.Length != 2) return "Vector2 properties require two values.";
                    property.vector2Value = new Vector2(values[0], values[1]); return null;
                case SerializedPropertyType.Vector3:
                    if (values.Length != 3) return "Vector3 properties require three values.";
                    property.vector3Value = new Vector3(values[0], values[1], values[2]); return null;
                case SerializedPropertyType.Vector4:
                    if (values.Length != 4) return "Vector4 properties require four values.";
                    property.vector4Value = new Vector4(values[0], values[1], values[2], values[3]); return null;
                case SerializedPropertyType.Color:
                    if (values.Length != 4) return "Color properties require RGBA values.";
                    property.colorValue = new Color(values[0], values[1], values[2], values[3]); return null;
                case SerializedPropertyType.ObjectReference:
                    property.objectReferenceValue = ResolveObjectReference(request);
                    if ((!string.IsNullOrEmpty(request.assetPath) || !string.IsNullOrEmpty(request.value)) && property.objectReferenceValue == null)
                        return "Object reference was not found. Use assetPath or a GlobalObjectId in value.";
                    return null;
                default:
                    return $"Property type {property.propertyType} is read-only through the bridge.";
            }
        }

        private static UnityEngine.Object ResolveObjectReference(BridgeRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.assetPath))
                return AssetDatabase.LoadMainAssetAtPath(request.assetPath.Trim());
            if (!string.IsNullOrWhiteSpace(request.value) && GlobalObjectId.TryParse(request.value.Trim(), out GlobalObjectId id))
                return GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id);
            return null;
        }

        private static string PropertyValue(SerializedProperty property)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Boolean: return property.boolValue.ToString();
                case SerializedPropertyType.Integer: return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Float: return property.floatValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.String: return property.stringValue ?? string.Empty;
                case SerializedPropertyType.Enum: return property.enumDisplayNames.Length > property.enumValueIndex && property.enumValueIndex >= 0 ? property.enumDisplayNames[property.enumValueIndex] : property.enumValueIndex.ToString();
                case SerializedPropertyType.Vector2: return property.vector2Value.ToString("R");
                case SerializedPropertyType.Vector3: return property.vector3Value.ToString("R");
                case SerializedPropertyType.Vector4: return property.vector4Value.ToString("R");
                case SerializedPropertyType.Color: return property.colorValue.ToString();
                case SerializedPropertyType.ObjectReference: return property.objectReferenceValue == null ? "null" : property.objectReferenceValue.name;
                default: return property.propertyType.ToString();
            }
        }

        private static BridgePropertyInfo ToPropertyInfo(SerializedProperty property) => new BridgePropertyInfo
        {
            path = property.propertyPath,
            displayName = property.displayName,
            type = property.propertyType.ToString(),
            value = PropertyValue(property),
            tooltip = property.tooltip ?? string.Empty,
            editable = property.editable && property.propertyPath != "m_Script",
            isArray = property.isArray,
            arraySize = property.isArray ? property.arraySize : 0,
            enumValues = property.propertyType == SerializedPropertyType.Enum ? property.enumNames : Array.Empty<string>()
        };

        private static string CheckObjectPreconditions(GameObject gameObject, BridgeRequest request)
        {
            if (request.expectedName != null && gameObject.name != request.expectedName)
                return $"Object precondition failed: expected name '{request.expectedName}', found '{gameObject.name}'.";
            if (request.hasExpectedActive && gameObject.activeSelf != request.expectedActive)
                return $"Object precondition failed: expected activeSelf={request.expectedActive}, found {gameObject.activeSelf}.";
            return null;
        }

        private static Component ResolveComponent(GameObject gameObject, string typeName, int index)
        {
            Type type = ResolveComponentType(typeName);
            if (type == null || index < 0) return null;
            Component[] matches = gameObject.GetComponents(type);
            return index < matches.Length ? matches[index] : null;
        }

        internal static Type ResolveComponentType(string typeName)
        {
            string wanted = typeName?.Trim();
            if (string.IsNullOrEmpty(wanted)) return null;
            return TypeCache.GetTypesDerivedFrom<Component>().FirstOrDefault(type =>
                       type.FullName == wanted || type.AssemblyQualifiedName == wanted) ??
                   (typeof(Component).FullName == wanted ? typeof(Component) : null);
        }

        internal static GameObject ResolveGameObject(BridgeRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.globalObjectId) &&
                GlobalObjectId.TryParse(request.globalObjectId.Trim(), out GlobalObjectId id))
            {
                GameObject resolved = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) as GameObject;
                if (resolved != null) return resolved;
            }
            return FindByPath(request.objectPath);
        }

        internal static Transform ResolveParent(BridgeRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.parentGlobalObjectId) &&
                GlobalObjectId.TryParse(request.parentGlobalObjectId.Trim(), out GlobalObjectId id))
            {
                GameObject resolved = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) as GameObject;
                if (resolved != null) return resolved.transform;
            }
            return FindByPath(request.parentPath)?.transform;
        }

        private static GameObject FindByPath(string path)
        {
            string normalized = path?.Trim().Trim('/');
            if (string.IsNullOrEmpty(normalized)) return null;
            foreach (Scene scene in LoadedScenes())
            {
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    if (root.name == normalized) return root;
                    if (!normalized.StartsWith(root.name + "/", StringComparison.Ordinal)) continue;
                    Transform match = root.transform.Find(normalized.Substring(root.name.Length + 1));
                    if (match != null) return match.gameObject;
                }
            }
            return null;
        }

        private static IEnumerable<Scene> LoadedScenes()
        {
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                Scene scene = SceneManager.GetSceneAt(index);
                if (scene.isLoaded) yield return scene;
            }
        }

        internal static IEnumerable<GameObject> LoadedSceneObjects()
        {
            foreach (Scene scene in LoadedScenes())
                foreach (GameObject root in scene.GetRootGameObjects())
                    foreach (GameObject gameObject in DescendantsAndSelf(root.transform))
                        yield return gameObject;
        }

        internal static IEnumerable<GameObject> DescendantsAndSelf(Transform transform)
        {
            yield return transform.gameObject;
            foreach (Transform child in transform)
                foreach (GameObject descendant in DescendantsAndSelf(child))
                    yield return descendant;
        }

        internal static BridgeObjectInfo ToObjectInfo(GameObject gameObject)
        {
            Transform transform = gameObject.transform;
            return new BridgeObjectInfo
            {
                globalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(gameObject).ToString(),
                scenePath = gameObject.scene.path,
                path = GetHierarchyPath(transform),
                name = gameObject.name,
                activeSelf = gameObject.activeSelf,
                activeInHierarchy = gameObject.activeInHierarchy,
                layer = gameObject.layer,
                tag = gameObject.tag,
                localPosition = Floats(transform.localPosition),
                localEulerAngles = Floats(transform.localEulerAngles),
                localScale = Floats(transform.localScale),
                components = gameObject.GetComponents<Component>().Select(component => component == null ? "<Missing Script>" : component.GetType().FullName).ToArray()
            };
        }

        private static float[] Floats(Vector3 value) => new[] { value.x, value.y, value.z };

        internal static string GetHierarchyPath(Transform transform)
        {
            var names = new Stack<string>();
            while (transform != null)
            {
                names.Push(transform.name);
                transform = transform.parent;
            }
            return string.Join("/", names.ToArray());
        }

        internal static void MarkChanged(GameObject gameObject)
        {
            EditorUtility.SetDirty(gameObject);
            if (gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(gameObject.scene);
        }

        private static string TargetNotFound(BridgeRequest request) =>
            $"Scene object was not found (globalObjectId='{request.globalObjectId}', objectPath='{request.objectPath}').";
    }
}
