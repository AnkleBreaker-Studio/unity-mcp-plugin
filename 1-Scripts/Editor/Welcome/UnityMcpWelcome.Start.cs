using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityMCP.Editor.Welcome
{
    /// <summary>The Get started column: the only part of the window that differs between the
    /// tool and the art profile.</summary>
    internal sealed partial class UnityMcpWelcome
    {
        // -- Tool profile ---------------------------------------------------

        private void BuildToolStart(VisualElement host)
        {
            UnityMcpWelcomeData config = _context.Config;
            bool blocked = BuildToolStatus(host);
            BuildUpdateBand(host);

            int done = config.steps.Count(IsStepDone);
            bool setupDone = config.steps.Length > 0 && done == config.steps.Length;
            bool showSteps = !setupDone || UnityMcpWelcomeServices.GetFlag(_context, "ShowSteps");

            if (setupDone) BuildSetupDone(host, showSteps);
            if (!setupDone && !blocked) BuildHero(host);
            if (showSteps && config.steps.Length > 0) BuildSteps(host, done);
            if (setupDone && !showSteps) BuildShortcuts(host);
            BuildStats(host);
            if (!setupDone) BuildHelp(host);
            BuildReview(host);
        }

        /// <summary>Returns whether the package is blocked: missing compile dependencies, or not
        /// compiled, both of which make every other button of the column useless.</summary>
        private bool BuildToolStatus(VisualElement host)
        {
            UnityMcpWelcomeData config = _context.Config;
            List<UnityMcpRequirement> missing = UnityMcpWelcomeServices.MissingRequirements(_context);
            List<UnityMcpRequirement> compileMissing = missing.Where(r => r.compile).ToList();

            if (compileMissing.Count > 0)
            {
                VisualElement band = Band(host, "error", "Install " + JoinNames(compileMissing.Select(r => r.name)) + " first",
                    config.name + " needs " + (compileMissing.Count == 1 ? "it" : "them") + " to compile. " +
                    WhereFrom(compileMissing) + " Unity recompiles once " + (compileMissing.Count == 1 ? "it is" : "they are") + " in.");
                AddInstallButtons(band, compileMissing);
                return true;
            }

            if (!string.IsNullOrEmpty(config.ownAssembly) && !UnityMcpWelcomeServices.IsAssemblyLoaded(config.ownAssembly))
            {
                VisualElement band = Band(host, "error", config.name + " has not compiled",
                    "Its dependencies are here, but its code did not build. The first error in the Console names the file.");
                VisualElement console = Clickable(() => EditorApplication.ExecuteMenuItem("Window/General/Console"), "abw-btn");
                console.Add(new Label("Open the Console"));
                band.Add(console);
                return true;
            }

            // A feature dependency the package compiles without: the package works, that feature
            // does not. Never folded into "Ready to go", which would tell the buyer it is active.
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

            var parts = new List<string>();
            List<string> resolved = config.requires.Where(r => !missing.Contains(r)).Select(r => r.name).ToList();
            if (resolved.Count > 0) parts.Add(JoinNames(resolved) + " resolved");
            parts.Add("Unity " + Application.unityVersion);
            parts.Add(UnityMcpWelcomeServices.PipelineLabel(pipeline));
            parts.Add(EditorUtility.scriptCompilationFailed ? "compile errors in the project" : "0 compile errors");
            Band(host, "ok", "Ready to go", string.Join(" \u00b7 ", parts));
            return false;
        }

        /// <summary>The one honest reason to reopen the window later: a newer version exists.</summary>
        private void BuildUpdateBand(VisualElement host)
        {
            UnityMcpProduct self = Self;
            string version = UnityMcpWelcomeServices.DisplayVersion(_context);
            if (self == null || !UnityMcpWelcomeServices.IsNewer(UnityMcpWelcomeServices.StoreVersion(_context, self), version))
                return;
            VisualElement band = Band(host, "warn", _context.Config.name + " " + self.version + " is on the Asset Store",
                (string.IsNullOrEmpty(self.whatsNew) ? "" : self.whatsNew + " ") + "Your project is on " + version + ".");
            VisualElement read = Clickable(() => Application.OpenURL(self.url), "abw-btn");
            read.Add(new Label("Read what changed"));
            band.Add(read);
        }

        private void BuildHero(VisualElement host)
        {
            UnityMcpWelcomeData config = _context.Config;
            if (!UnityMcpWelcomeServices.IsSet(config.hero)) return;

            var row = new VisualElement();
            row.AddToClassList("abw-hero-row");

            VisualElement hero = Clickable(() => Run(config.hero), "abw-hero");
            hero.Add(Text("\u25b6", "abw-hero__glyph"));
            var text = new VisualElement();
            text.AddToClassList("abw-hero__text");
            text.Add(Text(config.hero.label, "abw-hero__title"));
            if (!string.IsNullOrEmpty(config.hero.detail)) text.Add(Text(config.hero.detail, "abw-hero__detail"));
            hero.Add(text);
            hero.SetEnabled(UnityMcpWelcomeServices.CanRun(_context, config.hero));
            row.Add(hero);

            if (UnityMcpWelcomeServices.IsSet(config.heroAlt))
            {
                VisualElement alt = Clickable(() => Run(config.heroAlt), "abw-hero-alt");
                alt.Add(new Label(config.heroAlt.label));
                alt.SetEnabled(UnityMcpWelcomeServices.CanRun(_context, config.heroAlt));
                row.Add(alt);
            }
            host.Add(row);
        }

        private bool IsStepDone(UnityMcpStep step) => UnityMcpWelcomeServices.GetDate(_context, "Step." + step.id) != null;

        private void BuildSteps(VisualElement host, int done)
        {
            UnityMcpStep[] steps = _context.Config.steps;
            VisualElement section = Section(host);
            section.Add(Eyebrow("START HERE", done + " of " + steps.Length + " done"));

            for (int i = 0; i < steps.Length; i++)
            {
                UnityMcpStep step = steps[i];
                DateTime? when = UnityMcpWelcomeServices.GetDate(_context, "Step." + step.id);

                var row = new VisualElement();
                row.AddToClassList("abw-step");
                row.EnableInClassList("abw-step--done", when != null);
                row.Add(Text(when != null ? "\u2713" : (i + 1).ToString(CultureInfo.InvariantCulture), "abw-step__index"));

                var text = new VisualElement();
                text.AddToClassList("abw-step__text");
                text.Add(Text(step.title, "abw-step__title"));
                string body = when != null
                    ? step.doneVerb + " on " + when.Value.ToLocalTime().ToString("d MMM", CultureInfo.InvariantCulture) + " \u2014 " + step.body
                    : step.body;
                text.Add(Text(body, "abw-step__body"));
                row.Add(text);

                if (UnityMcpWelcomeServices.IsSet(step.action))
                {
                    UnityMcpStep captured = step;
                    VisualElement button = Clickable(() =>
                    {
                        UnityMcpWelcomeServices.SetDate(_context, "Step." + captured.id);
                        Run(captured.action);
                        Rebuild();
                    }, "abw-btn", when != null ? "abw-btn--quiet" : "abw-btn");
                    button.Add(new Label(when != null ? "Reopen" : step.action.label));
                    button.SetEnabled(UnityMcpWelcomeServices.CanRun(_context, step.action));
                    row.Add(button);
                }
                section.Add(row);
            }
        }

        private void BuildSetupDone(VisualElement host, bool showingSteps)
        {
            int total = _context.Config.steps.Length;
            VisualElement band = Band(host, "ok", "Setup done \u2014 " + total + " of " + total,
                string.Join(", ", _context.Config.steps.Select(s => s.title)));
            VisualElement toggle = Clickable(() =>
            {
                UnityMcpWelcomeServices.SetFlag(_context, "ShowSteps", !showingSteps);
                Rebuild();
            }, "abw-btn", "abw-btn--quiet");
            toggle.Add(new Label(showingSteps ? "Hide the steps" : "Show the steps"));
            band.Add(toggle);
        }

        private void BuildShortcuts(VisualElement host)
        {
            UnityMcpAction[] shortcuts = _context.Config.shortcuts;
            if (shortcuts.Length == 0) return;
            VisualElement section = Section(host);
            section.Add(Eyebrow("OPEN"));
            var grid = new VisualElement();
            grid.AddToClassList("abw-row");
            foreach (UnityMcpAction action in shortcuts)
            {
                UnityMcpAction captured = action;
                VisualElement tile = Clickable(() => Run(captured), "abw-shortcut");
                tile.Add(Text(action.label, "abw-shortcut__title"));
                if (!string.IsNullOrEmpty(action.detail)) tile.Add(Text(action.detail, "abw-shortcut__detail"));
                tile.SetEnabled(UnityMcpWelcomeServices.CanRun(_context, action));
                grid.Add(tile);
            }
            section.Add(grid);
        }

        private void BuildStats(VisualElement host)
        {
            UnityMcpStat[] stats = _context.Config.stats;
            if (stats.Length == 0) return;
            VisualElement section = Section(host);
            section.Add(Eyebrow(_context.Config.statsTitle));
            var row = new VisualElement();
            row.AddToClassList("abw-stats");
            foreach (UnityMcpStat stat in stats)
            {
                var chip = new VisualElement();
                chip.AddToClassList("abw-stat");
                chip.Add(Text(UnityMcpWelcomeServices.StatValue(_context, stat), "abw-stat__value"));
                chip.Add(Text(stat.label, "abw-stat__label"));
                row.Add(chip);
            }
            section.Add(row);
        }

        private void BuildHelp(VisualElement host)
        {
            UnityMcpHelp help = _context.Config.help;
            VisualElement section = Section(host);
            section.Add(Eyebrow("IF YOU NEED US", help.note));
            var row = new VisualElement();
            row.AddToClassList("abw-row");
            if (UnityMcpWelcomeServices.IsSet(help.docs)) row.Add(Button(help.docs, "abw-btn"));
            row.Add(UrlButton("Report a bug", help.bugUrl));
            row.Add(UrlButton("Request a feature", help.featureUrl));
            section.Add(row);
        }

        private VisualElement UrlButton(string label, string url)
        {
            VisualElement button = Clickable(() => Application.OpenURL(UnityMcpWelcomeServices.Url(url, _catalog)), "abw-btn");
            button.Add(new Label(label));
            button.SetEnabled(!string.IsNullOrEmpty(url));
            return button;
        }

        /// <summary>
        /// Asked on a return visit only, once the package has had time to be used: never at
        /// import, where there is nothing to judge yet. Same clock and same machine-wide state as
        /// the review popup (<see cref="UnityMcpWelcomePrompts"/>), without its cap: here the buyer
        /// opened the window himself.
        /// </summary>
        private void BuildReview(VisualElement host)
        {
            if (!UnityMcpWelcomePrompts.ReviewCardDue(_context, _catalog, DateTime.UtcNow)) return;

            UnityMcpUsage usage = _context.Config.usage;
            int made = UnityMcpWelcomeServices.UsageCount(_context);
            host.Add(Fill());
            var card = new VisualElement();
            card.AddToClassList("abw-review");
            string lead = string.IsNullOrEmpty(usage.phrase) || made < usage.minCount
                ? "Has " + _context.Config.name + " earned its place in your project?"
                : usage.phrase.Replace("{n}", made.ToString(CultureInfo.InvariantCulture)) + " Would you say so?";
            card.Add(Text(lead, "abw-review__title"));
            card.Add(Text("A review is how the next developer finds it. If something is wrong instead, Discord gets you a human faster than one star does.", "abw-review__body"));
            var actions = new VisualElement();
            actions.AddToClassList("abw-review__actions");
            VisualElement rate = Clickable(() =>
            {
                UnityMcpWelcomePrompts.MarkReviewDone(_context);
                string url = UnityMcpWelcomePrompts.ReviewUrl(_context, _catalog);
                if (url != null) Application.OpenURL(url);
                Rebuild();
            }, "abw-btn", "abw-btn--accent");
            rate.Add(new Label("Rate " + _context.Config.name));
            actions.Add(rate);
            VisualElement later = Clickable(() =>
            {
                UnityMcpWelcomePrompts.MarkReviewLater(_context, DateTime.UtcNow);
                Rebuild();
            }, "abw-btn", "abw-btn--quiet");
            later.Add(new Label("Not now"));
            actions.Add(later);
            actions.Add(Fill());
            Label rated = Text("I already rated it", "abw-review__aside");
            rated.AddManipulator(new Clickable(() =>
            {
                UnityMcpWelcomePrompts.MarkReviewDone(_context);
                Rebuild();
            }));
            actions.Add(rated);
            card.Add(actions);
            host.Add(card);
        }

        // -- Art profile ----------------------------------------------------

        private void BuildArtStart(VisualElement host)
        {
            UnityMcpWelcomeData config = _context.Config;
            BuildPipelineBand(host);
            BuildHero(host);
            BuildBoard(host);
            BuildStats(host);
            host.Add(Fill());
            BuildTie(host);
            if (config.utilities.Length > 0)
            {
                var row = new VisualElement();
                row.AddToClassList("abw-row");
                foreach (UnityMcpAction action in config.utilities) row.Add(Button(action, "abw-btn"));
                host.Add(row);
            }
            BuildReview(host);
        }

        /// <summary>For an art pack the only blocking question is the render pipeline: a material
        /// built for another one renders magenta.</summary>
        private void BuildPipelineBand(VisualElement host)
        {
            UnityMcpPipeline band = _context.Config.pipelineBand;
            string folder = _context.Resolve(band.materials);
            string pipeline = UnityMcpWelcomeServices.PipelineLabel(UnityMcpWelcomeServices.ActivePipeline());
            List<Material> wrong = UnityMcpWelcomeServices.ScopedMismatchedMaterials(folder, band.materialGuids);
            int materials = UnityMcpWelcomeServices.ScopedGuids("t:Material", folder, band.materialGuids).Length;
            string textureFolder = _context.Resolve("Textures");
            int textures = UnityMcpWelcomeServices.ScopedGuids("t:Texture2D", textureFolder, band.textureGuids).Length;

            VisualElement row;
            if (wrong.Count > 0)
            {
                row = Band(host, "error", wrong.Count + (wrong.Count == 1 ? " material is" : " materials are") + " built for another pipeline than " + pipeline,
                    "They render magenta until converted. The pack ships a ready-made " + pipeline + " version: one click imports it, and only those files change.");
            }
            else
            {
                row = Band(host, "ok", "Set up for " + pipeline,
                    materials + (materials == 1 ? " material" : " materials") + " \u00b7 " + textures + " textures \u00b7 no magenta, nothing left for you to click");
            }
            if (UnityMcpWelcomeServices.MenuExists(band.fixMenu))
            {
                VisualElement fix = Clickable(() =>
                {
                    EditorApplication.ExecuteMenuItem(band.fixMenu);
                    Rebuild();
                }, "abw-btn", wrong.Count > 0 ? "abw-btn--accent" : "abw-btn--quiet");
                fix.Add(new Label(wrong.Count > 0 ? "Convert now" : "Re-run"));
                row.Add(fix);
            }
        }

        private const int BOARD_ROWS = 4;
        private const float BOARD_TILE_STEP = 106f; // .abw-tile width 98 + right margin 8
        private const long PREVIEW_POLL_MS = 150;

        /// <summary>The pack is its content: its real inventory icons, or the prefab's own
        /// rendered preview when the pack has none, one click per prefab. Four rows at most, whatever
        /// the window width: the last tile says how many more there are and opens the folder, so a
        /// pack of sixty rocks does not bury the rest of the column.</summary>
        private void BuildBoard(VisualElement host)
        {
            UnityMcpBoard board = _context.Config.board;
            string prefabs = _context.Resolve(board.prefabs);
            if ((board.prefabGuids == null || board.prefabGuids.Length == 0) && (string.IsNullOrEmpty(board.prefabs) || !AssetDatabase.IsValidFolder(prefabs))) return;

            string icons = _context.Resolve(board.icons);
            var paths = UnityMcpWelcomeServices.ScopedGuids("t:Prefab", prefabs, board.prefabGuids)
                .Select(AssetDatabase.GUIDToAssetPath)
                .Distinct()
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
            var grouped = paths.Where(p => !string.IsNullOrEmpty(board.groupSuffix) &&
                                           Path.GetFileNameWithoutExtension(p).EndsWith(board.groupSuffix, StringComparison.Ordinal)).ToList();
            var shown = paths.Except(grouped).ToList();

            VisualElement section = Section(host);
            section.Add(Eyebrow(board.title, board.hint));
            var grid = new VisualElement();
            grid.AddToClassList("abw-board");
            string common = CommonPrefix(shown.Select(Path.GetFileNameWithoutExtension).ToList());

            var tiles = new List<BoardTile>();
            foreach (string path in shown)
            {
                string name = Path.GetFileNameWithoutExtension(path);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                VisualElement tile = Clickable(() => UnityMcpWelcomeServices.DropPrefab(prefab), "abw-tile");
                tile.tooltip = name;
                string iconPath = string.IsNullOrEmpty(board.icons) ? null : icons + "/" + name + ".png";
                Texture2D icon = UnityMcpWelcomeServices.LoadImage(iconPath);
                var image = new Image { image = icon != null ? icon : AssetPreview.GetMiniThumbnail(prefab), scaleMode = ScaleMode.ScaleToFit };
                image.AddToClassList("abw-tile__icon");
                tile.Add(image);
                tile.Add(Text(TileLabel(name, common), "abw-tile__label"));
                grid.Add(tile);
                tiles.Add(new BoardTile { Element = tile, Image = image, Prefab = prefab, NeedsPreview = icon == null, Name = name });
            }

            VisualElement group = null;
            if (grouped.Count > 0)
            {
                string first = grouped[0];
                group = Clickable(() => Reveal(first), "abw-tile", "abw-tile--group");
                group.tooltip = string.Join("\n", grouped.Select(Path.GetFileNameWithoutExtension));
                group.Add(Text("+" + grouped.Count, "abw-tile__count"));
                group.Add(Text(board.groupLabel, "abw-tile__label"));
                grid.Add(group);
            }

            string folder = AssetDatabase.IsValidFolder(prefabs) ? prefabs : Path.GetDirectoryName(shown.FirstOrDefault() ?? "")?.Replace('\\', '/');
            Label moreCount = Text("", "abw-tile__count");
            VisualElement more = Clickable(() => Reveal(folder), "abw-tile", "abw-tile--group", "abw-tile--more");
            more.Add(moreCount);
            more.Add(Text("more prefabs", "abw-tile__label"));
            grid.Add(more);
            section.Add(grid);

            int lastCapacity = -1;
            void Layout(float width)
            {
                int perRow = Mathf.Max(1, Mathf.FloorToInt((width + 8f) / BOARD_TILE_STEP));
                int capacity = perRow * BOARD_ROWS;
                if (capacity == lastCapacity) return;
                lastCapacity = capacity;

                int total = tiles.Count + (group != null ? 1 : 0);
                bool overflow = total > capacity;
                int visible = overflow ? capacity - 1 : tiles.Count;
                for (int i = 0; i < tiles.Count; i++)
                    tiles[i].Element.style.display = i < visible ? DisplayStyle.Flex : DisplayStyle.None;
                if (group != null) group.style.display = overflow ? DisplayStyle.None : DisplayStyle.Flex;

                more.style.display = overflow ? DisplayStyle.Flex : DisplayStyle.None;
                if (overflow)
                {
                    int hidden = tiles.Count - visible + grouped.Count;
                    moreCount.text = "+" + hidden;
                    more.tooltip = string.Join("\n", tiles.Skip(visible).Select(t => t.Name).Concat(grouped.Select(Path.GetFileNameWithoutExtension)).Take(40));
                }
                RequestPreviews(tiles.Take(visible).Where(t => t.NeedsPreview).ToList());
            }

            // Before the first layout, assume the narrowest column so nothing flashes past four rows.
            Layout(MIN_SIZE.x * 0.5f);
            grid.RegisterCallback<GeometryChangedEvent>(evt => Layout(evt.newRect.width));
        }

        private sealed class BoardTile
        {
            public VisualElement Element;
            public Image Image;
            public GameObject Prefab;
            public bool NeedsPreview;
            public string Name;
        }

        /// <summary>AssetPreview renders asynchronously and returns null until it has: poll only
        /// the tiles on screen, and stop once each has its picture or Unity gave up on it.</summary>
        private void RequestPreviews(List<BoardTile> pending)
        {
            if (pending.Count == 0) return;
            AssetPreview.SetPreviewTextureCacheSize(Mathf.Max(256, pending.Count * 2));
            IVisualElementScheduledItem poll = null;
            poll = rootVisualElement.schedule.Execute(() =>
            {
                pending.RemoveAll(t =>
                {
                    if (t.Prefab == null || t.Image.panel == null) return true;
                    Texture2D preview = AssetPreview.GetAssetPreview(t.Prefab);
                    if (preview != null) { t.Image.image = preview; t.NeedsPreview = false; return true; }
                    return !AssetPreview.IsLoadingAssetPreview(t.Prefab.GetInstanceID());
                });
                if (pending.Count == 0) poll.Pause();
            }).Every(PREVIEW_POLL_MS);
        }

        private static void Reveal(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return;
            UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset == null) return;
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }

        /// <summary>"Cauldron_T1_Simple" under a common "Cauldron_" prefix reads "T1 Simple"; the
        /// base prefab keeps its full name so it is not left blank.</summary>
        private static string TileLabel(string name, string common)
        {
            string label = common.Length > 0 && name.Length > common.Length && name.StartsWith(common, StringComparison.Ordinal)
                ? name.Substring(common.Length)
                : name;
            return label.Replace('_', ' ');
        }

        private static string CommonPrefix(List<string> names)
        {
            if (names.Count < 2) return "";
            string prefix = names[0];
            foreach (string name in names)
            {
                int i = 0;
                while (i < prefix.Length && i < name.Length && prefix[i] == name[i]) i++;
                prefix = prefix.Substring(0, i);
            }
            int cut = prefix.LastIndexOf('_');
            return cut >= 0 ? prefix.Substring(0, cut + 1) : "";
        }

        private void BuildTie(VisualElement host)
        {
            UnityMcpTie tie = _context.Config.tie;
            if (string.IsNullOrEmpty(tie.product)) return;
            UnityMcpProduct product = _catalog.products.FirstOrDefault(p => p.id == tie.product);
            if (product == null) return;

            var card = new VisualElement();
            card.AddToClassList("abw-tie");
            var text = new VisualElement();
            text.AddToClassList("abw-tie__text");
            text.Add(Text(tie.title, "abw-tie__title"));
            text.Add(Text(tie.body, "abw-tie__body"));
            card.Add(text);
            bool installed = UnityMcpWelcomeServices.IsInstalled(product);
            string label = installed ? "Open it" : UnityMcpWelcomeServices.IsComingSoon(product) ? "Coming soon" : tie.label;
            Label link = Text(label + " \u203a", "abw-link");
            link.AddManipulator(new Clickable(() => UnityMcpWelcomeServices.OpenProduct(product, _catalog)));
            card.Add(link);
            host.Add(card);
        }

        /// <summary>Where each missing package comes from, in the words the buyer needs: a Git
        /// install fails on a machine without Git, an Asset Store package has no one-click add.</summary>
        private static string WhereFrom(List<UnityMcpRequirement> missing)
        {
            var sentences = new List<string>();
            List<UnityMcpRequirement> git = missing.Where(r => UnityMcpWelcomeServices.IsGitSource(r.packageId)).ToList();
            List<UnityMcpRequirement> registry = missing.Where(r => !string.IsNullOrEmpty(r.packageId) && !git.Contains(r)).ToList();
            List<UnityMcpRequirement> store = missing.Where(r => string.IsNullOrEmpty(r.packageId)).ToList();
            if (registry.Count > 0)
                sentences.Add(JoinNames(registry.Select(r => r.name)) + (registry.Count == 1 ? " comes" : " come") + " from the Unity registry.");
            if (git.Count > 0)
                sentences.Add(JoinNames(git.Select(r => r.name)) + (git.Count == 1 ? " comes" : " come") +
                    " from GitHub: Git must be installed on this machine, or the Package Manager reports an error.");
            if (store.Count > 0)
                sentences.Add(JoinNames(store.Select(r => r.name)) + (store.Count == 1 ? " is" : " are") +
                    " on the Asset Store: import " + (store.Count == 1 ? "it" : "them") + " from Package Manager > My Assets.");
            return string.Join(" ", sentences);
        }

        /// <summary>One Install for everything the Package Manager can add, one store link for
        /// each package it cannot.</summary>
        private static void AddInstallButtons(VisualElement band, List<UnityMcpRequirement> missing)
        {
            var buttons = new List<VisualElement>();
            int installable = missing.Count(r => !string.IsNullOrEmpty(r.packageId));
            if (installable > 0)
            {
                // Two assemblies can ship in one package: add each source once.
                string[] ids = missing.Select(r => r.packageId).Where(id => !string.IsNullOrEmpty(id)).Distinct().ToArray();
                VisualElement install = Clickable(() => UnityEditor.PackageManager.Client.AddAndRemove(ids), "abw-btn", "abw-btn--accent");
                install.Add(new Label(installable == missing.Count ? (installable == 1 ? "Install it" : "Install all " + installable) : "Install " + installable + " of " + missing.Count));
                buttons.Add(install);
            }
            foreach (UnityMcpRequirement requirement in missing.Where(r => string.IsNullOrEmpty(r.packageId) && !string.IsNullOrEmpty(r.url)))
            {
                string url = requirement.url;
                VisualElement open = Clickable(() => Application.OpenURL(url), "abw-btn");
                open.Add(new Label("Get " + requirement.name));
                buttons.Add(open);
            }

            // One button sits beside the text; several go under it, or they squeeze the text.
            if (buttons.Count == 1)
            {
                band.Add(buttons[0]);
                return;
            }
            var row = new VisualElement();
            row.AddToClassList("abw-band__actions");
            foreach (VisualElement button in buttons) row.Add(button);
            (band.Q(className: "abw-band__text") ?? band).Add(row);
        }

        private static string JoinNames(IEnumerable<string> names)
        {
            List<string> list = names.Where(n => !string.IsNullOrEmpty(n)).ToList();
            if (list.Count <= 1) return list.FirstOrDefault() ?? "";
            return string.Join(", ", list.Take(list.Count - 1)) + " and " + list.Last();
        }
    }
}
