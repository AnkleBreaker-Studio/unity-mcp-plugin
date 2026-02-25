using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Commands for capturing screenshots from the Unity Editor.
    /// </summary>
    public static class MCPScreenshotCommands
    {
        // ─── Capture Game View ───

        public static object CaptureGameView(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                path = "Assets/Screenshots/GameView_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";

            int superSize = args.ContainsKey("superSize") ? Convert.ToInt32(args["superSize"]) : 1;

            // Ensure directory exists
            string dir = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            ScreenCapture.CaptureScreenshot(path, superSize);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "path", path },
                { "superSize", superSize },
                { "message", $"Screenshot will be saved to '{path}' on next frame render" },
            };
        }

        // ─── Capture Scene View ───

        public static object CaptureSceneView(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                path = "Assets/Screenshots/SceneView_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";

            int width = args.ContainsKey("width") ? Convert.ToInt32(args["width"]) : 1920;
            int height = args.ContainsKey("height") ? Convert.ToInt32(args["height"]) : 1080;

            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
                return new { error = "No active Scene View found" };

            // Ensure directory exists
            string dir = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var camera = sceneView.camera;
            var rt = new RenderTexture(width, height, 24);
            camera.targetTexture = rt;
            camera.Render();

            RenderTexture.active = rt;
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();

            camera.targetTexture = null;
            RenderTexture.active = null;

            byte[] bytes = tex.EncodeToPNG();
            File.WriteAllBytes(path, bytes);

            UnityEngine.Object.DestroyImmediate(tex);
            UnityEngine.Object.DestroyImmediate(rt);

            AssetDatabase.Refresh();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "path", path },
                { "width", width },
                { "height", height },
                { "sizeBytes", bytes.Length },
            };
        }

        // ─── Get Scene View Camera Info ───

        public static object GetSceneViewInfo(Dictionary<string, object> args)
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
                return new { error = "No active Scene View found" };

            var pivot = sceneView.pivot;
            var rotation = sceneView.rotation.eulerAngles;

            return new Dictionary<string, object>
            {
                { "pivot", new Dictionary<string, object>
                    {
                        { "x", pivot.x }, { "y", pivot.y }, { "z", pivot.z },
                    }
                },
                { "rotation", new Dictionary<string, object>
                    {
                        { "x", rotation.x }, { "y", rotation.y }, { "z", rotation.z },
                    }
                },
                { "size", sceneView.size },
                { "orthographic", sceneView.orthographic },
                { "is2D", sceneView.in2DMode },
                { "drawGizmos", sceneView.drawGizmos },
            };
        }

        // ─── Set Scene View Camera ───

        public static object SetSceneViewCamera(Dictionary<string, object> args)
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
                return new { error = "No active Scene View found" };

            var updated = new List<string>();

            if (args.ContainsKey("pivot") && args["pivot"] is Dictionary<string, object> pivotDict)
            {
                float x = pivotDict.ContainsKey("x") ? Convert.ToSingle(pivotDict["x"]) : sceneView.pivot.x;
                float y = pivotDict.ContainsKey("y") ? Convert.ToSingle(pivotDict["y"]) : sceneView.pivot.y;
                float z = pivotDict.ContainsKey("z") ? Convert.ToSingle(pivotDict["z"]) : sceneView.pivot.z;
                sceneView.pivot = new Vector3(x, y, z);
                updated.Add("pivot");
            }

            if (args.ContainsKey("rotation") && args["rotation"] is Dictionary<string, object> rotDict)
            {
                float x = rotDict.ContainsKey("x") ? Convert.ToSingle(rotDict["x"]) : 0;
                float y = rotDict.ContainsKey("y") ? Convert.ToSingle(rotDict["y"]) : 0;
                float z = rotDict.ContainsKey("z") ? Convert.ToSingle(rotDict["z"]) : 0;
                sceneView.rotation = Quaternion.Euler(x, y, z);
                updated.Add("rotation");
            }

            if (args.ContainsKey("size"))
            {
                sceneView.size = Convert.ToSingle(args["size"]);
                updated.Add("size");
            }

            if (args.ContainsKey("orthographic"))
            {
                sceneView.orthographic = Convert.ToBoolean(args["orthographic"]);
                updated.Add("orthographic");
            }

            if (args.ContainsKey("is2D"))
            {
                sceneView.in2DMode = Convert.ToBoolean(args["is2D"]);
                updated.Add("is2D");
            }

            if (args.ContainsKey("lookAt") && args["lookAt"] is Dictionary<string, object> lookDict)
            {
                float x = lookDict.ContainsKey("x") ? Convert.ToSingle(lookDict["x"]) : 0;
                float y = lookDict.ContainsKey("y") ? Convert.ToSingle(lookDict["y"]) : 0;
                float z = lookDict.ContainsKey("z") ? Convert.ToSingle(lookDict["z"]) : 0;
                float sz = args.ContainsKey("lookAtSize") ? Convert.ToSingle(args["lookAtSize"]) : 10f;
                sceneView.LookAt(new Vector3(x, y, z), sceneView.rotation, sz);
                updated.Add("lookAt");
            }

            if (args.ContainsKey("frameSelected") && Convert.ToBoolean(args["frameSelected"]))
            {
                sceneView.FrameSelected();
                updated.Add("frameSelected");
            }

            sceneView.Repaint();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "updated", updated },
            };
        }
    }
}
