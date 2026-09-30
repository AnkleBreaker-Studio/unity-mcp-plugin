using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace UnityMCP.Editor.Welcome
{
    /// <summary>A loaded config and the two folders every path in it is resolved against.</summary>
    internal sealed class UnityMcpWelcomeContext
    {
        public UnityMcpWelcomeData Config;

        /// <summary>Asset path of the folder holding the config (and its Media/).</summary>
        public string Dir;

        /// <summary>Asset path of the package root.</summary>
        public string Root;
        public string Guid;

        public string Resolve(string relative) => UnityMcpWelcomeServices.Combine(Root, relative);
        public string Media(string relative) => UnityMcpWelcomeServices.Combine(Dir, relative);
    }

    /// <summary>
    /// What the window needs from the editor: its configs, per-project state, what is installed,
    /// the catalogue, images, and the buttons' effects. No UI here.
    /// </summary>
    internal static class UnityMcpWelcomeServices
    {
        public const string CONFIG_NAME = "MCPForUnity.welcome";
        public const string PREFS = "AB.MCPForUnity.CanonicalWelcome";

        private const string CATALOG_URL = "https://raw.githubusercontent.com/AnkleBreaker-Studio/welcome-catalogue/main/welcome-catalog.json";

        /// <summary>Machine-wide override of the catalogue URL, for testing a catalogue before it
        /// is published (a <c>file:///</c> URL works).</summary>
        public const string CATALOG_URL_PREF = "AnkleBreaker.Welcome.CatalogUrl";

        /// <summary>Shared by every AnkleBreaker window in the project: one fetch serves them all.</summary>
        private const string CACHE_DIR = "Library/AnkleBreakerWelcome";
        private const int CATALOG_TIMEOUT_S = 2;
        private const int CARD_TIMEOUT_S = 4;

        public static event Action CatalogChanged;

        // -- Configs --------------------------------------------------------

        public static List<UnityMcpWelcomeContext> LoadContexts()
        {
            
            var found = new List<UnityMcpWelcomeContext>();
            string query = CONFIG_NAME + " t:TextAsset";
            foreach (string guid in AssetDatabase.FindAssets(query))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string file = Path.GetFileNameWithoutExtension(path);
                if (file != CONFIG_NAME)
                    continue;

                UnityMcpWelcomeContext context = Load(path, guid);
                if (context == null) continue;
                found.Add(context);
            }
            return found;
        }

        private static UnityMcpWelcomeContext Load(string path, string guid)
        {
            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            if (asset == null) return null;
            UnityMcpWelcomeData config;
            try { config = JsonUtility.FromJson<UnityMcpWelcomeData>(asset.text); }
            catch (ArgumentException) { return null; }
            if (config == null || string.IsNullOrEmpty(config.name)) return null;

            string dir = Path.GetDirectoryName(path)?.Replace('\\', '/');
            return new UnityMcpWelcomeContext
            {
                Config = config,
                Dir = dir,
                Root = Combine(dir, config.root),
                Guid = guid,
            };
        }

        /// <summary>Joins asset paths and folds <c>..</c>, which AssetDatabase does not.</summary>
        public static string Combine(string baseDir, string relative)
        {
            if (string.IsNullOrEmpty(relative)) return baseDir;
            var parts = new List<string>((baseDir ?? "").Split('/'));
            foreach (string segment in relative.Replace('\\', '/').Split('/'))
            {
                if (segment == "" || segment == ".") continue;
                if (segment == ".." && parts.Count > 1) parts.RemoveAt(parts.Count - 1);
                else parts.Add(segment);
            }
            return string.Join("/", parts.Where(p => p != ""));
        }

        // -- Per-project state ----------------------------------------------

        /// <summary>
        /// EditorPrefs is machine-wide, so every key carries the project's productGUID: a bare key
        /// would let the first project a buyer imports into consume the first-run state of every
        /// project he opens afterwards.
        /// </summary>
        private static string Key(UnityMcpWelcomeContext context, string name)
        {
            Guid product = PlayerSettings.productGUID;
            string project = product != System.Guid.Empty ? product.ToString("N") : Application.dataPath.GetHashCode().ToString("x8");
            return PREFS + "." + project + "." + context.Config.id + "." + name;
        }

        public static bool GetFlag(UnityMcpWelcomeContext c, string name) => EditorPrefs.GetBool(Key(c, name), false);
        public static void SetFlag(UnityMcpWelcomeContext c, string name, bool value) => EditorPrefs.SetBool(Key(c, name), value);
        public static int GetInt(UnityMcpWelcomeContext c, string name) => EditorPrefs.GetInt(Key(c, name), 0);
        public static void SetInt(UnityMcpWelcomeContext c, string name, int value) => EditorPrefs.SetInt(Key(c, name), value);

        public static int WelcomeRevision(UnityMcpWelcomeContext c) => Math.Max(1, c.Config.welcomeRevision);

        public static bool ShouldAutoOpen(UnityMcpWelcomeContext c)
        {
            if (!GetFlag(c, "Seen")) return true;
            string key = Key(c, "LastSeenWelcomeVersion");
            // Existing Seen-only installs belong to revision 1, even if they skip straight to revision 2+.
            if (!EditorPrefs.HasKey(key)) EditorPrefs.SetInt(key, 1);
            return WelcomeRevision(c) > EditorPrefs.GetInt(key, 1);
        }

        public static void MarkWelcomeSeen(UnityMcpWelcomeContext c)
        {
            SetFlag(c, "Seen", true);
            // Keep the highest seen revision so a downgrade cannot trigger another update prompt.
            SetInt(c, "LastSeenWelcomeVersion", Math.Max(WelcomeRevision(c), GetInt(c, "LastSeenWelcomeVersion")));
        }

        public static DateTime? GetDate(UnityMcpWelcomeContext c, string name)
        {
            string raw = EditorPrefs.GetString(Key(c, name), "");
            return long.TryParse(raw, out long ticks) ? new DateTime(ticks, DateTimeKind.Utc) : (DateTime?)null;
        }

        public static void SetDate(UnityMcpWelcomeContext c, string name) =>
            EditorPrefs.SetString(Key(c, name), DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));

        public static void ClearState(UnityMcpWelcomeContext c)
        {
            foreach (string name in new[] { "DontShow", "Seen", "LastSeenWelcomeVersion", "Opens", "FirstOpen", "ReviewDone", "ReviewSnooze", "ShowSteps" })
                EditorPrefs.DeleteKey(Key(c, name));
            foreach (UnityMcpStep step in c.Config.steps)
                EditorPrefs.DeleteKey(Key(c, "Step." + step.id));
            UnityMcpWelcomePrompts.ClearState(c);
        }

        // -- What the project has -------------------------------------------

        public static bool IsAssemblyLoaded(string name) =>
            !string.IsNullOrEmpty(name) &&
            AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == name);

        public static List<UnityMcpRequirement> MissingRequirements(UnityMcpWelcomeContext c) =>
            c.Config.requires.Where(r => !IsAssemblyLoaded(r.assembly)).ToList();

        public static bool IsGitSource(string packageId) =>
            !string.IsNullOrEmpty(packageId) &&
            (packageId.Contains("://") || packageId.StartsWith("git@", StringComparison.Ordinal) || packageId.Contains(".git"));

        /// <summary><c>builtin</c>, <c>urp</c>, <c>hdrp</c> or <c>custom</c>. Read by type name so
        /// this compiles in a project that has neither SRP installed, which is most of them.</summary>
        public static string ActivePipeline()
        {
            RenderPipelineAsset asset = GraphicsSettings.currentRenderPipeline;
            if (asset == null) return "builtin";
            string type = asset.GetType().FullName ?? "";
            if (type.Contains("Universal")) return "urp";
            if (type.Contains("HighDefinition")) return "hdrp";
            return "custom";
        }

        public static string PipelineLabel(string id) =>
            id == "urp" ? "URP" : id == "hdrp" ? "HDRP" : id == "builtin" ? "Built-in" : "a custom pipeline";

        /// <summary>Materials under <paramref name="folder"/> built for another pipeline than the
        /// active one - the ones that render magenta.</summary>
        public static string[] ScopedGuids(string filter, string folder, string[] guids = null)
        {
            if (guids != null && guids.Length > 0)
                return guids.Where(g => !string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(g))).Distinct().ToArray();
            return AssetDatabase.IsValidFolder(folder) ? AssetDatabase.FindAssets(filter, new[] { folder }) : new string[0];
        }

        public static List<Material> MismatchedMaterials(string folder) => ScopedMismatchedMaterials(folder, null);

        public static bool ShaderHasPipeline(Shader shader, string pipeline)
        {
            if (shader == null) return false;
            for (int i = 0; i < shader.subshaderCount; i++)
                if (shader.FindSubshaderTagValue(i, new ShaderTagId("RenderPipeline")).name == pipeline) return true;
            return false;
        }

        public static List<Material> ScopedMismatchedMaterials(string folder, string[] guids)
        {
            var wrong = new List<Material>();
            string pipeline = ActivePipeline();
            foreach (string guid in ScopedGuids("t:Material", folder, guids))
            {
                var material = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
                if (material == null) continue;
                string shader = material.shader != null ? material.shader.name : "";
                bool urp = shader.StartsWith("Universal Render Pipeline", StringComparison.Ordinal) || ShaderHasPipeline(material.shader, "UniversalPipeline");
                bool hdrp = shader.StartsWith("HDRP", StringComparison.Ordinal) || ShaderHasPipeline(material.shader, "HDRenderPipeline");
                bool broken = shader == "" || shader == "Hidden/InternalErrorShader";
                bool fits = pipeline == "urp" ? urp : pipeline == "hdrp" ? hdrp : pipeline == "builtin" ? !urp && !hdrp : true;
                if (broken || !fits) wrong.Add(material);
            }
            return wrong;
        }

        public static bool IsInstalled(UnityMcpProduct product)
        {
            foreach (string rule in product.detect)
            {
                int colon = rule.IndexOf(':');
                if (colon < 0) continue;
                string kind = rule.Substring(0, colon);
                string value = rule.Substring(colon + 1);
                switch (kind)
                {
                    case "folder": if (AssetDatabase.IsValidFolder(value)) return true; break;
                    case "asm": if (IsAssemblyLoaded(value)) return true; break;
                    case "type": if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetType(value, false) != null)) return true; break;
                    case "package": if (UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/" + value) != null) return true; break;
                    case "welcome": if (AssetDatabase.FindAssets(value + ".welcome t:TextAsset").Length > 0) return true; break;
                }
            }
            return false;
        }

        /// <summary>Version of an installed AnkleBreaker product, when it carries a welcome config.</summary>
        public static string InstalledVersion(string productId)
        {
            foreach (string guid in AssetDatabase.FindAssets(productId + ".welcome t:TextAsset"))
            {
                var context = Load(AssetDatabase.GUIDToAssetPath(guid), guid);
                if (context != null && context.Config.id == productId) return DisplayVersion(context);
            }
            return null;
        }

        /// <summary>The config's version, or when it has none, the version of the Package Manager
        /// package holding the Welcome (a GitHub package: it follows each release unstamped).</summary>
        public static string DisplayVersion(UnityMcpWelcomeContext c)
        {
            if (!string.IsNullOrEmpty(c.Config.version)) return c.Config.version;
            UnityEditor.PackageManager.PackageInfo info = string.IsNullOrEmpty(c.Dir)
                ? null : UnityEditor.PackageManager.PackageInfo.FindForAssetPath(c.Dir);
            return info != null ? info.version : "";
        }

        /// <summary>The store product's version to compare with, or null: offline, unknown, or a
        /// Welcome whose version is not the store product's (config storeVersion false).</summary>
        public static string StoreVersion(UnityMcpWelcomeContext c, UnityMcpProduct self) =>
            c.Config.storeVersion && CatalogOnline ? self?.version : null;

        public static bool IsNewer(string candidate, string current) =>
            Version.TryParse(candidate ?? "", out Version a) && Version.TryParse(current ?? "", out Version b) && a > b;

        // -- Figures --------------------------------------------------------

        public static string StatValue(UnityMcpWelcomeContext c, UnityMcpStat stat)
        {
            if (!string.IsNullOrEmpty(stat.count))
            {
                string folder = c.Resolve(stat.path);
                if (!AssetDatabase.IsValidFolder(folder) && (stat.guids == null || stat.guids.Length == 0)) return stat.value ?? "?";
                int count = ScopedGuids(stat.count, folder, stat.guids)
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .Distinct()
                    .Count(p => string.IsNullOrEmpty(stat.exclude) || !p.Contains(stat.exclude));
                return count.ToString(CultureInfo.InvariantCulture);
            }
            if (stat.tris.Length > 0)
            {
                var counts = stat.tris.Select(name => TrianglesOf(c, name)).Where(n => n > 0).ToList();
                if (counts.Count > 0) return Compact(counts.Average());
            }
            return string.IsNullOrEmpty(stat.value) ? "?" : stat.value;
        }

        private static long TrianglesOf(UnityMcpWelcomeContext c, string prefabName)
        {
            GameObject prefab = FindPrefab(c, prefabName);
            if (prefab == null) return 0;
            long total = 0;
            foreach (MeshFilter filter in prefab.GetComponentsInChildren<MeshFilter>(true))
                total += Triangles(filter.sharedMesh);
            foreach (SkinnedMeshRenderer skinned in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                total += Triangles(skinned.sharedMesh);
            return total;
        }

        private static long Triangles(Mesh mesh)
        {
            if (mesh == null) return 0;
            long total = 0;
            for (int i = 0; i < mesh.subMeshCount; i++) total += (long)mesh.GetIndexCount(i) / 3;
            return total;
        }

        private static string Compact(double value) =>
            value >= 10000 ? (value / 1000).ToString("0", CultureInfo.InvariantCulture) + "k"
            : value >= 1000 ? (value / 1000).ToString("0.0", CultureInfo.InvariantCulture) + "k"
            : value.ToString("0", CultureInfo.InvariantCulture);

        public static GameObject FindPrefab(UnityMcpWelcomeContext c, string name)
        {
            foreach (string guid in AssetDatabase.FindAssets(name + " t:Prefab", new[] { c.Root }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) == name) return AssetDatabase.LoadAssetAtPath<GameObject>(path);
            }
            return null;
        }

        /// <summary>How many things the buyer made with the package, outside the package itself.</summary>
        public static int UsageCount(UnityMcpWelcomeContext c)
        {
            if (string.IsNullOrEmpty(c.Config.usage.filter)) return 0;
            return AssetDatabase.FindAssets(c.Config.usage.filter, new[] { "Assets" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Count(p => !p.StartsWith(c.Root + "/", StringComparison.Ordinal));
        }

        // -- Actions --------------------------------------------------------

        public static bool IsSet(UnityMcpAction action) => action != null && !string.IsNullOrEmpty(action.label);

        /// <summary>Whether the button would do anything. A menu item that does not exist (the
        /// package did not compile) or a scene that is not there draws the button disabled rather
        /// than letting it fail silently.</summary>
        public static bool CanRun(UnityMcpWelcomeContext c, UnityMcpAction action)
        {
            if (!IsSet(action)) return false;
            switch (action.kind)
            {
                case "scene": return FindScene(action.target) != null;
                case "menu": return MenuExists(action.target);
                case "ping": return AssetDatabase.LoadMainAssetAtPath(c.Resolve(action.target)) != null;
                case "discord": return true;
                default: return !string.IsNullOrEmpty(action.target);
            }
        }

        public static void Run(UnityMcpWelcomeContext c, UnityMcpAction action, Action<string> openTab, UnityMcpCatalog catalog)
        {
            if (!IsSet(action)) return;
            switch (action.kind)
            {
                case "scene":
                    string scene = FindScene(action.target);
                    if (scene != null && EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                        EditorSceneManager.OpenScene(scene);
                    break;
                case "menu":
                    EditorApplication.ExecuteMenuItem(action.target);
                    break;
                case "url":
                    Application.OpenURL(Url(action.target, catalog));
                    break;
                case "discord":
                    Application.OpenURL(Url("discord", catalog));
                    break;
                case "ping":
                    Object asset = AssetDatabase.LoadMainAssetAtPath(c.Resolve(action.target));
                    if (asset != null)
                    {
                        Selection.activeObject = asset;
                        EditorGUIUtility.PingObject(asset);
                    }
                    break;
                case "tab":
                    openTab?.Invoke(action.target);
                    break;
                case "product":
                    OpenProduct(catalog?.products.FirstOrDefault(p => p.id == action.target), catalog);
                    break;
            }
        }

        /// <summary>An installed product opens its own window, a published one its store page. A
        /// coming-soon one has no page yet: it opens the Discord, where it will be announced.</summary>
        public static void OpenProduct(UnityMcpProduct product, UnityMcpCatalog catalog)
        {
            if (product == null) return;
            if (IsInstalled(product) && MenuExists(product.window)) EditorApplication.ExecuteMenuItem(product.window);
            else if (IsComingSoon(product)) Application.OpenURL(catalog != null ? catalog.discordUrl : product.url);
            else if (!string.IsNullOrEmpty(product.url)) Application.OpenURL(product.url);
        }

        /// <summary>"discord" stands for the catalogue's Discord link, so the invite lives in one
        /// file (Catalog/products.json) instead of in every package's config.</summary>
        public static string Url(string url, UnityMcpCatalog catalog) =>
            url == "discord" && catalog != null ? catalog.discordUrl : url;

        public static bool IsComingSoon(UnityMcpProduct product) => product != null && product.status == "coming-soon";

        public static string FindScene(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (string guid in AssetDatabase.FindAssets(name + " t:Scene"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) == name) return path;
            }
            return null;
        }

        /// <summary>Menu.GetEnabled is false for a path that does not exist, and also for one that
        /// exists but is greyed out: both are "the button would do nothing".</summary>
        public static bool MenuExists(string path) => !string.IsNullOrEmpty(path) && Menu.GetEnabled(path);

        public static void DropPrefab(GameObject prefab)
        {
            if (prefab == null) return;
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            if (instance == null) return;
            Undo.RegisterCreatedObjectUndo(instance, "Add " + prefab.name);
            SceneView view = SceneView.lastActiveSceneView;
            if (view != null) instance.transform.position = view.pivot;
            Selection.activeGameObject = instance;
            view?.FrameSelected();
        }

        // -- Catalogue ------------------------------------------------------

        public static DateTime? CatalogFetchedAt { get; private set; }
        public static bool CatalogOnline => CatalogFetchedAt.HasValue;

        private static UnityMcpCatalog s_remoteCatalog;
        private static string s_catalogUrl;

        public static UnityMcpCatalog LoadCatalog(UnityMcpWelcomeContext c)
        {
            UnityMcpCatalog embedded = ReadCatalog(ReadText(c.Media("welcome-catalog.json"))) ?? new UnityMcpCatalog();
            if (s_remoteCatalog == null) return embedded;
            // Older feeds omit games; an explicit empty array intentionally removes them.
            if (s_remoteCatalog.games == null) s_remoteCatalog.games = embedded.games;
            return s_remoteCatalog;
        }

        private static bool ValidGames(UnityMcpGame[] games) => games == null ||
            (games.All(g => g != null && !string.IsNullOrEmpty(g.id) && !string.IsNullOrEmpty(g.name) &&
                g.products != null && g.products.All(p => p != null && !string.IsNullOrEmpty(p.id) &&
                    (p.role == "game" || p.role == "development")) &&
                ValidGameFile(g.cover) && ValidGameFile(g.logo)) &&
            games.Select(g => g.id).Distinct().Count() == games.Length);

        private static bool ValidGameFile(string file) => string.IsNullOrEmpty(file) ||
            Regex.IsMatch(file, @"\A[A-Za-z0-9_-]+\.(png|jpg)\z");

        private static UnityMcpProduct GameMedia(UnityMcpGame game, bool logo) => new UnityMcpProduct
        {
            id = "game-" + game.id + (logo ? "-logo" : "-cover"),
            cardUrl = logo ? game.logoUrl : game.coverUrl
        };

        public static Texture2D GameImage(UnityMcpWelcomeContext c, UnityMcpGame game, bool logo)
        {
            UnityMcpProduct media = GameMedia(game, logo);
            Texture2D remote = CatalogOnline && !string.IsNullOrEmpty(media.cardUrl) ? LoadImage(CachedCardPath(media)) : null;
            if (remote != null) return remote;
            string file = logo ? game.logo : game.cover;
            return string.IsNullOrEmpty(file) ? null : LoadImage(c.Media("Media/Games/" + file));
        }

        private static UnityMcpCatalog ReadCatalog(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var catalog = JsonUtility.FromJson<UnityMcpCatalog>(json);
                return catalog != null && ValidGames(catalog.games) && catalog.schema == 1 && catalog.products != null && catalog.products.Length > 0 &&
                    catalog.products.All(p => p != null && !string.IsNullOrEmpty(p.id) &&
                        !string.IsNullOrEmpty(p.name) && !string.IsNullOrEmpty(p.url) && p.detect != null) &&
                    catalog.products.Select(p => p.id).Distinct().Count() == catalog.products.Length &&
                    catalog.families != null && catalog.families.All(f => f != null) &&
                    catalog.showcases != null && catalog.showcases.All(s => s != null && s.shelf != null &&
                        s.shelf.All(p => p != null && !string.IsNullOrEmpty(p.id))) ? catalog : null;
            }
            catch (ArgumentException) { return null; }
        }

        private static string ReadText(string assetPath)
        {
            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(assetPath);
            return asset != null ? asset.text : null;
        }

        private static UnityWebRequest s_request;
        private static readonly Queue<UnityMcpProduct> s_cardQueue = new Queue<UnityMcpProduct>();
        private static UnityWebRequest s_cardRequest;
        private static UnityMcpProduct s_cardProduct;

        // A disk cache cannot establish connectivity or keep old promotions alive after a failure.
        public static void RefreshCatalog(UnityMcpCatalog current, bool force = false)
        {
            string url = EditorPrefs.GetString(CATALOG_URL_PREF, CATALOG_URL);
            if (s_request != null && url == s_catalogUrl) return;
            if (s_request != null) { s_request.Abort(); s_request.Dispose(); s_request = null; }
            EditorApplication.update -= PollCatalog;
            s_catalogUrl = url;
            s_remoteCatalog = null;
            CatalogFetchedAt = null;
            s_devlogOnline = false;
            s_devlogAttempted = false;
            if (s_devlogRequest != null) { s_devlogRequest.Abort(); s_devlogRequest.Dispose(); s_devlogRequest = null; }
            EditorApplication.update -= PollDevlog;
            s_cardQueue.Clear();
            CatalogChanged?.Invoke();
            try
            {
                s_request = UnityWebRequest.Get(url);
                s_request.timeout = CATALOG_TIMEOUT_S;
                s_request.SendWebRequest();
                EditorApplication.update += PollCatalog;
            }
            catch (Exception)
            {
                if (s_request != null) s_request.Dispose();
                s_request = null;
            }
        }

        private static void PollCatalog()
        {
            if (s_request == null || !s_request.isDone) return;
            EditorApplication.update -= PollCatalog;
            try
            {
                if (s_request.result == UnityWebRequest.Result.Success)
                    s_remoteCatalog = ReadCatalog(s_request.downloadHandler.text);
                CatalogFetchedAt = s_remoteCatalog != null ? DateTime.UtcNow : (DateTime?)null;
            }
            catch (Exception) { s_remoteCatalog = null; CatalogFetchedAt = null; }
            finally { s_request.Dispose(); s_request = null; }
            CatalogChanged?.Invoke();
        }

        /// <summary>Downloads the Cards the package does not embed, one at a time, into the shared
        /// cache. Until one lands its product draws as a text card.</summary>
        public static void QueueCards(UnityMcpCatalog catalog)
        {
            if (catalog == null || !CatalogOnline) return;
            IEnumerable<UnityMcpProduct> media = catalog.products.Concat((catalog.games ?? new UnityMcpGame[0])
                .SelectMany(g => new[] { GameMedia(g, false), GameMedia(g, true) }));
            foreach (UnityMcpProduct product in media)
            {
                if (!Uri.TryCreate(product.cardUrl, UriKind.Absolute, out Uri uri) ||
                    (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeFile) ||
                    File.Exists(CachedCardPath(product))) continue;
                if (s_cardProduct?.cardUrl != product.cardUrl && !s_cardQueue.Any(p => p.cardUrl == product.cardUrl)) s_cardQueue.Enqueue(product);
            }
            if (s_cardRequest == null && s_cardQueue.Count > 0) NextCard();
        }

        private static void NextCard()
        {
            if (s_cardQueue.Count == 0) return;
            s_cardProduct = s_cardQueue.Dequeue();
            s_cardRequest = UnityWebRequest.Get(s_cardProduct.cardUrl);
            s_cardRequest.timeout = CARD_TIMEOUT_S;
            s_cardRequest.SendWebRequest();
            EditorApplication.update += PollCard;
        }

        private static void PollCard()
        {
            if (s_cardRequest == null || !s_cardRequest.isDone) return;
            EditorApplication.update -= PollCard;
            bool landed = false;
            try
            {
                if (s_cardRequest.result == UnityWebRequest.Result.Success && s_cardRequest.downloadHandler.data.Length > 0)
                {
                    Directory.CreateDirectory(Path.Combine(CACHE_DIR, "cards"));
                    File.WriteAllBytes(CachedCardPath(s_cardProduct), s_cardRequest.downloadHandler.data);
                    landed = true;
                }
            }
            catch (Exception) { /* a missing card is a text card */ }
            finally
            {
                s_cardRequest.Dispose();
                s_cardRequest = null;
            }
            if (landed) CatalogChanged?.Invoke();
            NextCard();
        }

        public static string CachedCardPath(UnityMcpProduct product) =>
            Path.Combine(CACHE_DIR, "cards", Hash128.Compute(product.cardUrl ?? product.id).ToString() + ".image");

        // -- Devlog ---------------------------------------------------------

        private static UnityWebRequest s_devlogRequest;
        private static int s_devlogStage;
        private static bool s_devlogOnline;
        private static bool s_devlogAttempted;
        private static UnityMcpDevlog s_devlogPending;

        private static string DevlogJson => Path.Combine(CACHE_DIR, "devlog.json");
        public static string DevlogImage => Path.Combine(CACHE_DIR, "devlog-cover");
        public static bool DevlogLoading => s_devlogRequest != null;

        public static UnityMcpDevlog LoadDevlog()
        {
            try { return CatalogOnline && s_devlogOnline && File.Exists(DevlogJson) ? JsonUtility.FromJson<UnityMcpDevlog>(File.ReadAllText(DevlogJson)) : null; }
            catch (Exception) { return null; }
        }

        /// <summary>Fetches the current post silently; failed requests leave the devlog hidden.</summary>
        public static void RefreshDevlog(string feedUrl)
        {
            if (!CatalogOnline || s_devlogAttempted || s_devlogRequest != null || string.IsNullOrEmpty(feedUrl)) return;
            s_devlogAttempted = true;
            s_devlogStage = 0;
            StartDevlogRequest(feedUrl);
        }

        private static void StartDevlogRequest(string url)
        {
            try
            {
                s_devlogRequest = UnityWebRequest.Get(url);
                s_devlogRequest.timeout = CATALOG_TIMEOUT_S;
                s_devlogRequest.SendWebRequest();
            }
            catch (Exception)
            {
                if (s_devlogRequest != null) s_devlogRequest.Dispose();
                s_devlogRequest = null;
                return;
            }
            EditorApplication.update -= PollDevlog;
            EditorApplication.update += PollDevlog;
        }

        private static void PollDevlog()
        {
            if (s_devlogRequest == null || !s_devlogRequest.isDone) return;
            EditorApplication.update -= PollDevlog;
            bool ok = s_devlogRequest.result == UnityWebRequest.Result.Success;
            string text = ok && s_devlogStage < 2 ? s_devlogRequest.downloadHandler.text : null;
            byte[] data = ok && s_devlogStage == 2 ? s_devlogRequest.downloadHandler.data : null;
            s_devlogRequest.Dispose();
            s_devlogRequest = null;
            try
            {
                if (s_devlogStage == 0)
                {
                    s_devlogPending = ok ? ParseFeed(text) : null;
                    if (s_devlogPending == null) return;
                    s_devlogStage = 1;
                    StartDevlogRequest(s_devlogPending.link);
                }
                else if (s_devlogStage == 1)
                {
                    Match image = ok ? OG_IMAGE.Match(text) : Match.Empty;
                    if (!image.Success) { SaveDevlog(null); return; }
                    s_devlogPending.image = WebUtility.HtmlDecode(image.Groups[1].Value);
                    s_devlogStage = 2;
                    StartDevlogRequest(s_devlogPending.image);
                }
                else
                {
                    SaveDevlog(data);
                }
            }
            catch (Exception) { /* no devlog is a normal state, not an error */ }
        }

        private static readonly Regex OG_IMAGE =
            new Regex("<meta[^>]+property=\"og:image\"[^>]+content=\"([^\"]+)\"", RegexOptions.IgnoreCase);
        private static readonly Regex ITEM = new Regex("<item>(.*?)</item>", RegexOptions.Singleline);
        private static readonly Regex CDATA = new Regex(@"^\s*<!\[CDATA\[|\]\]>\s*$");
        private static readonly Regex TAGS = new Regex("<[^>]+>");

        private static void SaveDevlog(byte[] cover)
        {
            Directory.CreateDirectory(CACHE_DIR);
            if (cover != null && cover.Length > 0) File.WriteAllBytes(DevlogImage, cover);
            else if (File.Exists(DevlogImage)) File.Delete(DevlogImage);
            File.WriteAllText(DevlogJson, JsonUtility.ToJson(s_devlogPending));
            s_devlogOnline = true;
            s_textures.Remove(DevlogImage);
            CatalogChanged?.Invoke();
        }

        private static UnityMcpDevlog ParseFeed(string xml)
        {
            Match item = ITEM.Match(xml ?? "");
            if (!item.Success) return null;
            string body = item.Groups[1].Value;
            string Field(string tag)
            {
                Match m = Regex.Match(body, "<" + tag + ">(.*?)</" + tag + ">", RegexOptions.Singleline);
                string value = CDATA.Replace(m.Success ? m.Groups[1].Value : "", "");
                return WebUtility.HtmlDecode(TAGS.Replace(value, "")).Trim();
            }
            var post = new UnityMcpDevlog { title = Field("title"), link = Field("link"), summary = Field("description") };
            if (DateTime.TryParse(Field("pubDate"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out DateTime date))
                post.date = date.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
            return string.IsNullOrEmpty(post.title) || string.IsNullOrEmpty(post.link) ? null : post;
        }

        // -- Images ---------------------------------------------------------

        private static readonly Dictionary<string, Texture2D> s_textures = new Dictionary<string, Texture2D>();

        /// <summary>
        /// Loads a PNG or JPG from disk, bypassing its import settings: a Card imported as a
        /// compressed, mipmapped texture comes out soft and blocky at 176 px, and the settings
        /// travel in a .meta nobody reviews.
        /// </summary>
        public static Texture2D LoadImage(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (s_textures.TryGetValue(path, out Texture2D cached) && cached != null) return cached;
            if (!File.Exists(path)) return null;
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            if (!texture.LoadImage(File.ReadAllBytes(path)))
            {
                Object.DestroyImmediate(texture);
                return null;
            }
            texture.filterMode = FilterMode.Bilinear;
            s_textures[path] = texture;
            return texture;
        }

        public static Texture2D Card(UnityMcpWelcomeContext c, UnityMcpProduct product)
        {
            if (product == null) return null;
            Texture2D embedded = string.IsNullOrEmpty(product.card) ? null : LoadImage(c.Media("Media/Cards/" + product.card));
            Texture2D remote = CatalogOnline ? LoadImage(CachedCardPath(product)) : null;
            return remote != null ? remote : embedded;
        }

        public static void ReleaseImages()
        {
            foreach (Texture2D texture in s_textures.Values)
                if (texture != null) Object.DestroyImmediate(texture);
            s_textures.Clear();
        }
    }

    /// <summary>Each package opens for its first use and higher welcome revisions; manual access remains available.</summary>
    [InitializeOnLoad]
    internal static class UnityMcpWelcomeFirstOpen
    {
        private const string SESSION_KEY = UnityMcpWelcomeServices.PREFS + ".OpenedThisSession";

        private const int MAX_DEFERRAL_TICKS = 30;
        private static int s_ticks;

        static UnityMcpWelcomeFirstOpen()
        {
            EditorApplication.delayCall -= TryOpen;
            EditorApplication.delayCall += TryOpen;
            AssetDatabase.importPackageCompleted -= OnPackageImported;
            AssetDatabase.importPackageCompleted += OnPackageImported;
        }

        public static bool OpenedThisSession
        {
            get => SessionState.GetBool(SESSION_KEY, false);
            set => SessionState.SetBool(SESSION_KEY, value);
        }

        private static void TryOpen()
        {
            if (Application.isBatchMode) return;
            // Creating a window brings the editor to the front, even unfocused. An editor started
            // in the background (by an agent, a script) waits until the user comes back to it.
            if (!InternalEditorUtility.isApplicationActive)
            {
                EditorApplication.focusChanged -= OnFocusChanged;
                EditorApplication.focusChanged += OnFocusChanged;
                return;
            }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                if (s_ticks++ < MAX_DEFERRAL_TICKS) EditorApplication.delayCall += TryOpen;
                return;
            }

            foreach (UnityMcpWelcomeContext context in UnityMcpWelcomeServices.LoadContexts())
            {
                if (UnityMcpWelcomeServices.ShouldAutoOpen(context))
                {
                    // Tells every package's prompt scheduler to leave this session alone.
                    SessionState.SetBool(UnityMcpWelcomePrompts.SESSION_AUTO_OPENED, true);
                    UnityMcpWelcome.Open(context.Guid);
                    return;
                }
            }
        }

        private static void OnFocusChanged(bool focused)
        {
            if (!focused) return;
            EditorApplication.focusChanged -= OnFocusChanged;
            s_ticks = 0;
            EditorApplication.delayCall += TryOpen;
        }

        private static void OnPackageImported(string packageName)
        {
            s_ticks = 0;
            EditorApplication.delayCall -= TryOpen;
            EditorApplication.delayCall += TryOpen;
        }
    }
}
