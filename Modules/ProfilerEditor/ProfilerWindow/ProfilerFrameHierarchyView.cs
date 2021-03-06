// Unity C# reference source
// Copyright (c) Unity Technologies. For terms of use, see
// https://unity3d.com/legal/licenses/Unity_Reference_Only_License

using UnityEngine;
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEditor.Profiling;
using Unity.Profiling;

namespace UnityEditorInternal.Profiling
{
    [Serializable]
    internal class ProfilerFrameDataHierarchyView : ProfilerFrameDataViewBase
    {
        enum DetailedViewType
        {
            None,
            Objects,
            CallersAndCallees,
        }

        static readonly string kMainThreadName = "Main Thread";
        static readonly string kRenderThreadName = "Render Thread";

        static readonly GUIContent[] kDetailedViewTypeNames =
        {
            EditorGUIUtility.TrTextContent("No Details"),
            EditorGUIUtility.TrTextContent("Show Related Objects"),
            EditorGUIUtility.TrTextContent("Show Calls")
        };
        static readonly int[] kDetailedViewTypes = new[]
        {
            (int)DetailedViewType.None,
            (int)DetailedViewType.Objects,
            (int)DetailedViewType.CallersAndCallees,
        };

        [Flags]
        public enum CpuProfilerOptions
        {
            None = 0,
            CollapseEditorBoundarySamples = 1 << 0, // Session based override, default to off
        };

        static readonly GUIContent[] k_CpuProfilerOptions =
        {
            EditorGUIUtility.TrTextContent("Collapse EditorOnly Samples", "Samples that are only created due to profiling the editor are collapsed by default, renamed to EditorOnly [<FunctionName>] and any GC Alloc incurred by them will not be accumulated."),
        };

        private const string k_CpuProfilerHierarchyViewOptionsPrefKey = "CPUHierarchyView." + nameof(m_CpuProfilerOptions);


        [NonSerialized]
        bool m_Initialized;

        [NonSerialized]
        int m_FrameIndex = -1;

        [SerializeField]
        TreeViewState m_TreeViewState;

        [SerializeField]
        MultiColumnHeaderState m_MultiColumnHeaderState;

        [SerializeField]
        int m_ThreadIndex = 0;

        [NonSerialized]
        string[] m_ThreadNames;

        [NonSerialized]
        int[] m_ThreadIndices;

        [SerializeField]
        DetailedViewType m_DetailedViewType = DetailedViewType.None;

        [SerializeField]
        SplitterState m_DetailedViewSpliterState;

        SearchField m_SearchField;
        ProfilerFrameDataTreeView m_TreeView;

        [SerializeField]
        ProfilerDetailedObjectsView m_DetailedObjectsView;

        [SerializeField]
        ProfilerDetailedCallsView m_DetailedCallsView;

        [SerializeField]
        int m_CpuProfilerOptions = (int)CpuProfilerOptions.CollapseEditorBoundarySamples;

        public ProfilerFrameDataTreeView treeView
        {
            get { return m_TreeView; }
        }

        public ProfilerDetailedObjectsView detailedObjectsView
        {
            get { return m_DetailedObjectsView; }
        }

        public ProfilerDetailedCallsView detailedCallsView
        {
            get { return m_DetailedCallsView; }
        }

        [SerializeField]
        string m_ThreadName;

        public string threadName
        {
            get
            {
                return m_ThreadName;
            }
            set
            {
                m_ThreadName = value;
            }
        }

        public int sortedProfilerColumn
        {
            get
            {
                return m_TreeView == null ? HierarchyFrameDataView.columnDontSort : m_TreeView.sortedProfilerColumn;
            }
        }

        public bool sortedProfilerColumnAscending
        {
            get
            {
                return m_TreeView == null ? false : m_TreeView.sortedProfilerColumnAscending;
            }
        }

        public delegate void SelectionChangedCallback(int id);
        public event SelectionChangedCallback selectionChanged;

        public delegate void SearchChangedCallback(string newSearch);
        public event SearchChangedCallback searchChanged;

        static readonly ProfilerMarker m_DoGUIMarker = new ProfilerMarker(nameof(ProfilerFrameDataHierarchyView) + ".DoGUI");

        public ProfilerFrameDataHierarchyView()
        {
            m_Initialized = false;
            m_ThreadName = kMainThreadName;
        }

        void InitIfNeeded()
        {
            if (m_Initialized)
                return;

            var cpuHierarchyColumns = new[]
            {
                HierarchyFrameDataView.columnName,
                HierarchyFrameDataView.columnTotalPercent,
                HierarchyFrameDataView.columnSelfPercent,
                HierarchyFrameDataView.columnCalls,
                HierarchyFrameDataView.columnGcMemory,
                HierarchyFrameDataView.columnTotalTime,
                HierarchyFrameDataView.columnSelfTime,
                HierarchyFrameDataView.columnWarningCount
            };
            var gpuHierarchyColumns = new[]
            {
                HierarchyFrameDataView.columnName,
                HierarchyFrameDataView.columnTotalGpuPercent,
                HierarchyFrameDataView.columnDrawCalls,
                HierarchyFrameDataView.columnTotalGpuTime
            };
            var profilerColumns = gpuView ? gpuHierarchyColumns : cpuHierarchyColumns;
            var defaultSortColumn = gpuView ? HierarchyFrameDataView.columnTotalGpuTime : HierarchyFrameDataView.columnTotalTime;

            var columns = CreateColumns(profilerColumns);
            var headerState = CreateDefaultMultiColumnHeaderState(columns, defaultSortColumn);
            if (MultiColumnHeaderState.CanOverwriteSerializedFields(m_MultiColumnHeaderState, headerState))
                MultiColumnHeaderState.OverwriteSerializedFields(m_MultiColumnHeaderState, headerState);

            var firstInit = m_MultiColumnHeaderState == null;
            m_MultiColumnHeaderState = headerState;

            var multiColumnHeader = new ProfilerFrameDataMultiColumnHeader(m_MultiColumnHeaderState, columns) { height = 25 };
            if (firstInit)
                multiColumnHeader.ResizeToFit();

            // Check if it already exists (deserialized from window layout file or scriptable object)
            if (m_TreeViewState == null)
                m_TreeViewState = new TreeViewState();
            m_TreeView = new ProfilerFrameDataTreeView(m_TreeViewState, multiColumnHeader);
            m_TreeView.selectionChanged += OnMainTreeViewSelectionChanged;
            m_TreeView.searchChanged += OnMainTreeViewSearchChanged;
            m_TreeView.Reload();

            m_CpuProfilerOptions = SessionState.GetInt(k_CpuProfilerHierarchyViewOptionsPrefKey, m_CpuProfilerOptions);

            m_SearchField = new SearchField();
            m_SearchField.downOrUpArrowKeyPressed += m_TreeView.SetFocusAndEnsureSelectedItem;

            if (m_DetailedObjectsView == null)
                m_DetailedObjectsView = new ProfilerDetailedObjectsView();
            m_DetailedObjectsView.gpuView = gpuView;
            m_DetailedObjectsView.frameItemEvent += FrameItem;
            if (m_DetailedCallsView == null)
                m_DetailedCallsView = new ProfilerDetailedCallsView();
            m_DetailedCallsView.frameItemEvent += FrameItem;
            if (m_DetailedViewSpliterState == null || m_DetailedViewSpliterState.relativeSizes == null || m_DetailedViewSpliterState.relativeSizes.Length == 0)
                m_DetailedViewSpliterState = new SplitterState(new[] { 70f, 30f }, new[] { 450, 50 }, null);

            m_Initialized = true;
        }

        public static ProfilerFrameDataMultiColumnHeader.Column[] CreateColumns(int[] profilerColumns)
        {
            var columns = new ProfilerFrameDataMultiColumnHeader.Column[profilerColumns.Length];
            for (var i = 0; i < profilerColumns.Length; ++i)
            {
                var columnName = GetProfilerColumnName(profilerColumns[i]);
                var content = profilerColumns[i] == HierarchyFrameDataView.columnWarningCount
                    ? EditorGUIUtility.IconContent("ProfilerColumn.WarningCount", columnName)
                    : new GUIContent(columnName);
                var column = new ProfilerFrameDataMultiColumnHeader.Column
                {
                    profilerColumn = profilerColumns[i],
                    headerLabel = content
                };
                columns[i] = column;
            }

            return columns;
        }

        public static MultiColumnHeaderState CreateDefaultMultiColumnHeaderState(ProfilerFrameDataMultiColumnHeader.Column[] columns, int defaultSortColumn)
        {
            var headerColumns = new MultiColumnHeaderState.Column[columns.Length];
            for (var i = 0; i < columns.Length; ++i)
            {
                var width = 80;
                var minWidth = 50;
                var maxWidth = 1000000f;
                var autoResize = false;
                var allowToggleVisibility = true;
                switch (columns[i].profilerColumn)
                {
                    case HierarchyFrameDataView.columnName:
                        width = 200;
                        minWidth = 200;
                        autoResize = true;
                        allowToggleVisibility = false;
                        break;
                    case HierarchyFrameDataView.columnWarningCount:
                        width = 25;
                        minWidth = 25;
                        maxWidth = 25;
                        break;
                }

                var headerColumn = new MultiColumnHeaderState.Column
                {
                    headerContent = columns[i].headerLabel,
                    headerTextAlignment = TextAlignment.Left,
                    sortingArrowAlignment = TextAlignment.Right,
                    width = width,
                    minWidth = minWidth,
                    maxWidth = maxWidth,
                    autoResize = autoResize,
                    allowToggleVisibility = allowToggleVisibility,
                    sortedAscending = i == 0
                };
                headerColumns[i] = headerColumn;
            }

            var state = new MultiColumnHeaderState(headerColumns)
            {
                sortedColumnIndex = ProfilerFrameDataMultiColumnHeader.GetMultiColumnHeaderIndex(columns, defaultSortColumn),
            };
            return state;
        }

        static string GetProfilerColumnName(int column)
        {
            switch (column)
            {
                case HierarchyFrameDataView.columnName:
                    return LocalizationDatabase.GetLocalizedString("Overview");
                case HierarchyFrameDataView.columnTotalPercent:
                    return LocalizationDatabase.GetLocalizedString("Total");
                case HierarchyFrameDataView.columnSelfPercent:
                    return LocalizationDatabase.GetLocalizedString("Self");
                case HierarchyFrameDataView.columnCalls:
                    return LocalizationDatabase.GetLocalizedString("Calls");
                case HierarchyFrameDataView.columnGcMemory:
                    return LocalizationDatabase.GetLocalizedString("GC Alloc");
                case HierarchyFrameDataView.columnTotalTime:
                    return LocalizationDatabase.GetLocalizedString("Time ms");
                case HierarchyFrameDataView.columnSelfTime:
                    return LocalizationDatabase.GetLocalizedString("Self ms");
                case HierarchyFrameDataView.columnDrawCalls:
                    return LocalizationDatabase.GetLocalizedString("DrawCalls");
                case HierarchyFrameDataView.columnTotalGpuTime:
                    return LocalizationDatabase.GetLocalizedString("GPU ms");
                case HierarchyFrameDataView.columnSelfGpuTime:
                    return LocalizationDatabase.GetLocalizedString("Self ms");
                case HierarchyFrameDataView.columnTotalGpuPercent:
                    return LocalizationDatabase.GetLocalizedString("Total");
                case HierarchyFrameDataView.columnSelfGpuPercent:
                    return LocalizationDatabase.GetLocalizedString("Self");
                case HierarchyFrameDataView.columnWarningCount:
                    return LocalizationDatabase.GetLocalizedString("|Warnings");
                case HierarchyFrameDataView.columnObjectName:
                    return LocalizationDatabase.GetLocalizedString("Name");
                case HierarchyFrameDataView.columnStartTime:
                    return LocalizationDatabase.GetLocalizedString("Start ms");
                default:
                    return "ProfilerColumn." + column;
            }
        }

        public void DoGUI(HierarchyFrameDataView frameDataView)
        {
            using (m_DoGUIMarker.Auto())
            {
                InitIfNeeded();

                var collectingSamples = ProfilerDriver.enabled && (ProfilerDriver.profileEditor || EditorApplication.isPlaying);
                var isSearchAllowed = string.IsNullOrEmpty(treeView.searchString) || !(collectingSamples && ProfilerDriver.deepProfiling);

                var isDataAvailable = frameDataView != null && frameDataView.valid;

                var showDetailedView = isDataAvailable && m_DetailedViewType != DetailedViewType.None;
                if (showDetailedView)
                    SplitterGUILayout.BeginHorizontalSplit(m_DetailedViewSpliterState);

                // Hierarchy view area
                GUILayout.BeginVertical();

                DrawToolbar(frameDataView, showDetailedView);

                if (!isDataAvailable)
                {
                    GUILayout.Label(BaseStyles.noData, BaseStyles.label);
                }
                else if (!isSearchAllowed)
                {
                    GUILayout.Label(BaseStyles.disabledSearchText, BaseStyles.label);
                }
                else
                {
                    var rect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.ExpandHeight(true), GUILayout.ExpandHeight(true));
                    m_TreeView.SetFrameDataView(frameDataView);
                    m_TreeView.OnGUI(rect);
                }

                GUILayout.EndVertical();

                if (showDetailedView)
                {
                    GUILayout.BeginVertical();

                    // Detailed view area
                    EditorGUILayout.BeginHorizontal(BaseStyles.toolbar);

                    DrawDetailedViewPopup();
                    GUILayout.FlexibleSpace();

                    DrawOptionsMenuPopup();
                    EditorGUILayout.EndHorizontal();

                    switch (m_DetailedViewType)
                    {
                        case DetailedViewType.Objects:
                            detailedObjectsView.DoGUI(BaseStyles.header, frameDataView, m_TreeView.GetSelection());
                            break;
                        case DetailedViewType.CallersAndCallees:
                            detailedCallsView.DoGUI(BaseStyles.header, frameDataView, m_TreeView.GetSelection());
                            break;
                    }

                    GUILayout.EndVertical();

                    SplitterGUILayout.EndHorizontalSplit();
                }

                HandleKeyboardEvents();
            }
        }

        void DrawSearchBar()
        {
            var rect = GUILayoutUtility.GetRect(50f, 300f, EditorGUI.kSingleLineHeight, EditorGUI.kSingleLineHeight, EditorStyles.toolbarSearchField);
            treeView.searchString = m_SearchField.OnToolbarGUI(rect, treeView.searchString);
        }

        void DrawToolbar(HierarchyFrameDataView frameDataView, bool showDetailedView)
        {
            EditorGUILayout.BeginHorizontal(BaseStyles.toolbar);

            if (frameDataView != null)
                DrawViewTypePopup((frameDataView.viewMode & HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName) != 0 ? ProfilerViewType.Hierarchy : ProfilerViewType.RawHierarchy);

            using (new EditorGUI.DisabledScope(!frameDataView.valid))
            {
                EditorGUILayout.Space(); // workaround: Remove double lines
                DrawThreadPopup(frameDataView);
            }

            GUILayout.FlexibleSpace();

            if (frameDataView != null)
                DrawCPUGPUTime(frameDataView.frameTimeMs, frameDataView.frameGpuTimeMs);

            GUILayout.FlexibleSpace();

            DrawSearchBar();

            if (!showDetailedView)
            {
                DrawDetailedViewPopup();
                EditorGUILayout.Space(); // workaround: Remove double lines
                DrawOptionsMenuPopup();
            }

            EditorGUILayout.EndHorizontal();
        }

        struct ThreadComparer : IComparer<string>
        {
            static int GetGroupOrder(string s)
            {
                if (s.StartsWith("Job"))
                    return 2;
                if (s.StartsWith("Loading"))
                    return 3;
                if (s.StartsWith("Scripting Thread"))
                    return 4;
                if (s.StartsWith(kMainThreadName))
                    return 0;
                if (s.StartsWith(kRenderThreadName))
                    return 1;
                return 10;
            }

            public int Compare(string x, string y)
            {
                int groupX = GetGroupOrder(x);
                int groupY = GetGroupOrder(y);
                if (groupX != groupY)
                    return groupX < groupY ? -1 : 1;
                return string.Compare(x, y);
            }
        }

        private void DrawThreadPopup(HierarchyFrameDataView frameDataView)
        {
            if (!frameDataView.valid)
            {
                var disabledValues = new string[] { m_ThreadName };
                EditorGUILayout.AdvancedPopup(0, disabledValues, BaseStyles.detailedViewTypeToolbarDropDown, GUILayout.Width(BaseStyles.detailedViewTypeToolbarDropDown.fixedWidth));
                return;
            }

            if (m_FrameIndex != frameDataView.frameIndex)
            {
                m_FrameIndex = frameDataView.frameIndex;
                m_ThreadIndex = 0;

                using (var frameIterator = new ProfilerFrameDataIterator())
                {
                    var threadCount = frameIterator.GetThreadCount(m_FrameIndex);
                    m_ThreadNames = new string[threadCount];
                    m_ThreadIndices = new int[threadCount];
                    for (var i = 0; i < threadCount; ++i)
                    {
                        frameIterator.SetRoot(m_FrameIndex, i);
                        var groupName = frameIterator.GetGroupName();
                        var threadName = frameIterator.GetThreadName();
                        var name = string.IsNullOrEmpty(groupName) ? threadName : groupName + "." + threadName;
                        m_ThreadNames[i] = name;
                        m_ThreadIndices[i] = i;
                        if (m_ThreadName == name)
                            m_ThreadIndex = i;
                    }

                    Array.Sort(m_ThreadNames, m_ThreadIndices, new ThreadComparer());
                    for (var i = 0; i < threadCount; ++i)
                    {
                        if (m_ThreadName == m_ThreadNames[i])
                            m_ThreadIndex = i;
                    }
                }
            }

            var newThreadIndex = EditorGUILayout.AdvancedPopup(m_ThreadIndex, m_ThreadNames, BaseStyles.detailedViewTypeToolbarDropDown, GUILayout.Width(BaseStyles.detailedViewTypeToolbarDropDown.fixedWidth));
            if (newThreadIndex != m_ThreadIndex)
            {
                m_ThreadIndex = newThreadIndex;
                m_ThreadName = m_ThreadNames[m_ThreadIndex];
            }
        }

        void DrawDetailedViewPopup()
        {
            m_DetailedViewType = (DetailedViewType)EditorGUILayout.IntPopup((int)m_DetailedViewType, kDetailedViewTypeNames, kDetailedViewTypes, BaseStyles.detailedViewTypeToolbarDropDown, GUILayout.Width(BaseStyles.detailedViewTypeToolbarDropDown.fixedWidth));
        }

        void HandleKeyboardEvents()
        {
            if (!m_TreeView.HasFocus() || !m_TreeView.HasSelection())
                return;

            var evt = Event.current;
            if (evt.type == EventType.KeyDown && (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter))
                SelectObjectsInHierarchyView();
        }

        void SelectObjectsInHierarchyView()
        {
            var instanceIds = m_TreeView.GetSelectedInstanceIds();
            if (instanceIds == null || instanceIds.Count == 0)
                return;

            var selection = new List<UnityEngine.Object>();

            foreach (int t in instanceIds)
            {
                var obj = EditorUtility.InstanceIDToObject(t);
                var com = obj as Component;
                if (com != null)
                    selection.Add(com.gameObject);
                else if (obj != null)
                    selection.Add(obj);
            }

            if (selection.Count != 0)
                Selection.objects = selection.ToArray();
        }

        void OnMainTreeViewSelectionChanged(int id)
        {
            if (selectionChanged != null)
                selectionChanged.Invoke(id);
        }

        void OnMainTreeViewSearchChanged(string newSearch)
        {
            if (searchChanged != null)
                searchChanged.Invoke(newSearch);
            if (m_TreeView.HasSelection())
            {
                var selection = m_TreeView.GetSelection();
                if (selection != null && selection.Count > 0 && selection[0] > 0)
                {
                    m_TreeView.SetSelection(selection);
                    m_TreeView.FrameItem(selection[0]);
                }
            }
        }

        void FrameItem(int id)
        {
            m_TreeView.SetFocus();
            m_TreeView.SetSelection(new List<int>(1) { id });
            m_TreeView.FrameItem(id);
        }

        public void SetSelectionFromLegacyPropertyPath(string selectedPropertyPath)
        {
            InitIfNeeded();
            m_TreeView.SetSelectionFromLegacyPropertyPath(selectedPropertyPath);
        }

        public override void Clear()
        {
            if (m_DetailedObjectsView != null)
                m_DetailedObjectsView.Clear();
            if (m_DetailedCallsView != null)
                m_DetailedCallsView.Clear();
            if (m_TreeView != null)
                m_TreeView.Clear();
        }

        public override HierarchyFrameDataView.ViewModes GetFilteringMode()
        {
            if (gpuView)
                return base.GetFilteringMode();
            return base.GetFilteringMode() | (OptionEnabled(CpuProfilerOptions.CollapseEditorBoundarySamples) ? HierarchyFrameDataView.ViewModes.HideEditorOnlySamples : 0);
        }

        void DrawOptionsMenuPopup()
        {
            var position = GUILayoutUtility.GetRect(ProfilerWindow.Styles.optionsButtonContent, EditorStyles.toolbarButton);
            if (GUI.Button(position, ProfilerWindow.Styles.optionsButtonContent, EditorStyles.toolbarButton))
            {
                var pm = new GenericMenu();
                for (int i = 0; i < k_CpuProfilerOptions.Length; i++)
                {
                    CpuProfilerOptions option = (CpuProfilerOptions)(1 << i);
                    pm.AddItem(k_CpuProfilerOptions[i], OptionEnabled(option), () => ToggleOption(option));
                }
                pm.Popup(position, -1);
            }
        }

        bool OptionEnabled(CpuProfilerOptions option)
        {
            return (option & (CpuProfilerOptions)m_CpuProfilerOptions) != CpuProfilerOptions.None;
        }

        void ToggleOption(CpuProfilerOptions option)
        {
            m_CpuProfilerOptions = (int)((CpuProfilerOptions)m_CpuProfilerOptions ^ option);
            SessionState.SetInt(k_CpuProfilerHierarchyViewOptionsPrefKey, m_CpuProfilerOptions);
            treeView.Clear();
        }
    }
}
