using System;
using System.Collections.Generic;
using System.IO;
#if UNITY_EDITOR_WIN
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
#endif
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
            if (width < 1 || height < 1 || width > 8192 || height > 8192 || (long)width * height > 33554432)
                return new { error = "Scene screenshot dimensions must be 1-8192 pixels, with at most 33554432 pixels in total" };

            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
                return new { error = "No active Scene View found" };

            // Ensure directory exists
            string dir = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var camera = sceneView.camera;
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            RenderTexture rt = null;
            Texture2D tex = null;
            byte[] bytes;
            try
            {
                rt = new RenderTexture(width, height, 24);
                camera.targetTexture = rt;
                camera.Render();
                RenderTexture.active = rt;
                tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                bytes = tex.EncodeToPNG();
                File.WriteAllBytes(path, bytes);
            }
            finally
            {
                // A render or filesystem failure must not retain GPU resources or replace the editor's render targets.
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                if (rt != null) UnityEngine.Object.DestroyImmediate(rt);
            }

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

        // ─── Capture an arbitrary EditorWindow (Inspector, Project, custom windows…) ───
        // Unlike Game/Scene view (which ARE cameras and are captured by rendering the camera
        // into a RenderTexture), an EditorWindow is an IMGUI/UI-Toolkit window with no camera.
        // The only way to get its pixels is to capture the OS window, via the Win32 PrintWindow
        // API (occlusion-proof: the window renders itself into an offscreen DC, so no raise/focus
        // is needed). PLATFORM: Windows editor only — PrintWindow is a Win32 API with no macOS/
        // Linux equivalent, so this command is unsupported there (returns a clear error).
        //
        // args: window (required — EditorWindow type FullName e.g. "UnityEditor.InspectorWindow",
        //       simple type name, or tab title); path (optional — default Assets/Screenshots/…,
        //       any user-chosen .png path is honoured); maxDimension (optional, default 8192).
        public static object CaptureEditorWindow(Dictionary<string, object> args)
        {
#if UNITY_EDITOR_WIN
            string window = args != null && args.ContainsKey("window") ? args["window"].ToString()
                          : args != null && args.ContainsKey("typeOrTitle") ? args["typeOrTitle"].ToString() : "";
            if (string.IsNullOrEmpty(window))
                return Err("'window' is required (EditorWindow type FullName e.g. 'UnityEditor.InspectorWindow', simple type name, or tab title).");

            string path = args != null && args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                path = "Assets/Screenshots/EditorWindow_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
            if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                return Err("path must end in .png");
            int maxDimension = args != null && args.ContainsKey("maxDimension") ? Convert.ToInt32(args["maxDimension"]) : 8192;

            var win = FindWindow(window, out int matchCount);
            if (win == null)
                return Err(matchCount > 1
                    ? "Ambiguous: " + matchCount + " windows match '" + window + "'. Pass the exact type FullName."
                    : "No EditorWindow matches '" + window + "'.");

            var (pid, main) = ProcInfo();
            if (main == IntPtr.Zero) return Err("Could not resolve the main editor window handle.");

            bool floating = IsFloating(win);
            EditorWindow prevFocus = EditorWindow.focusedWindow;
            try
            {
                // Only the docked path needs the tab activated; a floating window is captured by
                // its own HWND, so it is never raised/focused (keeps the occlusion-proof promise).
                if (!floating) win.Focus();
                win.Repaint();
                RepaintImmediately(win);

                IntPtr hwnd; bool whole; int px = 0, py = 0, pw = 0, ph = 0;
                if (floating)
                {
                    hwnd = FindHwndByTitleExact(win, pid, main, out int n);
                    if (hwnd == IntPtr.Zero)
                        return Err(n > 1 ? "Ambiguous floating-window HWND (" + n + " matches)."
                                         : "Floating-window HWND not found (minimized or on another virtual desktop?).");
                    whole = true;
                }
                else
                {
                    hwnd = main; whole = false;
                    float ppp = EditorGUIUtility.pixelsPerPoint;
                    var rp = win.position;
                    px = (int)Math.Round(rp.x * ppp); py = (int)Math.Round(rp.y * ppp);
                    pw = (int)Math.Round(rp.width * ppp); ph = (int)Math.Round(rp.height * ppp);
                    if (pw <= 0 || ph <= 0) return Err("Bad panel rect " + pw + "x" + ph);
                }

                return GrabAndEncode(hwnd, whole, px, py, pw, ph, path, maxDimension, win, floating);
            }
            finally
            {
                // Restore the user's previously-focused tab (only the docked path changed it).
                if (!floating && prevFocus != null && prevFocus != win)
                    try { prevFocus.Focus(); } catch { }
            }
#else
            return new Dictionary<string, object>
            {
                { "success", false },
                { "error", "CaptureEditorWindow is Windows-only: it uses the Win32 PrintWindow API to grab an EditorWindow's pixels, which has no macOS/Linux equivalent. For the game or scene view use screenshot/game or screenshot/scene (camera-based, cross-platform)." },
                { "platform", Application.platform.ToString() },
            };
#endif
        }

#if UNITY_EDITOR_WIN
        static Dictionary<string, object> Err(string msg, int win32 = 0)
        {
            var d = new Dictionary<string, object> { { "success", false }, { "error", msg } };
            if (win32 != 0) d["win32Error"] = win32;
            return d;
        }

        // All GDI handles and the Texture2D are released in finally (deselect-before-delete).
        static Dictionary<string, object> GrabAndEncode(IntPtr hwnd, bool wholeWindow,
            int panelX, int panelY, int panelW, int panelH,
            string path, int maxDimension, EditorWindow win, bool floating)
        {
            if (IsIconic(hwnd)) return Err("Window is minimized.");
            if (!GetWindowRect(hwnd, out RECT wr)) return Err("GetWindowRect failed.", Marshal.GetLastWin32Error());
            int winW = wr.right - wr.left, winH = wr.bottom - wr.top;
            if (winW <= 0 || winH <= 0) return Err("Bad window rect " + winW + "x" + winH);

            int cropX, cropY, cropW, cropH;
            if (wholeWindow) { cropX = 0; cropY = 0; cropW = winW; cropH = winH; }
            else
            {
                cropX = panelX - wr.left; cropY = panelY - wr.top;
                if (cropX < 0 || cropY < 0) return Err("Panel offscreen (crop " + cropX + "," + cropY + "); DPI / multi-monitor mismatch?");
                cropW = panelW; cropH = panelH;
                if (cropX + cropW > winW) cropW = winW - cropX;
                if (cropY + cropH > winH) cropH = winH - cropY;
                if (cropW <= 0 || cropH <= 0) return Err("Bad crop " + cropW + "x" + cropH);
            }

            // Bound dimensions before allocating: int-overflow guard (long math) + GPU limit.
            int maxTex = SystemInfo.maxTextureSize; if (maxTex <= 0) maxTex = 8192;
            int cap = Math.Min(maxDimension > 0 ? maxDimension : int.MaxValue, maxTex);
            if (cropW > cap || cropH > cap) return Err("Too large " + cropW + "x" + cropH + " (cap " + cap + ")");
            long need = (long)cropW * cropH * 4L;
            if (need > int.MaxValue) return Err("Capture buffer too large (" + need + " B)");

            IntPtr hScreen = IntPtr.Zero, hMemFull = IntPtr.Zero, hBmpFull = IntPtr.Zero, oldFull = IntPtr.Zero;
            IntPtr hMemCrop = IntPtr.Zero, hBmpCrop = IntPtr.Zero, oldCrop = IntPtr.Zero;
            try
            {
                hScreen = GetDC(IntPtr.Zero); if (hScreen == IntPtr.Zero) return Err("GetDC failed.", Marshal.GetLastWin32Error());
                hMemFull = CreateCompatibleDC(hScreen); if (hMemFull == IntPtr.Zero) return Err("CreateCompatibleDC failed.", Marshal.GetLastWin32Error());
                hBmpFull = CreateCompatibleBitmap(hScreen, winW, winH); if (hBmpFull == IntPtr.Zero) return Err("CreateCompatibleBitmap failed.", Marshal.GetLastWin32Error());
                oldFull = SelectObject(hMemFull, hBmpFull);

                if (!PrintWindow(hwnd, hMemFull, PW_RENDERFULLCONTENT)) return Err("PrintWindow failed.", Marshal.GetLastWin32Error());

                IntPtr grabBmp;
                if (wholeWindow)
                {
                    // GetDIBits needs the bitmap deselected; oldFull is NOT nulled so the finally
                    // re-asserts the deselect even if this SelectObject fails (else DeleteObject leaks).
                    SelectObject(hMemFull, oldFull);
                    grabBmp = hBmpFull;
                }
                else
                {
                    hMemCrop = CreateCompatibleDC(hScreen); if (hMemCrop == IntPtr.Zero) return Err("CreateCompatibleDC(2) failed.", Marshal.GetLastWin32Error());
                    hBmpCrop = CreateCompatibleBitmap(hScreen, cropW, cropH); if (hBmpCrop == IntPtr.Zero) return Err("CreateCompatibleBitmap(2) failed.", Marshal.GetLastWin32Error());
                    oldCrop = SelectObject(hMemCrop, hBmpCrop);
                    if (!BitBlt(hMemCrop, 0, 0, cropW, cropH, hMemFull, cropX, cropY, SRCCOPY)) return Err("BitBlt failed.", Marshal.GetLastWin32Error());
                    SelectObject(hMemCrop, oldCrop);
                    grabBmp = hBmpCrop;
                }

                var bmi = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = cropW, biHeight = cropH, // positive = bottom-up, matches Texture2D row order
                    biPlanes = 1, biBitCount = 32, biCompression = 0
                };
                byte[] buf = new byte[cropW * cropH * 4];
                int scan = GetDIBits(hScreen, grabBmp, 0, (uint)cropH, buf, ref bmi, DIB_RGB_COLORS);
                if (scan == 0) return Err("GetDIBits failed.", Marshal.GetLastWin32Error());
                if (scan != cropH) return Err("GetDIBits partial (" + scan + "/" + cropH + ").");

                // All-black detection (GPU refused PW_RENDERFULLCONTENT): aligned RGB sample.
                long sum = 0; int stride = Math.Max(1, (cropW * cropH) / 4096) * 4;
                for (int i = 0; i + 2 < buf.Length; i += stride) sum += buf[i] + buf[i + 1] + buf[i + 2];
                if (sum == 0) return Err("All-black frame (GPU refused PW_RENDERFULLCONTENT).");

                byte[] png = EncodeBgraBottomUp(buf, cropW, cropH);
                if (png == null || png.Length == 0) return Err("PNG encode failed.");

                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(path, png);
                string normalized = path.Replace('\\', '/');
                if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || normalized.Contains("/Assets/"))
                    AssetDatabase.Refresh();

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "path", path },
                    { "width", cropW },
                    { "height", cropH },
                    { "sizeBytes", png.Length },
                    { "window", win.GetType().FullName },
                    { "floating", floating },
                };
            }
            finally
            {
                if (oldCrop != IntPtr.Zero && hMemCrop != IntPtr.Zero) SelectObject(hMemCrop, oldCrop);
                if (hBmpCrop != IntPtr.Zero) DeleteObject(hBmpCrop);
                if (hMemCrop != IntPtr.Zero) DeleteDC(hMemCrop);
                if (oldFull != IntPtr.Zero && hMemFull != IntPtr.Zero) SelectObject(hMemFull, oldFull);
                if (hBmpFull != IntPtr.Zero) DeleteObject(hBmpFull);
                if (hMemFull != IntPtr.Zero) DeleteDC(hMemFull);
                if (hScreen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hScreen);
            }
        }

        // GDI 32bpp DIB is BGRA; Texture2D wants RGBA + bottom-up rows (our positive biHeight).
        // Alpha is forced opaque (PrintWindow leaves it garbage). Texture released in finally.
        static byte[] EncodeBgraBottomUp(byte[] bgra, int w, int h)
        {
            var cols = new Color32[w * h];
            for (int i = 0; i < cols.Length; i++) { int o = i * 4; cols[i] = new Color32(bgra[o + 2], bgra[o + 1], bgra[o], 255); }
            Texture2D tex = null;
            try { tex = new Texture2D(w, h, TextureFormat.RGBA32, false); tex.SetPixels32(cols); tex.Apply(false); return tex.EncodeToPNG(); }
            finally { if (tex != null) UnityEngine.Object.DestroyImmediate(tex); }
        }

        // Exact FullName → exact simple type name → exact title → unambiguous substring.
        static EditorWindow FindWindow(string typeOrTitle, out int matchCount)
        {
            matchCount = 0;
            if (string.IsNullOrEmpty(typeOrTitle)) return null;
            var wins = Resources.FindObjectsOfTypeAll<EditorWindow>();
            foreach (var w in wins) if (w != null && w.GetType().FullName == typeOrTitle) { matchCount = 1; return w; }

            EditorWindow nameHit = null; int nameN = 0;
            foreach (var w in wins) if (w != null && string.Equals(w.GetType().Name, typeOrTitle, StringComparison.Ordinal)) { nameN++; if (nameHit == null) nameHit = w; }
            if (nameN >= 1) { matchCount = nameN; return nameN == 1 ? nameHit : null; }

            EditorWindow exact = null; int exactN = 0;
            foreach (var w in wins) { if (w == null) continue; var t = w.titleContent != null ? w.titleContent.text : w.name; if (!string.IsNullOrEmpty(t) && string.Equals(t, typeOrTitle, StringComparison.OrdinalIgnoreCase)) { exactN++; if (exact == null) exact = w; } }
            if (exactN >= 1) { matchCount = exactN; return exactN == 1 ? exact : null; }

            EditorWindow sub = null; int subN = 0;
            foreach (var w in wins) { if (w == null) continue; var t = w.titleContent != null ? w.titleContent.text : w.name; if (!string.IsNullOrEmpty(t) && t.IndexOf(typeOrTitle, StringComparison.OrdinalIgnoreCase) >= 0) { subN++; if (sub == null) sub = w; } }
            matchCount = subN; return subN == 1 ? sub : null;
        }

        // EditorWindow.docked is internal → reflected, guarded. Unknown ⇒ docked (main + crop).
        static bool IsFloating(EditorWindow win)
        {
            try { var p = typeof(EditorWindow).GetProperty("docked", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public); if (p != null && p.GetValue(win, null) is bool d) return !d; } catch { }
            return false;
        }

        // EditorWindow.RepaintImmediately is internal → reflected, guarded best-effort (H3).
        static void RepaintImmediately(EditorWindow win)
        {
            try { var m = typeof(EditorWindow).GetMethod("RepaintImmediately", BindingFlags.Instance | BindingFlags.NonPublic); if (m != null) m.Invoke(win, null); } catch { }
        }

        static (int pid, IntPtr main) ProcInfo()
        {
            using (var p = Process.GetCurrentProcess()) return (p.Id, p.MainWindowHandle);
        }

        // A floating EditorWindow's OS window carries its tab title (or type name). Exact match,
        // excludes the main editor HWND, reports the count so ambiguity is surfaced not guessed.
        static IntPtr FindHwndByTitleExact(EditorWindow win, int pid, IntPtr main, out int count)
        {
            string t1 = win.titleContent != null ? win.titleContent.text : null;
            string t2 = win.GetType().Name;
            IntPtr found = IntPtr.Zero; int n = 0;
            EnumWindows((h, l) =>
            {
                // Never let a managed exception unwind through the native frame.
                try
                {
                    if (h == main) return true;
                    if (!IsWindowVisible(h)) return true;
                    GetWindowThreadProcessId(h, out uint wp);
                    if (wp != (uint)pid) return true;
                    int len = GetWindowTextLength(h); if (len <= 0) return true;
                    var sb = new System.Text.StringBuilder(len + 1);
                    GetWindowText(h, sb, sb.Capacity);
                    string title = sb.ToString();
                    if ((t1 != null && string.Equals(title, t1, StringComparison.OrdinalIgnoreCase)) || string.Equals(title, t2, StringComparison.OrdinalIgnoreCase))
                    { n++; if (found == IntPtr.Zero) found = h; }
                }
                catch { }
                return true;
            }, IntPtr.Zero);
            count = n; return n == 1 ? found : IntPtr.Zero;
        }

        // ── Win32 P/Invoke + GDI types (Windows editor only) ──
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder s, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr hWnd);
        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("gdi32.dll", SetLastError = true)] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll", SetLastError = true)] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool BitBlt(IntPtr hdcDest, int xD, int yD, int w, int h, IntPtr hdcSrc, int xS, int yS, uint rop);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DeleteObject(IntPtr ho);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll", SetLastError = true)] static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);

        const uint SRCCOPY = 0x00CC0020;
        const uint PW_RENDERFULLCONTENT = 0x00000002; // Windows 8.1+
        const uint DIB_RGB_COLORS = 0;
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public uint biSize; public int biWidth; public int biHeight;
            public ushort biPlanes; public ushort biBitCount; public uint biCompression;
            public uint biSizeImage; public int biXPPM; public int biYPPM;
            public uint biClrUsed; public uint biClrImportant;
        }
#endif
    }
}
