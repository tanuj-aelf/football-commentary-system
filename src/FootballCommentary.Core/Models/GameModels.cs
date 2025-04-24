using System;
using System.Collections.Generic;
using Orleans.Concurrency;

namespace FootballCommentary.Core.Models
{
    [GenerateSerializer]
    public enum GameStatus
    {
        [Id(0)] NotStarted,
        [Id(1)] InProgress,
        [Id(2)] Paused,
        [Id(3)] Ended,
        [Id(4)] GoalScored
    }

    [GenerateSerializer]
    public enum GameEventType
    {
        [Id(0)] GameStart,
        [Id(1)] GameEnd,
        [Id(2)] Goal,
        [Id(3)] Pass,
        [Id(4)] Shot,
        [Id(5)] Save,
        [Id(6)] Tackle,
        [Id(7)] OutOfBounds,
        [Id(8)] Foul,
        [Id(9)] StateUpdate,
        [Id(10)] PossessionLost
    }

    [GenerateSerializer]
    public enum CommentaryType
    {
        [Id(0)] Factual,
        [Id(1)] Excitement,
        [Id(2)] Analysis,
        [Id(3)] Background,
        [Id(4)] Summary
    }

    [Immutable]
    [GenerateSerializer]
    public class Team
    {
        [Id(0)] public string TeamId { get; set; } = string.Empty;
        [Id(1)] public string Name { get; set; } = string.Empty;
        [Id(2)] public int Score { get; set; }
        [Id(3)] public List<Player> Players { get; set; } = new List<Player>();
    }

    [Immutable]
    [GenerateSerializer]
    public class Player
    {
        [Id(0)] public string PlayerId { get; set; } = string.Empty;
        [Id(1)] public string Name { get; set; } = string.Empty;
        [Id(2)] public Position Position { get; set; } = new Position();
    }

    [Immutable]
    [GenerateSerializer]
    public class Position
    {
        [Id(0)] public double X { get; set; }
        [Id(1)] public double Y { get; set; }
    }

    [Immutable]
    [GenerateSerializer]
    public class Ball
    {
        [Id(0)] public Position Position { get; set; } = new Position();
        [Id(1)] public double VelocityX { get; set; }
        [Id(2)] public double VelocityY { get; set; }
    }

    [Immutable]
    [GenerateSerializer]
    public class GameState
    {
        [Id(0)] public string GameId { get; set; } = string.Empty;
        [Id(1)] public GameStatus Status { get; set; } = GameStatus.NotStarted;
        [Id(2)] public Team HomeTeam { get; set; } = new Team();
        [Id(3)] public Team AwayTeam { get; set; } = new Team();
        [Id(4)] public Ball Ball { get; set; } = new Ball();
        [Id(5)] public TimeSpan GameTime { get; set; } = TimeSpan.Zero;
        [Id(6)] public DateTime LastUpdateTime { get; set; } = DateTime.UtcNow;
        [Id(7)] public DateTime GameStartTime { get; set; } = DateTime.UtcNow;
        [Id(8)] public string BallPossession { get; set; } = string.Empty;
        [Id(9)] public int SimulationStep { get; set; } = 0;
        [Id(10)] public string? LastScoringTeamId { get; set; }
    }

    [Immutable]
    [GenerateSerializer]
    public class GameEvent
    {
        [Id(0)] public string GameId { get; set; } = string.Empty;
        [Id(1)] public GameEventType EventType { get; set; }
        [Id(2)] public string TeamId { get; set; } = string.Empty;
        [Id(3)] public int? PlayerId { get; set; }
        [Id(4)] public Position Position { get; set; } = new Position();
        [Id(5)] public Dictionary<string, string> AdditionalData { get; set; } = new Dictionary<string, string>();
        [Id(6)] public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    [Immutable]
    [GenerateSerializer]
    public class GameStateUpdate
    {
        [Id(0)] public string GameId { get; set; } = string.Empty;
        [Id(1)] public GameStatus Status { get; set; }
        [Id(2)] public TimeSpan GameTime { get; set; }
        [Id(3)] public Position BallPosition { get; set; } = new Position();
        [Id(4)] public Dictionary<string, Position> PlayerPositions { get; set; } = new Dictionary<string, Position>();
    }

    [Immutable]
    [GenerateSerializer]
    public class CommentaryMessage
    {
        [Id(0)] public string GameId { get; set; } = string.Empty;
        [Id(1)] public string Text { get; set; } = string.Empty;
        [Id(2)] public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        [Id(3)] public CommentaryType Type { get; set; } = CommentaryType.Factual;
    }
} 