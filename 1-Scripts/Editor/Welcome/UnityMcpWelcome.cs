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
        private bool _rebuildPending;
        private bool _reusePanelsOnFlush;
        private bool _skin;
        private static bool s_opening;

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
        public static void Open(string contextGuid) => OpenAt(contextGuid, "start", null);

        /// <summary>The discovery prompt's "Show me": the catalogue, already filtered.</summary>
        public static void OpenAssets(string contextGuid, string filter) => OpenAt(contextGuid, "assets", filter);

        /// <summary>After an automatic opening: the package's CLICKME hub, when one is stamped,
        /// is selected so the Inspector shows it beside the window.</summary>
        internal static void OnAutoOpened(UnityMcpWelcomeContext context) => SelectClickMe(context);

        /// <summary>Implemented by the CLICKME partial (_ClickMe template); a no-op without it.</summary>
        static partial void SelectClickMe(UnityMcpWelcomeContext context);

        private static void OpenAt(string contextGuid, string tab, string filter)
        {
            UnityMcpWelcomeFirstOpen.OpenedThisSession = true;
            bool existed = HasOpenInstances<UnityMcpWelcome>();
            UnityMcpWelcome window;
            s_opening = true;
            try { window = GetWindow<UnityMcpWelcome>(false, "Welcome", true); }
            finally { s_opening = false; }
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
            if (tab == "assets")
            {
                // Offline or still fetching: Rebuild keeps the destination pending until it lands.
                window._filter = filter ?? "all";
                window._tab = "assets";
            }
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
            UnityMcpWelcomeServices.AcquireConsumer();
            _skin = EditorGUIUtility.isProSkin;
            EditorApplication.projectChanged += ScheduleRebuild;
            UnityMcpWelcomeServices.MediaChanged += UpdateTitleIcon;
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
            EditorApplication.projectChanged -= ScheduleRebuild;
            UnityMcpWelcomeServices.MediaChanged -= UpdateTitleIcon;
            EditorApplication.update -= FlushRebuild;
            _rebuildPending = false;
            CancelPageBuild();
            rootVisualElement.Clear();
            ForgetPanels();
            _pageOffsets.Clear();
            _filterOffsets.Clear();
            _uiGeneration++;
            UnityMcpWelcomeServices.ReleaseConsumer();
            _pendingAssetsFilter = null;
            UnityMcpWelcomeServices.CatalogChanged -= OnCatalogChanged;
            EditorSceneManager.sceneOpened -= OnSceneOpened;
            UnityEditor.PackageManager.Events.registeredPackages -= OnPackagesChanged;
            AssetDatabase.importPackageCompleted -= OnPackageImported;
        }

        private void UpdateTitleIcon(string path)
        {
            if (_context != null && path == _context.Media(_context.Config.icon))
                titleContent.image = UnityMcpWelcomeServices.LoadImage(path);
        }

        private void OnPackageImported(string packageName) => ScheduleRebuild();

        private void OnDestroy() => UnityMcpWelcomeServices.ReleaseImages();

        private void OnCatalogChanged()
        {
            if (_context == null) return;
            var catalog = UnityMcpWelcomeServices.LoadCatalog(_context);
            if (ReferenceEquals(_catalog, catalog))
            {
                InvalidateStudio();
                return;
            }
            _catalog = catalog;
            if (UnityMcpWelcomeServices.CatalogOnline && _pendingAssetsFilter != null)
            {
                _filter = _pendingAssetsFilter;
                _pendingAssetsFilter = null;
                _tab = "assets";
            }
            ScheduleRebuild();
        }

        private void OnSceneOpened(Scene scene, OpenSceneMode mode) => ScheduleRebuild();
        private void OnPackagesChanged(UnityEditor.PackageManager.PackageRegistrationEventArgs args) => ScheduleRebuild();
        private void OnFocus()
        {
            if (_skin == EditorGUIUtility.isProSkin) return;
            _skin = EditorGUIUtility.isProSkin;
            ScheduleRebuild();
        }

        private void ScheduleRebuild()
        {
            _reusePanelsOnFlush = false;
            if (_rebuildPending) return;
            _rebuildPending = true;
            EditorApplication.update += FlushRebuild;
        }

        private void FlushRebuild()
        {
            EditorApplication.update -= FlushRebuild;
            _rebuildPending = false;
            if (this == null) return;
            if (_context != null)
            {
                _context = UnityMcpWelcomeServices.LoadContexts().FirstOrDefault(c => c.Guid == _contextGuid);
                if (_context != null) _catalog = UnityMcpWelcomeServices.LoadCatalog(_context);
            }
            Render(_reusePanelsOnFlush);
        }

        private void CreateGUI()
        {
            if (s_opening) return;
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

        private void Rebuild() => Render(false);

        private void Render(bool reusePanels)
        {
            using var perf = new UnityMcpWelcomePerf.Scope("Rebuild");
            EditorApplication.update -= FlushRebuild;
            _rebuildPending = false;
            VisualElement root = rootVisualElement;
            if (root == null) return;
            if (_context == null) Bind();

            Vector2 scroll = _scroll != null ? _scroll.scrollOffset : Vector2.zero;
            Vector2 panelScroll = _panelScroll != null ? _panelScroll.scrollOffset : Vector2.zero;
            Action restoreFocus = PreserveFocus(root);
            int generation = ++_uiGeneration;
            CancelPageBuild();
            if (!reusePanels) ForgetPanels();
            if ((_tab == "assets" || _tab == "studio") && UnityMcpWelcomeServices.CatalogOnline && !_pages.ContainsKey(_tab) &&
                root.childCount == 1 && root[0].Q<VisualElement>(className: "abw-body") != null)
            {
                BeginPageBuild(reusePanels, restoreFocus);
                return;
            }
            if (reusePanels && _context != null && root.childCount == 1 &&
                (_tab != "assets" || UnityMcpWelcomeServices.CatalogOnline) && (_tab != "studio" || HasStudioTab))
            {
                RenderNavigation(root);
                if (restoreFocus != null) root.schedule.Execute(() => { if (generation == _uiGeneration) restoreFocus(); });
                return;
            }
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

            shell.Add(PageBody());

            shell.Add(BuildFooter());
            if (restoreFocus != null) root.schedule.Execute(() => { if (generation == _uiGeneration) restoreFocus(); });
            if (!reusePanels)
            {
                RestoreOffset(_scroll, scroll);
                if (_panelScroll != null) RestoreOffset(_panelScroll, panelScroll);
            }
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
            foreach (string guid in UnityMcpWelcomeProjectCache.FindAssets(STYLE_NAME + " t:StyleSheet"))
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
            using var perf = new UnityMcpWelcomePerf.Scope("UI.Header");
            UnityMcpWelcomeData config = _context.Config;
            var header = new VisualElement();
            header.AddToClassList("abw-header");

            header.Add(LiveImage(() => UnityMcpWelcomeServices.LoadImage(_context.Media(config.icon)), "abw-header__icon", path: _context.Media(config.icon)));

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
            Render(true);
        }

        private VisualElement BuildFooter()
        {
            using var perf = new UnityMcpWelcomePerf.Scope("UI.Footer");
            var footer = new VisualElement();
            footer.AddToClassList("abw-footer");

            VisualElement discord = Clickable(() => Application.OpenURL(_catalog.discordUrl), "abw-btn", "abw-discord");
            discord.Add(new Label("Join us on Discord"));
            footer.Add(discord);

            Label link = Text("Open our publisher page in the browser \u203a", "abw-link");
            link.name = "footer-publisher";
            link.style.display = _tab == "assets" ? DisplayStyle.Flex : DisplayStyle.None;
            link.AddManipulator(new Clickable(() => Application.OpenURL(_catalog.publisherUrl)));
            footer.Add(link);
            Label note = Text("Support, early builds, and where feature requests actually land.", "abw-footer__note");
            note.name = "footer-note";
            note.style.display = _tab == "assets" ? DisplayStyle.None : DisplayStyle.Flex;
            footer.Add(note);

            footer.Add(Fill());

            // Stands out until the buyer has rated: then it steps back to a quiet link-button.
            string reviewUrl = Self?.url;
            bool rated = UnityMcpWelcomePrompts.ReviewDone(_context);
            VisualElement review = Clickable(() =>
            {
                UnityMcpWelcomePrompts.MarkReviewDone(_context);
                Application.OpenURL(reviewUrl + "#reviews");
                Rebuild();
            }, "abw-btn", rated ? "abw-btn--quiet" : "abw-review-cta");
            review.Add(new Label(rated ? "Leave a review" : "\u2605  Leave a review"));
            review.name = "footer-review";
            review.SetEnabled(!string.IsNullOrEmpty(reviewUrl));
            footer.Add(review);

            VisualElement close = Clickable(Close, "abw-btn", "abw-btn--close");
            close.Add(new Label("Close"));
            footer.Add(close);
            return footer;
        }

        private void UpdateNavigation()
        {
            var shell = rootVisualElement[0];
            var tabs = shell[0].Q<VisualElement>(className: "abw-tabs");
            tabs.EnableInClassList("abw-tabs--panel", _tab == "start" && UnityMcpWelcomeServices.CatalogOnline);
            foreach (var tab in tabs.Children()) tab.EnableInClassList("abw-tab--on", tab.name == "tab-" + _tab);
            var footer = shell[shell.childCount - 1];
            footer.Q<Label>("footer-publisher").style.display = _tab == "assets" ? DisplayStyle.Flex : DisplayStyle.None;
            footer.Q<Label>("footer-note").style.display = _tab == "assets" ? DisplayStyle.None : DisplayStyle.Flex;
            var review = footer.Q<VisualElement>("footer-review");
            bool rated = UnityMcpWelcomePrompts.ReviewDone(_context);
            review.EnableInClassList("abw-btn--quiet", rated);
            review.EnableInClassList("abw-review-cta", !rated);
            review.Q<Label>().text = rated ? "Leave a review" : "\u2605  Leave a review";
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
