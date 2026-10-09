using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
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
        public static event Action<string> MediaChanged;
        private static int s_consumers;
        private static bool s_notifyingMedia;
        private static readonly HashSet<string> s_attemptedCards = new HashSet<string>();
        private static int s_cardGeneration;
        private static readonly Dictionary<string, object> s_projectCache = new Dictionary<string, object>();
        private static int s_projectRevision = -1;
        private static string s_pipeline;

        static UnityMcpWelcomeServices()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            EditorApplication.quitting += Shutdown;
            UnityMcpWelcomeImages.Subscribe(path => MediaChanged?.Invoke(path));
        }

        private static T Cached<T>(string key, Func<T> compute)
        {
            int revision = UnityMcpWelcomeProjectCache.Revision;
            string pipeline = ActivePipeline();
            if (revision != s_projectRevision || pipeline != s_pipeline)
            {
                s_projectCache.Clear();
                s_projectRevision = revision;
                s_pipeline = pipeline;
            }
            if (s_projectCache.TryGetValue(key, out object value)) return (T)value;
            T result = compute();
            s_projectCache[key] = result;
            return result;
        }

        public static void AcquireConsumer() { s_consumers++; UnityMcpWelcomeImages.Acquire(); }

        public static void ReleaseConsumer()
        {
            if (s_consumers > 0) { s_consumers--; UnityMcpWelcomeImages.Release(); }
            if (s_consumers == 0) Shutdown();
        }

        private static void CancelCards()
        {
            EditorApplication.update -= PollCard;
            foreach (CardJob job in s_cardJobs) UnityMcpWelcomeTransport.Release(ref job.Request);
            s_cardJobs.Clear();
            s_cardGeneration++;
            s_cardQueue.Clear();
            s_attemptedCards.Clear();
            s_cardPaths.Clear();
        }

        private static void Shutdown()
        {
            CancelCards();
            EditorApplication.update -= PollCatalog;
            EditorApplication.update -= PollDevlog;
            s_devlogParse = null; s_devlogLink = null; s_devlogWrite = null;
            UnityMcpWelcomeTransport.Release(ref s_request);
            UnityMcpWelcomeTransport.Release(ref s_devlogRequest);
            CatalogFetchedAt = null;
            s_remoteCatalog = null;
            s_devlogOnline = false;
            ReleaseImages();
        }

        // -- Configs --------------------------------------------------------

        public static List<UnityMcpWelcomeContext> LoadContexts()
        {
            using var perf = new UnityMcpWelcomePerf.Scope("LoadContexts");
            return Cached("contexts", () => ComputeLoadContexts());
        }

        private static List<UnityMcpWelcomeContext> ComputeLoadContexts()
        {
            using var perf = new UnityMcpWelcomePerf.Scope("Compute.LoadContexts");
            
            var found = new List<UnityMcpWelcomeContext>();
            string query = CONFIG_NAME + " t:TextAsset";
            foreach (string guid in UnityMcpWelcomeProjectCache.FindAssets(query))
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

        private static System.Reflection.Assembly[] Assemblies => Cached("assemblies", () => AppDomain.CurrentDomain.GetAssemblies());

        public static bool IsAssemblyLoaded(string name) =>
            !string.IsNullOrEmpty(name) &&
            Cached("assemblyNames", () => new HashSet<string>(Assemblies.Select(a => a.GetName().Name))).Contains(name);

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
            return AssetDatabase.IsValidFolder(folder) ? UnityMcpWelcomeProjectCache.FindAssets(filter, new[] { folder }) : new string[0];
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
            using var perf = new UnityMcpWelcomePerf.Scope("ScopedMismatchedMaterials");
            return Cached("materials:" + folder + ":" + string.Join("|", guids ?? new string[0]), () => ComputeScopedMismatchedMaterials(folder, guids));
        }

        private static List<Material> ComputeScopedMismatchedMaterials(string folder, string[] guids)
        {
            using var perf = new UnityMcpWelcomePerf.Scope("Compute.ScopedMismatchedMaterials");
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

        /// <summary>An art pack whose materials render magenta here and whose pipeline band can
        /// fix them: the same count the band shows.</summary>
        public static bool PipelineNeedsFix(UnityMcpWelcomeContext c)
        {
            if (c.Config.profile != "art") return false;
            UnityMcpPipeline band = c.Config.pipelineBand;
            if (band == null || !MenuExists(band.fixMenu)) return false;
            return ScopedMismatchedMaterials(c.Resolve(band.materials), band.materialGuids).Count > 0;
        }

        public static bool IsInstalled(UnityMcpProduct product)
        {
            using var perf = new UnityMcpWelcomePerf.Scope("IsInstalled");
            return Cached("installed:" + string.Join("|", product.detect), () => ComputeIsInstalled(product));
        }

        private static bool ComputeIsInstalled(UnityMcpProduct product)
        {
            using var perf = new UnityMcpWelcomePerf.Scope("Compute.IsInstalled");
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
                    case "type": if (Assemblies.Any(a => a.GetType(value, false) != null)) return true; break;
                    case "package": if (UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/" + value) != null) return true; break;
                    case "welcome": if (UnityMcpWelcomeProjectCache.FindAssets(value + ".welcome t:TextAsset").Length > 0) return true; break;
                }
            }
            return false;
        }

        /// <summary>Version of an installed AnkleBreaker product, when it carries a welcome config.</summary>
        public static string InstalledVersion(string productId)
        {
            using var perf = new UnityMcpWelcomePerf.Scope("InstalledVersion");
            return Cached("version:" + productId, () => ComputeInstalledVersion(productId));
        }

        private static string ComputeInstalledVersion(string productId)
        {
            using var perf = new UnityMcpWelcomePerf.Scope("Compute.InstalledVersion");
            foreach (string guid in UnityMcpWelcomeProjectCache.FindAssets(productId + ".welcome t:TextAsset"))
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
            using var perf = new UnityMcpWelcomePerf.Scope("StatValue");
            return Cached("stat:" + c.Dir + ":" + JsonUtility.ToJson(stat), () => ComputeStatValue(c, stat));
        }

        private static string ComputeStatValue(UnityMcpWelcomeContext c, UnityMcpStat stat)
        {
            using var perf = new UnityMcpWelcomePerf.Scope("Compute.StatValue");
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
            foreach (string guid in UnityMcpWelcomeProjectCache.FindAssets(name + " t:Prefab", new[] { c.Root }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) == name) return AssetDatabase.LoadAssetAtPath<GameObject>(path);
            }
            return null;
        }

        /// <summary>How many things the buyer made with the package, outside the package itself.</summary>
        public static int UsageCount(UnityMcpWelcomeContext c)
        {
            using var perf = new UnityMcpWelcomePerf.Scope("UsageCount");
            return Cached("usage:" + c.Root + ":" + c.Config.usage.filter, () => ComputeUsageCount(c));
        }

        private static int ComputeUsageCount(UnityMcpWelcomeContext c)
        {
            using var perf = new UnityMcpWelcomePerf.Scope("Compute.UsageCount");
            if (string.IsNullOrEmpty(c.Config.usage.filter)) return 0;
            return UnityMcpWelcomeProjectCache.FindAssets(c.Config.usage.filter, new[] { "Assets" })
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

        public static bool CanRecommend(UnityMcpProduct product, bool recommendationsOnly) =>
            product != null && (!recommendationsOnly || (product.status == "published" && !string.IsNullOrEmpty(product.url)));

        public static bool IsComingSoon(UnityMcpProduct product) => product != null && product.status == "coming-soon";

        public static string FindScene(string name)
        {
            using var perf = new UnityMcpWelcomePerf.Scope("FindScene");
            return Cached("scene:" + name, () => ComputeFindScene(name));
        }

        private static string ComputeFindScene(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (string guid in UnityMcpWelcomeProjectCache.FindAssets(name + " t:Scene"))
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
            using var perf = new UnityMcpWelcomePerf.Scope("LoadCatalog");
            UnityMcpCatalog embedded = Cached("catalog:" + c.Dir, () => ReadCatalog(ReadText(c.Media("welcome-catalog.json"))) ?? new UnityMcpCatalog());
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

        /// <summary>Game images are published in games/ next to the feed. A feed that names the file
        /// without its URL must not blank the Studio tab of packages that embed no game image.</summary>
        private static UnityMcpCatalog CompleteGameUrls(UnityMcpCatalog catalog, string feedUrl)
        {
            if (catalog?.games == null || !Uri.TryCreate(feedUrl, UriKind.Absolute, out Uri feed)) return catalog;
            foreach (UnityMcpGame game in catalog.games)
            {
                if (string.IsNullOrEmpty(game.coverUrl) && !string.IsNullOrEmpty(game.cover))
                    game.coverUrl = new Uri(feed, "games/" + game.cover).AbsoluteUri;
                if (string.IsNullOrEmpty(game.logoUrl) && !string.IsNullOrEmpty(game.logo))
                    game.logoUrl = new Uri(feed, "games/" + game.logo).AbsoluteUri;
            }
            foreach (UnityMcpVenture venture in catalog.ventures ?? new UnityMcpVenture[0])
            {
                if (string.IsNullOrEmpty(venture.coverUrl) && !string.IsNullOrEmpty(venture.cover))
                    venture.coverUrl = new Uri(feed, "games/" + venture.cover).AbsoluteUri;
                if (string.IsNullOrEmpty(venture.logoUrl) && !string.IsNullOrEmpty(venture.logo))
                    venture.logoUrl = new Uri(feed, "games/" + venture.logo).AbsoluteUri;
            }
            return catalog;
        }

        private static UnityMcpProduct VentureMedia(UnityMcpVenture venture, bool logo) => new UnityMcpProduct
        {
            id = "venture-" + venture.id + (logo ? "-logo" : "-cover"),
            cardUrl = logo ? venture.logoUrl : venture.coverUrl
        };

        /// <summary>Ventures only show once the remote catalogue has answered, so their images are
        /// never embedded.</summary>
        public static Texture2D VentureImage(UnityMcpVenture venture, bool logo)
        {
            UnityMcpProduct media = VentureMedia(venture, logo);
            QueueCard(media);
            return CatalogOnline && !string.IsNullOrEmpty(media.cardUrl) ? LoadImage(CachedCardPath(media)) : null;
        }

        private static bool ValidVentures(UnityMcpVenture[] ventures) => ventures == null ||
            (ventures.All(v => v != null && !string.IsNullOrEmpty(v.id) && !string.IsNullOrEmpty(v.title) &&
                ValidGameFile(v.cover) && ValidGameFile(v.logo) &&
                (v.links ?? new UnityMcpVentureLink[0]).All(l => l != null && !string.IsNullOrEmpty(l.label) && !string.IsNullOrEmpty(l.url))) &&
            ventures.Select(v => v.id).Distinct().Count() == ventures.Length);

        public static Texture2D GameImage(UnityMcpWelcomeContext c, UnityMcpGame game, bool logo)
        {
            UnityMcpProduct media = GameMedia(game, logo);
            QueueCard(media);
            string remotePath = CachedCardPath(media);
            if (CatalogOnline && !string.IsNullOrEmpty(media.cardUrl) && UnityMcpWelcomeImages.HasUsableFile(remotePath)) return LoadImage(remotePath);
            string file = logo ? game.logo : game.cover;
            return string.IsNullOrEmpty(file) ? null : LoadImage(c.Media("Media/Games/" + file));
        }

        private static UnityMcpCatalog ReadCatalog(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var catalog = JsonUtility.FromJson<UnityMcpCatalog>(json);
                return catalog != null && ValidGames(catalog.games) && ValidVentures(catalog.ventures) && catalog.schema == 1 && catalog.products != null && catalog.products.Length > 0 &&
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
        private static readonly List<CardJob> s_cardJobs = new List<CardJob>();

        /// <summary>One Card in flight: downloading while <see cref="Write"/> is null, then saving.</summary>
        private sealed class CardJob
        {
            public UnityWebRequest Request;
            public UnityMcpProduct Product;
            public Task Write;
            public string Path;
        }
        private static bool s_overrideWarned;

        // A disk cache cannot establish connectivity or keep old promotions alive after a failure.
        public static void RefreshCatalog(UnityMcpCatalog current, bool force = false) =>
            RefreshCatalogFromUrl(EditorPrefs.GetString(CATALOG_URL_PREF, CATALOG_URL));

        private static void RefreshCatalogFromUrl(string url)
        {
            if (s_request != null && url == s_catalogUrl) return;
            // Machine-wide and set only by our lab tools: a forgotten one silently skews every AB window.
            if (url != CATALOG_URL && !s_overrideWarned)
            {
                s_overrideWarned = true;
                Debug.LogWarning("[AnkleBreaker Welcome] Catalogue URL overridden by EditorPrefs \"" + CATALOG_URL_PREF + "\": " + url);
            }
            UnityMcpWelcomeTransport.Release(ref s_request);
            EditorApplication.update -= PollCatalog;
            s_catalogUrl = url;
            s_remoteCatalog = null;
            CatalogFetchedAt = null;
            s_devlogOnline = false;
            s_devlogAttempted = false;
            UnityMcpWelcomeTransport.Release(ref s_devlogRequest);
            EditorApplication.update -= PollDevlog;
            s_devlogParse = null; s_devlogLink = null; s_devlogWrite = null;
            CancelCards();
            try
            {
                s_request = UnityMcpWelcomeTransport.Acquire(url, CATALOG_TIMEOUT_S, false);
                EditorApplication.update += PollCatalog;
            }
            catch (Exception)
            {
                UnityMcpWelcomeTransport.Release(ref s_request);
            }
            CatalogChanged?.Invoke();
        }

        private static void PollCatalog()
        {
            using var perf = new UnityMcpWelcomePerf.Scope("PollCatalog");
            if (s_request == null || !s_request.isDone) return;
            EditorApplication.update -= PollCatalog;
            try
            {
                if (s_request.result == UnityWebRequest.Result.Success)
                    s_remoteCatalog = CompleteGameUrls(ReadCatalog(s_request.downloadHandler.text), s_catalogUrl);
                CatalogFetchedAt = s_remoteCatalog != null ? DateTime.UtcNow : (DateTime?)null;
            }
            catch (Exception) { s_remoteCatalog = null; CatalogFetchedAt = null; }
            finally { UnityMcpWelcomeTransport.Release(ref s_request); }
            CatalogChanged?.Invoke();
        }

        /// <summary>Downloads the Cards the package does not embed, several at a time, into the shared
        /// cache. Until one lands its product draws as a text card.</summary>
        public static void QueueCards(UnityMcpCatalog catalog)
        {
            if (catalog == null || !CatalogOnline) return;
            foreach (UnityMcpProduct product in catalog.products) QueueCard(product);
            foreach (UnityMcpGame game in catalog.games ?? new UnityMcpGame[0])
            { QueueCard(GameMedia(game, false)); QueueCard(GameMedia(game, true)); }
            foreach (UnityMcpVenture venture in catalog.ventures ?? new UnityMcpVenture[0])
            { QueueCard(VentureMedia(venture, false)); QueueCard(VentureMedia(venture, true)); }
        }

        private static void QueueCard(UnityMcpProduct product)
        {
            if (!CatalogOnline || product == null || s_attemptedCards.Contains(product.cardUrl ?? "") || !Uri.TryCreate(product.cardUrl, UriKind.Absolute, out Uri uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeFile) ||
                !s_attemptedCards.Add(product.cardUrl) || File.Exists(CachedCardPath(product))) return;
            s_cardQueue.Enqueue(product);
            EditorApplication.update -= PollCard;
            EditorApplication.update += PollCard;
        }

        private static void NextCards()
        {
            using var perf = new UnityMcpWelcomePerf.Scope("NextCard");
            if (s_notifyingMedia) return;
            while (s_cardJobs.Count < UnityMcpWelcomeTransport.MaxActive && s_cardQueue.Count > 0)
            {
                UnityMcpProduct product = s_cardQueue.Peek();
                if (File.Exists(CachedCardPath(product))) { s_cardQueue.Dequeue(); continue; }
                try
                {
                    UnityWebRequest request = UnityMcpWelcomeTransport.Acquire(product.cardUrl, CARD_TIMEOUT_S, true);
                    if (request == null) return;
                    s_cardQueue.Dequeue();
                    s_cardJobs.Add(new CardJob { Request = request, Product = product });
                }
                catch (Exception) { s_cardQueue.Dequeue(); }
            }
        }

        private static void PollCard()
        {
            using var perf = new UnityMcpWelcomePerf.Scope("PollCard");
            List<string> landed = null;
            for (int i = s_cardJobs.Count - 1; i >= 0; i--)
            {
                CardJob job = s_cardJobs[i];
                if (job.Write != null)
                {
                    if (!job.Write.IsCompleted) continue;
                    if (job.Write.IsFaulted) _ = job.Write.Exception;
                    else if (!job.Write.IsCanceled) (landed ??= new List<string>()).Add(job.Path);
                    s_cardJobs.RemoveAt(i);
                    continue;
                }
                if (!job.Request.isDone) continue;
                try
                {
                    if (job.Request.result == UnityWebRequest.Result.Success)
                    {
                        byte[] data = job.Request.downloadHandler.data;
                        if (data != null && data.Length > 0)
                        {
                            string path = job.Path = CachedCardPath(job.Product);
                            job.Write = Task.Run(() => AtomicWrite(path, data));
                        }
                    }
                }
                finally { UnityMcpWelcomeTransport.Release(ref job.Request); }
                if (job.Write == null) s_cardJobs.RemoveAt(i);
            }
            if (landed != null)
            {
                // A listener may refresh the catalogue, which cancels this generation mid-loop.
                int generation = s_cardGeneration;
                s_notifyingMedia = true;
                try
                {
                    foreach (string path in landed)
                        if (generation == s_cardGeneration) MediaChanged?.Invoke(path);
                }
                finally { s_notifyingMedia = false; }
            }
            NextCards();
            if (s_cardJobs.Count == 0 && s_cardQueue.Count == 0) EditorApplication.update -= PollCard;
        }

        private static void AtomicWrite(string path, byte[] data)
        {
            string fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            string temporary = fullPath + "." + System.Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, data);
                if (!File.Exists(fullPath))
                {
                    try { File.Move(temporary, fullPath); }
                    catch (IOException) { if (!File.Exists(fullPath)) throw; }
                }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static readonly Dictionary<string, string> s_cardPaths = new Dictionary<string, string>();

        public static string CachedCardPath(UnityMcpProduct product)
        {
            string key = product.cardUrl ?? product.id ?? "";
            if (!s_cardPaths.TryGetValue(key, out var path))
            {
                path = Path.Combine(CACHE_DIR, "cards", Hash128.Compute(key).ToString() + ".image");
                s_cardPaths[key] = path;
            }
            return path;
        }

        // -- Devlog ---------------------------------------------------------

        private static UnityWebRequest s_devlogRequest;
        private static int s_devlogStage;
        private static bool s_devlogOnline;
        private static bool s_devlogAttempted;
        private static UnityMcpDevlog s_devlogPending;
        private static string s_previousDevlogImage;
        private static Task<UnityMcpDevlog> s_devlogParse;
        private static Task<string> s_devlogLink;
        private static Task<bool> s_devlogWrite;

        private static string DevlogJson => Path.Combine(CACHE_DIR, "devlog.json");
        public static string DevlogImage => Path.Combine(CACHE_DIR, "cards", "devlog-" + Hash128.Compute(s_devlogPending?.image ?? "").ToString() + ".image");
        public static bool DevlogLoading => s_devlogRequest != null || s_devlogParse != null || s_devlogLink != null || s_devlogWrite != null;

        public static UnityMcpDevlog LoadDevlog()
        {
            try { return CatalogOnline && s_devlogOnline ? s_devlogPending : null; }
            catch (Exception) { return null; }
        }

        /// <summary>Fetches the current post silently; failed requests leave the devlog hidden.</summary>
        public static void RefreshDevlog(string feedUrl)
        {
            if (!CatalogOnline || s_devlogAttempted || s_devlogRequest != null || string.IsNullOrEmpty(feedUrl)) return;
            s_devlogAttempted = true;
            s_previousDevlogImage = DevlogImage;
            s_devlogStage = 0;
            StartDevlogRequest(feedUrl);
        }

        private static void StartDevlogRequest(string url)
        {
            try
            {
                s_devlogRequest = UnityMcpWelcomeTransport.Acquire(url, CATALOG_TIMEOUT_S, false);
            }
            catch (Exception)
            {
                UnityMcpWelcomeTransport.Release(ref s_devlogRequest);
                return;
            }
            EditorApplication.update -= PollDevlog;
            EditorApplication.update += PollDevlog;
        }

        private static void PollDevlog()
        {
            using var perf = new UnityMcpWelcomePerf.Scope("PollDevlog");
            if (s_devlogParse != null)
            {
                if (!s_devlogParse.IsCompleted) return;
                s_devlogPending = s_devlogParse.GetAwaiter().GetResult();
                s_devlogParse = null;
                if (s_devlogPending == null) { EditorApplication.update -= PollDevlog; return; }
                s_devlogStage = 1;
                StartDevlogRequest(s_devlogPending.link);
                return;
            }
            if (s_devlogLink != null)
            {
                if (!s_devlogLink.IsCompleted) return;
                string link = s_devlogLink.GetAwaiter().GetResult();
                s_devlogLink = null;
                if (string.IsNullOrEmpty(link)) { SaveDevlog(null); return; }
                s_devlogPending.image = link;
                s_devlogStage = 2;
                StartDevlogRequest(link);
                return;
            }
            if (s_devlogWrite != null)
            {
                if (!s_devlogWrite.IsCompleted) return;
                bool ok = s_devlogWrite.GetAwaiter().GetResult();
                s_devlogWrite = null;
                EditorApplication.update -= PollDevlog;
                if (ok)
                {
                    s_devlogOnline = true;
                    if (s_previousDevlogImage != DevlogImage) UnityMcpWelcomeImages.Retire(s_previousDevlogImage);
                    CatalogChanged?.Invoke();
                }
                return;
            }
            if (s_devlogRequest == null) { EditorApplication.update -= PollDevlog; return; }
            if (!s_devlogRequest.isDone) return;
            bool success = s_devlogRequest.result == UnityWebRequest.Result.Success;
            string text = success && s_devlogStage < 2 ? s_devlogRequest.downloadHandler.text : null;
            byte[] data = success && s_devlogStage == 2 ? s_devlogRequest.downloadHandler.data : null;
            UnityMcpWelcomeTransport.Release(ref s_devlogRequest);
            if (s_devlogStage == 0)
                s_devlogParse = Task.Run(() => { try { return success ? ParseFeed(text) : null; } catch (Exception) { return null; } });
            else if (s_devlogStage == 1)
                s_devlogLink = Task.Run(() =>
                {
                    try { Match image = success ? OG_IMAGE.Match(text) : Match.Empty; return image.Success ? WebUtility.HtmlDecode(image.Groups[1].Value) : null; }
                    catch (Exception) { return null; }
                });
            else SaveDevlog(data);
        }

        private static readonly Regex OG_IMAGE =
            new Regex("<meta[^>]+property=\"og:image\"[^>]+content=\"([^\"]+)\"", RegexOptions.IgnoreCase);
        private static readonly Regex ITEM = new Regex("<item>(.*?)</item>", RegexOptions.Singleline);
        private static readonly Regex CDATA = new Regex(@"^\s*<!\[CDATA\[|\]\]>\s*$");
        private static readonly Regex TAGS = new Regex("<[^>]+>");

        private static void SaveDevlog(byte[] cover)
        {
            string imagePath = DevlogImage;
            s_devlogWrite = Task.Run(() =>
            {
                try
                {
                    if (cover != null && cover.Length > 0) AtomicWrite(imagePath, cover);
                    return true;
                }
                catch (Exception) { return false; }
            });
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

        public static Texture2D LoadImage(string path)
        {
            using var perf = new UnityMcpWelcomePerf.Scope("LoadImage");
            return UnityMcpWelcomeImages.Load(path);
        }

        public static Texture2D Card(UnityMcpWelcomeContext c, UnityMcpProduct product)
        {
            if (product == null) return null;
            QueueCard(product);
            string remotePath = CachedCardPath(product);
            if (CatalogOnline && UnityMcpWelcomeImages.HasUsableFile(remotePath)) return LoadImage(remotePath);
            return string.IsNullOrEmpty(product.card) ? null : LoadImage(c.Media("Media/Cards/" + product.card));
        }

        public static void ReleaseImages()
        {
            using var perf = new UnityMcpWelcomePerf.Scope("ReleaseImages");
            if (s_consumers > 0) return;
            UnityMcpWelcomeImages.ClearIfUnused();
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
            UnityMcpWelcomeStartup.Enqueue(TryOpen);
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
                if (s_ticks++ < MAX_DEFERRAL_TICKS) UnityMcpWelcomeStartup.Enqueue(TryOpen);
                return;
            }

            foreach (UnityMcpWelcomeContext context in UnityMcpWelcomeServices.LoadContexts())
            {
                // Seen already, but its materials render magenta in this project (a new project,
                // a pipeline switch): the pipeline band is the fix, so the window comes back once
                // per editor session until they are converted.
                string pipelineKey = UnityMcpWelcomeServices.PREFS + ".PipelineOpened." + context.Guid;
                bool pipeline = !SessionState.GetBool(pipelineKey, false) && UnityMcpWelcomeServices.PipelineNeedsFix(context);
                if (UnityMcpWelcomeServices.ShouldAutoOpen(context) || pipeline)
                {
                    SessionState.SetBool(pipelineKey, true);
                    // Tells every package's prompt scheduler to leave this session alone.
                    SessionState.SetBool(UnityMcpWelcomePrompts.SESSION_AUTO_OPENED, true);
                    UnityMcpWelcome.Open(context.Guid);
                    UnityMcpWelcome.OnAutoOpened(context);
                    return;
                }
            }
        }

        private static void OnFocusChanged(bool focused)
        {
            if (!focused) return;
            EditorApplication.focusChanged -= OnFocusChanged;
            s_ticks = 0;
            UnityMcpWelcomeStartup.Enqueue(TryOpen);
        }

        private static void OnPackageImported(string packageName)
        {
            s_ticks = 0;
            UnityMcpWelcomeStartup.Enqueue(TryOpen);
        }
    }
}
