# WarcraftAutoBalance v2.10 — Full Server & Warcraft Mod Implementation Guide

This guide covers the complete installation, integration, configuration, validation, and operational model for **WarcraftAutoBalance v2.7**.

v2.9 is designed for a Warcraft-style Counter-Strike 2 server where:

- races can dramatically change player power;
- players may change races frequently;
- races may respawn players;
- some races may temporarily respawn players as summons or alternate forms;
- summons/projectiles may deal damage independently of the owning player controller;
- bots may be present as population filler;
- player population can change quickly;
- low-population matches may intentionally use asymmetric human teams such as 1v2, 1v3, 1v4, or 1v5.

The balancer therefore does **not** treat normal CS2 K/D or physical team counts as the complete measure of team strength.

---

# 1. What the Plugin Does

WarcraftAutoBalance maintains a persistent hidden balance rating for each human player and combines it with current Warcraft race information.

The normal player balance value is based on:

| Component | Weight |
|---|---:|
| Recent ADR | 35% |
| Recent K/D | 25% |
| Persistent historical rating | 20% |
| KAST/round contribution | 10% |
| Objective contribution | 10% |

That base player rating is then modified by:

1. the automatically learned strength of the player's current **base race**;
2. a small race-level modifier.

Conceptually:

```text
Recent player performance
        +
Historical player skill
        ↓
Base player rating
        ×
Learned race modifier
        ×
Race-level modifier
        ↓
Current balance rating
```

The plugin uses that value differently depending on server population.

---

# 2. Important v2.7 Warcraft Assumptions

## 2.1 Base race is the persistent race identity

The balancer needs to know the race the player actually selected.

For example:

```text
Player selects Phoenix
        ↓
Phoenix ability kills player
        ↓
Phoenix respawns player in another form
```

The player's balance race should remain:

```text
Phoenix
```

Do **not** change the balance race merely because the player temporarily exists as a summon, alternate model, reincarnation, transformed unit, or other temporary form.

The race that caused the mechanic should receive the long-term race-performance attribution.

Only call `SetPlayerRace()` with a different race when the player's actual selected/base race changes.

## 2.2 A respawn does not restore survival credit

v2.7 defines survival as:

```csharp
bool survived = !round.Died;
```

If a player dies and a Warcraft race brings them back:

```text
Death
→ round.Died = true
→ Respawn
→ player is alive again
→ round.Died remains true
```

This is intentional.

The extra life is a benefit of the race. It should not make the player appear to have survived the original life.

Multiple actual deaths may still increase the player's round death count. That allows an extra-life race to expose the real combat cost of those additional lives rather than artificially limiting every player to one death per round.

## 2.3 Summons are not additional human players

Human population calculations use real, non-bot player controllers with valid SteamID64 values.

A summon should not turn:

```text
3 humans vs 3 humans
```

into:

```text
4 vs 3
```

for the player-count expectation model.

The race-learning system should instead discover that the summon-producing race wins more often than expected.

## 2.4 Direct summon/projectile attribution is a future integration point

The standalone plugin currently records normal human-vs-human game-event combat.

If the Warcraft mod creates a custom summon/projectile whose attacker is not the owning human controller, WarcraftAutoBalance should **not guess** who owns that damage.

That prevents incorrect attribution.

For maximum future accuracy, the main Warcraft mod can expose the summon/projectile owner SteamID64 and report owner-attributed combat to the balance service.

Race win/loss learning still captures a large part of summon power even without direct damage attribution.

---

# 3. Requirements

The server should already have:

1. a working CS2 dedicated server;
2. Metamod:Source;
3. CounterStrikeSharp;
4. the main Warcraft mod;
5. .NET 8-compatible CounterStrikeSharp plugin build tooling.

Use the CounterStrikeSharp version already used by the Warcraft project wherever possible. Do not independently upgrade CounterStrikeSharp just for this plugin without checking the main mod first.

---

# 4. Recommended Project Layout

A clean development solution can look like:

```text
WarcraftServer/
│
├── WarcraftMod/
│   ├── WarcraftMod.csproj
│   └── ...
│
├── WarcraftAutoBalance/
│   ├── WarcraftAutoBalance.csproj
│   └── WarcraftAutoBalance.cs
│
└── WarcraftBalance.Contracts/
    ├── WarcraftBalance.Contracts.csproj
    └── IWarcraftBalanceService.cs
```

The **Contracts** project is strongly recommended.

It prevents the main Warcraft plugin from directly depending on the complete AutoBalance implementation.

The relationship becomes:

```text
WarcraftMod
     │
     └──── WarcraftBalance.Contracts
                    ▲
                    │
WarcraftAutoBalance ┘
```

Both plugins know the small shared interface.

Neither needs to compile directly against the other's implementation assembly.

---

# 5. Create the WarcraftAutoBalance Project

Create a .NET 8 class library:

```bash
dotnet new classlib -n WarcraftAutoBalance -f net8.0
```

Add the CounterStrikeSharp API package/version used by the main Warcraft mod.

The project should reference:

```text
CounterStrikeSharp.API
```

Copy:

```text
WarcraftAutoBalance.cs
```

into the project.

Remove the automatically generated `Class1.cs`.

Build:

```bash
dotnet build -c Release
```

Before deployment, resolve **all compiler errors and warnings that indicate API mismatches** against the exact CounterStrikeSharp version installed on the server.

---

# 6. Create the Shared Contract

Create:

```text
WarcraftBalance.Contracts
```

as another .NET 8 class library.

Example interface:

```csharp
using CounterStrikeSharp.API.Core;

namespace WarcraftBalance.Contracts;

public interface IWarcraftBalanceService
{
    void SetPlayerRace(
        CCSPlayerController player,
        string raceName,
        int currentLevel,
        int maximumLevel);

    void ClearPlayerRace(
        CCSPlayerController player);
}
```

Both projects reference this contract:

```text
WarcraftAutoBalance
→ WarcraftBalance.Contracts

WarcraftMod
→ WarcraftBalance.Contracts
```

Do not create separate copies of the interface in both projects.

They must use the same shared contract assembly.

---

# 7. Expose WarcraftAutoBalance Through a Plugin Capability

The current v2.7 source exposes public `SetPlayerRace()` and `ClearPlayerRace()` methods.

For clean cross-plugin production integration, add a CounterStrikeSharp plugin capability to the balancer.

Add the contracts namespace:

```csharp
using WarcraftBalance.Contracts;
```

Make the plugin implement the service:

```csharp
public class WarcraftAutoBalance :
    BasePlugin,
    IWarcraftBalanceService
```

Declare one capability ID:

```csharp
public static PluginCapability<IWarcraftBalanceService>
    BalanceCapability { get; } =
        new("warcraft:autobalance");
```

The string:

```text
warcraft:autobalance
```

must be identical in both plugins.

Register the provider during `Load()`:

```csharp
Capabilities.RegisterPluginCapability(
    BalanceCapability,
    () => this
);
```

The capability should be registered after the plugin has initialized its persistent data.

---

# 8. Consume the Capability From the Main Warcraft Mod

In the Warcraft mod:

```csharp
using WarcraftBalance.Contracts;
```

Declare:

```csharp
public static PluginCapability<IWarcraftBalanceService>
    BalanceCapability { get; } =
        new("warcraft:autobalance");
```

Add a helper:

```csharp
private IWarcraftBalanceService? GetBalanceService()
{
    return BalanceCapability.Get();
}
```

The Warcraft mod should remain functional if the balance plugin is unavailable.

Therefore use null-safe calls:

```csharp
GetBalanceService()?.SetPlayerRace(...);
```

Do not make normal Warcraft gameplay depend on the balancer being loaded.

---

# 9. Where the Warcraft Mod Must Synchronize Race Data

The balancer should be updated whenever the player's **real selected/base race state** changes.

## 9.1 Player profile/race loads

After the Warcraft mod has loaded the player's current race:

```csharp
GetBalanceService()?.SetPlayerRace(
    player,
    race.Name,
    playerRaceLevel,
    race.MaxLevel
);
```

## 9.2 Player selects another race

Immediately after the Warcraft mod commits the new selection:

```csharp
GetBalanceService()?.SetPlayerRace(
    player,
    selectedRace.Name,
    selectedRaceLevel,
    selectedRace.MaxLevel
);
```

## 9.3 Race level changes

If a level increase changes the player's race level:

```csharp
GetBalanceService()?.SetPlayerRace(
    player,
    currentRace.Name,
    newLevel,
    currentRace.MaxLevel
);
```

## 9.4 True no-race state

Only call:

```csharp
GetBalanceService()?.ClearPlayerRace(player);
```

when the player genuinely has no selected/base race.

Do not clear it during a normal death.

Do not clear it during a race-controlled respawn.

Do not clear it because the player temporarily becomes a summon.

Do not clear it because a pawn is destroyed and recreated.

---

# 10. Respawns and Temporary Summon Forms

This is especially important for this Warcraft implementation.

Suppose:

```text
Base race: Necromancer

Player dies
→ ability triggers
→ player respawns as Skeleton
→ later returns to normal
```

Do **not** send:

```csharp
SetPlayerRace(player, "Skeleton", ...);
```

unless Skeleton is actually the player's newly selected race.

Keep:

```text
Necromancer
```

as the balance race.

Why?

Because if Necromancer's ability gives players an extra life as a Skeleton, that extra power belongs to **Necromancer's learned race modifier**.

Changing the balance assignment to Skeleton would incorrectly split the statistical effect between two races.

---

# 11. Round-Start Snapshots

v2.7 snapshots human state at round start.

Each player's current-round data records:

```text
SteamID64
team at round start
base race at round start
race-level modifier at round start
damage
kills
deaths
assists
objective contribution
whether the player died
```

The race-learning win expectation is also captured at round start.

Later events cannot retroactively change the expected result:

```text
disconnect
respawn
summon transformation
pawn recreation
mid-round team-state change
```

This is deliberate.

---

# 12. Uneven Round-Start Population

Normal average rating alone does not represent a numerical advantage.

For race-learning expectations, v2.7 applies:

```csharp
private const double PlayerCountExpectationAdjustment = 75.0;
```

Conceptually:

```text
CT average skill = 1000
T average skill  = 1000

CT has one extra real human

Adjusted CT expectation rating:
1000 + 75 = 1075
```

The existing Elo-style expected-win function is then applied.

Only real human players affect this count.

Bots and summon entities do not.

This adjustment affects **race-learning expected wins**, not the normal player rating formula.

---

# 13. Human Combat Statistics

Direct player statistics are intentionally human-vs-human.

Damage is counted only when:

```text
attacker = usable human
victim   = usable human
attacker != victim
attacker team != victim team
```

Kills and assists follow the same philosophy.

This means:

```text
human damages bot     → ignored
human kills bot       → ignored
human assists vs bot  → ignored
```

Bots therefore cannot inflate a human's ADR/KD/KAST rating.

---

# 14. Rating Formula

The default weights are:

```csharp
AdrWeight        = 0.35;
KdWeight         = 0.25;
HistoricalWeight = 0.20;
KastWeight       = 0.10;
ObjectiveWeight  = 0.10;
```

The recent window is:

```csharp
RollingWindowRounds = 12;
```

Normalization roughly treats:

```text
100 ADR        ≈ 1000
1.0 K/D        ≈ 1000
70% KAST       ≈ 1000
0.15 objective points/round ≈ 1000
```

The result is:

```text
BaseRating =
    ADR rating × 35%
  + K/D rating × 25%
  + Historical rating × 20%
  + KAST rating × 10%
  + Objective rating × 10%
```

Then:

```text
FinalRating =
    BaseRating
    × RaceModifier
    × LevelModifier
```

---

# 15. Historical Rating

New players begin at approximately:

```text
1000
```

The historical rating uses a slow exponential update toward recent performance.

Current learning rate:

```csharp
HistoricalLearningRate = 0.05;
```

Historical rating is clamped approximately to:

```text
600–1600
```

This prevents one unusually good or bad session from completely rewriting the player's long-term rating.

The player is identified by:

```text
SteamID64
```

not:

```text
name
slot
entity index
current session
```

A name change therefore does not create a new player rating.

---

# 16. Automated Race Strength Learning

Race strength is not manually hardcoded.

The plugin tracks:

```text
race rounds played
actual race wins
expected race wins
last learned modifier
```

The key idea is:

```text
Race performance =
actual wins - expected wins
```

not simply raw win percentage.

Example:

```text
Race actual win rate:   57%
Expected from users:    54%
Observed race edge:      3%
```

This is much better than declaring the race +7% strong merely because good players prefer it.

Race learning begins after:

```csharp
RaceMinimumSampleRounds = 40;
```

and uses shrinkage toward neutral for smaller samples.

Default race modifier bounds are:

```text
0.95–1.05
```

---

# 17. Race Level Modifier

Race level is deliberately a small adjustment.

Current range is approximately:

```text
0.98–1.02
```

A strong player does not suddenly become weak because they changed to a low-level race, and a weak player does not become elite merely because a race is maxed.

Race level supplements the skill model rather than replacing it.

---

# 18. Normal Population Balancing

For more than six real humans, normal population logic applies.

The plugin evaluates balance every:

```csharp
BalanceEveryRounds = 4;
```

The normal trigger is:

```csharp
BalanceTriggerWinChance = 0.58;
```

So a team must be approximately:

```text
58/42 or worse
```

before a skill-based swap is considered.

This prevents unnecessary player movement for trivial differences such as:

```text
51/49
52/48
```

The preferred post-swap target is:

```csharp
TargetWinChance = 0.55;
```

or approximately:

```text
55/45 or better
```

`FindBestSingleSwap()` evaluates every T/CT human pair and chooses the candidate producing the result closest to 50/50.

It does **not** stop at the first merely acceptable 55/45 candidate.

---


# 18A. Initial Balance Before Live Round 1

v2.8 adds a one-time initial balance so the match does not wait until Round 4
before correcting the starting teams.

The plugin observes:

```text
warmup_end
        ↓
first non-warmup round_prestart
        ↓
re-read CURRENT humans
re-read CURRENT base race/level assignments
        ↓
calculate complete starting partition
        ↓
SwitchTeam() during round_prestart
        ↓
CS2 continues normal round restart actions
        ↓
players receive normal Round 1 team spawns
```

CS2's `round_prestart` event is emitted before the rest of the round restart
actions. The plugin therefore does not change teams while players are already
alive and fighting in warmup.

The initial balance never uses:

```text
Respawn()
manual kill/slay
manual spawn teleport
pawn replacement
```

That is important for this Warcraft server because those actions could trigger
race resurrection, summon-form respawns, death hooks, cooldown resets, extra-life
logic, or spawn abilities.

The player/race list is recalculated at the live prestart rather than being frozen
several seconds earlier during warmup. A race change, join, or disconnect before
that point is therefore reflected in the starting partition.

For 2–6 humans, the existing nonlinear low-pop model is used and can intentionally
produce 1v2, 1v3, 1v4, or 1v5 human splits when the strength model supports it.

For 7+ humans, the initial pass ignores the normal 58/42 disruption threshold.
Its purpose is to build the closest starting matchup available. Up to 32 humans,
an exact meet-in-the-middle fixed-cardinality subset search finds the team rating
sum closest to the ideal target. For larger populations, a greedy selection plus
local pair-swap refinement is used.

Odd populations evaluate both possible team-size orientations, including the
same modest player-count expectation adjustment so the extra real human is not
treated as having zero value.

Bots are redistributed only after the logical human partition is known.

`round_prestart` also fires during warmup, so the plugin checks
`CCSGameRulesProxy.GameRules.WarmupPeriod`. It also observes `warmup_end`.
If GameRules is unavailable and warmup end has not been positively observed, the
plugin delays the initial pass rather than risking a warmup team switch.

On plugin hot reload during an already-live match, the initial balance is treated
as completed unless GameRules clearly reports that warmup is still active. This
prevents an unexpected mid-match "Round 1" rebalance.

# 19. No Swap Protection

There is intentionally:

```text
NO 12-round move protection
NO LastMovedRound
NO protected-player flag
NO balance immunity after a move
```

This is deliberate for this server.

Players may:

```text
change races frequently
join frequently
leave frequently
change the effective strength of a team rapidly
```

A protection period approaching the length of an entire map would prevent the balancer from responding.

A player can therefore be moved again if the current server state genuinely requires it.

---

# 20. Adaptive Low-Population Mode

Low-pop mode applies when there are:

```csharp
2–6 real humans
```

Bots are not used to decide human strength.

Instead, every valid human partition is evaluated.

Possible outcomes include:

```text
1v1
1v2
1v3
1v4
1v5
2v2
2v3
2v4
3v3
```

The system does not force equal human counts.

---

# 21. Low-Population Effective Power

Simply adding 1000-based ratings makes elite-vs-many arrangements almost impossible.

v2.7 therefore uses nonlinear power:

```csharp
power =
    Math.Exp(
        (finalRating - 1000) /
        LowPopulationPowerScale
    );
```

with:

```csharp
LowPopulationPowerScale = 300.0;
```

Approximate examples:

```text
1000 rating → 1.00 power
1300 rating → 2.72 power
1600 rating → 7.39 power
```

Therefore an extremely strong player on a strong race can legitimately be evaluated against several weaker humans.

The selected partition is the one with the closest total effective power.

---

# 22. Low-Pop Bot Distribution

Humans are balanced first.

Bots are filler second.

v2.7 does **not** switch humans and immediately re-read their `Team` property to determine bot placement.

Instead:

```text
selected human partition
        ↓
known logical T human count
known logical CT human count
        ↓
bot redistribution
```

Bot redistribution evaluates every possible split of the existing bots and chooses:

1. the smallest final physical team-count difference;
2. the fewest bot moves as a tiebreaker.

It preselects distinct bots before `SwitchTeam()` calls, so it does not depend on engine team state updating synchronously in the same frame.

---

# 23. Emergency Disconnect Balancing

Disconnects do not wait for the normal four-round interval.

The plugin listens for player disconnects and queues an emergency population check after approximately:

```csharp
DisconnectRebalanceDelaySeconds = 0.50f;
```

Multiple disconnects during that short window are coalesced.

Example:

```text
10v10
→ player disconnects
→ 10v9
→ another disconnects immediately
→ 10v8

one emergency evaluation occurs
```

The emergency physical-count trigger is:

```csharp
EmergencyTeamCountDifference = 2;
```

So:

```text
10v9 → no forced emergency count move
10v8 → emergency correction
10v6 → emergency correction with multiple moves if required
```

---

# 24. Emergency Multi-Move State

Emergency correction maintains its own logical T and CT collections.

When it chooses a move:

```text
remove controller from logical source
add controller to logical destination
call SwitchTeam()
```

The next emergency decision therefore uses the intended logical state rather than assuming CounterStrikeSharp/CS2 has already updated `player.Team` during the same frame.

Bots on the oversized side are preferred as unrated physical filler.

If a human must move, the plugin uses the full human rating model to select the move that creates the best projected matchup.

---

# 25. Post-Emergency Strength Check

Fixing physical counts does not automatically mean the teams are balanced.

After emergency count correction:

```csharp
Server.NextFrame(EvaluateTeamBalance);
```

runs the normal strength model.

Example:

```text
10v8
→ emergency count correction
→ 9v9
→ projected strength still 61/39
→ normal 58/42 trigger is violated
→ best 1-for-1 skill swap
→ projected 52/48
```

If the corrected result is already acceptable:

```text
10v8
→ 9v9
→ projected 54/46
→ no additional skill swap
```

---

# 26. Required/Recommended CS2 ConVars

Because WarcraftAutoBalance owns team redistribution, disable native CS2 balancing:

```cfg
mp_autoteambalance 0
mp_limitteams 0
```

The package includes:

```text
warcraft_autobalance_server.cfg
```

You can execute it from the server configuration or place the equivalent convars directly in your normal server config.

Do not leave native auto-balance active while testing this plugin.

Otherwise CS2 may undo or interfere with WarcraftAutoBalance decisions.

---

# 27. Plugin Deployment

After a successful Release build, deploy the plugin under the normal CounterStrikeSharp plugin directory, for example:

```text
game/csgo/addons/counterstrikesharp/plugins/WarcraftAutoBalance/
```

The directory should contain the plugin assembly and required managed dependencies.

Conceptually:

```text
game/
└── csgo/
    └── addons/
        └── counterstrikesharp/
            ├── plugins/
            │   ├── WarcraftMod/
            │   └── WarcraftAutoBalance/
            │       ├── WarcraftAutoBalance.dll
            │       └── ...
            │
            └── shared/
                └── WarcraftBalance.Contracts/
                    └── WarcraftBalance.Contracts.dll
```

Use the dependency layout expected by the CounterStrikeSharp version installed on the server.

---

# 28. Shared Contract Deployment

The exact same compiled:

```text
WarcraftBalance.Contracts.dll
```

must be available to both plugins.

Recommended shared location:

```text
game/csgo/addons/counterstrikesharp/shared/WarcraftBalance.Contracts/
```

Do not deploy different builds of the contract DLL to each plugin.

That can cause type identity/capability resolution problems.

---

# 29. Plugin Load Order and Resynchronization

The Warcraft mod should not assume AutoBalance always loads first.

On normal race events:

```csharp
GetBalanceService()?.SetPlayerRace(...);
```

is safe if the service is absent.

For hot reloads, provide a resynchronization path.

After the balancer becomes available, iterate currently connected Warcraft players and resend their current base race and level:

```csharp
foreach (CCSPlayerController player in activePlayers)
{
    PlayerRaceState state =
        GetCurrentRaceState(player);

    GetBalanceService()?.SetPlayerRace(
        player,
        state.RaceName,
        state.Level,
        state.MaxLevel
    );
}
```

The exact hook depends on the architecture of the main Warcraft mod.

The important rule is:

```text
AutoBalance reload
→ current connected players must eventually be re-sent
→ current base race/level
```

---

# 30. Team-Owned Warcraft Entities

Automatic team movement can interact badly with Warcraft entities such as:

```text
clones
summons
portals
traps
wards
projectiles
auras
team-owned NPCs
```

The main Warcraft mod should have one central team-change cleanup path.

Conceptually:

```csharp
void OnAutoBalanceTeamChanged(
    CCSPlayerController player,
    CsTeam oldTeam,
    CsTeam newTeam)
{
    RemoveOrRetargetClones(player);
    RemoveOrRetargetSummons(player);
    RemoveOrRetargetPortals(player);
    RemoveOrRetargetTraps(player);
    RemoveOrRetargetProjectiles(player);
    RefreshTeamAuras(player);
}
```

Do not duplicate this cleanup independently in several race classes if the Warcraft framework can centralize it.

A future extension of `IWarcraftBalanceService` or a Warcraft-side team-change hook can formalize this.

---

# 31. Persistence

Persistent data is stored at:

```text
<WarcraftAutoBalance ModuleDirectory>/balance_data.json
```

The plugin loads JSON into in-memory dictionaries.

Runtime player lookup is therefore effectively:

```csharp
Dictionary<ulong, PlayerBalanceData>
```

using SteamID64.

The JSON file itself is not a database index.

Performance comes from:

```text
disk JSON
→ load once
→ dictionaries in RAM
→ gameplay reads/writes RAM
→ batched persistence
```

This is appropriate for a single Warcraft server and a normal community-server player history.

---


# 31A. v2.9 SQLite Persistence Architecture

v2.9 replaces `balance_data.json` as the active persistence backend with
`balance.db`.

The architecture is intentionally:

```text
persistent history → SQLite
active gameplay    → RAM
```

The plugin does **not** query SQLite on every `player_hurt`, death, assist, or
objective event. Those hot paths remain dictionary/state updates.

Historical player scale is separated from active server population:

```text
plugin startup
→ open SQLite
→ enable WAL / NORMAL synchronous / foreign keys
→ load metadata
→ load all race statistics only
→ do NOT load every historical player

player first needed
→ SELECT by SteamID64
→ SELECT last 12 RecentRounds
→ active PlayerBalanceData in RAM

player disconnects
→ save that player's state in one transaction
→ evict from _players RAM dictionary
```

Therefore a very large historical player table does not create a matching
in-memory player population.

## Schema

```sql
CREATE TABLE Players (
    SteamId INTEGER PRIMARY KEY,
    Name TEXT NOT NULL,
    HistoricalRating REAL NOT NULL DEFAULT 1000,
    LifetimeRounds INTEGER NOT NULL DEFAULT 0,
    LifetimeWins INTEGER NOT NULL DEFAULT 0,
    CreatedUtc TEXT NOT NULL,
    LastSeenUtc TEXT NOT NULL
);
```

The SteamID64 values used by Steam fit inside SQLite's signed 64-bit INTEGER
range. The code uses a checked conversion to avoid silent overflow.

Recent balance history is normalized into:

```sql
CREATE TABLE RecentRounds (
    SteamId INTEGER NOT NULL,
    Sequence INTEGER NOT NULL,
    Damage INTEGER NOT NULL,
    Kills INTEGER NOT NULL,
    Deaths INTEGER NOT NULL,
    Assists INTEGER NOT NULL,
    Survived INTEGER NOT NULL,
    Contributed INTEGER NOT NULL,
    TeamWon INTEGER NOT NULL,
    ObjectivePoints REAL NOT NULL,
    PRIMARY KEY (SteamId, Sequence),
    FOREIGN KEY (SteamId)
        REFERENCES Players(SteamId)
        ON DELETE CASCADE
);
```

Only the rolling `RollingWindowRounds` records are retained by the active model.
The current default is 12. A player save replaces at most those bounded rows.

Race statistics remain:

```sql
CREATE TABLE RaceStats (
    RaceName TEXT PRIMARY KEY COLLATE NOCASE,
    RoundsPlayed INTEGER NOT NULL DEFAULT 0,
    ActualWins REAL NOT NULL DEFAULT 0,
    ExpectedWins REAL NOT NULL DEFAULT 0,
    LastCalculatedModifier REAL NOT NULL DEFAULT 1.0
);
```

Global/plugin state such as RoundNumber and SchemaVersion is stored in `Metadata`.

## WAL

The connection executes:

```sql
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA foreign_keys = ON;
PRAGMA busy_timeout = 5000;
```

WAL is appropriate for this local server database, particularly if a future
admin/reporting process reads the database while the game server is running.

When WAL is active, `balance.db-wal` and `balance.db-shm` may exist while the
server is running. They are normal SQLite files.

## Transaction strategy

Every completed round uses one transaction for:

```text
RoundNumber metadata
currently resident players
their bounded RecentRounds
all race profiles
```

A disconnecting player also receives one small transaction before being evicted from RAM. Normal balancing still runs every four rounds; persistence and balance cadence are intentionally separate.

The plugin intentionally does not perform disk writes in high-frequency combat
event handlers.

## Automatic JSON migration

On startup, if legacy `balance_data.json` exists and the new SQLite Players table
is empty:

```text
read old JSON
→ begin transaction
→ import RoundNumber
→ import Players
→ import each player's recent 12-round list
→ import RaceStats
→ COMMIT
```

Only after a successful commit is the JSON renamed:

```text
balance_data.json.migrated-YYYYMMDD-HHMMSS
```

If import fails, the transaction rolls back/disposes and the original JSON stays
in place.

This makes migration recoverable and preserves the existing rating history.


# 32. SteamID64 Identity

Persistent player data is keyed by:

```text
SteamID64
```

The player's name is metadata only.

Test this explicitly:

1. join as PlayerNameA;
2. accumulate rating data;
3. stop server/plugin and confirm JSON is saved;
4. change Steam display name;
5. reconnect;
6. confirm the same SteamID64 record is reused and the name field updates.

Do not use player slot or entity index as persistent identity.

---

# 33. Persistent Race Data

Race records retain approximately:

```text
RaceName
RoundsPlayed
ActualWins
ExpectedWins
LastCalculatedModifier
```

The race dictionary is case-insensitive.

Race naming should nevertheless be canonical in the main Warcraft mod.

Avoid sending:

```text
Internet Troll
internet troll
InternetTroll
The Internet Troll
```

for the same race.

Use the actual internal/display race name consistently.

---

# 34. Saving Behavior

Persistent data is saved on:

```text
plugin unload
scheduled balance intervals
emergency corrections where applicable
```

The plugin writes through a temporary file and replaces the primary JSON file.

This is safer than writing directly over the active file.

Still back up:

```text
balance_data.json
```

before major migrations or experimental rating changes.

---

# 35. Admin Diagnostics

The plugin exposes:

```text
css_balance
```

and, when `!` is configured as a public CounterStrikeSharp command trigger:

```text
!balance
```

The command is protected by:

```csharp
[RequiresPermissions("@css/generic")]
```

so it is intended for administrators.

Diagnostics include information such as:

```text
T/CT average rating
expected win chance
individual player ratings
ADR
K/D
KAST
historical rating
race
race modifier
level modifier
recommended swap
learned race performance
```

Low-pop diagnostics use the low-pop partition/power model rather than pretending the match must be equal human counts.

---

# 36. First Startup Test

Before testing automatic movement:

1. start the server;
2. confirm CounterStrikeSharp loads;
3. confirm the Warcraft mod loads;
4. confirm WarcraftAutoBalance loads;
5. inspect server logs for the AutoBalance startup message;
6. confirm no plugin exceptions;
7. connect one real Steam player;
8. select a Warcraft race;
9. verify the Warcraft mod sends the race assignment;
10. execute `!balance` as an authorized admin.

Do not begin live balancing tests until this basic path works.

---

# 37. Race Synchronization Test

Use one player and deliberately change races.

Example:

```text
Round 1: Internet Troll
Round 2: Human Alliance
Round 3: Internet Troll
```

After each actual selection, ensure the balance integration calls:

```csharp
SetPlayerRace(...)
```

with the new base race and level.

Then test a temporary transformation:

```text
Internet Troll
→ dies
→ ability temporarily respawns as Murloc summon/form
```

The balance race should remain:

```text
Internet Troll
```

unless the player actually selected Murloc as their new base race.

---

# 38. Respawn/KAST Test

Use a race capable of respawning.

Test:

```text
Player starts round
→ dies
→ race respawns player
→ player is alive when round ends
```

Expected balance statistics:

```text
Deaths >= 1
Survived = false
```

The respawn should not erase the death.

Then test a player who never dies:

```text
Deaths = 0
Survived = true
```

---

# 39. Bot Isolation Test

Run a server with humans and bots.

Have a human:

```text
damage bots
kill bots
assist against bots
```

Those actions should not inflate the human's rating statistics.

Then fight another real human and confirm normal damage/kills/assists are recorded.

Bots should affect physical team filling only.

---

# 40. Normal 4-Round Balance Test

Use more than six humans.

Create deliberately unequal player ratings if necessary using test data.

Verify:

```text
round 1 → no scheduled balance
round 2 → no scheduled balance
round 3 → no scheduled balance
round 4 → EvaluateTeamBalance
```

Test a projected matchup below the trigger:

```text
56/44
```

Expected:

```text
no skill swap
```

Test:

```text
60/40
```

Expected:

```text
best available single swap is evaluated
```

Confirm the plugin chooses the candidate closest to 50/50 rather than the first candidate below 55/45.

---

# 41. Repeated-Move Test

Because move protection is intentionally gone, explicitly test:

```text
Player A moved at balance pass
→ several players change race or leave
→ next balance pass determines Player A is again the best move
```

Expected:

```text
Player A is eligible again
```

There should be no:

```text
LastMovedRound
12-round cooldown
protected flag
```

preventing the decision.

---

# 42. Low-Pop 3-Human Test

Use:

```text
3 real humans
7 bots
```

Give one human a substantially higher balance rating.

Expected possible human arrangement:

```text
strong player
vs
other two humans
```

Bots should then redistribute based on the selected logical human partition.

The plugin should not use stale human `Team` properties from the same frame to calculate bot requirements.

---

# 43. Extreme Low-Pop Test

Test up to six humans.

Construct a scenario where one player has substantially higher effective power.

Possible valid result:

```text
1v5
```

The plugin should choose this only if the nonlinear power model says it is the closest human-strength partition.

Do not expect 1v5 merely because one player has the highest rating.

The combined opposing effective power still matters.

---

# 44. Low-Pop Bot Availability Test

Test asymmetric human arrangements with different bot counts:

```text
1v5 humans + 0 bots
1v5 humans + 2 bots
1v5 humans + 7 bots
2v4 humans + 3 bots
```

The bot redistribution algorithm should choose the physical distribution that minimizes final physical team-count difference using only the bots that actually exist.

It should not repeatedly select the same bot during one pass.

---

# 45. Emergency Disconnect Test: 10v10 → 10v8

Start:

```text
10 vs 10
```

Disconnect two players from the same team within approximately half a second.

Expected:

```text
one coalesced emergency evaluation
→ physical count correction
→ immediate post-correction strength evaluation
```

Confirm the plugin does not wait four rounds.

---

# 46. Emergency Disconnect Test: 10v10 → 10v6

Disconnect four players from one side quickly.

This tests multiple emergency moves.

Verify:

```text
each selected bot/human is logically removed from source
each is logically added to destination
the same bot is not selected repeatedly
```

The final emergency logical counts should be correct even if engine-side `Team` state updates one frame later.

---

# 47. Uneven Round-Start Expectation Test

Start a real round at:

```text
5 humans vs 4 humans
```

with approximately equal average player ratings.

The expected win probability should no longer be approximately 50/50.

The five-human side receives the configured numerical-strength adjustment.

Then test:

```text
4 elite humans
vs
5 weak humans
```

The count adjustment should supplement player skill rather than replace it.

---

# 48. Disconnect During Round Test

Because race expectation is frozen at round start:

```text
5v5 begins
→ expectation captured
→ one player disconnects mid-round
```

The race-learning expected probability for that already-started round remains based on the round-start state.

This avoids retroactively changing the baseline after the outcome is already in progress.

The emergency balancer can still correct the live team population separately.

---

# 49. Warmup, Restart, Halftime, and Map Transition Testing

Before production deployment, explicitly test:

```text
warmup rounds
mp_restartgame
map changes
halftime/team swaps if applicable
plugin hot reload
Warcraft mod hot reload
```

Watch for:

```text
false round-stat snapshots
unexpected race-learning rounds
balance calls during noncompetitive transitions
team-owned Warcraft entities surviving team switches
```

If the server's exact game mode produces false learning during warmup/halftime, add explicit game-rules suppression before production.

This remains an environment-specific hardening area.

---

# 50. Custom Damage Attribution

The current direct stat model relies on CS2 game events.

For normal player weapon damage this is appropriate.

For custom Warcraft abilities, determine how the main mod reports:

```text
ability damage
summon damage
projectile damage
damage-over-time
reflected damage
clone damage
```

If the resulting `EventPlayerHurt.Attacker` is the owning human, the existing system can count it.

If the attacker is:

```text
world
summon entity
NPC
projectile controller
another synthetic entity
```

the standalone balancer cannot reliably infer ownership.

Do not guess.

The preferred future design is an explicit owner-attribution API.

---

# 51. Recommended Future Combat Attribution API

A future contract could add methods similar to:

```csharp
void RecordOwnedDamage(
    ulong ownerSteamId,
    ulong victimSteamId,
    int damage);

void RecordOwnedKill(
    ulong ownerSteamId,
    ulong victimSteamId);

void RecordOwnedAssist(
    ulong ownerSteamId,
    ulong victimSteamId);
```

The exact interface should be designed around the Warcraft mod's existing combat pipeline.

The main rule is:

```text
Warcraft mod knows ability ownership
→ Warcraft mod reports ownership
→ AutoBalance records statistics
```

rather than:

```text
AutoBalance guesses entity ownership
```

---

# 52. Team-Change Callback — Recommended Future Integration

Similarly, the Warcraft mod knows which entities belong to a player.

A future shared contract/event can communicate:

```text
AutoBalance moved player
old team
new team
```

The Warcraft mod can then clean or retarget:

```text
summons
clones
portals
traps
auras
projectiles
```

This is safer than AutoBalance attempting to understand every race implementation.

---

# 53. JSON Validation

After several rounds, inspect:

```text
balance_data.json
```

Confirm:

```text
SteamID64 records persist
names update
historical ratings change gradually
recent round history remains bounded
race rounds increase
actual race wins increase correctly
expected race wins are fractional
learned modifiers remain within configured bounds
```

Do not manually edit the live JSON while the plugin is actively running unless you understand when the plugin will next overwrite it.

---

# 54. Backup Strategy

At minimum, back up:

```text
balance_data.json
```

before:

```text
major rating formula changes
race modifier formula changes
season resets
large plugin upgrades
manual data edits
```

A simple dated copy is sufficient:

```text
balance_data_2026-08-30.json
```

---

# 55. When to Move From JSON to SQLite

JSON + in-memory dictionaries is appropriate now.

Consider SQLite later if you want:

```text
large historical datasets
per-season history
detailed per-race analytics
admin dashboards
top-player queries
long-term round history
multiple reporting tools
```

SQLite itself is free and supports automated reads/writes.

If migrating, keep the same runtime architecture:

```text
SQLite
→ load active/persistent state
→ RAM dictionaries during gameplay
→ batched database writes
```

Do not execute synchronous SQL on every `player_hurt` event.

A SteamID64 player table can use:

```sql
CREATE TABLE Players (
    SteamId INTEGER PRIMARY KEY,
    Name TEXT NOT NULL,
    HistoricalRating REAL NOT NULL,
    LifetimeRounds INTEGER NOT NULL DEFAULT 0,
    LifetimeWins INTEGER NOT NULL DEFAULT 0
);
```

`SteamId INTEGER PRIMARY KEY` is already indexed by SQLite.

---

# 56. Performance Guidelines

Keep high-frequency event handlers cheap.

Good:

```text
dictionary lookup
integer increment
small state update
```

Avoid:

```text
disk writes per hit
SQL query per hit
large LINQ scans per hit
network requests per hit
race-wide entity scans per hit
```

More expensive balance calculations occur only occasionally:

```text
every four rounds
low-pop rebalance
emergency disconnect correction
admin diagnostics
```

For typical community-server populations, evaluating all T/CT single-swap pairs is trivial.

Example:

```text
15 T × 15 CT = 225 candidate swaps
```

every four rounds is negligible compared with normal CS2 simulation and Warcraft entity processing.

---

# 57. Production Configuration Checklist

Before going live:

- [ ] Metamod loads correctly.
- [ ] CounterStrikeSharp loads correctly.
- [ ] Main Warcraft mod loads correctly.
- [ ] WarcraftAutoBalance builds against the server's exact CounterStrikeSharp API.
- [ ] Shared contracts assembly is identical for both plugins.
- [ ] Capability ID is exactly `warcraft:autobalance`.
- [ ] Race selection calls `SetPlayerRace()`.
- [ ] Race level changes call `SetPlayerRace()`.
- [ ] Temporary summon/respawn forms do **not** overwrite base race.
- [ ] True no-race state calls `ClearPlayerRace()`.
- [ ] `mp_autoteambalance 0`.
- [ ] `mp_limitteams 0`.
- [ ] `!balance` is admin-only.
- [ ] `balance_data.json` is writable.
- [ ] SteamID64 persistence has been tested.
- [ ] Bot kills do not affect human rating.
- [ ] Respawned players do not regain survival credit.
- [ ] Low-pop 1v2/1v3/1v4/1v5 tests have been performed.
- [ ] 10v10 → 10v8 emergency test passes.
- [ ] 10v10 → 10v6 multi-move test passes.
- [ ] Warcraft summons/clones are cleaned or retargeted on team movement.
- [ ] Warmup/restart/halftime behavior has been tested.
- [ ] Hot-reload race resynchronization has been tested.
- [ ] Persistent data is backed up before production rollout.

---

# 58. Recommended Integration Responsibility Split

## Main Warcraft Mod owns

```text
selected/base race
race level
race abilities
respawns
summon ownership
projectile ownership
clones
portals
traps
auras
race-specific cleanup
```

## WarcraftAutoBalance owns

```text
SteamID64 player ratings
recent performance window
historical skill
race performance learning
race modifiers
team-strength calculations
low-pop human partitioning
bot redistribution
emergency disconnect correction
balance diagnostics
persistent balance data
```

This separation is important.

The balancer should understand **strength and teams**.

The Warcraft mod should understand **race mechanics and entity ownership**.

---

# 59. Recommended Rollout Order

Do not enable every behavior at once on a populated live server.

Recommended order:

### Phase 1 — Data only

Install the plugin and race integration.

Verify:

```text
player ratings
race assignments
JSON persistence
diagnostics
respawn statistics
```

### Phase 2 — Controlled low-pop test

Use admins/testers.

Verify:

```text
1v2
1v3
bot redistribution
race changes
respawns
summon transformations
```

### Phase 3 — Emergency population tests

Verify:

```text
10v10 → 10v8
10v10 → 10v6
multiple simultaneous disconnects
```

### Phase 4 — Normal automatic balancing

Enable/test the four-round production behavior with a larger group.

Watch:

```text
frequency of moves
predicted win percentages
player complaints
race modifier movement
unexpected Warcraft entity behavior
```

### Phase 5 — Tune from actual server data

After enough real rounds, review:

```text
race modifier distribution
historical rating distribution
low-pop outcomes
58/42 trigger frequency
75-point population adjustment
300 low-pop power scale
```

Tune only after collecting enough data to see a real pattern.

---

# 60. Current Default Values

| Setting | v2.7 Default |
|---|---:|
| Scheduled balance interval | 4 rounds |
| Recent performance window | 12 rounds |
| Normal balance trigger | 58/42 |
| Preferred post-balance target | 55/45 |
| Low-pop threshold | 6 humans |
| Low-pop power scale | 300 |
| Emergency disconnect delay | 0.50 sec |
| Emergency physical count difference | 2 |
| New historical rating | 1000 |
| Historical learning rate | 5% |
| Race minimum sample | 40 rounds |
| Race modifier range | 0.95–1.05 |
| Race-level modifier range | 0.98–1.02 |
| Uneven-human expectation adjustment | 75 rating / extra human |
| Swap protection | None |

---

# 61. Final Architecture

The intended production flow is:

```text
Player connects
        ↓
Warcraft profile loads
        ↓
Main mod determines base race + level
        ↓
SetPlayerRace()
        ↓
Round starts
        ↓
Human/race/team snapshot
        ↓
Pre-round expected win probability frozen
        ↓
Cheap combat/objective event collection
        ↓
Round ends
        ↓
Recent + historical player rating update
        ↓
Race actual-vs-expected learning
        ↓
Every 4 rounds:
normal balance evaluation
```

For low population:

```text
2–6 humans
        ↓
calculate nonlinear human power
        ↓
evaluate every human partition
        ↓
choose closest power split
        ↓
switch humans
        ↓
use known logical human counts
        ↓
redistribute existing bots
```

For disconnect emergencies:

```text
disconnect(s)
        ↓
0.50 sec coalescing window
        ↓
physical count difference >= 2?
        ↓
yes
        ↓
logical emergency T/CT state
        ↓
move bot if possible
otherwise choose rating-aware human
        ↓
repeat until physical difference <= 1
        ↓
next frame
        ↓
normal 58/42 strength check
```

For Warcraft respawns:

```text
base race selected
        ↓
player dies
        ↓
Died = true
        ↓
race respawns player / summon form
        ↓
base race assignment remains unchanged
        ↓
survival credit remains false
        ↓
race receives long-term outcome attribution
```

That is the intended v2.9 implementation model.


# v2.10 Recent-Round Persistence Optimization

Recent rounds are now permanent, ordered records while they are inside the
rolling window.

Each snapshot has a durable `RoundId` and `PlayedUtc`. Missing rounds are not
materialized, so a player with 4 real rounds is evaluated from 4 records rather
than 4 records plus 8 artificial zero rounds.

Reconnects query the indexed newest rows with `ORDER BY RoundId DESC LIMIT 12`,
then reverse that tiny result into chronological order in RAM.

The write path uses a runtime `PendingPersistence` flag. Only newly completed
snapshots are inserted. The flag is cleared only after the containing SQLite
transaction successfully commits; failed transactions leave data pending for a
later retry.

This avoids the v2.9 delete-and-rewrite behavior and also avoids attempting 12
`INSERT ... DO NOTHING` statements per player every round. In the normal case,
one active player produces exactly one RecentRounds INSERT for one completed
round.

The SQLite table remains bounded to the rolling window using the composite
`(SteamId, RoundId DESC)` index and an indexed prune statement.

Cross-session behavior is natural:

```text
Day 1: 4 rounds → DB has 4
Day 2: load 4, play 4 → DB has 8
Day 3: load 8, play 4 → DB has 12
next round → oldest record pruned, newest 12 remain
```
