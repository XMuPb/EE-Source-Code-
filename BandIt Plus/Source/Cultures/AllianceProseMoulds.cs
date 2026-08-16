// AllianceProseMoulds.cs — per-culture fallback templates for BanditAllianceQuest
// journal entries. Used by ResolveProse() when a chief's CultureProfile.Alliance*Prose
// field is null. Each culture has 4 templates (one per entry category).
// Created 2026-06-03 per docs/superpowers/specs/2026-06-03-quest-prose-expansion-design.md
using System;
using System.Collections.Generic;

namespace BandItPlus.Cultures
{
    public class ProseMould
    {
        public string Ch1AcceptTemplate;
        public string Ch1HuntBriefTemplate;
        public string MidQuestUpgradeTemplate;
        public string Ch2AdvanceTemplate;

        public string GetTemplate(string fieldName)
        {
            switch (fieldName)
            {
                case "AllianceCh1AcceptProse":       return Ch1AcceptTemplate;
                case "AllianceCh1HuntBriefProse":    return Ch1HuntBriefTemplate;
                case "AllianceMidQuestUpgradeProse": return MidQuestUpgradeTemplate;
                case "AllianceCh2AdvanceProse":      return Ch2AdvanceTemplate;
                default: return null;
            }
        }
    }

    public static class AllianceProseMoulds
    {
        // 14 cultures total: 6 vanilla bandit (forest, mountain, steppe, sea, desert, looters)
        // + 8 BP-authored (bp_frost_reavers, bp_marsh_stalkers, bp_highwaymen,
        // bp_slaver_caravans, bp_fallen_legionaries, bp_sky_raiders, bp_steppe_wolves,
        // bp_pagan_cult). Each mould populated in Phase 2 tasks 17-30 of plan
        // 2026-06-03-quest-prose-expansion.md.
        public static readonly Dictionary<string, ProseMould> ByCultureId =
            new Dictionary<string, ProseMould>(StringComparer.OrdinalIgnoreCase)
        {
            ["forest_bandits"]    = new ProseMould
            {
                Ch1AcceptTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_001",
                    "\"You sit in my hideout uninvited, outsider, but I've heard your name on the trade roads. "
                    + "I am {CHIEF_NAME}, and these woods have eaten warrants like yours for a generation.\n\n"
                    + "Twenty-five days. That's all you get to prove you're worth more to me alive than dead. "
                    + "Bring me word of a kill — a party of those who profit from the same imperial coin that called for ours. "
                    + "The hunt-brief comes next; my scouts have marked one.\n\n"
                    + "After that, we'll talk again. Run, and you'll find I keep my hideouts well-watched.\""),

                Ch1HuntBriefTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_002",
                    "\"My scouts have eyes on {PARTY_NAME} — {ARTICLE} {CULTURE} {KIND} working the trade roads near {AREA}. "
                    + "They don't know yet that they're already dead.\n\n"
                    + "Bring me their banner, or their captain's blade, or whatever you can carry back. "
                    + "Engage them alone — my people are watching to count the kill, and they only count what they see.\n\n"
                    + "The forest will hide you on the approach. After that, it's your skill or your bones.\""),

                MidQuestUpgradeTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_003",
                    "\"Change of marks. {PARTY_NAME} now — {ARTICLE} {CULTURE} {KIND} near {AREA}. "
                    + "The old quarry slipped my watchers, or moved beyond where I trust them. The forest keeps no grudges against the ones that get away — but the new one will do.\""),

                Ch2AdvanceTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_004",
                    "\"You came back. Many don't.\n\n"
                    + "My runners brought word before you did — a {CULTURE} party broken, the captain dead, the banner taken. "
                    + "I had a wager with one of mine that you wouldn't return; she's a denar lighter tonight.\n\n"
                    + "Come to {HIDEOUT} when the sun's down. We have more to discuss now that you've proven you can be trusted to swing a blade.\"")
            },
            ["mountain_bandits"]  = new ProseMould
            {
                Ch1AcceptTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_005",
                    "\"You climbed a long way to find me, outsider. Not many make the last ridge without breaking a leg or losing their nerve. I am {CHIEF_NAME}, and this granite is mine.\n\n"
                    + "Twenty-five days. That's the rope I'm throwing you. Use it to drag back a kill — a party whose loss the lowlands will feel. My scouts will mark the target; you'll have the name and the road before you leave my fire.\n\n"
                    + "Fail me and the descent's harder than the climb. Cross me and you won't see the second pass.\""),
                Ch1HuntBriefTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_006",
                    "\"My watchers came down off the high ledges with a name. {PARTY_NAME} — {ARTICLE} {CULTURE} {KIND} moving the low country near {AREA}, fat with the easy walking down there.\n\n"
                    + "Catch them on open ground where the slope works for you. They climb soft and they tire fast; an hour of broken terrain and their column will be strung out like beads. Take the head of it, the tail folds.\n\n"
                    + "Bring back proof I can hold in my hand. The granite remembers what gets written on it.\""),
                MidQuestUpgradeTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_007",
                    "\"New mark — the old trail went cold below the snowline. {PARTY_NAME} now, {ARTICLE} {CULTURE} {KIND} working the roads near {AREA}. Same climb down, different blood at the end of it.\""),
                Ch2AdvanceTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_008",
                    "\"You came back up the mountain. That tells me enough.\n\n"
                    + "Word reached the ridge before you did — {ARTICLE} {CULTURE} column broken on the lowland road, the captain down, the survivors scattered toward whatever town would still open a gate for them. Clean work, by the sound of it. Cleaner than I bet you for.\n\n"
                    + "Come to {HIDEOUT} before the light goes. The wind up here turns mean after dusk and I won't shout terms across a black ridge. We have the next stretch of the climb to mark out, you and me.\"")
            },
            ["steppe_bandits"]    = new ProseMould
            {
                Ch1AcceptTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_009",
                    "\"You ride into my camp without a banner, outsider. Bold. I am {CHIEF_NAME}, and the plains have long memories for faces.\n\n"
                    + "Twenty-five days. The sky will turn that many times before I lose patience. Bring me a kill — a party that bleeds for the same coin that hunts us. My outriders have already picked one. The brief follows.\n\n"
                    + "Ride hard. Ride alone. The horizon is wide and I have men watching every line of it. Break your word and you'll learn how far a steppe man can chase.\""),
                Ch1HuntBriefTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_010",
                    "\"My riders cut their tracks two days east. {PARTY_NAME} — {ARTICLE} {CULTURE} {KIND} crawling the open ground near {AREA}. Slow. Heavy. Easy meat for a man who knows the wind.\n\n"
                    + "Run them down before they reach walls. Out here there's nowhere to hide a horse, so they'll see you coming. Doesn't matter. By the time they shape a line, you're already through.\n\n"
                    + "Take the kill alone. My watchers will be on the ridge, counting. They count nothing they don't see with their own eyes.\""),
                MidQuestUpgradeTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_011",
                    "\"New tracks. {PARTY_NAME} now — {ARTICLE} {CULTURE} {KIND} crossing the flats near {AREA}. The old quarry rode past the horizon and out of my reach. The wind doesn't carry grudges. Ride.\""),
                Ch2AdvanceTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_012",
                    "\"You came back across the plain. Good.\n\n"
                    + "A rider brought the word ahead of you — {ARTICLE} {CULTURE} party scattered, the captain face-down in the grass, the horses taken. My watchers say you rode through them clean. That's the kind of work the steppe respects.\n\n"
                    + "Ride to {HIDEOUT} before the sky turns again. Sit by my fire. There's more ground to cover between us, and I'd rather speak of it where the wind can't carry our voices to the wrong ears.\"")
            },
            ["sea_raiders"]       = new ProseMould
            {
                Ch1AcceptTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_013",
                    "\"Salt and storm, you've walked into my hold and lived to draw breath. I am {CHIEF_NAME}, and my crew swore their oaths on this deck before you were weaned.\n\n"
                    + "Twenty-five days, no more. The sea grants no mercy and neither do I. Bring me a kill — a party whose blood pays the toll for what your kind has cost mine. My lookouts will mark the quarry next, and you'll sail to meet it.\n\n"
                    + "Break the oath and there's no shore far enough. Keep it, and you'll have drunk from a longship captain's horn.\""),
                Ch1HuntBriefTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_014",
                    "\"My lookouts sighted {PARTY_NAME} from the headlands — {ARTICLE} {CULTURE} {KIND} crawling the coast near {AREA}. Fat, slow, and far from any tide that could save them.\n\n"
                    + "Take them clean. Bring me a torn standard, a captain's ring, something the gulls haven't picked over. Go alone — my crew watches from the rocks, and an oath unwitnessed is no oath at all.\n\n"
                    + "The wind is with you for now. After the steel meets, only your arm decides whether you ride home or feed the crabs.\""),
                MidQuestUpgradeTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_015",
                    "\"New quarry. {PARTY_NAME} now — {ARTICLE} {CULTURE} {KIND} working the coast near {AREA}. The old mark slipped the tide, and the sea keeps what it takes. This one will do, and my lookouts swear by it.\""),
                Ch2AdvanceTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_016",
                    "\"You came back salt-stained and breathing. Few do.\n\n"
                    + "A gull-runner brought the word ahead of you — a {CULTURE} party broken on the strand, the captain face-down, the standard hauled down by your own hand. One of my oar-mates wagered against you. He'll be bailing bilge for a week.\n\n"
                    + "Come to {HIDEOUT} when the tide turns. There's mead in the hall and oaths still to be sworn, and a captain who has held his word is owed a captain's welcome.\"")
            },
            ["desert_bandits"]    = new ProseMould
            {
                Ch1AcceptTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_017",
                    "\"You walk into my shade unbidden, stranger, and the sand has not yet covered your tracks. I am {CHIEF_NAME}, and out here a name is a debt the desert collects.\n\n"
                    + "Twenty-five days. The sun counts them whether you do or not. Bring me one party dead before that count runs dry — a target my watchers will name for you next. No excuses; the heat takes those too.\n\n"
                    + "Drink, then ride. Cross me, and there are wells you will not reach in time.\""),
                Ch1HuntBriefTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_018",
                    "\"My riders followed the dust. {PARTY_NAME} — {ARTICLE} {CULTURE} {KIND} crawling the wastes near {AREA}, watering at the same oases we do.\n\n"
                    + "Ride light. Strike when the sun is low and their guards squint west. Bring me a blade, a buckle, anything I can hold — my men count the kill with their eyes, and the sand swallows the rest.\n\n"
                    + "The distance is long and the water short. Plan your route, or the desert finishes the job before you do.\""),
                MidQuestUpgradeTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_019",
                    "\"New tracks. {PARTY_NAME} now — {ARTICLE} {CULTURE} {KIND} watering near {AREA}. The old quarry crossed dunes my riders won't follow, and the sand has already forgotten them.\""),
                Ch2AdvanceTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_020",
                    "\"You came back before the waterskins ran dry. Few do.\n\n"
                    + "The dust reached me ahead of you — a {CULTURE} party broken in the open, vultures already counting. One of my riders said the heat would finish you before any blade did. He owes me a skin of date wine tonight.\n\n"
                    + "Ride to {HIDEOUT} before the sun climbs again. We have terms to set now that the sand knows your name, and I would rather speak them in shade than under the noon.\"")
            },
            ["looters"]           = new ProseMould
            {
                Ch1AcceptTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_021",
                    "\"Sit down, sit down... don't stand over an old man like that. I am {CHIEF_NAME}, and I've outlived three chiefs braver than me. That's how I'm still breathing.\n\n"
                    + "Twenty-five days, that's what I can give you. No more. We're hungry here, and they — the steel-shirts, the big bands with proper banners — they pick us apart whenever they smell weakness. Bring me one party dead, just one, and the others above us will look elsewhere for sport.\n\n"
                    + "The scouts will whisper a name to you soon. Please... don't fail an old huddled fool. We can't take another bad season.\""),
                Ch1HuntBriefTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_022",
                    "\"My boys came back shivering, but they brought word. {PARTY_NAME} — {ARTICLE} {CULTURE} {KIND} — sits near {AREA}. They don't know we exist, and that's the only blessing left to us.\n\n"
                    + "Strike them quick. Don't let them turn and look at us proper, or they'll remember where the smoke comes from and bring worse down on our heads. We can't fight what comes after, you understand? We just can't.\n\n"
                    + "Take what you can carry. Leave the bodies where the crows will find them first. Hurry back before they... before they notice we were ever here.\""),
                MidQuestUpgradeTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_023",
                    "\"No, no, forget the last one — gone, lost, my scouts won't follow that far. New mark: {PARTY_NAME}, {ARTICLE} {CULTURE} {KIND} drifting past {AREA}. Please, take this one quick before they wander somewhere we can't watch from.\""),
                Ch2AdvanceTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_024",
                    "\"You came back. Bones of my fathers, you actually came back...\n\n"
                    + "Word reached us before you did — a {CULTURE} party broken open, the captain cold. The young ones are eating tonight because of you. Some of them haven't eaten proper in a week, you understand what that means to an old man like me?\n\n"
                    + "Come to {HIDEOUT} when you can. We'll huddle close to a real fire, and I'll tell you what little we know of the bigger predators above us. Maybe — maybe — we live through another winter yet. Don't tarry on the road. They're always watching.\"")
            },
            ["bp_frost_reavers"]  = new ProseMould
            {
                Ch1AcceptTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_025",
                    "\"You stand in my hall, and I have not yet decided if your breath is welcome here. I am {CHIEF_NAME}. The ice does not hurry. Neither do I.\n\n"
                    + "Twenty-five days. That is the span I grant you. In that time you will bring me a kill — a banner taken, a captain stilled. My watchers will mark the deed before any words pass your lips.\n\n"
                    + "Think slowly before you answer. The cold has a long memory, and so do I. Should you turn from this pact, you will find the snow knows every road you take.\""),
                Ch1HuntBriefTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_026",
                    "\"My scouts have watched the white roads for seven nights. They have found {PARTY_NAME} — {ARTICLE} {CULTURE} {KIND} moving slow near {AREA}, their breath rising in the cold like signal smoke.\n\n"
                    + "Ride out. Take them apart in your own time. There is no virtue in haste; the ice teaches that lesson to any who hurry. My people will be watching from the treeline, and they count only what their own eyes have seen.\n\n"
                    + "Bring back the captain's token. The frost will preserve it as well as any oath.\""),
                MidQuestUpgradeTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_027",
                    "\"A new mark. {PARTY_NAME} now — {ARTICLE} {CULTURE} {KIND} crossing near {AREA}. The old quarry slipped beyond the reach of my watchers, and what the ice cannot hold, I do not chase. This one will answer for them both.\""),
                Ch2AdvanceTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_028",
                    "\"You return. The cold did not keep you, and neither did they.\n\n"
                    + "Word reached my hall before your boots did — a {CULTURE} party broken on the open road, the captain laid in the snow, the banner taken down. My runners spoke of it slowly, as is our custom, and I listened without interrupting them.\n\n"
                    + "Come to {HIDEOUT} when the dusk settles and the breath turns to frost again. There is more to say, and it is the kind of saying that wants a fire between us. Walk carefully on the way. The ice remembers every step.\"")
            },
            ["bp_marsh_stalkers"] = new ProseMould
            {
                Ch1AcceptTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_029",
                    "\"You walked through my marsh and lived. Few do. I am {CHIEF_NAME}. I watched you come.\n\n"
                    + "Twenty-five days. That is your length of rope. Bring me a kill — a party that walks where it should not. My scouts will mark one. You will go. You will return, or you will not.\n\n"
                    + "Do not lie to me in this place. The bog hears. The reeds hear. I hear. Speak when spoken to. Move when told. The next words you get from me will be a name and a road.\""),
                Ch1HuntBriefTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_030",
                    "\"{PARTY_NAME}. {ARTICLE} {CULTURE} {KIND}. Moving slow near {AREA}.\n\n"
                    + "My watchers have lain in the reeds two days. They counted boots. They counted blades. They know the gaps. The {KIND} does not know it is being counted.\n\n"
                    + "Take them quiet, or take them loud — the bog does not care. But take them. Alone. My eyes will see it done, or they will not, and only what is seen is paid. Walk the low ground on your way in. The high road has horses on it. Go.\""),
                MidQuestUpgradeTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_031",
                    "\"New mark. {PARTY_NAME}. {ARTICLE} {CULTURE} {KIND}. Near {AREA}. The last one slipped the reeds — gone past where my watchers will follow. This one has not. Go.\""),
                Ch2AdvanceTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_032",
                    "\"You came back. With mud on you. Good.\n\n"
                    + "A runner crossed the bog before dawn. Whispered it to me in the dark. {CULTURE} party — broken. Dead in the wet. The reeds have already taken what the crows left. My watchers saw. I believe them. I believe you.\n\n"
                    + "Come to {HIDEOUT}. Walk soft. Speak when I speak. There is more to say, and it is not said out here where the wind carries. The bog kept faith with you once. Do not test it twice.\"")
            },
            ["bp_highwaymen"]     = new ProseMould
            {
                Ch1AcceptTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_033",
                    "\"You stand in my hideout, and I will not pretend it does not gall me to receive you here. I was {CHIEF_NAME} once of a column that wore real colours. Now I wear none, and welcome strangers in the dark.\n\n"
                    + "The terms are simple, as terms should be. Twenty-five days. One target struck from the rolls. My scouts will name the party; you will see it ended. Do not insult me with delay.\n\n"
                    + "Discharge the duty, and we speak again as something nearer to equals. Fail it, and I shall regret the courtesy I extended tonight.\""),
                Ch1HuntBriefTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_034",
                    "\"My scouts have filed their report, and the matter is plain. {PARTY_NAME} — {ARTICLE} {CULTURE} {KIND}, moving in good order on the roads about {AREA}. I have read the disposition twice. It will serve.\n\n"
                    + "You will engage them alone. I require a clean account, not a shared one — witnesses I trust are posted to observe, and they record only what they see with their own eyes. Do them the favour of leaving no ambiguity.\n\n"
                    + "When the column is broken, return to me. Bring something I can show my own — a standard, a seal, an officer's blade. Proof, in the old fashion.\""),
                MidQuestUpgradeTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_035",
                    "\"Revised orders. The previous mark is struck from the slate — lost to my watchers, which I shall address in due course. The new target is {PARTY_NAME}, {ARTICLE} {CULTURE} {KIND}, last sighted near {AREA}. See it done with the discipline I asked for the first time.\""),
                Ch2AdvanceTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_036",
                    "\"You returned. I confess I did not allot the matter much hope.\n\n"
                    + "The dispatches reached me before you did — a {CULTURE} column broken in the field, its officer dead, the standard recovered. A creditable action, by what my people relay. I have seen worse reports filed by men who still wore the sash. I have, in truth, filed worse myself.\n\n"
                    + "Come to {HIDEOUT} when the watches change. There is more to be said now, and I would rather say it under a roof than at the gate. You have earned the chair across from mine.\"")
            },
            ["bp_slaver_caravans"]    = new ProseMould
            {
                Ch1AcceptTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_037",
                    "\"You walk into my counting-house unannounced, and yet the ledger says you arrived precisely on time. I am {CHIEF_NAME}, and every soul that passes through these chains is an entry I have priced, taxed, and resold.\n\n"
                    + "Twenty-five days. That is the term I extend to new debtors, and you are one now whether you like the word or not. The opening payment is simple — one party erased from the roads. A clean strike, no witnesses owed wages.\n\n"
                    + "Discharge it and your line of credit opens. Default, and I sell what's left of you by the pound. The hunt-brief follows; my brokers have already valued the mark.\""),
                Ch1HuntBriefTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_038",
                    "\"My factors have appraised {PARTY_NAME} — {ARTICLE} {CULTURE} {KIND} moving slow on the roads outside {AREA}. Their cargo manifest is the kind that doesn't argue back, which means the guard is thinner than the price suggests.\n\n"
                    + "I want them written off. Not robbed, not ransomed — closed. Every breathing asset on that roster becomes a zero in my ledger, and you bring me the captain's seal to confirm the entry.\n\n"
                    + "Do it alone. My clerks need a single name on the receipt, and split commissions sour the books. Their value is highest the day you act.\""),
                MidQuestUpgradeTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_039",
                    "\"Reprice the contract. {PARTY_NAME} now — {ARTICLE} {CULTURE} {KIND} near {AREA}. The previous mark cleared its debt to someone faster, or wandered off my market. This one's valuation is fresher, and the margin is yours.\""),
                Ch2AdvanceTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_040",
                    "\"You closed the account. My brokers logged the entry an hour before your shadow reached the gate.\n\n"
                    + "A {CULTURE} party struck from the rolls, captain's seal noted, cargo presumably distributed by whichever scavengers reached the wreck first. I have marked your name in black ink — that means credit, in this house — and the standing fee for our arrangement has been deducted from a debtor you'll never meet.\n\n"
                    + "Return to {HIDEOUT} when the trade winds shift. There are larger contracts I prefer to discuss where the walls are mine and the witnesses are paid.\"")
            },
            ["bp_fallen_legionaries"] = new ProseMould
            {
                Ch1AcceptTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_041",
                    "\"You stand in my hideout as if it were a praetorium, outsider. I am {CHIEF_NAME}, once a centurion of a legion whose name I will not soil by speaking it here.\n\n"
                    + "Twenty-five days. That is your watch, your campaign, your tour. Bring me the head of one who still draws breath from the same rotten standards that broke ours. My scouts will mark the target — you will execute the order as if it were nailed to the column.\n\n"
                    + "Fail, and you join the unburied. Succeed, and we speak again — soldier to soldier, oath to broken oath.\""),
                Ch1HuntBriefTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_042",
                    "\"My outriders have eyes on {PARTY_NAME} — {ARTICLE} {CULTURE} {KIND} moving the roads near {AREA}. They march in column as if the eagle still watched over them. It does not.\n\n"
                    + "Strike them as a legionary strikes — quickly, in formation, without ceremony. Bring back a signum, a gladius, anything that proves the kill. My men will witness from the treeline; what they do not see, I do not count.\n\n"
                    + "Move at the third watch. The roads near {AREA} grow thin then, and discipline rewards the patient soldier.\""),
                MidQuestUpgradeTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_043",
                    "\"New orders. The old quarry slipped the picket line — a failure of my scouts, not yours. Mark {PARTY_NAME} now, {ARTICLE} {CULTURE} {KIND} sighted near {AREA}. The legion taught me never to mourn a missed target when another marches into range.\""),
                Ch2AdvanceTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_044",
                    "\"You returned. The legion taught us that a soldier who comes back has either won or lied — and my runners spoke before you did.\n\n"
                    + "A {CULTURE} party broken on the road. The captain cut down, the standard taken, the column scattered like grain. You did the work cleanly, as one who has stood in a shield-line and knows what the man beside him is owed.\n\n"
                    + "Come to {HIDEOUT} before the next watch. There is wine that remembers better days, and orders that require a soldier I can trust with more than a single blade.\"")
            },
            ["bp_sky_raiders"]    = new ProseMould
            {
                Ch1AcceptTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_045",
                    "\"You climbed far to find me, lowlander. The wind up here strips lies from a man before he reaches my fire. I am {CHIEF_NAME}, and the drop behind you is longer than your patience.\n\n"
                    + "Twenty-five days. That is the span the wings of my scouts will tolerate your shadow on our ledges. Bring me a kill — a party of those who crawl the valleys fattening on what should be ours. The hunt-brief follows; my watchers have already marked one from above.\n\n"
                    + "Fail, and the cliffs do my accounting. Many have fallen who refused to.\""),
                Ch1HuntBriefTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_046",
                    "\"From the high ridge my scouts have tracked {PARTY_NAME} — {ARTICLE} {CULTURE} {KIND} crawling the low roads beneath {AREA}. They walk with their eyes on the dirt, never the sky. They will not see the drop until they are in it.\n\n"
                    + "Strike them alone. My watchers perch where you cannot, and they will count only the kill they witness with their own eyes. No companions, no shared glory.\n\n"
                    + "Approach from the heights if you have the stomach. The fall does half the work — your blade need only finish what gravity began.\""),
                MidQuestUpgradeTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_047",
                    "\"New quarry, lowlander. {PARTY_NAME} — {ARTICLE} {CULTURE} {KIND} crawling near {AREA}. The old mark slipped beneath the cloud-line where my scouts will not follow. From this height, one valley-walker looks much like the next.\""),
                Ch2AdvanceTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_048",
                    "\"You returned. The cliffs let you pass twice — that is rarer than you know.\n\n"
                    + "My scouts saw it from the ridge before your horse cleared the pass. A {CULTURE} banner trampled in the dust, the captain unmoving, the carrion birds already circling lower than my watchers. They reported it the way they report weather — flat, certain, witnessed from above.\n\n"
                    + "Climb to {HIDEOUT} when the thermals settle and the light goes long across the stone. There is more to say now that you have proven your blade can reach as far as your ambition.\"")
            },
            ["bp_steppe_wolves"]  = new ProseMould
            {
                Ch1AcceptTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_049",
                    "\"You stand before the heir of khans, outsider. I am {CHIEF_NAME}, and my blood runs back through ten generations of riders who broke empires upon the open grass.\n\n"
                    + "Twenty-five days. That is the span I grant you to prove your name is worth carving beside ours. Bring me a kill — a banner pulled from a body, a chieftain felled in the open. My outriders have already marked the quarry; the hunt-brief follows when I am done speaking.\n\n"
                    + "Honor the terms, and you ride under the wolf-standard. Fail them, and the ancestors will know your name only as carrion.\""),
                Ch1HuntBriefTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_050",
                    "\"My outriders have run their ponies hard, and the word is brought before the standard. {PARTY_NAME} — {ARTICLE} {CULTURE} {KIND} — moves upon the plains near {AREA}, slow and fat with confidence.\n\n"
                    + "Ride them down as the khans of old rode down the slow-footed. Take the captain's head, or his sigil, or the iron from his belt — bring me proof a wolf can read. My horsemen will watch from the ridgelines and count the dead, for the ancestors record only what their eyes see.\n\n"
                    + "The steppe favors the swift. Be swifter.\""),
                MidQuestUpgradeTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_051",
                    "\"The mark has shifted, rider. {PARTY_NAME} now — {ARTICLE} {CULTURE} {KIND} drifting near {AREA}. The old quarry rode beyond the horizon my outriders patrol, and the great Khans teach that a wolf does not chase what the wind has taken. This new banner will serve.\""),
                Ch2AdvanceTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_052",
                    "\"You return, and the standard stands taller for it. Few outsiders ride back to face a khan's heir twice.\n\n"
                    + "My outriders brought the tale ahead of your dust — a {CULTURE} captain cut down, his banner taken, his column scattered like chaff before the steppe wind. The ancestors heard it spoken at the fire last night, and one of my old warriors lost a wager he made against your bones. He pours koumiss for you now whether he likes it or not.\n\n"
                    + "Come to {HIDEOUT} before the moon turns. There is more to speak of, now that the bloodline knows your name.\"")
            },
            ["bp_pagan_cult"]     = new ProseMould
            {
                Ch1AcceptTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_053",
                    "\"You walk into my circle of bones, stranger, and the smoke did not flinch. That alone tells me more than your tongue ever could. I am {CHIEF_NAME}, and the old gods have been whispering your shape into the embers for three nights.\n\n"
                    + "Twenty-five days the omens grant you. No more. Bring me a death — blood spilled in the right hand will sweeten the augury, and the elders below the earth will know your name.\n\n"
                    + "Refuse, and the same fire that warmed you will read your entrails. The gods are patient. I am less so.\""),
                Ch1HuntBriefTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_054",
                    "\"The knucklebones fell wrong-side-up at dawn, and the crows above {AREA} have been circling since. {PARTY_NAME} moves there — {ARTICLE} {CULTURE} {KIND}, marked already by something older than my hand.\n\n"
                    + "Spill them. Spill them where the soil is hungry. {CULTURE} prayers do not reach down so deep as ours, and what they bleed will quicken the roots beneath {AREA}. Take a token — a tooth, a ring, anything the smoke can recognise.\n\n"
                    + "Go alone. The gods count witnesses, and they prefer their offerings clean of company.\""),
                MidQuestUpgradeTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_055",
                    "\"The fire turned its face. The old mark is lost to the bones — gone beyond the reading. A new shape rises in the embers: {PARTY_NAME}, {ARTICLE} {CULTURE} {KIND} drifting near {AREA}. Spill that one instead. The gods care nothing for which throat, only that the cup is filled.\""),
                Ch2AdvanceTemplate = BandItPlus.Localization.Get("bp_allianceprosemoulds_056",
                    "\"The smoke bent eastward at dusk, and I knew. Before any runner crossed my threshold, the embers had already spoken your deed.\n\n"
                    + "A {CULTURE} banner fallen, and the blood drunk by ground that had thirsted long. The old gods are sated tonight — they spoke your name in the crackle of the fire, and not as a curse this time. Few earn that turn of tongue.\n\n"
                    + "Return to {HIDEOUT} before the next moon dies. There are deeper rites to read together now, and the bones are restless to fall for you again.\"")
            }
        };
    }
}
