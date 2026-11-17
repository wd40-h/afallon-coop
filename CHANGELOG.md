# Changelog

All notable changes to the mod, newest first. Everyone needs the same version to play together.

- **0.8.0:**
  - A long session no longer keeps growing: a cast glow whose stop never arrived, entries for players, bots and group
    frames that left the scene, a finished session's loot and dungeon-finder state, and the bookkeeping of mobs this
    machine no longer runs are dropped when they are no longer needed.
  - Less work per frame: the HUD feed, the player's own reported look and the dungeon's place name are built only when
    they change, status marks on group frames no longer pile up when the game rebuilds those frames, and the
    status-effect check, the ride bookkeeping and the host's kept auction mail no longer allocate or re-read the mail
    file on every pass.
- **0.7.1:**
  - Internal cleanup, with no behaviour change: the host's join checks (protocol, game build, password, ban list, an
    already-connected peer and a full session) now live in their own class.
- **0.7.0:**
  - Renamed the mod to AfallonCoop, including the release archive name (`AfallonCoop-<version>.zip`) and the in-game
    title.
  - Fixes: the last reliable messages of a player who disconnects over Steam are no longer lost; group loot rolls are
    cleared when someone leaves and stale kills are pruned; a trade answer from someone not in the trade is ignored
    and the host refunds a trade nobody answers after 20 s; the damage meter handles an over-long remote encounter.
  - Less work per frame: inspect and the damage meter no longer scan the scene every frame, player visuals and packet
    handling allocate less, and the network core ticks without LINQ.
  - Internal cleanup, with no behaviour or protocol change: the network session is split into smaller parts (outbox,
    peers, pause, spawner leases, groups) and the transports and registries were tidied up.
- **0.6.0:**
  - Fixes: no more lost items or gold when the host is on a loading screen; trades that freeze when someone
    disconnects give the items back; a loot roll won by a player who left reaches them by mail; auction mail is no
    longer lost; a player who rejoins no longer keeps another player's form, mount or duel.
  - Fixes: effects and heal-over-time ticks no longer play twice; bots show the right rings and stats, and inspect
    shows their real items; a bot handed to another player keeps its item stats.
  - Less traffic: the auction house updates every 3 s only while someone has it open, mobs are sent only to players
    near them.
  - New: host password, ban list, Steam games open to the host's friends only, scrolling server lists, SHOW DEBUG
    switch (the status line in the corner, off by default).
  - Removed: joining by lobby code (it did not work); join Steam friends from the list instead.
- **0.5.0:** dungeon instances and Dungeon Finder for groups, corruption keys, challenge stones, group loot rolls,
  trade, duels, inspect, mail, friends, shared auction house, shared world quests and random events, boss mechanics on
  every machine, dragon flights, Follow/Summon/Travel to, riding druid forms, quest sharing, shared damage meter.
- **0.4.0:** groups with native frames, bot companions for the group, buffs and debuffs sync, mounts and forms.
- **0.3.0:** shared mobs, combat, loot and quest credit, pets and bots, world time and weather.
