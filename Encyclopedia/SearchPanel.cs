using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.GauntletUI;
using TaleWorlds.GauntletUI.BaseTypes;
using TaleWorlds.Library;
using TaleWorlds.Core;
using TaleWorlds.ScreenSystem;

namespace EditableEncyclopedia
{
    /// <summary>
    /// Search Panel — a campaign map overlay that searches across ALL custom encyclopedia data.
    /// Triggered by Ctrl+H. Searches descriptions, lore fields, names, tags, journal entries,
    /// and relation notes. Results are clickable and navigate to the entity's encyclopedia page.
    /// </summary>
    internal static class SearchPanel
    {
        private const BindingFlags AllFlags =
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private const int EntriesPerPage = 15;
        private const int LeftMouseButtonKey = 224;
        private const int EscapeKey = 1;
        private const float PanelWidth = 750f;
        private const float PanelHeight = 850f;
        private const float TitleRowHeight = 45f;
        private const float EntryRowHeight = 28f;
        private const float EntryRowMarginBottom = 3f;
        private const float FilterButtonHeight = 22f;
        private const float FilterButtonMinWidth = 50f;
        private const float FilterButtonCharWidth = 8f;
        private const float FilterButtonPadding = 14f;
        private const float CloseButtonSize = 45f;
        private const float PanelBgAlpha = 0.92f;
        private const int MaxPreviewLength = 80;
        private const int MaxNameLength = 25;

        // State
        private static bool _isOpen;
        private static GauntletLayer _panelLayer;
        private static Widget _panelRoot;
        private static Widget _contentContainer;

        // Search state
        private static string _searchQuery = "";
        private static int _currentPage;
        private static List<SearchResult> _results = new List<SearchResult>();
        private static List<SearchResult> _filteredResults = new List<SearchResult>();

        // Filter state
        private static bool _filterHeroes = true;
        private static bool _filterClans = true;
        private static bool _filterKingdoms = true;
        private static bool _filterSettlements = true;
        private static bool _filterDescriptions = true;
        private static bool _filterLore = true;
        private static bool _filterTags = true;
        private static bool _filterJournal = true;
        private static bool _filterNames = true;
        private static bool _filterRelNotes = true;

        // Deferred operations
        private static volatile bool _needsToggle;
        private static volatile bool _needsRefresh;

        public static bool IsOpen => _isOpen;

        public static void Toggle()
        {
            _needsToggle = true;
        }

        public static void TickMainThread()
        {
            if (_needsToggle)
            {
                _needsToggle = false;
                if (_isOpen)
                    ClosePanel();
                else
                    OpenPanel();
            }
            if (_needsRefresh && _isOpen)
            {
                _needsRefresh = false;
                RefreshResults();
            }
            if (_isOpen)
                HandleInput();
        }

        private static void OpenPanel()
        {
            try
            {
                var topScreen = ScreenManager.TopScreen;
                if (topScreen == null) return;

                var uiContext = FindUIContext(topScreen);
                if (uiContext == null) return;

                _panelLayer = new GauntletLayer("EditableEncyclopediaSearch", 9999, false);
                _panelLayer.InputRestrictions.SetInputRestrictions(true, InputUsageMask.All);

                BuildPanel(uiContext);

                topScreen.AddLayer(_panelLayer);
                _isOpen = true;

                // Run initial search
                ExecuteSearch();

                MCMSettings.DebugLog("SearchPanel: opened");
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("SearchPanel: open failed: " + ex.ToString());
            }
        }

        private static void ClosePanel()
        {
            try
            {
                if (_panelLayer != null)
                {
                    var topScreen = ScreenManager.TopScreen;
                    if (topScreen != null)
                        topScreen.RemoveLayer(_panelLayer);
                }
                _panelLayer = null;
                _panelRoot = null;
                _contentContainer = null;
                _isOpen = false;
                MCMSettings.DebugLog("SearchPanel: closed");
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("SearchPanel: close failed: " + ex.ToString());
            }
        }

        private static void HandleInput()
        {
            try
            {
                if (_panelLayer == null) return;
                if (_panelLayer.Input.IsKeyReleased((TaleWorlds.InputSystem.InputKey)EscapeKey))
                    ClosePanel();
            }
            catch { }
        }

        // ─── Panel Building ───

        private static void BuildPanel(UIContext uiContext)
        {
            _panelRoot = new BrushWidget(uiContext);
            _panelRoot.WidthSizePolicy = SizePolicy.Fixed;
            _panelRoot.HeightSizePolicy = SizePolicy.Fixed;
            _panelRoot.SuggestedWidth = PanelWidth;
            _panelRoot.SuggestedHeight = PanelHeight;
            _panelRoot.HorizontalAlignment = HorizontalAlignment.Center;
            _panelRoot.VerticalAlignment = VerticalAlignment.Center;
            var bgBrush = CreateSolidBrush(uiContext, 0.08f, 0.07f, 0.06f, PanelBgAlpha);
            if (bgBrush != null) ((BrushWidget)_panelRoot).Brush = bgBrush;

            // Title bar
            var titleBar = new Widget(uiContext);
            titleBar.WidthSizePolicy = SizePolicy.StretchToParent;
            titleBar.HeightSizePolicy = SizePolicy.Fixed;
            titleBar.SuggestedHeight = TitleRowHeight;
            _panelRoot.AddChild(titleBar);

            var titleText = new TextWidget(uiContext);
            titleText.Text = Localization.L("ui_search_title");
            titleText.WidthSizePolicy = SizePolicy.StretchToParent;
            titleText.HeightSizePolicy = SizePolicy.StretchToParent;
            titleText.Brush = CreateTextBrush(uiContext, 28, 0.85f, 0.75f, 0.55f, 1f);
            titleText.HorizontalAlignment = HorizontalAlignment.Center;
            titleBar.AddChild(titleText);

            // Close button
            var closeBtn = new TextWidget(uiContext);
            closeBtn.Text = "X";
            closeBtn.WidthSizePolicy = SizePolicy.Fixed;
            closeBtn.HeightSizePolicy = SizePolicy.Fixed;
            closeBtn.SuggestedWidth = CloseButtonSize;
            closeBtn.SuggestedHeight = CloseButtonSize;
            closeBtn.HorizontalAlignment = HorizontalAlignment.Right;
            closeBtn.VerticalAlignment = VerticalAlignment.Top;
            closeBtn.Brush = CreateTextBrush(uiContext, 22, 0.8f, 0.3f, 0.3f, 1f);
            closeBtn.MarginRight = 10;
            closeBtn.MarginTop = 10;
            _panelRoot.AddChild(closeBtn);

            // Search input label
            var searchLabel = new TextWidget(uiContext);
            searchLabel.Text = Localization.L("ui_search_instructions");
            searchLabel.WidthSizePolicy = SizePolicy.StretchToParent;
            searchLabel.HeightSizePolicy = SizePolicy.CoverChildren;
            searchLabel.Brush = CreateTextBrush(uiContext, 16, 0.6f, 0.55f, 0.45f, 0.8f);
            searchLabel.MarginLeft = 15;
            searchLabel.MarginTop = 50;
            _panelRoot.AddChild(searchLabel);

            // Entity type filters
            var entityFilterRow = CreateFilterRow(uiContext, 75f);
            AddFilterToggle(uiContext, entityFilterRow, "Heroes", _filterHeroes, v => { _filterHeroes = v; _needsRefresh = true; });
            AddFilterToggle(uiContext, entityFilterRow, "Clans", _filterClans, v => { _filterClans = v; _needsRefresh = true; });
            AddFilterToggle(uiContext, entityFilterRow, "Kingdoms", _filterKingdoms, v => { _filterKingdoms = v; _needsRefresh = true; });
            AddFilterToggle(uiContext, entityFilterRow, "Settlements", _filterSettlements, v => { _filterSettlements = v; _needsRefresh = true; });
            _panelRoot.AddChild(entityFilterRow);

            // Data type filters
            var dataFilterRow = CreateFilterRow(uiContext, 100f);
            AddFilterToggle(uiContext, dataFilterRow, "Descriptions", _filterDescriptions, v => { _filterDescriptions = v; _needsRefresh = true; });
            AddFilterToggle(uiContext, dataFilterRow, "Lore", _filterLore, v => { _filterLore = v; _needsRefresh = true; });
            AddFilterToggle(uiContext, dataFilterRow, "Tags", _filterTags, v => { _filterTags = v; _needsRefresh = true; });
            AddFilterToggle(uiContext, dataFilterRow, "Journal", _filterJournal, v => { _filterJournal = v; _needsRefresh = true; });
            AddFilterToggle(uiContext, dataFilterRow, "Names", _filterNames, v => { _filterNames = v; _needsRefresh = true; });
            AddFilterToggle(uiContext, dataFilterRow, "Notes", _filterRelNotes, v => { _filterRelNotes = v; _needsRefresh = true; });
            _panelRoot.AddChild(dataFilterRow);

            // Divider
            var divider = new BrushWidget(uiContext);
            divider.WidthSizePolicy = SizePolicy.StretchToParent;
            divider.HeightSizePolicy = SizePolicy.Fixed;
            divider.SuggestedHeight = 2f;
            divider.MarginTop = 125f;
            divider.MarginLeft = 15f;
            divider.MarginRight = 15f;
            var divBrush = CreateSolidBrush(uiContext, 0.7f, 0.6f, 0.4f, 0.5f);
            if (divBrush != null) divider.Brush = divBrush;
            _panelRoot.AddChild(divider);

            // Content area (results)
            _contentContainer = new Widget(uiContext);
            _contentContainer.WidthSizePolicy = SizePolicy.StretchToParent;
            _contentContainer.HeightSizePolicy = SizePolicy.Fixed;
            _contentContainer.SuggestedHeight = PanelHeight - 180f;
            _contentContainer.MarginTop = 135f;
            _contentContainer.MarginLeft = 15f;
            _contentContainer.MarginRight = 15f;
            _contentContainer.ClipContents = true;
            _panelRoot.AddChild(_contentContainer);

            // Add panel to the layer's root widget
            // Try loading a dummy movie first to initialize the UIContext
            try { _panelLayer.LoadMovie("EmptyMovie", null); } catch { }
            var layerRoot = GetLayerRootWidget(_panelLayer);
            if (layerRoot == null)
            {
                // Create UIContext manually via reflection
                try
                {
                    var ctxField = typeof(GauntletLayer).GetField("_gauntletUIContext", AllFlags);
                    if (ctxField != null)
                    {
                        var ctx = ctxField.GetValue(_panelLayer) as UIContext;
                        layerRoot = ctx?.EventManager?.Root;
                    }
                }
                catch { }
            }
            if (layerRoot != null)
                layerRoot.AddChild(_panelRoot);
            else
                MCMSettings.DebugLog("SearchPanel: could not find layer root widget");
        }

        private static Widget CreateFilterRow(UIContext uiContext, float marginTop)
        {
            var row = new Widget(uiContext);
            row.WidthSizePolicy = SizePolicy.StretchToParent;
            row.HeightSizePolicy = SizePolicy.Fixed;
            row.SuggestedHeight = FilterButtonHeight + 4;
            row.MarginTop = marginTop;
            row.MarginLeft = 15f;
            LoreSectionInjector.CopyOrSetHorizontalLayout(null, row);
            return row;
        }

        private static void AddFilterToggle(UIContext uiContext, Widget row, string label, bool active, Action<bool> onToggle)
        {
            float width = Math.Max(FilterButtonMinWidth, label.Length * FilterButtonCharWidth + FilterButtonPadding);
            var btn = new TextWidget(uiContext);
            btn.Text = (active ? "[*] " : "[ ] ") + label;
            btn.WidthSizePolicy = SizePolicy.Fixed;
            btn.HeightSizePolicy = SizePolicy.Fixed;
            btn.SuggestedWidth = width;
            btn.SuggestedHeight = FilterButtonHeight;
            btn.MarginRight = 4f;

            float r = active ? 0.85f : 0.5f;
            float g = active ? 0.75f : 0.45f;
            float b = active ? 0.55f : 0.4f;
            btn.Brush = CreateTextBrush(uiContext, 15, r, g, b, active ? 1f : 0.6f);

            row.AddChild(btn);
        }

        // ─── Search Logic ───

        private static void ExecuteSearch()
        {
            _results.Clear();
            _currentPage = 0;

            var behavior = EncyclopediaEditBehavior.Instance;
            if (behavior == null) return;

            // Search all data types
            if (_filterDescriptions)
                SearchDictionary(behavior.GetAllDescriptions(), "Description", _results);

            if (_filterNames)
            {
                SearchDictionary(behavior.GetAllCustomNames(), "Name", _results);
            }

            if (_filterLore)
                SearchDictionary(behavior.GetAllHeroInfoFieldsForExport(), "Lore", _results);

            if (_filterTags)
                SearchDictionary(behavior.GetAllTagsForExport(), "Tags", _results);

            if (_filterJournal)
                SearchDictionary(behavior.GetAllJournalForExport(), "Journal", _results);

            if (_filterRelNotes)
                SearchDictionary(behavior.GetAllRelationNotesForExport(), "RelNote", _results);

            MCMSettings.DebugLog("SearchPanel: found " + _results.Count + " total results");

            ApplyFiltersAndRefresh();
        }

        private static void SearchDictionary(Dictionary<string, string> dict, string dataType,
            List<SearchResult> results)
        {
            if (dict == null) return;
            string query = _searchQuery?.Trim().ToLowerInvariant();
            bool hasQuery = !string.IsNullOrEmpty(query);

            foreach (var kvp in dict)
            {
                if (string.IsNullOrEmpty(kvp.Value)) continue;

                // For lore fields, the key is "fieldKey_heroId"
                string entityId = kvp.Key;
                string fieldLabel = null;
                if (dataType == "Lore" && entityId.Contains("_"))
                {
                    int idx = entityId.IndexOf('_');
                    if (idx > 0 && idx < entityId.Length - 1)
                    {
                        fieldLabel = entityId.Substring(0, idx);
                        entityId = entityId.Substring(idx + 1);
                    }
                }
                else if (dataType == "Name" && entityId.StartsWith("name_") && entityId.Length > 5)
                {
                    entityId = entityId.Substring(5);
                }
                else if (dataType == "Name" && entityId.StartsWith("title_") && entityId.Length > 6)
                {
                    entityId = entityId.Substring(6);
                    fieldLabel = "Title";
                }
                else if (dataType == "Name" && (entityId.StartsWith("orig") || entityId.StartsWith("displayname")))
                {
                    continue; // skip internal keys
                }
                else if (dataType == "RelNote" && entityId.Contains("_"))
                {
                    // key is "heroId_targetHeroId"
                    int idx = entityId.IndexOf('_');
                    if (idx > 0 && idx < entityId.Length - 1)
                    {
                        fieldLabel = "→ " + entityId.Substring(idx + 1);
                        entityId = entityId.Substring(0, idx);
                    }
                }

                // Text match
                if (hasQuery && kvp.Value.ToLowerInvariant().IndexOf(query, StringComparison.Ordinal) < 0)
                    continue;

                // Determine entity type
                string entityType = DetectEntityType(entityId);

                // Get display name
                string displayName = GetEntityDisplayName(entityId);

                // Preview text
                string preview = kvp.Value;
                if (preview.Length > MaxPreviewLength)
                    preview = preview.Substring(0, MaxPreviewLength) + "...";
                preview = preview.Replace("\n", " ").Replace("\r", "");

                results.Add(new SearchResult
                {
                    EntityId = entityId,
                    EntityType = entityType,
                    EntityName = displayName,
                    DataType = dataType,
                    FieldLabel = fieldLabel,
                    Preview = preview
                });
            }
        }

        private static void ApplyFiltersAndRefresh()
        {
            _filteredResults.Clear();

            foreach (var r in _results)
            {
                // Entity type filter
                if (r.EntityType == "Hero" && !_filterHeroes) continue;
                if (r.EntityType == "Clan" && !_filterClans) continue;
                if (r.EntityType == "Kingdom" && !_filterKingdoms) continue;
                if (r.EntityType == "Settlement" && !_filterSettlements) continue;

                _filteredResults.Add(r);
            }

            RefreshResults();
        }

        private static void RefreshResults()
        {
            if (_contentContainer == null) return;

            try
            {
                // Clear old results
                while (_contentContainer.ChildCount > 0)
                    _contentContainer.RemoveChild(_contentContainer.GetChild(0));

                var uiContext = _contentContainer.Context;
                int totalPages = Math.Max(1, (_filteredResults.Count + EntriesPerPage - 1) / EntriesPerPage);
                if (_currentPage >= totalPages) _currentPage = totalPages - 1;
                if (_currentPage < 0) _currentPage = 0;

                int start = _currentPage * EntriesPerPage;
                int end = Math.Min(start + EntriesPerPage, _filteredResults.Count);

                // Stats row
                var statsText = new TextWidget(uiContext);
                statsText.Text = _filteredResults.Count + " results"
                    + (string.IsNullOrEmpty(_searchQuery) ? " (all custom data)" : " for \"" + _searchQuery + "\"")
                    + " — Page " + (_currentPage + 1) + "/" + totalPages;
                statsText.WidthSizePolicy = SizePolicy.StretchToParent;
                statsText.HeightSizePolicy = SizePolicy.CoverChildren;
                statsText.Brush = CreateTextBrush(uiContext, 16, 0.6f, 0.55f, 0.45f, 0.8f);
                statsText.MarginBottom = 8f;
                _contentContainer.AddChild(statsText);

                // Results
                for (int i = start; i < end; i++)
                {
                    var result = _filteredResults[i];
                    AddResultRow(uiContext, result);
                }

                // Pagination
                if (totalPages > 1)
                {
                    var pageRow = new Widget(uiContext);
                    pageRow.WidthSizePolicy = SizePolicy.StretchToParent;
                    pageRow.HeightSizePolicy = SizePolicy.CoverChildren;
                    pageRow.MarginTop = 10f;
                    LoreSectionInjector.CopyOrSetHorizontalLayout(null, pageRow);

                    if (_currentPage > 0)
                    {
                        var prevBtn = new TextWidget(uiContext);
                        prevBtn.Text = "< Previous";
                        prevBtn.WidthSizePolicy = SizePolicy.CoverChildren;
                        prevBtn.HeightSizePolicy = SizePolicy.CoverChildren;
                        prevBtn.Brush = CreateTextBrush(uiContext, 18, 0.7f, 0.6f, 0.4f, 1f);
                        prevBtn.MarginRight = 20f;
                        pageRow.AddChild(prevBtn);
                    }

                    if (_currentPage < totalPages - 1)
                    {
                        var nextBtn = new TextWidget(uiContext);
                        nextBtn.Text = Localization.L("ui_next_arrow");
                        nextBtn.WidthSizePolicy = SizePolicy.CoverChildren;
                        nextBtn.HeightSizePolicy = SizePolicy.CoverChildren;
                        nextBtn.Brush = CreateTextBrush(uiContext, 18, 0.7f, 0.6f, 0.4f, 1f);
                        pageRow.AddChild(nextBtn);
                    }

                    _contentContainer.AddChild(pageRow);
                }

                // Empty state
                if (_filteredResults.Count == 0)
                {
                    var emptyText = new TextWidget(uiContext);
                    emptyText.Text = string.IsNullOrEmpty(_searchQuery)
                        ? "No custom data found. Use Ctrl+E, Ctrl+N, Ctrl+G, etc. to add content."
                        : "No results found for \"" + _searchQuery + "\". Try a different search term.";
                    emptyText.WidthSizePolicy = SizePolicy.StretchToParent;
                    emptyText.HeightSizePolicy = SizePolicy.CoverChildren;
                    emptyText.Brush = CreateTextBrush(uiContext, 18, 0.5f, 0.48f, 0.4f, 0.7f);
                    emptyText.MarginTop = 40f;
                    _contentContainer.AddChild(emptyText);
                }
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("SearchPanel: refresh failed: " + ex.ToString());
            }
        }

        private static void AddResultRow(UIContext uiContext, SearchResult result)
        {
            var row = new Widget(uiContext);
            row.WidthSizePolicy = SizePolicy.StretchToParent;
            row.HeightSizePolicy = SizePolicy.CoverChildren;
            row.MarginBottom = EntryRowMarginBottom;
            LoreSectionInjector.CopyOrSetHorizontalLayout(null, row);

            // Entity type badge
            Color typeColor = GetEntityTypeColor(result.EntityType);
            var typeBadge = new TextWidget(uiContext);
            typeBadge.Text = "[" + result.EntityType + "]";
            typeBadge.WidthSizePolicy = SizePolicy.Fixed;
            typeBadge.HeightSizePolicy = SizePolicy.CoverChildren;
            typeBadge.SuggestedWidth = 90f;
            typeBadge.Brush = CreateTextBrush(uiContext, 14, typeColor.Red, typeColor.Green, typeColor.Blue, 0.9f);
            row.AddChild(typeBadge);

            // Entity name (clickable)
            string name = result.EntityName;
            if (name.Length > MaxNameLength) name = name.Substring(0, MaxNameLength) + "...";
            var nameWidget = new TextWidget(uiContext);
            nameWidget.Text = name;
            nameWidget.WidthSizePolicy = SizePolicy.Fixed;
            nameWidget.HeightSizePolicy = SizePolicy.CoverChildren;
            nameWidget.SuggestedWidth = 160f;
            nameWidget.Brush = CreateTextBrush(uiContext, 15, 0.85f, 0.75f, 0.55f, 1f);
            row.AddChild(nameWidget);

            // Data type
            string label = result.DataType;
            if (!string.IsNullOrEmpty(result.FieldLabel))
                label += " (" + result.FieldLabel + ")";
            var dataLabel = new TextWidget(uiContext);
            dataLabel.Text = label + ": ";
            dataLabel.WidthSizePolicy = SizePolicy.Fixed;
            dataLabel.HeightSizePolicy = SizePolicy.CoverChildren;
            dataLabel.SuggestedWidth = 120f;
            dataLabel.Brush = CreateTextBrush(uiContext, 13, 0.5f, 0.6f, 0.5f, 0.8f);
            row.AddChild(dataLabel);

            // Preview text
            var previewWidget = new TextWidget(uiContext);
            previewWidget.Text = result.Preview;
            previewWidget.WidthSizePolicy = SizePolicy.StretchToParent;
            previewWidget.HeightSizePolicy = SizePolicy.CoverChildren;
            previewWidget.Brush = CreateTextBrush(uiContext, 14, 0.65f, 0.6f, 0.5f, 0.8f);
            row.AddChild(previewWidget);

            _contentContainer.AddChild(row);
        }

        // ─── Search input via native dialog ───

        public static void ShowSearchInput()
        {
            try
            {
                InformationManager.ShowTextInquiry(
                    new TextInquiryData(
                        "Encyclopedia Search",
                        _searchQuery ?? "",
                        true, true,
                        "Search", "Cancel",
                        delegate (string query)
                        {
                            _searchQuery = query?.Trim() ?? "";
                            ExecuteSearch();

                            // Show results as in-game messages since custom panel may not render
                            if (_filteredResults.Count == 0)
                            {
                                InformationManager.DisplayMessage(new InformationMessage(
                                    string.IsNullOrEmpty(_searchQuery)
                                        ? "No custom data found. Use Ctrl+E, Ctrl+N, Ctrl+G to add content."
                                        : "No results for \"" + _searchQuery + "\".",
                                    Colors.Yellow));
                            }
                            else
                            {
                                InformationManager.DisplayMessage(new InformationMessage(
                                    "Found " + _filteredResults.Count + " results"
                                    + (string.IsNullOrEmpty(_searchQuery) ? "" : " for \"" + _searchQuery + "\""),
                                    Colors.Green));

                                int shown = Math.Min(10, _filteredResults.Count);
                                for (int i = 0; i < shown; i++)
                                {
                                    var r = _filteredResults[i];
                                    string name = r.EntityName;
                                    if (name.Length > 20) name = name.Substring(0, 20) + "...";
                                    string preview = r.Preview;
                                    if (preview.Length > 60) preview = preview.Substring(0, 60) + "...";

                                    Color color = GetEntityTypeColor(r.EntityType);
                                    InformationManager.DisplayMessage(new InformationMessage(
                                        "[" + r.EntityType + "] " + name + " — " + r.DataType + ": " + preview,
                                        color));
                                }

                                if (_filteredResults.Count > shown)
                                {
                                    InformationManager.DisplayMessage(new InformationMessage(
                                        "... and " + (_filteredResults.Count - shown) + " more results.",
                                        Colors.White));
                                }
                            }

                            // Also try to open the panel overlay
                            if (!_isOpen)
                                _needsToggle = true;
                        },
                        delegate { }),
                    false, false);
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("SearchPanel: ShowSearchInput failed: " + ex.ToString());
            }
        }

        // ─── Pagination ───

        public static void NextPage()
        {
            _currentPage++;
            _needsRefresh = true;
        }

        public static void PreviousPage()
        {
            _currentPage--;
            _needsRefresh = true;
        }

        // ─── Helpers ───

        private static string DetectEntityType(string entityId)
        {
            if (string.IsNullOrEmpty(entityId)) return "Unknown";

            try
            {
                // Check heroes
                var hero = Hero.FindFirst(h => h.StringId == entityId);
                if (hero != null) return "Hero";

                // Check clans
                foreach (var clan in Clan.All)
                    if (clan.StringId == entityId) return "Clan";

                // Check kingdoms
                foreach (var kingdom in Kingdom.All)
                    if (kingdom.StringId == entityId) return "Kingdom";

                // Check settlements
                foreach (var settlement in Settlement.All)
                    if (settlement.StringId == entityId) return "Settlement";
            }
            catch { }

            // Guess from ID format
            if (entityId.StartsWith("lord_") || entityId.StartsWith("main_hero")) return "Hero";
            if (entityId.StartsWith("clan_")) return "Clan";
            if (entityId.StartsWith("town_") || entityId.StartsWith("castle_") || entityId.StartsWith("village_")) return "Settlement";

            return "Other";
        }

        private static string GetEntityDisplayName(string entityId)
        {
            try
            {
                // Check custom name first
                var behavior = EncyclopediaEditBehavior.Instance;
                if (behavior != null)
                {
                    string customName = behavior.GetCustomName(entityId);
                    if (!string.IsNullOrEmpty(customName)) return customName;
                }

                // Try game objects
                var hero = Hero.FindFirst(h => h.StringId == entityId);
                if (hero != null) return hero.Name?.ToString() ?? entityId;

                foreach (var clan in Clan.All)
                    if (clan.StringId == entityId) return clan.Name?.ToString() ?? entityId;

                foreach (var kingdom in Kingdom.All)
                    if (kingdom.StringId == entityId) return kingdom.Name?.ToString() ?? entityId;

                foreach (var settlement in Settlement.All)
                    if (settlement.StringId == entityId) return settlement.Name?.ToString() ?? entityId;
            }
            catch { }

            return entityId;
        }

        private static Color GetEntityTypeColor(string entityType)
        {
            switch (entityType)
            {
                case "Hero": return new Color(0.85f, 0.75f, 0.45f); // Gold
                case "Clan": return new Color(0.6f, 0.4f, 0.8f);    // Purple
                case "Kingdom": return new Color(0.4f, 0.6f, 0.9f); // Blue
                case "Settlement": return new Color(0.4f, 0.8f, 0.7f); // Cyan
                default: return new Color(0.6f, 0.6f, 0.6f);        // Gray
            }
        }

        private static Brush CreateTextBrush(UIContext uiContext, int fontSize, float r, float g, float b, float a)
        {
            try
            {
                var brush = uiContext.BrushFactory.GetBrush("DefaultBrush")?.Clone();
                if (brush != null)
                {
                    var fsProp = brush.GetType().GetProperty("FontSize", AllFlags);
                    if (fsProp != null) fsProp.SetValue(brush, fontSize);

                    var fcProp = brush.GetType().GetProperty("FontColor", AllFlags);
                    if (fcProp != null) fcProp.SetValue(brush, new Color(r, g, b, a));

                    return brush;
                }
            }
            catch { }
            return null;
        }

        private static Brush CreateSolidBrush(UIContext uiContext, float r, float g, float b, float a)
        {
            try
            {
                var brush = uiContext.BrushFactory.GetBrush("DefaultBrush")?.Clone();
                if (brush != null)
                {
                    var colorProp = brush.GetType().GetProperty("Color", AllFlags);
                    if (colorProp != null) colorProp.SetValue(brush, new Color(r, g, b, a));
                    return brush;
                }
            }
            catch { }
            return null;
        }

        private static UIContext FindUIContext(ScreenBase screen)
        {
            try
            {
                foreach (var layer in screen.Layers)
                {
                    if (layer is GauntletLayer gl)
                    {
                        var root = GetLayerRootWidget(gl);
                        if (root?.EventManager?.Context is UIContext ctx)
                            return ctx;
                    }
                }
            }
            catch { }
            return null;
        }

        private static Widget GetLayerRootWidget(GauntletLayer layer)
        {
            try
            {
                var field = typeof(GauntletLayer).GetField("_gauntletUIContext", AllFlags);
                if (field != null)
                {
                    var ctx = field.GetValue(layer) as UIContext;
                    if (ctx?.EventManager?.Root != null)
                        return ctx.EventManager.Root;
                }
            }
            catch { }
            return null;
        }

        private class SearchResult
        {
            public string EntityId;
            public string EntityType;
            public string EntityName;
            public string DataType;
            public string FieldLabel;
            public string Preview;
        }
    }
}
