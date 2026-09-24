using System;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace UnityMCP.Editor.Welcome
{
    /// <summary>
    /// Stable per-project token used to scope this window's <see cref="EditorPrefs"/>
    /// keys, so two projects on the same machine keep independent "first run" /
    /// "don't show again" state.
    /// </summary>
    internal static class UnityMcpWelcomeProjectHash
    {
        private static string s_cached;

        public static string Get()
        {
            if (s_cached != null) return s_cached;

            Guid productGuid = PlayerSettings.productGUID;
            if (productGuid != Guid.Empty)
            {
                s_cached = productGuid.ToString("N");
                return s_cached;
            }

            s_cached = HashDataPath(Application.dataPath);
            return s_cached;
        }

        private static string HashDataPath(string dataPath)
        {
            if (string.IsNullOrEmpty(dataPath)) return "unknown";

            using (System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(dataPath));
                System.Text.StringBuilder sb = new System.Text.StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }
    }

    /// <summary>
    /// Opens the welcome window once on first import, then on every Unity start
    /// until the user ticks "Don't show again" (gated per process by
    /// <see cref="SessionState"/>, per project by <see cref="EditorPrefs"/>).
    /// </summary>
    [InitializeOnLoad]
    internal static class UnityMcpWelcomeAutoOpen
    {
        private const string SESSION_KEY_OPENED = "UnityMcp.WelcomeWindow.OpenedThisSession";
        private const string EDITOR_PREFS_PREFIX = "UnityMcp.WelcomeWindow";
        private const string KEY_HAS_OPENED_ONCE = "HasOpenedOnce";
        private const string KEY_DONT_SHOW_AGAIN = "DontShowAgain";

        private const int MAX_DEFERRAL_TICKS = 30;
        private static int s_deferralTicks;

        static UnityMcpWelcomeAutoOpen()
        {
            EditorApplication.delayCall -= TryAutoOpen;
            EditorApplication.delayCall += TryAutoOpen;
        }

        private static string HasOpenedOnceKey => $"{EDITOR_PREFS_PREFIX}.{UnityMcpWelcomeProjectHash.Get()}.{KEY_HAS_OPENED_ONCE}";
        private static string DontShowAgainKey => $"{EDITOR_PREFS_PREFIX}.{UnityMcpWelcomeProjectHash.Get()}.{KEY_DONT_SHOW_AGAIN}";

        public static bool DontShowAgain
        {
            get => EditorPrefs.GetBool(DontShowAgainKey, false);
            set => EditorPrefs.SetBool(DontShowAgainKey, value);
        }

        private static bool HasOpenedOnce
        {
            get => EditorPrefs.GetBool(HasOpenedOnceKey, false);
            set => EditorPrefs.SetBool(HasOpenedOnceKey, value);
        }

        private static void TryAutoOpen()
        {
            if (SessionState.GetBool(SESSION_KEY_OPENED, false) || Application.isBatchMode) return;

            // Creating a window brings the editor to the front, even unfocused. An editor started
            // in the background (by an agent, a script) waits until the user comes back to it.
            if (!InternalEditorUtility.isApplicationActive)
            {
                EditorApplication.focusChanged -= OnFocusChanged;
                EditorApplication.focusChanged += OnFocusChanged;
                return;
            }

            if (EditorApplication.isCompiling
                || EditorApplication.isUpdating
                || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                if (s_deferralTicks++ >= MAX_DEFERRAL_TICKS) return;
                EditorApplication.delayCall += TryAutoOpen;
                return;
            }

            if (!HasOpenedOnce) { OpenAndMark(); return; }
            if (!DontShowAgain) OpenAndMark();
        }

        private static void OnFocusChanged(bool focused)
        {
            if (!focused) return;
            EditorApplication.focusChanged -= OnFocusChanged;
            s_deferralTicks = 0;
            EditorApplication.delayCall += TryAutoOpen;
        }

        private static void OpenAndMark()
        {
            SessionState.SetBool(SESSION_KEY_OPENED, true);
            HasOpenedOnce = true;
            UnityMcpWelcomeWindow.Open();
        }
    }
}
