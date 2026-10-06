using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityMCP.Editor.Welcome
{
    /// <summary>
    /// The two prompts a package may raise on its own: a review request, and a nudge towards the
    /// Assets tab when the catalogue really has something new. One slot for both, shared by every
    /// AnkleBreaker package on the machine.
    ///
    /// <para>Every package carries its own copy of this code, under its own names. They agree only
    /// through the keys below, which are a contract: renaming one breaks the coordination with every
    /// package already sold. Never open a URL from here (Asset Store guideline 2.5.1.d): the store
    /// opens from a click, in this popup or in the Welcome.</para>
    /// </summary>
    [InitializeOnLoad]
    internal static class UnityMcpWelcomePrompts
    {
        // -- Frozen contract ------------------------------------------------
        public const string LAST_SHOWN = "AnkleBreaker.Prompt.LastShown";
        public const string DISCOVER_SEEN = "AnkleBreaker.Discover.LastSeen";
        public const string DISCOVER_OFFERS = "AnkleBreaker.Discover.Offers";
        private const string REVIEW = "AnkleBreaker.Review.";
        private const string SESSION_DONE = "AnkleBreaker.Prompt.SessionDone";
        private const string SESSION_CANDIDATES = "AnkleBreaker.Prompt.Candidates";
        public const string SESSION_AUTO_OPENED = "AnkleBreaker.Welcome.AutoOpenedThisSession";

        public const int REVIEW_AFTER_DAYS = 3;
        public const int GLOBAL_GAP_DAYS = 14;
        public const int REVIEW_LATER_DAYS = 30;
        public const int REVIEW_MAX_ASKS = 2;

        private const double SESSION_WARMUP_S = 600;
        private const double TICK_S = 5;
        private const double ELECTION_S = 12;
        private const double CATALOG_WAIT_S = 20;
        private const double CARD_WAIT_S = 10;

        private static double s_nextTick;
        private static double s_registeredAt = -1;
        private static double s_waitStarted = -1;
        private static UnityMcpWelcomeContext s_discoverContext;
        private static Offer s_offer;
        private static bool s_consumer;

        static UnityMcpWelcomePrompts()
        {
            if (Application.isBatchMode || SessionState.GetBool(SESSION_DONE, false)) return;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        // -- Machine-wide state of one package ------------------------------

        private static string ReviewKey(string id, string name) => REVIEW + id + "." + name;

        public static DateTime? GetDate(string key)
        {
            string raw = EditorPrefs.GetString(key, "");
            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks)
                ? new DateTime(ticks, DateTimeKind.Utc) : (DateTime?)null;
        }

        public static void SetDate(string key, DateTime when) =>
            EditorPrefs.SetString(key, when.Ticks.ToString(CultureInfo.InvariantCulture));

        /// <summary>First use of the package on this machine. Seeded from the project's own first
        /// open, so an install older than this code does not restart its clock.</summary>
        public static DateTime FirstSeen(UnityMcpWelcomeContext c, DateTime now)
        {
            string key = ReviewKey(c.Config.id, "FirstSeen");
            DateTime? seen = GetDate(key);
            if (seen != null) return seen.Value;
            DateTime first = UnityMcpWelcomeServices.GetDate(c, "FirstOpen") ?? now;
            SetDate(key, first);
            return first;
        }

        /// <summary>A review is asked once per buyer, not per project: "done" lives on the machine.
        /// The per-project flag of earlier revisions still counts.</summary>
        public static bool ReviewDone(UnityMcpWelcomeContext c) =>
            EditorPrefs.GetBool(ReviewKey(c.Config.id, "Done"), false) || UnityMcpWelcomeServices.GetFlag(c, "ReviewDone");

        /// <summary>Rate and "I already rated it": nothing can tell whether the buyer actually wrote
        /// one, so the click is taken as the review.</summary>
        public static void MarkReviewDone(UnityMcpWelcomeContext c) => EditorPrefs.SetBool(ReviewKey(c.Config.id, "Done"), true);

        public static void MarkReviewLater(UnityMcpWelcomeContext c, DateTime now) => SetDate(ReviewKey(c.Config.id, "Later"), now);

        public static void ClearState(UnityMcpWelcomeContext c)
        {
            foreach (string name in new[] { "FirstSeen", "Done", "Later", "Asked" })
                EditorPrefs.DeleteKey(ReviewKey(c.Config.id, name));
        }

        /// <summary>The store page the review goes to, or null when the package has none yet.</summary>
        public static string ReviewUrl(UnityMcpWelcomeContext c, UnityMcpCatalog catalog)
        {
            string configured = c.Config.usage.reviewUrl;
            if (!string.IsNullOrEmpty(configured) && configured != "store") return configured;
            UnityMcpProduct self = catalog?.products.FirstOrDefault(p => p.id == c.Config.id);
            if (self == null || string.IsNullOrEmpty(self.url) || UnityMcpWelcomeServices.IsComingSoon(self)) return null;
            return self.url + "#reviews";
        }

        /// <summary>Whether the buyer has used the package enough to judge it. A tool counts its
        /// finished setup or what was made with it; an art pack has only time to go on.</summary>
        public static bool HasBeenUsed(UnityMcpWelcomeContext c)
        {
            UnityMcpWelcomeData config = c.Config;
            if (config.profile == "art") return true;
            bool setupDone = config.steps.All(s => UnityMcpWelcomeServices.GetDate(c, "Step." + s.id) != null);
            bool made = !string.IsNullOrEmpty(config.usage.filter) && UnityMcpWelcomeServices.UsageCount(c) >= config.usage.minCount;
            return setupDone || made;
        }

        /// <summary>The card inside the Welcome: same clock, no cap on asks, since the buyer opened
        /// the window himself.</summary>
        public static bool ReviewCardDue(UnityMcpWelcomeContext c, UnityMcpCatalog catalog, DateTime now) =>
            ReviewUrl(c, catalog) != null && !ReviewDone(c) &&
            IsReviewTimeDue(FirstSeen(c, now), GetDate(ReviewKey(c.Config.id, "Later")), int.MaxValue, 0, now) &&
            HasBeenUsed(c);

        /// <summary>The popup: the card's rule, plus the per-package cap.</summary>
        public static bool ReviewPopupDue(UnityMcpWelcomeContext c, UnityMcpCatalog catalog, DateTime now) =>
            ReviewUrl(c, catalog) != null && !ReviewDone(c) &&
            IsReviewTimeDue(FirstSeen(c, now), GetDate(ReviewKey(c.Config.id, "Later")), REVIEW_MAX_ASKS,
                EditorPrefs.GetInt(ReviewKey(c.Config.id, "Asked"), 0), now) &&
            HasBeenUsed(c);

        /// <summary>Pure rule, kept apart for the lab checks.</summary>
        public static bool IsReviewTimeDue(DateTime firstSeen, DateTime? later, int maxAsks, int asked, DateTime now) =>
            asked < maxAsks &&
            (now - firstSeen).TotalDays >= REVIEW_AFTER_DAYS &&
            (later == null || (now - later.Value).TotalDays >= REVIEW_LATER_DAYS);

        public static bool IsGlobalSlotFree(DateTime? lastShown, DateTime now) =>
            lastShown == null || (now - lastShown.Value).TotalDays >= GLOBAL_GAP_DAYS;

        // -- What is new in the catalogue -----------------------------------

        internal sealed class Offer
        {
            public List<UnityMcpProduct> Sales = new List<UnityMcpProduct>();
            public List<UnityMcpProduct> Releases = new List<UnityMcpProduct>();
            public bool Any => Sales.Count > 0 || Releases.Count > 0;
            public UnityMcpProduct Featured => Sales.FirstOrDefault() ?? Releases.FirstOrDefault();
        }

        private static string OfferKey(UnityMcpProduct p) => p.id + ":" + p.discount.ToString(CultureInfo.InvariantCulture);

        /// <summary>Only what the buyer has not been shown: a release after his last look at the
        /// catalogue, or a sale he has not seen at that percentage. Never what he owns, never a
        /// product that cannot be bought yet.</summary>
        public static Offer FindOffer(IEnumerable<UnityMcpProduct> products, string selfId, Func<UnityMcpProduct, bool> installed,
            DateTime lastSeen, ICollection<string> seenOffers)
        {
            var offer = new Offer();
            foreach (UnityMcpProduct p in products)
            {
                if (p == null || p.id == selfId || p.pinned || UnityMcpWelcomeServices.IsComingSoon(p) || installed(p)) continue;
                if (p.discount > 0 && !seenOffers.Contains(OfferKey(p))) offer.Sales.Add(p);
                else if (ReleaseDate(p) > lastSeen) offer.Releases.Add(p);
            }
            offer.Sales = offer.Sales.OrderByDescending(p => p.discount).ThenByDescending(ReleaseDate).ToList();
            offer.Releases = offer.Releases.OrderByDescending(ReleaseDate).ToList();
            return offer;
        }

        private static DateTime ReleaseDate(UnityMcpProduct p) =>
            DateTime.TryParse(p.releasedAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out DateTime d) ? d : DateTime.MinValue;

        private static HashSet<string> SeenOffers() =>
            new HashSet<string>(EditorPrefs.GetString(DISCOVER_OFFERS, "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));

        /// <summary>First look ever: releases before today are not news to this buyer.</summary>
        private static DateTime DiscoverBaseline(DateTime now)
        {
            DateTime? seen = GetDate(DISCOVER_SEEN);
            if (seen != null) return seen.Value;
            SetDate(DISCOVER_SEEN, now);
            return now;
        }

        /// <summary>Called by the Assets tab and by the discovery popup: what the buyer has seen is
        /// no longer news. Sales that ended drop out of the list.</summary>
        public static void MarkCatalogSeen(UnityMcpCatalog catalog, DateTime now)
        {
            if (catalog?.products == null) return;
            SetDate(DISCOVER_SEEN, now);
            EditorPrefs.SetString(DISCOVER_OFFERS, string.Join(";", catalog.products.Where(p => p.discount > 0).Select(OfferKey)));
        }

        // -- Election -------------------------------------------------------

        /// <summary>Every package that runs this code at the same editor start writes one line;
        /// all of them read the same list and agree on one winner. A review due beats discovery,
        /// the oldest install first; otherwise the first id in order raises discovery.</summary>
        public static string PickWinner(string candidates)
        {
            var rows = (candidates ?? "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('|'))
                .Where(parts => parts.Length >= 3)
                .Select(parts => (id: parts[0], review: parts[1] == "1",
                    first: long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long t) ? t : long.MaxValue))
                .ToList();
            if (rows.Count == 0) return null;
            var review = rows.Where(r => r.review).OrderBy(r => r.first).ThenBy(r => r.id, StringComparer.Ordinal).FirstOrDefault();
            return review.id ?? rows.OrderBy(r => r.id, StringComparer.Ordinal).First().id;
        }

        private static void Register(string id, bool review, DateTime firstSeen)
        {
            string list = SessionState.GetString(SESSION_CANDIDATES, "");
            if (list.Split('\n').Any(line => line.StartsWith(id + "|", StringComparison.Ordinal))) return;
            SessionState.SetString(SESSION_CANDIDATES, list + id + "|" + (review ? "1" : "0") + "|" +
                firstSeen.Ticks.ToString(CultureInfo.InvariantCulture) + "\n");
        }

        // -- Scheduler ------------------------------------------------------

        private static void Stop(bool sessionDone)
        {
            EditorApplication.update -= Tick;
            if (s_consumer) { s_consumer = false; UnityMcpWelcomeServices.ReleaseConsumer(); }
            if (sessionDone) SessionState.SetBool(SESSION_DONE, true);
        }

        /// <summary>Never at launch, never over work: ten minutes into the session, editor focused,
        /// idle, and not in a session a Welcome already opened itself in.</summary>
        private static void Tick()
        {
            double t = EditorApplication.timeSinceStartup;
            if (t < s_nextTick) return;
            s_nextTick = t + (s_waitStarted >= 0 ? 0.5 : TICK_S);
            if (SessionState.GetBool(SESSION_DONE, false)) { Stop(false); return; }
            if (t < SESSION_WARMUP_S) return;
            if (!InternalEditorUtility.isApplicationActive || EditorApplication.isCompiling ||
                EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (SessionState.GetBool(SESSION_AUTO_OPENED, false)) { Stop(true); return; }

            DateTime now = DateTime.UtcNow;
            if (!IsGlobalSlotFree(GetDate(LAST_SHOWN), now)) { Stop(true); return; }

            if (s_waitStarted >= 0) { WaitForOffer(t, now); return; }

            UnityMcpWelcomeContext context = UnityMcpWelcomeServices.LoadContexts().FirstOrDefault();
            if (context == null) { Stop(false); return; }
            UnityMcpCatalog catalog = UnityMcpWelcomeServices.LoadCatalog(context);
            bool review = ReviewPopupDue(context, catalog, now);

            if (s_registeredAt < 0)
            {
                Register(context.Config.id, review, FirstSeen(context, now));
                s_registeredAt = t;
                return;
            }
            if (t - s_registeredAt < ELECTION_S) return;

            if (PickWinner(SessionState.GetString(SESSION_CANDIDATES, "")) != context.Config.id) { Stop(false); return; }

            if (review)
            {
                Claim(now);
                EditorPrefs.SetInt(ReviewKey(context.Config.id, "Asked"), EditorPrefs.GetInt(ReviewKey(context.Config.id, "Asked"), 0) + 1);
                UnityMcpWelcomePromptWindow.ShowReview(context, catalog);
                return;
            }

            // Discovery needs today's catalogue: prices and sales from an embedded copy are stale.
            s_discoverContext = context;
            if (!s_consumer) { s_consumer = true; UnityMcpWelcomeServices.AcquireConsumer(); }
            s_waitStarted = t;
            UnityMcpWelcomeServices.RefreshCatalog(catalog, true);
        }

        private static void WaitForOffer(double t, DateTime now)
        {
            UnityMcpWelcomeContext context = s_discoverContext;
            if (s_offer == null)
            {
                if (!UnityMcpWelcomeServices.CatalogOnline)
                {
                    if (t - s_waitStarted > CATALOG_WAIT_S) Stop(true);
                    return;
                }
                UnityMcpCatalog catalog = UnityMcpWelcomeServices.LoadCatalog(context);
                s_offer = FindOffer(catalog.products, context.Config.id, UnityMcpWelcomeServices.IsInstalled, DiscoverBaseline(now), SeenOffers());
                if (!s_offer.Any) { Stop(true); return; }
                UnityMcpWelcomeServices.QueueCards(new UnityMcpCatalog { products = new[] { s_offer.Featured } });
                s_waitStarted = t;
                return;
            }
            // A text-only popup sells less than the Card: give the download a few seconds.
            bool cardReady = UnityMcpWelcomeServices.Card(context, s_offer.Featured) != null;
            if (!cardReady && t - s_waitStarted < CARD_WAIT_S) return;
            UnityMcpWelcomePromptWindow.ShowDiscover(context, UnityMcpWelcomeServices.LoadCatalog(context), s_offer);
            Claim(now);
        }

        private static void Claim(DateTime now)
        {
            SetDate(LAST_SHOWN, now);
            Stop(true);
        }

        // -- Copy -----------------------------------------------------------

        public static string OfferTitle(Offer offer)
        {
            UnityMcpProduct sale = offer.Sales.FirstOrDefault();
            if (sale != null && offer.Releases.Count > 0) return sale.name + " is " + sale.discount + "% off, plus new releases";
            if (sale != null && offer.Sales.Count > 1) return sale.name + " is " + sale.discount + "% off, and " + (offer.Sales.Count - 1) + " more on sale";
            if (sale != null) return sale.name + " is " + sale.discount + "% off right now";
            if (offer.Releases.Count == 1) return "New from AnkleBreaker: " + offer.Releases[0].name;
            return offer.Releases.Count + " new assets since you last looked";
        }

        public static string OfferBody(Offer offer, UnityMcpWelcomeContext c)
        {
            string what = c.Config.profile == "art" ? "our assets" : "our tools";
            if (offer.Sales.Count > 0 && offer.Releases.Count > 0)
                return "Looks like " + what + " earned a place in your project. Sales and new releases are waiting in the catalogue.";
            if (offer.Sales.Count > 0)
                return "Looks like " + what + " earned a place in your project. Sales do not last: have a look while it is on.";
            return "Looks like " + what + " earned a place in your project. Here is what came out since.";
        }
    }

    /// <summary>The small window both prompts use. Closing it is "Later".</summary>
    internal sealed class UnityMcpWelcomePromptWindow : EditorWindow
    {
        private static readonly Vector2 SIZE_REVIEW = new Vector2(460f, 196f);
        private static readonly Vector2 SIZE_DISCOVER = new Vector2(460f, 462f);
        private static readonly Vector2 SIZE_DISCOVER_TEXT = new Vector2(460f, 176f);

        private UnityMcpWelcomeContext _context;
        private UnityMcpCatalog _catalog;
        private UnityMcpWelcomePrompts.Offer _offer;
        private bool _answered;

        public static void ShowReview(UnityMcpWelcomeContext context, UnityMcpCatalog catalog) =>
            Present(context, catalog, null, SIZE_REVIEW, "Your opinion on " + context.Config.name);

        public static void ShowDiscover(UnityMcpWelcomeContext context, UnityMcpCatalog catalog, UnityMcpWelcomePrompts.Offer offer)
        {
            // Told is seen: the same sale is not news in fourteen days.
            UnityMcpWelcomePrompts.MarkCatalogSeen(catalog, DateTime.UtcNow);
            // The Card may still be downloading when the wait runs out: no empty frame for it.
            bool card = UnityMcpWelcomeServices.Card(context, offer.Featured) != null;
            Present(context, catalog, offer, card ? SIZE_DISCOVER : SIZE_DISCOVER_TEXT, "New from AnkleBreaker");
        }

        private static void Present(UnityMcpWelcomeContext context, UnityMcpCatalog catalog, UnityMcpWelcomePrompts.Offer offer, Vector2 size, string title)
        {
            var window = CreateInstance<UnityMcpWelcomePromptWindow>();
            window._context = context;
            window._catalog = catalog;
            window._offer = offer;
            window.titleContent = new GUIContent(title);
            Rect main = EditorGUIUtility.GetMainWindowPosition();
            window.position = new Rect(main.x + (main.width - size.x) * 0.5f, main.y + (main.height - size.y) * 0.4f, size.x, size.y);
            window.minSize = window.maxSize = size;
            window.ShowUtility();
            window.Build();
        }

        /// <summary>Not restored with the layout: a prompt belongs to the session that raised it.</summary>
        private void OnEnable()
        {
            UnityMcpWelcomeServices.AcquireConsumer();
            if (_context == null) EditorApplication.delayCall += () => { if (this != null && _context == null) Close(); };
        }

        private void OnDisable()
        {
            rootVisualElement.Clear();
            UnityMcpWelcomeServices.ReleaseConsumer();
        }

        private void OnDestroy()
        {
            if (!_answered && _offer == null && _context != null) UnityMcpWelcomePrompts.MarkReviewLater(_context, DateTime.UtcNow);
        }

        private void Build()
        {
            VisualElement root = rootVisualElement;
            root.Clear();
            var shell = new VisualElement();
            StyleSheet style = LoadStyle();
            if (style != null) shell.styleSheets.Add(style);
            shell.AddToClassList("abw");
            shell.AddToClassList("abw-prompt");
            shell.EnableInClassList("abw--light", !EditorGUIUtility.isProSkin);
            root.Add(shell);
            if (_offer == null) BuildReview(shell);
            else BuildDiscover(shell);
        }

        private void BuildReview(VisualElement shell)
        {
            UnityMcpWelcomeData config = _context.Config;
            var head = new VisualElement();
            head.AddToClassList("abw-prompt__head");
            head.Add(UnityMcpWelcome.LiveImage(() => UnityMcpWelcomeServices.LoadImage(_context.Media(config.icon)), "abw-prompt__icon", path: _context.Media(config.icon)));
            var text = new VisualElement();
            text.AddToClassList("abw-prompt__text");
            string made = config.usage.phrase;
            int count = UnityMcpWelcomeServices.UsageCount(_context);
            string lead = string.IsNullOrEmpty(made) || count < config.usage.minCount
                ? "Has " + config.name + " earned its place in your project?"
                : made.Replace("{n}", count.ToString(CultureInfo.InvariantCulture)) + " Would you say so?";
            text.Add(MakeLabel(lead, "abw-prompt__title"));
            text.Add(MakeLabel("A review is how the next developer finds it. If something is wrong instead, Discord gets you a human faster than one star does.", "abw-prompt__body"));
            head.Add(text);
            shell.Add(head);

            // The answer we hope for is the big one, bottom right, where the eye ends; the way
            // out sits beside it, smaller; the escape for those who already rated is plain text.
            VisualElement actions = Actions(shell);
            Label rated = MakeLabel("I already rated it", "abw-prompt__aside");
            rated.AddManipulator(new Clickable(() =>
            {
                UnityMcpWelcomePrompts.MarkReviewDone(_context);
                Answer();
            }));
            actions.Add(rated);
            actions.Add(Fill());
            actions.Add(MakeButton("Later", false, () =>
            {
                UnityMcpWelcomePrompts.MarkReviewLater(_context, DateTime.UtcNow);
                Answer();
            }));
            actions.Add(MakeButton("Rate " + config.name, true, () =>
            {
                UnityMcpWelcomePrompts.MarkReviewDone(_context);
                string url = UnityMcpWelcomePrompts.ReviewUrl(_context, _catalog);
                if (url != null) Application.OpenURL(url);
                Answer();
            }));
        }

        private void BuildDiscover(VisualElement shell)
        {
            UnityMcpProduct featured = _offer.Featured;
            if (position.height >= SIZE_DISCOVER.y)
                shell.Add(UnityMcpWelcome.LiveImage(() => UnityMcpWelcomeServices.Card(_context, featured), "abw-prompt__card", path: UnityMcpWelcomeServices.CachedCardPath(featured), fallbackPath: _context.Media("Media/Cards/" + featured.card)));
            var text = new VisualElement();
            text.AddToClassList("abw-prompt__text");
            text.Add(MakeLabel("ENJOYING " + (_context.Config.profile == "art" ? "OUR ASSETS" : "OUR TOOLS") + "?", "abw-prompt__eyebrow"));
            text.Add(MakeLabel(UnityMcpWelcomePrompts.OfferTitle(_offer), "abw-prompt__title"));
            text.Add(MakeLabel(UnityMcpWelcomePrompts.OfferBody(_offer, _context), "abw-prompt__body"));
            shell.Add(text);

            VisualElement actions = Actions(shell);
            string filter = _offer.Sales.Count > 0 ? "sale" : "all";
            actions.Add(Fill());
            actions.Add(MakeButton("Not now", false, Answer));
            actions.Add(MakeButton("Show me", true, () =>
            {
                Answer();
                UnityMcpWelcome.OpenAssets(_context.Guid, filter);
            }));
        }

        private void Answer()
        {
            _answered = true;
            Close();
        }

        private static VisualElement Actions(VisualElement shell)
        {
            var actions = new VisualElement();
            actions.AddToClassList("abw-prompt__actions");
            shell.Add(actions);
            return actions;
        }

        private static VisualElement MakeButton(string label, bool accent, Action onClick)
        {
            var button = new VisualElement();
            button.AddToClassList(accent ? "abw-prompt__primary" : "abw-prompt__secondary");
            button.AddManipulator(new Clickable(onClick));
            button.Add(new Label(label));
            return button;
        }

        private static Label MakeLabel(string text, string cssClass)
        {
            var label = new Label(text);
            label.AddToClassList(cssClass);
            return label;
        }

        private static VisualElement Fill()
        {
            var fill = new VisualElement();
            fill.AddToClassList("abw-fill");
            return fill;
        }

        private static StyleSheet LoadStyle()
        {
            foreach (string guid in AssetDatabase.FindAssets("UnityMcpWelcome t:StyleSheet"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) != "UnityMcpWelcome") continue;
                var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
                if (sheet != null) return sheet;
            }
            return null;
        }
    }
}
