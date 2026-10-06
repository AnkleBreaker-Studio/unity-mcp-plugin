using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityMCP.Editor.Welcome
{
    internal sealed partial class UnityMcpWelcome
    {
        private sealed class Page
        {
            public VisualElement Body;
            public ScrollView Scroll;
            public ScrollView Panel;
            public string Filter;
            public Dictionary<ScrollView, Vector2> HiddenOffsets;
            public VisualElement Showcase;
            public bool ShowcaseReady;
        }

        private readonly Dictionary<string, Page> _pages = new Dictionary<string, Page>();
        private readonly Dictionary<string, Vector2> _pageOffsets = new Dictionary<string, Vector2>();
        private readonly Dictionary<string, VisualElement> _assetCards = new Dictionary<string, VisualElement>();
        private readonly Dictionary<string, Vector2> _filterOffsets = new Dictionary<string, Vector2>();
        private int _uiGeneration;
        private Queue<System.Action> _pageBuildQueue;
        private IVisualElementScheduledItem _pageBuildTick;
        private IVisualElementScheduledItem _showcaseTick;
        private Vector2 _showcaseOffset;
        private VisualElement _stagingBody;
        private HashSet<string> _assetFilterIds = new HashSet<string>();

        private static void SetCardVisible(VisualElement card, bool visible)
        {
            card.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            foreach (Image image in card.Query<Image>().ToList())
                if (image.userData is System.Action<bool> activate) activate(visible);
        }

        private void AddAssetCard(VisualElement host, UnityMcpProduct product, string pitch, string tag)
        {
            var card = AssetCard(product, pitch, tag);
            SetCardVisible(card, _assetFilterIds.Contains(product.id));
            host.Add(card);
        }

        private void ApplyAssetFilter(VisualElement host)
        {
            using var perf = new UnityMcpWelcomePerf.Scope("UI.ApplyAssetFilter");
            var filtered = Filtered(_filter, includeSelf: true).ToList();
            _assetFilterIds = new HashSet<string>(filtered.Select(p => p.id));
            foreach (var pair in _assetCards)
            {
                bool visible = _assetFilterIds.Contains(pair.Key);
                if ((pair.Value.style.display.value != DisplayStyle.None) != visible) SetCardVisible(pair.Value, visible);
            }
            int upcoming = filtered.Count(UnityMcpWelcomeServices.IsComingSoon);
            int available = filtered.Count - upcoming;
            foreach (bool soon in new[] { false, true })
            {
                var section = host.Q<VisualElement>(soon ? "assets-coming-soon" : "assets-available");
                if (section == null) continue;
                int count = soon ? upcoming : available;
                section.style.display = count == 0 ? DisplayStyle.None : DisplayStyle.Flex;
                section.Q<Label>(className: "abw-assets-section__title").text = (soon ? "Coming soon" : "Available now") + " (" + count + ")";
                section.EnableInClassList("abw-assets-section--separated", soon && available > 0);
            }
            var empty = host.Q<Label>("assets-empty");
            if (empty != null) empty.style.display = filtered.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            var filters = Filters();
            var chips = host.Query<VisualElement>(className: "abw-chip").ToList();
            for (int i = 0; i < chips.Count && i < filters.Count; i++) chips[i].EnableInClassList("abw-chip--on", filters[i].id == _filter);
        }

        private void CancelPageBuild()
        {
            _showcaseTick?.Pause();
            _showcaseTick = null;
            _pageBuildTick?.Pause();
            _pageBuildTick = null;
            _pageBuildQueue = null;
            _stagingBody?.RemoveFromHierarchy();
            _stagingBody = null;
        }

        private static void RestoreBodyLayout(VisualElement body)
        {
            body.style.position = StyleKeyword.Null;
            body.style.left = StyleKeyword.Null; body.style.top = StyleKeyword.Null;
            body.style.width = StyleKeyword.Null; body.style.height = StyleKeyword.Null;
        }

        private static void GuardStagingFocus(VisualElement element, List<VisualElement> focusable)
        {
            if (element.focusable) { focusable.Add(element); element.focusable = false; }
            for (int i = 0; i < element.hierarchy.childCount; i++) GuardStagingFocus(element.hierarchy[i], focusable);
        }

        private void PrepareShowcase(Page page)
        {
            if (page.Showcase == null || page.ShowcaseReady || _showcaseTick != null) return;
            var products = _catalog.products.Where(p => !IsSelf(p) && !p.pinned && TopCategory(p) == "Tools").ToArray();
            int index = 0;
            int generation = _uiGeneration;
            _showcaseTick = rootVisualElement.schedule.Execute(() =>
            {
                using var perf = new UnityMcpWelcomePerf.Scope("UI.PrepareShowcase");
                if (generation != _uiGeneration || this == null || _tab != "start" || page.Body.panel == null)
                {
                    _showcaseTick?.Pause(); _showcaseTick = null;
                    return;
                }
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                while (index < products.Length)
                {
                    UnityMcpWelcomeServices.IsInstalled(products[index++]);
                    if ((System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency >= 2) return;
                }
                _showcaseTick.Pause(); _showcaseTick = null;
                var frame = BuildPanel();
                int position = page.Body.IndexOf(page.Showcase);
                page.Showcase.RemoveFromHierarchy();
                page.Body.Insert(position, frame);
                page.Showcase = frame;
                page.ShowcaseReady = true;
                page.Panel = _panelScroll;
                RestoreOffset(page.Panel, _showcaseOffset);
            }).Every(1);
        }

        private void BeginPageBuild(bool reuseShell, System.Action initialFocus)
        {
            string target = _tab;
            var shell = rootVisualElement[0];
            var previous = shell.Q<VisualElement>(className: "abw-body");
            if (reuseShell) UpdateNavigation();
            else
            {
                shell[0].RemoveFromHierarchy(); shell.Insert(0, BuildHeader());
                shell[shell.childCount - 1].RemoveFromHierarchy(); shell.Add(BuildFooter());
            }
            shell.EnableInClassList("abw--light", !UnityEditor.EditorGUIUtility.isProSkin);
            var body = new VisualElement();
            body.AddToClassList("abw-body");
            body.style.position = Position.Absolute;
            body.style.left = previous.layout.x;
            body.style.top = previous.layout.y;
            body.style.width = previous.layout.width;
            body.style.height = previous.layout.height;
            body.style.opacity = 0;
            var focusable = new List<VisualElement>();
            var scroll = Scroll("abw-full", "abw-scroll--full");
            body.Add(scroll);
            _stagingBody = body;
            _pageBuildQueue = new Queue<System.Action>();
            if (target == "studio") BuildStudioTab(scroll.contentContainer);
            else BuildAssetsTab(scroll.contentContainer);
            GuardStagingFocus(body, focusable);
            shell.Insert(shell.IndexOf(previous), body);
            int generation = _uiGeneration;
            bool laidOut = false;
            _pageBuildTick = rootVisualElement.schedule.Execute(() =>
            {
                using var perf = new UnityMcpWelcomePerf.Scope("UI.PreparePage");
                if (generation != _uiGeneration || this == null || body.panel == null) { CancelPageBuild(); return; }
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                while (_pageBuildQueue.Count > 0)
                {
                    _pageBuildQueue.Dequeue()();
                    if ((System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency >= 2)
                    {
                        GuardStagingFocus(body, focusable);
                        return;
                    }
                }
                if (!laidOut)
                {
                    GuardStagingFocus(body, focusable);
                    laidOut = true;
                    return;
                }
                System.Action restoreFocus = PreserveFocus(rootVisualElement) ?? initialFocus;
                _pageBuildTick.Pause(); _pageBuildTick = null; _pageBuildQueue = null; _stagingBody = null;
                HidePage(previous);
                RestoreBodyLayout(body);
                body.style.opacity = StyleKeyword.Null;
                foreach (var element in focusable) element.focusable = true;
                _pages[target] = new Page { Body = body, Scroll = scroll, Filter = _filter };
                _scroll = scroll; _panelScroll = null;
                if (target == "studio") InvalidateStudio();
                var offsets = target == "assets" ? _filterOffsets : _pageOffsets;
                RestoreOffset(scroll, offsets.TryGetValue(target == "assets" ? _filter : target, out var offset) ? offset : Vector2.zero);
                if (restoreFocus != null) rootVisualElement.schedule.Execute(() => { if (generation == _uiGeneration) restoreFocus(); });
            }).Every(1);
        }

        private void HidePage(VisualElement body)
        {
            if (_pages.Values.Any(page => ReferenceEquals(page.Body, body))) SetPageActive(body, false);
            else body.RemoveFromHierarchy();
        }

        private void SetPageActive(VisualElement body, bool active)
        {
            using var perf = new UnityMcpWelcomePerf.Scope("UI.ActivatePage");
            var page = _pages.Values.FirstOrDefault(p => ReferenceEquals(p.Body, body));
            if (!active && page != null) page.HiddenOffsets = body.Query<ScrollView>().ToList().ToDictionary(s => s, s => s.scrollOffset);
            foreach (Image image in body.Query<Image>().ToList())
            {
                bool visible = active;
                for (var parent = image.parent; visible && parent != null && parent != body; parent = parent.parent)
                    visible = parent.style.display.value != DisplayStyle.None;
                if (image.userData is System.Action<bool> activate) activate(visible);
            }
            body.style.display = active ? DisplayStyle.Flex : DisplayStyle.None;
            body.EnableInClassList("abw-body", active);
            body.EnableInClassList("abw-cached-body", !active);
            if (active && page?.HiddenOffsets != null)
            {
                foreach (var pair in page.HiddenOffsets) RestoreOffset(pair.Key, pair.Value);
                page.HiddenOffsets = null;
            }
        }

        private void RenderNavigation(VisualElement root)
        {
            var shell = root[0];
            VisualElement previousBody = shell.Q<VisualElement>(className: "abw-body");
            VisualElement body = PageBody();
            UpdateNavigation();
            if (!ReferenceEquals(previousBody, body))
            {
                if (previousBody != null) HidePage(previousBody);
                if (body.parent == null) shell.Insert(shell.childCount - 1, body);
                SetPageActive(body, true);
            }
        }

        private void ForgetPanels()
        {
            foreach (var pair in _pages)
            {
                var page = pair.Value;
                var offset = page.HiddenOffsets != null && page.HiddenOffsets.TryGetValue(page.Scroll, out var saved) ? saved : page.Scroll.scrollOffset;
                _pageOffsets[pair.Key] = offset;
                if (pair.Key == "assets") _filterOffsets[page.Filter] = offset;
                if (pair.Key == "start" && page.Panel != null)
                    _showcaseOffset = page.HiddenOffsets != null && page.HiddenOffsets.TryGetValue(page.Panel, out var side) ? side : page.Panel.scrollOffset;
            }
            foreach (var page in _pages.Values)
                if (page.Body.ClassListContains("abw-cached-body")) page.Body.RemoveFromHierarchy();
            _pages.Clear();
            _assetCards.Clear();
        }

        private void InvalidateStudio()
        {
            if (!_pages.TryGetValue("studio", out var page)) return;
            using var perf = new UnityMcpWelcomePerf.Scope("UI.UpdateDevlog");
            var anchor = page.Body.Q<VisualElement>("devlog-anchor");
            if (anchor?.parent == null) return;
            bool active = page.Body.ClassListContains("abw-body");
            var offset = page.Scroll.scrollOffset;
            var restoreFocus = active ? PreserveFocus(rootVisualElement) : null;
            page.Body.Q<VisualElement>("devlog")?.RemoveFromHierarchy();
            var staging = new VisualElement();
            BuildDevlog(staging);
            if (staging.childCount > 0)
            {
                var card = staging[0];
                if (!active)
                    foreach (Image image in card.Query<Image>().ToList())
                        if (image.userData is System.Action<bool> activate) activate(false);
                anchor.parent.Insert(anchor.parent.IndexOf(anchor) + 1, card);
            }
            if (!active) return;
            RestoreOffset(page.Scroll, offset);
            if (restoreFocus != null) rootVisualElement.schedule.Execute(restoreFocus);
        }

        private void SelectFilter(string filter)
        {
            _filter = filter;
            Render(true);
        }

        private VisualElement AssetCard(UnityMcpProduct product, string pitch, string tag)
        {
            if (_assetCards.TryGetValue(product.id, out var card)) return card;
            card = Card(product, pitch, true, tag);
            _assetCards.Add(product.id, card);
            return card;
        }

        private VisualElement PageBody()
        {
            if (_pages.TryGetValue(_tab, out var cached))
            {
                if (_tab == "assets") UnityMcpWelcomePrompts.MarkCatalogSeen(_catalog, System.DateTime.UtcNow);
                _scroll = cached.Scroll;
                _panelScroll = cached.Panel;
                if (_tab == "assets" && cached.Filter != _filter)
                {
                    _filterOffsets[cached.Filter] = cached.HiddenOffsets != null && cached.HiddenOffsets.TryGetValue(_scroll, out var hidden) ? hidden : _scroll.scrollOffset;
                    ApplyAssetFilter(_scroll.contentContainer);
                    cached.Filter = _filter;
                    var offset = _filterOffsets.TryGetValue(_filter, out var savedFilter) ? savedFilter : Vector2.zero;
                    if (cached.HiddenOffsets != null) cached.HiddenOffsets[_scroll] = offset;
                    RestoreOffset(_scroll, offset);
                }
                if (_tab == "start") PrepareShowcase(cached);
                return cached.Body;
            }

            var body = new VisualElement();
            body.AddToClassList("abw-body");
            _panelScroll = null;
            VisualElement showcase = null;
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
                if (_context.Config.profile == "art") BuildArtStart(_scroll.contentContainer);
                else BuildToolStart(_scroll.contentContainer);
                body.Add(_scroll);
                if (UnityMcpWelcomeServices.CatalogOnline)
                {
                    showcase = new VisualElement();
                    showcase.AddToClassList("abw-panel");
                    body.Add(showcase);
                }
            }
            var page = new Page { Body = body, Scroll = _scroll, Panel = _panelScroll, Filter = _filter, Showcase = showcase };
            _pages[_tab] = page;
            if (_tab == "start") PrepareShowcase(page);
            if (_tab == "assets") RestoreOffset(_scroll, _filterOffsets.TryGetValue(_filter, out var offset) ? offset : Vector2.zero);
            else if (_pageOffsets.TryGetValue(_tab, out var saved)) RestoreOffset(_scroll, saved);
            return body;
        }

        private void RestoreOffset(ScrollView scroll, Vector2 offset)
        {
            int generation = _uiGeneration;
            scroll.schedule.Execute(() => { if (generation == _uiGeneration) scroll.scrollOffset = offset; });
        }
    }
}
