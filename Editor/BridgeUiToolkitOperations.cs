using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Limen.EditorBridge
{
    internal static class BridgeUiToolkitOperations
    {
        private const int MaximumElements = 2000;
        private const int MaximumTextLength = 256;

        public static BridgeResponse InspectTree(BridgeRequest request)
        {
            UIDocument document = null;
            VisualTreeAsset sourceAsset = null;
            VisualElement root;
            bool live;
            if (!string.IsNullOrWhiteSpace(request.assetPath))
            {
                string assetPath = request.assetPath.Trim().Replace('\\', '/');
                if (!assetPath.StartsWith("Assets/", StringComparison.Ordinal) ||
                    !assetPath.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase) || assetPath.Contains("../"))
                    return BridgeResponse.Failure("UI Toolkit source must be a normalized Assets/*.uxml path.");
                sourceAsset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(assetPath);
                if (sourceAsset == null) return BridgeResponse.Failure("VisualTreeAsset was not found.");
                root = sourceAsset.CloneTree();
                live = false;
            }
            else
            {
                document = ResolveDocument(request, out string resolutionError);
                if (document == null) return BridgeResponse.Failure(resolutionError);
                sourceAsset = document.visualTreeAsset;
                root = document.rootVisualElement;
                live = root != null && root.panel != null;
                if (root == null || root.childCount == 0)
                {
                    if (sourceAsset == null)
                        return BridgeResponse.Failure("The UIDocument has no live root and no VisualTreeAsset.");
                    root = sourceAsset.CloneTree();
                    live = false;
                }
            }

            string filter = request.filter?.Trim() ?? string.Empty;
            int limit = Mathf.Clamp(request.limit <= 0 ? 250 : request.limit, 1, MaximumElements);
            int scanLimit = string.IsNullOrEmpty(filter) ? limit : Math.Min(MaximumElements, limit * 10);
            var elements = new List<BridgeUiElementInfo>();
            int visited = 0;
            AddElements(root, RootSegment(root), filter, limit, scanLimit, elements, ref visited);

            BridgeResponse response = BridgeResponse.Success(live
                ? "Inspected the live UIDocument visual tree."
                : "Inspected a detached clone of the UIDocument VisualTreeAsset; resolved layout is not live.");
            response.uiLive = live;
            response.uiSourceAsset = AssetDatabase.GetAssetPath(sourceAsset) ?? string.Empty;
            response.uiElements = elements.ToArray();
            response.truncated = visited >= scanLimit || elements.Count >= limit;
            response.sceneObject = document == null ? null : BridgeSceneOperations.ToObjectInfo(document.gameObject);
            return response;
        }

        private static UIDocument ResolveDocument(BridgeRequest request, out string error)
        {
            error = string.Empty;
            bool hasTarget = !string.IsNullOrWhiteSpace(request.globalObjectId) || !string.IsNullOrWhiteSpace(request.objectPath);
            if (hasTarget)
            {
                GameObject gameObject = BridgeSceneOperations.ResolveGameObject(request);
                if (gameObject == null)
                {
                    error = "UIDocument target object was not found.";
                    return null;
                }
                UIDocument[] documents = gameObject.GetComponents<UIDocument>();
                if (request.componentIndex < 0 || request.componentIndex >= documents.Length)
                {
                    error = "UIDocument component was not found at the requested componentIndex.";
                    return null;
                }
                return documents[request.componentIndex];
            }

            List<UIDocument> loaded = BridgeSceneOperations.LoadedSceneObjects()
                .SelectMany(gameObject => gameObject.GetComponents<UIDocument>())
                .Take(2)
                .ToList();
            if (loaded.Count == 0)
            {
                error = "No UIDocument exists in the loaded scenes.";
                return null;
            }
            if (loaded.Count > 1)
            {
                error = "Multiple UIDocuments exist. Supply objectPath or globalObjectId.";
                return null;
            }
            return loaded[0];
        }

        private static void AddElements(
            VisualElement element,
            string path,
            string filter,
            int limit,
            int scanLimit,
            List<BridgeUiElementInfo> output,
            ref int visited)
        {
            if (visited >= scanLimit || output.Count >= limit) return;
            visited++;
            string[] classes = element.GetClasses().Take(32).ToArray();
            if (Matches(element, path, classes, filter) && output.Count < limit)
                output.Add(ToInfo(element, path, classes));

            int childIndex = 0;
            foreach (VisualElement child in element.Children())
            {
                AddElements(child, path + "/" + Segment(child, childIndex), filter, limit, scanLimit, output, ref visited);
                childIndex++;
            }
        }

        private static bool Matches(VisualElement element, string path, string[] classes, string filter)
        {
            if (string.IsNullOrEmpty(filter)) return true;
            return path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (element.name ?? string.Empty).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   element.GetType().FullName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   classes.Any(className => className.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static BridgeUiElementInfo ToInfo(VisualElement element, string path, string[] classes)
        {
            Rect layout = element.layout;
            Rect worldBound = element.worldBound;
            string text = element is TextElement textElement ? textElement.text ?? string.Empty : string.Empty;
            if (text.Length > MaximumTextLength) text = text.Substring(0, MaximumTextLength);
            return new BridgeUiElementInfo
            {
                path = path,
                name = element.name ?? string.Empty,
                type = element.GetType().FullName,
                classes = classes,
                text = text,
                childCount = element.childCount,
                enabledSelf = element.enabledSelf,
                attachedToPanel = element.panel != null,
                pickingMode = element.pickingMode.ToString(),
                display = element.resolvedStyle.display.ToString(),
                visibility = element.resolvedStyle.visibility.ToString(),
                opacity = element.resolvedStyle.opacity,
                layout = RectValues(layout),
                worldBound = RectValues(worldBound)
            };
        }

        private static string RootSegment(VisualElement element) => Segment(element, 0);

        private static string Segment(VisualElement element, int siblingIndex)
        {
            string identity = string.IsNullOrEmpty(element.name) ? element.GetType().Name : "#" + element.name;
            return identity + "[" + siblingIndex + "]";
        }

        private static float[] RectValues(Rect rect) => new[] { rect.x, rect.y, rect.width, rect.height };
    }
}
