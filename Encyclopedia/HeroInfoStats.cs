using System;
using System.Collections.Generic;
using System.Text;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;

namespace EditableEncyclopedia
{
    /// <summary>
    /// Single source of truth for the hero "Info" stat set shown on the encyclopedia
    /// page and (via EditableEncyclopediaAPI) in EE-ChronicleNoters' chronicle pane.
    ///
    /// The fog-of-war mask lives HERE and nowhere else. Duplicating it is what caused
    /// the 2026-08-02 leak: rows were built in one place and masked in another, so
    /// gold/troops/holdings showed for heroes the player had never met while vanilla
    /// still rendered "???" for Age and Occupation.
    /// </summary>
    public static class HeroInfoStats
    {
        /// <summary>
        /// Vanilla's mask, std_common_strings id keqS2dGa — literally "???" in all 12 shipped
        /// languages. Aliased to EncyclopediaPatchHelper's constant so the two can never drift.
        /// </summary>
        public static readonly string Hidden = EncyclopediaPatchHelper.HiddenInfoPlaceholder;

        /// <summary>
        /// Ordered label/value pairs for a hero. When <paramref name="infoHidden"/> is true
        /// every value is replaced with <see cref="Hidden"/>.
        /// </summary>
        public static List<KeyValuePair<string, string>> Build(Hero hero, bool infoHidden)
        {
            var rows = new List<KeyValuePair<string, string>>();
            if (hero == null) return rows;

            Action<string, string> add = (label, value) =>
            {
                if (string.IsNullOrEmpty(value)) return;
                rows.Add(new KeyValuePair<string, string>(label, infoHidden ? Hidden : value));
            };

            // Never let a derivation throw across a mod boundary: EE-ChronicleNoters and
            // EditableEncyclopediaAPI call this directly and have no try of their own.
            // Because rows fills incrementally, a throw returns everything gathered so far —
            // the same partial-panel behaviour the old inline encyclopedia code had.
            try
            {
                // ═══ PERSONAL ═══

                // Kingdom / Faction
                string kingdomName = hero.Clan?.Kingdom?.Name?.ToString();
                if (!string.IsNullOrEmpty(kingdomName))
                    add("Kingdom", kingdomName);
                else if (hero.Clan?.IsMinorFaction == true)
                    add("Faction", hero.Clan.Name?.ToString() ?? "Minor Faction");
                else
                    add("Kingdom", "None");

                // Location
                string locationDisplay = null;
                if (hero.IsPrisoner)
                {
                    var captorName = hero.PartyBelongedToAsPrisoner?.LeaderHero?.Name?.ToString()
                                  ?? hero.PartyBelongedToAsPrisoner?.Name?.ToString();
                    locationDisplay = !string.IsNullOrEmpty(captorName) ? "Prisoner of " + captorName : "In captivity";
                }
                else if (hero.CurrentSettlement != null)
                    locationDisplay = hero.CurrentSettlement.Name?.ToString();
                else if (hero.PartyBelongedTo != null)
                {
                    locationDisplay = "Traveling";
                    try
                    {
                        var nearStr = EncyclopediaEditBehavior.GetNearestSettlementName(hero.PartyBelongedTo);
                        if (!string.IsNullOrEmpty(nearStr))
                            locationDisplay = "Near " + nearStr;
                    }
                    catch (Exception ex) { MCMSettings.DebugLog("Patches: stat item injection failed: " + ex.ToString()); }
                }
                add("Location", !string.IsNullOrEmpty(locationDisplay) ? locationDisplay : "Unknown");

                // Status
                string statusDisplay = "Active";
                try
                {
                    if (hero.IsDead) statusDisplay = "Dead";
                    else if (hero.IsPrisoner) statusDisplay = "Prisoner";
                    else if (hero.IsWounded) statusDisplay = "Wounded";
                    else { try { if (hero.IsFugitive) statusDisplay = "Fugitive"; else if (hero.IsReleased) statusDisplay = "Recently Released"; } catch (Exception ex) { MCMSettings.DebugLog("Patches: hero status check failed: " + ex.ToString()); } }
                }
                catch (Exception ex) { MCMSettings.DebugLog("Patches: stat item injection failed: " + ex.ToString()); }
                add("Status", statusDisplay);

                // Relation to Player
                try
                {
                    var mainHero = Hero.MainHero;
                    if (mainHero != null && hero != mainHero)
                    {
                        int relation = hero.GetRelation(mainHero);
                        string relLabel = relation >= 50 ? "Friend" : relation >= 20 ? "Warm" : relation <= -50 ? "Hostile" : relation <= -20 ? "Cold" : "Neutral";
                        add("Relation", relation + " (" + relLabel + ")");
                    }
                }
                catch (Exception ex) { MCMSettings.DebugLog("Patches: stat item injection failed: " + ex.ToString()); }

                // Spouse / Family
                try
                {
                    string spouseName = hero.Spouse?.Name?.ToString();
                    add("Spouse", !string.IsNullOrEmpty(spouseName) ? spouseName : "None");

                    int children = 0;
                    try { if (hero.Children != null) children = hero.Children.Count; } catch (Exception ex) { MCMSettings.DebugLog("Patches: children access failed: " + ex.ToString()); }
                    add("Children", children.ToString());
                }
                catch { add("Spouse", "None"); add("Children", "0"); }

                // Traits — removed (already shown natively by the game)

                // At War With
                try
                {
                    var warFactions = new System.Collections.Generic.List<string>();
                    var heroKingdom = hero.Clan?.Kingdom;
                    var heroFaction = (IFaction)heroKingdom ?? hero.Clan;
                    if (heroFaction != null)
                    {
                        foreach (var faction in Campaign.Current.Factions)
                        {
                            if (faction != null && faction != heroFaction && heroFaction.IsAtWarWith(faction))
                            {
                                // Kingdoms only — matching the clan and kingdom pages.
                                // A clan inherits its kingdom's war state, so also listing
                                // every member clan of every enemy kingdom repeated the same
                                // war 20-30 times and was what made this row overflow.
                                if (faction is Kingdom)
                                {
                                    string wfName = faction.Name?.ToString() ?? "Unknown";
                                    if (!warFactions.Contains(wfName))
                                        warFactions.Add(wfName);
                                }
                            }
                        }
                    }
                    // One row per faction — a comma-joined list overflows the
                    // fixed-width stats cell and paints over the next column.
                    EncyclopediaPatchHelper.AddWarRows(add, warFactions, infoHidden);
                }
                catch { add("At War", "Unknown"); }

                // ═══ ECONOMY ═══

                // Wealth
                try
                {
                    int gold = hero.Gold;
                    string goldStr;
                    if (gold >= 1000000) goldStr = (gold / 1000000f).ToString("F1") + "M";
                    else if (gold >= 1000) goldStr = (gold / 1000f).ToString("F1") + "K";
                    else goldStr = gold.ToString();
                    add("Wealth", goldStr + " denars");
                }
                catch { add("Wealth", "0 denars"); }

                // Influence
                try
                {
                    float influence = hero.Clan?.Influence ?? 0;
                    string infStr;
                    if (influence >= 1000) infStr = (influence / 1000f).ToString("F1") + "K";
                    else infStr = ((int)influence).ToString();
                    add("Influence", infStr);
                }
                catch { add("Influence", "0"); }

                // Daily Income (workshops + caravans estimate)
                try
                {
                    int dailyIncome = 0;
                    if (hero.OwnedWorkshops != null)
                    {
                        foreach (var ws in hero.OwnedWorkshops)
                        {
                            try { if (ws != null) dailyIncome += ws.ProfitMade; } catch (Exception ex) { MCMSettings.DebugLog("Patches: workshop profit access failed: " + ex.ToString()); }
                        }
                    }
                    // Estimate caravan income (~200-500 per caravan)
                    int caravanCount = hero.OwnedCaravans?.Count ?? 0;
                    if (caravanCount > 0) dailyIncome += caravanCount * 300;
                    add("Daily Income", dailyIncome > 0 ? "~" + dailyIncome + " denars" : "0");
                }
                catch { add("Daily Income", "0"); }

                // ═══ MILITARY ═══

                // Troops
                try
                {
                    int totalTroops = hero.PartyBelongedTo?.MemberRoster?.TotalManCount ?? 0;
                    add("Troops", totalTroops.ToString());
                }
                catch { add("Troops", "0"); }

                // Mercenaries
                try
                {
                    int mercCount = 0;
                    var party = hero.PartyBelongedTo;
                    if (party?.MemberRoster != null)
                    {
                        var heroCulture = hero.Culture;
                        foreach (var troop in party.MemberRoster.GetTroopRoster())
                        {
                            if (troop.Character != null && !troop.Character.IsHero
                                && troop.Character.Culture != null
                                && heroCulture != null
                                && troop.Character.Culture != heroCulture)
                                mercCount += troop.Number;
                        }
                    }
                    add("Mercenaries", mercCount.ToString());
                }
                catch { add("Mercenaries", "0"); }

                // Party Morale
                try
                {
                    if (hero.PartyBelongedTo != null)
                    {
                        float morale = hero.PartyBelongedTo.Morale;
                        string moraleLabel = morale >= 70 ? "High" : morale >= 40 ? "Steady" : "Low";
                        add("Morale", ((int)morale) + " (" + moraleLabel + ")");
                    }
                    else
                        add("Morale", "N/A");
                }
                catch { add("Morale", "N/A"); }

                // Lords
                try
                {
                    int lordCount = 0;
                    if (hero.Clan != null)
                        foreach (var h in hero.Clan.Heroes)
                            if (h != null && h.IsAlive && h.IsLord) lordCount++;
                    add("Lords", lordCount.ToString());
                }
                catch { add("Lords", "0"); }

                // Companions
                try
                {
                    int inParty = 0, inClan = 0;
                    if (hero.PartyBelongedTo?.MemberRoster != null)
                        foreach (var troop in hero.PartyBelongedTo.MemberRoster.GetTroopRoster())
                            if (troop.Character?.HeroObject != null && troop.Character.HeroObject != hero && !troop.Character.HeroObject.IsLord)
                                inParty++;
                    if (hero.Clan != null)
                        foreach (var h in hero.Clan.Companions)
                            if (h != null && h.IsAlive) inClan++;
                    add("Companions", inParty + " in Party / " + inClan + " in Clan");
                }
                catch { add("Companions", "0"); }

                // ═══ HOLDINGS ═══

                try
                {
                    int towns = 0, castles = 0, totalGarrison = 0, workshops = 0, alleys = 0, caravans = 0, supporters = 0;
                    if (hero.Clan != null)
                    {
                        foreach (var s in hero.Clan.Settlements)
                        {
                            if (s == null) continue;
                            if (s.IsTown) { towns++; totalGarrison += s.Town?.GarrisonParty?.MemberRoster?.TotalManCount ?? 0; }
                            else if (s.IsCastle) { castles++; totalGarrison += s.Town?.GarrisonParty?.MemberRoster?.TotalManCount ?? 0; }
                        }
                    }
                    try { workshops = hero.OwnedWorkshops?.Count ?? 0; } catch (Exception ex) { MCMSettings.DebugLog("Patches: workshop access failed: " + ex.ToString()); }
                    try { alleys = hero.OwnedAlleys?.Count ?? 0; } catch (Exception ex) { MCMSettings.DebugLog("Patches: alley access failed: " + ex.ToString()); }
                    try { caravans = hero.OwnedCaravans?.Count ?? 0; } catch (Exception ex) { MCMSettings.DebugLog("Patches: caravan access failed: " + ex.ToString()); }
                    try
                    {
                        foreach (var notable in Hero.AllAliveHeroes)
                            if (notable != null && notable.IsNotable && notable.SupporterOf == hero.Clan) supporters++;
                    }
                    catch (Exception ex) { MCMSettings.DebugLog("Patches: stat item injection failed: " + ex.ToString()); }

                    bool hasHoldings = towns > 0 || castles > 0 || workshops > 0 || alleys > 0 || caravans > 0;
                    if (hasHoldings)
                    {
                        add("Towns", towns.ToString());
                        add("Castles", castles.ToString());
                        add("Garrisons", totalGarrison.ToString());
                        add("Workshops", workshops.ToString());
                        add("Caravans", caravans.ToString());
                        if (alleys > 0)
                            add("Alleys", alleys.ToString());
                    }
                    else
                    {
                        add("Holdings", "No Holdings");
                    }
                    add("Supporters", supporters.ToString());
                }
                catch { add("Holdings", "No Holdings"); }

                // ═══ RECORD ═══

                // Battles / Kills / Tournaments — use persistent counter (immune to journal trim)
                try
                {
                    var behavior = EncyclopediaEditBehavior.Instance;
                    if (behavior != null)
                    {
                        int wins = 0, losses = 0, captures = 0, heroKills = 0, troopKills = 0, tournaments = 0;
                        behavior.GetBattleStats(hero.StringId, out wins, out losses, out captures, out heroKills, out troopKills, out tournaments);

                        // Battles
                        if (wins > 0 || losses > 0 || captures > 0)
                        {
                            var battleSb = new StringBuilder();
                            battleSb.Append(wins).Append("W / ").Append(losses).Append("L");
                            if (captures > 0) battleSb.Append(" / ").Append(captures).Append("C");
                            add("Battles", battleSb.ToString());
                        }
                        else
                            add("Battles", "-");

                        // Kills
                        if (heroKills > 0 || troopKills > 0)
                        {
                            var killSb = new StringBuilder();
                            if (heroKills > 0) killSb.Append(heroKills).Append(" Heroes");
                            if (troopKills > 0)
                            {
                                if (killSb.Length > 0) killSb.Append(", ");
                                killSb.Append(troopKills).Append(" Troops");
                            }
                            add("Kills", killSb.ToString());
                        }
                        else
                            add("Kills", "-");

                        // Tournaments
                        add("Tournaments", tournaments > 0 ? tournaments + " Won" : "-");
                    }
                }
                catch (Exception battleEx)
                {
                    MCMSettings.DebugLog("Battle stats injection failed: " + battleEx.Message);
                }

                // Hall Rank with tier labels
                try
                {
                    if (hero.IsLord && hero.IsAlive && hero.Clan != null)
                    {
                        var allLords = new System.Collections.Generic.List<Hero>();
                        foreach (var h in Hero.AllAliveHeroes)
                            if (h != null && h.IsLord && h.Clan != null) allLords.Add(h);

                        allLords.Sort((a, b) =>
                        {
                            float scoreA = (a.PartyBelongedTo?.MemberRoster?.TotalManCount ?? 0) + (a.Gold / 1000f) + ((a.Clan?.Influence ?? 0) / 10f);
                            float scoreB = (b.PartyBelongedTo?.MemberRoster?.TotalManCount ?? 0) + (b.Gold / 1000f) + ((b.Clan?.Influence ?? 0) / 10f);
                            return scoreB.CompareTo(scoreA);
                        });

                        int rank = 0;
                        for (int i = 0; i < allLords.Count; i++)
                            if (allLords[i].StringId == hero.StringId) { rank = i + 1; break; }

                        if (rank > 0)
                        {
                            float pct = (float)rank / allLords.Count;
                            string tier = pct <= 0.05f ? "Legendary" : pct <= 0.15f ? "Elite" : pct <= 0.35f ? "Renowned" : pct <= 0.6f ? "Notable" : "Common";
                            add("Hall Rank", "#" + rank + " of " + allLords.Count + " (" + tier + ")");
                        }
                        else
                            add("Hall Rank", "Unranked");
                    }
                    else
                        add("Hall Rank", "N/A");
                }
                catch (Exception rankEx)
                {
                    MCMSettings.DebugLog("Hall Rank injection failed: " + rankEx.Message);
                }
            }
            catch (Exception ex)
            {
                MCMSettings.DebugLog("HeroInfoStats.Build failed: " + ex.ToString());
            }

            return rows;
        }
    }
}
