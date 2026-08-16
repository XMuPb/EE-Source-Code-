# BandIt Plus — next update (unreleased)

Running notes for the upcoming release. Content lands here as it's field-proven;
at release this becomes the Workshop changelog.

## New: Chief Signature Quests (wave 1)

Every named chief now has ONE personal job — offered in his own voice once you
reach trust tier 2 (trusted) with his culture, once per campaign.

- **Skarvac of the High Pass — The Mountain's Toll**: a rich column ran the pass
  waving a lord's paper. Collect 2400 denars — demand it with visible strength,
  settle for half, or take it in blood.
- **Granny Hod — The Widows' Grain**: tax-riders emptied the wood-village that
  fills the camp's pot. No bargains — spears, then soup with marrow in it.
- **Khan Borchu — Forty Hooves**: a lord's drovers walk forty steppe horses to
  market. Stampede them by night or ride them down in the open; Borchu gifts
  two of the best on return.
- **Khalid the Chain — Break the Rival's Chain**: a splinter ring undercuts his
  prices. Their captain offers 3000 to look away — take it and betray Khalid
  forever, or break them and decide what happens to the stock.
- **Centurion Aurelius — The Lost Standard**: an imperial column carries the
  eagle of his disgraced Twelfth. Demand it on your name, buy it quietly, or
  meet a legion's drill line.
- **Ulan Twice-Banished — The Third Writ**: a khan's emissary carries the third
  banishment-and-bounty writ ever written against Ulan. He wants to own the
  paper that hunts him.

Mechanics: roadside herald lines point at waiting jobs; confrontations open in
conversation with real choices (strength demands, buy-outs, bribes, steel);
targets spawn out on the roads near the chief's camp; journal opens with the
chief's full speech; rewards are gold + chief relation + per-quest extras
(horses, food-vendor trust). Betraying a chief costs a trust tier and his
respect, permanently. MCM toggle: EnableSignatureQuests.

And the rest of the wilds (waves 2/3, same update):

- **Vargolf the Iron-eyed — The Iron Price**: seize a lord's iron convoy before
  winter — the Reavers need axe-heads more than his garrison needs steel.
- **Mireborn — The Drowned Road**: a royal surveyor is staking a causeway
  through the marsh. Break the column; the charts go to the black water.
- **The Reeve — The Reeve's Arrears**: a merchant combine ran his roads unpaid
  for a season. Collect — principal plus interest, compounded quarterly by insult.
- **Aerlin Cliffborn — The Caged Cliffs**: nine cliff-falcons netted from the
  eyries ride south in wicker. The birds come home to the wind.
- **Crone Vael — The Barrow Spoils**: grave-diggers sold the old dead to a
  collector. Come by dark, as the dead come.
- **Boll the Eldest — The Ditch-Folk's Coin**: the lord's levy squeezed the
  families that feed the looters. Make the counting stop.
- **Bjornulf Salt-Tongue — The Shore-Price**: a harbormaster auctioned a beached
  crew's shares. Nine piles on sail-cloth, weighed the old way.
- **Hakkun the Sand-Reader — The Well-Killers**: quicklime wagons roll for the
  deep desert wells. Water is life; stop them.

Every confrontation speaker carries an authored name (Master Cavren, Guild
Factor Hamon, Engineer Fazil...) instead of a troop class label.

## New: Sneak-In Shadow Bar

The hideout sneak-in finally shows you what the engine already knows. A
top-center stealth HUD, invisible while you're safe:

- **Amber suspicion** while a bandit is close to noticing you (line-of-sight
  cone, decays when you slip away) — the vanilla game shows nothing here.
- **SEEN** — the engine's hidden 5-second fail timer becomes a red draining
  bar with a countdown. Break line of sight or silence the witness before it
  empties and it resets; a green CLEAR flash confirms it.
- **WATCHERS** counter for how many eyes are on you.

The chief-reveal card also appears sneak-flavored in this mission: HIDEOUT
INFILTRATION instead of ASSAULT, and the ODDS readout becomes RISK (how many
bandits are near you, live). MCM toggle: SneakShadowHud.

## Fixed

- LOCALIZATION: prefab UI labels (HUD headers, book/console chapter names, buttons
  like "Back", the origin picker) could show their raw {=id} tag instead of the word
  in a non-English game (English was always fine). BP loads its own prefabs directly
  and static prefab text doesn't pass through the game's text resolver the way code
  text does, so a marker whose id wasn't in the active language printed raw. Those
  labels are now translatable like the rest of the mod: their ids ship in the EN
  strings file so a translator can translate them, and a load-time resolver runs each
  one through the game's text system — showing the translation when present, English
  when a translation misses it, never the raw tag. The prose, dialog, quests, book and
  encyclopedia are all code-driven and translatable too. Reported by a translator.
- CRASH (mod interop): a save could stop loading with other mods installed if a
  Bandit-King rebellion had left a town without an owning clan. The engine's
  militia owner resolves through Settlement.OwnerClan.Leader with no null guard,
  so any mod that scans every party as the game loads — Guilds of Calradia's
  smuggler-provisions feed was the reported one — hit that ownerless town's
  militia and crashed on session start. BP now heals ownerless settlements while
  the save loads, before that scan runs, handing the town back to a living clan;
  it repairs an already-affected save on the next load. BP alone and those mods
  alone were never affected — only the combination, once a rebellion had run.
- STORYLINE: BandIt Plus now stands down during the main quest's tutorial chain
  (brother, healer, Radagos' raiders and their hideout) and never touches
  quest-flagged parties anywhere. Player reports confirmed the damage: the
  call-for-backup fold was DESTROYING Radagos' raider parties as "reinforcements"
  (quest unfinishable), the peace override broke the scripted raider encounters,
  the Spoils popup swallowed the story's next-step notification, and at-peace
  players could lose the attack option on the story hideout. Sandbox campaigns
  are unaffected; after the tutorial everything works as before.
- COMPAT: new-game crash with Adonnay's Troop Changer — ATC's volunteer model
  chokes on our chiefs (bp-culture notables it never configured). When ATC is
  installed, BP chiefs now report zero volunteer production before ATC's code
  runs. No effect when ATC is absent.
- partytemplates.xml now validates against the game schema (root partyTemplates,
  MBPartyTemplate entries, stacks wrapper) — kills the "Incorrect structure"
  error players saw in their load logs since v0.1. Thanks to the player who
  reported it with the correct structure included.
- CRASH: closing a native screen (quest journal, inventory) could crash if a
  BP overlay (toast, peace letter) had auto-closed while that screen covered
  the map — the overlay removed itself from the wrong screen and left a dead
  layer behind. All seven BP overlays now track their host screen, and the
  peace letter waits until you're back on the map before delivering.
- Royal Codex console settings now PERSIST: changes are written to MCM's
  settings file when the codex closes, so they survive quitting the game and
  reloading any save exactly as set. (They previously reverted on restart —
  the console changed values in memory but nothing ever saved them.)
- Every BP HUD is now toggleable from the codex HUD chapter (11 rows): map HUD
  (preview, now DEFAULT OFF), rank-up toasts, glow, quest alert, hideout chief
  card, sneak shadow bar, battle AI panel, hideout alarm system, Bandit King
  panels, rebellion-as-toasts, and rebellion bandit-only scoping.
- Chief dialog: "Do you have any quest for me?" now tags as (Quests), not (Story).
- CRASH: attacking any at-peace bandit party (vanilla hostile action against a
  leaderless bandit clan hit a null-leader relation change). Latent since
  peaceful encounters shipped; now guarded.
- CRASH (latent): three alliance-quest party spawns passed a null clan, which
  NREs in 1.4.5+ party initialization. All spawn sites now resolve the real
  bandit clan.
- Quest kill-detection registered only after a reload for quests accepted the
  same session (StartQuest vs OnQuestStarted). Fixed across signature,
  named-target, raid-village, slaver AND alliance quests.
- Signature battle victories count even when survivors flee the field.

## Under the hood

- Every player-facing line in the mod now runs through the localization system
  and ships in a proper English strings file (ModuleData/Languages/EN) — 3,738
  entries covering dialog, quests, HUDs, panels, vendor and slaver menus,
  settings, the static labels baked into the UI prefabs (HUD column headers,
  chapter names, buttons, briefing captions), the full intro-book chronicle
  (all eighteen pages of prologue prose, with the coloured names, places and
  numbers preserved as inline markup), AND the encyclopedia chronicle prose —
  the player's origin story and clan history, plus the Bandit King and his
  Holdout's lore (the hero name, pronouns and clan link ride in as runtime
  placeholders). Nothing changes for English players (the text is byte-for-byte
  the same); it just all lives in one file now and the mod is ready to translate.
  A short HOW_TO_TRANSLATE.txt sits in the EN folder for anyone who wants to.

## Before release (checklist)

- [x] StartQuest sweep: alliance, raid-village, named-target, slaver
- [x] Waves 2/3 chief content (8 chiefs — all 14 now authored)
- [x] Confrontation captain nameplates (ConversationNamePatch)
- [ ] Khalid betrayal + post-battle stock paths field-run
- [ ] Battle-outcome path field-run (fixed after the 05:13 kill, not re-run yet)
- [ ] Save/load mid-quest check
- [ ] Banner-flip cosmetic bug (quest column nameplate after full-screen menus)
- [ ] Release build log check (errors only)
