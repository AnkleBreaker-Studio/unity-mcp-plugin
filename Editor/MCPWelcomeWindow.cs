using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Welcome window shown on first editor startup.
    /// Introduces AnkleBreaker Unity MCP, encourages reviews,
    /// promotes GitHub Sponsors, and can be permanently dismissed.
    /// </summary>
    [InitializeOnLoad]
    public class MCPWelcomeWindow : EditorWindow
    {
        private const string HideKey = "UnityMCP_HideWelcome";
        private const string ShownSessionKey = "UnityMCP_WelcomeShownThisSession";

        private static readonly Vector2 WindowSize = new Vector2(520, 640);

        private Vector2 _scrollPosition;
        private GUIStyle _titleStyle;
        private GUIStyle _headingStyle;
        private GUIStyle _bodyStyle;
        private GUIStyle _italicStyle;
        private GUIStyle _linkButtonStyle;
        private GUIStyle _accentButtonStyle;
        private bool _stylesReady;

        // Colors
        private static readonly Color AccentBlue = new Color(0.35f, 0.6f, 0.95f);
        private static readonly Color AccentGold = new Color(0.95f, 0.75f, 0.2f);
        private static readonly Color HeartRed = new Color(0.9f, 0.25f, 0.3f);
        private static readonly Color SubtleGrey = new Color(0.6f, 0.6f, 0.6f);

        static MCPWelcomeWindow()
        {
            EditorApplication.delayCall += ShowOnStartup;
        }

        private static void ShowOnStartup()
        {
            // Don't show if user opted out permanently
            if (EditorPrefs.GetBool(HideKey, false))
                return;

            // Only show once per editor session (survives domain reloads)
            if (SessionState.GetBool(ShownSessionKey, false))
                return;

            SessionState.SetBool(ShownSessionKey, true);

            // Small delay so the editor is fully loaded
            EditorApplication.delayCall += () =>
            {
                var window = GetWindow<MCPWelcomeWindow>(true, "Welcome to AnkleBreaker Unity MCP", true);
                window.minSize = WindowSize;
                window.maxSize = new Vector2(WindowSize.x + 60, WindowSize.y + 120);
                window.ShowUtility();
                window.CenterOnScreen();
            };
        }

        private void CenterOnScreen()
        {
            var mainWindow = EditorGUIUtility.GetMainWindowPosition();
            var pos = position;
            pos.x = mainWindow.x + (mainWindow.width - pos.width) * 0.5f;
            pos.y = mainWindow.y + (mainWindow.height - pos.height) * 0.5f;
            position = pos;
        }

        private void InitStyles()
        {
            if (_stylesReady) return;

            _titleStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 22,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
                richText = true,
            };

            _headingStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                wordWrap = true,
                richText = true,
            };

            _bodyStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 12,
                wordWrap = true,
                richText = true,
            };

            _italicStyle = new GUIStyle(_bodyStyle)
            {
                fontStyle = FontStyle.Italic,
            };

            _linkButtonStyle = new GUIStyle(EditorStyles.miniButton)
            {
                fontSize = 11,
                fixedHeight = 28,
                richText = true,
            };

            _accentButtonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                fixedHeight = 36,
                richText = true,
            };

            _stylesReady = true;
        }

        private void OnGUI()
        {
            InitStyles();

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            EditorGUILayout.Space(12);

            // ─── Title ───
            EditorGUILayout.LabelField("AnkleBreaker Unity MCP", _titleStyle, GUILayout.Height(32));
            EditorGUILayout.Space(2);

            var prevColor = GUI.contentColor;
            GUI.contentColor = SubtleGrey;
            EditorGUILayout.LabelField("by AnkleBreaker Studio", new GUIStyle(_bodyStyle)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 11,
            });
            GUI.contentColor = prevColor;

            EditorGUILayout.Space(12);
            DrawSeparator();
            EditorGUILayout.Space(8);

            // ─── About ───
            EditorGUILayout.LabelField("Who we are", _headingStyle);
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(
                "AnkleBreaker Studio is a French game development team based near Paris, " +
                "working remotely and fueled by a shared passion for survival, competitive, " +
                "and RTS games. We're currently working on our upcoming title, <b>Mithrall</b>.",
                _bodyStyle);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(
                "We built the <b>Unity MCP</b> to bridge AI assistants directly into the Unity Editor \u2014 " +
                "giving you access to <b>200+ tools</b> that let AI read, modify, and interact with " +
                "your scenes, assets, scripts, and more.",
                _bodyStyle);

            EditorGUILayout.Space(12);

            // ─── Maintained ───
            EditorGUILayout.LabelField("Built for the long haul", _headingStyle);
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(
                "This MCP will be actively maintained by our team for as long as we develop " +
                "games on Unity \u2014 and that should be a <b>very</b> long time! We use it every day " +
                "ourselves, so you can count on it staying up-to-date and reliable.",
                _bodyStyle);

            EditorGUILayout.Space(12);

            // ─── Review request ───
            DrawBoxSection(
                "\u2B50  Leave us a review!",
                "If you find the Unity MCP useful, we'd really appreciate a star or review on " +
                "GitHub. It helps other developers discover the project and motivates us to keep " +
                "building awesome tools for the community.",
                "Star on GitHub \u2197",
                "https://github.com/AnkleBreaker-Studio/unity-mcp-plugin"
            );

            EditorGUILayout.Space(10);

            // ─── Premium MCPs ───
            DrawBoxSection(
                "\u26A1  Premium MCP Library",
                "Want even more power? We offer a curated library of premium MCPs for just " +
                "<b>$15/month</b> through GitHub Sponsors (Backer tier). You'll get access to our " +
                "<b>Discord MCP</b>, <b>Jira MCP</b>, <b>Git & GitHub MCP</b>, <b>LinkedIn MCP</b>, " +
                "<b>Plastic SCM MCP</b>, and more as we keep building.\n\n" +
                "Each one is crafted with the same care as this open-source plugin.",
                "Become a Sponsor \u2197",
                "https://github.com/sponsors/AnkleBreaker-Studio"
            );

            EditorGUILayout.Space(10);

            // ─── Open source encouragement ───
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.Space(4);

            prevColor = GUI.contentColor;
            GUI.contentColor = HeartRed;
            EditorGUILayout.LabelField("\u2764  Support open source", _headingStyle);
            GUI.contentColor = prevColor;

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(
                "Even if premium MCPs aren't for you, your support means the world to us. " +
                "A star, a kind word, a bug report, a contribution \u2014 every little bit " +
                "encourages us to keep making more tools and giving back to the community.\n\n" +
                "Thank you for being part of this journey with us!",
                _bodyStyle);
            EditorGUILayout.Space(4);
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(12);

            // ─── Links row ───
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Studio \u2197", _linkButtonStyle))
                Application.OpenURL("https://www.anklebreaker-studio.com");
            if (GUILayout.Button("Consulting \u2197", _linkButtonStyle))
                Application.OpenURL("https://www.anklebreaker-consulting.com");
            if (GUILayout.Button("Discord \u2197", _linkButtonStyle))
                Application.OpenURL("https://discord.gg/jrgNeUn6Ft");
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(16);
            DrawSeparator();
            EditorGUILayout.Space(8);

            // ─── Bottom buttons ───
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Don't show this again", EditorStyles.miniButton, GUILayout.Height(24)))
            {
                EditorPrefs.SetBool(HideKey, true);
                Close();
            }

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("  Got it, let's go!  ", _accentButtonStyle))
            {
                Close();
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(8);

            EditorGUILayout.EndScrollView();
        }

        // ─── Helpers ───

        private void DrawBoxSection(string heading, string body, string buttonLabel, string url)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(heading, _headingStyle);
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(body, _bodyStyle);
            EditorGUILayout.Space(6);

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(buttonLabel, _accentButtonStyle, GUILayout.Width(220)))
            {
                Application.OpenURL(url);
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
            EditorGUILayout.EndVertical();
        }

        private void DrawSeparator()
        {
            var rect = EditorGUILayout.GetControlRect(false, 1);
            rect.x += 20;
            rect.width -= 40;
            EditorGUI.DrawRect(rect, new Color(0.5f, 0.5f, 0.5f, 0.3f));
        }
    }
}
