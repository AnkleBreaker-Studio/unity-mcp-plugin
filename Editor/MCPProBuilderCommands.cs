using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
#if PROBUILDER_INSTALLED
using System;
using System.Linq;
using UnityEngine.ProBuilder;
using UnityEngine.ProBuilder.MeshOperations;
using UnityEditor.ProBuilder;
#endif

namespace UnityMCP.Editor
{
    /// <summary>
    /// ProBuilder integration (com.unity.probuilder). Create and edit editable geometry —
    /// shapes, extrude/bevel/subdivide, per-face materials, boolean ops, ProBuilderize — all
    /// through ProBuilder's public API so the resulting meshes are real ProBuilder objects the
    /// user can keep editing in the editor. Every mutation registers Undo, so it composes with
    /// the multi-agent queue's per-action undo tracking.
    ///
    /// The whole surface is gated on PROBUILDER_INSTALLED (asmdef versionDefine). When ProBuilder
    /// isn't in the project, every handler returns a clear "not installed" error instead of
    /// failing to compile.
    /// </summary>
    public static class MCPProBuilderCommands
    {
#if !PROBUILDER_INSTALLED
        private static object NotInstalled()
        {
            return new Dictionary<string, object>
            {
                { "error", "ProBuilder (com.unity.probuilder) is not installed. Install it via Package Manager to use probuilder/* tools." },
                { "hint", "Window > Package Manager > Unity Registry > ProBuilder > Install." },
            };
        }

        public static object CreateShape(Dictionary<string, object> args) => NotInstalled();
        public static object GetInfo(Dictionary<string, object> args) => NotInstalled();
        public static object ExtrudeFaces(Dictionary<string, object> args) => NotInstalled();
        public static object BevelEdges(Dictionary<string, object> args) => NotInstalled();
        public static object Subdivide(Dictionary<string, object> args) => NotInstalled();
        public static object DeleteFaces(Dictionary<string, object> args) => NotInstalled();
        public static object SetFaceMaterial(Dictionary<string, object> args) => NotInstalled();
        public static object BooleanOp(Dictionary<string, object> args) => NotInstalled();
        public static object ProBuilderize(Dictionary<string, object> args) => NotInstalled();
        public static object Combine(Dictionary<string, object> args) => NotInstalled();
        public static object TranslateFaces(Dictionary<string, object> args) => NotInstalled();
        public static object FlipNormals(Dictionary<string, object> args) => NotInstalled();
        public static object CenterPivot(Dictionary<string, object> args) => NotInstalled();
        public static object ExportMesh(Dictionary<string, object> args) => NotInstalled();
#else
        // ─────────────────────────────────────────────
        //  Create
        // ─────────────────────────────────────────────

        public static object CreateShape(Dictionary<string, object> args)
        {
            string shape = args.ContainsKey("shape") ? args["shape"].ToString().ToLowerInvariant() : "cube";
            float w = GetFloat(args, "width", 1f), h = GetFloat(args, "height", 1f), d = GetFloat(args, "depth", 1f);
            var size = new Vector3(w, h, d);

            ProBuilderMesh pb;
            switch (shape)
            {
                case "cube": pb = ShapeGenerator.GenerateCube(PivotLocation.Center, size); break;
                case "plane": pb = ShapeGenerator.GeneratePlane(PivotLocation.Center, w, d, GetInt(args, "widthSegments", 1), GetInt(args, "lengthSegments", 1), Axis.Up); break;
                case "cylinder": pb = ShapeGenerator.GenerateCylinder(PivotLocation.Center, GetInt(args, "sides", 8), Mathf.Max(w, d) * 0.5f, h, GetInt(args, "heightSegments", 0), 1); break;
                case "prism": pb = ShapeGenerator.GeneratePrism(PivotLocation.Center, size); break;
                case "stair": pb = ShapeGenerator.GenerateStair(PivotLocation.Center, size, GetInt(args, "steps", 6), GetBool(args, "sides", true)); break;
                case "cone": pb = ShapeGenerator.GenerateCone(PivotLocation.Center, Mathf.Max(w, d) * 0.5f, h, GetInt(args, "sides", 8)); break;
                case "door": pb = ShapeGenerator.GenerateDoor(PivotLocation.Center, w, h, GetFloat(args, "ledge", 0.15f), GetFloat(args, "legWidth", 0.25f), d); break;
                case "pipe": pb = ShapeGenerator.GeneratePipe(PivotLocation.Center, Mathf.Max(w, d) * 0.5f, h, GetFloat(args, "thickness", 0.1f), GetInt(args, "sides", 8), GetInt(args, "heightSegments", 1)); break;
                case "arch": pb = ShapeGenerator.GenerateArch(PivotLocation.Center, GetFloat(args, "angle", 180f), Mathf.Max(w, d) * 0.5f, GetFloat(args, "thickness", 0.2f), d, GetInt(args, "sides", 6), true, true, true, true, true); break;
                case "sphere":
                case "icosahedron": pb = ShapeGenerator.GenerateIcosahedron(PivotLocation.Center, Mathf.Max(w, h, d) * 0.5f, GetInt(args, "subdivisions", 2), true, true); break;
                case "torus": pb = ShapeGenerator.GenerateTorus(PivotLocation.Center, GetInt(args, "rows", 12), GetInt(args, "columns", 16), Mathf.Max(w, d) * 0.5f, GetFloat(args, "tubeRadius", 0.2f), true, 360f, 360f, true); break;
                default:
                    return Error($"Unknown shape '{shape}'. Supported: cube, plane, cylinder, prism, stair, cone, door, pipe, arch, sphere, torus.");
            }

            var go = pb.gameObject;
            go.name = args.ContainsKey("name") ? args["name"].ToString() : ("PB_" + shape);
            if (args.ContainsKey("position") && args["position"] is Dictionary<string, object> p)
                go.transform.position = new Vector3(GetFloat(p, "x", 0), GetFloat(p, "y", 0), GetFloat(p, "z", 0));

            // ShapeGenerator leaves the renderer's material NULL — the shape renders magenta
            // and a null material key crashes ProBuilder's CSG. Always end with a real
            // material: the explicit arg when given, ProBuilder's default otherwise.
            var mat = args.ContainsKey("material")
                ? AssetDatabase.LoadAssetAtPath<Material>(args["material"].ToString())
                : null;
            pb.SetMaterial(pb.faces, mat != null ? mat : BuiltinMaterials.defaultMaterial);

            Rebuild(pb);
            Undo.RegisterCreatedObjectUndo(go, "Create ProBuilder " + shape);
            return Ok(pb, new Dictionary<string, object> { { "shape", shape }, { "name", go.name } });
        }

        // ─────────────────────────────────────────────
        //  Inspect
        // ─────────────────────────────────────────────

        public static object GetInfo(Dictionary<string, object> args)
        {
            if (!TryResolve(args, out var pb, out var err)) return err;
            var b = pb.GetComponent<MeshFilter>()?.sharedMesh != null ? pb.GetComponent<MeshFilter>().sharedMesh.bounds : new Bounds();
            var materials = pb.faces.Select(f => f.submeshIndex).Distinct().OrderBy(i => i).ToArray();
            return new Dictionary<string, object>
            {
                { "success", true },
                { "name", pb.name },
                { "instanceId", MCPObjectId.Get(pb.gameObject) },
                { "isProBuilder", true },
                { "faceCount", pb.faceCount },
                { "vertexCount", pb.vertexCount },
                { "edgeCount", pb.edgeCount },
                { "sharedVertexCount", pb.sharedVertices.Count },
                { "submeshCount", materials.Length },
                { "bounds", new Dictionary<string, object> {
                    { "center", V(b.center) }, { "size", V(b.size) } } },
            };
        }

        // ─────────────────────────────────────────────
        //  Geometry edits
        // ─────────────────────────────────────────────

        public static object ExtrudeFaces(Dictionary<string, object> args)
        {
            if (!TryResolve(args, out var pb, out var err)) return err;
            var faces = ResolveFaces(pb, args, out var faceErr);
            if (faceErr != null) return faceErr;
            float distance = GetFloat(args, "distance", 0.5f);
            var method = ParseExtrudeMethod(args);

            UndoRecord(pb, "Extrude Faces");
            var newFaces = ExtrudeElements.Extrude(pb, faces, method, distance);
            Rebuild(pb);
            return Ok(pb, new Dictionary<string, object> { { "extrudedFaces", faces.Count }, { "newFaces", newFaces?.Length ?? 0 }, { "distance", distance } });
        }

        public static object BevelEdges(Dictionary<string, object> args)
        {
            if (!TryResolve(args, out var pb, out var err)) return err;
            var faces = ResolveFaces(pb, args, out var faceErr);
            if (faceErr != null) return faceErr;
            float amount = GetFloat(args, "amount", 0.1f);
            // Bevel every edge of the selected faces (a face-driven bevel — the common case).
            var edges = faces.SelectMany(f => f.edges).Distinct().ToList();

            UndoRecord(pb, "Bevel Edges");
            Bevel.BevelEdges(pb, edges, amount);
            Rebuild(pb);
            return Ok(pb, new Dictionary<string, object> { { "beveledEdges", edges.Count }, { "amount", amount } });
        }

        public static object Subdivide(Dictionary<string, object> args)
        {
            if (!TryResolve(args, out var pb, out var err)) return err;
            var faces = args.ContainsKey("faceIndices") ? ResolveFaces(pb, args, out var fe) : pb.faces.ToList();
            if (faces == null) return Error("Invalid faceIndices.");

            UndoRecord(pb, "Subdivide");
            ConnectElements.Connect(pb, faces);
            Rebuild(pb);
            return Ok(pb, new Dictionary<string, object> { { "subdividedFaces", faces.Count } });
        }

        public static object DeleteFaces(Dictionary<string, object> args)
        {
            if (!TryResolve(args, out var pb, out var err)) return err;
            var faces = ResolveFaces(pb, args, out var faceErr);
            if (faceErr != null) return faceErr;
            if (faces.Count >= pb.faceCount)
                return Error("Refusing to delete every face (that would empty the mesh). Delete a subset or delete the GameObject instead.");

            UndoRecord(pb, "Delete Faces");
            DeleteElements.DeleteFaces(pb, faces);
            Rebuild(pb);
            return Ok(pb, new Dictionary<string, object> { { "deletedFaces", faces.Count } });
        }

        public static object TranslateFaces(Dictionary<string, object> args)
        {
            if (!TryResolve(args, out var pb, out var err)) return err;
            var faces = ResolveFaces(pb, args, out var faceErr);
            if (faceErr != null) return faceErr;
            var d = args.ContainsKey("translation") && args["translation"] is Dictionary<string, object> t
                ? new Vector3(GetFloat(t, "x", 0), GetFloat(t, "y", 0), GetFloat(t, "z", 0))
                : Vector3.zero;

            UndoRecord(pb, "Move Faces");
            var indexes = faces.SelectMany(f => f.distinctIndexes).Distinct();
            pb.TranslateVertices(faces.SelectMany(f => f.indexes).Distinct().Select(i => i).ToArray(), d);
            Rebuild(pb);
            return Ok(pb, new Dictionary<string, object> { { "movedFaces", faces.Count }, { "translation", V(d) } });
        }

        public static object FlipNormals(Dictionary<string, object> args)
        {
            if (!TryResolve(args, out var pb, out var err)) return err;
            UndoRecord(pb, "Flip Normals");
            foreach (var f in pb.faces) f.Reverse();
            Rebuild(pb);
            return Ok(pb, new Dictionary<string, object> { { "flippedFaces", pb.faceCount } });
        }

        // ─────────────────────────────────────────────
        //  Materials
        // ─────────────────────────────────────────────

        public static object SetFaceMaterial(Dictionary<string, object> args)
        {
            if (!TryResolve(args, out var pb, out var err)) return err;
            if (!args.ContainsKey("material")) return Error("material (asset path) is required.");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(args["material"].ToString());
            if (mat == null) return Error($"Material not found: {args["material"]}");
            var faces = args.ContainsKey("faceIndices") ? ResolveFaces(pb, args, out var fe) : pb.faces.ToList();
            if (faces == null) return Error("Invalid faceIndices.");

            UndoRecord(pb, "Set Face Material");
            pb.SetMaterial(faces, mat);
            Rebuild(pb);
            return Ok(pb, new Dictionary<string, object> { { "material", mat.name }, { "faces", faces.Count } });
        }

        // ─────────────────────────────────────────────
        //  Boolean / combine / convert
        // ─────────────────────────────────────────────

        public static object BooleanOp(Dictionary<string, object> args)
        {
            string op = args.ContainsKey("operation") ? args["operation"].ToString().ToLowerInvariant() : "union";
            var a = ResolveGameObject(args, "targetPath", "targetInstanceId");
            var b = ResolveGameObject(args, "otherPath", "otherInstanceId");
            if (a == null || b == null) return Error("Both targetPath/targetInstanceId and otherPath/otherInstanceId must resolve to GameObjects.");

            string method;
            switch (op)
            {
                case "union": method = "Union"; break;
                case "subtract": case "difference": method = "Subtract"; break;
                case "intersect": case "intersection": method = "Intersect"; break;
                default: return Error($"Unknown boolean operation '{op}'. Use union, subtract, or intersect.");
            }
            // CSG builds a material lookup table and throws on a null key — meshes with
            // missing material slots (e.g. hand-built or older shapes) get ProBuilder's
            // default assigned first (Undo-tracked, disclosed in the result).
            bool materialsDefaulted = EnsureRendererMaterials(a) | EnsureRendererMaterials(b);
            // ProBuilder's CSG class is internal, so invoke it via reflection.
            var mesh = InvokeCsg(method, a, b, out var csgErr);
            if (mesh == null) return Error(csgErr ?? "Boolean operation produced no mesh (are both objects solid meshes?).");
            var result = mesh;

            // CSG output vertices are in WORLD space — the result object must sit at the
            // identity transform (setting it to an operand's position double-offsets the mesh).
            var go = new GameObject($"PB_Boolean_{op}");
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = result;
            var mr = go.AddComponent<MeshRenderer>();
            var srcMat = a.GetComponent<MeshRenderer>() != null ? a.GetComponent<MeshRenderer>().sharedMaterial : null;
            mr.sharedMaterial = srcMat;
            // Make the boolean result editable in ProBuilder: add a ProBuilderMesh to the
            // result object and import the generated mesh into it. Pass the CSG mesh directly —
            // AddComponent<ProBuilderMesh> nulls the MeshFilter's sharedMesh.
            var pb = Undo.AddComponent<ProBuilderMesh>(go);
            var imported = MeshImporter_TryImport(pb, result, srcMat != null ? new[] { srcMat } : new Material[0]);
            if (imported)
            {
                Rebuild(pb);
                // Move the pivot from the world origin to the geometry center so the result
                // behaves like a normal object for later transforms/edits.
                pb.CenterPivot(null);
                Rebuild(pb);
            }
            Undo.RegisterCreatedObjectUndo(go, "ProBuilder Boolean");

            var boolResult = new Dictionary<string, object>
            {
                { "success", true },
                { "operation", op },
                { "name", go.name },
                { "instanceId", MCPObjectId.Get(go) },
                { "vertexCount", result.vertexCount },
                { "editableProBuilder", imported },
            };
            if (materialsDefaulted) boolResult["materialsDefaulted"] = true;
            return boolResult;
        }

        public static object Combine(Dictionary<string, object> args)
        {
            if (!(args.ContainsKey("paths") && args["paths"] is List<object> raw) || raw.Count < 2)
                return Error("paths (array of >= 2 GameObject paths) is required.");
            var meshes = new List<ProBuilderMesh>();
            foreach (var o in raw)
            {
                var go = GameObject.Find(o.ToString());
                var pb = go != null ? go.GetComponent<ProBuilderMesh>() : null;
                if (pb == null) return Error($"'{o}' is not a ProBuilder object (ProBuilderize it first).");
                meshes.Add(pb);
            }
            var result = CombineMeshes.Combine(meshes, meshes[0]);
            foreach (var pb in result) Rebuild(pb);
            // ProBuilder merges into the first mesh and destroys the rest.
            for (int i = 1; i < meshes.Count; i++)
                if (meshes[i] != null) Undo.DestroyObjectImmediate(meshes[i].gameObject);
            return Ok(meshes[0], new Dictionary<string, object> { { "combined", raw.Count } });
        }

        public static object ProBuilderize(Dictionary<string, object> args)
        {
            var go = ResolveGameObject(args, "path", "instanceId");
            if (go == null) return Error("GameObject not found.");
            if (go.GetComponent<ProBuilderMesh>() != null) return Error("GameObject is already a ProBuilder object.");
            var mf = go.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return Error("GameObject has no MeshFilter/mesh to convert.");
            // Capture source mesh + materials BEFORE adding ProBuilderMesh (which nulls sharedMesh).
            var srcMesh = mf.sharedMesh;
            var srcMats = go.GetComponent<MeshRenderer>() != null ? go.GetComponent<MeshRenderer>().sharedMaterials : new Material[0];

            var pb = Undo.AddComponent<ProBuilderMesh>(go);
            bool ok = MeshImporter_TryImport(pb, srcMesh, srcMats);
            if (!ok) { Undo.DestroyObjectImmediate(pb); return Error("Failed to import the mesh into ProBuilder."); }
            Rebuild(pb);
            return Ok(pb, new Dictionary<string, object> { { "converted", true } });
        }

        // ─────────────────────────────────────────────
        //  Pivot / export
        // ─────────────────────────────────────────────

        public static object CenterPivot(Dictionary<string, object> args)
        {
            if (!TryResolve(args, out var pb, out var err)) return err;
            UndoRecord(pb, "Center Pivot");
            pb.CenterPivot(null);
            Rebuild(pb);
            return Ok(pb, new Dictionary<string, object> { { "centeredPivot", true } });
        }

        public static object ExportMesh(Dictionary<string, object> args)
        {
            if (!TryResolve(args, out var pb, out var err)) return err;
            // Distinct key from the 'path' used to RESOLVE the object, so an object identified by
            // hierarchy path can still be exported to a chosen asset path.
            if (!args.ContainsKey("outputPath") || args["outputPath"] == null)
                return Error("outputPath (e.g. 'Assets/Meshes/MyMesh.asset') is required.");
            string path = args["outputPath"].ToString();
            if (!path.EndsWith(".asset")) path += ".asset";
            if (!MCPAssetSafety.TryResolveProjectPath(path, out _, out var pathError)) return Error(pathError);
            var over = MCPAssetSafety.OverwriteGuard(path, args);
            if (over != null) return over;

            var mesh = UnityEngine.Object.Instantiate(pb.GetComponent<MeshFilter>().sharedMesh);
            mesh.name = pb.name;
            AssetDatabase.CreateAsset(mesh, MCPAssetSafety.ToAssetDatabasePath(path));
            AssetDatabase.SaveAssets();
            return new Dictionary<string, object> { { "success", true }, { "assetPath", path }, { "vertexCount", mesh.vertexCount } };
        }

        // ─────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────

        private static void Rebuild(ProBuilderMesh pb)
        {
            pb.ToMesh();
            pb.Refresh();
            UnityEditor.ProBuilder.EditorUtility.SynchronizeWithMeshFilter(pb);
        }

        private static void UndoRecord(ProBuilderMesh pb, string msg)
        {
            // Public Undo API — records the ProBuilderMesh so an undo restores geometry and
            // composes with the queue's per-action undo-group tracking.
            Undo.RegisterCompleteObjectUndo(pb, msg);
        }

        /// <summary>
        /// Replace null material slots with ProBuilder's default material. CSG keys a
        /// dictionary by material and throws ("Value cannot be null. Parameter name: key")
        /// when any slot is null. Undo-tracked; returns whether anything changed.
        /// </summary>
        private static bool EnsureRendererMaterials(GameObject go)
        {
            var mr = go.GetComponent<MeshRenderer>();
            if (mr == null) return false;
            var mats = mr.sharedMaterials;
            bool changed = false;
            if (mats == null || mats.Length == 0)
            {
                mats = new[] { BuiltinMaterials.defaultMaterial };
                changed = true;
            }
            else
            {
                for (int i = 0; i < mats.Length; i++)
                    if (mats[i] == null) { mats[i] = BuiltinMaterials.defaultMaterial; changed = true; }
            }
            if (changed)
            {
                Undo.RegisterCompleteObjectUndo(mr, "Assign Default Material");
                mr.sharedMaterials = mats;
            }
            return changed;
        }

        /// <summary>Invoke ProBuilder's internal CSG.Union/Subtract/Intersect via reflection.</summary>
        private static Mesh InvokeCsg(string method, GameObject a, GameObject b, out string error)
        {
            error = null;
            try
            {
                var csg = System.AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(asm => { try { return asm.GetTypes(); } catch { return new System.Type[0]; } })
                    .FirstOrDefault(t => t.Name == "CSG" && t.Namespace != null && t.Namespace.Contains("ProBuilder"));
                if (csg == null) { error = "ProBuilder CSG type not found."; return null; }
                var mi = csg.GetMethod(method, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                    null, new[] { typeof(GameObject), typeof(GameObject) }, null);
                if (mi == null) { error = $"ProBuilder CSG.{method} not found."; return null; }
                // CSG.Union/Subtract/Intersect return a Csg.Model; its .mesh property is the built Mesh.
                var modelObj = mi.Invoke(null, new object[] { a, b });
                if (modelObj == null) { error = "Boolean operation returned no result."; return null; }
                var meshProp = modelObj.GetType().GetProperty("mesh");
                var mesh = meshProp != null ? meshProp.GetValue(modelObj) as Mesh : null;
                if (mesh == null) { error = "Boolean result had no mesh."; return null; }
                return mesh;
            }
            catch (System.Exception ex) { error = "Boolean op failed: " + (ex.InnerException ?? ex).Message; return null; }
        }

        private static bool TryResolve(Dictionary<string, object> args, out ProBuilderMesh pb, out object error)
        {
            pb = null; error = null;
            var go = ResolveGameObject(args, "path", "instanceId");
            if (go == null) { error = Error("GameObject not found (pass 'path' or 'instanceId')."); return false; }
            pb = go.GetComponent<ProBuilderMesh>();
            if (pb == null) { error = Error($"'{go.name}' is not a ProBuilder object. Use probuilder/probuilderize first."); return false; }
            return true;
        }

        private static GameObject ResolveGameObject(Dictionary<string, object> args, string pathKey, string idKey)
        {
            if (args.ContainsKey(idKey) && args[idKey] != null)
            {
                var obj = MCPObjectId.ToObject(args[idKey]);
                if (obj is GameObject g) return g;
                if (obj is Component c) return c.gameObject;
            }
            if (args.ContainsKey(pathKey) && args[pathKey] != null)
                return GameObject.Find(args[pathKey].ToString());
            return null;
        }

        private static List<Face> ResolveFaces(ProBuilderMesh pb, Dictionary<string, object> args, out object error)
        {
            error = null;
            if (!args.ContainsKey("faceIndices") || !(args["faceIndices"] is List<object> raw))
            {
                error = Error("faceIndices (array of face indices) is required. Use probuilder/info for the face count.");
                return null;
            }
            var faces = new List<Face>();
            foreach (var o in raw)
            {
                if (!int.TryParse(o.ToString(), out int idx) || idx < 0 || idx >= pb.faceCount)
                { error = Error($"Face index out of range: {o} (mesh has {pb.faceCount} faces)."); return null; }
                faces.Add(pb.faces[idx]);
            }
            if (faces.Count == 0) { error = Error("faceIndices is empty."); return null; }
            return faces;
        }

        /// <summary>
        /// Import a source mesh + materials into <paramref name="pb"/>. The mesh and materials
        /// MUST be captured by the caller BEFORE adding the ProBuilderMesh component — adding it
        /// resets the MeshFilter's sharedMesh to an empty mesh, so reading it afterwards yields null.
        /// </summary>
        private static bool MeshImporter_TryImport(ProBuilderMesh pb, Mesh sourceMesh, Material[] mats)
        {
            if (sourceMesh == null) return false;
            try
            {
                var importer = new MeshImporter(sourceMesh, mats ?? new Material[0], pb);
                importer.Import(new MeshImportSettings { quads = true, smoothing = true, smoothingAngle = 30f });
                return true;
            }
            catch { return false; }
        }

        private static ExtrudeMethod ParseExtrudeMethod(Dictionary<string, object> args)
        {
            string m = args.ContainsKey("method") ? args["method"].ToString().ToLowerInvariant() : "faceNormal";
            switch (m)
            {
                case "individual": case "individualfaces": return ExtrudeMethod.IndividualFaces;
                case "vertexnormal": return ExtrudeMethod.VertexNormal;
                default: return ExtrudeMethod.FaceNormal;
            }
        }

        private static object Ok(ProBuilderMesh pb, Dictionary<string, object> extra)
        {
            var d = new Dictionary<string, object>
            {
                { "success", true },
                { "name", pb.name },
                { "instanceId", MCPObjectId.Get(pb.gameObject) },
                { "faceCount", pb.faceCount },
                { "vertexCount", pb.vertexCount },
            };
            foreach (var kv in extra) d[kv.Key] = kv.Value;
            return d;
        }

        private static Dictionary<string, object> V(Vector3 v) =>
            new Dictionary<string, object> { { "x", v.x }, { "y", v.y }, { "z", v.z } };
#endif

        // ─── Shared arg helpers (available in both branches) ───
        private static object Error(string msg) => new Dictionary<string, object> { { "error", msg } };

        private static float GetFloat(Dictionary<string, object> a, string k, float def) =>
            a != null && a.ContainsKey(k) && a[k] != null && float.TryParse(a[k].ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : def;

        private static int GetInt(Dictionary<string, object> a, string k, int def) =>
            a != null && a.ContainsKey(k) && a[k] != null && int.TryParse(a[k].ToString(), out var i) ? i : def;

        private static bool GetBool(Dictionary<string, object> a, string k, bool def) =>
            a != null && a.ContainsKey(k) && a[k] != null && (a[k].ToString().ToLowerInvariant() == "true" || a[k].ToString() == "1") ? true : (a != null && a.ContainsKey(k) && a[k] != null ? false : def);
    }
}
