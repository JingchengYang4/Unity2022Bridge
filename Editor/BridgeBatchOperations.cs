using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Limen.EditorBridge
{
    internal static class BridgeBatchOperations
    {
        private const int MaximumOperations = 100;

        public static BridgeResponse Execute(BridgeRequest request)
        {
            BridgeResponse unsafeResponse = BridgeSceneOperations.RejectUnsafeMutation();
            if (unsafeResponse != null) return unsafeResponse;
            if (request.operations == null || request.operations.Length == 0)
                return BridgeResponse.Failure("Batch operations are required.");
            if (request.operations.Length > MaximumOperations)
                return BridgeResponse.Failure($"A batch may contain at most {MaximumOperations} operations.");

            string undoName = string.IsNullOrWhiteSpace(request.undoName)
                ? "Limen Bridge Batch"
                : request.undoName.Trim();
            var references = new Dictionary<string, BridgeObjectInfo>(StringComparer.Ordinal);
            var results = new List<BridgeBatchResult>();
            UnityEngine.Object previousSelection = Selection.activeObject;
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(undoName);

            try
            {
                for (int index = 0; index < request.operations.Length; index++)
                {
                    BridgeRequest operation = request.operations[index];
                    if (operation == null)
                        return RollBack(undoGroup, previousSelection, results, index, string.Empty, "Batch operation is null.");

                    string targetReference = ReferenceName(operation.objectPath);
                    string parentReference = ReferenceName(operation.parentPath);
                    if (targetReference != null)
                    {
                        if (!references.TryGetValue(targetReference, out BridgeObjectInfo resolvedObject))
                            return RollBack(undoGroup, previousSelection, results, index, operation.command, $"Unknown local reference '${targetReference}'.");
                        operation.objectPath = resolvedObject.path;
                        operation.globalObjectId = resolvedObject.globalObjectId;
                    }
                    if (parentReference != null)
                    {
                        if (!references.TryGetValue(parentReference, out BridgeObjectInfo resolvedParent))
                            return RollBack(undoGroup, previousSelection, results, index, operation.command, $"Unknown local reference '${parentReference}'.");
                        operation.parentPath = resolvedParent.path;
                        operation.parentGlobalObjectId = resolvedParent.globalObjectId;
                    }

                    BridgeResponse operationResponse = ExecuteOperation(operation);
                    results.Add(new BridgeBatchResult
                    {
                        index = index,
                        command = operation.command ?? string.Empty,
                        referenceId = operation.referenceId ?? string.Empty,
                        ok = operationResponse.ok,
                        message = operationResponse.message,
                        error = operationResponse.error,
                        sceneObject = operationResponse.sceneObject
                    });
                    if (!operationResponse.ok)
                        return RollBack(undoGroup, previousSelection, results, -1, operation.command, operationResponse.error);

                    if (!string.IsNullOrWhiteSpace(operation.referenceId) && operationResponse.sceneObject != null)
                        references[operation.referenceId.Trim()] = operationResponse.sceneObject;
                    if (targetReference != null && operationResponse.sceneObject != null)
                        references[targetReference] = operationResponse.sceneObject;
                    if (targetReference != null && string.Equals(operation.command, "object.delete", StringComparison.Ordinal))
                        references.Remove(targetReference);
                }

                Undo.FlushUndoRecordObjects();
                if (request.dryRun)
                {
                    Undo.RevertAllDownToGroup(undoGroup);
                    Selection.activeObject = previousSelection;
                }
                else
                {
                    Undo.CollapseUndoOperations(undoGroup);
                }

                BridgeResponse response = BridgeResponse.Success(request.dryRun
                    ? $"Dry run validated {results.Count} operations and rolled them back."
                    : $"Applied {results.Count} operations as one Undo group.");
                response.batchResults = results.ToArray();
                return response;
            }
            catch (Exception exception)
            {
                Undo.RevertAllDownToGroup(undoGroup);
                Selection.activeObject = previousSelection;
                return BridgeResponse.Failure($"Batch failed and was rolled back: {exception.Message}");
            }
        }

        private static BridgeResponse ExecuteOperation(BridgeRequest operation)
        {
            switch ((operation.command ?? string.Empty).Trim())
            {
                case "object.create": return BridgeSceneOperations.CreateObject(operation);
                case "object.update": return BridgeSceneOperations.UpdateObject(operation);
                case "object.delete": return BridgeSceneOperations.DeleteObject(operation);
                case "component.add": return BridgeSceneOperations.AddComponent(operation);
                case "component.remove": return BridgeSceneOperations.RemoveComponent(operation);
                case "property.set": return BridgeSceneOperations.SetProperty(operation);
                default: return BridgeResponse.Failure($"Unsupported batch command: {operation.command}");
            }
        }

        private static BridgeResponse RollBack(
            int undoGroup,
            UnityEngine.Object previousSelection,
            List<BridgeBatchResult> results,
            int index,
            string command,
            string error)
        {
            if (index >= 0)
            {
                results.Add(new BridgeBatchResult
                {
                    index = index,
                    command = command ?? string.Empty,
                    referenceId = string.Empty,
                    ok = false,
                    message = string.Empty,
                    error = error
                });
            }
            Undo.RevertAllDownToGroup(undoGroup);
            Selection.activeObject = previousSelection;
            BridgeResponse response = BridgeResponse.Failure($"Batch failed and was rolled back: {error}");
            response.batchResults = results.ToArray();
            return response;
        }

        private static string ReferenceName(string selector)
        {
            string value = selector?.Trim();
            return !string.IsNullOrEmpty(value) && value[0] == '$' ? value.Substring(1) : null;
        }
    }
}
