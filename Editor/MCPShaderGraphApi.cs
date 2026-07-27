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
        // Optional — only required to bind a Property node (see Initialize).
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

            // Property-node binding members are resolved OPTIONALLY: they are needed only when
            // someone adds a Property node. Hard-requiring them (as PR #23 proposed) would mark
            // the ENTIRE ShaderGraph surface unavailable on any build where PropertyNode or its
            // 'property' setter is renamed — reintroducing exactly the all-or-nothing brittleness
            // this guard exists to avoid. AddNode raises a precise error if they're absent.
            _graphPropertiesProp = _graphDataT.GetProperty("properties", PIF);
            _propertyNodePropertyProp = _propertyNodeT?.GetProperty("property", PIF);
            var jsonObjectT = FindType(sg, "JsonObject");
            _jsonObjectIdProp = jsonObjectT?.GetProperty("objectId", PIF);

            _addContexts = _graphDataT.GetMethod("AddContexts", PIF);
            _initOutputs = _graphDataT.GetMethod("InitializeOutputs", PIF);
            _getActiveBlocks = _graphDataT.GetMethod("GetActiveBlocksForAllActiveTargets", PIF);
            _addRemoveBlocks = _graphDataT.GetMethod("AddRemoveBlocksFromActiveList", PIF);
            _onEnable = _graphDataT.GetMethod("OnEnable", PIF);
            // Defensive arity tolerance. On com.unity.shadergraph 17.3.0 (verified by reflecting
            // the shipped Unity.ShaderGraph.Editor assembly) the ONLY instance overload is the
            // 1-arg AddNode(AbstractMaterialNode) — the 2-arg form does not exist there. A
            // community report (PR #23) described the opposite on their build, which we could not
            // reproduce; accepting either shape costs nothing and removes the whole question.
            // If neither exists the named guard below reports exactly that.
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

            // Name the members that are actually missing. The old message ("One or more
            // ShaderGraph API methods not found") gave a user on an unexpected ShaderGraph
            // build no way to say WHICH member broke, and no way for us to reproduce it —
            // a bug report against this path was previously unfalsifiable in both directions.
            var missing = new List<string>();
            if (_addContexts == null) missing.Add("GraphData.AddContexts");
            if (_initOutputs == null) missing.Add("GraphData.InitializeOutputs");
            if (_getActiveBlocks == null) missing.Add("GraphData.GetActiveBlocksForAllActiveTargets");
            if (_addRemoveBlocks == null) missing.Add("GraphData.AddRemoveBlocksFromActiveList");
            if (_onEnable == null) missing.Add("GraphData.OnEnable");
            if (_addNode == null) missing.Add("GraphData.AddNode(AbstractMaterialNode[, bool])");
            if (_getNodeFromId == null) missing.Add("GraphData.GetNodeFromId(string)");
            if (_connect == null) missing.Add("GraphData.Connect");
            if (_removeEdge == null) missing.Add("GraphData.RemoveEdge");
            if (_removeNode == null) missing.Add("GraphData.RemoveNode");
            if (_getNodesGeneric == null) missing.Add("GraphData.GetNodes<T>");
            if (_serialize == null) missing.Add("MultiJson.Serialize");
            if (_deserializeGeneric == null) missing.Add("MultiJson.Deserialize<T>(4-arg)");
            if (_writeToDisk == null) missing.Add("FileUtilities.WriteToDisk");
            if (_writeGraphToDisk == null) missing.Add("FileUtilities.WriteShaderGraphToDisk");
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    $"ShaderGraph API version mismatch — {missing.Count} member(s) not found on this ShaderGraph build: {string.Join(", ", missing)}. " +
                    "Please report this list with your Unity and com.unity.shadergraph versions.");
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
        ///
        /// A PropertyNode MUST be bound to one of the graph's blackboard properties
        /// (<paramref name="propertyId"/> = that property's objectId). An unbound one
        /// serializes with an empty m_Property and no slots, and the next import throws inside
        /// PropertyNode.AddOutputSlot — which fails the whole asset. That is issue #18 bug 3;
        /// the GraphData rewrite fixed the other three but left this one, because it added the
        /// node type-only. We now bind it, or refuse before anything is written to disk.
        /// </summary>
        internal static string AddNode(object graph, Type nodeType, float x, float y, string propertyId = null)
        {
            var node = Activator.CreateInstance(nodeType);
            var draw = _drawStateProp.GetValue(node);
            _drawPositionProp.SetValue(draw, new Rect(x, y, 208, 300));
            _drawStateProp.SetValue(node, draw); // DrawState is a struct — write back
            // Both the 1-arg and 2-arg AddNode shapes are accepted (see Initialize); pass the
            // preview-preference default when the resolved overload takes it.
            _addNode.Invoke(graph, _addNode.GetParameters().Length == 2
                ? new object[] { node, true }
                : new object[] { node });

            if (_propertyNodeT != null && _propertyNodeT.IsInstanceOfType(node))
            {
                if (_propertyNodePropertyProp == null || _graphPropertiesProp == null || _jsonObjectIdProp == null)
                    throw new InvalidOperationException(
                        "This ShaderGraph build does not expose the members needed to bind a Property node " +
                        "(PropertyNode.property / GraphData.properties / JsonObject.objectId). Other node types still work.");
                if (string.IsNullOrEmpty(propertyId))
                    throw new InvalidOperationException(
                        "A Property node must be bound to a blackboard property: pass 'propertyId' (the property's objectId). " +
                        "An unbound Property node makes the .shadergraph fail to import.");
                var property = FindProperty(graph, propertyId)
                    ?? throw new InvalidOperationException($"No blackboard property with objectId '{propertyId}' exists on this graph.");
                // The setter rebuilds the node's output slot from the property's concrete type.
                _propertyNodePropertyProp.SetValue(node, property);
            }

            return (string)_objectIdProp.GetValue(node);
        }

        /// <summary>Find a blackboard property on the graph by its objectId, or null.</summary>
        private static object FindProperty(object graph, string propertyId)
        {
            var properties = _graphPropertiesProp.GetValue(graph) as IEnumerable;
            if (properties == null) return null;
            foreach (var property in properties)
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
