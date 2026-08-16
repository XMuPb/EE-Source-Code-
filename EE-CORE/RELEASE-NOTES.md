# EE-Core

Shared base module for the Editable Encyclopedia family. It carries the code that more
than one of my mods needs, so a fix lands once instead of four times.

**If you're a player:** you don't configure anything here. Subscribe it, leave it enabled,
make sure it loads before the mods that need it. That's the whole job.

---

## Features

### Version compatibility

This is the main reason EE-Core exists. TaleWorlds moves APIs between patches, and a mod
built against one version usually just dies on another. EE-Core detects what the running
game actually exposes and gives the mods on top of it one stable way to call things.

Two areas covered right now:

- **Encyclopedia API.** 1.4.5 deleted `EncyclopediaData` and folded everything into
  `EncyclopediaManager`. EE-Core figures out which one you're on and routes accordingly.
- **Campaign party AI.** New in this release. Covers the argument change 1.4 made to the
  raid order. See the changelog below.

Detection runs once at load and gets cached. Nothing here throws into a campaign tick. If
something can't be resolved it logs and backs off rather than taking the save down with it.

### Shared plumbing for the EE mods

Logging, localisation, shared saveable types and save-schema migration, cross-module
registration, and file export helpers. None of it is player facing. It's there so
Editable Encyclopedia, EE-ChronicleNoters and EE-WebAPI aren't each carrying their own
copy.

### Diagnostics

Compatibility detection writes to:

```
Documents\Mount and Blade II Bannerlord\Configs\ModSettings\Global\EditableEncyclopedia\Logs\debug-compat.log
```

That file gets written whether or not debug logging is switched on, because the whole point
of it is to still exist when something has gone wrong. If a mod that depends on EE-Core is
misbehaving, that log is the first thing to look at.

### Requirements

- Harmony
- ButterLib

Used by Editable Encyclopedia, EE-ChronicleNoters, EE-WebAPI, and now Companion Lead Army.

---

## Changelog

## v2.6.1.1 (2026-08-06)

Adds a compatibility layer for the campaign party-AI API so mods on top of EE-Core can
support Bannerlord 1.3.x and 1.4.x from one build. No behaviour change to anything that
already worked. Save-compatible.

### Added

- **`CampaignCompat`,** a version shim for `SetPartyAiAction`. Bannerlord 1.4 appended an
  `isTargetingPort` argument to `GetActionForRaidingSettlement`, so a mod compiled against
  1.4 can't make that call on 1.3.15. Worse than it sounds: .NET resolves every call in a
  method before executing any of it, so one unresolvable call kills the entire method it
  sits in, however long that method is. `CampaignCompat` looks at what the running game
  actually declares, then calls the four or five argument version to match.

  It binds by counting parameters instead of checking a version number, so it should cope
  with 1.4.0 through 1.4.4 as well. I haven't been able to get those builds to test against,
  so treat that as expected rather than confirmed.

  `CampaignCompat.Describe()` prints what it bound, and detection is written to
  `debug-compat.log` either way.

### Notes

- Save-compatible. Install over v2.6.1 and restart.
- **Update this before updating Companion Lead Army.** v2.1.0 of that mod needs
  `CampaignCompat` and won't load against an older EE-Core.
- Editable Encyclopedia, EE-ChronicleNoters and EE-WebAPI are unaffected. They ask for
  v2.6.1 or newer, which this satisfies. No need to reinstall them.
- Verified by pulling the assemblies from both 1.3.15 and 1.4.7 and diffing the full public
  surface of every type involved, rather than going off patch notes. Patch notes don't
  mention argument changes like this one.
