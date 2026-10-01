using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
#if !UNITY_2022_1_OR_NEWER
using IntegerField = UnityEditor.UIElements.IntegerField;
#endif

namespace UnityMCP.Editor
{
    /// <summary>
    /// Editor window providing an overview of AB Unity MCP status, feature categories,
    /// server controls, queue monitoring, studio news, settings, and active agent
    /// sessions. Accessible via Window > AB Unity MCP > Dashboard.
    ///
    /// UI Toolkit, themed to the AnkleBreaker studio palette (shared brand sheet via
    /// <see cref="MCPTheme"/>). Dynamic sections refresh on a schedule and only rebuild
    /// their rows when a cheap content signature changes.
    /// </summary>
    public class MCPDashboardWindow : EditorWindow
    {
        private const int RefreshIntervalMs = 750;

        // A leaf of its own: a leaf at "Window/AB Unity MCP" shared the path of the Welcome and
        // Action History submenu, so Unity hid it and ExecuteMenuItem could not reach it.
        [MenuItem("Window/AB Unity MCP/Dashboard", false, -10)]
        public static void ShowWindow()
        {
            var window = GetWindow<MCPDashboardWindow>("AB Unity MCP");
            window.minSize = new Vector2(360, 500);
        }

        internal static void ShowSection(string title)
        {
            ShowWindow();
            var window = GetWindow<MCPDashboardWindow>();
            EditorApplication.delayCall += () =>
            {
                if (window == null) return;
                window.rootVisualElement.Query<Foldout>().ForEach(foldout =>
                {
                    if (foldout.text != title) return;
                    foldout.value = true;
                    // Expanding a section changes its geometry before scrolling can locate it.
                    foldout.schedule.Execute(() =>
                    {
                        if (window != null && foldout.panel != null)
                            window.rootVisualElement.Q<ScrollView>().ScrollTo(foldout);
                    });
                });
            };
        }

        // Cached roots for dynamic sections (rebuilt when their signature changes).
        private VisualElement _statusRows;
        private Button _startBtn;
        private Button _stopBtn;
        private VisualElement _newsRows;
        private VisualElement _queueRows;
        private VisualElement _contextRows;
        private VisualElement _agentRows;
        private VisualElement _actionRows;
        private VisualElement _categoryRows;
        private VisualElement _testBar;
        private Toggle _autoStartToggle;
        private Toggle _newsToggle;
        private Toggle _manualPortToggle;
        private IntegerField _portField;
        private VisualElement _portManualGroup;
        private Label _portAutoInfo;
        private Label _portRestartHint;
        private Toggle _mppmToggle;

        private string _statusSig, _newsSig, _contextSig, _actionSig, _categorySig;
        private MCPRequestQueue.DashboardQueueSnapshot _queueSnapshot;
        private bool _hasQueueSnapshot;
        private readonly List<KeyValuePair<string, int>> _queueDepths = new List<KeyValuePair<string, int>>();
        private readonly List<KeyValuePair<string, int>> _shownQueueDepths = new List<KeyValuePair<string, int>>();
        private long _httpRevision = -1;
        private Label _httpActivity, _httpResponses, _httpFailures, _httpBytes, _httpTiming, _httpReloads, _httpInput;
        private string _expandedTestCategory;
        private IVisualElementScheduledItem _refreshSchedule;
        private readonly List<MCPAgentSession.DashboardSnapshot> _agentSnapshots = new List<MCPAgentSession.DashboardSnapshot>();
        private readonly Dictionary<string, AgentView> _agentViews = new Dictionary<string, AgentView>();
        private readonly HashSet<string> _visibleAgentIds = new HashSet<string>();
        private readonly List<string> _removedAgentIds = new List<string>();

        private sealed class AgentView
        {
            internal VisualElement Root, Dot;
            internal Label Latest, Stats, Timing;
            internal MCPAgentSession.DashboardSnapshot Snapshot;
            internal bool HasSnapshot;
        }

        private void OnDisable()
        {
            _refreshSchedule?.Pause();
            _refreshSchedule = null;
            MCPNewsService.Changed -= OnNewsChanged;
        }

        public void CreateGUI()
        {
            // Unity can recreate a window's visual tree without creating a new window instance.
            _refreshSchedule?.Pause();
            MCPNewsService.Changed -= OnNewsChanged;
            rootVisualElement.Clear();
            _agentViews.Clear();
            _hasQueueSnapshot = false;
            _httpRevision = -1;
            MCPTheme.Apply(rootVisualElement);
            MCPNewsService.Changed += OnNewsChanged;

            var scroll = new ScrollView();
            scroll.AddToClassList("ab-dash__scroll");
            rootVisualElement.Add(scroll);

            BuildHeader(scroll);
            BuildStatus(scroll);
            BuildControls(scroll);
            BuildFoldout(scroll, "Request Queue", true, out _queueRows);
            BuildFoldout(scroll, "Active Agent Sessions", true, out _agentRows);
            BuildHttpActivity(scroll);
            BuildActions(scroll);
            BuildContext(scroll);
            BuildCategories(scroll);
            BuildNews(scroll);
            BuildSettings(scroll);
            BuildVersion(scroll);

            // First population is deferred one frame: on layout-restored windows Unity applies
            // saved view-data AFTER CreateGUI, which can stomp children added synchronously here.
            _refreshSchedule = rootVisualElement.schedule.Execute(RefreshAll).Every(RefreshIntervalMs);
        }

        private void OnNewsChanged() => RefreshNews();

        private void RefreshAll()
        {
            // Each section refreshes in isolation: a transient failure (e.g. a data source
            // hiccup around a domain reload) must not blank the other sections, and resetting
            // the failed section's signature makes it rebuild — and self-heal — on the next tick.
            Guarded(RefreshStatus, () => _statusSig = null);
            Guarded(RefreshControls, null);
            Guarded(RefreshNews, () => _newsSig = null);
            Guarded(RefreshQueue, () => _hasQueueSnapshot = false);
            Guarded(RefreshHttpActivity, () => _httpRevision = -1);
            Guarded(RefreshContext, () => _contextSig = null);
            Guarded(RefreshAgents, InvalidateAgentViews);
            Guarded(RefreshActions, () => _actionSig = null);
            Guarded(RefreshCategories, () => _categorySig = null);
            Guarded(RefreshSettings, null);
        }

        private static void Guarded(System.Action refresh, System.Action resetSignature)
        {
            try { refresh(); }
            catch (System.Exception)
            {
                try { resetSignature?.Invoke(); } catch { }
            }
        }

        // ─── Small builders ──────────────────────────────────────────────

        private static VisualElement Row(VisualElement parent)
        {
            var row = new VisualElement();
            row.AddToClassList("ab-dash__row");
            parent.Add(row);
            return row;
        }

        private static VisualElement Dot(VisualElement row, string colorClass)
        {
            var dot = new VisualElement();
            dot.AddToClassList("ab-dash__dot");
            dot.AddToClassList(colorClass);
            row.Add(dot);
            return dot;
        }

        private static Label Text(VisualElement row, string text, string styleClass)
        {
            var label = new Label(text);
            label.AddToClassList(styleClass);
            row.Add(label);
            return label;
        }

        private static void Grow(VisualElement row)
        {
            var spacer = new VisualElement();
            spacer.AddToClassList("ab-dash__grow");
            row.Add(spacer);
        }

        private Foldout BuildFoldout(VisualElement parent, string title, bool open, out VisualElement rows)
        {
            string preferenceKey = "UnityMCP_Dashboard_" + Application.dataPath + "_" + title;
            var foldout = new Foldout { text = title, value = EditorPrefs.GetBool(preferenceKey, open) };
            foldout.RegisterValueChangedCallback(evt =>
            {
                // Nested toggles also bubble boolean changes through their section.
                if (evt.target == foldout) EditorPrefs.SetBool(preferenceKey, evt.newValue);
            });
            foldout.AddToClassList("ab-dash__section");
            parent.Add(foldout);

            var box = new VisualElement();
            box.AddToClassList("ab-box");
            foldout.Add(box);
            rows = box;
            return foldout;
        }

        // ─── Header ──────────────────────────────────────────────────────

        private void BuildHeader(VisualElement parent)
        {
            Button navigation = null;
            navigation = new Button(() => MCPToolbarElement.ShowMenu(navigation.worldBound))
            {
                name = "mcp-navigation-menu",
                text = "MCP Menu \u25BE",
                tooltip = "Dashboard, history, Welcome, settings, server controls and documentation"
            };
            navigation.style.alignSelf = Align.FlexStart;
            navigation.style.marginBottom = 6;
            parent.Add(navigation);

            var title = new Label("AnkleBreaker Unity MCP");
            title.AddToClassList("ab-title");
            parent.Add(title);

            string project = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Application.dataPath));
            var subtitle = new Label($"{project} · Unity {Application.unityVersion}") { tooltip = MCPAssetSafety.ProjectRoot };
            subtitle.AddToClassList("ab-subtitle");
            subtitle.style.whiteSpace = WhiteSpace.Normal;
            parent.Add(subtitle);
        }

        // ─── Connection status ───────────────────────────────────────────

        private void BuildStatus(VisualElement parent)
        {
            _statusRows = new VisualElement();
            _statusRows.AddToClassList("ab-box");
            parent.Add(_statusRows);
        }

        private void RefreshStatus()
        {
            bool running = MCPBridgeServer.IsRunning;
            int agents = MCPRequestQueue.ActiveSessionCount;
            int queued = MCPRequestQueue.TotalQueuedCount;
            bool clone = MCPInstanceRegistry.IsParrelSyncClone();
            int displayPort = running ? MCPBridgeServer.ActivePort : MCPSettingsManager.Port;

            string sig = $"{running}|{displayPort}|{agents}|{queued}|{clone}";
            if (sig == _statusSig && _statusRows.childCount > 0) return;
            _statusSig = sig;

            _statusRows.Clear();

            var main = Row(_statusRows);
            Dot(main, running ? "ab-dash__dot--green" : "ab-dash__dot--red");
            Text(main, running ? "Server Running" : "Server Stopped", "ab-dash__bold");
            Grow(main);
            string portLabel = running && !MCPSettingsManager.UseManualPort
                ? $"Port {displayPort} (auto)" : $"Port {displayPort}";
            Text(main, portLabel, "ab-dash__mini");

            if (agents > 0 || queued > 0)
            {
                var counts = Row(_statusRows);
                if (agents > 0)
                {
                    Dot(counts, "ab-dash__dot--green");
                    Text(counts, $"{agents} agent{(agents > 1 ? "s" : "")}", "ab-dash__label");
                }
                if (queued > 0)
                {
                    Dot(counts, "ab-dash__dot--yellow");
                    Text(counts, $"{queued} queued", "ab-dash__label");
                }
            }

            if (clone)
            {
                var cloneRow = Row(_statusRows);
                Text(cloneRow, "⤷ " + MCPInstanceRegistry.GetParrelSyncCloneLabel(), "ab-dash__blue-text");
            }
        }

        // ─── Server controls ─────────────────────────────────────────────

        private void BuildControls(VisualElement parent)
        {
            var row = Row(parent);
            row.AddToClassList("ab-dash__section");

            _startBtn = new Button(OnStartClicked) { text = "Start" };
            _stopBtn = new Button(OnStopClicked) { text = "Stop" };
            var restart = new Button(OnRestartClicked) { text = "Restart" };
            foreach (var b in new[] { _startBtn, _stopBtn, restart })
            {
                b.AddToClassList("ab-dash__grow");
                row.Add(b);
            }
        }

        private void OnStartClicked() => MCPBridgeServer.Start();
        private void OnStopClicked() => MCPBridgeServer.Stop();

        private void OnRestartClicked()
        {
            MCPBridgeServer.Stop();
            EditorApplication.delayCall += () => MCPBridgeServer.Start();
        }

        private void RefreshControls()
        {
            bool running = MCPBridgeServer.IsRunning;
            _startBtn.SetEnabled(!running);
            _stopBtn.SetEnabled(running);
        }

        // ─── AnkleBreaker news ───────────────────────────────────────────

        private void BuildNews(VisualElement parent)
        {
            BuildFoldout(parent, "AnkleBreaker News", false, out _newsRows);
        }

        private void RefreshNews()
        {
            if (_newsRows == null) return;

            var posts = MCPNewsService.Posts;
            var sb = new StringBuilder();
            sb.Append(MCPNewsService.Enabled).Append('|').Append(MCPNewsService.UnseenCount)
              .Append('|').Append(MCPNewsService.LastError ?? "");
            foreach (var p in posts) sb.Append('|').Append(p.Slug);
            string sig = sb.ToString();
            if (sig == _newsSig && _newsRows.childCount > 0) return;
            _newsSig = sig;

            _newsRows.Clear();

            if (!MCPNewsService.Enabled)
            {
                Text(_newsRows, "News notifications are disabled.", "ab-dash__mini");
                var enableBtn = new Button(OnEnableNewsClicked) { text = "Enable News" };
                _newsRows.Add(enableBtn);
                return;
            }

            var header = Row(_newsRows);
            int unseen = MCPNewsService.UnseenCount;
            Text(header, unseen > 0 ? $"{unseen} new post{(unseen > 1 ? "s" : "")}" : "You're all caught up",
                unseen > 0 ? "ab-dash__accent-text" : "ab-dash__mini");
            Grow(header);
            if (unseen > 0)
                header.Add(new Button(MCPNewsService.MarkAllSeen) { text = "Mark All Read" });
            header.Add(new Button(OnOpenDevlogClicked) { text = "Devlog" });
            header.Add(new Button(MCPNewsService.ForceRefresh) { text = "↺" });

            if (posts.Count == 0)
            {
                Text(_newsRows, MCPNewsService.LastError == null
                    ? "Fetching studio news…"
                    : $"Couldn't reach the devlog ({MCPNewsService.LastError})", "ab-dash__mini");
                return;
            }

            foreach (var post in posts)
            {
                var item = new VisualElement();
                item.AddToClassList("ab-news__item");
                if (MCPNewsService.IsUnseen(post))
                    item.AddToClassList("ab-news__item--unseen");

                // Feed-derived text: disable rich text so markup in a title can never render
                // (defense-in-depth — MCPNewsService already strips angle brackets at parse).
                var title = new Label(post.Title) { enableRichText = false };
                title.AddToClassList("ab-news__title");
                item.Add(title);

                Grow(item);

                if (!string.IsNullOrEmpty(post.Category))
                {
                    var chip = new Label(post.Category) { enableRichText = false };
                    chip.AddToClassList("ab-chip");
                    item.Add(chip);
                }

                if (post.PubDateUtc.Ticks > 0)
                {
                    var date = new Label(post.PubDateUtc.ToLocalTime().ToString("d MMM yyyy"));
                    date.AddToClassList("ab-dash__mini");
                    date.style.marginLeft = 6;
                    item.Add(date);
                }

                var captured = post;
                item.RegisterCallback<ClickEvent>(_ => MCPNewsService.OpenPost(captured));
                _newsRows.Add(item);
            }
        }

        private void OnEnableNewsClicked()
        {
            MCPNewsService.Enabled = true;
            MCPNewsService.ForceRefresh();
        }

        private void OnOpenDevlogClicked() => Application.OpenURL(MCPNewsService.DevlogUrl);

        // ─── Request queue ───────────────────────────────────────────────

        private void RefreshQueue()
        {
            var snapshot = MCPRequestQueue.CopyDashboardQueue(_queueDepths);
            bool unchanged = _hasQueueSnapshot && snapshot.Matches(_queueSnapshot) && _queueDepths.Count == _shownQueueDepths.Count;
            for (int i = 0; unchanged && i < _queueDepths.Count; i++)
                unchanged = _queueDepths[i].Key == _shownQueueDepths[i].Key && _queueDepths[i].Value == _shownQueueDepths[i].Value;
            if (unchanged && _queueRows.childCount > 0) return;
            _queueSnapshot = snapshot;
            _shownQueueDepths.Clear();
            _shownQueueDepths.AddRange(_queueDepths);
            _hasQueueSnapshot = true;
            int totalQueued = snapshot.TotalQueued, executing = snapshot.Executing, cacheSize = snapshot.Cached;

            _queueRows.Clear();

            var summary = Row(_queueRows);
            bool busy = totalQueued > 0 || executing > 0;
            Dot(summary, busy ? "ab-dash__dot--yellow" : "ab-dash__dot--green");
            string statusText = busy
                ? $"{totalQueued} pending  ·  {executing} running  ·  {cacheSize} cached"
                : $"Idle  ·  {cacheSize} cached";
            var summaryText = Text(summary, statusText, "ab-dash__label");
            summaryText.style.whiteSpace = WhiteSpace.Normal;

            if (_queueDepths.Count > 0)
            {
                Text(_queueRows, "Per-agent queue depth:", "ab-dash__mini");
                foreach (var kvp in _queueDepths)
                {
                    int depth = kvp.Value;
                    var row = Row(_queueRows);
                    Dot(row, depth > 0 ? "ab-dash__dot--yellow" : "ab-dash__dot--green");
                    var name = Text(row, kvp.Key, "ab-dash__label");
                    name.AddToClassList("ab-dash__ellipsis");
                    name.style.minWidth = 0;
                    name.tooltip = kvp.Key;
                    Grow(row);
                    Text(row, $"{depth} pending", "ab-dash__mini");
                }
            }
        }

        private void BuildHttpActivity(VisualElement parent)
        {
            var foldout = BuildFoldout(parent, "HTTP Activity", false, out var rows);
            foldout.tooltip = "Bridge HTTP requests, including discovery and polling. Command outcomes are shown in agent sessions.";
            _httpActivity = HttpLine(rows);
            _httpResponses = HttpLine(rows);
            _httpFailures = HttpLine(rows);
            _httpBytes = HttpLine(rows);
            _httpInput = HttpLine(rows);
            _httpTiming = HttpLine(rows);
            _httpReloads = HttpLine(rows);
            _httpResponses.tooltip = "HTTP status codes do not describe command success. Incomplete means no response finished writing.";
            _httpFailures.tooltip = "Input rejects occur before a ticket is accepted. Serialization failures can occur after execution.";
            _httpBytes.tooltip = "Body bytes read and successful response-body writes, aggregated when each handler ends. Excludes headers and active handlers; writing does not confirm client receipt.";
            _httpInput.tooltip = "At most 8 bodies are read together, with 64 MiB reserved across them. Unknown lengths reserve 32 MiB. Each read has a 30-second total deadline; these limits exclude queued arguments and native buffering.";
            _httpTiming.tooltip = "From entry into the HTTP worker to its exit, including queue waits. Excludes network latency and time waiting for a worker.";
            _httpReloads.tooltip = "Reload count persists for this editor session. Duration runs from the before-reload hook to diagnostics initialization; it excludes script compilation and does not measure bridge readiness.";
            var note = HttpLine(rows);
            note.text = "HTTP counters reset on domain reload.";
            note.AddToClassList("ab-dash__mini");
        }

        private static Label HttpLine(VisualElement parent)
        {
            var label = Text(parent, "", "ab-dash__label");
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.minWidth = 0;
            return label;
        }

        private void RefreshHttpActivity()
        {
            var snapshot = MCPHttpDiagnostics.Read();
            if (_httpRevision == snapshot.Revision) return;
            _httpActivity.text = $"{snapshot.Completed} completed · {snapshot.Active} active · peak {snapshot.PeakActive}";
            _httpResponses.text = $"Responses: {snapshot.Responses2xx} 2xx · {snapshot.Responses4xx} 4xx · {snapshot.Responses5xx} 5xx · {snapshot.OtherResponses} other · {snapshot.Incomplete} incomplete";
            _httpFailures.text = $"{snapshot.InputRejected} input rejects · {snapshot.SerializationFailures} serialization failures · {snapshot.Aborted} interrupted";
            _httpBytes.text = $"Body traffic: {snapshot.InputBytes:N0} B read · {snapshot.OutputBytes:N0} B written";
            _httpInput.text = $"Uploads: {snapshot.BodyReaders}/{MCPRequestInput.MaxConcurrentBodyReads} reading · {snapshot.ReservedBodyBytes / (1024.0 * 1024):F1} MiB reserved · {snapshot.BodyAdmissionRefusals} busy refusals · {snapshot.BodyReadTimeouts} timed out";
            _httpTiming.text = $"Handler time: {snapshot.AverageDurationMs:F1} ms average · {snapshot.MaxDurationMs:F1} ms maximum";
            _httpReloads.text = snapshot.DomainReloads == 0 ? "Domain reloads: none recorded this session"
                : $"Domain reloads: {snapshot.DomainReloads} · last {snapshot.LastReloadMs:F0} ms · {snapshot.ActiveAtLastReload} active at reload";
            _httpRevision = snapshot.Revision;
        }

        // ─── Project context ─────────────────────────────────────────────

        private void BuildContext(VisualElement parent)
        {
            BuildFoldout(parent, "Project Context", true, out _contextRows);
        }

        private void RefreshContext()
        {
            bool enabled = MCPSettingsManager.ContextEnabled;
            var files = MCPContextManager.GetContextFileList();

            var sb = new StringBuilder();
            sb.Append(enabled).Append('|').Append(MCPSettingsManager.ContextPath);
            foreach (var f in files) sb.Append('|').Append(f.Category).Append(':').Append(f.Exists).Append(':').Append(f.SizeBytes);
            string sig = sb.ToString();
            if (sig == _contextSig && _contextRows.childCount > 0) return;
            _contextSig = sig;

            _contextRows.Clear();

            var header = Row(_contextRows);
            var toggle = new Toggle("Enable Context") { value = enabled };
            toggle.RegisterValueChangedCallback(OnContextToggled);
            header.Add(toggle);
            var actions = Row(_contextRows);
            actions.Add(new Button(OnCreateTemplatesClicked) { text = "Create Templates" });
            actions.Add(new Button(OnOpenContextFolderClicked) { text = "Open Folder" });

            if (!enabled)
            {
                Text(_contextRows, "Project context is disabled. Agents will not receive project documentation.", "ab-dash__mini").style.whiteSpace = WhiteSpace.Normal;
                return;
            }

            Text(_contextRows, $"Path: {MCPSettingsManager.ContextPath}", "ab-dash__mini");

            bool anyFiles = false;
            foreach (var file in files)
            {
                if (!file.IsStandard && !file.Exists) continue;
                anyFiles = true;

                var row = Row(_contextRows);
                string dotClass = file.Exists && file.SizeBytes > 0 ? "ab-dash__dot--green"
                    : file.Exists ? "ab-dash__dot--yellow" : "ab-dash__dot--grey";
                Dot(row, dotClass);
                Text(row, file.Category, "ab-dash__label");
                Grow(row);
                string sizeLabel = !file.Exists ? "not created"
                    : file.SizeBytes == 0 ? "empty"
                    : file.SizeBytes > 1024 ? $"{file.SizeBytes / 1024f:0.#} KB" : $"{file.SizeBytes} B";
                Text(row, sizeLabel, "ab-dash__mini");
            }

            if (!anyFiles)
                Text(_contextRows, "No context files found. Click 'Create Templates' to get started.", "ab-dash__mini").style.whiteSpace = WhiteSpace.Normal;
        }

        private void OnContextToggled(ChangeEvent<bool> evt) => MCPSettingsManager.ContextEnabled = evt.newValue;

        private void OnCreateTemplatesClicked()
        {
            int created = MCPContextManager.CreateDefaultTemplates();
            EditorUtility.DisplayDialog(
                created > 0 ? "Templates Created" : "Templates Exist",
                created > 0
                    ? $"Created {created} template file(s) in:\n{MCPSettingsManager.ContextPath}"
                    : "All template files already exist.",
                "OK");
        }

        private void OnOpenContextFolderClicked()
        {
            string folderPath = MCPContextManager.GetContextFolderPath();
            if (System.IO.Directory.Exists(folderPath))
                EditorUtility.RevealInFinder(folderPath);
            else
                EditorUtility.DisplayDialog("Folder Not Found",
                    $"Context folder does not exist yet.\nClick 'Create Templates' to set it up.\n\n{folderPath}", "OK");
        }

        // ─── Agent sessions ──────────────────────────────────────────────

        private void InvalidateAgentViews()
        {
            foreach (var view in _agentViews.Values) view.HasSnapshot = false;
        }

        private AgentView BuildAgentView(MCPAgentSession.DashboardSnapshot snapshot)
        {
            if (_agentViews.Count == 0) _agentRows.Clear();
            var root = new VisualElement();
            root.AddToClassList("ab-dash__agent");
            _agentRows.Add(root);
            var row = Row(root);
            var dot = Dot(row, "ab-dash__dot--green");
            var name = Text(row, snapshot.AgentId, "ab-dash__bold");
            name.AddToClassList("ab-dash__ellipsis");
            name.AddToClassList("ab-dash__grow");
            name.style.minWidth = 0;
            name.tooltip = snapshot.AgentId;
            var latest = Text(root, "", "ab-dash__mini");
            latest.AddToClassList("ab-dash__ellipsis");
            var stats = Text(root, "", "ab-dash__mini");
            stats.style.whiteSpace = WhiteSpace.Normal;
            stats.tooltip = "Finished includes all terminal requests. Command errors are returned by handlers; exceptions escape the handler. Outstanding includes queued and running requests.";
            var timing = Text(root, "", "ab-dash__mini");
            timing.style.whiteSpace = WhiteSpace.Normal;
            timing.tooltip = "Measured until each ticket finishes or times out. A timeout cannot stop an operation that already started.";
            var view = new AgentView { Root = root, Dot = dot, Latest = latest, Stats = stats, Timing = timing };
            _agentViews.Add(snapshot.AgentId, view);
            return view;
        }

        private void RefreshAgents()
        {
            MCPRequestQueue.CopyDashboardSessions(_agentSnapshots);
            _visibleAgentIds.Clear();
            foreach (var snapshot in _agentSnapshots)
            {
                _visibleAgentIds.Add(snapshot.AgentId);
                if (!_agentViews.TryGetValue(snapshot.AgentId, out var view)) view = BuildAgentView(snapshot);
                if (view.HasSnapshot && view.Snapshot.Matches(snapshot)) continue;
                view.Snapshot = snapshot;
                view.HasSnapshot = true;
                view.Dot.EnableInClassList("ab-dash__dot--green", snapshot.Outstanding == 0);
                view.Dot.EnableInClassList("ab-dash__dot--yellow", snapshot.Outstanding > 0);
                view.Latest.text = "Latest request: " + snapshot.LatestAction;
                view.Latest.tooltip = "Most recently submitted request, which may be queued or already finished: " + snapshot.LatestAction;
                view.Stats.text = $"{snapshot.Completed} finished · {snapshot.Outstanding} outstanding · " +
                    $"{snapshot.CommandErrors} command error{(snapshot.CommandErrors == 1 ? "" : "s")} · " +
                    $"{snapshot.Exceptions} exception{(snapshot.Exceptions == 1 ? "" : "s")} · {snapshot.Timeouts} timed out";
                view.Timing.text = $"Average: {snapshot.AverageWaitMs} ms waiting · {snapshot.AverageProcessingMs} ms processing";
            }
            _removedAgentIds.Clear();
            foreach (var id in _agentViews.Keys)
                if (!_visibleAgentIds.Contains(id)) _removedAgentIds.Add(id);
            foreach (var id in _removedAgentIds)
            {
                _agentViews[id].Root.RemoveFromHierarchy();
                _agentViews.Remove(id);
            }
            if (_agentViews.Count == 0 && _agentRows.childCount == 0)
                Text(_agentRows, "No active agent sessions.", "ab-dash__mini");
        }

        private void BuildActions(VisualElement parent)
        {
            BuildFoldout(parent, "Recent Actions", true, out _actionRows);
        }

        private void RefreshActions()
        {
            var recent = MCPActionHistory.GetRecent(8);

            var sb = new StringBuilder();
            foreach (var r in recent) sb.Append(r.Id).Append(':').Append(r.DisplayStatus).Append('|');
            string sig = sb.ToString();
            if (sig == _actionSig && _actionRows.childCount > 0) return;
            _actionSig = sig;

            _actionRows.Clear();

            if (recent.Count == 0)
            {
                Text(_actionRows, "No actions recorded yet.", "ab-dash__mini");
                return;
            }

            for (int i = recent.Count - 1; i >= 0; i--)
            {
                var r = recent[i];
                var row = Row(_actionRows);
                string dotClass = r.CommandFailed ? "ab-dash__dot--red" : r.Status == "Completed" ? "ab-dash__dot--green"
                    : r.Status == "Failed" ? "ab-dash__dot--red" : "ab-dash__dot--yellow";
                Dot(row, dotClass);
                row.tooltip = string.IsNullOrEmpty(r.ErrorMessage) ? r.DisplayStatus : r.DisplayStatus + ": " + r.ErrorMessage;
                Text(row, r.Timestamp.ToString("HH:mm:ss"), "ab-dash__mini");

                string agent = r.AgentId ?? "?";
                if (agent.Length > 10) agent = agent.Substring(0, 8) + "..";
                Text(row, agent, "ab-dash__blue-text").style.marginLeft = 6;

                Text(row, MCPActionRecord.ExtractCommand(r.ActionName), "ab-dash__label").style.marginLeft = 6;
                if (r.CommandFailed) Text(row, "Command error", "ab-dash__mini").style.marginLeft = 6;

                string target = r.TargetPath ?? "";
                if (target.Length > 28) target = ".." + target.Substring(target.Length - 26);
                var targetLabel = Text(row, target, "ab-dash__mini");
                targetLabel.AddToClassList("ab-dash__ellipsis");
                targetLabel.style.marginLeft = 6;
                Grow(row);
            }

            var footer = Row(_actionRows);
            Grow(footer);
            string btnLabel = MCPActionHistory.Count > 8
                ? $"Open Full History ({MCPActionHistory.Count} actions)" : "Open Full History";
            footer.Add(new Button(MCPActionHistoryWindow.ShowWindow) { text = btnLabel });
            Grow(footer);
        }

        // ─── Feature categories + tests ──────────────────────────────────

        private void BuildCategories(VisualElement parent)
        {
            var foldout = BuildFoldout(parent, "Feature Categories", true, out _categoryRows);
            _testBar = new VisualElement();
            _testBar.AddToClassList("ab-dash__row");
            foldout.Insert(0, _testBar);
        }

        private void RefreshCategories()
        {
            string[] categories = MCPSettingsManager.GetAllCategoryNames();

            var sb = new StringBuilder();
            sb.Append(MCPSelfTest.IsRunning).Append('|').Append(MCPSelfTest.Progress.ToString("0.00"))
              .Append('|').Append(MCPSelfTest.CurrentCategory).Append('|').Append(_expandedTestCategory)
              .Append('|').Append(MCPBridgeServer.IsRunning);
            foreach (var cat in categories)
            {
                var r = MCPSelfTest.GetResult(cat);
                sb.Append('|').Append(cat).Append(':').Append(MCPSettingsManager.IsCategoryEnabled(cat))
                  .Append(':').Append(r == null ? "-" : r.Status.ToString()).Append(':').Append(r?.Message);
            }
            string sig = sb.ToString();
            if (sig == _categorySig && _categoryRows.childCount > 0) return;
            _categorySig = sig;

            RefreshTestBar();

            _categoryRows.Clear();
            foreach (var cat in categories)
            {
                bool enabled = MCPSettingsManager.IsCategoryEnabled(cat);
                var result = MCPSelfTest.GetResult(cat);

                var row = Row(_categoryRows);
                Dot(row, CategoryDotClass(enabled, result));
                Text(row, char.ToUpper(cat[0]) + cat.Substring(1), "ab-dash__label");

                bool hasTested = result != null && result.Status != MCPTestResult.TestStatus.Untested;
                if (hasTested)
                {
                    string statusClass = result.Status == MCPTestResult.TestStatus.Passed ? "ab-dash__green-text"
                        : result.Status == MCPTestResult.TestStatus.Warning ? "ab-dash__yellow-text" : "ab-dash__red-text";
                    Text(row, TestStatusText(result), statusClass).style.marginLeft = 8;

                    bool hasDetails = result.Status == MCPTestResult.TestStatus.Failed ||
                                      result.Status == MCPTestResult.TestStatus.Warning;
                    if (hasDetails && !string.IsNullOrEmpty(result.Details))
                    {
                        string captured = cat;
                        var detailsBtn = new Button(() => ToggleDetails(captured)) { text = "?" };
                        detailsBtn.style.marginLeft = 4;
                        row.Add(detailsBtn);
                    }
                }

                Grow(row);

                var toggle = new Toggle { value = enabled };
                string catCapture = cat;
                toggle.RegisterValueChangedCallback(evt => MCPSettingsManager.SetCategoryEnabled(catCapture, evt.newValue));
                row.Add(toggle);

                if (_expandedTestCategory == cat && result != null && !string.IsNullOrEmpty(result.Details))
                {
                    var details = new Label(result.Details);
                    details.AddToClassList("ab-dash__details");
                    _categoryRows.Add(details);
                }
            }
        }

        private void ToggleDetails(string category)
        {
            _expandedTestCategory = _expandedTestCategory == category ? null : category;
            _categorySig = null;
            RefreshCategories();
        }

        private void RefreshTestBar()
        {
            _testBar.Clear();

            if (MCPSelfTest.IsRunning)
            {
                Text(_testBar, $"Testing: {MCPSelfTest.CurrentCategory}…", "ab-dash__mini");
                var track = new VisualElement();
                track.AddToClassList("ab-dash__progress-track");
                var fill = new VisualElement();
                fill.AddToClassList("ab-dash__progress-fill");
                fill.style.width = Length.Percent(Mathf.Clamp01(MCPSelfTest.Progress) * 100f);
                track.Add(fill);
                _testBar.Add(track);
                return;
            }

            if (MCPSelfTest.LastRunTime > System.DateTime.MinValue)
            {
                int failed = MCPSelfTest.FailedCount;
                int warnings = MCPSelfTest.WarningCount;
                int total = MCPSettingsManager.GetAllCategoryNames().Length;
                if (failed > 0) Text(_testBar, $"{failed} failed", "ab-dash__red-text").style.marginRight = 8;
                if (warnings > 0) Text(_testBar, $"{warnings} warn", "ab-dash__yellow-text").style.marginRight = 8;
                Text(_testBar, $"{MCPSelfTest.PassedCount}/{total} passed", "ab-dash__green-text");
            }
            else
            {
                Text(_testBar, "No tests run yet", "ab-dash__mini");
            }

            Grow(_testBar);
            var runBtn = new Button(MCPSelfTest.RunAllAsync) { text = "Run Tests" };
            runBtn.SetEnabled(!MCPSelfTest.IsRunning && MCPBridgeServer.IsRunning);
            _testBar.Add(runBtn);
        }

        private static string CategoryDotClass(bool enabled, MCPTestResult result)
        {
            if (!enabled) return "ab-dash__dot--grey";
            if (result == null || result.Status == MCPTestResult.TestStatus.Untested) return "ab-dash__dot--green";
            switch (result.Status)
            {
                case MCPTestResult.TestStatus.Passed: return "ab-dash__dot--green";
                case MCPTestResult.TestStatus.Warning: return "ab-dash__dot--yellow";
                case MCPTestResult.TestStatus.Failed: return "ab-dash__dot--red";
                default: return "ab-dash__dot--grey";
            }
        }

        private static string TestStatusText(MCPTestResult result)
        {
            switch (result.Status)
            {
                case MCPTestResult.TestStatus.Passed: return $"✓ {result.DurationMs:0}ms";
                case MCPTestResult.TestStatus.Warning: return $"⚠ {result.Message}";
                case MCPTestResult.TestStatus.Failed: return $"✗ {result.Message}";
                default: return "—";
            }
        }

        // ─── Settings ────────────────────────────────────────────────────

        private void BuildSettings(VisualElement parent)
        {
            BuildFoldout(parent, "Settings", false, out var box);

            Text(box, "General", "ab-section-heading");

            _autoStartToggle = new Toggle("Auto-start on Editor Load") { value = MCPSettingsManager.AutoStart };
            _autoStartToggle.RegisterValueChangedCallback(OnAutoStartToggled);
            box.Add(_autoStartToggle);

            _newsToggle = new Toggle("News Notifications") { value = MCPNewsService.Enabled };
            _newsToggle.RegisterValueChangedCallback(OnNewsToggled);
            box.Add(_newsToggle);

            Text(box, "Port", "ab-section-heading");

            _manualPortToggle = new Toggle("Use Manual Port") { value = MCPSettingsManager.UseManualPort };
            _manualPortToggle.RegisterValueChangedCallback(OnManualPortToggled);
            box.Add(_manualPortToggle);

            _portManualGroup = new VisualElement();
            _portField = new IntegerField("Server Port") { value = MCPSettingsManager.Port };
            _portField.RegisterValueChangedCallback(OnPortChanged);
            _portManualGroup.Add(_portField);
            _portRestartHint = Text(_portManualGroup, "Restart server to apply port change.", "ab-dash__accent-text");
            box.Add(_portManualGroup);

            _portAutoInfo = Text(box, "", "ab-dash__mini");

            Text(box, "Multiplayer Play Mode (MPPM)", "ab-section-heading");

            _mppmToggle = new Toggle("Start on Virtual Players")
            {
                value = MCPSettingsManager.StartOnVirtualPlayers,
                tooltip = "When off, the MCP bridge does not auto-start on Multiplayer Play Mode " +
                          "virtual players — only on the main Editor. Manual start still works.",
            };
            _mppmToggle.RegisterValueChangedCallback(OnMppmToggled);
            box.Add(_mppmToggle);

            var resetBtn = new Button(OnResetClicked) { text = "Reset All Settings to Defaults" };
            resetBtn.style.marginTop = 8;
            box.Add(resetBtn);
        }

        private void OnAutoStartToggled(ChangeEvent<bool> evt) => MCPSettingsManager.AutoStart = evt.newValue;
        private void OnNewsToggled(ChangeEvent<bool> evt) => MCPNewsService.Enabled = evt.newValue;
        private void OnManualPortToggled(ChangeEvent<bool> evt) => MCPSettingsManager.UseManualPort = evt.newValue;
        private void OnMppmToggled(ChangeEvent<bool> evt) => MCPSettingsManager.StartOnVirtualPlayers = evt.newValue;

        private void OnPortChanged(ChangeEvent<int> evt)
        {
            if (evt.newValue > 1024 && evt.newValue < 65536)
                MCPSettingsManager.Port = evt.newValue;
        }

        private void OnResetClicked()
        {
            if (EditorUtility.DisplayDialog("Reset Settings", "Reset all MCP settings to defaults?", "Reset", "Cancel"))
            {
                MCPSettingsManager.ResetToDefaults();
                _statusSig = _newsSig = _contextSig = _actionSig = _categorySig = null;
                _hasQueueSnapshot = false;
                _httpRevision = -1;
                InvalidateAgentViews();
            }
        }

        private void RefreshSettings()
        {
            _autoStartToggle.SetValueWithoutNotify(MCPSettingsManager.AutoStart);
            _newsToggle.SetValueWithoutNotify(MCPNewsService.Enabled);

            bool manual = MCPSettingsManager.UseManualPort;
            _manualPortToggle.SetValueWithoutNotify(manual);
            _portManualGroup.EnableInClassList("hidden", !manual);
            _portAutoInfo.EnableInClassList("hidden", manual);

            if (manual)
            {
                // Don't clobber the field while the user is typing in it.
                bool editing = _portField.focusController != null
                    && _portField.focusController.focusedElement != null
                    && _portField.Contains(_portField.focusController.focusedElement as VisualElement);
                if (!editing)
                    _portField.SetValueWithoutNotify(MCPSettingsManager.Port);
                bool mismatch = MCPBridgeServer.IsRunning && MCPBridgeServer.ActivePort != MCPSettingsManager.Port;
                _portRestartHint.EnableInClassList("hidden", !mismatch);
            }
            else
            {
                _portAutoInfo.text = MCPBridgeServer.IsRunning
                    ? $"Auto-selected port {MCPBridgeServer.ActivePort} (range: {MCPInstanceRegistry.PortRangeStart}-{MCPInstanceRegistry.PortRangeEnd})"
                    : $"Will auto-select from range {MCPInstanceRegistry.PortRangeStart}-{MCPInstanceRegistry.PortRangeEnd}";
            }

            _mppmToggle.SetValueWithoutNotify(MCPSettingsManager.StartOnVirtualPlayers);
        }

        // ─── Version footer ──────────────────────────────────────────────

        private void BuildVersion(VisualElement parent)
        {
            var box = new VisualElement();
            box.AddToClassList("ab-box");
            var row = Row(box);
            Text(row, $"Plugin Version: {MCPUpdateChecker.CurrentVersion}", "ab-dash__mini");
            Grow(row);
            row.Add(new Button(OnCheckUpdatesClicked) { text = "Check for Updates" });
            parent.Add(box);
        }

        private void OnCheckUpdatesClicked()
        {
            MCPUpdateChecker.CheckForUpdates((hasUpdate, latestVersion) =>
            {
                EditorUtility.DisplayDialog(
                    hasUpdate ? "Update Available" : "Up to Date",
                    hasUpdate
                        ? $"A new version ({latestVersion}) is available.\nUpdate via Unity Package Manager."
                        : "You are running the latest version.",
                    "OK");
            });
        }

        // ─── Helpers ─────────────────────────────────────────────────────

        private static string Read(Dictionary<string, object> dict, string key, string fallback) =>
            dict.TryGetValue(key, out var v) && v != null ? v.ToString() : fallback;

    }
}
