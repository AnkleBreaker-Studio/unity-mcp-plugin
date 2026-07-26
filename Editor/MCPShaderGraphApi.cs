using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Reflection wrapper over ShaderGraph's REAL editor API (GraphData / MultiJson /
    /// FileUtilities). ShaderGraph edits used to be done by regex/string surgery on the
    /// .shadergraph JSON, which produced invalid graphs (dangling output node, blanked
    /// edges, unbound property nodes → import NRE). Round-tripping through the actual
    /// GraphData model guarantees the serialized result is always valid, because
    /// ShaderGraph itself writes it — exactly what the editor does on save.
    ///
    /// All types are internal to Unity.ShaderGraph.Editor / the URP editor assembly, so
    /// every access is reflected. Handles are resolved once and cached; Available is false
    /// if the ShaderGraph package or an expected member is missing (older/newer versions),
    /// and callers fall back to a clear error rather than corrupting anything.
    /// </summary>
    internal static class MCPShaderGraphApi
    {
        internal static bool Available { get; private set; }
        internal static string UnavailableReason { get; private set; }

        private static Type _graphDataT, _targetT, _blockFieldT, _absNodeT, _slotRefT, _materialSlotT, _messageManagerT, _multiJsonT, _fileUtilT, _propertyNodeT;
        private static Type _urpTargetT, _litSubT, _unlitSubT, _spriteLitSubT, _spriteUnlitSubT;
        private static PropertyInfo _messageManagerProp, _objectIdProp, _drawStateProp, _edgesProp, _drawPositionProp;
        private static PropertyInfo _graphPropertiesProp, _propertyNodePropertyProp, _jsonObjectIdProp;
        private static MethodInfo _addContexts, _initOutputs, _getActiveBlocks, _addRemoveBlocks, _onEnable, _addNode, _getNodeFromId, _connect, _removeEdge, _removeNode, _getNodesGeneric, _getSlotsGeneric, _trySetSubTarget;
        private static MethodInfo _serialize, _deserializeGeneric, _writeToDisk, _writeGraphToDisk;
        private static ConstructorInfo _slotRefCtor;

        static MCPShaderGraphApi()
        {
            try { Initialize(); Available = true; }
            catch (Exception ex) { Available = false; UnavailableReason = ex.Message; }
        }

        private static Type Req(Assembly asm, string fullName)
        {
            var t = asm.GetType(fullName);
            if (t == null) throw new InvalidOperationException($"ShaderGraph type not found: {fullName}");
            return t;
        }

        private static Type FindType(Assembly asm, string simpleName)
        {
            try { return asm.GetTypes().FirstOrDefault(t => t.Name == simpleName); }
            catch (ReflectionTypeLoadException e) { return e.Types.FirstOrDefault(t => t != null && t.Name == simpleName); }
        }

        private static void Initialize()
        {
            var sg = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Unity.ShaderGraph.Editor")
                     ?? throw new InvalidOperationException("Unity.ShaderGraph.Editor assembly not loaded");
            var urp = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Unity.RenderPipelines.Universal.Editor");

            _graphDataT = Req(sg, "UnityEditor.ShaderGraph.GraphData");
            _targetT = Req(sg, "UnityEditor.ShaderGraph.Target");
            _blockFieldT = Req(sg, "UnityEditor.ShaderGraph.BlockFieldDescriptor");
            _absNodeT = Req(sg, "UnityEditor.ShaderGraph.AbstractMaterialNode");
            _slotRefT = Req(sg, "UnityEditor.Graphing.SlotReference");
            _materialSlotT = Req(sg, "UnityEditor.ShaderGraph.MaterialSlot");
            _multiJsonT = Req(sg, "UnityEditor.ShaderGraph.Serialization.MultiJson");
            _fileUtilT = Req(sg, "UnityEditor.ShaderGraph.FileUtilities");
            _propertyNodeT = sg.GetType("UnityEditor.ShaderGraph.PropertyNode");
            _messageManagerT = FindType(sg, "MessageManager") ?? throw new InvalidOperationException("MessageManager not found");

            const BindingFlags PIF = BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic;
            const BindingFlags SF = BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic;

            _messageManagerProp = _graphDataT.GetProperty("messageManager", PIF) ?? throw new InvalidOperationException("messageManager");
            _objectIdProp = _absNodeT.GetProperty("objectId", PIF) ?? throw new InvalidOperationException("objectId");
            _drawStateProp = _absNodeT.GetProperty("drawState", PIF) ?? throw new InvalidOperationException("drawState");
            _edgesProp = _graphDataT.GetProperty("edges", PIF) ?? throw new InvalidOperationException("edges");
            _drawPositionProp = _drawStateProp.PropertyType.GetProperty("position", PIF) ?? throw new InvalidOperationException("drawState.position");
            _graphPropertiesProp = _graphDataT.GetProperty("properties", PIF) ?? throw new InvalidOperationException("properties");
            _jsonObjectIdProp = Req(sg, "UnityEditor.ShaderGraph.Serialization.JsonObject").GetProperty("objectId", PIF) ?? throw new InvalidOperationException("JsonObject.objectId");
            _propertyNodePropertyProp = _propertyNodeT?.GetProperty("property", PIF) ?? throw new InvalidOperationException("PropertyNode.property");

            _addContexts = _graphDataT.GetMethod("AddContexts", PIF);
            _initOutputs = _graphDataT.GetMethod("InitializeOutputs", PIF);
            _getActiveBlocks = _graphDataT.GetMethod("GetActiveBlocksForAllActiveTargets", PIF);
            _addRemoveBlocks = _graphDataT.GetMethod("AddRemoveBlocksFromActiveList", PIF);
            _onEnable = _graphDataT.GetMethod("OnEnable", PIF);
            // AddNode is AddNode(node) on older ShaderGraph and AddNode(node, usePreviewPref)
            // on 17.x (Unity 6.3) — accept either shape; the call site matches the arity.
            _addNode = _graphDataT.GetMethod("AddNode", PIF, null, new[] { _absNodeT }, null)
                       ?? _graphDataT.GetMethod("AddNode", PIF, null, new[] { _absNodeT, typeof(bool) }, null);
            // GetNodeFromId has both a generic and a non-generic (string) overload, so a
            // typed GetMethod is ambiguous — pick the non-generic one explicitly.
            _getNodeFromId = _graphDataT.GetMethods(PIF).FirstOrDefault(m =>
                m.Name == "GetNodeFromId" && !m.IsGenericMethod &&
                m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
            _connect = _graphDataT.GetMethod("Connect", PIF, null, new[] { _slotRefT, _slotRefT }, null);
            _removeEdge = _graphDataT.GetMethods(PIF).FirstOrDefault(m => m.Name == "RemoveEdge" && m.GetParameters().Length == 1);
            _removeNode = _graphDataT.GetMethods(PIF).FirstOrDefault(m =>
                m.Name == "RemoveNode" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == _absNodeT);
            _getNodesGeneric = _graphDataT.GetMethods(PIF).FirstOrDefault(m => m.Name == "GetNodes" && m.IsGenericMethod);
            _getSlotsGeneric = _absNodeT.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m => m.Name == "GetSlots" && m.IsGenericMethod && m.GetParameters().Length == 1);
            _trySetSubTarget = _urpTargetTryResolve(urp);
            _slotRefCtor = _slotRefT.GetConstructor(new[] { _absNodeT, typeof(int) }) ?? throw new InvalidOperationException("SlotReference ctor");

            _serialize = _multiJsonT.GetMethod("Serialize", SF);
            // Pin the 4-arg Deserialize<T>(T, string, JsonObject, bool) shape LoadGraph invokes;
            // a different arity in some version would otherwise TargetParameterCountException at
            // call time despite Available==true. Fail closed at init instead.
            _deserializeGeneric = _multiJsonT.GetMethods(SF).FirstOrDefault(m => m.Name == "Deserialize" && m.IsGenericMethod && m.GetParameters().Length == 4);
            _writeToDisk = _fileUtilT.GetMethod("WriteToDisk", SF);
            _writeGraphToDisk = _fileUtilT.GetMethod("WriteShaderGraphToDisk", SF);

            if (_addContexts == null || _initOutputs == null || _getActiveBlocks == null || _addRemoveBlocks == null ||
                _onEnable == null || _addNode == null || _getNodeFromId == null || _connect == null || _removeEdge == null ||
                _removeNode == null || _getNodesGeneric == null || _serialize == null || _deserializeGeneric == null ||
                _writeToDisk == null || _writeGraphToDisk == null)
                throw new InvalidOperationException("One or more ShaderGraph API methods not found (version mismatch)");
        }

        private static MethodInfo _urpTargetTryResolve(Assembly urp)
        {
            if (urp == null) return null;
            _urpTargetT = urp.GetType("UnityEditor.Rendering.Universal.ShaderGraph.UniversalTarget");
            _litSubT = urp.GetType("UnityEditor.Rendering.Universal.ShaderGraph.UniversalLitSubTarget");
            _unlitSubT = urp.GetType("UnityEditor.Rendering.Universal.ShaderGraph.UniversalUnlitSubTarget");
            _spriteLitSubT = urp.GetType("UnityEditor.Rendering.Universal.ShaderGraph.UniversalSpriteLitSubTarget");
            _spriteUnlitSubT = urp.GetType("UnityEditor.Rendering.Universal.ShaderGraph.UniversalSpriteUnlitSubTarget");
            return _urpTargetT?.GetMethod("TrySetActiveSubTarget");
        }

        // ─────────────────────────────────────────────────────────────
        //  High-level operations
        // ─────────────────────────────────────────────────────────────

        /// <summary>Map a template name to a URP SubTarget type; null = blank (no target).</summary>
        internal static Type ResolveTemplateSubTarget(string template)
        {
            switch ((template ?? "").ToLowerInvariant())
            {
                case "urp_lit":
                case "lit":
                case "": return _litSubT;
                case "urp_unlit":
                case "unlit": return _unlitSubT;
                case "urp_sprite_lit":
                case "sprite_lit": return _spriteLitSubT;
                case "urp_sprite_unlit":
                case "sprite_unlit": return _spriteUnlitSubT;
                case "blank":
                case "empty": return null;
                default: return _litSubT;
            }
        }

        internal static bool IsUrpAvailable => _urpTargetT != null && _trySetSubTarget != null;

        /// <summary>
        /// Create a valid new graph on disk. subTargetType null → a blank graph (valid, no
        /// active target; the user picks one in-editor). Returns null on success, else an error message.
        /// </summary>
        internal static string CreateGraph(string assetPath, string fullPath, Type subTargetType)
        {
            var graph = NewGraph();
            _addContexts.Invoke(graph, null);

            if (subTargetType != null)
            {
                if (!IsUrpAvailable) return "URP Shader Graph targets are not available in this project; use template 'blank'.";
                var target = Activator.CreateInstance(_urpTargetT);
                _trySetSubTarget.Invoke(target, new object[] { subTargetType });
                var targets = Array.CreateInstance(_targetT, 1);
                targets.SetValue(target, 0);
                _initOutputs.Invoke(graph, new object[] { targets, Array.CreateInstance(_blockFieldT, 0) });
                _addRemoveBlocks.Invoke(graph, new object[] { _getActiveBlocks.Invoke(graph, null) });
            }

            _onEnable.Invoke(graph, null);
            string text = (string)_serialize.Invoke(null, new object[] { graph });
            _writeToDisk.Invoke(null, new object[] { assetPath, text });
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            return null;
        }

        /// <summary>Load an existing .shadergraph into a live, mutable GraphData.</summary>
        internal static object LoadGraph(string fullPath)
        {
            string text = File.ReadAllText(fullPath);
            var graph = NewGraph();
            _deserializeGeneric.MakeGenericMethod(_graphDataT).Invoke(null, new object[] { graph, text, null, false });
            _onEnable.Invoke(graph, null);
            return graph;
        }

        /// <summary>Serialize a mutated graph back to disk and reimport.</summary>
        internal static void SaveGraph(string assetPath, object graph)
        {
            _writeGraphToDisk.Invoke(null, new object[] { assetPath, graph });
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
        }

        private static object NewGraph()
        {
            var graph = Activator.CreateInstance(_graphDataT);
            _messageManagerProp.SetValue(graph, Activator.CreateInstance(_messageManagerT));
            return graph;
        }

        /// <summary>
        /// Instantiate a node type, set its position, and add it. Returns its objectId.
        /// A PropertyNode must be bound to one of the graph's blackboard properties
        /// (<paramref name="propertyId"/> = that property's objectId); an unbound one
        /// serializes with no slots and throws in PropertyNode.AddOutputSlot on the next
        /// import, failing the whole asset — so this fails closed instead (#18 bug 3).
        /// </summary>
        internal static string AddNode(object graph, Type nodeType, float x, float y, string propertyId = null)
        {
            var node = Activator.CreateInstance(nodeType);
            var draw = _drawStateProp.GetValue(node);
            _drawPositionProp.SetValue(draw, new Rect(x, y, 208, 300));
            _drawStateProp.SetValue(node, draw); // DrawState is a struct — write back
            // usePreviewPref: true matches the parameter's declared default on 17.x.
            _addNode.Invoke(graph, _addNode.GetParameters().Length == 2
                ? new object[] { node, true }
                : new object[] { node });

            if (_propertyNodeT.IsInstanceOfType(node))
            {
                if (string.IsNullOrEmpty(propertyId))
                    throw new InvalidOperationException(
                        "A Property node needs 'propertyId' — the objectId of a blackboard property on this graph (see shadergraph/get-nodes).");
                var property = FindProperty(graph, propertyId)
                    ?? throw new InvalidOperationException($"No blackboard property with objectId '{propertyId}' on this graph.");
                // The setter rebuilds the node's output slot from the property's type.
                _propertyNodePropertyProp.SetValue(node, property);
            }

            return (string)_objectIdProp.GetValue(node);
        }

        private static object FindProperty(object graph, string propertyId)
        {
            foreach (var property in (IEnumerable)_graphPropertiesProp.GetValue(graph))
                if ((string)_jsonObjectIdProp.GetValue(property) == propertyId)
                    return property;
            return null;
        }

        internal static object GetNodeFromId(object graph, string nodeId) => _getNodeFromId.Invoke(graph, new object[] { nodeId });

        /// <summary>Remove a node and its connected edges (byte-faithful to survivors).</summary>
        internal static void RemoveNode(object graph, object node) => _removeNode.Invoke(graph, new object[] { node });

        /// <summary>
        /// True if the template names a specific pipeline target we can't build because that
        /// pipeline's editor assembly isn't present (so create would silently fall back to blank).
        /// </summary>
        internal static bool IsUnavailablePipelineTemplate(string template)
        {
            string t = (template ?? "").ToLowerInvariant();
            bool wantsUrp = t.StartsWith("urp_") || t == "lit" || t == "unlit" || t.StartsWith("sprite_");
            return wantsUrp && !IsUrpAvailable;
        }

        /// <summary>Find a slot id on a node by input/output and (optional) explicit id. Returns int.MinValue if none.</summary>
        internal static int FindSlotId(object node, bool wantInput, int explicitId)
        {
            var listT = typeof(List<>).MakeGenericType(_materialSlotT);
            var list = Activator.CreateInstance(listT);
            _getSlotsGeneric.MakeGenericMethod(_materialSlotT).Invoke(node, new object[] { list });
            foreach (var slot in (IEnumerable)list)
            {
                bool isInput = (bool)slot.GetType().GetProperty("isInputSlot").GetValue(slot);
                int id = (int)slot.GetType().GetProperty("id").GetValue(slot);
                if (isInput != wantInput) continue;
                if (explicitId != int.MinValue) { if (id == explicitId) return id; }
                else return id; // first matching-direction slot
            }
            return int.MinValue;
        }

        /// <summary>Connect output(node,slot) → input(node,slot). Returns null on success or an error.</summary>
        internal static string Connect(object graph, object outNode, int outSlot, object inNode, int inSlot)
        {
            var outRef = _slotRefCtor.Invoke(new object[] { outNode, outSlot });
            var inRef = _slotRefCtor.Invoke(new object[] { inNode, inSlot });
            var edge = _connect.Invoke(graph, new object[] { outRef, inRef });
            return edge == null ? "Connection refused by ShaderGraph (incompatible slots or would create a cycle)." : null;
        }

        /// <summary>Remove edges matching the 4-tuple (any component &lt;0/null = wildcard). Returns removed count.</summary>
        internal static int Disconnect(object graph, string outNodeId, int outSlot, string inNodeId, int inSlot)
        {
            var toRemove = new List<object>();
            foreach (var edge in (IEnumerable)_edgesProp.GetValue(graph))
            {
                var outSlotRef = edge.GetType().GetProperty("outputSlot").GetValue(edge);
                var inSlotRef = edge.GetType().GetProperty("inputSlot").GetValue(edge);
                string eOut = SlotRefNodeId(outSlotRef);
                string eIn = SlotRefNodeId(inSlotRef);
                int eOutSlot = (int)outSlotRef.GetType().GetProperty("slotId").GetValue(outSlotRef);
                int eInSlot = (int)inSlotRef.GetType().GetProperty("slotId").GetValue(inSlotRef);

                if (outNodeId != null && eOut != outNodeId) continue;
                if (inNodeId != null && eIn != inNodeId) continue;
                if (outSlot >= 0 && eOutSlot != outSlot) continue;
                if (inSlot >= 0 && eInSlot != inSlot) continue;
                toRemove.Add(edge);
            }
            foreach (var edge in toRemove)
                _removeEdge.Invoke(graph, new object[] { edge });
            return toRemove.Count;
        }

        private static string SlotRefNodeId(object slotRef)
        {
            // SlotReference exposes the owning node; read its objectId.
            var nodeProp = slotRef.GetType().GetProperty("node", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            var node = nodeProp?.GetValue(slotRef);
            return node != null ? (string)_objectIdProp.GetValue(node) : null;
        }

        internal static Type ResolveNodeType(string name) => MCPShaderGraphCommands.ResolveShaderGraphNodeType(name);
    }
}
