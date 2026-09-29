using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace UnityMCP.Editor.Welcome
{
    /// <summary>
    /// The window a buyer lands on after the import: whether the project can run the package,
    /// what to press first, what is in the box, and the rest of the catalogue.
    ///
    /// <para>One shell for every AnkleBreaker package, two profiles of content (tool, art). The
    /// shell is this file; the left column is the .Start partial, the showcase panel and the
    /// Assets and Studio tabs the .Store partial. All text comes from the package's
    /// <c>*.welcome.json</c>.</para>
    ///
    /// <para>It lives in an assembly that references nothing, and reaches the package only
    /// through menu paths and scene names: it has to compile, and say what to install, precisely
    /// when the package itself does not.</para>
    /// </summary>
    internal sealed partial class UnityMcpWelcome : EditorWindow
    {
        private const string MENU_PATH = "Tools/AnkleBreaker/Unity MCP/Welcome";
        private const string STYLE_NAME = "UnityMcpWelcome";
        private static readonly Vector2 OPEN_SIZE = new Vector2(1180f, 810f);
        private static readonly Vector2 MIN_SIZE = new Vector2(980f, 680f);

        private ScrollView _panelScroll;

        [SerializeField] private string _contextGuid;
        [SerializeField] private string _tab = "start";
        [SerializeField] private string _filter = "all";

        private string _pendingAssetsFilter;

        private UnityMcpWelcomeContext _context;
        private UnityMcpCatalog _catalog;
        private ScrollView _scroll;

        [MenuItem(MENU_PATH, false, 0)]
        private static void OpenFromMenu() => Open(null);

        /// <summary>Opens this package context, or its first config when no GUID is supplied.</summary>
        public static void Open(string contextGuid)
        {
            UnityMcpWelcomeFirstOpen.OpenedThisSession = true;
            bool existed = HasOpenInstances<UnityMcpWelcome>();
            var window = GetWindow<UnityMcpWelcome>(false, "Welcome", true);
            window.minSize = MIN_SIZE;
            Rect main = EditorGUIUtility.GetMainWindowPosition();
            // GetWindow can return an existing off-screen window after a layout or monitor change.
            if (!existed || (!window.docked && !window.position.Overlaps(main)))
            {
                window.position = new Rect(
                    main.x + Mathf.Max(0f, (main.width - OPEN_SIZE.x) * 0.5f),
                    main.y + Mathf.Max(0f, (main.height - OPEN_SIZE.y) * 0.5f),
                    OPEN_SIZE.x, OPEN_SIZE.y);
            }
            if (!string.IsNullOrEmpty(contextGuid)) window._contextGuid = contextGuid;
            window._tab = "start";
            window._pendingAssetsFilter = null;
            window.Bind();
            window.MarkOpened();
            window.Rebuild();
            window.Show();
            window.Focus();
        }

        /// <summary>
        /// An ordinary window is written into the saved layout and comes back at every project
        /// open, long after onboarding. A utility window would not, but it has no dock tab and
        /// Windows hides it whenever the editor loses focus. So: an ordinary window that closes
        /// itself when the layout, rather than this code, brought it back.
        /// </summary>
        private void OnEnable()
        {
            if (!UnityMcpWelcomeFirstOpen.OpenedThisSession)
            {
                EditorApplication.delayCall += () =>
                {
                    if (this != null && !UnityMcpWelcomeFirstOpen.OpenedThisSession) Close();
                };
            }
            UnityMcpWelcomeServices.CatalogChanged -= OnCatalogChanged;
            UnityMcpWelcomeServices.CatalogChanged += OnCatalogChanged;
            EditorSceneManager.sceneOpened -= OnSceneOpened;
            EditorSceneManager.sceneOpened += OnSceneOpened;
            UnityEditor.PackageManager.Events.registeredPackages -= OnPackagesChanged;
            UnityEditor.PackageManager.Events.registeredPackages += OnPackagesChanged;
            // The pipeline fix of an art pack imports an overlay asynchronously: the band has to
            // flip when it lands, not at the next focus.
            AssetDatabase.importPackageCompleted -= OnPackageImported;
            AssetDatabase.importPackageCompleted += OnPackageImported;
        }

        private void OnDisable()
        {
            _pendingAssetsFilter = null;
            UnityMcpWelcomeServices.CatalogChanged -= OnCatalogChanged;
            EditorSceneManager.sceneOpened -= OnSceneOpened;
            UnityEditor.PackageManager.Events.registeredPackages -= OnPackagesChanged;
            AssetDatabase.importPackageCompleted -= OnPackageImported;
        }

        private void OnPackageImported(string packageName) => Rebuild();

        private void OnDestroy() => UnityMcpWelcomeServices.ReleaseImages();

        private void OnCatalogChanged()
        {
            if (_context == null) return;
            _catalog = UnityMcpWelcomeServices.LoadCatalog(_context);
            if (UnityMcpWelcomeServices.CatalogOnline && _pendingAssetsFilter != null)
            {
                _filter = _pendingAssetsFilter;
                _pendingAssetsFilter = null;
                _tab = "assets";
            }
            // A fresh catalogue can name Cards this package does not embed: fetch them now. Each
            // one that lands raises this event again; the queue skips what is already cached.
            UnityMcpWelcomeServices.QueueCards(_catalog);
            Rebuild();
        }

        private void OnSceneOpened(Scene scene, OpenSceneMode mode) => Rebuild();
        private void OnPackagesChanged(UnityEditor.PackageManager.PackageRegistrationEventArgs args) => Rebuild();
        private void OnFocus() => Rebuild();

        private void CreateGUI()
        {
            Bind();
            Rebuild();
        }

        private void Bind()
        {
            List<UnityMcpWelcomeContext> contexts = UnityMcpWelcomeServices.LoadContexts();
            _context = contexts.FirstOrDefault(c => c.Guid == _contextGuid) ?? contexts.FirstOrDefault();
            if (_context == null) return;
            _contextGuid = _context.Guid;
            _catalog = UnityMcpWelcomeServices.LoadCatalog(_context);
            titleContent = new GUIContent(_context.Config.name, UnityMcpWelcomeServices.LoadImage(_context.Media(_context.Config.icon)));
            UnityMcpWelcomeServices.RefreshCatalog(_catalog);
        }

        private void MarkOpened()
        {
            if (_context == null) return;
            UnityMcpWelcomeServices.MarkWelcomeSeen(_context);
            UnityMcpWelcomeServices.SetInt(_context, "Opens", UnityMcpWelcomeServices.GetInt(_context, "Opens") + 1);
            if (UnityMcpWelcomeServices.GetDate(_context, "FirstOpen") == null) UnityMcpWelcomeServices.SetDate(_context, "FirstOpen");
        }

        // -- Shell ----------------------------------------------------------

        private void Rebuild()
        {
            VisualElement root = rootVisualElement;
            if (root == null) return;
            if (_context == null) Bind();

            Vector2 scroll = _scroll != null ? _scroll.scrollOffset : Vector2.zero;
            root.Clear();

            // On the shell, never on the root: rootVisualElement.styleSheets also carries the
            // editor's default theme, font included, and clearing it drops every Label to a
            // height of 0 - the window draws its boxes and not one word, with nothing in the
            // console.
            var shell = new VisualElement();
            StyleSheet style = LoadStyle();
            if (style != null) shell.styleSheets.Add(style);
            shell.AddToClassList("abw");
            shell.EnableInClassList("abw--light", !EditorGUIUtility.isProSkin);
            root.Add(shell);

            if (_context == null)
            {
                shell.Add(Text("No welcome config found. Reimport the package.", "abw-band__title"));
                return;
            }
            if (_tab == "assets" && !UnityMcpWelcomeServices.CatalogOnline)
            {
                // Keep an explicit destination while its opening request revalidates the catalogue.
                // The offline UI still shows local content, and later navigation cancels the intent.
                _pendingAssetsFilter = _filter;
                _tab = "start";
            }
            if (_tab == "studio" && !HasStudioTab) _tab = "start";

            shell.Add(BuildHeader());

            var body = new VisualElement();
            body.AddToClassList("abw-body");
            shell.Add(body);

            if (_tab == "assets" || _tab == "studio")
            {
                _scroll = Scroll("abw-full", "abw-scroll--full");
                if (_tab == "studio") BuildStudioTab(_scroll.contentContainer);
                else BuildAssetsTab(_scroll.contentContainer);
                body.Add(_scroll);
            }
            else
            {
                _scroll = Scroll("abw-left", "abw-scroll--left");
                if (_tab == "studio") BuildStudioTab(_scroll.contentContainer);
                else if (_context.Config.profile == "art") BuildArtStart(_scroll.contentContainer);
                else BuildToolStart(_scroll.contentContainer);
                body.Add(_scroll);
                if (UnityMcpWelcomeServices.CatalogOnline) body.Add(BuildPanel());
            }

            shell.Add(BuildFooter());
            ScrollView restored = _scroll;
            restored.schedule.Execute(() => restored.scrollOffset = scroll);
        }

        /// <summary>
        /// Padding goes on the content container by class, not through a child selector: the
        /// ScrollView's internal hierarchy changed between 2022.3 and Unity 6, and a selector
        /// written against one silently misses the other. No horizontal scroller: the columns
        /// wrap, and a scroller that appears for one overhanging pixel eats 13 px of height.
        /// </summary>
        private static ScrollView Scroll(string viewClass, string contentClass)
        {
            var scroll = new ScrollView(ScrollViewMode.Vertical) { horizontalScrollerVisibility = ScrollerVisibility.Hidden };
            scroll.AddToClassList(viewClass);
            scroll.contentContainer.AddToClassList(contentClass);
            return scroll;
        }

        private StyleSheet LoadStyle()
        {
            string script = AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(this));
            string beside = Path.GetDirectoryName(script)?.Replace('\\', '/') + "/" + STYLE_NAME + ".uss";
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(beside);
            if (sheet != null) return sheet;
            foreach (string guid in AssetDatabase.FindAssets(STYLE_NAME + " t:StyleSheet"))
            {
                sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(AssetDatabase.GUIDToAssetPath(guid));
                if (sheet != null) return sheet;
            }
            return null;
        }

        /// <summary>Art packs included: the studio and its devlog are the
        /// same argument whatever the buyer bought.</summary>
        private bool HasStudioTab => _context.Config.studioTab;

        private VisualElement BuildHeader()
        {
            UnityMcpWelcomeData config = _context.Config;
            var header = new VisualElement();
            header.AddToClassList("abw-header");

            Texture2D icon = UnityMcpWelcomeServices.LoadImage(_context.Media(config.icon));
            if (icon != null)
            {
                var image = new Image { image = icon, scaleMode = ScaleMode.ScaleToFit };
                image.AddToClassList("abw-header__icon");
                header.Add(image);
            }

            var text = new VisualElement();
            text.AddToClassList("abw-header__text");
            text.Add(Text(config.name, "abw-header__title"));
            text.Add(Text(config.tagline, "abw-header__tagline"));
            header.Add(text);

            var side = new VisualElement();
            side.AddToClassList("abw-header__side");
            side.Add(VersionBadge());

            // Anchored to the header's bottom edge: the active tab takes the colour of what it
            // opens onto, Home's showcase panel included, and reads as attached to the content.
            var tabs = new VisualElement();
            tabs.AddToClassList("abw-tabs");
            tabs.EnableInClassList("abw-tabs--panel", _tab == "start" && UnityMcpWelcomeServices.CatalogOnline);
            tabs.Add(Tab("start", "Get started"));
            if (UnityMcpWelcomeServices.CatalogOnline) tabs.Add(Tab("assets", "Assets"));
            if (HasStudioTab) tabs.Add(Tab("studio", "Studio"));
            side.Add(tabs);
            header.Add(side);
            return header;
        }

        /// <summary>Claims LATEST only when the fetched catalogue says so. Offline, the badge
        /// states the version and nothing it cannot know.</summary>
        private VisualElement VersionBadge()
        {
            string version = UnityMcpWelcomeServices.DisplayVersion(_context);
            string latest = UnityMcpWelcomeServices.StoreVersion(_context, Self);

            var badge = new VisualElement();
            badge.AddToClassList("abw-badge");
            string label = "v" + version;
            if (UnityMcpWelcomeServices.IsNewer(latest, version))
            {
                badge.AddToClassList("abw-badge--warn");
                label = version + " \u2014 " + latest + " IS OUT";
            }
            else if (!string.IsNullOrEmpty(latest))
            {
                badge.AddToClassList("abw-badge--ok");
                label = "\u2713  " + version + " \u2014 LATEST";
            }
            badge.Add(new Label(label));
            return badge;
        }

        /// <summary>Focusable: Enter or Space opens it, Left and Right walk the row. The rebuild
        /// replaces the header, so a keyboard selection puts the focus back on the new tab.</summary>
        private VisualElement Tab(string id, string label)
        {
            VisualElement tab = Clickable(() => SelectTab(id, false), "abw-tab");
            tab.name = "tab-" + id;
            tab.focusable = true;
            tab.EnableInClassList("abw-tab--on", _tab == id);
            tab.Add(new Label(label));
            tab.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (IsSubmit(evt.keyCode)) SelectTab(id, true);
                else if (evt.keyCode == KeyCode.LeftArrow || evt.keyCode == KeyCode.RightArrow)
                {
                    int index = tab.parent.IndexOf(tab) + (evt.keyCode == KeyCode.RightArrow ? 1 : -1);
                    if (index >= 0 && index < tab.parent.childCount) tab.parent[index].Focus();
                }
                else return;
                evt.StopPropagation();
            });
            return tab;
        }

        private void SelectTab(string id, bool keepFocus)
        {
            if (id == "assets") _filter = "all";
            OpenTab(id);
            if (!keepFocus) return;
            VisualElement root = rootVisualElement;
            root.schedule.Execute(() => root.Q<VisualElement>("tab-" + _tab)?.Focus());
        }

        private static bool IsSubmit(KeyCode key) =>
            key == KeyCode.Return || key == KeyCode.KeypadEnter || key == KeyCode.Space;

        private void OpenTab(string id)
        {
            _pendingAssetsFilter = null;
            if (id != "assets" && id != "studio") id = "start";
            _tab = id;
            _scroll = null;
            Rebuild();
        }

        private VisualElement BuildFooter()
        {
            var footer = new VisualElement();
            footer.AddToClassList("abw-footer");

            VisualElement discord = Clickable(() => Application.OpenURL(_catalog.discordUrl), "abw-btn", "abw-discord");
            discord.Add(new Label("Join us on Discord"));
            footer.Add(discord);

            if (_tab == "assets")
            {
                Label link = Text("Open our publisher page in the browser \u203a", "abw-link");
                link.AddManipulator(new Clickable(() => Application.OpenURL(_catalog.publisherUrl)));
                footer.Add(link);
            }
            else
            {
                footer.Add(Text("Support, early builds, and where feature requests actually land.", "abw-footer__note"));
            }

            footer.Add(Fill());

            string reviewUrl = Self?.url;
            VisualElement review = Clickable(() => Application.OpenURL(reviewUrl + "#reviews"), "abw-btn", "abw-btn--quiet");
            review.Add(new Label("Leave a review"));
            review.SetEnabled(!string.IsNullOrEmpty(reviewUrl));
            footer.Add(review);

            VisualElement close = Clickable(Close, "abw-btn", "abw-btn--close");
            close.Add(new Label("Close"));
            footer.Add(close);
            return footer;
        }

        // -- Small builders shared by the partials --------------------------

        private UnityMcpProduct Self => _catalog?.products.FirstOrDefault(p => p.id == _context.Config.id);

        private void Run(UnityMcpAction action) =>
            UnityMcpWelcomeServices.Run(_context, action, OpenTab, _catalog);

        private static Label Text(string text, params string[] classes)
        {
            var label = new Label(text ?? "");
            foreach (string c in classes) label.AddToClassList(c);
            return label;
        }

        private static VisualElement Clickable(Action onClick, params string[] classes)
        {
            var element = new VisualElement();
            foreach (string c in classes) element.AddToClassList(c);
            if (onClick != null) element.AddManipulator(new Clickable(onClick));
            return element;
        }

        private VisualElement Button(UnityMcpAction action, params string[] classes)
        {
            VisualElement button = Clickable(() => Run(action), classes.Length > 0 ? classes : new[] { "abw-btn" });
            button.Add(new Label(action.label));
            button.SetEnabled(UnityMcpWelcomeServices.CanRun(_context, action));
            return button;
        }

        private static VisualElement Fill()
        {
            var fill = new VisualElement();
            fill.AddToClassList("abw-fill");
            return fill;
        }

        private static VisualElement Eyebrow(string title, string aside = null, bool accent = false)
        {
            var row = new VisualElement();
            row.AddToClassList("abw-eyebrow");
            if (accent) row.AddToClassList("abw-eyebrow--accent");
            row.Add(Text(title, "abw-eyebrow__text"));
            var rule = new VisualElement();
            rule.AddToClassList("abw-eyebrow__rule");
            row.Add(rule);
            if (!string.IsNullOrEmpty(aside)) row.Add(Text(aside, "abw-eyebrow__aside"));
            return row;
        }

        private static VisualElement Section(VisualElement host)
        {
            var section = new VisualElement();
            section.AddToClassList("abw-section");
            host.Add(section);
            return section;
        }

        /// <summary>A one-line state: ok, warn or error, with its fix as the button.</summary>
        private static VisualElement Band(VisualElement host, string tone, string title, string detail)
        {
            var band = new VisualElement();
            band.AddToClassList("abw-band");
            band.AddToClassList("abw-band--" + tone);
            band.Add(Text(tone == "ok" ? "\u2713" : "!", "abw-band__mark"));
            var text = new VisualElement();
            text.AddToClassList("abw-band__text");
            text.Add(Text(title, "abw-band__title"));
            if (!string.IsNullOrEmpty(detail)) text.Add(Text(detail, "abw-band__detail"));
            band.Add(text);
            host.Add(band);
            return band;
        }
    }
}
