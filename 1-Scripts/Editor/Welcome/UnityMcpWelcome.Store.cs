using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityMCP.Editor.Welcome
{
    /// <summary>
    /// The showcase: the right-hand panel (identical on every tab that has it, it does not know
    /// which one is showing), the Assets tab that is its full-width catalogue, and the Studio tab.
    ///
    /// <para>Every product fact on screen comes from the store through the catalogue: name,
    /// category, price, sale, rating, published or coming soon. Nothing here is typed per product.</para>
    /// </summary>
    internal sealed partial class UnityMcpWelcome
    {
        private string _studioGame;
        private readonly Dictionary<string, float> _gameCarouselOffsets = new Dictionary<string, float>();
        // -- Panel ----------------------------------------------------------

        private UnityMcpShowcase Showcase =>
            _catalog.showcases.FirstOrDefault(s => s.id == _context.Config.id) ?? _context.Config.showcase;

        private VisualElement BuildPanel()
        {
            using var perf = new UnityMcpWelcomePerf.Scope("UI.BuildPanel");
            UnityMcpShowcase showcase = Showcase;
            var frame = new VisualElement();
            frame.AddToClassList("abw-panel");
            Vector2 offset = _panelScroll != null ? _panelScroll.scrollOffset : Vector2.zero;
            _panelScroll = Scroll("abw-panel-scroll", "abw-panel-content");
            frame.Add(_panelScroll);
            VisualElement panel = _panelScroll.contentContainer;

            BuildFreeProducts(panel);
            panel.Add(Text(showcase.title, "abw-panel__title"));
            if (!string.IsNullOrEmpty(showcase.blurb)) panel.Add(Text(showcase.blurb, "abw-panel__blurb"));

            List<UnityMcpProduct> shelf = _catalog.products
                .Where(p => !IsSelf(p) && !p.pinned && TopCategory(p) == "Tools")
                .Where(p => !UnityMcpWelcomeServices.IsInstalled(p))
                .Where(p => UnityMcpWelcomeServices.CanRecommend(p, RecommendationsOnly))
                .OrderBy(p => UnityMcpWelcomeServices.IsComingSoon(p))
                .ThenByDescending(p => !RecommendationsOnly && p.discount > 0)
                .ThenByDescending(p => ReleaseDate(p))
                .ThenBy(p => p.name)
                .Take(6).ToList();
            if (shelf.Count > 0)
            {
                var row = new VisualElement();
                row.AddToClassList("abw-shelf");
                row.style.marginTop = 7;
                foreach (UnityMcpProduct product in shelf) row.Add(Card(product, null, false));
                panel.Add(row);
            }

            int count = Filtered("all", includeSelf: true).Count();
            VisualElement seeAll = Clickable(() => { _filter = "all"; OpenTab("assets"); }, "abw-catalog-next");
            string collection = "assets";
            seeAll.Add(Text("Discover all " + count.ToString(CultureInfo.InvariantCulture) + " " + collection + "  \u2192", "abw-catalog-next__title"));
            seeAll.Add(Text(RecommendationsOnly ? "View on the Unity Asset Store" : "Open the full catalogue", "abw-catalog-next__subtitle"));
            panel.Add(seeAll);

            ScrollView restored = _panelScroll;
            restored.schedule.Execute(() => restored.scrollOffset = offset);
            return frame;
        }

        private void BuildFreeProducts(VisualElement panel)
        {
            List<UnityMcpProduct> free = _catalog.products.Where(p => p.pinned && !IsSelf(p) && UnityMcpWelcomeServices.CanRecommend(p, RecommendationsOnly)).Take(2).ToList();
            if (free.Count == 0) return;
            panel.Add(Eyebrow(RecommendationsOnly ? "MORE FROM OUR STUDIO" : "FREE FROM US", null, true));
            foreach (UnityMcpProduct product in free) panel.Add(Line(product));
        }

        private static VisualElement Spacer(float height)
        {
            var spacer = new VisualElement();
            spacer.style.height = height;
            return spacer;
        }

        private bool IsSelf(UnityMcpProduct product) => product.id == _context.Config.id;

        private List<(UnityMcpProduct, string)> PickedShelf()
        {
            var shelf = new List<(UnityMcpProduct, string)>();
            foreach (UnityMcpPick pick in Showcase.shelf)
            {
                UnityMcpProduct product = _catalog.products.FirstOrDefault(p => p.id == pick.id);
                if (UnityMcpWelcomeServices.CanRecommend(product, RecommendationsOnly) && !IsSelf(product) && !product.pinned && !UnityMcpWelcomeServices.IsInstalled(product)) shelf.Add((product, pick.pitch));
            }
            return shelf;
        }

        /// <summary>Same family first: the ones the buyer can buy today, then the ones coming,
        /// then the ones already installed.</summary>
        private List<(UnityMcpProduct, string)> FamilyShelf(UnityMcpFamily family) =>
            _catalog.products
                .Where(p => UnityMcpWelcomeServices.CanRecommend(p, RecommendationsOnly) && p.family == family.id && !IsSelf(p) && !p.pinned && !UnityMcpWelcomeServices.IsInstalled(p))
                .OrderBy(p => UnityMcpWelcomeServices.IsInstalled(p) ? 2 : UnityMcpWelcomeServices.IsComingSoon(p) ? 1 : 0)
                .Select(p => (p, (string)null))
                .ToList();

        private UnityMcpFamily FamilyOf(string id) =>
            string.IsNullOrEmpty(id) ? null : _catalog.families.FirstOrDefault(f => f.id == id);

        /// <summary>Completing a set is the argument a props buyer acts on. No bundle exists on
        /// the store, so the card names the others and opens the family: no price, no deduction.</summary>
        private void BuildFamilyCard(VisualElement host, UnityMcpFamily family)
        {
            List<UnityMcpProduct> members = _catalog.products.Where(p => p.family == family.id && UnityMcpWelcomeServices.CanRecommend(p, RecommendationsOnly)).ToList();
            if (members.Count < 2) return;
            List<UnityMcpProduct> owned = members.Where(p => IsSelf(p) || UnityMcpWelcomeServices.IsInstalled(p)).ToList();
            List<UnityMcpProduct> others = members.Except(owned).ToList();

            var card = new VisualElement();
            card.AddToClassList("abw-family");
            string title = others.Count == 0
                ? "You have all " + members.Count + " " + family.name + " packs"
                : "You have " + owned.Count + " of the " + members.Count + " " + family.name + " packs";
            card.Add(Text(title, "abw-family__title"));
            Label link = Text("See the whole family \u203a", "abw-link");
            link.AddManipulator(new Clickable(() => { _filter = "family:" + family.id; OpenTab("assets"); }));
            card.Add(link);
            host.Add(card);
        }

        private static string NumberWord(int n) =>
            n >= 0 && n < 10 ? new[] { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine" }[n] : n.ToString(CultureInfo.InvariantCulture);

        // -- Cards ----------------------------------------------------------

        /// <summary>
        /// A store Card: the real 420x280 image, the name, why, and the store line. Installed, it
        /// stays under a veil and says thank you rather than disappearing. On sale, the store's own
        /// percentage sits on the image and the pre-sale price is struck through. Coming soon, it
        /// has no price and opens the Discord.
        /// </summary>
        private VisualElement Card(UnityMcpProduct product, string pitch, bool wide, string tag = null)
        {
            using var perf = new UnityMcpWelcomePerf.Scope("UI.Card");
            bool installed = UnityMcpWelcomeServices.IsInstalled(product);
            bool soon = UnityMcpWelcomeServices.IsComingSoon(product);
            bool sale = !RecommendationsOnly && product.discount > 0 && !installed && !soon;
            VisualElement card = Clickable(() => UnityMcpWelcomeServices.OpenProduct(product, _catalog), "abw-card");
            if (wide) card.AddToClassList("abw-card--wide");
            if (!RecommendationsOnly && product.free) card.AddToClassList("abw-card--free");
            if (sale) card.AddToClassList("abw-card--sale");
            if (installed) card.AddToClassList("abw-card--installed");
            card.tooltip = installed ? product.name + " is in this project"
                : soon ? "Not on the store yet: it will be announced on our Discord"
                : product.storeName;

            var media = new VisualElement();
            media.AddToClassList("abw-card__media");
            media.RegisterCallback<GeometryChangedEvent>(evt =>
            {
                float height = evt.newRect.width * 2f / 3f;
                if (height > 0f && Mathf.Abs(evt.newRect.height - height) > 0.5f) media.style.height = height;
            });
            Label placeholder = Text(product.name, "abw-card__placeholder-name");
            media.Add(placeholder);
            Image picture = LiveImage(() => UnityMcpWelcomeServices.Card(_context, product), "abw-card__image",
                path: UnityMcpWelcomeServices.CachedCardPath(product), fallbackPath: _context.Media("Media/Cards/" + product.card), availability: ready =>
                {
                    media.EnableInClassList("abw-card__placeholder", !ready);
                    placeholder.style.display = ready ? DisplayStyle.None : DisplayStyle.Flex;
                });
            media.Insert(0, picture);
            if (sale)
            {
                var badge = new VisualElement();
                badge.AddToClassList("abw-card__sale");
                badge.Add(new Label("\u2212" + product.discount + "%"));
                media.Add(badge);
            }
            if (installed)
            {
                var veil = new VisualElement();
                veil.AddToClassList("abw-card__veil");
                var badge = new VisualElement();
                badge.AddToClassList("abw-card__veil-badge");
                badge.Add(new Label("\u2713  INSTALLED"));
                veil.Add(badge);
                media.Add(veil);
            }
            card.Add(media);

            var body = new VisualElement();
            body.AddToClassList("abw-card__body");
            var nameRow = new VisualElement();
            nameRow.AddToClassList("abw-card__name-row");
            nameRow.Add(Text(product.name, "abw-card__name"));
            if (soon) nameRow.Add(Pill("COMING SOON", "abw-pill--soon"));
            else if (!RecommendationsOnly && product.free && !wide) nameRow.Add(Pill("FREE", null));
            else if (!string.IsNullOrEmpty(tag)) nameRow.Add(Pill(tag, "abw-pill--quiet"));
            body.Add(nameRow);
            if (wide && !string.IsNullOrEmpty(product.category)) body.Add(Text(product.category, "abw-card__category"));

            if (installed)
            {
                if (!string.IsNullOrEmpty(product.blurb)) body.Add(Text(product.blurb, "abw-card__blurb"));
                if (!IsSelf(product) && UnityMcpWelcomeServices.MenuExists(product.window)) body.Add(Text("Open its window \u203a", "abw-card__link"));
            }
            else
            {
                string blurb = !string.IsNullOrEmpty(pitch) ? pitch : !string.IsNullOrEmpty(product.blurb) ? product.blurb : wide ? null : product.category;
                if (!string.IsNullOrEmpty(blurb)) body.Add(Text(blurb, "abw-card__blurb"));
                if (soon) body.Add(Text("Follow it on Discord \u203a", "abw-card__link"));
                else
                {
                    VisualElement store = StoreLine(product);
                    if (store != null) body.Add(store);
                }
            }
            if (installed)
            {
                var thanks = new VisualElement();
                thanks.AddToClassList("abw-card__store");
                thanks.AddToClassList("abw-card__thanks-row");
                thanks.Add(Text("Thanks for your trust", "abw-card__thanks"));
                body.Add(thanks);
            }
            card.Add(body);
            return card;
        }

        /// <summary>Rating, review count, price, and on a sale the struck pre-sale price. Left out
        /// when the catalogue has not been reached: an unknown figure is not drawn as zero stars,
        /// and the store itself shows no average under three reviews.</summary>
        private VisualElement StoreLine(UnityMcpProduct product)
        {
            if (RecommendationsOnly) return Text("View on the Unity Asset Store >", "abw-card__link");
            bool rated = product.rating > 0f;
            bool priced = !product.free && !string.IsNullOrEmpty(product.price);
            if (!rated && !priced && !product.free) return null;
            var line = new VisualElement();
            line.AddToClassList("abw-card__store");
            if (rated)
            {
                line.Add(Text("\u2605 " + product.rating.ToString("0.0", CultureInfo.InvariantCulture), "abw-card__rating"));
                line.Add(Text("(" + product.reviews.ToString(CultureInfo.InvariantCulture) + ")", "abw-card__reviews"));
            }
            var spacer = new VisualElement();
            spacer.AddToClassList("abw-card__spacer");
            line.Add(spacer);
            if (product.free)
                line.Add(Text("FREE", "abw-card__price", "abw-card__price--free"));
            else if (priced && product.discount > 0 && !string.IsNullOrEmpty(product.originalPrice))
            {
                line.Add(Struck(product.originalPrice));
                line.Add(Text(product.price, "abw-card__price", "abw-card__price--sale"));
            }
            else if (priced)
            {
                line.Add(Text(product.price, "abw-card__price"));
            }
            return line;
        }

        /// <summary>A struck-through price. UI Toolkit 2022.3 has no text-decoration, and rich text
        /// strikethrough differs between its text engines, so the line is an element of its own.</summary>
        private static VisualElement Struck(string text)
        {
            var box = new VisualElement();
            box.AddToClassList("abw-struck");
            box.Add(Text(text, "abw-struck__text"));
            var line = new VisualElement();
            line.AddToClassList("abw-struck__line");
            box.Add(line);
            return box;
        }

        private static VisualElement Pill(string text, string variant)
        {
            var pill = new VisualElement();
            pill.AddToClassList("abw-pill");
            if (!string.IsNullOrEmpty(variant)) pill.AddToClassList(variant);
            var label = new Label(text);
            label.style.whiteSpace = WhiteSpace.NoWrap;
            label.style.flexShrink = 0;
            pill.Add(label);
            return pill;
        }

        private VisualElement Line(UnityMcpProduct product)
        {
            bool installed = UnityMcpWelcomeServices.IsInstalled(product);
            VisualElement line = Clickable(() => UnityMcpWelcomeServices.OpenProduct(product, _catalog), "abw-line");
            line.Add(LiveImage(() => UnityMcpWelcomeServices.Card(_context, product), "abw-line__image", ScaleMode.ScaleToFit));
            var text = new VisualElement();
            text.AddToClassList("abw-line__text");
            var heading = new VisualElement();
            heading.AddToClassList("abw-line__heading");
            heading.Add(Text(product.name, "abw-line__name"));
            if (!RecommendationsOnly) heading.Add(Pill("FREE", null));
            text.Add(heading);
            AddGameBadges(text, product);
            if (!string.IsNullOrEmpty(product.blurb)) text.Add(Text(product.blurb, "abw-line__pitch"));
            bool canOpen = installed && UnityMcpWelcomeServices.MenuExists(product.window);
            text.Add(Text(canOpen ? "Installed - Open tool >" : installed ? "Installed - View on store >" : "Discover the tool >", "abw-line__action"));
            line.Add(text);
            return line;
        }

        private static string TopCategory(UnityMcpProduct product)
        {
            string category = product.category ?? "";
            int slash = category.IndexOf('/');
            return (slash < 0 ? category : category.Substring(0, slash)).Trim();
        }

        /// <param name="includeSelf">True for the counts ("See all 45", "45 assets"), which
        /// describe the catalogue; false for the grid, which never advertises this package to
        /// itself. Counting one way and listing the other made the panel say 17 and the tab 18.</param>
        private IEnumerable<UnityMcpProduct> Filtered(string filter, bool includeSelf = false)
        {
            IEnumerable<UnityMcpProduct> all = _catalog.products.Where(p => (includeSelf || !IsSelf(p)) && UnityMcpWelcomeServices.CanRecommend(p, RecommendationsOnly));
            if (string.IsNullOrEmpty(filter) || filter == "all") return all;
            if (filter == "free") return all.Where(p => p.free);
            // A sale on something the buyer already owns is not an offer.
            if (filter == "sale") return all.Where(p => p.discount > 0 && !UnityMcpWelcomeServices.IsComingSoon(p) && !UnityMcpWelcomeServices.IsInstalled(p));
            if (filter == "soon") return all.Where(UnityMcpWelcomeServices.IsComingSoon);
            if (filter == "art") filter = "cat:3D";
            if (filter.StartsWith("family:", StringComparison.Ordinal)) return all.Where(p => p.family == filter.Substring(7));
            if (filter.StartsWith("cat:", StringComparison.Ordinal)) return all.Where(p => TopCategory(p) == filter.Substring(4));
            return all;
        }

        /// <summary>The chips follow the data: the store's top-level categories as they are on the
        /// catalogue, "On sale" only while something is, "Coming soon" only while something is.</summary>
        private List<(string id, string label)> Filters()
        {
            List<UnityMcpProduct> others = _catalog.products.Where(p => !IsSelf(p)).ToList();
            var filters = new List<(string, string)> { ("all", "All"), ("free", "Free") };
            if (others.Any(p => p.discount > 0 && !UnityMcpWelcomeServices.IsComingSoon(p) && !UnityMcpWelcomeServices.IsInstalled(p)))
                filters.Add(("sale", "On sale"));
            foreach (IGrouping<string, UnityMcpProduct> group in others.Where(p => TopCategory(p) != "").GroupBy(TopCategory).OrderByDescending(g => g.Count()))
                filters.Add(("cat:" + group.Key, group.Key));
            if (others.Any(UnityMcpWelcomeServices.IsComingSoon)) filters.Add(("soon", "Coming soon"));
            if (_filter.StartsWith("family:", StringComparison.Ordinal))
            {
                UnityMcpFamily family = FamilyOf(_filter.Substring(7));
                if (family != null) filters.Add((_filter, family.name));
            }
            return filters;
        }

        private void BuildAssetsTab(VisualElement host)
        {
            using var perf = new UnityMcpWelcomePerf.Scope("UI.BuildAssetsTab");
            if (_filter == "art") _filter = "cat:3D";
            // What is on screen here is no longer news for the discovery prompt.
            UnityMcpWelcomePrompts.MarkCatalogSeen(_catalog, DateTime.UtcNow);
            var head = new VisualElement();
            head.AddToClassList("abw-assets-head");
            var text = new VisualElement();
            text.AddToClassList("abw-assets-head__text");
            int total = Filtered("all", includeSelf: true).Count();
            text.Add(Text(total + " assets from AnkleBreaker Studio", "abw-assets-head__title"));
            text.Add(Text("Every one came out of our own game. " + FreshnessLine(), "abw-assets-head__detail"));
            head.Add(text);

            var chips = new VisualElement();
            chips.AddToClassList("abw-chips");
            foreach ((string id, string label) in Filters())
            {
                string captured = id;
                VisualElement chip = Clickable(() => SelectFilter(captured), "abw-chip");
                chip.EnableInClassList("abw-chip--on", _filter == id);
                if (id == "sale") chip.AddToClassList("abw-chip--sale");
                chip.Add(new Label(label));
                chips.Add(chip);
            }
            host.Add(head);
            // Their own row: the chips follow the data, and eight of them next to the title ran
            // into the right edge.
            host.Add(chips);

            _assetFilterIds = new HashSet<string>(Filtered(_filter, includeSelf: true).Select(p => p.id));
            var picks = new HashSet<string>(Showcase.shelf.Select(p => p.id));
            List<UnityMcpProduct> grid = Filtered("all", includeSelf: true)
                .OrderBy(p => Rank(p))
                .ThenByDescending(p => ReleaseDate(p))
                .ThenBy(p => p.name)
                .ToList();
            List<UnityMcpProduct> available = grid.Where(p => !UnityMcpWelcomeServices.IsComingSoon(p)).ToList();
            List<UnityMcpProduct> upcoming = grid.Where(UnityMcpWelcomeServices.IsComingSoon).ToList();
            if (available.Count > 0) BuildAssetsSection(host, available, picks, false, false);
            if (upcoming.Count > 0) BuildAssetsSection(host, upcoming, picks, true, available.Count > 0);
            var empty = Text("Nothing in this filter yet.", "abw-assets-head__detail");
            empty.name = "assets-empty";
            host.Add(empty);
            ApplyAssetFilter(host);
        }

        private void BuildAssetsSection(VisualElement host, List<UnityMcpProduct> products, HashSet<string> picks, bool upcoming, bool separated)
        {
            using var perf = new UnityMcpWelcomePerf.Scope("UI.AssetsSection");
            var section = new VisualElement { name = upcoming ? "assets-coming-soon" : "assets-available" };
            section.AddToClassList("abw-assets-section");
            if (separated) section.AddToClassList("abw-assets-section--separated");
            section.Add(Text((upcoming ? "Coming soon" : "Available now") + " (" + products.Count + ")", "abw-assets-section__title"));
            if (upcoming)
                section.Add(Text("Not released yet. Follow their progress on Discord.", "abw-assets-section__detail"));
            var cards = new VisualElement();
            cards.AddToClassList("abw-grid");
            foreach (UnityMcpProduct product in products)
            {
                string pitch = Showcase.shelf.FirstOrDefault(p => p.id == product.id)?.pitch;
                string tag = picks.Contains(product.id) ? "PAIRS" : null;
                if (_pageBuildQueue != null) _pageBuildQueue.Enqueue(() => AddAssetCard(cards, product, pitch, tag));
                else AddAssetCard(cards, product, pitch, tag);
            }
            section.Add(cards);
            host.Add(section);
        }

        private static int Rank(UnityMcpProduct product)
        {
            if (UnityMcpWelcomeServices.IsComingSoon(product)) return 3;
            return product.discount > 0 ? 1 : 2;
        }

        private static DateTime ReleaseDate(UnityMcpProduct product) =>
            DateTime.TryParse(product.releasedAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out DateTime date)
                ? date : DateTime.MinValue;

        private string FreshnessLine()
        {
            return "Catalogue updated " + _catalog.updated + ". Prices in USD; check the Asset Store for the final price.";
        }

        // -- Studio tab -----------------------------------------------------

        private IEnumerable<UnityMcpGame> VisibleGames => (_catalog.games ?? new UnityMcpGame[0]).Where(game => !game.hidden);

        /// <summary>The Studio blocks this package draws, in order. The catalogue decides; without
        /// a list, the built-in order puts the ventures between the games and the studio.</summary>
        private IEnumerable<string> StudioBlocks()
        {
            UnityMcpStudioBlock[] blocks = _catalog.studio?.blocks;
            if (blocks == null || blocks.Length == 0)
                return new[] { "devlog", "games" }
                    .Concat((_catalog.ventures ?? new UnityMcpVenture[0]).Select(venture => venture.id))
                    .Concat(new[] { "about", "careers", "consulting" });
            return blocks.Where(block => block != null && !block.hidden && !string.IsNullOrEmpty(block.id) &&
                    (block.profiles == null || block.profiles.Length == 0 || block.profiles.Contains(_context.Config.profile)))
                .Select(block => block.id).Distinct();
        }

        private void BuildStudioTab(VisualElement host)
        {
            using var perf = new UnityMcpWelcomePerf.Scope("UI.BuildStudioTab");
            // Careers and consulting share a row when they follow each other.
            VisualElement cards = null;
            foreach (string id in StudioBlocks())
            {
                Action build = () =>
                {
                    if (id == "careers" || id == "consulting")
                    {
                        if (cards == null)
                        {
                            cards = new VisualElement();
                            cards.AddToClassList("abw-row");
                            cards.style.marginRight = -10;
                            host.Add(cards);
                        }
                        cards.Add(id == "careers" ? CareersCard() : ConsultingCard());
                        return;
                    }
                    cards = null;
                    if (id == "devlog")
                    {
                        var anchor = new VisualElement { name = "devlog-anchor" };
                        anchor.style.display = DisplayStyle.None;
                        host.Add(anchor);
                        BuildDevlog(host);
                    }
                    else if (id == "games") BuildGames(host);
                    else if (id == "about") BuildAbout(host);
                    else
                    {
                        UnityMcpVenture venture = _catalog.ventures?.FirstOrDefault(v => v.id == id);
                        if (venture != null) BuildVenture(host, venture);
                    }
                };
                if (_pageBuildQueue != null) _pageBuildQueue.Enqueue(build);
                else build();
            }
        }

        private void BuildGames(VisualElement host)
        {
            UnityMcpGame[] visible = VisibleGames.ToArray();
            if (visible.Length == 0) return;
            host.Add(Eyebrow("MADE WITH OUR PACKAGES", null));
            host.Add(Text("The games behind the tools", "abw-studio__title"));
            host.Add(Text("Explore our games and the packages we use to build them.", "abw-studio__body"));
            var games = new VisualElement();
            games.AddToClassList("abw-games");
            foreach (UnityMcpGame game in visible.OrderBy(game => game.id == "mithrall" ? 0 : game.id == "kickdom" ? 1 : 2))
                if (_pageBuildQueue != null) _pageBuildQueue.Enqueue(() => games.Add(GameCard(game)));
                else games.Add(GameCard(game));
            host.Add(games);
        }

        private void BuildAbout(VisualElement host)
        {
            UnityMcpStudio studio = _context.Config.studio;
            UnityMcpStudioLayout remote = _catalog.studio;
            host.Add(Text(Pick(remote?.title, studio.title), "abw-studio__title"));
            host.Add(Text(Pick(remote?.body, studio.body), "abw-studio__body"));
        }

        private VisualElement CareersCard()
        {
            UnityMcpStudioCard card = _catalog.studio?.careers;
            return StudioCard(Pick(card?.title, "Come build with us"),
                Pick(card?.body, "See our open roles, or tell us what you would bring."),
                Pick(card?.link, "View open roles \u203a"), _catalog.careersUrl);
        }

        private VisualElement ConsultingCard()
        {
            UnityMcpStudioCard card = _catalog.studio?.consulting;
            return StudioCard(Pick(card?.title, "Need senior engineers?"),
                Pick(card?.body, "AnkleBreaker Consulting builds games, SaaS and web platforms, AI and developer tools, and joins your team when you need more hands."),
                Pick(card?.link, "Visit AnkleBreaker Consulting \u203a"), _catalog.consultingUrl);
        }

        private static string Pick(string remote, string local) => string.IsNullOrEmpty(remote) ? local : remote;

        /// <summary>A studio product outside the Asset Store, drawn with the anatomy of a game card:
        /// cover under a veil, logo top right, links, then a strip of what it gives. Promotion, so
        /// it follows the showcase rule and only shows with the remote catalogue.</summary>
        private void BuildVenture(VisualElement host, UnityMcpVenture venture)
        {
            if (!UnityMcpWelcomeServices.CatalogOnline) return;
            if (!string.IsNullOrEmpty(venture.eyebrow)) host.Add(Eyebrow(venture.eyebrow, null));
            if (!string.IsNullOrEmpty(venture.heading)) host.Add(Text(venture.heading, "abw-studio__title"));
            if (!string.IsNullOrEmpty(venture.intro)) host.Add(Text(venture.intro, "abw-studio__body"));

            var card = new VisualElement { name = "venture-" + venture.id };
            card.AddToClassList("abw-game");
            card.AddToClassList("abw-venture");
            card.Add(LiveImage(() => UnityMcpWelcomeServices.VentureImage(venture, false), "abw-game__backdrop", ScaleMode.ScaleAndCrop));
            var mask = new VisualElement { pickingMode = PickingMode.Ignore };
            mask.AddToClassList("abw-game__mask");
            mask.generateVisualContent += context => DrawMask(context, mask.contentRect, GAME_MASK);
            card.Add(mask);

            var intro = new VisualElement();
            intro.AddToClassList("abw-game__intro");
            intro.AddToClassList("abw-venture__intro");
            intro.Add(LiveImage(() => UnityMcpWelcomeServices.VentureImage(venture, true), "abw-game__logo", ScaleMode.ScaleToFit));
            var body = new VisualElement();
            body.AddToClassList("abw-game__body");
            if (!string.IsNullOrEmpty(venture.tagline)) body.Add(Text(venture.tagline, "abw-game__tagline"));
            body.Add(Text(venture.title, "abw-venture__title"));
            if (!string.IsNullOrEmpty(venture.summary)) body.Add(Text(venture.summary, "abw-game__summary"));
            var links = new VisualElement();
            links.AddToClassList("abw-game__links");
            foreach (UnityMcpVentureLink link in venture.links ?? new UnityMcpVentureLink[0])
                links.Add(GameLink(link.label, link.url, link.primary));
            body.Add(links);
            intro.Add(body);
            card.Add(intro);

            UnityMcpVentureFeature[] features = (venture.features ?? new UnityMcpVentureFeature[0]).Where(f => f != null && !string.IsNullOrEmpty(f.title)).ToArray();
            if (features.Length > 0 || !string.IsNullOrEmpty(venture.footnote))
            {
                var strip = new VisualElement();
                strip.AddToClassList("abw-game__packages");
                if (!string.IsNullOrEmpty(venture.featuresTitle)) strip.Add(Text(venture.featuresTitle, "abw-game__usage-title"));
                var row = new VisualElement();
                row.AddToClassList("abw-venture__features");
                foreach (UnityMcpVentureFeature feature in features)
                {
                    var tile = new VisualElement();
                    tile.AddToClassList("abw-venture__feature");
                    tile.Add(Text(feature.title, "abw-venture__feature-title"));
                    if (!string.IsNullOrEmpty(feature.body)) tile.Add(Text(feature.body, "abw-venture__feature-body"));
                    row.Add(tile);
                }
                if (features.Length > 0) strip.Add(row);
                if (!string.IsNullOrEmpty(venture.footnote)) strip.Add(Text(venture.footnote, "abw-venture__footnote"));
                card.Add(strip);
            }
            host.Add(card);
        }

        private void AddGameBadges(VisualElement host, UnityMcpProduct product)
        {
            // A badge opens the game in Studio: none while the catalogue keeps the games out of it.
            if (!HasStudioTab || _catalog.games == null || !StudioBlocks().Contains("games")) return;
            var row = new VisualElement();
            row.AddToClassList("abw-game-badges");
            foreach (UnityMcpGame game in VisibleGames)
            {
                UnityMcpGameProduct use = game.products.FirstOrDefault(p => p.id == product.id);
                if (use == null) continue;
                var badge = new Button(() => OpenGame(game.id)) { text = "Used in " + game.name };
                badge.name = "game-badge-" + game.id + "-" + product.id;
                badge.AddToClassList("abw-game-badge");
                badge.tooltip = use.role == "development" ? "Development tool for " + game.name + ". Discover the game in Studio." : "Discover " + game.name + " in Studio.";
                row.Add(badge);
            }
            if (row.childCount > 0) host.Add(row);
        }

        private void OpenGame(string id)
        {
            _studioGame = id;
            OpenTab("studio");
            ScrollView scroll = _scroll;
            VisualElement target = scroll.Q<VisualElement>("game-" + id);
            if (target == null) return;
            scroll.schedule.Execute(() => scroll.scrollOffset = new Vector2(0,
                Mathf.Max(0, target.worldBound.y - scroll.contentContainer.worldBound.y))).ExecuteLater(30);
        }

        private VisualElement GameCard(UnityMcpGame game)
        {
            var card = new VisualElement { name = "game-" + game.id };
            card.AddToClassList("abw-game");
            card.EnableInClassList("abw-game--selected", _studioGame == game.id);
            card.Add(LiveImage(() => UnityMcpWelcomeServices.GameImage(_context, game, false), "abw-game__backdrop", ScaleMode.ScaleAndCrop));
            var mask = new VisualElement { pickingMode = PickingMode.Ignore };
            mask.AddToClassList("abw-game__mask");
            mask.generateVisualContent += context => DrawMask(context, mask.contentRect, GAME_MASK);
            card.Add(mask);
            var intro = new VisualElement();
            intro.AddToClassList("abw-game__intro");
            intro.Add(LiveImage(() => UnityMcpWelcomeServices.GameImage(_context, game, true), "abw-game__logo", ScaleMode.ScaleToFit));
            var body = new VisualElement();
            body.AddToClassList("abw-game__body");
            body.Add(Text(game.tagline, "abw-game__tagline"));
            body.Add(Text(game.name, "abw-game__name"));
            body.Add(Text(game.summary, "abw-game__summary"));
            var links = new VisualElement();
            links.AddToClassList("abw-game__links");
            if (!string.IsNullOrEmpty(game.steamUrl)) links.Add(GameLink(game.steamLabel ?? "Visit on Steam", game.steamUrl, true));
            if (!string.IsNullOrEmpty(game.websiteUrl)) links.Add(GameLink("Official website", game.websiteUrl, string.IsNullOrEmpty(game.steamUrl)));
            body.Add(links);
            intro.Add(body);
            card.Add(intro);
            GameProducts(card, game);
            return card;
        }

        private static Button GameLink(string label, string url, bool primary)
        {
            var button = new Button(() => Application.OpenURL(url)) { text = label, tooltip = url };
            button.AddToClassList("abw-game-link");
            if (primary) button.AddToClassList("abw-game-link--primary");
            return button;
        }

        // Opacity of the dark veil at the 3 x 3 grid points, rows top to bottom, left to right:
        // USS 2022.3 has no gradient, so the veil is a mesh.
        private static readonly float[] GAME_MASK = { 0.94f, 0.70f, 0.30f, 0.95f, 0.80f, 0.55f, 0.98f, 0.98f, 0.96f };
        private static readonly float[] DEVLOG_MASK = { 0.93f, 0.76f, 0.30f, 0.95f, 0.80f, 0.36f, 0.97f, 0.86f, 0.52f };

        private static void DrawMask(MeshGenerationContext context, Rect rect, float[] opacity)
        {
            if (rect.width <= 0 || rect.height <= 0) return;
            MeshWriteData mesh = context.Allocate(9, 24);
            for (int y = 0; y < 3; y++)
                for (int x = 0; x < 3; x++)
                    mesh.SetNextVertex(new Vertex
                    {
                        position = new Vector3(rect.width * x / 2f, rect.height * y / 2f, Vertex.nearZ),
                        tint = new Color(0.035f, 0.045f, 0.055f, opacity[y * 3 + x])
                    });
            for (int y = 0; y < 2; y++)
                for (int x = 0; x < 2; x++)
                {
                    ushort i = (ushort)(y * 3 + x);
                    mesh.SetNextIndex(i); mesh.SetNextIndex((ushort)(i + 1)); mesh.SetNextIndex((ushort)(i + 3));
                    mesh.SetNextIndex((ushort)(i + 1)); mesh.SetNextIndex((ushort)(i + 4)); mesh.SetNextIndex((ushort)(i + 3));
                }
        }

        private void GameProducts(VisualElement host, UnityMcpGame game)
        {
            var section = new VisualElement();
            section.AddToClassList("abw-game__packages");
            var heading = new VisualElement();
            heading.AddToClassList("abw-game__carousel-heading");
            heading.Add(Text("Built with our packages", "abw-game__usage-title"));
            var position = Text("", "abw-game__position");
            heading.Add(position);
            var scroll = new ScrollView(ScrollViewMode.Horizontal) { name = "game-carousel-" + game.id };
            scroll.AddToClassList("abw-game-carousel");
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            scroll.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            var previous = new Button(() => scroll.scrollOffset = new Vector2(Mathf.Max(0, scroll.scrollOffset.x - 192 * Mathf.Max(1, Mathf.FloorToInt(scroll.contentViewport.layout.width / 192))), 0)) { text = "<", tooltip = "Previous packages", name = "game-previous-" + game.id };
            var next = new Button(() => scroll.scrollOffset = new Vector2(Mathf.Min(scroll.horizontalScroller.highValue, scroll.scrollOffset.x + 192 * Mathf.Max(1, Mathf.FloorToInt(scroll.contentViewport.layout.width / 192))), 0)) { text = ">", tooltip = "Next packages", name = "game-next-" + game.id };
            previous.AddToClassList("abw-game__carousel-arrow");
            next.AddToClassList("abw-game__carousel-arrow");
            heading.Add(previous);
            heading.Add(next);
            section.Add(heading);
            int count = 0;
            foreach (UnityMcpGameProduct use in game.products)
            {
                UnityMcpProduct product = _catalog.products.FirstOrDefault(p => p.id == use.id);
                if (!UnityMcpWelcomeServices.CanRecommend(product, RecommendationsOnly)) continue;
                var link = new Button(() => UnityMcpWelcomeServices.OpenProduct(product, _catalog)) { tooltip = product.storeName, name = "game-product-" + game.id + "-" + product.id };
                link.AddToClassList("abw-game-product");
                var picture = LiveImage(() => UnityMcpWelcomeServices.Card(_context, product), "abw-game-product__image", path: UnityMcpWelcomeServices.CachedCardPath(product), fallbackPath: _context.Media("Media/Cards/" + product.card));
                link.Add(picture);
                link.Add(Text(product.name, "abw-game-product__name"));
                link.Add(Text(use.role == "development" ? "Development tool" : "Used in " + game.name, "abw-game-product__role"));
                link.RegisterCallback<FocusInEvent>(_ => scroll.ScrollTo(link));
                scroll.Add(link);
                count++;
            }
            if (count == 0) return;
            Action update = () =>
            {
                float width = scroll.contentViewport.layout.width;
                int first = Mathf.Clamp(Mathf.FloorToInt(scroll.scrollOffset.x / 192) + 1, 1, count);
                int last = Mathf.Clamp(Mathf.FloorToInt((scroll.scrollOffset.x + width + 1) / 192), first, count);
                position.text = first + " - " + last + " / " + count;
                previous.style.visibility = scroll.scrollOffset.x > 1 ? Visibility.Visible : Visibility.Hidden;
                next.style.visibility = scroll.scrollOffset.x < scroll.horizontalScroller.highValue - 1 ? Visibility.Visible : Visibility.Hidden;
                _gameCarouselOffsets[game.id] = scroll.scrollOffset.x;
            };
            // The page keeps the vertical wheel: over the cards it scrolls Studio as it does
            // anywhere else. Only a horizontal gesture moves the carousel; arrows and focus do the rest.
            scroll.RegisterCallback<WheelEvent>(evt =>
            {
                ScrollView page = scroll.GetFirstAncestorOfType<ScrollView>();
                evt.StopPropagation();
                if (Mathf.Abs(evt.delta.x) > Mathf.Abs(evt.delta.y))
                    scroll.scrollOffset += new Vector2(evt.delta.x * scroll.mouseWheelScrollSize, 0);
                else if (page != null)
                    page.scrollOffset += new Vector2(0, evt.delta.y * page.mouseWheelScrollSize);
            }, TrickleDown.TrickleDown);
            float saved = _gameCarouselOffsets.TryGetValue(game.id, out float offset) ? offset : 0;
            scroll.horizontalScroller.valueChanged += _ => update();
            scroll.RegisterCallback<GeometryChangedEvent>(_ => update());
            scroll.schedule.Execute(() => { scroll.scrollOffset = new Vector2(saved, 0); update(); }).ExecuteLater(30);
            section.Add(scroll);
            host.Add(section);
        }

        /// <summary>The latest post as a banner over its own cover, veiled on the text side; a
        /// post without a cover keeps the banner on a plain dark ground. A stale post must not
        /// make a failed feed look online.</summary>
        private void BuildDevlog(VisualElement host)
        {
            if (!UnityMcpWelcomeServices.CatalogOnline) return;
            UnityMcpWelcomeServices.RefreshDevlog(_catalog.devlogFeed);
            UnityMcpDevlog post = UnityMcpWelcomeServices.LoadDevlog();
            if (post == null) return;
            Action open = () => Application.OpenURL(post.link);
            VisualElement card = Clickable(open, "abw-devlog");
            card.name = "devlog";
            card.tooltip = post.link;
            card.focusable = true;
            card.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (!IsSubmit(evt.keyCode)) return;
                open();
                evt.StopPropagation();
            });
            {
                card.Add(LiveImage(() => UnityMcpWelcomeServices.LoadImage(UnityMcpWelcomeServices.DevlogImage), "abw-devlog__backdrop", ScaleMode.ScaleAndCrop, availability: ready => card.EnableInClassList("abw-devlog--plain", !ready)));
                var mask = new VisualElement { pickingMode = PickingMode.Ignore };
                mask.AddToClassList("abw-devlog__mask");
                mask.generateVisualContent += context => DrawMask(context, mask.contentRect, DEVLOG_MASK);
                card.Add(mask);
            }

            var body = new VisualElement();
            body.AddToClassList("abw-devlog__body");
            body.Add(Text("DEVBLOG", "abw-devlog__kicker"));
            var rule = new VisualElement();
            rule.AddToClassList("abw-devlog__rule");
            body.Add(rule);
            string date = string.IsNullOrEmpty(post.date) ? "" : "  \u00b7  " + post.date.ToUpperInvariant();
            body.Add(Text("LATEST POST" + date, "abw-devlog__date"));
            body.Add(Text(post.title, "abw-devlog__title"));
            if (!string.IsNullOrEmpty(post.summary)) body.Add(Text(post.summary, "abw-devlog__summary"));
            var cta = new VisualElement();
            cta.AddToClassList("abw-devlog__cta");
            cta.Add(new Label("Read the article"));
            body.Add(cta);
            card.Add(body);
            host.Add(card);
        }

        private static VisualElement StudioCard(string title, string body, string link, string url)
        {
            var card = new VisualElement();
            card.AddToClassList("abw-studio-card");
            card.Add(Text(title, "abw-studio-card__title"));
            card.Add(Text(body, "abw-studio-card__body"));
            Label anchor = Text(link, "abw-link");
            anchor.AddManipulator(new Clickable(() => Application.OpenURL(url)));
            card.Add(anchor);
            return card;
        }
    }
}
