using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;

namespace WarcraftAutoBalance;

public class WarcraftAutoBalancePlugin : BasePlugin
{
    public override string ModuleName => "Warcraft Auto Balance";
    public override string ModuleVersion => "2.7.0";
    public override string ModuleAuthor => "YourName";
    public override string ModuleDescription =>
        "Persistent, self-learning team balancing for Warcraft CS2.";

    // ============================================================
    // GENERAL BALANCE CONFIG
    // ============================================================

    private const int BalanceEveryRounds = 4;
    private const int RollingWindowRounds = 12;
    // Only rebalance when the stronger team is predicted
    // to have at least a 58% chance to win.
    private const double BalanceTriggerWinChance = 0.58;
    private const double TargetWinChance = 0.55;

    private const double DefaultHistoricalRating = 1000.0;
    private const double HistoricalLearningRate = 0.05;
    private const int MinimumRoundsForRecentStats = 3;

    // ============================================================
    // LOW POPULATION MODE
    // ============================================================

    // At or below this many REAL players, balance the human teams
    // by total human combat strength instead of average team rating.
    //
    // Bots are ignored for skill/race learning and are only used
    // afterward to fill the physical T/CT team sizes.
    private const int LowPopulationHumanThreshold = 6;

    // Low-pop uses a nonlinear combat-power curve instead of simply
    // adding raw ratings. This allows one exceptional player to be
    // legitimately balanced against several much weaker players.
    //
    // Lower values make elite-vs-many splits more likely.
    // Higher values make the curve more conservative.
    private const double LowPopulationPowerScale = 300.0;

    // ============================================================
    // EMERGENCY POPULATION REBALANCE
    // ============================================================

    // Coalesces multiple disconnect events that occur nearly together.
    // Example: 10v10 -> 10v9 -> 10v8 is handled once as 10v8.
    private const float DisconnectRebalanceDelaySeconds = 0.50f;

    // A physical team-count difference of 2 or more bypasses the
    // normal every-4-round balance cadence and skill threshold.
    private const int EmergencyTeamCountDifference = 2;

    private bool _disconnectRebalancePending;

    // ============================================================
    // PLAYER RATING WEIGHTS
    // ============================================================

    private const double AdrWeight = 0.35;
    private const double KdWeight = 0.25;
    private const double HistoricalWeight = 0.20;
    private const double KastWeight = 0.10;
    private const double ObjectiveWeight = 0.10;

    // ============================================================
    // AUTOMATIC RACE BALANCING CONFIG
    // ============================================================

    // Race modifiers do not move away from 1.00 until this sample.
    private const int RaceMinimumSampleRounds = 40;

    // Shrinks small samples toward neutral.
    private const double RacePriorRounds = 100.0;

    // Controls how strongly excess race performance affects modifier.
    private const double RaceAdjustmentSensitivity = 0.75;

    private const double MinimumRaceModifier = 0.95;
    private const double MaximumRaceModifier = 1.05;

    // Used only for pre-round race-learning expectation.
    // A one-human numerical advantage is modeled as roughly a
    // 75-rating-point advantage before race modifiers are considered.
    // This is intentionally modest because Warcraft races can add
    // extra lives, summons, and other non-player combat power that
    // should be learned through race results instead of raw headcount.
    private const double PlayerCountExpectationAdjustment = 75.0;

    // Small separate modifier for race level.
    private const double MinimumLevelModifier = 0.98;
    private const double MaximumLevelModifier = 1.02;

    // ============================================================
    // RUNTIME STATE
    // ============================================================

    private readonly Dictionary<ulong, PlayerBalanceData> _players = new();
    private readonly Dictionary<ulong, CurrentRoundData> _currentRound = new();
    private readonly Dictionary<ulong, RaceAssignment> _playerRaces = new();

    private readonly Dictionary<string, RacePerformanceData> _raceStats =
        new(StringComparer.OrdinalIgnoreCase);

    private int _roundNumber;

    // Captured at round start and held constant for the entire round.
    // Disconnects, respawns, temporary summon forms, and pawn resets
    // later in the round cannot retroactively change race expectation.
    private RoundExpectation _roundExpectation =
        new()
        {
            TerroristWinChance = 0.50,
            CounterTerroristWinChance = 0.50
        };

    // ============================================================
    // PERSISTENCE
    // ============================================================

    private string DataFilePath =>
        Path.Combine(ModuleDirectory, "balance_data.json");

    // ============================================================
    // LOAD / UNLOAD
    // ============================================================

    public override void Load(bool hotReload)
    {
        LoadPersistentData();

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

    public override void Unload(bool hotReload)
    {
        SavePersistentData();
    }

    // ============================================================
    // ADMIN DIAGNOSTIC COMMAND
    //
    // css_balance is available as !balance when ! is configured
    // as a CounterStrikeSharp public chat trigger.
    // ============================================================

    [ConsoleCommand("css_balance", "Shows Warcraft team balance diagnostics.")]
    [RequiresPermissions("@css/generic")]
    public void OnBalanceCommand(
        CCSPlayerController? caller,
        CommandInfo command)
    {
        PrintBalanceDiagnostics(caller, command);
    }

    // ============================================================
    // WARCRAFT INTEGRATION
    // ============================================================

    /// <summary>
    /// Call whenever a player selects/changes their persistent/base
    /// Warcraft race or that race's level.
    ///
    /// IMPORTANT:
    /// Do not call this merely because a race temporarily respawns the
    /// player as a summon/alternate form. The balancer should continue
    /// to attribute the round to the base race whose mechanic caused
    /// that transformation. If the player actually selects a different
    /// base race, call this normally.
    ///
    /// Example:
    /// SetPlayerRace(player, "Internet Troll", 5, 5);
    /// </summary>
    public void SetPlayerRace(
        CCSPlayerController player,
        string raceName,
        int currentLevel,
        int maximumLevel)
    {
        if (player == null ||
            !player.IsValid ||
            player.SteamID == 0 ||
            string.IsNullOrWhiteSpace(raceName))
        {
            return;
        }

        double levelFraction;

        if (maximumLevel <= 1)
        {
            levelFraction = 1.0;
        }
        else
        {
            levelFraction =
                (currentLevel - 1.0) /
                (maximumLevel - 1.0);

            levelFraction =
                Math.Clamp(levelFraction, 0.0, 1.0);
        }

        double levelModifier =
            MinimumLevelModifier +
            (MaximumLevelModifier - MinimumLevelModifier) *
            levelFraction;

        _playerRaces[player.SteamID] =
            new RaceAssignment
            {
                RaceName = raceName.Trim(),
                CurrentLevel = currentLevel,
                MaximumLevel = maximumLevel,
                LevelModifier = levelModifier
            };
    }

    public void ClearPlayerRace(CCSPlayerController player)
    {
        if (player == null)
            return;

        _playerRaces.Remove(player.SteamID);
    }

    // ============================================================
    // PLAYER VALIDATION
    // ============================================================

    private static bool IsPlayingController(
        CCSPlayerController? player)
    {
        return player != null
               && player.IsValid
               &&
               (
                   player.Team == CsTeam.Terrorist ||
                   player.Team == CsTeam.CounterTerrorist
               );
    }

    // Human player eligible for ratings/stat learning.
    private static bool IsUsablePlayer(
        CCSPlayerController? player)
    {
        return IsPlayingController(player)
               && !player!.IsBot
               && player.SteamID != 0;
    }

    private static List<CCSPlayerController> GetActivePlayers()
    {
        return Utilities
            .GetPlayers()
            .Where(IsUsablePlayer)
            .ToList();
    }

    private static List<CCSPlayerController> GetActiveBots()
    {
        return Utilities
            .GetPlayers()
            .Where(p =>
                IsPlayingController(p) &&
                p.IsBot)
            .ToList();
    }

    // ============================================================
    // DATA LOOKUPS
    // ============================================================

    private PlayerBalanceData GetPlayerData(
        CCSPlayerController player)
    {
        if (!_players.TryGetValue(
                player.SteamID,
                out PlayerBalanceData? data))
        {
            data = new PlayerBalanceData
            {
                SteamId = player.SteamID,
                Name = player.PlayerName,
                HistoricalRating = DefaultHistoricalRating
            };

            _players[player.SteamID] = data;
        }

        data.Name = player.PlayerName;

        return data;
    }

    private CurrentRoundData GetCurrentRoundData(
        CCSPlayerController player)
    {
        if (!_currentRound.TryGetValue(
                player.SteamID,
                out CurrentRoundData? data))
        {
            data = CreateCurrentRoundData(player);
            _currentRound[player.SteamID] = data;
        }

        return data;
    }

    private CurrentRoundData CreateCurrentRoundData(
        CCSPlayerController player)
    {
        string? raceName = null;
        double levelModifier = 1.0;

        if (_playerRaces.TryGetValue(
                player.SteamID,
                out RaceAssignment? race))
        {
            raceName = race.RaceName;
            levelModifier = race.LevelModifier;
        }

        return new CurrentRoundData
        {
            TeamAtRoundStart = player.Team,
            RaceName = raceName,
            RaceLevelModifier = levelModifier
        };
    }

    // ============================================================
    // PLAYER DISCONNECT / IMMEDIATE POPULATION CHECK
    // ============================================================

    private HookResult OnPlayerDisconnect(
        EventPlayerDisconnect @event,
        GameEventInfo info)
    {
        QueueEmergencyPopulationRebalance();
        return HookResult.Continue;
    }

    private void QueueEmergencyPopulationRebalance()
    {
        // If several players disconnect together, only queue one check.
        if (_disconnectRebalancePending)
            return;

        _disconnectRebalancePending = true;

        AddTimer(
            DisconnectRebalanceDelaySeconds,
            () =>
            {
                _disconnectRebalancePending = false;
                EvaluateEmergencyPopulationBalance();
            }
        );
    }

    // ============================================================
    // ROUND START
    // ============================================================

    private HookResult OnRoundStart(
        EventRoundStart @event,
        GameEventInfo info)
    {
        _currentRound.Clear();

        foreach (CCSPlayerController player in GetActivePlayers())
        {
            GetPlayerData(player);

            _currentRound[player.SteamID] =
                CreateCurrentRoundData(player);
        }

        // This really is a PRE-round expectation now. Capture it once
        // from human controllers and their round-start teams. Later
        // disconnects, respawns, summon transformations, or pawn resets
        // cannot alter the expectation used for race learning.
        _roundExpectation =
            CalculatePreRoundExpectation();

        return HookResult.Continue;
    }

    // ============================================================
    // DAMAGE
    // ============================================================

    private HookResult OnPlayerHurt(
        EventPlayerHurt @event,
        GameEventInfo info)
    {
        CCSPlayerController? attacker = @event.Attacker;
        CCSPlayerController? victim = @event.Userid;

        if (!IsUsablePlayer(attacker) ||
            !IsUsablePlayer(victim))
        {
            return HookResult.Continue;
        }

        if (attacker == victim)
            return HookResult.Continue;

        if (attacker!.Team == victim!.Team)
            return HookResult.Continue;

        CurrentRoundData round =
            GetCurrentRoundData(attacker);

        int damage =
            Math.Max(0, @event.DmgHealth);

        round.Damage += damage;

        return HookResult.Continue;
    }

    // ============================================================
    // KILLS / DEATHS / ASSISTS
    // ============================================================

    private HookResult OnPlayerDeath(
        EventPlayerDeath @event,
        GameEventInfo info)
    {
        CCSPlayerController? victim = @event.Userid;
        CCSPlayerController? attacker = @event.Attacker;
        CCSPlayerController? assister = @event.Assister;

        // Balance-rating combat statistics are human-vs-human only.
        if (IsUsablePlayer(victim))
        {
            CurrentRoundData victimRound =
                GetCurrentRoundData(victim!);

            victimRound.Deaths++;
            victimRound.Died = true;
        }

        if (IsUsablePlayer(attacker) &&
            IsUsablePlayer(victim) &&
            attacker != victim &&
            attacker!.Team != victim!.Team)
        {
            CurrentRoundData attackerRound =
                GetCurrentRoundData(attacker);

            attackerRound.Kills++;
        }

        if (IsUsablePlayer(assister) &&
            IsUsablePlayer(victim) &&
            assister != victim &&
            assister != attacker &&
            assister!.Team != victim!.Team)
        {
            CurrentRoundData assisterRound =
                GetCurrentRoundData(assister);

            assisterRound.Assists++;
        }

        return HookResult.Continue;
    }

    // ============================================================
    // OBJECTIVES
    // ============================================================

    private HookResult OnBombPlanted(
        EventBombPlanted @event,
        GameEventInfo info)
    {
        CCSPlayerController? player = @event.Userid;

        if (!IsUsablePlayer(player))
            return HookResult.Continue;

        GetCurrentRoundData(player!).ObjectivePoints += 1.0;

        return HookResult.Continue;
    }

    private HookResult OnBombDefused(
        EventBombDefused @event,
        GameEventInfo info)
    {
        CCSPlayerController? player = @event.Userid;

        if (!IsUsablePlayer(player))
            return HookResult.Continue;

        GetCurrentRoundData(player!).ObjectivePoints += 1.25;

        return HookResult.Continue;
    }

    // ============================================================
    // ROUND END
    // ============================================================

    private HookResult OnRoundEnd(
        EventRoundEnd @event,
        GameEventInfo info)
    {
        _roundNumber++;

        CsTeam winningTeam =
            (CsTeam)@event.Winner;

        FinalizeRoundStatistics(
            winningTeam,
            _roundExpectation
        );

        if (_roundNumber % BalanceEveryRounds == 0)
        {
            SavePersistentData();
            Server.NextFrame(EvaluateTeamBalance);
        }

        return HookResult.Continue;
    }

    // ============================================================
    // EXPECTED RESULT BEFORE RACE MODIFIERS
    // ============================================================

    private RoundExpectation CalculatePreRoundExpectation()
    {
        List<CCSPlayerController> players =
            GetActivePlayers();

        List<CCSPlayerController> terrorists =
            players
                .Where(p =>
                    _currentRound.TryGetValue(
                        p.SteamID,
                        out CurrentRoundData? r)
                    &&
                    r.TeamAtRoundStart ==
                    CsTeam.Terrorist)
                .ToList();

        List<CCSPlayerController> counterTerrorists =
            players
                .Where(p =>
                    _currentRound.TryGetValue(
                        p.SteamID,
                        out CurrentRoundData? r)
                    &&
                    r.TeamAtRoundStart ==
                    CsTeam.CounterTerrorist)
                .ToList();

        if (terrorists.Count == 0 ||
            counterTerrorists.Count == 0)
        {
            return new RoundExpectation
            {
                TerroristWinChance = 0.50,
                CounterTerroristWinChance = 0.50
            };
        }

        double tBase =
            terrorists.Average(
                CalculatePlayerRatingWithoutRace);

        double ctBase =
            counterTerrorists.Average(
                CalculatePlayerRatingWithoutRace);

        // Human numerical advantage matters, but should not be modeled
        // as another full 1000-rating player. Warcraft races can grant
        // respawns, summons, alternate forms, and other extra combat
        // resources. Those mechanics remain part of the race's learned
        // actual-vs-expected performance instead of being counted as
        // additional human controllers here.
        int humanCountDifference =
            counterTerrorists.Count -
            terrorists.Count;

        ctBase +=
            humanCountDifference *
            PlayerCountExpectationAdjustment;

        double ctChance =
            CalculateExpectedWinChance(
                ctBase,
                tBase
            );

        return new RoundExpectation
        {
            CounterTerroristWinChance = ctChance,
            TerroristWinChance = 1.0 - ctChance
        };
    }

    // ============================================================
    // FINALIZE ROUND
    // ============================================================

    private void FinalizeRoundStatistics(
        CsTeam winningTeam,
        RoundExpectation expectation)
    {
        foreach (CCSPlayerController player in GetActivePlayers())
        {
            PlayerBalanceData persistent =
                GetPlayerData(player);

            CurrentRoundData round =
                GetCurrentRoundData(player);

            // "Survived" means the player never died during this
            // round. A Warcraft race that dies and respawns (including
            // as a summon/alternate form) must not regain KAST survival
            // credit simply because its replacement pawn is alive at
            // round end.
            bool survived =
                !round.Died;

            bool contributed =
                round.Kills > 0 ||
                round.Assists > 0 ||
                survived ||
                round.Damage >= 40;

            bool teamWon =
                round.TeamAtRoundStart == winningTeam;

            RoundSnapshot snapshot = new()
            {
                Damage = round.Damage,
                Kills = round.Kills,
                Deaths = round.Deaths,
                Assists = round.Assists,
                Survived = survived,
                Contributed = contributed,
                ObjectivePoints = round.ObjectivePoints,
                TeamWon = teamWon
            };

            persistent.RecentRounds.Add(snapshot);

            while (persistent.RecentRounds.Count >
                   RollingWindowRounds)
            {
                persistent.RecentRounds.RemoveAt(0);
            }

            persistent.LifetimeRounds++;

            if (teamWon)
                persistent.LifetimeWins++;

            if (!string.IsNullOrWhiteSpace(round.RaceName))
            {
                RacePerformanceData race =
                    GetRacePerformance(round.RaceName!);

                double expectedWin =
                    round.TeamAtRoundStart ==
                    CsTeam.CounterTerrorist
                        ? expectation.CounterTerroristWinChance
                        : expectation.TerroristWinChance;

                race.RoundsPlayed++;
                race.ExpectedWins += expectedWin;

                if (teamWon)
                    race.ActualWins += 1.0;

                race.LastCalculatedModifier =
                    CalculateAutomatedRaceModifier(race);
            }

            UpdateHistoricalRating(persistent);
        }
    }

    // ============================================================
    // HISTORICAL PLAYER SKILL
    // ============================================================

    private void UpdateHistoricalRating(
        PlayerBalanceData player)
    {
        if (player.RecentRounds.Count <
            MinimumRoundsForRecentStats)
        {
            return;
        }

        double recent =
            CalculateRecentPerformanceWithoutHistory(player);

        player.HistoricalRating =
            player.HistoricalRating *
            (1.0 - HistoricalLearningRate)
            +
            recent *
            HistoricalLearningRate;

        player.HistoricalRating =
            Math.Clamp(
                player.HistoricalRating,
                600.0,
                1600.0
            );
    }

    // ============================================================
    // RACE LEARNING
    // ============================================================

    private RacePerformanceData GetRacePerformance(
        string raceName)
    {
        if (!_raceStats.TryGetValue(
                raceName,
                out RacePerformanceData? data))
        {
            data = new RacePerformanceData
            {
                RaceName = raceName,
                LastCalculatedModifier = 1.0
            };

            _raceStats[raceName] = data;
        }

        return data;
    }

    private double CalculateAutomatedRaceModifier(
        RacePerformanceData race)
    {
        if (race.RoundsPlayed <
            RaceMinimumSampleRounds)
        {
            return 1.0;
        }

        // Compare ACTUAL wins against EXPECTED wins based on
        // the skill of the players/teams using the race.
        double excessWins =
            race.ActualWins -
            race.ExpectedWins;

        double rawPerformanceEdge =
            excessWins /
            race.RoundsPlayed;

        // Bayesian-style shrinkage toward neutral for small samples.
        double confidence =
            race.RoundsPlayed /
            (race.RoundsPlayed + RacePriorRounds);

        double adjustedEdge =
            rawPerformanceEdge *
            confidence;

        double modifier =
            1.0 +
            (adjustedEdge * RaceAdjustmentSensitivity);

        return Math.Clamp(
            modifier,
            MinimumRaceModifier,
            MaximumRaceModifier
        );
    }

    private double GetRaceModifier(
        string? raceName)
    {
        if (string.IsNullOrWhiteSpace(raceName))
            return 1.0;

        if (!_raceStats.TryGetValue(
                raceName,
                out RacePerformanceData? race))
        {
            return 1.0;
        }

        return CalculateAutomatedRaceModifier(race);
    }

    // ============================================================
    // PLAYER RATING
    // ============================================================

    private RatingBreakdown GetRatingBreakdown(
        CCSPlayerController player)
    {
        PlayerBalanceData data =
            GetPlayerData(player);

        if (data.RecentRounds.Count == 0)
        {
            double raceMod = GetPlayerRaceModifier(player);
            double levelMod = GetPlayerLevelModifier(player);

            return new RatingBreakdown
            {
                AdrRating = 1000,
                KdRating = 1000,
                KastRating = 1000,
                ObjectiveRating = 1000,
                HistoricalRating = data.HistoricalRating,
                BaseRating = data.HistoricalRating,
                RaceModifier = raceMod,
                LevelModifier = levelMod,
                FinalRating = data.HistoricalRating * raceMod * levelMod
            };
        }

        List<RoundSnapshot> rounds =
            data.RecentRounds;

        double count = rounds.Count;

        double adr =
            rounds.Sum(x => x.Damage) /
            count;

        double kills =
            rounds.Sum(x => x.Kills);

        double deaths =
            rounds.Sum(x => x.Deaths);

        double kd =
            kills /
            Math.Max(1.0, deaths);

        double kast =
            rounds.Count(x => x.Contributed) /
            count;

        double objectivePerRound =
            rounds.Sum(x => x.ObjectivePoints) /
            count;

        double adrRating =
            1000.0 *
            Math.Clamp(
                adr / 100.0,
                0.40,
                1.60
            );

        double kdRating =
            1000.0 *
            Math.Clamp(
                kd,
                0.40,
                1.60
            );

        double kastRating =
            1000.0 *
            Math.Clamp(
                kast / 0.70,
                0.50,
                1.40
            );

        double objectiveRating =
            1000.0 *
            Math.Clamp(
                objectivePerRound / 0.15,
                0.50,
                1.50
            );

        double baseRating =
            adrRating * AdrWeight +
            kdRating * KdWeight +
            data.HistoricalRating * HistoricalWeight +
            kastRating * KastWeight +
            objectiveRating * ObjectiveWeight;

        double raceModifier =
            GetPlayerRaceModifier(player);

        double levelModifier =
            GetPlayerLevelModifier(player);

        return new RatingBreakdown
        {
            Adr = adr,
            Kd = kd,
            Kast = kast,

            AdrRating = adrRating,
            KdRating = kdRating,
            KastRating = kastRating,
            ObjectiveRating = objectiveRating,
            HistoricalRating = data.HistoricalRating,

            BaseRating = baseRating,

            RaceModifier = raceModifier,
            LevelModifier = levelModifier,

            FinalRating =
                baseRating *
                raceModifier *
                levelModifier
        };
    }

    private double CalculatePlayerRating(
        CCSPlayerController player)
    {
        return GetRatingBreakdown(player).FinalRating;
    }

    private double CalculatePlayerRatingWithoutRace(
        CCSPlayerController player)
    {
        return GetRatingBreakdown(player).BaseRating;
    }

    private double GetPlayerRaceModifier(
        CCSPlayerController player)
    {
        if (!_playerRaces.TryGetValue(
                player.SteamID,
                out RaceAssignment? assignment))
        {
            return 1.0;
        }

        return GetRaceModifier(assignment.RaceName);
    }

    private double GetPlayerLevelModifier(
        CCSPlayerController player)
    {
        if (!_playerRaces.TryGetValue(
                player.SteamID,
                out RaceAssignment? assignment))
        {
            return 1.0;
        }

        return assignment.LevelModifier;
    }

    private double CalculateRecentPerformanceWithoutHistory(
        PlayerBalanceData data)
    {
        List<RoundSnapshot> rounds =
            data.RecentRounds;

        if (rounds.Count == 0)
            return DefaultHistoricalRating;

        double count = rounds.Count;

        double adr =
            rounds.Sum(x => x.Damage) /
            count;

        double kd =
            rounds.Sum(x => x.Kills) /
            Math.Max(
                1.0,
                rounds.Sum(x => x.Deaths)
            );

        double kast =
            rounds.Count(x => x.Contributed) /
            count;

        double objective =
            rounds.Sum(x => x.ObjectivePoints) /
            count;

        double adrRating =
            1000 *
            Math.Clamp(
                adr / 100.0,
                0.40,
                1.60
            );

        double kdRating =
            1000 *
            Math.Clamp(
                kd,
                0.40,
                1.60
            );

        double kastRating =
            1000 *
            Math.Clamp(
                kast / 0.70,
                0.50,
                1.40
            );

        double objectiveRating =
            1000 *
            Math.Clamp(
                objective / 0.15,
                0.50,
                1.50
            );

        double weight =
            AdrWeight +
            KdWeight +
            KastWeight +
            ObjectiveWeight;

        return
            (
                adrRating * AdrWeight +
                kdRating * KdWeight +
                kastRating * KastWeight +
                objectiveRating * ObjectiveWeight
            ) / weight;
    }

    // ============================================================
    // TEAM CALCULATION
    // ============================================================

    private double CalculateTeamRating(
        IEnumerable<CCSPlayerController> players)
    {
        List<CCSPlayerController> list =
            players.ToList();

        if (list.Count == 0)
            return 0;

        return list.Average(CalculatePlayerRating);
    }

    private static double CalculateExpectedWinChance(
        double ratingA,
        double ratingB)
    {
        return 1.0 /
               (
                   1.0 +
                   Math.Pow(
                       10.0,
                       (ratingB - ratingA) /
                       400.0
                   )
               );
    }

    // ============================================================
    // EMERGENCY POPULATION BALANCE
    // ============================================================

    private void EvaluateEmergencyPopulationBalance()
    {
        List<CCSPlayerController> allPlaying =
            Utilities
                .GetPlayers()
                .Where(IsPlayingController)
                .ToList();

        if (allPlaying.Count < 2)
            return;

        List<CCSPlayerController> humans =
            allPlaying
                .Where(p =>
                    !p.IsBot &&
                    p.SteamID != 0)
                .ToList();

        // Low-pop has its own complete human-partition system.
        if (humans.Count >= 2 &&
            humans.Count <= LowPopulationHumanThreshold)
        {
            EvaluateLowPopulationBalance(humans);
            return;
        }

        // Keep logical local team state for this full pass. Do not rely
        // on SwitchTeam() updating controller.Team synchronously.
        List<CCSPlayerController> logicalT =
            allPlaying
                .Where(p => p.Team == CsTeam.Terrorist)
                .ToList();

        List<CCSPlayerController> logicalCT =
            allPlaying
                .Where(p => p.Team == CsTeam.CounterTerrorist)
                .ToList();

        if (Math.Abs(logicalT.Count - logicalCT.Count) <
            EmergencyTeamCountDifference)
        {
            return;
        }

        int totalMoves = 0;

        while (Math.Abs(logicalT.Count - logicalCT.Count) >=
               EmergencyTeamCountDifference)
        {
            bool tIsLarger = logicalT.Count > logicalCT.Count;

            List<CCSPlayerController> larger =
                tIsLarger ? logicalT : logicalCT;

            List<CCSPlayerController> smaller =
                tIsLarger ? logicalCT : logicalT;

            CsTeam destination =
                tIsLarger
                    ? CsTeam.CounterTerrorist
                    : CsTeam.Terrorist;

            CCSPlayerController? bot =
                larger.FirstOrDefault(p => p.IsBot);

            if (bot != null)
            {
                // Mutate logical state before issuing the engine switch,
                // so this bot cannot be selected twice in the same pass.
                larger.Remove(bot);
                smaller.Add(bot);
                bot.SwitchTeam(destination);
                totalMoves++;
                continue;
            }

            List<CCSPlayerController> largerHumans =
                larger.Where(IsUsablePlayer).ToList();

            List<CCSPlayerController> smallerHumans =
                smaller.Where(IsUsablePlayer).ToList();

            if (largerHumans.Count == 0)
                break;

            CsTeam largerSide =
                tIsLarger
                    ? CsTeam.Terrorist
                    : CsTeam.CounterTerrorist;

            EmergencyMoveCandidate? emergencyMove =
                FindBestEmergencyMove(
                    largerHumans,
                    smallerHumans,
                    largerSide);

            if (emergencyMove == null)
                break;

            CCSPlayerController bestMove =
                emergencyMove.Player;

            // Keep local state authoritative for subsequent moves.
            larger.Remove(bestMove);
            smaller.Add(bestMove);
            bestMove.SwitchTeam(destination);
            totalMoves++;

            Logger.LogInformation(
                "[WarcraftBalance] Emergency move {Player}: projected T {TRating:F0}, CT {CTRating:F0}, expected T {TChance:P1}, CT {CTChance:P1}.",
                bestMove.PlayerName,
                emergencyMove.ProjectedTRating,
                emergencyMove.ProjectedCTRating,
                1.0 - emergencyMove.ProjectedCTWinChance,
                emergencyMove.ProjectedCTWinChance);
        }

        if (totalMoves <= 0)
            return;

        Server.PrintToChatAll(
            " \x04[Balance]\x01 Teams immediately corrected after disconnects.");

        Logger.LogInformation(
            "[WarcraftBalance] Emergency disconnect balance moved {Moves} player(s). Final logical count T {TCount} vs CT {CTCount}.",
            totalMoves,
            logicalT.Count,
            logicalCT.Count);

        SavePersistentData();

        // Immediately run the normal rating/58-42 check after the
        // physical population problem is corrected.
        Server.NextFrame(EvaluateTeamBalance);
    }

    private EmergencyMoveCandidate? FindBestEmergencyMove(
        List<CCSPlayerController> largerTeam,
        List<CCSPlayerController> smallerTeam,
        CsTeam largerTeamSide)
    {
        if (largerTeam.Count == 0)
            return null;

        // Cache the COMPLETE final player rating once. This rating already
        // includes ADR, K/D, historical rating, KAST/impact, objectives,
        // learned race modifier and race-level modifier.
        Dictionary<ulong, double> ratings =
            largerTeam
                .Concat(smallerTeam)
                .Distinct()
                .ToDictionary(
                    p => p.SteamID,
                    CalculatePlayerRating);

        EmergencyMoveCandidate? best = null;

        foreach (CCSPlayerController candidate
                 in largerTeam)
        {
            List<CCSPlayerController> proposedLarge =
                largerTeam
                    .Where(p => p != candidate)
                    .ToList();

            List<CCSPlayerController> proposedSmall =
                smallerTeam
                    .Append(candidate)
                    .ToList();

            if (proposedLarge.Count == 0 ||
                proposedSmall.Count == 0)
            {
                continue;
            }

            double proposedLargeRating =
                proposedLarge.Average(
                    p => ratings[p.SteamID]);

            double proposedSmallRating =
                proposedSmall.Average(
                    p => ratings[p.SteamID]);

            double projectedTRating;
            double projectedCTRating;

            if (largerTeamSide ==
                CsTeam.Terrorist)
            {
                projectedTRating =
                    proposedLargeRating;
                projectedCTRating =
                    proposedSmallRating;
            }
            else
            {
                projectedTRating =
                    proposedSmallRating;
                projectedCTRating =
                    proposedLargeRating;
            }

            double projectedCTChance =
                CalculateExpectedWinChance(
                    projectedCTRating,
                    projectedTRating);

            double strongestChance =
                Math.Max(
                    projectedCTChance,
                    1.0 - projectedCTChance);

            double distanceFromEven =
                Math.Abs(
                    projectedCTChance - 0.50);

            bool reachesTarget =
                strongestChance <=
                TargetWinChance;

            EmergencyMoveCandidate current =
                new()
                {
                    Player = candidate,
                    ProjectedTRating =
                        projectedTRating,
                    ProjectedCTRating =
                        projectedCTRating,
                    ProjectedCTWinChance =
                        projectedCTChance,
                    StrongestProjectedWinChance =
                        strongestChance,
                    DistanceFromEven =
                        distanceFromEven,
                    ReachesTarget =
                        reachesTarget
                };

            if (best == null)
            {
                best = current;
                continue;
            }

            // Use the same target philosophy as normal balancing:
            // 1) Prefer a move that gets both teams inside 55/45.
            // 2) Within the same target class, choose the result closest to 50/50.
            if (current.ReachesTarget &&
                !best.ReachesTarget)
            {
                best = current;
                continue;
            }

            if (current.ReachesTarget ==
                best.ReachesTarget)
            {
                if (current.DistanceFromEven <
                    best.DistanceFromEven - 0.0001)
                {
                    best = current;
                    continue;
                }

            }
        }

        return best;
    }

    // ============================================================
    // BALANCE CHECK
    // ============================================================

    private void EvaluateTeamBalance()
    {
        List<CCSPlayerController> humans =
            GetActivePlayers();

        // Low population uses TOTAL human strength and allows
        // intentionally uneven human counts (1v2, 2v3, etc.).
        // Bots are redistributed only after the human split is chosen.
        if (humans.Count >= 2 &&
            humans.Count <= LowPopulationHumanThreshold)
        {
            EvaluateLowPopulationBalance(humans);
            return;
        }

        List<CCSPlayerController> terrorists =
            humans
                .Where(x =>
                    x.Team == CsTeam.Terrorist)
                .ToList();

        List<CCSPlayerController> counterTerrorists =
            humans
                .Where(x =>
                    x.Team == CsTeam.CounterTerrorist)
                .ToList();

        if (terrorists.Count == 0 ||
            counterTerrorists.Count == 0)
        {
            return;
        }

        if (Math.Abs(
                terrorists.Count -
                counterTerrorists.Count) > 1)
        {
            FixPlayerCountImbalance(
                terrorists,
                counterTerrorists
            );

            return;
        }

        double tRating =
            CalculateTeamRating(terrorists);

        double ctRating =
            CalculateTeamRating(counterTerrorists);

        double ctChance =
            CalculateExpectedWinChance(
                ctRating,
                tRating
            );

        double strongestChance =
            Math.Max(
                ctChance,
                1.0 - ctChance
            );

        Logger.LogInformation(
            "[WarcraftBalance] T {TRating:F0} vs CT {CTRating:F0}. CT expected {Chance:P1}",
            tRating,
            ctRating,
            ctChance
        );

        if (strongestChance <
            BalanceTriggerWinChance)
        {
            return;
        }

        SwapCandidate? best =
            FindBestSingleSwap(
                terrorists,
                counterTerrorists
            );

        if (best == null)
            return;

        double currentDifference =
            Math.Abs(ctChance - 0.50);

        double proposedDifference =
            Math.Abs(
                best.ExpectedCTWinChance -
                0.50
            );

        if (proposedDifference >=
            currentDifference)
        {
            return;
        }

        ExecuteSwap(best);
    }

    // ============================================================
    // LOW POPULATION BALANCING
    // ============================================================

    private void EvaluateLowPopulationBalance(
        List<CCSPlayerController> humans)
    {
        if (humans.Count < 2)
            return;

        LowPopulationPartition? best =
            FindBestLowPopulationPartition(humans);

        if (best == null)
            return;

        int humanMoves = 0;

        foreach (CCSPlayerController player in humans)
        {
            CsTeam desiredTeam =
                best.TerroristSteamIds.Contains(player.SteamID)
                    ? CsTeam.Terrorist
                    : CsTeam.CounterTerrorist;

            if (player.Team == desiredTeam)
                continue;

            player.SwitchTeam(desiredTeam);
            humanMoves++;
        }

        // Do not re-read human Team state in this frame. We already
        // know the exact logical human partition selected above.
        int terroristHumans =
            best.TerroristSteamIds.Count;

        int counterTerroristHumans =
            humans.Count - terroristHumans;

        int botMoves =
            RedistributeBotsForEvenTeams(
                terroristHumans,
                counterTerroristHumans);

        if (humanMoves > 0 || botMoves > 0)
        {
            Server.PrintToChatAll(
                $" \x04[Balance]\x01 Low-pop teams adjusted " +
                $"({humans.Count} humans, bots used as filler)."
            );

            Logger.LogInformation(
                "[WarcraftBalance] Low-pop split applied. " +
                "T human power {TPower:F2} vs CT human power {CTPower:F2}. " +
                "Human moves {HumanMoves}, bot moves {BotMoves}.",
                best.TerroristPower,
                best.CounterTerroristPower,
                humanMoves,
                botMoves
            );

            SavePersistentData();
        }
    }

    private LowPopulationPartition? FindBestLowPopulationPartition(
        List<CCSPlayerController> humans)
    {
        if (humans.Count < 2)
            return null;

        // Cache each player's final rating and convert it to nonlinear
        // effective combat power for this balance pass.
        //
        // Examples with a 300-point scale:
        // 1000 rating -> 1.00 power
        // 1300 rating -> 2.72 power
        // 1600 rating -> 7.39 power
        //
        // Race and level modifiers are already included in the final
        // rating before this transformation.
        Dictionary<ulong, double> ratings =
            humans.ToDictionary(
                p => p.SteamID,
                CalculatePlayerRating);

        Dictionary<ulong, double> power =
            ratings.ToDictionary(
                x => x.Key,
                x => CalculateLowPopulationPower(x.Value));

        LowPopulationPartition? best = null;

        int combinations =
            1 << humans.Count;

        // T/CT inverse partitions are equivalent in strength, so force
        // the first human into T to avoid evaluating duplicate splits.
        for (int mask = 1;
             mask < combinations - 1;
             mask++)
        {
            if ((mask & 1) == 0)
                continue;

            HashSet<ulong> tIds =
                new();

            double tPower = 0;
            double ctPower = 0;

            for (int i = 0;
                 i < humans.Count;
                 i++)
            {
                CCSPlayerController player =
                    humans[i];

                if ((mask & (1 << i)) != 0)
                {
                    tIds.Add(player.SteamID);
                    tPower +=
                        power[player.SteamID];
                }
                else
                {
                    ctPower +=
                        power[player.SteamID];
                }
            }

            if (tIds.Count == 0 ||
                tIds.Count == humans.Count)
            {
                continue;
            }

            double difference =
                Math.Abs(
                    tPower -
                    ctPower);

            int moves =
                CountMovesForPartition(
                    humans,
                    tIds);

            LowPopulationPartition candidate =
                new()
                {
                    TerroristSteamIds = tIds,
                    TerroristPower = tPower,
                    CounterTerroristPower = ctPower,
                    PowerDifference = difference,
                    HumanMoves = moves
                };

            if (best == null ||
                candidate.PowerDifference <
                best.PowerDifference - 0.0001 ||
                (
                    Math.Abs(
                        candidate.PowerDifference -
                        best.PowerDifference) < 0.0001
                    &&
                    candidate.HumanMoves <
                    best.HumanMoves
                ))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static double CalculateLowPopulationPower(
        double finalRating)
    {
        // Exponential curve makes differences at the high end matter
        // much more than raw additive rating.
        //
        // This is intentionally only used in low-pop partitioning.
        return Math.Exp(
            (finalRating - DefaultHistoricalRating) /
            LowPopulationPowerScale
        );
    }

    private static int CountMovesForPartition(
        List<CCSPlayerController> humans,
        HashSet<ulong> terroristSteamIds)
    {
        int moves = 0;

        foreach (CCSPlayerController player in humans)
        {
            CsTeam desiredTeam =
                terroristSteamIds.Contains(player.SteamID)
                    ? CsTeam.Terrorist
                    : CsTeam.CounterTerrorist;

            if (player.Team != desiredTeam)
                moves++;
        }

        return moves;
    }

    private int RedistributeBotsForEvenTeams(
        int terroristHumans,
        int counterTerroristHumans)
    {
        List<CCSPlayerController> bots =
            Utilities
                .GetPlayers()
                .Where(IsPlayingController)
                .Where(p => p.IsBot)
                .ToList();

        if (bots.Count == 0)
            return 0;

        List<CCSPlayerController> terroristBots =
            bots
                .Where(
                    b => b.Team == CsTeam.Terrorist)
                .ToList();

        List<CCSPlayerController> counterTerroristBots =
            bots
                .Where(
                    b => b.Team ==
                         CsTeam.CounterTerrorist)
                .ToList();

        int currentTBotCount =
            terroristBots.Count;

        int bestTBotCount =
            currentTBotCount;

        int bestPhysicalDifference =
            int.MaxValue;

        int bestBotMoveCount =
            int.MaxValue;

        // Evaluate every possible distribution of the EXISTING bots.
        // Human counts are logical values from the selected low-pop
        // partition, so human SwitchTeam() synchronization is irrelevant.
        for (int proposedTBotCount = 0;
             proposedTBotCount <= bots.Count;
             proposedTBotCount++)
        {
            int proposedCTBotCount =
                bots.Count -
                proposedTBotCount;

            int physicalT =
                terroristHumans +
                proposedTBotCount;

            int physicalCT =
                counterTerroristHumans +
                proposedCTBotCount;

            int physicalDifference =
                Math.Abs(
                    physicalT -
                    physicalCT);

            int botMoves =
                Math.Abs(
                    currentTBotCount -
                    proposedTBotCount);

            if (physicalDifference <
                    bestPhysicalDifference ||
                (physicalDifference ==
                     bestPhysicalDifference &&
                 botMoves <
                     bestBotMoveCount))
            {
                bestPhysicalDifference =
                    physicalDifference;

                bestBotMoveCount =
                    botMoves;

                bestTBotCount =
                    proposedTBotCount;
            }
        }

        int moved = 0;

        if (currentTBotCount >
            bestTBotCount)
        {
            int required =
                currentTBotCount -
                bestTBotCount;

            // Preselect unique controllers before any SwitchTeam()
            // calls so delayed engine-side Team updates cannot cause
            // a bot to be selected twice.
            foreach (CCSPlayerController bot
                     in terroristBots.Take(required))
            {
                bot.SwitchTeam(
                    CsTeam.CounterTerrorist);

                moved++;
            }
        }
        else if (currentTBotCount <
                 bestTBotCount)
        {
            int required =
                bestTBotCount -
                currentTBotCount;

            foreach (CCSPlayerController bot
                     in counterTerroristBots.Take(required))
            {
                bot.SwitchTeam(
                    CsTeam.Terrorist);

                moved++;
            }
        }

        return moved;
    }

    // ============================================================
    // FIND BEST SWAP
    // ============================================================

    private SwapCandidate? FindBestSingleSwap(
        List<CCSPlayerController> terrorists,
        List<CCSPlayerController> counterTerrorists)
    {
        SwapCandidate? best = null;

        foreach (CCSPlayerController t in terrorists)
        {
            foreach (CCSPlayerController ct in counterTerrorists)
            {
                List<CCSPlayerController> proposedT =
                    terrorists
                        .Where(x => x != t)
                        .Append(ct)
                        .ToList();

                List<CCSPlayerController> proposedCT =
                    counterTerrorists
                        .Where(x => x != ct)
                        .Append(t)
                        .ToList();

                double tRating = CalculateTeamRating(proposedT);
                double ctRating = CalculateTeamRating(proposedCT);

                double ctChance =
                    CalculateExpectedWinChance(ctRating, tRating);

                SwapCandidate candidate =
                    new()
                    {
                        Terrorist = t,
                        CounterTerrorist = ct,
                        ExpectedCTWinChance = ctChance,
                        Imbalance = Math.Abs(ctChance - 0.50)
                    };

                // Scan every possible pair and keep the result closest
                // to 50/50. The 55/45 target does not short-circuit.
                if (best == null ||
                    candidate.Imbalance < best.Imbalance)
                {
                    best = candidate;
                }
            }
        }

        return best;
    }

    // ============================================================
    // SWAP
    // ============================================================

    private void ExecuteSwap(
        SwapCandidate swap)
    {
        if (!IsUsablePlayer(swap.Terrorist) ||
            !IsUsablePlayer(swap.CounterTerrorist))
        {
            return;
        }

        string tName =
            swap.Terrorist.PlayerName;

        string ctName =
            swap.CounterTerrorist.PlayerName;

        swap.Terrorist.SwitchTeam(
            CsTeam.CounterTerrorist);

        swap.CounterTerrorist.SwitchTeam(
            CsTeam.Terrorist);

        Server.PrintToChatAll(
            $" \x04[Balance]\x01 " +
            $"\x0B{tName}\x01 ↔ " +
            $"\x0B{ctName}\x01"
        );

        SavePersistentData();
    }

    // ============================================================
    // PLAYER COUNT CORRECTION
    // ============================================================

    private void FixPlayerCountImbalance(
        List<CCSPlayerController> terrorists,
        List<CCSPlayerController> counterTerrorists)
    {
        List<CCSPlayerController> larger;
        List<CCSPlayerController> smaller;

        CsTeam target;

        if (terrorists.Count >
            counterTerrorists.Count)
        {
            larger = terrorists;
            smaller = counterTerrorists;
            target = CsTeam.CounterTerrorist;
        }
        else
        {
            larger = counterTerrorists;
            smaller = terrorists;
            target = CsTeam.Terrorist;
        }

        CCSPlayerController? best = null;
        double bestDifference =
            double.MaxValue;

        foreach (CCSPlayerController player in larger)
        {
            List<CCSPlayerController> proposedLarge =
                larger
                    .Where(x => x != player)
                    .ToList();

            List<CCSPlayerController> proposedSmall =
                smaller
                    .Append(player)
                    .ToList();

            double difference =
                Math.Abs(
                    CalculateTeamRating(proposedLarge) -
                    CalculateTeamRating(proposedSmall)
                );

            if (difference <
                bestDifference)
            {
                bestDifference = difference;
                best = player;
            }
        }

        if (best == null)
            return;

        best.SwitchTeam(target);

        Server.PrintToChatAll(
            $" \x04[Balance]\x01 " +
            $"\x0B{best.PlayerName}\x01 moved to correct team sizes."
        );

        SavePersistentData();
    }

    // ============================================================
    // ADMIN DIAGNOSTICS
    // ============================================================

    private void PrintBalanceDiagnostics(
        CCSPlayerController? caller,
        CommandInfo command)
    {
        List<CCSPlayerController> terrorists =
            GetActivePlayers()
                .Where(p =>
                    p.Team == CsTeam.Terrorist)
                .ToList();

        List<CCSPlayerController> cts =
            GetActivePlayers()
                .Where(p =>
                    p.Team == CsTeam.CounterTerrorist)
                .ToList();

        if (terrorists.Count == 0 ||
            cts.Count == 0)
        {
            command.ReplyToCommand(
                "[Balance] Both teams need players."
            );

            return;
        }

        double tRating =
            CalculateTeamRating(terrorists);

        double ctRating =
            CalculateTeamRating(cts);

        double ctChance =
            CalculateExpectedWinChance(
                ctRating,
                tRating
            );

        ReplyDiagnostic(command, "====================================");
        ReplyDiagnostic(command, $"BALANCE DIAGNOSTICS - Round {_roundNumber}");
        ReplyDiagnostic(command, $"T Rating: {tRating:F0}");
        ReplyDiagnostic(command, $"CT Rating: {ctRating:F0}");
        ReplyDiagnostic(command, $"Expected: T {1.0 - ctChance:P1} | CT {ctChance:P1}");
        ReplyDiagnostic(command, "");

        ReplyDiagnostic(command, "--- TERRORISTS ---");

        foreach (CCSPlayerController player
                 in terrorists.OrderByDescending(CalculatePlayerRating))
        {
            PrintPlayerDiagnostic(command, player);
        }

        ReplyDiagnostic(command, "");
        ReplyDiagnostic(command, "--- COUNTER-TERRORISTS ---");

        foreach (CCSPlayerController player
                 in cts.OrderByDescending(CalculatePlayerRating))
        {
            PrintPlayerDiagnostic(command, player);
        }

        if (terrorists.Count + cts.Count <=
            LowPopulationHumanThreshold)
        {
            List<CCSPlayerController> lowPopHumans =
                terrorists
                    .Concat(cts)
                    .ToList();

            LowPopulationPartition? lowPop =
                FindBestLowPopulationPartition(
                    lowPopHumans);

            ReplyDiagnostic(command, "");

            if (lowPop != null)
            {
                string tNames =
                    string.Join(
                        ", ",
                        lowPopHumans
                            .Where(p =>
                                lowPop.TerroristSteamIds
                                    .Contains(p.SteamID))
                            .Select(p => p.PlayerName));

                string ctNames =
                    string.Join(
                        ", ",
                        lowPopHumans
                            .Where(p =>
                                !lowPop.TerroristSteamIds
                                    .Contains(p.SteamID))
                            .Select(p => p.PlayerName));

                ReplyDiagnostic(
                    command,
                    $"LOW-POP recommended split: T [{tNames}] vs CT [{ctNames}]"
                );

                ReplyDiagnostic(
                    command,
                    $"Effective power: T {lowPop.TerroristPower:F2} | CT {lowPop.CounterTerroristPower:F2}"
                );
            }

            ReplyDiagnostic(command, "====================================");
            return;
        }

        SwapCandidate? candidate =
            FindBestSingleSwap(
                terrorists,
                cts
            );

        ReplyDiagnostic(command, "");

        if (candidate != null)
        {
            ReplyDiagnostic(
                command,
                $"Recommended swap: " +
                $"{candidate.Terrorist.PlayerName} <-> " +
                $"{candidate.CounterTerrorist.PlayerName}"
            );

            ReplyDiagnostic(
                command,
                $"After swap: " +
                $"T {1.0 - candidate.ExpectedCTWinChance:P1} | " +
                $"CT {candidate.ExpectedCTWinChance:P1}"
            );
        }
        else
        {
            ReplyDiagnostic(command, "Recommended swap: none");
        }

        ReplyDiagnostic(command, "");
        ReplyDiagnostic(command, "--- LEARNED RACE MODIFIERS ---");

        foreach (var item
                 in _raceStats.Values
                     .Select(r => new
                     {
                         Race = r,
                         Modifier =
                             CalculateAutomatedRaceModifier(r)
                     })
                     .OrderByDescending(
                         x => x.Modifier))
        {
            RacePerformanceData race =
                item.Race;

            double modifier =
                item.Modifier;

            double actualRate =
                race.RoundsPlayed > 0
                    ? race.ActualWins / race.RoundsPlayed
                    : 0.0;

            double expectedRate =
                race.RoundsPlayed > 0
                    ? race.ExpectedWins / race.RoundsPlayed
                    : 0.0;

            ReplyDiagnostic(
                command,
                $"{race.RaceName}: " +
                $"x{modifier:F3} | " +
                $"{race.RoundsPlayed} rounds | " +
                $"Actual {actualRate:P1} | " +
                $"Expected {expectedRate:P1}"
            );
        }

        ReplyDiagnostic(command, "====================================");
    }

    private void PrintPlayerDiagnostic(
        CommandInfo command,
        CCSPlayerController player)
    {
        RatingBreakdown b =
            GetRatingBreakdown(player);

        string raceName =
            _playerRaces.TryGetValue(
                player.SteamID,
                out RaceAssignment? race)
                ? race.RaceName
                : "Unknown";

        ReplyDiagnostic(
            command,
            $"{player.PlayerName}: " +
            $"{b.FinalRating:F0} | " +
            $"ADR {b.Adr:F0} | " +
            $"KD {b.Kd:F2} | " +
            $"KAST {b.Kast:P0} | " +
            $"Hist {b.HistoricalRating:F0} | " +
            $"{raceName} " +
            $"Race x{b.RaceModifier:F3} " +
            $"Lvl x{b.LevelModifier:F3}"
        );
    }

    private static void ReplyDiagnostic(
        CommandInfo command,
        string message)
    {
        command.ReplyToCommand(
            $"[Balance] {message}"
        );
    }

    // ============================================================
    // JSON PERSISTENCE
    // ============================================================

    private void LoadPersistentData()
    {
        try
        {
            Directory.CreateDirectory(
                ModuleDirectory
            );

            if (!File.Exists(DataFilePath))
                return;

            string json =
                File.ReadAllText(
                    DataFilePath
                );

            PersistentState? state =
                JsonSerializer.Deserialize<PersistentState>(
                    json
                );

            if (state == null)
                return;

            _roundNumber =
                state.RoundNumber;

            _players.Clear();

            foreach (PlayerBalanceData player
                     in state.Players)
            {
                _players[player.SteamId] =
                    player;
            }

            _raceStats.Clear();

            foreach (RacePerformanceData race
                     in state.Races)
            {
                _raceStats[race.RaceName] =
                    race;
            }

            Logger.LogInformation(
                "[WarcraftBalance] Loaded {Players} player ratings and {Races} race profiles.",
                _players.Count,
                _raceStats.Count
            );
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "[WarcraftBalance] Failed to load balance_data.json."
            );
        }
    }

    private void SavePersistentData()
    {
        try
        {
            Directory.CreateDirectory(
                ModuleDirectory
            );

            PersistentState state =
                new()
                {
                    RoundNumber =
                        _roundNumber,

                    Players =
                        _players.Values
                            .ToList(),

                    Races =
                        _raceStats.Values
                            .ToList()
                };

            JsonSerializerOptions options =
                new()
                {
                    WriteIndented = true
                };

            string json =
                JsonSerializer.Serialize(
                    state,
                    options
                );

            string temp =
                DataFilePath + ".tmp";

            File.WriteAllText(
                temp,
                json
            );

            File.Move(
                temp,
                DataFilePath,
                true
            );
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "[WarcraftBalance] Failed to save persistent data."
            );
        }
    }

    // ============================================================
    // DATA CLASSES
    // ============================================================

    public sealed class PersistentState
    {
        public int RoundNumber { get; set; }

        public List<PlayerBalanceData> Players {
            get;
            set;
        } = new();

        public List<RacePerformanceData> Races {
            get;
            set;
        } = new();
    }

    public sealed class PlayerBalanceData
    {
        public ulong SteamId { get; set; }

        public string Name { get; set; } =
            string.Empty;

        public double HistoricalRating { get; set; } =
            DefaultHistoricalRating;
        public int LifetimeRounds { get; set; }

        public int LifetimeWins { get; set; }

        public List<RoundSnapshot> RecentRounds {
            get;
            set;
        } = new();
    }

    public sealed class RacePerformanceData
    {
        public string RaceName { get; set; } =
            string.Empty;

        public int RoundsPlayed { get; set; }

        public double ActualWins { get; set; }

        public double ExpectedWins { get; set; }

        public double LastCalculatedModifier { get; set; } =
            1.0;
    }

    public sealed class RoundSnapshot
    {
        public int Damage { get; set; }

        public int Kills { get; set; }

        public int Deaths { get; set; }

        public int Assists { get; set; }

        public bool Survived { get; set; }

        public bool Contributed { get; set; }

        public bool TeamWon { get; set; }

        public double ObjectivePoints { get; set; }
    }

    private sealed class CurrentRoundData
    {
        public CsTeam TeamAtRoundStart { get; set; }

        public int Damage { get; set; }

        public int Kills { get; set; }

        public int Deaths { get; set; }

        public int Assists { get; set; }

        public bool Died { get; set; }

        public double ObjectivePoints { get; set; }

        public string? RaceName { get; set; }

        public double RaceLevelModifier { get; set; } =
            1.0;
    }

    private sealed class RaceAssignment
    {
        public string RaceName { get; set; } =
            string.Empty;

        public int CurrentLevel { get; set; }

        public int MaximumLevel { get; set; }

        public double LevelModifier { get; set; } =
            1.0;
    }

    private sealed class RatingBreakdown
    {
        public double Adr { get; set; }

        public double Kd { get; set; }

        public double Kast { get; set; }

        public double AdrRating { get; set; }

        public double KdRating { get; set; }

        public double KastRating { get; set; }

        public double ObjectiveRating { get; set; }

        public double HistoricalRating { get; set; }

        public double BaseRating { get; set; }

        public double RaceModifier { get; set; }

        public double LevelModifier { get; set; }

        public double FinalRating { get; set; }
    }

    private sealed class RoundExpectation
    {
        public double TerroristWinChance { get; set; }

        public double CounterTerroristWinChance {
            get;
            set;
        }
    }

    private sealed class EmergencyMoveCandidate
    {
        public required CCSPlayerController Player {
            get;
            init;
        }

        public double ProjectedTRating {
            get;
            init;
        }

        public double ProjectedCTRating {
            get;
            init;
        }

        public double ProjectedCTWinChance {
            get;
            init;
        }

        public double StrongestProjectedWinChance {
            get;
            init;
        }

        public double DistanceFromEven {
            get;
            init;
        }

        public bool ReachesTarget {
            get;
            init;
        }
    }

    private sealed class LowPopulationPartition
    {
        public HashSet<ulong> TerroristSteamIds {
            get;
            init;
        } = new();

        public double TerroristPower {
            get;
            init;
        }

        public double CounterTerroristPower {
            get;
            init;
        }

        public double PowerDifference {
            get;
            init;
        }

        public int HumanMoves {
            get;
            init;
        }
    }

    private sealed class SwapCandidate
    {
        public required CCSPlayerController Terrorist {
            get;
            init;
        }

        public required CCSPlayerController CounterTerrorist {
            get;
            init;
        }

        public double ExpectedCTWinChance {
            get;
            init;
        }

        public double Imbalance {
            get;
            init;
        }
    }
}
