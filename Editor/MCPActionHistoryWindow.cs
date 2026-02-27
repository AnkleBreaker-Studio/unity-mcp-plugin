using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Standalone editor window displaying the full action history for all MCP agents.
    /// Supports filtering by agent, category, and search text; selecting target objects
    /// in the hierarchy; undoing actions; and copying details to clipboard.
    /// </summary>
    public class MCPActionHistoryWindow : EditorWindow
    {
        // ═══════════════════════════════════════════════════════════
        //  State
        // ═══════════════════════════════════════════════════════════

        private Vector2 _listScroll;
        private Vector2 _detailScroll;
        private int _selectedIndex = -1;
        private MCPActionRecord _selectedRecord;

        // Filters
        private int _agentFilterIndex = 0;
        private int _categoryFilterIndex = 0;
        private string _searchText = "";
        private string[] _agentOptions = { "All Agents" };
        private string[] _categoryOptions = { "All Categories" };

        // Cached results
        private List<MCPActionRecord> _filteredRecords = new List<MCPActionRecord>();
        private double _lastRefreshTime;
        private const double RefreshInterval = 0.5;

        // Layout
        private float _detailPanelHeight = 180f;
        private bool _isResizingPanel;

        // ═══════════════════════════════════════════════════════════
        //  Colors & Styles (matching MCPDashboardWindow)
        // ═══════════════════════════════════════════════════════════

        private static readonly Color ColorGreen  = new Color(0.2f, 0.8f, 0.2f);
        private static readonly Color ColorRed    = new Color(0.9f, 0.2f, 0.2f);
        private static readonly Color ColorYellow = new Color(0.9f, 0.8f, 0.1f);
        private static readonly Color ColorGrey   = new Color(0.5f, 0.5f, 0.5f);
        private static readonly Color ColorBlue   = new Color(0.4f, 0.7f, 1.0f);
        private static readonly Color ColorOrange = new Color(0.9f, 0.6f, 0.1f);

        private GUIStyle _headerStyle;
        private GUIStyle _dotStyle;
        private GUIStyle _rowStyle;
        private GUIStyle _rowAltStyle;
        private GUIStyle _rowSelectedStyle;
        private GUIStyle _smallLabel;
        private GUIStyle _detailLabel;
        private GUIStyle _linkStyle;
        private bool _stylesInitialized;

        // ═══════════════════════════════════════════════════════════
        //  Menu & Show
        // ═══════════════════════════════════════════════════════════

        [MenuItem("Window/AB Unity MCP/Action History")]
        public static void ShowWindow()
        {
            var window = GetWindow<MCPActionHistoryWindow>("MCP Action History");
            window.minSize = new Vector2(500, 400);
        }

        // ═══════════════════════════════════════════════════════════
        //  Lifecycle
        // ═══════════════════════════════════════════════════════════

        private void OnEnable()
        {
            MCPActionHistory.OnActionRecorded += OnNewAction;
            RefreshFilters();
            RefreshList();
        }

        private void OnDisable()
        {
            MCPActionHistory.OnActionRecorded -= OnNewAction;
        }

        private void OnNewAction(MCPActionRecord record)
        {
            RefreshFilters();
            RefreshList();
            Repaint();
        }

        private void OnInspectorUpdate()
        {
            if (EditorApplication.timeSinceStartup - _lastRefreshTime > RefreshInterval)
            {
                RefreshList();
                Repaint();
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  Styles
        // ═══════════════════════════════════════════════════════════

        private void InitStyles()
        {
            if (_stylesInitialized) return;

            _headerStyle = new GUIStyle(EditorStyles.largeLabel)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
            };

            _dotStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleCenter,
                fixedWidth = 18,
            };

            _rowStyle = new GUIStyle("CN Box")
            {
                padding = new RectOffset(4, 4, 2, 2),
                margin = new RectOffset(0, 0, 0, 0),
                fixedHeight = 0,
            };

            _rowAltStyle = new GUIStyle(_rowStyle);

            _rowSelectedStyle = new GUIStyle(_rowStyle);
            var selTex = new Texture2D(1, 1);
            selTex.SetPixel(0, 0, new Color(0.24f, 0.48f, 0.9f, 0.3f));
            selTex.Apply();
            _rowSelectedStyle.normal.background = selTex;

            _smallLabel = new GUIStyle(EditorStyles.miniLabel)
            {
                wordWrap = false,
            };

            _detailLabel = new GUIStyle(EditorStyles.label)
            {
                wordWrap = true,
                richText = true,
            };

            _linkStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = ColorBlue },
                hover = { textColor = new Color(0.5f, 0.8f, 1.0f) },
                wordWrap = false,
            };

            _stylesInitialized = true;
        }

        // ═══════════════════════════════════════════════════════════
        //  Refresh Data
        // ═══════════════════════════════════════════════════════════

        private void RefreshFilters()
        {
            var agents = MCPActionHistory.GetDistinctAgents();
            _agentOptions = new string[agents.Count + 1];
            _agentOptions[0] = "All Agents";
            for (int i = 0; i < agents.Count; i++)
                _agentOptions[i + 1] = agents[i];

            var cats = MCPActionHistory.GetDistinctCategories();
            _categoryOptions = new string[cats.Count + 1];
            _categoryOptions[0] = "All Categories";
            for (int i = 0; i < cats.Count; i++)
                _categoryOptions[i + 1] = cats[i];

            // Clamp filter indices
            if (_agentFilterIndex >= _agentOptions.Length) _agentFilterIndex = 0;
            if (_categoryFilterIndex >= _categoryOptions.Length) _categoryFilterIndex = 0;
        }

        private void RefreshList()
        {
            _lastRefreshTime = EditorApplication.timeSinceStartup;

            string agentFilter = _agentFilterIndex > 0 ? _agentOptions[_agentFilterIndex] : null;
            string catFilter = _categoryFilterIndex > 0 ? _categoryOptions[_categoryFilterIndex] : null;
            string search = string.IsNullOrWhiteSpace(_searchText) ? null : _searchText;

            _filteredRecords = MCPActionHistory.GetFiltered(agentFilter, catFilter, search);

            // Reverse so newest is first
            _filteredRecords.Reverse();
        }

        // ═══════════════════════════════════════════════════════════
        //  Main GUI
        // ═══════════════════════════════════════════════════════════

        private void OnGUI()
        {
            InitStyles();

            DrawToolbar();
            DrawActionList();
            DrawResizeHandle();
            DrawDetailPanel();
        }

        // ═══════════════════════════════════════════════════════════
        //  Toolbar
        // ═══════════════════════════════════════════════════════════

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            // Agent filter
            EditorGUI.BeginChangeCheck();
            _agentFilterIndex = EditorGUILayout.Popup(_agentFilterIndex, _agentOptions,
                EditorStyles.toolbarPopup, GUILayout.Width(130));
            if (EditorGUI.EndChangeCheck()) RefreshList();

            // Category filter
            EditorGUI.BeginChangeCheck();
            _categoryFilterIndex = EditorGUILayout.Popup(_categoryFilterIndex, _categoryOptions,
                EditorStyles.toolbarPopup, GUILayout.Width(120));
            if (EditorGUI.EndChangeCheck()) RefreshList();

            // Search
            EditorGUI.BeginChangeCheck();
            _searchText = EditorGUILayout.TextField(_searchText, EditorStyles.toolbarSearchField);
            if (EditorGUI.EndChangeCheck()) RefreshList();

            // Count label
            GUILayout.Label($"{_filteredRecords.Count}/{MCPActionHistory.Count}", _smallLabel,
                GUILayout.Width(55));

            // Persistence toggle
            EditorGUI.BeginChangeCheck();
            bool persist = GUILayout.Toggle(MCPSettingsManager.ActionHistoryPersistence, "Persist",
                EditorStyles.toolbarButton, GUILayout.Width(50));
            if (EditorGUI.EndChangeCheck())
                MCPSettingsManager.ActionHistoryPersistence = persist;

            // Clear button
            if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(40)))
            {
                if (EditorUtility.DisplayDialog("Clear History",
                    "Clear all action history? This cannot be undone.", "Clear", "Cancel"))
                {
                    MCPActionHistory.Clear();
                    _selectedIndex = -1;
                    _selectedRecord = null;
                    RefreshList();
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        // ═══════════════════════════════════════════════════════════
        //  Action List
        // ═══════════════════════════════════════════════════════════

        private void DrawActionList()
        {
            float listHeight = position.height - _detailPanelHeight - 28; // 28 = toolbar
            if (listHeight < 80) listHeight = 80;

            _listScroll = EditorGUILayout.BeginScrollView(_listScroll, GUILayout.Height(listHeight));

            if (_filteredRecords.Count == 0)
            {
                EditorGUILayout.HelpBox("No actions recorded yet. Perform MCP tool calls to see them here.", MessageType.Info);
            }
            else
            {
                for (int i = 0; i < _filteredRecords.Count; i++)
                {
                    var record = _filteredRecords[i];
                    bool isSelected = (i == _selectedIndex);

                    GUIStyle style = isSelected ? _rowSelectedStyle : (i % 2 == 0 ? _rowStyle : _rowAltStyle);
                    EditorGUILayout.BeginHorizontal(style);

                    // Status dot
                    Color dotColor = GetStatusColor(record.Status);
                    var prevColor = GUI.color;
                    GUI.color = dotColor;
                    GUILayout.Label("\u25CF", _dotStyle, GUILayout.Width(18));
                    GUI.color = prevColor;

                    // Timestamp
                    GUILayout.Label(record.Timestamp.ToString("HH:mm:ss"), _smallLabel,
                        GUILayout.Width(55));

                    // Agent badge
                    string agentShort = TruncateAgent(record.AgentId);
                    prevColor = GUI.color;
                    GUI.color = ColorBlue;
                    GUILayout.Label(agentShort, _smallLabel, GUILayout.Width(65));
                    GUI.color = prevColor;

                    // Category
                    prevColor = GUI.color;
                    GUI.color = GetCategoryColor(record.Category);
                    GUILayout.Label(record.Category ?? "", _smallLabel, GUILayout.Width(75));
                    GUI.color = prevColor;

                    // Action command
                    string cmd = MCPActionRecord.ExtractCommand(record.ActionName);
                    GUILayout.Label(cmd, EditorStyles.miniLabel, GUILayout.Width(110));

                    // Target path (clickable)
                    if (!string.IsNullOrEmpty(record.TargetPath))
                    {
                        if (GUILayout.Button(TruncateString(record.TargetPath, 30), _linkStyle))
                        {
                            SelectTargetObject(record);
                        }
                    }
                    else
                    {
                        GUILayout.FlexibleSpace();
                    }

                    // Duration
                    GUILayout.Label($"{record.ExecutionTimeMs}ms", _smallLabel, GUILayout.Width(50));

                    EditorGUILayout.EndHorizontal();

                    // Check if row was clicked
                    Rect rowRect = GUILayoutUtility.GetLastRect();
                    if (Event.current.type == EventType.MouseDown && rowRect.Contains(Event.current.mousePosition))
                    {
                        _selectedIndex = i;
                        _selectedRecord = record;
                        Event.current.Use();
                        Repaint();

                        // Double-click to frame in scene
                        if (Event.current.clickCount == 2)
                            FrameTargetObject(record);
                    }
                }
            }

            EditorGUILayout.EndScrollView();
        }

        // ═══════════════════════════════════════════════════════════
        //  Resize Handle
        // ═══════════════════════════════════════════════════════════

        private void DrawResizeHandle()
        {
            Rect handleRect = EditorGUILayout.GetControlRect(false, 4);
            handleRect.x = 0;
            handleRect.width = position.width;

            EditorGUI.DrawRect(handleRect, new Color(0.3f, 0.3f, 0.3f));
            EditorGUIUtility.AddCursorRect(handleRect, MouseCursor.ResizeVertical);

            if (Event.current.type == EventType.MouseDown && handleRect.Contains(Event.current.mousePosition))
            {
                _isResizingPanel = true;
                Event.current.Use();
            }

            if (_isResizingPanel)
            {
                if (Event.current.type == EventType.MouseDrag)
                {
                    _detailPanelHeight = position.height - Event.current.mousePosition.y - 14;
                    _detailPanelHeight = Mathf.Clamp(_detailPanelHeight, 60, position.height - 150);
                    Repaint();
                    Event.current.Use();
                }
                else if (Event.current.type == EventType.MouseUp)
                {
                    _isResizingPanel = false;
                    Event.current.Use();
                }
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  Detail Panel
        // ═══════════════════════════════════════════════════════════

        private void DrawDetailPanel()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.Height(_detailPanelHeight));

            if (_selectedRecord == null)
            {
                EditorGUILayout.LabelField("Select an action above to see details.", EditorStyles.centeredGreyMiniLabel);
            }
            else
            {
                var r = _selectedRecord;

                // Header row with action buttons
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"{r.ActionName}", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();

                // Copy button
                if (GUILayout.Button("Copy", EditorStyles.miniButton, GUILayout.Width(40)))
                {
                    EditorGUIUtility.systemCopyBuffer = r.ToCopyString();
                    Debug.Log("[MCP History] Action details copied to clipboard.");
                }

                // Undo button
                if (r.UndoGroup >= 0)
                {
                    if (GUILayout.Button("Undo", EditorStyles.miniButton, GUILayout.Width(40)))
                    {
                        if (EditorUtility.DisplayDialog("Undo Action",
                            $"Undo '{MCPActionRecord.ExtractCommand(r.ActionName)}'?\n\nThis will revert all changes in this undo group.",
                            "Undo", "Cancel"))
                        {
                            Undo.RevertAllDownToGroup(r.UndoGroup);
                            Debug.Log($"[MCP History] Reverted to undo group {r.UndoGroup}");
                        }
                    }
                }

                // Select button
                if (r.TargetInstanceId != 0)
                {
                    if (GUILayout.Button("Select", EditorStyles.miniButton, GUILayout.Width(45)))
                        SelectTargetObject(r);
                }

                EditorGUILayout.EndHorizontal();

                _detailScroll = EditorGUILayout.BeginScrollView(_detailScroll);

                // Status & timing
                Color statusColor = GetStatusColor(r.Status);
                DrawDetailRow("Status", r.Status, statusColor);
                DrawDetailRow("Agent", r.AgentId);
                DrawDetailRow("Time", r.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"));
                DrawDetailRow("Duration", $"{r.ExecutionTimeMs}ms");
                DrawDetailRow("Category", r.Category);

                if (!string.IsNullOrEmpty(r.TargetPath))
                    DrawDetailRow("Target", r.TargetPath);
                if (!string.IsNullOrEmpty(r.TargetType))
                    DrawDetailRow("Target Type", r.TargetType);
                if (r.TargetInstanceId != 0)
                    DrawDetailRow("Instance ID", r.TargetInstanceId.ToString());
                if (r.UndoGroup >= 0)
                    DrawDetailRow("Undo Group", r.UndoGroup.ToString());

                // Error message
                if (!string.IsNullOrEmpty(r.ErrorMessage))
                {
                    EditorGUILayout.Space(4);
                    var prevColor = GUI.color;
                    GUI.color = ColorRed;
                    EditorGUILayout.LabelField("Error:", EditorStyles.boldLabel);
                    GUI.color = prevColor;
                    EditorGUILayout.LabelField(r.ErrorMessage, _detailLabel);
                }

                // Parameters
                if (r.Parameters != null && r.Parameters.Count > 0)
                {
                    EditorGUILayout.Space(4);
                    EditorGUILayout.LabelField("Parameters:", EditorStyles.boldLabel);
                    foreach (var kvp in r.Parameters)
                        DrawDetailRow($"  {kvp.Key}", kvp.Value);
                }

                EditorGUILayout.EndScrollView();
            }

            EditorGUILayout.EndVertical();
        }

        // ═══════════════════════════════════════════════════════════
        //  Helpers
        // ═══════════════════════════════════════════════════════════

        private void DrawDetailRow(string label, string value, Color? valueColor = null)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, _smallLabel, GUILayout.Width(90));
            if (valueColor.HasValue)
            {
                var prev = GUI.color;
                GUI.color = valueColor.Value;
                EditorGUILayout.LabelField(value ?? "", _detailLabel);
                GUI.color = prev;
            }
            else
            {
                EditorGUILayout.LabelField(value ?? "", _detailLabel);
            }
            EditorGUILayout.EndHorizontal();
        }

        private void SelectTargetObject(MCPActionRecord record)
        {
            if (record.TargetInstanceId != 0)
            {
                var obj = EditorUtility.InstanceIDToObject(record.TargetInstanceId);
                if (obj != null)
                {
                    Selection.activeObject = obj;
                    EditorGUIUtility.PingObject(obj);
                    return;
                }
            }

            // Fallback: try finding by path
            if (!string.IsNullOrEmpty(record.TargetPath))
            {
                var go = GameObject.Find(record.TargetPath);
                if (go != null)
                {
                    Selection.activeGameObject = go;
                    EditorGUIUtility.PingObject(go);
                }
            }
        }

        private void FrameTargetObject(MCPActionRecord record)
        {
            SelectTargetObject(record);
            if (Selection.activeGameObject != null)
                SceneView.FrameLastActiveSceneView();
        }

        private static Color GetStatusColor(string status)
        {
            if (string.IsNullOrEmpty(status)) return ColorGrey;
            switch (status)
            {
                case "Completed": return ColorGreen;
                case "Failed":    return ColorRed;
                case "TimedOut":  return ColorOrange;
                default:          return ColorYellow;
            }
        }

        private static Color GetCategoryColor(string category)
        {
            if (string.IsNullOrEmpty(category)) return ColorGrey;
            // Simple hash-based color for variety
            int hash = category.GetHashCode();
            float h = Mathf.Abs(hash % 360) / 360f;
            return Color.HSVToRGB(h, 0.5f, 0.85f);
        }

        private static string TruncateAgent(string agentId)
        {
            if (string.IsNullOrEmpty(agentId)) return "unknown";
            return agentId.Length <= 10 ? agentId : agentId.Substring(0, 8) + "..";
        }

        private static string TruncateString(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= maxLen) return s;
            return ".." + s.Substring(s.Length - maxLen + 2);
        }
    }
}
