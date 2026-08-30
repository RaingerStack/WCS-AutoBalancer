# WarcraftAutoBalance v2.4 — Full Server & Warcraft Mod Integration Guide

This guide walks through installing, building, deploying, and integrating **WarcraftAutoBalance v2.4** with a CounterStrikeSharp-based Warcraft CS2 server.

The intended architecture is:

```text
CS2 Dedicated Server
└── CounterStrikeSharp
    ├── Main Warcraft Mod
    │   └── knows each player's race and race level
    │
    └── WarcraftAutoBalance
        ├── tracks player performance
        ├── learns persistent player ratings
        ├── learns race strength
        ├── balances every 4 rounds
        ├── handles low-population balancing
        └── immediately reacts to severe disconnect imbalance
```

The two plugins communicate through the CounterStrikeSharp **Shared Plugin API / PluginCapability** system.

---

# 1. Requirements

Before installing this plugin, the server should already have:

- A working Counter-Strike 2 dedicated server.
- Metamod:Source installed.
- CounterStrikeSharp installed.
- A working Warcraft mod built on CounterStrikeSharp.
- .NET 8 SDK installed on the machine used to compile plugins.
- Access to the server's `game/csgo/addons/counterstrikesharp/` directory.

A normal CounterStrikeSharp layout looks approximately like:

```text
game/
└── csgo/
    └── addons/
        ├── metamod/
        └── counterstrikesharp/
            ├── api/
            ├── bin/
            ├── dotnet/
            ├── gamedata/
            ├── plugins/
            └── shared/
```

Official CounterStrikeSharp installation documentation:

https://docs.cssharp.dev/docs/guides/getting-started.html

---

# 2. What WarcraftAutoBalance Does

The balancer maintains a hidden rating for every human player.

The current rating model uses:

```text
35%  Recent ADR
25%  Recent K/D
20%  Persistent historical rating
10%  KAST / round contribution
10%  Objective contribution
```

The player's base rating is then modified slightly by:

```text
Learned Race Modifier
×
Race Level Modifier
```

Bots are excluded from player rating and race-learning statistics.

## Normal population

With more than 6 humans:

- Balance is normally checked every 4 rounds.
- Team average ratings are compared.
- Expected win probability is calculated.
- Rebalancing normally begins at approximately 58/42.
- The plugin attempts the best single human-for-human swap.
- It aims to get the matchup to approximately 55/45 or better.
- Players normally receive 12 rounds of move protection.

## Low population

With 2–6 humans:

- Human counts are not forced to be equal.
- Every possible human team partition is evaluated.
- Effective combat power uses a nonlinear curve.
- 1v2, 1v3, 1v4, and 1v5 are all possible.
- Bots are used only as physical team fillers.

This allows an exceptional player on a strong race to be placed against several weaker players.

## Emergency disconnect balancing

A severe player-count imbalance bypasses the normal 4-round wait.

Example:

```text
10v10
↓
two CT players disconnect
↓
10v8
↓
0.50 second disconnect coalescing period
↓
balancer evaluates the final 10v8 state
↓
best correction is selected using ratings
↓
9v9
↓
normal 58/42 strength check runs immediately
```

The emergency system changes the **timing**, not the rating model.

---

# 3. Recommended Project Structure

For clean integration, use three projects or logical assemblies:

```text
WarcraftServer/
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

`WarcraftBalance.Contracts` contains only the interface shared by both plugins.

It should contain no gameplay logic.

---

# 4. Create the Shared Contract

Create a new .NET class library:

```bash
dotnet new classlib -n WarcraftBalance.Contracts
```

Target .NET 8.

Create:

```text
WarcraftBalance.Contracts/IWarcraftBalanceService.cs
```

Use:

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

The contract project needs access to CounterStrikeSharp types.

Either reference the installed CounterStrikeSharp API DLL or add the API package.

Example:

```bash
dotnet add package CounterStrikeSharp.API
```

Build:

```bash
dotnet build -c Release
```

You should get something similar to:

```text
WarcraftBalance.Contracts/bin/Release/net8.0/WarcraftBalance.Contracts.dll
```

---

# 5. Install the Shared Contract on the Server

CounterStrikeSharp shared API contracts belong beneath the `shared` directory.

Create:

```text
game/csgo/addons/counterstrikesharp/shared/WarcraftBalance.Contracts/
```

Copy:

```text
WarcraftBalance.Contracts.dll
```

into it.

Result:

```text
counterstrikesharp/
└── shared/
    └── WarcraftBalance.Contracts/
        └── WarcraftBalance.Contracts.dll
```

Both the Warcraft mod and WarcraftAutoBalance should reference this exact contract assembly.

Do not compile two different incompatible copies of the interface.

---

# 6. Add the Contract Reference to WarcraftAutoBalance

In the WarcraftAutoBalance project, reference the shared contract project during development.

Example `.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="CounterStrikeSharp.API" Version="*" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\WarcraftBalance.Contracts\WarcraftBalance.Contracts.csproj" />
  </ItemGroup>

</Project>
```

If your friend already has a working CounterStrikeSharp project setup, use the same API version/reference strategy used by the main Warcraft plugin.

Avoid blindly changing package versions on a working server.

---

# 7. Make WarcraftAutoBalance Implement the Shared Interface

At the top of `WarcraftAutoBalance.cs`, add:

```csharp
using CounterStrikeSharp.API.Core.Capabilities;
using WarcraftBalance.Contracts;
```

Change:

```csharp
public class WarcraftAutoBalancePlugin : BasePlugin
```

to:

```csharp
public class WarcraftAutoBalancePlugin :
    BasePlugin,
    IWarcraftBalanceService
```

The existing methods already match the interface:

```csharp
public void SetPlayerRace(
    CCSPlayerController player,
    string raceName,
    int currentLevel,
    int maximumLevel)
```

and:

```csharp
public void ClearPlayerRace(
    CCSPlayerController player)
```

---

# 8. Expose WarcraftAutoBalance as a PluginCapability

Inside `WarcraftAutoBalancePlugin`, add:

```csharp
public static PluginCapability<IWarcraftBalanceService>
    BalanceCapability { get; } =
        new("warcraft:autobalance");
```

The string:

```text
warcraft:autobalance
```

is the capability ID.

It must be identical in both plugins.

---

# 9. Register the Capability

Inside the balancer's existing:

```csharp
public override void Load(bool hotReload)
```

add:

```csharp
Capabilities.RegisterPluginCapability(
    BalanceCapability,
    () => this
);
```

A recommended placement is immediately after persistent data loads:

```csharp
public override void Load(bool hotReload)
{
    LoadPersistentData();

    Capabilities.RegisterPluginCapability(
        BalanceCapability,
        () => this
    );

    RegisterEventHandler<EventRoundStart>(OnRoundStart);
    RegisterEventHandler<EventRoundEnd>(OnRoundEnd);

    RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
    RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
    RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);

    RegisterEventHandler<EventBombPlanted>(OnBombPlanted);
    RegisterEventHandler<EventBombDefused>(OnBombDefused);

    Logger.LogInformation(
        "[WarcraftBalance] Loaded with {Players} player ratings and {Races} race profiles.",
        _players.Count,
        _raceStats.Count
    );
}
```

Now the Warcraft mod can obtain the balancing service without directly depending on the implementation class.

---

# 10. Add the Contract Reference to the Main Warcraft Mod

Add the same contract project/reference to the main Warcraft mod.

The Warcraft mod does **not** need a compile-time reference to:

```text
WarcraftAutoBalance.dll
```

It only needs:

```text
WarcraftBalance.Contracts.dll
```

This keeps the two plugins loosely coupled.

At the top of the Warcraft plugin file:

```csharp
using CounterStrikeSharp.API.Core.Capabilities;
using WarcraftBalance.Contracts;
```

---

# 11. Declare the Capability in the Main Warcraft Mod

Inside the main Warcraft plugin class, add:

```csharp
public static PluginCapability<IWarcraftBalanceService>
    BalanceCapability { get; } =
        new("warcraft:autobalance");
```

Again, the string must exactly match the provider:

```text
warcraft:autobalance
```

---

# 12. Create a Safe Helper in the Main Mod

Add a helper function to the Warcraft mod:

```csharp
private IWarcraftBalanceService? GetBalanceService()
{
    return BalanceCapability.Get();
}
```

Always assume it can return `null`.

That allows the Warcraft mod to continue functioning even if the balancer is temporarily missing, disabled, or hot-reloading.

Do not make Warcraft gameplay depend on the balancer being present.

---

# 13. Send Race Information When a Player Selects a Race

Locate the section of the Warcraft mod where a player's race is successfully selected.

After the race has been assigned internally, call:

```csharp
var balance = GetBalanceService();

balance?.SetPlayerRace(
    player,
    race.Name,
    playerRaceLevel,
    race.MaximumLevel
);
```

The exact property names depend on the main Warcraft mod.

For example, if your current code looks like:

```csharp
playerData.CurrentRace = selectedRace;
playerData.RaceLevel = selectedLevel;
```

add:

```csharp
var balance = GetBalanceService();

balance?.SetPlayerRace(
    player,
    selectedRace.Name,
    selectedLevel,
    selectedRace.MaxLevel
);
```

Only call the balancer **after** the Warcraft mod considers the race selection valid.

---

# 14. Update the Balancer When Race Level Changes

Whenever the player's active race level changes, call `SetPlayerRace` again.

Example:

```csharp
playerData.RaceLevel++;

var balance = GetBalanceService();

balance?.SetPlayerRace(
    player,
    playerData.CurrentRace.Name,
    playerData.RaceLevel,
    playerData.CurrentRace.MaxLevel
);
```

`SetPlayerRace` is intentionally safe to call repeatedly.

It replaces the current assignment.

---

# 15. Clear Race Information When Necessary

When a player no longer has an active race, call:

```csharp
GetBalanceService()?.ClearPlayerRace(player);
```

Good places include:

- player explicitly deselects a race;
- Warcraft profile resets;
- race becomes invalid;
- race is removed by an admin;
- plugin logic intentionally places player into a no-race state.

A normal team switch does **not** necessarily require clearing the race if Warcraft races persist between T and CT.

---

# 16. Restore Race Information After Player Connect / Profile Load

This step is important.

WarcraftAutoBalance stores player/race statistics persistently, but the currently selected race assignment is runtime state.

After the Warcraft mod finishes loading a player's profile, send their current race to the balancer.

Example:

```csharp
private void SyncRaceToBalancer(
    CCSPlayerController player,
    WarcraftPlayerData data)
{
    if (data.CurrentRace == null)
    {
        GetBalanceService()?.ClearPlayerRace(player);
        return;
    }

    GetBalanceService()?.SetPlayerRace(
        player,
        data.CurrentRace.Name,
        data.RaceLevel,
        data.CurrentRace.MaxLevel
    );
}
```

Call this after the player's Warcraft profile/race data is available.

Do not send race information before the main mod knows the correct race.

---

# 17. Handle Hot Reloads

CounterStrikeSharp can hot reload plugins.

Because capability resolution can temporarily return `null`, always use:

```csharp
BalanceCapability.Get()
```

when needed instead of permanently caching the service forever.

Good:

```csharp
BalanceCapability.Get()?.SetPlayerRace(...);
```

Less desirable:

```csharp
private IWarcraftBalanceService _balanceService;
```

held forever without refreshing it.

After a Warcraft mod hot reload, resync all currently connected humans.

Pseudo-code:

```csharp
foreach (var player in Utilities.GetPlayers())
{
    if (!player.IsValid || player.IsBot)
        continue;

    var profile = GetWarcraftProfile(player);

    if (profile?.CurrentRace == null)
        continue;

    BalanceCapability.Get()?.SetPlayerRace(
        player,
        profile.CurrentRace.Name,
        profile.RaceLevel,
        profile.CurrentRace.MaxLevel
    );
}
```

---

# 18. Build WarcraftAutoBalance

From its project directory:

```bash
dotnet restore
dotnet build -c Release
```

Expected output:

```text
bin/
└── Release/
    └── net8.0/
        ├── WarcraftAutoBalance.dll
        ├── WarcraftAutoBalance.deps.json
        └── WarcraftAutoBalance.pdb
```

The exact files depend on the project configuration.

---

# 19. Install WarcraftAutoBalance on the Server

Create:

```text
game/csgo/addons/counterstrikesharp/plugins/WarcraftAutoBalance/
```

Copy the built files into it.

Example:

```text
counterstrikesharp/
└── plugins/
    └── WarcraftAutoBalance/
        ├── WarcraftAutoBalance.dll
        ├── WarcraftAutoBalance.deps.json
        └── WarcraftAutoBalance.pdb
```

If additional dependency DLLs are produced and are not supplied by CounterStrikeSharp/shared resolution, copy those as required.

---

# 20. Install / Update the Main Warcraft Mod

Build the modified Warcraft mod.

Deploy its normal output exactly as you already deploy the Warcraft plugin.

Do not replace unrelated Warcraft configuration or player data files.

At this point both plugins should reference the shared contract:

```text
shared/
└── WarcraftBalance.Contracts/
    └── WarcraftBalance.Contracts.dll
```

---

# 21. Recommended Final Server Layout

Example:

```text
game/csgo/addons/counterstrikesharp/
├── api/
├── bin/
├── dotnet/
├── gamedata/
├── plugins/
│   ├── WarcraftMod/
│   │   ├── WarcraftMod.dll
│   │   ├── WarcraftMod.deps.json
│   │   └── ...
│   │
│   └── WarcraftAutoBalance/
│       ├── WarcraftAutoBalance.dll
│       ├── WarcraftAutoBalance.deps.json
│       ├── WarcraftAutoBalance.pdb
│       └── balance_data.json       <-- created automatically later
│
└── shared/
    └── WarcraftBalance.Contracts/
        └── WarcraftBalance.Contracts.dll
```

---

# 22. First Startup

Restart the server.

Watch the console.

You should see a message similar to:

```text
[WarcraftBalance] Loaded with 0 player ratings and 0 race profiles.
```

On later starts it may say:

```text
[WarcraftBalance] Loaded with 145 player ratings and 23 race profiles.
```

If the plugin does not load, check CounterStrikeSharp's plugin list and server console errors before testing gameplay.

---

# 23. Verify the Capability Connection

Temporarily add a log message in the Warcraft mod after resolving the service:

```csharp
var balance = BalanceCapability.Get();

if (balance == null)
{
    Logger.LogWarning(
        "WarcraftAutoBalance capability was not available."
    );
}
else
{
    Logger.LogInformation(
        "WarcraftAutoBalance capability connected."
    );
}
```

On server startup/profile sync you want:

```text
WarcraftAutoBalance capability connected.
```

If it returns null:

1. Confirm WarcraftAutoBalance loaded.
2. Confirm both plugins use exactly:

```text
warcraft:autobalance
```

3. Confirm both use the same `IWarcraftBalanceService` contract.
4. Confirm the contract DLL exists under CounterStrikeSharp `shared`.
5. Check assembly/version errors in server console.

---

# 24. Verify Race Sync

Temporarily log the data being sent from Warcraft:

```csharp
Logger.LogInformation(
    "Balance sync: {Player} = {Race} level {Level}/{Max}",
    player.PlayerName,
    race.Name,
    currentLevel,
    maximumLevel
);
```

Change race several times.

Verify the correct race and level are sent.

Remove the debug logging after validation.

---

# 25. Verify Persistent Data

After several rounds, inspect:

```text
game/csgo/addons/counterstrikesharp/plugins/WarcraftAutoBalance/balance_data.json
```

It should contain persistent player and race data.

Do not manually edit this file while the server/plugin is actively writing it.

Back it up before manual edits.

The file should survive:

- map changes;
- plugin reloads;
- server restarts.

---

# 26. Verify the Admin Command

The plugin registers:

```text
css_balance
```

With `!` configured as the public chat trigger, admins can use:

```text
!balance
```

The command requires:

```text
@css/generic
```

Expected diagnostics include:

- T average rating;
- CT average rating;
- expected win probability;
- player ratings;
- ADR;
- K/D;
- KAST approximation;
- historical rating;
- current race;
- race modifier;
- level modifier;
- recommended normal-pop swap;
- low-pop recommended partition;
- learned race performance.

---

# 27. Test Normal Population Balancing

Use more than 6 humans.

Example:

```text
T average: 1215
CT average: 1010
```

Allow the server to reach a 4-round balance check.

If the predicted stronger-team win chance is under 58%, no swap should occur.

If above approximately 58%, the plugin should evaluate single swaps.

The selected swap should improve the predicted matchup.

---

# 28. Test Move Protection

After a normal skill swap, the player should normally be protected for:

```text
12 rounds
```

The balancer should prefer other valid moves during that period.

Emergency population correction can override this if the teams become severely uneven.

---

# 29. Test Low-Population Mode

Use 2–6 human players.

Bots can remain enabled.

The balancer should ignore bot skill entirely.

Test:

```text
1 strong human
vs
2 weaker humans
```

Then test more extreme rating differences.

The system is allowed to create:

```text
1v3
1v4
1v5
```

if the nonlinear effective-power calculation says that is the closest matchup.

The current power formula is approximately:

```text
power = exp((finalRating - 1000) / 300)
```

The `300` value is:

```csharp
LowPopulationPowerScale
```

Lower value:

```text
elite-vs-many becomes more aggressive
```

Higher value:

```text
elite-vs-many becomes more conservative
```

Do not tune this until the server has enough real rating data.

---

# 30. Test Bots in Low Population

Example:

```text
10 total players
3 humans
7 bots
```

If ratings justify:

```text
Human A
vs
Human B + Human C
```

the final physical teams may become:

```text
T:
Human A
4 bots

CT:
Human B
Human C
3 bots
```

Bots should not appear in persistent player ratings.

---

# 31. Test Emergency Disconnect Balancing

Start:

```text
10v10
```

Have two players from the same team disconnect nearly simultaneously.

Expected:

```text
10v10
→ 10v8
→ approximately 0.50 seconds
→ evaluate candidates
→ 9v9
```

The plugin does not simply move the top-rated player.

It evaluates each possible correction using projected ratings and expected win probabilities.

Then it immediately performs the normal 58/42 strength check again.

Possible result:

```text
10v8
→ candidate correction
→ 9v9 at 61/39
→ still too lopsided
→ best 1-for-1 skill swap
→ 9v9 at 52/48
```

---

# 32. Warcraft-Specific Team-Change Cleanup

This is strongly recommended.

Your main Warcraft mod may own team-sensitive objects such as:

- summons;
- clones;
- traps;
- portals;
- projectiles;
- auras;
- team-based target lists;
- team-colored entities.

When WarcraftAutoBalance switches a player, CS2 changes their team but your Warcraft plugin may still have entities associated with the player's previous team.

Create a Warcraft-side cleanup routine.

For example:

```csharp
public void CleanupPlayerTeamEntities(
    CCSPlayerController player)
{
    RemovePlayerSummons(player);
    RemovePlayerClones(player);
    RemovePlayerPortals(player);
    RemovePlayerTraps(player);
}
```

Exactly what gets removed depends on the Warcraft mod.

Ideally, the Warcraft mod should already have a general "player changed team" or cleanup pathway used for normal team changes.

The balancer should cause that same cleanup path to run.

Do not let an Internet Troll clone, summon, portal, or other entity silently remain owned by the old team after an auto-balance move.

---

# 33. Ability Damage Attribution

WarcraftAutoBalance currently learns damage from CS2's normal player-damage event.

If Warcraft abilities correctly attribute their damage to the owning player through CS2 events, nothing else is necessary.

If custom summons/projectiles deal damage without the owner being represented as the attacker, that damage may not contribute to the player's ADR.

For maximum accuracy, the long-term integration should expose an additional function such as:

```csharp
void RecordWarcraftDamage(
    CCSPlayerController player,
    int damage);
```

Then the Warcraft mod can explicitly report custom damage.

This is optional for initial deployment.

First test how your existing Warcraft damage appears in `player_hurt`.

---

# 34. Summon Kills and Race Effects

The same principle applies to kills and assists.

If the CS2 event system attributes a summon/projectile kill to the player, the current balancer receives it normally.

If the kill has no owning human attacker, it may not count toward the player's K/D contribution.

Do not artificially add duplicate kill credit.

Determine first whether the normal event already reports the owner.

---

# 35. Warmup and Halftime Testing

Before production use, explicitly test:

- server warmup;
- map start;
- halftime;
- automatic CS2 side swaps;
- overtime if enabled;
- map changes.

The current balancing source should not be assumed to understand every custom Warcraft server's match lifecycle automatically.

Watch for:

```text
balance occurring during warmup
```

or:

```text
balance immediately fighting against a halftime side swap
```

If either occurs, add a server-state guard around balance execution.

---

# 36. Recommended Deployment Process

Do not deploy directly to the live server first.

Use:

```text
1. Development/local server
2. Compile
3. Start with bots
4. Check plugin load
5. Check capability connection
6. Check race sync
7. Check balance_data.json
8. Test !balance
9. Test normal 4-round balancing
10. Test low-pop balancing
11. Test disconnect emergency
12. Test Warcraft entities after team switches
13. Test map change
14. Test hot reload
15. Deploy to production
```

---

# 37. Backup Before Updating

Before replacing a working build, back up:

```text
WarcraftAutoBalance.dll
balance_data.json
Warcraft configuration
Warcraft player database/data
```

The most important balancer file is:

```text
balance_data.json
```

That contains accumulated learning.

Do not discard it between plugin updates unless intentionally resetting player/race learning.

---

# 38. Updating WarcraftAutoBalance Later

Typical update process:

```text
1. Stop server or use controlled hot reload.
2. Back up balance_data.json.
3. Replace WarcraftAutoBalance DLL/output files.
4. Do NOT delete balance_data.json.
5. Start/reload plugin.
6. Check console for JSON migration/deserialization errors.
7. Run !balance.
```

If the persistent data model changes significantly in a future version, migration logic may be necessary.

---

# 39. Recommended Production Improvements After Initial Testing

The current v2.4 is a strong baseline, but these are the next improvements worth considering.

## A. Config file instead of constants

Move values such as:

```text
BalanceEveryRounds
MoveProtectionRounds
BalanceTriggerWinChance
TargetWinChance
LowPopulationHumanThreshold
LowPopulationPowerScale
DisconnectRebalanceDelaySeconds
```

into CounterStrikeSharp plugin configuration.

This lets admins tune them without recompiling.

## B. Warcraft team-change callback

Expose a callback/event so Warcraft can immediately clean up team-owned entities when the balancer moves someone.

## C. Explicit Warcraft damage reporting

Add shared API methods for custom ability/summon damage that CS2 does not naturally attribute.

## D. Pre-round team snapshots

Snapshot team strength at round start so disconnects and late joins cannot distort race expected-win learning.

## E. Warmup / halftime guards

Add explicit match-state suppression once the exact server lifecycle is known.

## F. Learned server normalization

Eventually replace hard-coded assumptions like:

```text
100 ADR ≈ 1000 rating
1.0 K/D ≈ 1000 rating
70% KAST ≈ 1000 rating
```

with medians learned from this Warcraft server.

---

# 40. Important Integration Rule

The main Warcraft mod remains the **source of truth** for:

```text
current race
current race level
maximum race level
Warcraft abilities
Warcraft-owned entities
```

WarcraftAutoBalance remains the **source of truth** for:

```text
player balance rating
recent performance window
persistent historical rating
learned race-strength modifier
team-balance decisions
low-pop partitions
disconnect emergency corrections
```

Do not duplicate Warcraft profile storage inside the balancer.

The balancer only needs enough race information to understand the player's current combat context.

---

# 41. Minimal Main-Mod Integration Checklist

At minimum, the Warcraft mod must do these things:

```text
[ ] Reference WarcraftBalance.Contracts
[ ] Declare PluginCapability<IWarcraftBalanceService>
[ ] Use capability ID "warcraft:autobalance"
[ ] Call SetPlayerRace after race selection
[ ] Call SetPlayerRace after race-level changes
[ ] Resync current race after profile load / plugin reload
[ ] Call ClearPlayerRace when the player truly has no race
[ ] Ensure Warcraft team-owned entities are cleaned up after team changes
```

---

# 42. Minimal Server Installation Checklist

```text
[ ] Metamod works
[ ] CounterStrikeSharp works
[ ] Main Warcraft mod works
[ ] WarcraftBalance.Contracts.dll installed under shared/
[ ] WarcraftAutoBalance compiled against the server's API version
[ ] WarcraftAutoBalance output installed under plugins/WarcraftAutoBalance/
[ ] Main Warcraft mod rebuilt with contract integration
[ ] Server restarted
[ ] WarcraftAutoBalance appears in console/plugin list
[ ] Capability connection succeeds
[ ] Race sync logs correctly
[ ] balance_data.json appears
[ ] !balance works for admins
[ ] 4-round normal test passes
[ ] Low-pop test passes
[ ] Emergency disconnect test passes
[ ] Warcraft entities clean up correctly after team changes
```

---

# 43. Troubleshooting

## WarcraftAutoBalance does not load

Check:

```text
CounterStrikeSharp API mismatch
missing dependency
incorrect folder name
incorrect DLL location
.NET/runtime issue
```

Read the complete server-console exception.

Do not troubleshoot only from the final error line.

## `BalanceCapability.Get()` returns null

Check:

```text
WarcraftAutoBalance loaded successfully
contract DLL exists in shared/
provider registered capability
consumer uses same capability string
both plugins use the same interface assembly
```

Both must use exactly:

```text
warcraft:autobalance
```

## `!balance` is unknown

Check:

```text
WarcraftAutoBalance actually loaded
CounterStrikeSharp chat trigger configuration
css_balance exists
admin permissions
```

Try from server console:

```text
css_balance
```

## `!balance` says permission denied

The command currently requires:

```text
@css/generic
```

Ensure the admin account has the appropriate CounterStrikeSharp permissions.

## Race always shows None / Neutral

The Warcraft mod is not syncing race assignment.

Verify:

```csharp
BalanceCapability.Get()?.SetPlayerRace(...)
```

actually runs after the player profile/race loads.

## Race modifier stays 1.000

This is normal initially.

The race learner requires a sample before moving away from neutral.

Current minimum:

```text
40 race-round samples
```

It also deliberately shrinks small samples toward 1.000.

## Players are being moved but Warcraft summons remain on old team

That is a Warcraft integration issue, not a rating issue.

Wire team-switch cleanup into the main mod.

## Plugin loses ratings after restart

Check:

```text
balance_data.json exists
plugin directory is writable
JSON is not corrupted
server user has filesystem permissions
```

## Custom spell damage is missing

Inspect whether `EventPlayerHurt.Attacker` identifies the owning player.

If not, add explicit custom-damage reporting through the shared API.

---

# 44. Final Recommended Architecture

```text
                      ┌─────────────────────┐
                      │    Main Warcraft    │
                      │        Mod          │
                      └─────────┬───────────┘
                                │
                                │ current race
                                │ race level
                                │ max level
                                ▼
                  ┌──────────────────────────┐
                  │ IWarcraftBalanceService  │
                  │   PluginCapability API   │
                  └────────────┬─────────────┘
                               │
                               ▼
                 ┌────────────────────────────┐
                 │   WarcraftAutoBalance      │
                 │                            │
                 │ Recent performance         │
                 │ Historical rating          │
                 │ Race learning              │
                 │ Low-pop power              │
                 │ Emergency balance          │
                 │ Team swap selection        │
                 └─────────────┬──────────────┘
                               │
                               │ SwitchTeam
                               ▼
                         ┌─────────────┐
                         │     CS2     │
                         │   T / CT    │
                         └─────────────┘
```

This keeps the two systems separated cleanly:

- Warcraft owns Warcraft.
- The balancer owns balancing.
- The shared contract is the small bridge between them.
