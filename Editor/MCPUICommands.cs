using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Commands for creating and managing Unity UI (UGUI) elements.
    /// </summary>
    public static class MCPUICommands
    {
        // ─── Create Canvas ───

        public static object CreateCanvas(Dictionary<string, object> args)
        {
            string name = args.ContainsKey("name") ? args["name"].ToString() : "Canvas";
            string renderMode = args.ContainsKey("renderMode") ? args["renderMode"].ToString().ToLower() : "overlay";

            var canvasGo = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(canvasGo, "Create Canvas");

            var canvas = canvasGo.AddComponent<Canvas>();
            switch (renderMode)
            {
                case "camera": canvas.renderMode = RenderMode.ScreenSpaceCamera; break;
                case "world": canvas.renderMode = RenderMode.WorldSpace; break;
                default: canvas.renderMode = RenderMode.ScreenSpaceOverlay; break;
            }

            canvasGo.AddComponent<CanvasScaler>();
            canvasGo.AddComponent<GraphicRaycaster>();

            // Ensure EventSystem exists
            if (UnityEngine.Object.FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() == null)
            {
                var esGo = new GameObject("EventSystem");
                esGo.AddComponent<UnityEngine.EventSystems.EventSystem>();
                esGo.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
                Undo.RegisterCreatedObjectUndo(esGo, "Create EventSystem");
            }

            return new Dictionary<string, object>
            {
                { "success", true },
                { "name", canvasGo.name },
                { "renderMode", canvas.renderMode.ToString() },
                { "instanceId", canvasGo.GetInstanceID() },
            };
        }

        // ─── Create UI Element ───

        public static object CreateUIElement(Dictionary<string, object> args)
        {
            string type = args.ContainsKey("type") ? args["type"].ToString().ToLower() : "";
            if (string.IsNullOrEmpty(type))
                return new { error = "type is required (text, image, button, panel, slider, toggle, inputfield, dropdown, scrollview)" };

            string name = args.ContainsKey("name") ? args["name"].ToString() : type;
            string parent = args.ContainsKey("parent") ? args["parent"].ToString() : "";

            // Find or create canvas
            Transform parentTransform = null;
            if (!string.IsNullOrEmpty(parent))
            {
                var parentGo = GameObject.Find(parent);
                if (parentGo != null)
                    parentTransform = parentGo.transform;
            }
            if (parentTransform == null)
            {
                var canvas = UnityEngine.Object.FindFirstObjectByType<Canvas>();
                if (canvas == null)
                    return new { error = "No Canvas found. Create a canvas first." };
                parentTransform = canvas.transform;
            }

            GameObject go = null;
            switch (type)
            {
                case "text":
                    go = CreateTextElement(name);
                    break;
                case "image":
                    go = CreateImageElement(name);
                    break;
                case "button":
                    go = CreateButtonElement(name, args);
                    break;
                case "panel":
                    go = CreatePanelElement(name);
                    break;
                case "slider":
                    go = new GameObject(name);
                    go.AddComponent<RectTransform>();
                    go.AddComponent<Slider>();
                    break;
                case "toggle":
                    go = new GameObject(name);
                    go.AddComponent<RectTransform>();
                    go.AddComponent<Toggle>();
                    break;
                case "inputfield":
                    go = CreateInputFieldElement(name);
                    break;
                default:
                    return new { error = $"Unknown UI type '{type}'. Use: text, image, button, panel, slider, toggle, inputfield" };
            }

            go.transform.SetParent(parentTransform, false);
            Undo.RegisterCreatedObjectUndo(go, "Create UI Element");

            // Apply position if provided
            var rt = go.GetComponent<RectTransform>();
            if (rt != null)
            {
                if (args.ContainsKey("anchoredPosition") && args["anchoredPosition"] is Dictionary<string, object> posDict)
                {
                    float x = posDict.ContainsKey("x") ? Convert.ToSingle(posDict["x"]) : 0;
                    float y = posDict.ContainsKey("y") ? Convert.ToSingle(posDict["y"]) : 0;
                    rt.anchoredPosition = new Vector2(x, y);
                }
                if (args.ContainsKey("sizeDelta") && args["sizeDelta"] is Dictionary<string, object> sizeDict)
                {
                    float w = sizeDict.ContainsKey("x") ? Convert.ToSingle(sizeDict["x"]) : rt.sizeDelta.x;
                    float h = sizeDict.ContainsKey("y") ? Convert.ToSingle(sizeDict["y"]) : rt.sizeDelta.y;
                    rt.sizeDelta = new Vector2(w, h);
                }
            }

            return new Dictionary<string, object>
            {
                { "success", true },
                { "name", go.name },
                { "type", type },
                { "instanceId", go.GetInstanceID() },
                { "parent", parentTransform.name },
            };
        }

        // ─── Get UI Info ───

        public static object GetUIInfo(Dictionary<string, object> args)
        {
            var canvases = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var canvasInfos = new List<Dictionary<string, object>>();

            foreach (var canvas in canvases)
            {
                int childCount = CountUIElements(canvas.transform);
                canvasInfos.Add(new Dictionary<string, object>
                {
                    { "name", canvas.gameObject.name },
                    { "renderMode", canvas.renderMode.ToString() },
                    { "sortingOrder", canvas.sortingOrder },
                    { "uiElementCount", childCount },
                    { "instanceId", canvas.gameObject.GetInstanceID() },
                });
            }

            int totalTexts = UnityEngine.Object.FindObjectsByType<Text>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
            int totalImages = UnityEngine.Object.FindObjectsByType<Image>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
            int totalButtons = UnityEngine.Object.FindObjectsByType<Button>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;

            return new Dictionary<string, object>
            {
                { "canvasCount", canvases.Length },
                { "canvases", canvasInfos },
                { "totalTexts", totalTexts },
                { "totalImages", totalImages },
                { "totalButtons", totalButtons },
            };
        }

        // ─── Set UI Text ───

        public static object SetUIText(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            var go = GameObject.Find(path);
            if (go == null)
                return new { error = $"GameObject '{path}' not found" };

            var text = go.GetComponent<Text>();
            if (text == null)
                return new { error = $"No Text component on '{path}'" };

            Undo.RecordObject(text, "Set UI Text");

            if (args.ContainsKey("text"))
                text.text = args["text"].ToString();
            if (args.ContainsKey("fontSize"))
                text.fontSize = Convert.ToInt32(args["fontSize"]);
            if (args.ContainsKey("color") && args["color"] is Dictionary<string, object> colorDict)
            {
                float r = colorDict.ContainsKey("r") ? Convert.ToSingle(colorDict["r"]) : text.color.r;
                float g = colorDict.ContainsKey("g") ? Convert.ToSingle(colorDict["g"]) : text.color.g;
                float b = colorDict.ContainsKey("b") ? Convert.ToSingle(colorDict["b"]) : text.color.b;
                float a = colorDict.ContainsKey("a") ? Convert.ToSingle(colorDict["a"]) : text.color.a;
                text.color = new Color(r, g, b, a);
            }
            if (args.ContainsKey("alignment"))
            {
                string align = args["alignment"].ToString().ToLower();
                switch (align)
                {
                    case "upperleft": text.alignment = TextAnchor.UpperLeft; break;
                    case "uppercenter": text.alignment = TextAnchor.UpperCenter; break;
                    case "upperright": text.alignment = TextAnchor.UpperRight; break;
                    case "middleleft": text.alignment = TextAnchor.MiddleLeft; break;
                    case "middlecenter": text.alignment = TextAnchor.MiddleCenter; break;
                    case "middleright": text.alignment = TextAnchor.MiddleRight; break;
                    case "lowerleft": text.alignment = TextAnchor.LowerLeft; break;
                    case "lowercenter": text.alignment = TextAnchor.LowerCenter; break;
                    case "lowerright": text.alignment = TextAnchor.LowerRight; break;
                }
            }

            EditorUtility.SetDirty(text);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "path", path },
                { "text", text.text },
                { "fontSize", text.fontSize },
                { "alignment", text.alignment.ToString() },
            };
        }

        // ─── Set UI Image ───

        public static object SetUIImage(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            var go = GameObject.Find(path);
            if (go == null)
                return new { error = $"GameObject '{path}' not found" };

            var image = go.GetComponent<Image>();
            if (image == null)
                return new { error = $"No Image component on '{path}'" };

            Undo.RecordObject(image, "Set UI Image");

            if (args.ContainsKey("color") && args["color"] is Dictionary<string, object> colorDict)
            {
                float r = colorDict.ContainsKey("r") ? Convert.ToSingle(colorDict["r"]) : image.color.r;
                float g = colorDict.ContainsKey("g") ? Convert.ToSingle(colorDict["g"]) : image.color.g;
                float b = colorDict.ContainsKey("b") ? Convert.ToSingle(colorDict["b"]) : image.color.b;
                float a = colorDict.ContainsKey("a") ? Convert.ToSingle(colorDict["a"]) : image.color.a;
                image.color = new Color(r, g, b, a);
            }

            if (args.ContainsKey("sprite"))
            {
                string spritePath = args["sprite"].ToString();
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(spritePath);
                if (sprite != null)
                    image.sprite = sprite;
            }

            if (args.ContainsKey("imageType"))
            {
                string imgType = args["imageType"].ToString().ToLower();
                switch (imgType)
                {
                    case "simple": image.type = Image.Type.Simple; break;
                    case "sliced": image.type = Image.Type.Sliced; break;
                    case "tiled": image.type = Image.Type.Tiled; break;
                    case "filled": image.type = Image.Type.Filled; break;
                }
            }

            if (args.ContainsKey("raycastTarget"))
                image.raycastTarget = Convert.ToBoolean(args["raycastTarget"]);

            EditorUtility.SetDirty(image);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "path", path },
                { "hasSprite", image.sprite != null },
                { "imageType", image.type.ToString() },
            };
        }

        // ─── Helpers ───

        private static GameObject CreateTextElement(string name)
        {
            var go = new GameObject(name);
            var rt = go.AddComponent<RectTransform>();
            rt.sizeDelta = new Vector2(160, 30);
            var text = go.AddComponent<Text>();
            text.text = "New Text";
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.color = Color.black;
            text.alignment = TextAnchor.MiddleCenter;
            return go;
        }

        private static GameObject CreateImageElement(string name)
        {
            var go = new GameObject(name);
            var rt = go.AddComponent<RectTransform>();
            rt.sizeDelta = new Vector2(100, 100);
            go.AddComponent<Image>();
            return go;
        }

        private static GameObject CreateButtonElement(string name, Dictionary<string, object> args)
        {
            var go = new GameObject(name);
            var rt = go.AddComponent<RectTransform>();
            rt.sizeDelta = new Vector2(160, 30);
            go.AddComponent<Image>();
            go.AddComponent<Button>();

            // Add label
            var textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);
            var textRt = textGo.AddComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.sizeDelta = Vector2.zero;
            var text = textGo.AddComponent<Text>();
            text.text = args.ContainsKey("label") ? args["label"].ToString() : "Button";
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.color = Color.black;
            text.alignment = TextAnchor.MiddleCenter;

            return go;
        }

        private static GameObject CreatePanelElement(string name)
        {
            var go = new GameObject(name);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.sizeDelta = Vector2.zero;
            var image = go.AddComponent<Image>();
            image.color = new Color(1, 1, 1, 0.39f);
            return go;
        }

        private static GameObject CreateInputFieldElement(string name)
        {
            var go = new GameObject(name);
            var rt = go.AddComponent<RectTransform>();
            rt.sizeDelta = new Vector2(160, 30);
            var image = go.AddComponent<Image>();
            image.color = Color.white;

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);
            var textRt = textGo.AddComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.sizeDelta = new Vector2(-10, -6);
            var text = textGo.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.color = Color.black;
            text.supportRichText = false;

            var placeholderGo = new GameObject("Placeholder");
            placeholderGo.transform.SetParent(go.transform, false);
            var phRt = placeholderGo.AddComponent<RectTransform>();
            phRt.anchorMin = Vector2.zero;
            phRt.anchorMax = Vector2.one;
            phRt.sizeDelta = new Vector2(-10, -6);
            var phText = placeholderGo.AddComponent<Text>();
            phText.text = "Enter text...";
            phText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            phText.fontStyle = FontStyle.Italic;
            phText.color = new Color(0, 0, 0, 0.5f);

            var inputField = go.AddComponent<InputField>();
            inputField.textComponent = text;
            inputField.placeholder = phText;

            return go;
        }

        private static int CountUIElements(Transform parent)
        {
            int count = 0;
            foreach (Transform child in parent)
            {
                count++;
                count += CountUIElements(child);
            }
            return count;
        }
    }
}
