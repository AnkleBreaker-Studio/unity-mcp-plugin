using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Studio news notifications: polls the AnkleBreaker devlog RSS feed and tracks
    /// which posts this user has seen, so the toolbar can show a mobile-style unseen
    /// badge and the dashboard a news panel.
    ///
    /// Privacy: a plain GET of the public feed, at most every few hours, nothing sent.
    /// All state is user-scoped (global EditorPrefs, NOT per-project) — reading a post
    /// once marks it read everywhere. Disable entirely with <see cref="Enabled"/>.
    /// First run seeds every existing post as seen EXCEPT the newest, so a fresh
    /// install shows a gentle "1" instead of the whole backlog.
    /// </summary>
    [InitializeOnLoad]
    public static class MCPNewsService
    {
        public const string FeedUrl = "https://anklebreaker-studio.com/devlog/feed.xml";
        public const string DevlogUrl = "https://anklebreaker-studio.com/devlog";

        private const string KeyEnabled = "UnityMCP_news_Enabled";
        private const string KeySeenSlugs = "UnityMCP_news_SeenSlugs";
        private const string KeyNextCheckTicks = "UnityMCP_news_NextCheckTicks";
        private const string KeyCachedPosts = "UnityMCP_news_CachedPosts";

        private const double CheckIntervalHours = 6.0;
        private const int MaxSeenSlugs = 100;

        public sealed class Post
        {
            public string Slug;
            public string Title;
            public string Url;
            public string Category;
            public DateTime PubDateUtc;
        }

        /// <summary>Fired on the main thread whenever posts or unseen state change.</summary>
        public static event Action Changed;

        private static readonly List<Post> _posts = new List<Post>();
        private static HashSet<string> _seen;
        private static bool _inFlight;
        private static double _nextTickCheck;

        public static IReadOnlyList<Post> Posts => _posts;
        public static DateTime LastFetchUtc { get; private set; }
        public static string LastError { get; private set; }

        public static bool Enabled
        {
            get => EditorPrefs.GetBool(KeyEnabled, true);
            set
            {
                if (value == Enabled) return;
                EditorPrefs.SetBool(KeyEnabled, value);
                RaiseChanged();
            }
        }

        public static int UnseenCount
        {
            get
            {
                if (!Enabled) return 0;
                int count = 0;
                for (int i = 0; i < _posts.Count; i++)
                    if (!Seen.Contains(_posts[i].Slug))
                        count++;
                return count;
            }
        }

        static MCPNewsService()
        {
            LoadCache();
            EditorApplication.update += Tick;
        }

        public static bool IsUnseen(Post post) =>
            post != null && Enabled && !Seen.Contains(post.Slug);

        public static void MarkSeen(Post post)
        {
            if (post == null || Seen.Contains(post.Slug)) return;
            Seen.Add(post.Slug);
            SaveSeen();
            RaiseChanged();
        }

        public static void MarkAllSeen()
        {
            bool changed = false;
            foreach (var p in _posts)
                changed |= Seen.Add(p.Slug);
            if (!changed) return;
            SaveSeen();
            RaiseChanged();
        }

        /// <summary>Open a post in the browser and mark it read.</summary>
        public static void OpenPost(Post post)
        {
            if (post == null || string.IsNullOrEmpty(post.Url)) return;
            Application.OpenURL(post.Url);
            MarkSeen(post);
        }

        /// <summary>Fetch the feed now, regardless of the schedule.</summary>
        public static void ForceRefresh() => Fetch();

        // ─── Scheduling ───

        private static void Tick()
        {
            // The real work is hours apart — only look at the clock once a minute.
            if (EditorApplication.timeSinceStartup < _nextTickCheck) return;
            _nextTickCheck = EditorApplication.timeSinceStartup + 60.0;

            if (!Enabled || _inFlight) return;
            long due = ReadTicks(KeyNextCheckTicks);
            if (DateTime.UtcNow.Ticks < due) return;
            Fetch();
        }

        private static void Fetch()
        {
            if (_inFlight) return;
            _inFlight = true;

            UnityWebRequest req = UnityWebRequest.Get(FeedUrl);
            req.timeout = 15;
            req.SendWebRequest().completed += _ => OnFeedDone(req);
        }

        private static void OnFeedDone(UnityWebRequest req)
        {
            string xml = req.result == UnityWebRequest.Result.Success ? req.downloadHandler.text : null;
            string error = req.result == UnityWebRequest.Result.Success ? null : req.error;
            req.Dispose();
            _inFlight = false;

            // Success or failure, don't hammer the site — next attempt one interval away.
            WriteTicks(KeyNextCheckTicks, DateTime.UtcNow.AddHours(CheckIntervalHours).Ticks);

            if (string.IsNullOrEmpty(xml))
            {
                LastError = error ?? "Empty feed response";
                return;
            }

            List<Post> parsed = ParseFeed(xml);
            if (parsed.Count == 0)
            {
                LastError = "Feed contained no posts";
                return;
            }

            LastError = null;
            LastFetchUtc = DateTime.UtcNow;

            bool firstRun = !EditorPrefs.HasKey(KeySeenSlugs);

            _posts.Clear();
            _posts.AddRange(parsed);
            _posts.Sort(NewestFirst);

            if (firstRun)
            {
                // Everything that already exists is old news to a fresh install —
                // except the newest post, which greets the user with a single badge.
                for (int i = 1; i < _posts.Count; i++)
                    Seen.Add(_posts[i].Slug);
                SaveSeen();
            }

            SaveCache();
            RaiseChanged();
        }

        private static int NewestFirst(Post a, Post b) => b.PubDateUtc.CompareTo(a.PubDateUtc);

        // ─── Feed parsing (same tolerant string scanning the welcome window uses) ───

        private static List<Post> ParseFeed(string xml)
        {
            var posts = new List<Post>();
            int cursor = 0;
            while (true)
            {
                int start = xml.IndexOf("<item>", cursor, StringComparison.Ordinal);
                if (start < 0) break;
                int end = xml.IndexOf("</item>", start, StringComparison.Ordinal);
                if (end < 0) break;
                string item = xml.Substring(start + 6, end - start - 6);
                cursor = end + 7;

                string link = StripCData(Between(item, "<link>", "</link>"));
                string title = Decode(StripCData(Between(item, "<title>", "</title>")));
                if (string.IsNullOrEmpty(link) || string.IsNullOrEmpty(title)) continue;
                link = link.Trim();

                var post = new Post
                {
                    Title = title,
                    Url = link,
                    Slug = SlugOf(link),
                    Category = Decode(StripCData(Between(item, "<category>", "</category>"))) ?? "",
                };

                string pub = StripCData(Between(item, "<pubDate>", "</pubDate>"));
                DateTime when;
                post.PubDateUtc = !string.IsNullOrEmpty(pub) && DateTime.TryParse(
                        pub, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out when)
                    ? when
                    : DateTime.MinValue;

                posts.Add(post);
            }
            return posts;
        }

        private static string SlugOf(string url)
        {
            string trimmed = url.TrimEnd('/');
            int slash = trimmed.LastIndexOf('/');
            return slash >= 0 ? trimmed.Substring(slash + 1) : trimmed;
        }

        private static string Between(string source, string startTag, string endTag)
        {
            if (string.IsNullOrEmpty(source)) return null;
            int start = source.IndexOf(startTag, StringComparison.Ordinal);
            if (start < 0) return null;
            start += startTag.Length;
            int end = source.IndexOf(endTag, start, StringComparison.Ordinal);
            return end < 0 ? null : source.Substring(start, end - start);
        }

        private static string StripCData(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            value = value.Trim();
            const string open = "<![CDATA[";
            const string close = "]]>";
            if (value.StartsWith(open, StringComparison.Ordinal) && value.EndsWith(close, StringComparison.Ordinal))
                return value.Substring(open.Length, value.Length - open.Length - close.Length).Trim();
            return value;
        }

        private static string Decode(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Replace("&apos;", "'").Replace("&#39;", "'").Replace("&quot;", "\"")
                        .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&").Trim();
        }

        // ─── Persistence ───

        private static HashSet<string> Seen
        {
            get
            {
                if (_seen != null) return _seen;
                _seen = new HashSet<string>(StringComparer.Ordinal);
                string raw = EditorPrefs.GetString(KeySeenSlugs, "");
                foreach (var slug in raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    _seen.Add(slug);
                return _seen;
            }
        }

        private static void SaveSeen()
        {
            // Keep the set bounded: current feed slugs always survive the cap.
            var ordered = new List<string>();
            foreach (var p in _posts)
                if (Seen.Contains(p.Slug))
                    ordered.Add(p.Slug);
            foreach (var slug in Seen)
                if (!ordered.Contains(slug) && ordered.Count < MaxSeenSlugs)
                    ordered.Add(slug);
            EditorPrefs.SetString(KeySeenSlugs, string.Join(";", ordered));
        }

        private static void SaveCache()
        {
            var list = new List<object>();
            foreach (var p in _posts)
            {
                list.Add(new Dictionary<string, object>
                {
                    { "slug", p.Slug },
                    { "title", p.Title },
                    { "url", p.Url },
                    { "category", p.Category },
                    { "ticks", p.PubDateUtc.Ticks.ToString(CultureInfo.InvariantCulture) },
                });
            }
            EditorPrefs.SetString(KeyCachedPosts, MiniJson.Serialize(list));
        }

        private static void LoadCache()
        {
            string json = EditorPrefs.GetString(KeyCachedPosts, "");
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                if (!(MiniJson.Deserialize(json) is List<object> list)) return;
                _posts.Clear();
                foreach (var o in list)
                {
                    if (!(o is Dictionary<string, object> d)) continue;
                    var post = new Post
                    {
                        Slug = d.TryGetValue("slug", out var s) ? s as string : null,
                        Title = d.TryGetValue("title", out var t) ? t as string : null,
                        Url = d.TryGetValue("url", out var u) ? u as string : null,
                        Category = d.TryGetValue("category", out var c) ? c as string ?? "" : "",
                    };
                    long ticks;
                    post.PubDateUtc = d.TryGetValue("ticks", out var k) && long.TryParse(k as string, out ticks)
                        ? new DateTime(ticks, DateTimeKind.Utc)
                        : DateTime.MinValue;
                    if (!string.IsNullOrEmpty(post.Slug) && !string.IsNullOrEmpty(post.Title))
                        _posts.Add(post);
                }
                _posts.Sort(NewestFirst);
            }
            catch
            {
                _posts.Clear();
            }
        }

        private static long ReadTicks(string key)
        {
            long ticks;
            return long.TryParse(EditorPrefs.GetString(key, "0"), out ticks) ? ticks : 0L;
        }

        private static void WriteTicks(string key, long value) =>
            EditorPrefs.SetString(key, value.ToString(CultureInfo.InvariantCulture));

        private static void RaiseChanged()
        {
            try { Changed?.Invoke(); }
            catch (Exception ex) { Debug.LogWarning($"[AB-UMCP] News listener threw: {ex.Message}"); }
        }
    }
}
