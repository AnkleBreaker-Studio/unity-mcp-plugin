using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

// Every field of the two config classes below is written by JsonUtility, never by code (CS0649).
#pragma warning disable 0649

namespace UnityMCP.Editor.Welcome
{
    // Stamped from clickme-inspector/Template by `clickme.py stamp`. Edit the template, not this
    // copy: a re-stamp overwrites it.
    //
    // A partial of the Welcome on purpose: the hub draws with the window's own bands, buttons and
    // Install logic, and opens the window on a given tab, without the Welcome exposing any of it.
    // It needs the matching Welcome media/lifetime contract; `clickme.py stamp` checks every member it uses.

    /// <summary>The hub's own settings, read from the <c>*.clickme.json</c> beside the Welcome
    /// config. Everything else (name, icon, requirements, catalogue) is the Welcome's.</summary>
    [Serializable]
    internal sealed class UnityMcpClickMeData
    {
        public string id;

        /// <summary>Up to four tiles. Empty: the first four shortcuts of the Welcome.</summary>
        public UnityMcpClickMeAction[] actions = new UnityMcpClickMeAction[0];

        /// <summary><c>Namespace.Type.Method</c>: a static, parameterless method returning the
        /// live line of the Ready band (plugin version, bridge address). Looked up by name, as the
        /// package's code lives in assemblies this one must not reference.</summary>
        public string status;

        /// <summary>Art packs: one sentence under IN THE PACK saying what the pack holds. The
        /// hub draws no prefab previews: loading them would cost on every display.</summary>
        public string summary;

        /// <summary>The Resources row under the tiles: manuals, READMEs, extra scenes. Same kinds as
        /// an action, plus <c>file</c> (a path under the package root, opened with its default
        /// application). A link whose target is missing is not drawn.</summary>
        public UnityMcpClickMeAction[] links = new UnityMcpClickMeAction[0];
    }

    [Serializable]
    internal sealed class UnityMcpClickMeAction
    {
        public string label;
        public string detail;

        /// <summary>Same kinds as a Welcome action: scene, menu, url, discord, ping, tab, product.</summary>
        public string kind;
        public string target;

        /// <summary>Built-in editor icon name. Empty: one picked from the kind.</summary>
        public string icon;

        /// <summary>Replaces the detail while the action cannot run, to say why.</summary>
        public string unavailable;

        public UnityMcpAction ToAction() => new UnityMcpAction { label = label, detail = detail, kind = kind, target = target };
    }

    internal sealed partial class UnityMcpWelcome
    {
        /// <summary>Automatic opening of the Welcome: this package's CLICKME asset goes into the
        /// Selection, so the hub sits in the Inspector next to the window.</summary>
        static partial void SelectClickMe(UnityMcpWelcomeContext context)
        {
            string guid = UnityMcpWelcomeProjectCache.FindAssets("t:" + typeof(UnityMcpClickMe).Name).FirstOrDefault();
            if (guid == null) return;
            var hub = AssetDatabase.LoadAssetAtPath<UnityMcpClickMe>(AssetDatabase.GUIDToAssetPath(guid));
            if (hub == null) return;
            Selection.activeObject = hub;
            EditorGUIUtility.PingObject(hub);
        }

        /// <summary>Opens the window on one tab: <c>start</c>, <c>assets</c> or <c>studio</c>. A tab
        /// the window cannot show (catalogue offline, no studio tab) falls back to start.</summary>
        internal static void OpenOn(string contextGuid, string tab, string filter = null)
        {
            Open(contextGuid);
            if (tab != "assets" && tab != "studio") return;
            // Not GetWindow: its title argument would overwrite the icon title Bind just set.
            UnityMcpWelcome window = Resources.FindObjectsOfTypeAll<UnityMcpWelcome>().FirstOrDefault();
            if (window == null) return;
            if (tab == "assets") window._filter = string.IsNullOrEmpty(filter) ? "all" : filter;
            window.OpenTab(tab);
        }

        /// <summary>
        /// The status band of Get started, sized for an inspector: the same checks and words as
        /// BuildToolStatus, buttons under the text. Returns whether the band is the Ready one: any
        /// other band asks the buyer to act, and the rest of the hub steps back.
        /// </summary>
        private static bool ClickMeReady(VisualElement host, UnityMcpWelcomeContext context, string live)
        {
            UnityMcpWelcomeData config = context.Config;
            List<UnityMcpRequirement> missing = UnityMcpWelcomeServices.MissingRequirements(context);
            List<UnityMcpRequirement> compileMissing = missing.Where(r => r.compile).ToList();

            if (compileMissing.Count > 0)
            {
                VisualElement band = Band(host, "error", "Install " + JoinNames(compileMissing.Select(r => r.name)) + " first",
                    config.name + " needs " + (compileMissing.Count == 1 ? "it" : "them") + " to compile. " + WhereFrom(compileMissing));
                AddInstallButtons(band, compileMissing);
                StackButtons(band);
                return false;
            }

            if (!string.IsNullOrEmpty(config.ownAssembly) && !UnityMcpWelcomeServices.IsAssemblyLoaded(config.ownAssembly))
            {
                VisualElement band = Band(host, "error", config.name + " has not compiled",
                    "Its dependencies are here, but its code did not build. The first error in the Console names the file.");
                VisualElement console = Clickable(() => EditorApplication.ExecuteMenuItem("Window/General/Console"), "abw-btn");
                console.Add(new Label("Open the Console"));
                band.Add(console);
                StackButtons(band);
                return false;
            }

            List<UnityMcpRequirement> featureMissing = missing.Where(r => !r.compile).ToList();
            if (featureMissing.Count > 0)
            {
                string why = string.Join(" ", featureMissing.Select(r => r.why).Where(w => !string.IsNullOrEmpty(w)));
                VisualElement band = Band(host, "warn", JoinNames(featureMissing.Select(r => r.name)) +
                    (featureMissing.Count == 1 ? " is" : " are") + " not installed",
                    (string.IsNullOrEmpty(why) ? "The features of " + config.name + " that use " + (featureMissing.Count == 1 ? "it" : "them") +
                        " stay off until " + (featureMissing.Count == 1 ? "it is" : "they are") + " installed." : why) +
                    " " + WhereFrom(featureMissing));
                AddInstallButtons(band, featureMissing);
                StackButtons(band);
            }

            string pipeline = UnityMcpWelcomeServices.ActivePipeline();
            if (config.pipelines.Length > 0 && !config.pipelines.Contains(pipeline))
            {
                Band(host, "warn", "This project renders with " + UnityMcpWelcomeServices.PipelineLabel(pipeline),
                    config.name + " supports " + JoinNames(config.pipelines.Select(UnityMcpWelcomeServices.PipelineLabel)) +
                    ". Assign a supported pipeline asset in Project Settings > Graphics.");
                return false;
            }
            if (featureMissing.Count > 0) return false;

            string detail = live;
            if (string.IsNullOrEmpty(detail))
            {
                var parts = new List<string>();
                List<string> resolved = config.requires.Where(r => !missing.Contains(r)).Select(r => r.name).ToList();
                if (resolved.Count > 0) parts.Add(JoinNames(resolved) + " resolved");
                parts.Add("Unity " + Application.unityVersion);
                parts.Add(UnityMcpWelcomeServices.PipelineLabel(pipeline));
                detail = string.Join(" \u00b7 ", parts);
            }
            if (EditorUtility.scriptCompilationFailed) detail += " \u00b7 compile errors in the project";
            Band(host, "ok", "Ready to go", detail);
            return true;
        }

        /// <summary>A button beside the text squeezes it to a few words at inspector width: every
        /// button of the band goes on a row under the text.</summary>
        private static void StackButtons(VisualElement band)
        {
            List<VisualElement> buttons = band.Children().Where(c => c.ClassListContains("abw-btn")).ToList();
            if (buttons.Count == 0) return;
            var row = new VisualElement();
            row.AddToClassList("abw-band__actions");
            foreach (VisualElement button in buttons) row.Add(button);
            (band.Q(className: "abw-band__text") ?? band).Add(row);
        }

        /// <summary>
        /// The hub a buyer finds at the package root: where the package stands, the way into the
        /// Welcome and its tabs, four shortcuts, and, once the package has been used, the review.
        /// </summary>
        [CustomEditor(typeof(UnityMcpClickMe))]
        // UnityEditor.Editor in full: most stamped namespaces have an .Editor segment, which a
        // bare Editor would resolve to.
        internal sealed class ClickMeInspector : UnityEditor.Editor
        {
            private const string CLICKME_CONFIG = "MCPForUnity.clickme";
            private const string CLICKME_STYLE = "UnityMcpClickMe";
            private const string HOST_FILE = "host:";

            private const int NEW_POST_DAYS = 21;
            private const int STARS = 5;
            private const int SHELF_CARDS = 3;
            private const long REBUILD_DELAY_MS = 250;

            private VisualElement _root;
            private UnityMcpWelcomeContext _context;
            private UnityMcpClickMeData _data = new UnityMcpClickMeData();
            private UnityMcpCatalog _catalog;
            private readonly List<VisualElement> _stars = new List<VisualElement>();
            private bool _rebuildPending;
            private bool _attached;

            private IVisualElementScheduledItem _scheduledRebuild;

            // The hero is the header: the default one only repeats "CLICKME" and offers an Open
            // button that does nothing for this asset.
            protected override void OnHeaderGUI() { }

            public override bool UseDefaultMargins() => false;

            private void Attach()
            {
                if (_attached) return;
                _attached = true;
                UnityMcpWelcomeServices.AcquireConsumer();
                UnityMcpWelcomeServices.CatalogChanged += OnCatalogChanged;
                EditorApplication.projectChanged += ScheduleRebuild;
                if (_root != null && _root.childCount > 0) { Bind(); Rebuild(); }
            }

            private void OnDisable()
            {
                if (!_attached) return;
                _attached = false;
                UnityMcpWelcomeServices.CatalogChanged -= OnCatalogChanged;
                EditorApplication.projectChanged -= ScheduleRebuild;
                UnityMcpWelcomeServices.ReleaseConsumer();
                _scheduledRebuild?.Pause();
                _scheduledRebuild = null;
                _rebuildPending = false;
            }

            public override VisualElement CreateInspectorGUI()
            {
                _root = new VisualElement();
                _root.RegisterCallback<AttachToPanelEvent>(evt => { if (evt.target == _root) Attach(); });
                _root.RegisterCallback<DetachFromPanelEvent>(evt => { if (evt.target == _root) OnDisable(); });
                Attach();
                Bind();
                Rebuild();
                return _root;
            }

            private void Bind()
            {
                _context = UnityMcpWelcomeServices.LoadContexts().FirstOrDefault();
                if (_context == null) return;
                _data = ReadData() ?? new UnityMcpClickMeData();
                _catalog = UnityMcpWelcomeServices.LoadCatalog(_context);
                // One fetch serves every AnkleBreaker window: only ask when none has succeeded.
                if (!UnityMcpWelcomeServices.CatalogOnline) UnityMcpWelcomeServices.RefreshCatalog(_catalog);
                CountVisit();
            }

            private UnityMcpClickMeData ReadData()
            {
                var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(_context.Dir + "/" + CLICKME_CONFIG + ".json");
                if (asset == null) return null;
                try { return JsonUtility.FromJson<UnityMcpClickMeData>(asset.text); }
                catch (ArgumentException) { return null; }
            }

            /// <summary>One visit per editor session, however often the selection comes back.</summary>
            private void CountVisit()
            {
                string key = UnityMcpWelcomeServices.PREFS + ".ClickMeSession." + _context.Config.id;
                if (SessionState.GetBool(key, false)) return;
                SessionState.SetBool(key, true);
                UnityMcpWelcomeServices.SetInt(_context, "ClickMeVisits", UnityMcpWelcomeServices.GetInt(_context, "ClickMeVisits") + 1);
                if (UnityMcpWelcomeServices.GetDate(_context, "FirstOpen") == null) UnityMcpWelcomeServices.SetDate(_context, "FirstOpen");
            }

            private void OnCatalogChanged()
            {
                if (_context == null || !ReferenceEquals(_catalog, UnityMcpWelcomeServices.LoadCatalog(_context))) { ScheduleRebuild(); return; }
                if (_root == null || !_attached || _rebuildPending) return;
                using var perf = new UnityMcpWelcomePerf.Scope("ClickMe.UpdateStudio");
                var previous = _root.Q<VisualElement>("clickme-studio-tile");
                if (previous?.parent == null) return;
                Action restoreFocus = PreserveFocus(_root);
                var parent = previous.parent;
                int index = parent.IndexOf(previous);
                previous.RemoveFromHierarchy();
                parent.Insert(index, IsArt ? StudioStrip() : Studio());
                if (restoreFocus != null) _root.schedule.Execute(restoreFocus);
            }

            /// <summary>Coalesces catalogue and project changes while the hub is attached.</summary>
            private void ScheduleRebuild()
            {
                if (_root == null || !_attached || _rebuildPending) return;
                _rebuildPending = true;
                _scheduledRebuild = _root.schedule.Execute(() =>
                {
                    _scheduledRebuild = null;
                    _rebuildPending = false;
                    if (_attached) Rebuild();
                }).StartingIn(REBUILD_DELAY_MS);
            }

            private void Rebuild()
            {
                using var perf = new UnityMcpWelcomePerf.Scope("ClickMe.Rebuild");
                if (_root == null) return;
                if (_context == null) Bind();
                else
                {
                    _context = UnityMcpWelcomeServices.LoadContexts().FirstOrDefault();
                    if (_context != null) { _catalog = UnityMcpWelcomeServices.LoadCatalog(_context); _data = ReadData() ?? new UnityMcpClickMeData(); }
                }
                Action restoreFocus = PreserveFocus(_root);
                _root.Clear();
                _stars.Clear();

                var shell = new VisualElement();
                foreach (StyleSheet sheet in Styles()) shell.styleSheets.Add(sheet);
                shell.AddToClassList("abw");
                shell.AddToClassList("abc");
                shell.EnableInClassList("abw--light", !EditorGUIUtility.isProSkin);
                _root.Add(shell);

                if (_context == null)
                {
                    shell.Add(Text("No welcome config found beside this hub's scripts. Reimport the package.", "abc-empty"));
                    return;
                }
                if (UnityMcpWelcomeServices.CatalogOnline)
                {
                    // Only what this hub draws: the Welcome downloads the rest when it opens.
                    UnityMcpWelcomeServices.QueueCards(new UnityMcpCatalog
                    {
                        products = ShownProducts().ToArray(),
                        games = (_catalog.games ?? new UnityMcpGame[0]).Take(1).ToArray(),
                    });
                    UnityMcpWelcomeServices.RefreshDevlog(_catalog.devlogFeed);
                }

                shell.Add(BuildHeader());
                var body = new VisualElement();
                body.AddToClassList("abc-body");
                shell.Add(body);
                bool ready;
                if (IsArt)
                {
                    ready = ArtPipelineReady(body);
                    BuildArtHero(body, ready);
                    BuildArtPack(body);
                    if (UnityMcpWelcomeServices.CatalogOnline) BuildArtDiscover(body);
                }
                else
                {
                    ready = ClickMeReady(body, _context, LiveStatus());
                    BuildGuide(body, ready);
                    BuildActions(body);
                    BuildLinks(body);
                    if (UnityMcpWelcomeServices.CatalogOnline) BuildDiscover(body);
                }
                // Never ask for a review while the band asks for something else.
                bool asked = ready && BuildReview(body);
                shell.Add(BuildFooter(asked));
                if (restoreFocus != null) _root.schedule.Execute(restoreFocus);
            }

            // -- Images -----------------------------------------------------

            /// <summary>Uses the shared image cache; attached image bindings pin visible textures.</summary>
            private Texture2D OwnImage(string path) => UnityMcpWelcomeServices.LoadImage(path);

            /// <summary>The Welcome's rule: the downloaded Card while the catalogue is online,
            /// else the one the package embeds.</summary>
            private Texture2D CardImage(UnityMcpProduct product) => UnityMcpWelcomeServices.Card(_context, product);

            /// <summary>A game cover, cached under the same key as the Welcome's GameImage.</summary>
            private Texture2D GameCover(UnityMcpGame game) => UnityMcpWelcomeServices.GameImage(_context, game, false);

            private IEnumerable<StyleSheet> Styles()
            {
                string script = target != null ? AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject((ScriptableObject)target)) : null;
                string dir = string.IsNullOrEmpty(script) ? null : Path.GetDirectoryName(script)?.Replace('\\', '/');
                foreach (string name in new[] { STYLE_NAME, CLICKME_STYLE })
                {
                    StyleSheet sheet = dir != null ? AssetDatabase.LoadAssetAtPath<StyleSheet>(dir + "/" + name + ".uss") : null;
                    if (sheet == null)
                    {
                        string guid = UnityMcpWelcomeProjectCache.FindAssets(name + " t:StyleSheet").FirstOrDefault();
                        if (guid != null) sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(AssetDatabase.GUIDToAssetPath(guid));
                    }
                    if (sheet != null) yield return sheet;
                }
            }

            // -- Header -----------------------------------------------------

            private VisualElement BuildHeader()
            {
                UnityMcpWelcomeData config = _context.Config;
                var header = new VisualElement();
                header.AddToClassList("abc-header");

                header.Add(LiveImage(() => OwnImage(_context.Media(config.icon)), "abc-header__icon", path: _context.Media(config.icon)));

                var text = new VisualElement();
                text.AddToClassList("abc-header__text");
                text.Add(Text(config.name, "abc-header__title"));
                if (!string.IsNullOrEmpty(config.tagline)) text.Add(Text(config.tagline, "abc-header__tagline"));
                text.Add(VersionBadge());
                header.Add(text);
                return header;
            }

            /// <summary>Claims LATEST only when the fetched catalogue says so; a newer version on
            /// the store makes the badge the link to it.</summary>
            private VisualElement VersionBadge()
            {
                string version = UnityMcpWelcomeServices.DisplayVersion(_context);
                UnityMcpProduct self = Self;
                string latest = UnityMcpWelcomeServices.StoreVersion(_context, self);
                bool outdated = UnityMcpWelcomeServices.IsNewer(latest, version);

                VisualElement badge = outdated ? Clickable(() => Application.OpenURL(self.url), "abw-badge") : new VisualElement();
                badge.AddToClassList("abw-badge");
                badge.AddToClassList("abc-badge");
                string label = "v" + version;
                if (outdated)
                {
                    badge.AddToClassList("abw-badge--warn");
                    label = version + " \u2014 " + latest + " IS OUT \u203a";
                    badge.tooltip = "Open its Asset Store page";
                }
                else if (!string.IsNullOrEmpty(latest))
                {
                    badge.AddToClassList("abw-badge--ok");
                    label = "\u2713  " + version + " \u2014 LATEST";
                }
                badge.Add(new Label(label));
                return badge;
            }

            // -- Guide and shortcuts ----------------------------------------

            private void BuildGuide(VisualElement host, bool ready)
            {
                string guid = _context.Guid;
                VisualElement guide = Focusable(Clickable(() => OpenOn(guid, "start"), "abw-hero", "abc-guide"), () => OpenOn(guid, "start"));
                // While the band carries the fix, the guide steps back: one loud button at a time.
                guide.EnableInClassList("abc-guide--quiet", !ready);
                guide.Add(Text("\u25b6", "abw-hero__glyph"));
                var text = new VisualElement();
                text.AddToClassList("abw-hero__text");
                text.Add(Text("Open the Welcome guide", "abw-hero__title"));
                text.Add(Text(!ready ? "It walks you through the setup too" : "Setup steps, docs and what is inside the box", "abw-hero__detail"));
                guide.Add(text);
                host.Add(guide);
            }

            private void BuildActions(VisualElement host)
            {
                List<UnityMcpClickMeAction> actions = _data.actions.Where(a => a != null && !string.IsNullOrEmpty(a.label)).Take(4).ToList();
                if (actions.Count == 0)
                {
                    actions = _context.Config.shortcuts.Where(UnityMcpWelcomeServices.IsSet).Take(4)
                        .Select(a => new UnityMcpClickMeAction { label = a.label, detail = a.detail, kind = a.kind, target = a.target })
                        .ToList();
                }
                if (actions.Count == 0) return;

                var section = new VisualElement();
                section.AddToClassList("abc-section");
                section.Add(Eyebrow("QUICK ACTIONS"));
                VisualElement row = null;
                for (int i = 0; i < actions.Count; i++)
                {
                    if (i % 2 == 0) row = Pair(section);
                    row.Add(Tile(actions[i]));
                }
                if (actions.Count % 2 == 1) row.Add(Blank());
                host.Add(section);
            }

            private VisualElement Tile(UnityMcpClickMeAction item)
            {
                UnityMcpAction action = item.ToAction();
                bool runnable = UnityMcpWelcomeServices.CanRun(_context, action);
                Action run = () => UnityMcpWelcomeServices.Run(_context, action, tab => OpenOn(_context.Guid, tab), _catalog);
                VisualElement tile = Focusable(Clickable(run, "abc-tile"), run);

                Texture2D icon = EditorIcon(item);
                if (icon != null)
                {
                    var image = new Image { image = icon, scaleMode = ScaleMode.ScaleToFit };
                    image.AddToClassList("abc-tile__icon");
                    tile.Add(image);
                }
                tile.Add(Text(item.label, "abc-tile__title"));
                string detail = !runnable && !string.IsNullOrEmpty(item.unavailable) ? item.unavailable : item.detail;
                if (!string.IsNullOrEmpty(detail)) tile.Add(Text(detail, "abc-tile__detail"));
                if (action.kind == "url") tile.tooltip = action.target;
                tile.SetEnabled(runnable);
                return tile;
            }

            /// <summary>Small chips for what did not earn a tile. Only the runnable ones: a manual
            /// the buyer deleted, or a scene not imported, leaves no dead link behind.</summary>
            private void BuildLinks(VisualElement host)
            {
                List<UnityMcpClickMeAction> links = Links()
                    .Where(l => l != null && !string.IsNullOrEmpty(l.label) && CanRunLink(l))
                    .ToList();
                if (links.Count == 0) return;
                var row = new VisualElement();
                row.AddToClassList("abc-links");
                row.Add(Text("RESOURCES", "abc-links__title"));
                foreach (UnityMcpClickMeAction link in links)
                {
                    UnityMcpClickMeAction item = link;
                    Action run = () => RunLink(item);
                    VisualElement chip = Focusable(Clickable(run, "abc-chip"), run);
                    chip.Add(new Label(item.label));
                    if (!string.IsNullOrEmpty(item.detail)) chip.tooltip = item.detail;
                    row.Add(chip);
                }
                host.Add(row);
            }

            private bool CanRunLink(UnityMcpClickMeAction link) =>
                link.kind == "file"
                    ? AssetDatabase.LoadMainAssetAtPath(FilePath(link.target)) != null
                    : UnityMcpWelcomeServices.CanRun(_context, link.ToAction());

            /// <summary>A file under the package root, or with <c>host:</c> a file beside the
            /// CLICKME asset: a hub hosted by another package names that package's files so.</summary>
            private string FilePath(string path)
            {
                if (string.IsNullOrEmpty(path) || !path.StartsWith(HOST_FILE, StringComparison.Ordinal))
                    return _context.Resolve(path);
                string asset = target != null ? AssetDatabase.GetAssetPath(target) : null;
                string dir = string.IsNullOrEmpty(asset) ? null : Path.GetDirectoryName(asset)?.Replace('\\', '/');
                return dir == null ? null : UnityMcpWelcomeServices.Combine(dir, path.Substring(HOST_FILE.Length));
            }

            private void RunLink(UnityMcpClickMeAction link)
            {
                if (link.kind != "file")
                {
                    UnityMcpWelcomeServices.Run(_context, link.ToAction(), tab => OpenOn(_context.Guid, tab), _catalog);
                    return;
                }
                UnityEngine.Object asset = AssetDatabase.LoadMainAssetAtPath(FilePath(link.target));
                if (asset != null) AssetDatabase.OpenAsset(asset);
            }

            private static Texture2D EditorIcon(UnityMcpClickMeAction item)
            {
                string name = item.icon;
                if (string.IsNullOrEmpty(name))
                {
                    switch (item.kind)
                    {
                        case "scene": name = "UnityEditor.SceneView"; break;
                        case "url":
                        case "discord": name = "BuildSettings.Web.Small"; break;
                        case "ping": name = "Folder Icon"; break;
                        case "product": name = "Prefab Icon"; break;
                        case "tab": name = "_Help"; break;
                        default: name = "UnityEditor.InspectorWindow"; break;
                    }
                }
                Texture2D icon = EditorGUIUtility.isProSkin ? EditorGUIUtility.FindTexture("d_" + name) : null;
                return icon != null ? icon : EditorGUIUtility.FindTexture(name);
            }

            // -- Discover ---------------------------------------------------

            private void BuildDiscover(VisualElement host)
            {
                var section = new VisualElement();
                section.AddToClassList("abc-section");
                section.Add(Eyebrow("DISCOVER"));
                VisualElement row = Pair(section);
                row.Add(MoreTools());
                row.Add(_context.Config.studioTab ? Studio() : Blank());
                VisualElement free = FreeLine();
                if (free != null) section.Add(free);
                host.Add(section);
            }

            /// <summary>The Welcome panel's own shelf: tools the buyer does not have yet, what is on
            /// sale first, then the newest.</summary>
            private List<UnityMcpProduct> Shelf(string category = "Tools") =>
                _catalog.products
                    .Where(p => p.id != _context.Config.id && !p.pinned && TopCategory(p) == category)
                    .Where(p => !UnityMcpWelcomeServices.IsInstalled(p))
                    .OrderBy(p => UnityMcpWelcomeServices.IsComingSoon(p))
                    .ThenByDescending(p => p.discount > 0)
                    .ThenByDescending(p => ReleaseDate(p))
                    .ThenBy(p => p.name)
                    .ToList();

            private VisualElement MoreTools()
            {
                int tools = _catalog.products.Count(p => TopCategory(p) == "Tools");
                int art = _catalog.products.Count(p => TopCategory(p) == "3D");
                string detail = tools + " tools" + (art > 0 ? ", " + art + " art packs" : "");
                return FanTile("More tools \u203a", detail, Shelf(), SaleCount(null), null);
            }

            private int SaleCount(string category)
            {
                string self = _context.Config.id;
                return _catalog.products.Count(p => p.id != self && p.discount > 0 && !UnityMcpWelcomeServices.IsComingSoon(p) &&
                    (category == null || TopCategory(p) == category) && !UnityMcpWelcomeServices.IsInstalled(p));
            }

            /// <summary>A Discover tile: three store Cards fanned out, a sale tag, a caption. Opens
            /// the Welcome's Assets tab, on <paramref name="filter"/> when one is given.</summary>
            private VisualElement FanTile(string title, string detail, List<UnityMcpProduct> shelf, int sale, string filter)
            {
                string guid = _context.Guid;
                Action open = () => OpenOn(guid, "assets", filter);
                VisualElement tile = Focusable(Clickable(open, "abc-disc"), open);
                var stage = new VisualElement();
                stage.AddToClassList("abc-disc__stage");

                List<UnityMcpProduct> cards = shelf.Take(SHELF_CARDS).ToList();
                // Back to front: the second to the right, the third to the left, the first on top.
                // The fan fills the stage: its width follows the tile's (see .abc-fan).
                string[] slots = { "abc-stack--front", "abc-stack--right", "abc-stack--left" };
                var fan = new VisualElement { pickingMode = PickingMode.Ignore };
                fan.AddToClassList("abc-fan");
                for (int i = cards.Count - 1; i >= 0; i--)
                {
                    UnityMcpProduct product = cards[i];
                    var card = LiveImage(() => CardImage(product), "abc-stack", ScaleMode.ScaleAndCrop, UnityMcpWelcomeServices.CachedCardPath(product), fallbackPath: _context.Media("Media/Cards/" + product.card));
                    card.AddToClassList(slots[i]);
                    fan.Add(card);
                }
                stage.Add(fan);
                if (sale > 0) stage.Add(Tag(sale.ToString(CultureInfo.InvariantCulture) + " ON SALE", "abc-tag--sale", "abc-tag--right"));
                tile.Add(stage);
                AddCaption(tile, title, detail);
                return tile;
            }

            private VisualElement Studio()
            {
                string guid = _context.Guid;
                VisualElement tile = Focusable(Clickable(() => OpenOn(guid, "studio"), "abc-disc"), () => OpenOn(guid, "studio"));
                tile.name = "clickme-studio-tile";
                var stage = new VisualElement();
                stage.AddToClassList("abc-disc__stage");

                UnityMcpDevlog post = UnityMcpWelcomeServices.LoadDevlog();
                UnityMcpGame[] games = _catalog.games ?? new UnityMcpGame[0];
                stage.Add(LiveImage(() => { Texture2D cover = post != null ? OwnImage(UnityMcpWelcomeServices.DevlogImage) : null; return cover != null ? cover : games.Length > 0 ? GameCover(games[0]) : null; }, "abc-disc__cover", ScaleMode.ScaleAndCrop));
                if (post != null) stage.Add(Tag(IsRecent(post.date) ? "NEW POST" : "DEVBLOG", "abc-tag--accent", "abc-tag--left"));
                tile.Add(stage);

                var parts = games.Select(g => g.name).Where(n => !string.IsNullOrEmpty(n)).Take(2).ToList();
                parts.Add("devblog");
                if (!string.IsNullOrEmpty(_catalog.consultingUrl)) parts.Add("consulting");
                AddCaption(tile, "Our studio \u203a", string.Join(", ", parts));
                return tile;
            }

            private static bool IsRecent(string date) =>
                DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out DateTime when) &&
                (DateTime.UtcNow - when).TotalDays <= NEW_POST_DAYS;

            /// <summary>The first free product the buyer does not have yet, if any.</summary>
            private UnityMcpProduct FreeProduct()
            {
                string self = _context.Config.id;
                return _catalog.products.FirstOrDefault(p =>
                    p.pinned && p.id != self && !UnityMcpWelcomeServices.IsInstalled(p) && !UnityMcpWelcomeServices.IsComingSoon(p));
            }

            private IEnumerable<UnityMcpProduct> ShownProducts()
            {
                if (IsArt)
                {
                    foreach (UnityMcpProduct product in Shelf("3D").Take(SHELF_CARDS)) yield return product;
                    foreach (UnityMcpProduct product in Shelf().Take(SHELF_CARDS)) yield return product;
                    UnityMcpProduct tied = TiedProduct();
                    if (tied != null) yield return tied;
                    yield break;
                }
                foreach (UnityMcpProduct product in Shelf().Take(SHELF_CARDS)) yield return product;
                UnityMcpProduct free = FreeProduct();
                if (free != null) yield return free;
            }

            private VisualElement FreeLine()
            {
                UnityMcpProduct free = FreeProduct();
                if (free == null) return null;

                Action open = () => UnityMcpWelcomeServices.OpenProduct(free, _catalog);
                VisualElement line = Focusable(Clickable(open, "abc-free"), open);
                line.tooltip = free.url;
                line.Add(LiveImage(() => CardImage(free), "abc-free__card", ScaleMode.ScaleAndCrop, UnityMcpWelcomeServices.CachedCardPath(free), fallbackPath: _context.Media("Media/Cards/" + free.card)));
                var text = new VisualElement();
                text.AddToClassList("abc-free__text");
                var title = new VisualElement();
                title.AddToClassList("abc-free__title");
                title.Add(Text(free.name, "abc-free__name"));
                title.Add(Pill("FREE", null));
                text.Add(title);
                if (!string.IsNullOrEmpty(free.blurb)) text.Add(Text(free.blurb, "abc-free__blurb"));
                line.Add(text);
                line.Add(Text("Get it \u203a", "abc-free__link"));
                return line;
            }

            // -- Art profile ------------------------------------------------

            private bool IsArt => _context.Config.profile == "art";

            private IEnumerable<UnityMcpClickMeAction> Links()
            {
                if (_data.links.Length > 0 || !IsArt) return _data.links;
                // Art packs without their own list: the Welcome's utilities, minus Discord (the footer
                // has it), after the prefabs folder.
                var links = new List<UnityMcpClickMeAction>();
                string prefabs = _context.Config.board.prefabs;
                if (!string.IsNullOrEmpty(prefabs))
                    links.Add(new UnityMcpClickMeAction { label = "Prefabs folder", kind = "ping", target = prefabs });
                links.AddRange(_context.Config.utilities.Where(u => UnityMcpWelcomeServices.IsSet(u) && u.kind != "discord")
                    .Select(u => new UnityMcpClickMeAction { label = u.label, detail = u.detail, kind = u.kind, target = u.target }));
                return links;
            }

            /// <summary>The Welcome Art band, sized for an inspector: for an art pack the only
            /// blocking question is the render pipeline. Returns whether it is the ok band.</summary>
            private bool ArtPipelineReady(VisualElement host)
            {
                UnityMcpPipeline band = _context.Config.pipelineBand;
                string folder = _context.Resolve(band.materials);
                string pipeline = UnityMcpWelcomeServices.PipelineLabel(UnityMcpWelcomeServices.ActivePipeline());
                List<Material> wrong = UnityMcpWelcomeServices.ScopedMismatchedMaterials(folder, band.materialGuids);
                int materials = UnityMcpWelcomeServices.ScopedGuids("t:Material", folder, band.materialGuids).Length;

                VisualElement row = wrong.Count > 0
                    ? Band(host, "warn", wrong.Count + (wrong.Count == 1 ? " material renders" : " materials render") + " magenta in " + pipeline,
                        "They are built for another pipeline. The pack ships a ready-made " + pipeline + " version: one click imports it, and only those files change.")
                    : Band(host, "ok", "Renders in this project",
                        pipeline + " \u00b7 " + materials + (materials == 1 ? " material matches" : " materials match") + " the active pipeline");
                if (wrong.Count > 0 && UnityMcpWelcomeServices.MenuExists(band.fixMenu))
                {
                    VisualElement fix = Clickable(() =>
                    {
                        EditorApplication.ExecuteMenuItem(band.fixMenu);
                        Rebuild();
                    }, "abw-btn", "abw-btn--accent");
                    fix.Add(new Label("Apply the pipeline upgrade"));
                    row.Add(fix);
                    StackButtons(row);
                }
                return wrong.Count == 0;
            }

            /// <summary>The demo scene first: a props buyer wants to see the pack as it ships.
            /// The Welcome and the folder reveal sit under it, quiet.</summary>
            private void BuildArtHero(VisualElement host, bool ready)
            {
                UnityMcpWelcomeData config = _context.Config;
                string guid = _context.Guid;
                if (UnityMcpWelcomeServices.IsSet(config.hero))
                {
                    UnityMcpAction hero = config.hero;
                    Action run = () => UnityMcpWelcomeServices.Run(_context, hero, tab => OpenOn(guid, tab), _catalog);
                    VisualElement button = Focusable(Clickable(run, "abw-hero", "abc-guide"), run);
                    button.EnableInClassList("abc-guide--quiet", !ready);
                    button.Add(Text("\u25b6", "abw-hero__glyph"));
                    var text = new VisualElement();
                    text.AddToClassList("abw-hero__text");
                    text.Add(Text(hero.label, "abw-hero__title"));
                    string detail = ready ? "Every prefab of the pack, as it ships" : "It looks magenta until the upgrade runs";
                    text.Add(Text(detail, "abw-hero__detail"));
                    button.Add(text);
                    button.SetEnabled(UnityMcpWelcomeServices.CanRun(_context, hero));
                    host.Add(button);
                }

                var row = new VisualElement();
                row.AddToClassList("abc-quiet-row");
                if (UnityMcpWelcomeServices.IsSet(config.heroAlt))
                {
                    UnityMcpAction alt = config.heroAlt;
                    Action run = () => UnityMcpWelcomeServices.Run(_context, alt, tab => OpenOn(guid, tab), _catalog);
                    VisualElement button = Focusable(Clickable(run, "abw-btn", "abw-btn--quiet", "abc-quiet"), run);
                    button.Add(new Label(alt.label));
                    button.SetEnabled(UnityMcpWelcomeServices.CanRun(_context, alt));
                    row.Add(button);
                }
                Action guide = () => OpenOn(guid, "start");
                VisualElement welcome = Focusable(Clickable(guide, "abw-btn", "abw-btn--quiet", "abc-quiet"), guide);
                welcome.Add(new Label("Welcome guide \u203a"));
                row.Add(welcome);
                host.Add(row);
            }

            /// <summary>What the pack holds, in words and counts: no previews to load.</summary>
            private void BuildArtPack(VisualElement host)
            {
                var section = new VisualElement();
                section.AddToClassList("abc-section");
                // Counted figures only (a folder filter), never the triangle averages: those load prefabs.
                string aside = string.Join(" \u00b7 ", _context.Config.stats
                    .Where(s => !string.IsNullOrEmpty(s.count) && !string.IsNullOrEmpty(s.label))
                    .Take(3)
                    .Select(s => UnityMcpWelcomeServices.StatValue(_context, s) + " " + InlineLabel(s.label)));
                section.Add(Eyebrow("IN THE PACK", aside));
                if (!string.IsNullOrEmpty(_data.summary)) section.Add(Text(_data.summary, "abc-pack__summary"));
                host.Add(section);
                BuildLinks(host);
            }

            /// <summary>"Prefabs" reads "prefabs" inside a sentence; "FBX meshes" keeps its acronym.</summary>
            private static string InlineLabel(string label) =>
                label.Length > 1 && char.IsUpper(label[0]) && char.IsLower(label[1])
                    ? char.ToLowerInvariant(label[0]) + label.Substring(1)
                    : label;

            private void BuildArtDiscover(VisualElement host)
            {
                var section = new VisualElement();
                section.AddToClassList("abc-section");
                section.Add(Eyebrow("DISCOVER"));
                VisualElement row = Pair(section);
                string self = _context.Config.id;
                int others = _catalog.products.Count(p => p.id != self && TopCategory(p) == "3D");
                int tools = _catalog.products.Count(p => TopCategory(p) == "Tools");
                row.Add(FanTile("Our Stylized packs \u203a", others + " more packs in the same style", Shelf("3D"), SaleCount("3D"), "cat:3D"));
                row.Add(FanTile("Our tools \u203a", tools + " tools to build the game around it", Shelf(), SaleCount("Tools"), "cat:Tools"));
                if (_context.Config.studioTab) section.Add(StudioStrip());
                VisualElement tie = TieLine();
                if (tie != null) section.Add(tie);
                host.Add(section);
            }

            private VisualElement StudioStrip()
            {
                string guid = _context.Guid;
                Action open = () => OpenOn(guid, "studio");
                VisualElement strip = Focusable(Clickable(open, "abc-strip"), open);
                strip.name = "clickme-studio-tile";
                UnityMcpDevlog post = UnityMcpWelcomeServices.LoadDevlog();
                UnityMcpGame[] games = _catalog.games ?? new UnityMcpGame[0];
                strip.Add(LiveImage(() => { Texture2D cover = post != null ? OwnImage(UnityMcpWelcomeServices.DevlogImage) : null; return cover != null ? cover : games.Length > 0 ? GameCover(games[0]) : null; }, "abc-strip__cover", ScaleMode.ScaleAndCrop));
                var text = new VisualElement();
                text.AddToClassList("abc-strip__text");
                text.Add(Text("Our studio \u203a", "abc-disc__title"));
                var parts = games.Select(g => g.name).Where(n => !string.IsNullOrEmpty(n)).Take(2).ToList();
                parts.Add("devblog");
                text.Add(Text(string.Join(", ", parts), "abc-disc__detail"));
                strip.Add(text);
                if (post != null)
                {
                    VisualElement tag = Tag(IsRecent(post.date) ? "NEW POST" : "DEVBLOG", "abc-tag--accent");
                    tag.AddToClassList("abc-tag--inline");
                    strip.Add(tag);
                }
                return strip;
            }

            private UnityMcpProduct TiedProduct()
            {
                string id = _context.Config.tie.product;
                return string.IsNullOrEmpty(id) ? null : _catalog.products.FirstOrDefault(p => p.id == id);
            }

            /// <summary>The one tool of ours that uses this pack, as a capability (Welcome Art's tie).</summary>
            private VisualElement TieLine()
            {
                UnityMcpTie tie = _context.Config.tie;
                UnityMcpProduct product = TiedProduct();
                if (product == null) return null;
                Action open = () => UnityMcpWelcomeServices.OpenProduct(product, _catalog);
                VisualElement line = Focusable(Clickable(open, "abc-tie"), open);
                line.tooltip = product.url;
                line.Add(LiveImage(() => CardImage(product), "abc-free__card", ScaleMode.ScaleAndCrop, UnityMcpWelcomeServices.CachedCardPath(product), fallbackPath: _context.Media("Media/Cards/" + product.card)));
                var text = new VisualElement();
                text.AddToClassList("abc-free__text");
                text.Add(Text(string.IsNullOrEmpty(tie.title) ? product.name : tie.title, "abc-free__name"));
                if (!string.IsNullOrEmpty(tie.body)) text.Add(Text(tie.body, "abc-free__blurb"));
                line.Add(text);
                line.Add(Text((string.IsNullOrEmpty(tie.label) ? "Have a look" : tie.label) + " \u203a", "abc-free__link"));
                return line;
            }

            // -- Review -----------------------------------------------------

            private string ThanksKey => UnityMcpWelcomeServices.PREFS + ".ClickMeThanks." + _context.Config.id;

            /// <summary>"store" is this package's store page, reviews section, as the catalogue
            /// knows it: the same rule as the Welcome's own review card.</summary>
            private string ReviewUrl()
            {
                // An empty reviewUrl means the store page: an art config often has no usage block.
                string url = string.IsNullOrEmpty(_context.Config.usage.reviewUrl) ? "store" : _context.Config.usage.reviewUrl;
                if (url != "store") return url;
                UnityMcpProduct self = Self;
                return self != null && !string.IsNullOrEmpty(self.url) ? self.url + "#reviews" : null;
            }

            /// <summary>
            /// The Welcome's rule (<see cref="UnityMcpWelcomePrompts.ReviewCardDue"/>): three days after the
            /// first use on this machine, 30 days after a "Not now", once the package has been used,
            /// never again once rated. The state is shared with the Welcome and the review popup.
            /// </summary>
            private bool ReviewDue() =>
                !string.IsNullOrEmpty(ReviewUrl()) && UnityMcpWelcomePrompts.ReviewCardDue(_context, _catalog, DateTime.UtcNow);

            /// <summary>Returns whether a review card is on screen, which takes the footer link away.</summary>
            private bool BuildReview(VisualElement host)
            {
                if (SessionState.GetBool(ThanksKey, false))
                {
                    host.Add(Thanks());
                    return true;
                }
                if (!ReviewDue()) return false;

                string name = _context.Config.name;
                var card = new VisualElement();
                card.AddToClassList("abc-review");

                var top = new VisualElement();
                top.AddToClassList("abc-review__top");
                top.Add(Text("Enjoying " + name + "?", "abc-review__title"));
                VisualElement later = Clickable(() =>
                {
                    UnityMcpWelcomePrompts.MarkReviewLater(_context, DateTime.UtcNow);
                    Rebuild();
                }, "abc-review__close");
                later.Add(new Label("\u00d7"));
                later.tooltip = "Not now";
                top.Add(later);
                card.Add(top);
                card.Add(Text("A review takes a minute and is the biggest help a small studio can get. We read every one.", "abc-review__body"));

                // Every star opens the same page: which one was hovered is never sent anywhere,
                // and a low rating is not diverted away from the store.
                var stars = new VisualElement();
                stars.AddToClassList("abc-stars");
                for (int i = 1; i <= STARS; i++)
                {
                    int count = i;
                    VisualElement star = Clickable(Rate, "abc-star");
                    star.Add(new Label("\u2605"));
                    star.tooltip = "Rate it on the Asset Store";
                    star.RegisterCallback<PointerEnterEvent>(_ => HoverStars(count));
                    _stars.Add(star);
                    stars.Add(star);
                }
                stars.RegisterCallback<PointerLeaveEvent>(_ => HoverStars(0));
                card.Add(stars);

                var actions = new VisualElement();
                actions.AddToClassList("abc-review__actions");
                VisualElement rate = Focusable(Clickable(Rate, "abc-cta"), Rate);
                rate.Add(new Label("Leave a review \u203a"));
                actions.Add(rate);
                string rating = RatingLine();
                if (rating != null) actions.Add(Text(rating, "abc-review__rating"));
                card.Add(actions);

                var discord = new VisualElement();
                discord.AddToClassList("abc-review__aside");
                discord.Add(Text("Something broken?", "abc-review__note"));
                Label link = Text("Tell us on Discord first", "abc-link");
                link.AddManipulator(new Clickable(() => Application.OpenURL(_catalog.discordUrl)));
                discord.Add(link);
                card.Add(discord);
                host.Add(card);
                return true;
            }

            private void HoverStars(int count)
            {
                for (int i = 0; i < _stars.Count; i++) _stars[i].EnableInClassList("abc-star--on", i < count);
            }

            /// <summary>The store cannot tell us whether a review was posted: the click is the
            /// answer, thanked once for this session, then folded into the footer for good.</summary>
            private void Rate()
            {
                string url = ReviewUrl();
                if (string.IsNullOrEmpty(url)) return;
                UnityMcpWelcomePrompts.MarkReviewDone(_context);
                SessionState.SetBool(ThanksKey, true);
                Application.OpenURL(url);
                Rebuild();
            }

            private VisualElement Thanks()
            {
                var card = new VisualElement();
                card.AddToClassList("abc-thanks");
                card.Add(Text("\u2713", "abc-thanks__mark"));
                var text = new VisualElement();
                text.AddToClassList("abc-thanks__text");
                text.Add(Text("Thank you, it really counts.", "abc-thanks__title"));
                Label next = Text("Tell us on Discord what we should build next.", "abc-thanks__body");
                next.AddManipulator(new Clickable(() => Application.OpenURL(_catalog.discordUrl)));
                text.Add(next);
                card.Add(text);
                return card;
            }

            /// <summary>The store's own figures, from the fetched catalogue only. A rating of 0 is
            /// an unknown figure and is left out, as on the Welcome's Cards.</summary>
            private string RatingLine()
            {
                UnityMcpProduct self = Self;
                if (self == null || !UnityMcpWelcomeServices.CatalogOnline || self.rating <= 0f) return null;
                string reviews = self.reviews.ToString(CultureInfo.InvariantCulture) + (self.reviews == 1 ? " review" : " reviews");
                return "\u2605 " + self.rating.ToString("0.0", CultureInfo.InvariantCulture) + " \u00b7 " + reviews;
            }

            // -- Footer -----------------------------------------------------

            private VisualElement BuildFooter(bool reviewShown)
            {
                var footer = new VisualElement();
                footer.AddToClassList("abc-footer");
                string discordUrl = _catalog.discordUrl;
                VisualElement discord = Focusable(Clickable(() => Application.OpenURL(discordUrl), "abw-btn", "abw-discord"),
                    () => Application.OpenURL(discordUrl));
                discord.Add(new Label("Join us on Discord"));
                footer.Add(discord);
                footer.Add(Text("Support and early builds", "abc-footer__note"));
                if (!reviewShown && !string.IsNullOrEmpty(ReviewUrl()))
                {
                    Label review = Text("Leave a review", "abc-link", "abc-link--quiet");
                    review.AddManipulator(new Clickable(Rate));
                    footer.Add(review);
                }
                return footer;
            }

            // -- Small builders ---------------------------------------------

            private UnityMcpProduct Self => _catalog?.products.FirstOrDefault(p => p.id == _context.Config.id);

            /// <summary>The package's live line, from its own code. A hook that is missing or
            /// throws gives the generic line: the band must draw in every project state.</summary>
            private string LiveStatus()
            {
                string path = _data.status;
                int dot = string.IsNullOrEmpty(path) ? -1 : path.LastIndexOf('.');
                if (dot <= 0) return null;
                string typeName = path.Substring(0, dot);
                string methodName = path.Substring(dot + 1);
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type type = assembly.GetType(typeName, false);
                    MethodInfo method = type?.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                        null, Type.EmptyTypes, null);
                    if (method == null || method.ReturnType != typeof(string)) continue;
                    try { return method.Invoke(null, null) as string; }
                    catch (TargetInvocationException) { return null; }
                }
                return null;
            }

            private static VisualElement Pair(VisualElement host)
            {
                var row = new VisualElement();
                row.AddToClassList("abc-pair");
                host.Add(row);
                return row;
            }

            private static VisualElement Blank()
            {
                var blank = new VisualElement();
                blank.AddToClassList("abc-blank");
                return blank;
            }

            private static VisualElement Tag(string text, params string[] classes)
            {
                var tag = new VisualElement { pickingMode = PickingMode.Ignore };
                tag.AddToClassList("abc-tag");
                foreach (string c in classes) tag.AddToClassList(c);
                tag.Add(new Label(text));
                return tag;
            }

            private static void AddCaption(VisualElement tile, string title, string detail)
            {
                var caption = new VisualElement();
                caption.AddToClassList("abc-disc__caption");
                caption.Add(Text(title, "abc-disc__title"));
                caption.Add(Text(detail, "abc-disc__detail"));
                tile.Add(caption);
            }

            /// <summary>Tab reaches it, Enter or Space runs it.</summary>
            private static VisualElement Focusable(VisualElement element, Action run)
            {
                element.focusable = true;
                element.RegisterCallback<KeyDownEvent>(evt =>
                {
                    if (!IsSubmit(evt.keyCode)) return;
                    run();
                    evt.StopPropagation();
                });
                return element;
            }
        }
    }
}
