-- WarcraftAutoBalance v2.17 schema reference
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA foreign_keys = ON;
PRAGMA busy_timeout = 5000;

CREATE TABLE Metadata (
    Key TEXT PRIMARY KEY,
    Value TEXT NOT NULL
);

CREATE TABLE Players (
    SteamId INTEGER PRIMARY KEY,
    Name TEXT NOT NULL,
    HistoricalRating REAL NOT NULL DEFAULT 1000,
    LifetimeRounds INTEGER NOT NULL DEFAULT 0,
    LifetimeWins INTEGER NOT NULL DEFAULT 0,
    CreatedUtc TEXT NOT NULL,
    LastSeenUtc TEXT NOT NULL
);

CREATE TABLE RecentRounds (
    SteamId INTEGER NOT NULL,
    RoundId INTEGER NOT NULL,
    PlayedUtc TEXT NOT NULL,
    Damage INTEGER NOT NULL,
    Kills INTEGER NOT NULL,
    Deaths INTEGER NOT NULL,
    Assists INTEGER NOT NULL,
    Survived INTEGER NOT NULL,
    Contributed INTEGER NOT NULL,
    TeamWon INTEGER NOT NULL,
    ObjectivePoints REAL NOT NULL,
    PRIMARY KEY (SteamId, RoundId),
    FOREIGN KEY (SteamId)
        REFERENCES Players(SteamId)
        ON DELETE CASCADE
);

CREATE INDEX IX_RecentRounds_SteamId_RoundId
ON RecentRounds (SteamId, RoundId DESC);

CREATE INDEX IX_Players_LastSeenUtc
ON Players (LastSeenUtc);

CREATE TABLE RaceStats (
    RaceName TEXT PRIMARY KEY COLLATE NOCASE,
    RoundsPlayed INTEGER NOT NULL DEFAULT 0,
    ActualWins REAL NOT NULL DEFAULT 0,
    ExpectedWins REAL NOT NULL DEFAULT 0,
    LastCalculatedModifier REAL NOT NULL DEFAULT 1.0
);

CREATE INDEX IX_RaceStats_RoundsPlayed
ON RaceStats (RoundsPlayed DESC);
