using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;

namespace EditableEncyclopedia
{
    /// <summary>
    /// Handles Ctrl+F relation note hero picker and inline relation annotations.
    /// Notes are stored in EncyclopediaEditBehavior._relationNotes.
    /// </summary>
    internal static class RelationNotesInjector
    {
        private const int MaxNotePreviewLength = 120;
        private const int MaxInlineNotePreviewLength = 80;
        private const int InlineNoteEllipsisOffset = 3;
        private const int MaxSearchResults = 50;
        private const int SearchInputMaxLength = 100;
        private const int MinMultiSelCtorParams = 8;

        private static readonly BindingFlags AllFlags =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        // Popup-open flag: prevents TickAutoUnblock from removing the blocking layer
        // while the hero picker or native note editor dialog is visible.
        // Must be checked by EncyclopediaInputBlocker.AnyPopupOpen().
        private static volatile bool _popupOpen = false;
        public static bool IsPopupOpen => _popupOpen;

        private static void ReleasePopup()
        {
            _popupOpen = false;
            EncyclopediaInputBlocker.Unblock();
        }

        // State for pending hero picker (deferred to main thread)
        private static bool _pickerPending;
        private static string _pickerHeroId;

        /// <summary>
        /// Must be called from OnApplicationTick. Processes deferred hero picker display.
        /// </summary>
        public static void TickMainThread()
        {
            if (_pickerPending)
            {
                _pickerPending = false;
                ShowHeroPicker(_pickerHeroId);
            }
        }

        /// <summary>
        /// Schedules the hero picker to show on the main thread.
        /// Called from the key poller (timer thread) when Ctrl+F is pressed on a hero page.
        /// </summary>
        public static void ScheduleHeroPicker(string heroId)
        {
            _pickerPending = true;
            _pickerHeroId = heroId;
        }

        /// <summary>
        /// Shows a native multi-selection inquiry listing all alive heroes.
        /// Heroes with existing notes are marked with "* " prefix and sorted first.
        /// Uses the game's built-in searchable list UI (isSearchAvailable=true).
        /// </summary>
        private static void ShowHeroPicker(string heroId)
        {
            try
            {
                if (EncyclopediaEditBehavior.Instance == null) return;

                Hero currentHero = Hero.AllAliveHeroes.FirstOrDefault(h => h.StringId == heroId);
                if (currentHero == null)
                {
                    MCMSettings.DebugLog("RelationNotes: hero picker - hero not found: " + heroId);
                    return;
                }

                // Find InquiryElement and MultiSelectionInquiryData types
                Type inquiryElementType = null;
                Type multiSelDataType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        foreach (var t in asm.GetTypes())
                        {
                            if (t.Name == "InquiryElement" && inquiryElementType == null)
                                inquiryElementType = t;
                            if (t.Name == "MultiSelectionInquiryData" && multiSelDataType == null)
                                multiSelDataType = t;
                        }
                    }
                    catch (Exception ex) { MCMSettings.DebugLog("[RelationNotesInjector]: " + ex.ToString()); }
                }

                if (inquiryElementType == null || multiSelDataType == null)
                {
                    MCMSettings.DebugLog("RelationNotes: MultiSelection types not found");
                    return;
                }

                var showMethod = MultiSelectionMethodCache.Find(multiSelDataType);
                if (showMethod == null)
                {
                    MCMSettings.DebugLog("RelationNotes: ShowMultiSelectionInquiry not found");
                    return;
                }

                var elementCtor = MultiSelectionMethodCache.FindInquiryElementCtor(inquiryElementType, out bool secondParamIsTextObject);
                if (elementCtor == null) return;

                var elementCtorParams = elementCtor.GetParameters();
                var listType = typeof(List<>).MakeGenericType(inquiryElementType);
                var elements = (System.Collections.IList)Activator.CreateInstance(listType);

                // Build list of all alive heroes (excluding current)
                var heroes = new List<(string id, string name, bool hasNote)>();
                foreach (var h in Hero.AllAliveHeroes)
                {
                    if (h == null || h.StringId == heroId) continue;
                    string name = h.Name?.ToString();
                    if (string.IsNullOrEmpty(name)) continue;

                    bool hasNote = EncyclopediaEditBehavior.Instance.HasRelationNote(heroId, h.StringId);
                    heroes.Add((h.StringId, name, hasNote));
                }

                // Sort: heroes with notes first, then alphabetically
                heroes.Sort((a, b) =>
                {
                    if (a.hasNote != b.hasNote) return a.hasNote ? -1 : 1;
                    return string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase);
                });

                int noteCount = heroes.Count(h => h.hasNote);

                // Build InquiryElement list
                foreach (var (id, name, hasNote) in heroes)
                {
                    string label = hasNote ? "* " + name : name;
                    elements.Add(MultiSelectionMethodCache.CreateInquiryElement(
                        elementCtor, elementCtorParams, secondParamIsTextObject,
                        id, label));
                }

                // Build callbacks using expression trees (required for generic Action<List<InquiryElement>>)
                var actionType = typeof(Action<>).MakeGenericType(listType);

                var handler = new HeroPickerHandler(heroId);
                var onSelectedMethod = typeof(HeroPickerHandler).GetMethod("OnSelected");
                var onCancelMethod = typeof(HeroPickerHandler).GetMethod("OnCancel");
                if (onSelectedMethod == null || onCancelMethod == null)
                {
                    MCMSettings.DebugLog("[RelationNotesInjector]: GetMethod for HeroPickerHandler returned null");
                    return;
                }
                var affParam = System.Linq.Expressions.Expression.Parameter(listType, "elements");
                var affCall = System.Linq.Expressions.Expression.Call(
                    System.Linq.Expressions.Expression.Constant(handler),
                    onSelectedMethod,
                    System.Linq.Expressions.Expression.Convert(affParam, typeof(object)));
                var affirmativeDelegate = System.Linq.Expressions.Expression.Lambda(actionType, affCall, affParam).Compile();

                var negParam = System.Linq.Expressions.Expression.Parameter(listType, "elements");
                var negCall = System.Linq.Expressions.Expression.Call(
                    System.Linq.Expressions.Expression.Constant(handler),
                    onCancelMethod,
                    System.Linq.Expressions.Expression.Convert(negParam, typeof(object)));
                var negativeDelegate = System.Linq.Expressions.Expression.Lambda(actionType, negCall, negParam).Compile();

                // Find the largest constructor for MultiSelectionInquiryData
                System.Reflection.ConstructorInfo dataCtor = null;
                foreach (var ctor in multiSelDataType.GetConstructors())
                {
                    var pars = ctor.GetParameters();
                    if (pars.Length >= MinMultiSelCtorParams && (dataCtor == null || pars.Length > dataCtor.GetParameters().Length))
                        dataCtor = ctor;
                }

                if (dataCtor == null)
                {
                    MCMSettings.DebugLog("RelationNotes: MultiSelectionInquiryData ctor not found");
                    return;
                }

                string title = Localization.L("relation_picker_search_title",
                    currentHero.Name?.ToString() ?? heroId);
                string desc = noteCount > 0
                    ? Localization.L("relation_picker_note_count", noteCount.ToString())
                    : "";

                object dataObj = MultiSelectionMethodCache.BuildMultiSelectionInquiryData(
                    dataCtor, title, desc,
                    Localization.L("edit_save"), Localization.L("edit_cancel"),
                    elements, listType, affirmativeDelegate, negativeDelegate);

                // Block encyclopedia input while the hero picker is open.
                // This prevents navigation (backspace, clicking links, etc.)
                // while the user is selecting a hero from the list.
                // Block() is placed here (after all validation) so early returns
                // don't leave the encyclopedia permanently blocked.
                // _popupOpen prevents TickAutoUnblock from removing the layer
                // while the native picker dialog is visible.
                EncyclopediaInputBlocker.Block();
                _popupOpen = true;

                var showParams = showMethod.GetParameters();
                if (showParams.Length == 1)
                    showMethod.Invoke(null, new object[] { dataObj });
                else if (showParams.Length == 2)
                    showMethod.Invoke(null, new object[] { dataObj, false });
                else
                    showMethod.Invoke(null, new object[] { dataObj, false, false });

                MCMSettings.DebugLog("RelationNotes: showing hero list for " + heroId
                    + " (" + heroes.Count + " heroes, " + noteCount + " with notes)");

                // MultiSelectionInquiry may cause encyclopedia to navigate to Home
                EncyclopediaNavigationGuard.ScheduleNavigateBack();
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("RelationNotes: ShowHeroPicker error: " + ex.ToString());
                // Safety: if Block() was called but picker failed, release
                if (_popupOpen) ReleasePopup();
            }
        }

        /// <summary>
        /// Handler for the native hero picker multi-selection callback.
        /// </summary>
        private class HeroPickerHandler
        {
            private readonly string _heroId;
            public HeroPickerHandler(string heroId) { _heroId = heroId; }

            public void OnSelected(object elementsObj)
            {
                try
                {
                    // Release the picker's popup state — ShowNotePopup will set its own
                    ReleasePopup();

                    var list = elementsObj as System.Collections.IList;
                    if (list == null || list.Count == 0) return;

                    string selectedId = MultiSelectionMethodCache.GetInquiryElementId(list[0]);
                    if (string.IsNullOrEmpty(selectedId)) return;

                    // Resolve hero name for the note popup title
                    string targetName = selectedId;
                    foreach (var h in Hero.AllAliveHeroes)
                    {
                        if (h?.StringId == selectedId)
                        {
                            targetName = h.Name?.ToString() ?? selectedId;
                            break;
                        }
                    }

                    MCMSettings.DebugLog("RelationNotes: selected hero " + targetName + " (" + selectedId + ")");
                    ShowNotePopup(_heroId, selectedId, targetName);
                }
                catch (Exception ex)
                {
                    MCMSettings.DebugLog("RelationNotes: HeroPickerHandler.OnSelected error: " + ex.ToString());
                }
            }

            public void OnCancel(object elementsObj)
            {
                ReleasePopup();
                MCMSettings.DebugLog("RelationNotes: hero picker cancelled");
            }
        }

        /// <summary>
        /// Shows a selection popup with heroes matching the search text.
        /// </summary>
        private static void ShowFilteredHeroPicker(string heroId, Hero currentHero, string searchText)
        {
            try
            {
                string searchLower = searchText.ToLowerInvariant();
                var matches = new List<(string id, string name, bool hasNote)>();

                // Check all alive heroes for name match
                foreach (var h in Hero.AllAliveHeroes)
                {
                    if (h == null || h.StringId == heroId) continue;
                    string name = h.Name?.ToString();
                    if (string.IsNullOrEmpty(name)) continue;

                    if (name.ToLowerInvariant().Contains(searchLower))
                    {
                        bool hasNote = EncyclopediaEditBehavior.Instance.HasRelationNote(heroId, h.StringId);
                        matches.Add((h.StringId, name, hasNote));
                    }
                }

                // Sort: notes first, then alphabetically
                matches.Sort((a, b) =>
                {
                    if (a.hasNote != b.hasNote) return a.hasNote ? -1 : 1;
                    return string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase);
                });

                MCMSettings.DebugLog("RelationNotes: search '" + searchText + "' found " + matches.Count + " matches");

                if (matches.Count == 0)
                {
                    // No matches — re-show the search input
                    MCMSettings.DebugLog("RelationNotes: no matches for '" + searchText + "'");
                    string title = Localization.L("relation_picker_no_match", searchText);
                    EncyclopediaInputBlocker.Block();
                    _popupOpen = true;
                    try
                    {
                        EditPopupInjector.ScheduleShow(title, searchText, SearchInputMaxLength,
                            onConfirm: (string newSearch) =>
                            {
                                ReleasePopup();
                                if (!string.IsNullOrWhiteSpace(newSearch))
                                    ShowFilteredHeroPicker(heroId, currentHero, newSearch.Trim());
                            },
                            onCancel: () => { ReleasePopup(); });
                    }
                    catch (Exception ex)
                    {
                        // Theme 5 fix: if ScheduleShow throws before installing handlers,
                        // release the blocker (round 3 finding RNC-2).
                        ReleasePopup();
                        MCMSettings.DebugLog("RelationNotes: no-match search dialog error: " + ex.ToString());
                    }
                    return;
                }

                // If exactly one match, go straight to the note editor
                if (matches.Count == 1)
                {
                    var (id, name, _) = matches[0];
                    ShowNotePopup(heroId, id, name);
                    return;
                }

                // Cap at 50 results for usability
                int cap = Math.Min(matches.Count, MaxSearchResults);

                string pickerTitle = Localization.L("relation_picker_results",
                    searchText, matches.Count.ToString());
                string pickerDesc = matches.Count > cap
                    ? Localization.L("relation_picker_showing", cap.ToString())
                    : "";

                var items = new MBBindingList<SelectionItemVM>();
                for (int i = 0; i < cap; i++)
                {
                    var (id, name, hasNote) = matches[i];
                    string capturedId = id;
                    string capturedName = name;
                    string label = hasNote ? "* " + name : name;

                    items.Add(new SelectionItemVM(label, hasNote, () =>
                    {
                        SelectionPopupHelper.Close();
                        ShowNotePopup(heroId, capturedId, capturedName);
                    }));
                }

                SelectionPopupHelper.ScheduleShow(pickerTitle, pickerDesc, items, null);
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("RelationNotes: ShowFilteredHeroPicker error: " + ex.ToString());
            }
        }

        /// <summary>
        /// Builds relation notes text to append to the hero page description.
        /// Returns null if no notes exist. Called from HeroPageRefreshPatch.Postfix.
        /// </summary>
        public static string BuildRelationNotesText(string heroId)
        {
            if (EncyclopediaEditBehavior.Instance == null) return null;

            var notes = EncyclopediaEditBehavior.Instance.GetRelationNotesForHero(heroId);
            if (notes.Count == 0) return null;

            // Resolve hero names
            var lines = new List<string>();
            foreach (var (otherHeroId, note) in notes)
            {
                string name = null;
                foreach (var h in Hero.AllAliveHeroes)
                {
                    if (h?.StringId == otherHeroId)
                    {
                        name = h.Name?.ToString();
                        break;
                    }
                }
                // Also check dead heroes
                if (name == null)
                {
                    foreach (var h in Hero.DeadOrDisabledHeroes)
                    {
                        if (h?.StringId == otherHeroId)
                        {
                            name = h.Name?.ToString();
                            break;
                        }
                    }
                }
                name = name ?? otherHeroId;

                // Truncate long notes for display
                string displayNote = note.Length > MaxNotePreviewLength ? note.Substring(0, MaxNotePreviewLength - InlineNoteEllipsisOffset) + "..." : note;
                lines.Add(name + ": " + displayNote);
            }

            lines.Sort(StringComparer.OrdinalIgnoreCase);

            string header = Localization.L("relation_notes_header");
            return "\n\n" + header + "\n" + string.Join("\n", lines);
        }

        /// <summary>
        /// Annotates hero entries in the Friends/Enemies lists with relation score and note text inline.
        /// Example: "Derthert Relation: +32 Note: Loyal ally in my wars against Battania."
        /// Called from HeroPageRefreshPatch.Postfix.
        /// Uses reflection to access the EncyclopediaHeroPageVM's Friends and Enemies MBBindingLists.
        /// </summary>
        public static void AnnotateFriendEnemyNames(object heroPageVm, string viewingHeroId)
        {
            if (EncyclopediaEditBehavior.Instance == null || heroPageVm == null) return;

            // Resolve the viewing hero object for relation score lookup
            Hero viewingHero = null;
            foreach (var h in Hero.AllAliveHeroes)
            {
                if (h?.StringId == viewingHeroId) { viewingHero = h; break; }
            }
            if (viewingHero == null)
            {
                foreach (var h in Hero.DeadOrDisabledHeroes)
                {
                    if (h?.StringId == viewingHeroId) { viewingHero = h; break; }
                }
            }

            try
            {
                var vmType = heroPageVm.GetType();

                // Process both Friends and Enemies lists
                foreach (string listName in new[] { "Friends", "Enemies" })
                {
                    var listProp = vmType.GetProperty(listName,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (listProp == null) continue;

                    var list = listProp.GetValue(heroPageVm) as System.Collections.IEnumerable;
                    if (list == null) continue;

                    foreach (object item in list)
                    {
                        if (item == null) continue;
                        var itemType = item.GetType();

                        // Extract the Hero object from the HeroVM
                        Hero targetHero = null;
                        string targetId = null;
                        var heroProp = itemType.GetProperty("Hero", AllFlags)
                                    ?? itemType.GetProperty("HeroObject", AllFlags);
                        if (heroProp != null)
                        {
                            targetHero = heroProp.GetValue(item) as Hero;
                            if (targetHero != null)
                                targetId = targetHero.StringId;
                        }

                        if (string.IsNullOrEmpty(targetId)) continue;

                        // Check if a relation note exists for this hero
                        if (!EncyclopediaEditBehavior.Instance.HasRelationNote(viewingHeroId, targetId))
                            continue;

                        string noteText = EncyclopediaEditBehavior.Instance.GetRelationNote(viewingHeroId, targetId);
                        if (string.IsNullOrEmpty(noteText)) continue;

                        // Truncate long notes for inline display
                        string displayNote = noteText.Length > MaxInlineNotePreviewLength ? noteText.Substring(0, MaxInlineNotePreviewLength - InlineNoteEllipsisOffset) + "..." : noteText;

                        // Build the inline annotation: "Relation: +32 Note: text"
                        string annotation = "";
                        if (viewingHero != null && targetHero != null)
                        {
                            try
                            {
                                int relationScore = viewingHero.GetRelation(targetHero);
                                string sign = relationScore >= 0 ? "+" : "";
                                annotation = " Relation: " + sign + relationScore;
                            }
                            catch (Exception ex) { MCMSettings.DebugLog("[RelationNotesInjector]: " + ex.ToString()); }
                        }
                        annotation += " Note: " + displayNote;

                        // Append to the displayed name
                        var nameProp = itemType.GetProperty("NameText", AllFlags);
                        if (nameProp != null && nameProp.CanWrite)
                        {
                            string currentName = nameProp.GetValue(item)?.ToString() ?? "";
                            if (!currentName.Contains("Note:")) // avoid double-annotating
                                nameProp.SetValue(item, currentName + annotation);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("RelationNotes: AnnotateFriendEnemyNames error: " + ex.ToString());
            }
        }

        private static void ShowNotePopup(string heroId, string targetHeroId, string targetName)
        {
            if (EncyclopediaEditBehavior.Instance == null) return;

            string existingNote = EncyclopediaEditBehavior.Instance.GetRelationNote(heroId, targetHeroId) ?? "";
            bool isNewNote = string.IsNullOrEmpty(existingNote);

            string title = Localization.L("relation_note_title", targetName);

            // Show suggestion hint in title for new notes
            string suggested = EncyclopediaEditBehavior.Instance.SuggestRelationNoteTag(heroId, targetHeroId);
            if (!string.IsNullOrEmpty(suggested) && isNewNote)
            {
                string sugLabel = suggested.Length > 1
                    ? suggested.Substring(0, 1).ToUpperInvariant() + suggested.Substring(1)
                    : suggested.ToUpperInvariant();
                title += " [" + sugLabel + "?]";
            }

            // Block encyclopedia input while popup is open.
            // EditPopupInjector sets IsFocusLayer=true on the encyclopedia layer, which
            // causes the game to think the encyclopedia is closing. Block() activates the
            // TickForceEncyclopediaOpen() safety mechanism to prevent this.
            // _popupOpen prevents TickAutoUnblock from removing the layer while the
            // native TextInquiry fallback dialog is visible (EditPopupInjector.IsOpen
            // becomes false when falling back to native dialog).
            EncyclopediaInputBlocker.Block();
            _popupOpen = true;

            const int maxNoteLength = 500;

            try
            {
                EditPopupInjector.ScheduleShow(
                    title,
                    existingNote,
                    maxNoteLength,
                    onConfirm: (string newText) =>
                    {
                        ReleasePopup();

                        // Enforce max length
                        if (!string.IsNullOrEmpty(newText) && newText.Length > maxNoteLength)
                            newText = newText.Substring(0, maxNoteLength);

                        EncyclopediaEditBehavior.Instance.SetRelationNote(heroId, targetHeroId, newText);
                        MCMSettings.DebugLog("RelationNotes: saved note for " + heroId + " -> " + targetHeroId
                            + " (" + (string.IsNullOrEmpty(newText) ? "cleared" : newText.Length + " chars") + ")");

                        // Auto-tag on first creation only (strong signals)
                        if (isNewNote && !string.IsNullOrEmpty(newText))
                            RelationNotesSectionInjector.TryAutoTagOnFirstCreation(heroId, targetHeroId);

                        // Notify API consumers
                        try { EditableEncyclopediaAPI.RaiseRelationNoteChanged(heroId, targetHeroId, newText); }
                        catch (Exception ex) { MCMSettings.DebugLog("[RelationNotesInjector]: " + ex.ToString()); }

                        // Refresh the page so the note text appears in the description
                        try { EncyclopediaPageTracker.CurrentRefreshAction?.Invoke(); }
                        catch (Exception ex) { MCMSettings.DebugLog("RelationNotes: refresh error: " + ex.ToString()); }
                    },
                    onCancel: () =>
                    {
                        ReleasePopup();
                        MCMSettings.DebugLog("RelationNotes: cancelled note edit for " + targetHeroId);
                    }
                );
            }
            catch (Exception ex)
            {
                // Theme 5 fix: if ScheduleShow throws synchronously, release the blocker
                // to prevent the encyclopedia from being permanently input-locked.
                ReleasePopup();
                MCMSettings.DebugLog("RelationNotes: ShowNotePopup error: " + ex.ToString());
            }
        }
    }
}
