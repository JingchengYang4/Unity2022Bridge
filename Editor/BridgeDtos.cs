using System;

namespace Limen.EditorBridge
{
    [Serializable]
    internal sealed class BridgeRequest
    {
        public string state;
        public string path;
        public bool additive;
        public string filter;
        public string[] folders;
        public int limit;

        public string globalObjectId;
        public string objectPath;
        public string parentGlobalObjectId;
        public string parentPath;
        public string name;
        public string newName;
        public string componentType;
        public int componentIndex;
        public string propertyPath;
        public string value;
        public string assetPath;
        public int intValue;
        public bool boolValue;
        public float[] values;
        public bool hasActive;
        public bool active;
        public bool hasLocalPosition;
        public bool hasLocalEulerAngles;
        public bool hasLocalScale;
        public float[] localPosition;
        public float[] localEulerAngles;
        public float[] localScale;

        public string command;
        public string referenceId;
        public string undoName;
        public BridgeRequest[] operations;
        public bool dryRun;
        public bool recursive;
        public bool reverse;
        public bool overwrite;
        public string expectedName;
        public bool hasExpectedActive;
        public bool expectedActive;
        public string expectedValue;
    }

    [Serializable]
    internal sealed class BridgeResponse
    {
        public bool ok;
        public string message;
        public string error;
        public string jobId;
        public string unityVersion;
        public string projectPath;
        public string activeScene;
        public bool isPlaying;
        public bool isPaused;
        public bool isCompiling;
        public bool isUpdating;
        public string apiVersion;
        public long latestLogId;
        public bool truncated;
        public BridgeJobRecord job;
        public BridgeLogEntry[] logs;
        public BridgeHierarchyItem[] hierarchy;
        public BridgeAssetInfo[] assets;
        public BridgeSelectionInfo selection;
        public BridgeCommandInfo[] commands;
        public BridgeObjectInfo sceneObject;
        public BridgeObjectInfo[] sceneObjects;
        public BridgeComponentInfo[] components;
        public BridgePropertyInfo[] properties;
        public BridgeBatchResult[] batchResults;
        public BridgeComponentTypeInfo[] componentTypes;
        public BridgePrefabInfo prefab;
        public BridgeUiElementInfo[] uiElements;
        public string uiSourceAsset;
        public bool uiLive;

        public static BridgeResponse Success(string successMessage = null) => new BridgeResponse
        {
            ok = true,
            message = successMessage ?? string.Empty,
            error = string.Empty,
            jobId = string.Empty,
            unityVersion = string.Empty,
            projectPath = string.Empty,
            activeScene = string.Empty,
            logs = Array.Empty<BridgeLogEntry>(),
            hierarchy = Array.Empty<BridgeHierarchyItem>(),
            assets = Array.Empty<BridgeAssetInfo>(),
            commands = Array.Empty<BridgeCommandInfo>(),
            sceneObjects = Array.Empty<BridgeObjectInfo>(),
            components = Array.Empty<BridgeComponentInfo>(),
            properties = Array.Empty<BridgePropertyInfo>(),
            batchResults = Array.Empty<BridgeBatchResult>(),
            componentTypes = Array.Empty<BridgeComponentTypeInfo>(),
            uiElements = Array.Empty<BridgeUiElementInfo>()
        };

        public static BridgeResponse Failure(string failure) => new BridgeResponse
        {
            ok = false,
            message = string.Empty,
            error = failure,
            jobId = string.Empty,
            unityVersion = string.Empty,
            projectPath = string.Empty,
            activeScene = string.Empty,
            logs = Array.Empty<BridgeLogEntry>(),
            hierarchy = Array.Empty<BridgeHierarchyItem>(),
            assets = Array.Empty<BridgeAssetInfo>(),
            commands = Array.Empty<BridgeCommandInfo>(),
            sceneObjects = Array.Empty<BridgeObjectInfo>(),
            components = Array.Empty<BridgeComponentInfo>(),
            properties = Array.Empty<BridgePropertyInfo>(),
            batchResults = Array.Empty<BridgeBatchResult>(),
            componentTypes = Array.Empty<BridgeComponentTypeInfo>(),
            uiElements = Array.Empty<BridgeUiElementInfo>()
        };
    }

    [Serializable]
    internal sealed class BridgeJobRecord
    {
        public string id;
        public string command;
        public string state;
        public string message;
        public string createdUtc;
        public string updatedUtc;
    }

    [Serializable]
    internal sealed class BridgeJobFile
    {
        public BridgeJobRecord[] jobs;
    }

    [Serializable]
    internal sealed class BridgeLogEntry
    {
        public long id;
        public string timestampUtc;
        public string type;
        public string message;
        public string stackTrace;
    }

    [Serializable]
    internal sealed class BridgeHierarchyItem
    {
        public string path;
        public bool activeSelf;
        public bool activeInHierarchy;
        public string[] components;
    }

    [Serializable]
    internal sealed class BridgeAssetInfo
    {
        public string guid;
        public string path;
        public string type;
    }

    [Serializable]
    internal sealed class BridgeSelectionInfo
    {
        public int instanceId;
        public string name;
        public string type;
        public string assetPath;
        public string globalObjectId;
        public string objectPath;
    }

    [Serializable]
    internal sealed class BridgeCommandInfo
    {
        public string method;
        public string route;
        public bool mutating;
        public string description;
    }

    [Serializable]
    internal sealed class BridgeObjectInfo
    {
        public string globalObjectId;
        public string scenePath;
        public string path;
        public string name;
        public bool activeSelf;
        public bool activeInHierarchy;
        public int layer;
        public string tag;
        public float[] localPosition;
        public float[] localEulerAngles;
        public float[] localScale;
        public string[] components;
    }

    [Serializable]
    internal sealed class BridgeComponentInfo
    {
        public string type;
        public int index;
        public bool missing;
    }

    [Serializable]
    internal sealed class BridgePropertyInfo
    {
        public string path;
        public string displayName;
        public string type;
        public string value;
        public string tooltip;
        public bool editable;
        public bool isArray;
        public int arraySize;
        public string[] enumValues;
    }

    [Serializable]
    internal sealed class BridgeBatchResult
    {
        public int index;
        public string command;
        public string referenceId;
        public bool ok;
        public string message;
        public string error;
        public BridgeObjectInfo sceneObject;
    }

    [Serializable]
    internal sealed class BridgeComponentTypeInfo
    {
        public string type;
        public string assembly;
        public string nameSpace;
        public bool addable;
        public bool disallowMultiple;
        public string[] requiredComponents;
    }

    [Serializable]
    internal sealed class BridgePrefabInfo
    {
        public string assetPath;
        public string guid;
        public string instanceStatus;
        public string sourceAssetPath;
        public BridgeObjectInfo root;
        public BridgeObjectInfo[] objects;
    }

    [Serializable]
    internal sealed class BridgeUiElementInfo
    {
        public string path;
        public string name;
        public string type;
        public string[] classes;
        public string text;
        public int childCount;
        public bool enabledSelf;
        public bool attachedToPanel;
        public string pickingMode;
        public string display;
        public string visibility;
        public float opacity;
        public float[] layout;
        public float[] worldBound;
    }
}
