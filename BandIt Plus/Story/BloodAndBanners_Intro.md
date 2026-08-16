

```
New campaign → first safe map tick (BanditOriginBehavior.OnTick, ~3s delay)
  → THE CHRONICLE (BanditIntroStoryScreen — full-screen book, game paused)
      9 two-page spreads, page-turn slide + sound, Back button,
      reading-progress bar with native glow, BANDIT PLUS masthead
  → "Proceed with the story"
  → WHO WERE YOU — BEFORE THE ROAD? (BanditOriginChoiceScreen — card picker)
      3 tall cards, per-origin hero art over firelight, hover-ignite lore,
      click = golden CHOSEN frame → "Swear It — <Origin>" locks it
  → ApplyOrigin() → "Word on the road" names the 3 nearest chiefs
  → campaign begins (AT WAR with all bandit cultures, like everyone)
```

Fallback: if either screen fails to mount, the legacy MultiSelectionInquiry
popup shows instead — a campaign can never get stuck without an origin.

## The Chronicle — 9 spreads

| #   | Chapter                         | Content                                                                                                                                                |
| --- | ------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------ |
| 1   | Prologue — The Long Road        | Your birth, the year it broke, the drifting years                                                                                                      |
| 2   | Prologue — The Crossing         | The voyage, arrival with nothing, the one-eyed peddler who gives you this book                                                                         |
| 3   | I. The Bleeding Age             | The Empire torn three ways; the kingdoms drinking from the wound                                                                                       |
| 4   | II. How the Lawless Were Made   | Genesis of the six vanilla bandit cultures (looters→farmers, gallows-law, struck toll clans, cast-out riders, closed ports, cheated guards)            |
| 5   | III. The Strange Banners        | Genesis of the eight BP clans (Fallen Legionaries, Marsh Stalkers, Frost Reavers, Highwaymen, Slaver Caravans, Sky Raiders, Steppe Wolves, Pagan Cult) |
| 6   | IV. The Chiefs                  | Granny Hod, Skarvac of the High Pass, Ulan Twice-Banished                                                                                              |
| 7   | V. The Chiefs of Coast and Sand | Bjornulf Salt-Tongue, Hakkun the Sand-Reader, the lesser banners                                                                                       |
| 8   | VI. When the Horn Sounds        | The musters, the hungry seasons, bounties, lords' vengeance                                                                                            |
| 9   | VII. The White Flag             | The parley; "the road will want to know who you were" → hands off to the cards                                                                         |

Typography: 4 rich-text inks via `<span style="...">` on RichTextWidget —
NameInk (rubric red, names/banners), PlaceInk (slate blue, places/realms),
NumInk (grey, numbers), GoldInk (antique gold, money/loot).

## The origins — what they actually do

Design rule (user): **fresh game = war for everyone; peace must be earned,
clan by clan, at the chiefs' parleys.** The origin never grants peace — it
prices it. Two numbers in `BanditOriginBehavior`:

| Origin       | Tag    | Card art | Peace tribute | Bounties  | Identity perk (Wave 4.20.0)                                                                     |
| ------------ | ------ | -------- | ------------- | --------- | ----------------------------------------------------------------------------------------------- |
| Outlaw Blood | EASY   | hero2    | **×0.5**      | ×1.0      | Tier-1 hideout privileges (trade, basic camp access) from day 1 — `ApplyTrustGate` bypass       |
| Drifter      | MEDIUM | hero3    | ×1.0          | ×1.0      | Camp vendor trust climbs +2 tiers per quest (`VendorTrustStep`) — vendors max out twice as fast |
| Lawkeeper    | HARD   | hero     | **×2.0**      | **×1.25** | Head-money: flat 120g for ANY bandit party destroyed, even without a posted bounty              |

- Tribute price consumed at the white-flag parley (`BanditOriginBehavior`,
  `TRIBUTE_BASE_GOLD * PeaceCostMultiplier`).
- Bounty multiplier + head-money consumed in `BanditRaidBehavior`
  (`OnMobilePartyDestroyedBounty`).
- `GrantPeace(cultureId)` appends to a saved CSV; the Harmony patches on
  `FactionManager.IsAtWarAgainstFaction` (PeacefulBanditPatch) read it —
  per-culture peace, permanent, save-persisted.

## File map

| File                                                                     | Role                                                                                    |
| ------------------------------------------------------------------------ | --------------------------------------------------------------------------------------- |
| `Source/Origins/BanditOriginBehavior.cs`                                 | Origin state, popup orchestration, multipliers, GrantPeace, parley flow, chief guidance |
| `Source/Origins/BanditIntroStoryVM.cs` / `BanditIntroStoryScreen.cs`     | The book: 18 pages of prose, page-turn + open animations, pause enforcement             |
| `Source/Origins/BanditOriginChoiceVM.cs` / `BanditOriginChoiceScreen.cs` | The card picker: card texts, confirm beat (pending → Swear It), choose callback         |
| `GUI/Prefabs/BanditIntroStory.xml`                                       | Book layout (9 spread column pairs, masthead, progress bar)                             |
| `GUI/Prefabs/BanditOriginChoice.xml`                                     | Card picker layout (3 cards, glows, CHOSEN overlays, Swear It)                          |
| `GUI/Brushes/BandItPlusBookStyles.xml`                                   | All book + card typography/brushes, button states, sounds                               |
| `Source/Patches/PeacefulBanditPatch.cs`                                  | War/peace truth: per-culture overrides once an origin is chosen                         |
| Sprites (pipeline)                                                       | `book`, `thumbnail` (masthead), `hero`/`hero2`/`hero3` (transparent cutouts)            |

## Sequel hooks (queued)

- Join a chief's raids via alliance (ride with the war band, take a loot cut)
- Per-origin opening inventory/relations flavor (currently price-only)
