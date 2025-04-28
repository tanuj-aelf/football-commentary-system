using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FootballCommentary.Core.Abstractions;
using FootballCommentary.Core.Models;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.Timers;
using System.Text;
using System.Text.RegularExpressions;

namespace FootballCommentary.GAgents.GameState
{
    public enum TeamFormation
    {
        Formation_4_4_2,        // Classic 4-4-2
        Formation_4_3_3,        // Attacking 4-3-3
        Formation_4_2_3_1,      // Modern 4-2-3-1
        Formation_3_5_2,        // Wing-back 3-5-2
        Formation_5_3_2,        // Defensive 5-3-2
        Formation_4_1_4_1       // Balanced 4-1-4-1
    }
    
    [GenerateSerializer]
    public class TeamFormationData
    {
        [Id(0)] public TeamFormation Formation { get; set; } = TeamFormation.Formation_4_4_2;
        [Id(1)] public DateTime LastUpdateTime { get; set; } = DateTime.UtcNow;
        [Id(2)] public Dictionary<string, Position> BasePositions { get; set; } = new();
        [Id(3)] public Dictionary<string, (double dx, double dy)> CachedMovements { get; set; } = new();
    }

    [GenerateSerializer]
    public class GameStateLogEvent
    {
        [Id(0)] public string Message { get; set; } = string.Empty;
        [Id(1)] public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    [GenerateSerializer]
    public class GameStateGAgentState
    {
        [Id(0)] public Dictionary<string, FootballCommentary.Core.Models.GameState> Games { get; set; } = new();
        [Id(1)] public List<GameStateLogEvent> LogEvents { get; set; } = new();
        [Id(2)] public Dictionary<string, int> GoalCelebrationTicks { get; set; } = new(); // Ticks remaining for goal celebration
        [Id(3)] public Dictionary<string, TeamFormationData> TeamAFormations { get; set; } = new(); // Formations for Team A by gameId
        [Id(4)] public Dictionary<string, TeamFormationData> TeamBFormations { get; set; } = new(); // Formations for Team B by gameId
        [Id(5)] public Dictionary<string, DateTime> LastLLMUpdateTimes { get; set; } = new(); // Last time LLM was called for movement
    }

    public class GameStateGAgent : Grain, IGameStateAgent
    {
        private readonly ILogger<GameStateGAgent> _logger;
        private readonly IPersistentState<GameStateGAgentState> _state;
        private readonly Random _random = new Random();
        private readonly ILLMService _llmService;
        private readonly LLMTactician _llmTactician;
        private IStreamProvider? _streamProvider;
        private Dictionary<string, IDisposable> _gameSimulationTimers = new Dictionary<string, IDisposable>();
        
        // Game simulation constants
        private const double FIELD_WIDTH = 1.0;
        private const double FIELD_HEIGHT = 1.0;
        private const double GOAL_WIDTH = 0.2;
        private const double PLAYER_SPEED = 0.01;
        private const double BALL_SPEED = 0.02;
        private const double GOAL_POST_X_TEAM_A = 0.05;
        private const double GOAL_POST_X_TEAM_B = 0.95;
        private const double PASSING_DISTANCE = 0.3;
        private const double SHOOTING_DISTANCE = 0.2;
        private const double POSSESSION_CHANGE_PROBABILITY = 0.15;
        
        // Field zones for more realistic positioning
        private const double DEFENSE_ZONE = 0.3;
        private const double MIDFIELD_ZONE = 0.6;
        private const double ATTACK_ZONE = 0.7;
        private const double TEAMMATE_AVOIDANCE_DISTANCE = 0.08;
        private const double OPPONENT_AWARENESS_DISTANCE = 0.15;
        private const double POSITION_RECOVERY_WEIGHT = 0.02;
        private const double BALL_ATTRACTION_WEIGHT = 0.03;
        private const double PLAYER_ROLE_ADHERENCE = 0.04;
        private const double FORMATION_ADHERENCE = 0.03;
        
        public GameStateGAgent(
            ILogger<GameStateGAgent> logger,
            [PersistentState("gameState", "Default")] IPersistentState<GameStateGAgentState> state,
            ILLMService llmService)
        {
            _logger = logger;
            _state = state;
            _llmService = llmService;
            _llmTactician = new LLMTactician(
                logger.CreateLoggerForCategory<LLMTactician>(), 
                llmService);
        }
        
        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            await base.OnActivateAsync(cancellationToken);
            _streamProvider = this.GetStreamProvider("GameEvents");
            _logger.LogInformation("GameStateGAgent activated with key: {Key}", this.GetPrimaryKeyString());
        }
        
        public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            // Stop all game simulations
            foreach (var timer in _gameSimulationTimers.Values)
            {
                timer.Dispose();
            }
            
            await base.OnDeactivateAsync(reason, cancellationToken);
        }
        
        public Task<string> GetDescriptionAsync()
        {
            return Task.FromResult("Football game state agent that manages the state of football matches.");
        }
        
        public async Task<FootballCommentary.Core.Models.GameState> CreateGameAsync(string teamA, string teamB)
        {
            var gameId = Guid.NewGuid().ToString();
            _logger.LogInformation("Creating game {GameId} between {TeamA} and {TeamB}", gameId, teamA, teamB);
            
            var gameState = new FootballCommentary.Core.Models.GameState
            {
                GameId = gameId,
                Status = GameStatus.NotStarted,
                HomeTeam = new Team { TeamId = "TeamA", Name = teamA, Score = 0, Players = new List<Player>() },
                AwayTeam = new Team { TeamId = "TeamB", Name = teamB, Score = 0, Players = new List<Player>() },
                Ball = new Ball { Position = new Position { X = 0.5, Y = 0.5 } }
            };
            
            // Initialize players
            gameState.HomeTeam.Players = CreateTeamPlayers("TeamA", 11);
            gameState.AwayTeam.Players = CreateTeamPlayers("TeamB", 11);
            
            // Ensure player lists are never null
            if (gameState.HomeTeam.Players == null)
            {
                gameState.HomeTeam.Players = new List<Player>();
                _logger.LogWarning("Had to reinitialize HomeTeam Players list for game {GameId}", gameId);
            }
            
            if (gameState.AwayTeam.Players == null)
            {
                gameState.AwayTeam.Players = new List<Player>();
                _logger.LogWarning("Had to reinitialize AwayTeam Players list for game {GameId}", gameId);
            }
            
            // Log player counts
            _logger.LogInformation("Created game with {HomePlayerCount} home players and {AwayPlayerCount} away players", 
                gameState.HomeTeam.Players.Count, gameState.AwayTeam.Players.Count);
            
            // Assign ball to a random player on Team A
            var randomPlayerIndex = _random.Next(gameState.HomeTeam.Players.Count);
            gameState.BallPossession = gameState.HomeTeam.Players[randomPlayerIndex].PlayerId;
            
            // Initialize formations for both teams (default 4-4-2 initially, will be updated by AI)
            await InitializeTeamFormation(gameId, gameState, true);  // Team A
            await InitializeTeamFormation(gameId, gameState, false); // Team B
            
            _state.State.Games[gameId] = gameState;
            await _state.WriteStateAsync();
            
            // Publish game creation event
            await PublishGameEventAsync(new GameEvent
            {
                GameId = gameId,
                EventType = GameEventType.StateUpdate,
                AdditionalData = new Dictionary<string, string>
                {
                    ["TeamA"] = teamA,
                    ["TeamB"] = teamB
                }
            });
            
            return gameState;
        }
        
        private async Task InitializeTeamFormation(string gameId, FootballCommentary.Core.Models.GameState gameState, bool isTeamA)
        {
            try
            {
                // Get initial formation suggestion from LLM
                var formation = await _llmTactician.GetFormationSuggestionAsync(gameState, isTeamA);
                
                // Create formation data
                var formationData = new TeamFormationData
                {
                    Formation = formation,
                    LastUpdateTime = DateTime.UtcNow,
                    BasePositions = new Dictionary<string, Position>(),
                    CachedMovements = new Dictionary<string, (double dx, double dy)>()
                };
                
                // Calculate base positions for all players based on formation
                var players = isTeamA ? gameState.HomeTeam.Players : gameState.AwayTeam.Players;
                foreach (var player in players)
                {
                    // Get player number
                    if (int.TryParse(player.PlayerId.Split('_')[1], out int playerIndex))
                    {
                        int playerNumber = playerIndex + 1; // Convert to 1-based player number
                        formationData.BasePositions[player.PlayerId] = CalculatePositionForFormation(playerNumber, isTeamA, formation);
                    }
                }
                
                // Store formation data
                if (isTeamA)
                {
                    _state.State.TeamAFormations[gameId] = formationData;
                }
                else
                {
                    _state.State.TeamBFormations[gameId] = formationData;
                }
                
                _logger.LogInformation("Initialized {TeamId} with formation {Formation} for game {GameId}", 
                    isTeamA ? "Team A" : "Team B", formation, gameId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initializing team formation for {TeamId} in game {GameId}", 
                    isTeamA ? "Team A" : "Team B", gameId);
            }
        }
        
        private Position CalculatePositionForFormation(int playerNumber, bool isTeamA, TeamFormation formation)
        {
            // Player 1 is always the goalkeeper
            if (playerNumber == 1)
            {
                return new Position 
                { 
                    X = isTeamA ? 0.05 : 0.95, // Position goalkeepers closer to goal line
                    Y = 0.5 
                };
            }
            
            // Formation-based positioning
            double x, y;
            
            // Formation-based player position calculation
            switch (formation)
            {
                case TeamFormation.Formation_4_4_2:
                    (x, y) = Calculate_4_4_2_Position(playerNumber, isTeamA);
                    break;
                    
                case TeamFormation.Formation_4_3_3:
                    (x, y) = Calculate_4_3_3_Position(playerNumber, isTeamA);
                    break;
                    
                case TeamFormation.Formation_4_2_3_1:
                    (x, y) = Calculate_4_2_3_1_Position(playerNumber, isTeamA);
                    break;
                    
                case TeamFormation.Formation_3_5_2:
                    (x, y) = Calculate_3_5_2_Position(playerNumber, isTeamA);
                    break;
                    
                case TeamFormation.Formation_5_3_2:
                    (x, y) = Calculate_5_3_2_Position(playerNumber, isTeamA);
                    break;
                    
                case TeamFormation.Formation_4_1_4_1:
                    (x, y) = Calculate_4_1_4_1_Position(playerNumber, isTeamA);
                    break;
                    
                default:
                    // Fallback to 4-4-2
                    (x, y) = Calculate_4_4_2_Position(playerNumber, isTeamA);
                    break;
            }
            
            return new Position { X = x, Y = y };
        }
        
        private (double x, double y) Calculate_4_4_2_Position(int playerNumber, bool isTeamA)
        {
            double x, y;
            
            // Player positioning for 4-4-2 formation
            if (playerNumber == 1) // Goalkeeper
            {
                x = isTeamA ? 0.05 : 0.95;
                y = 0.5;
            }
            else if (playerNumber <= 5) // 4 Defenders
            {
                x = isTeamA ? 0.2 : 0.8;
                // Space defenders evenly across the width
                y = 0.2 + ((playerNumber - 2) * 0.2);
            }
            else if (playerNumber <= 9) // 4 Midfielders
            {
                x = isTeamA ? 0.4 : 0.6;
                // Space midfielders evenly
                y = 0.2 + ((playerNumber - 6) * 0.2);
            }
            else // 2 Forwards
            {
                x = isTeamA ? 0.7 : 0.3;
                // Two forwards positioned left and right
                y = (playerNumber == 10) ? 0.35 : 0.65;
            }
            
            return (x, y);
        }
        
        private (double x, double y) Calculate_4_3_3_Position(int playerNumber, bool isTeamA)
        {
            double x, y;
            
            if (playerNumber == 1) // Goalkeeper
            {
                x = isTeamA ? 0.05 : 0.95;
                y = 0.5;
            }
            else if (playerNumber <= 5) // 4 Defenders
            {
                x = isTeamA ? 0.2 : 0.8;
                y = 0.2 + ((playerNumber - 2) * 0.2);
            }
            else if (playerNumber <= 8) // 3 Midfielders
            {
                x = isTeamA ? 0.4 : 0.6;
                // 3 midfielders, centered
                y = 0.25 + ((playerNumber - 6) * 0.25);
            }
            else // 3 Forwards
            {
                x = isTeamA ? 0.75 : 0.25;
                // 3 forwards spread across
                y = 0.25 + ((playerNumber - 9) * 0.25);
            }
            
            return (x, y);
        }
        
        private (double x, double y) Calculate_4_2_3_1_Position(int playerNumber, bool isTeamA)
        {
            double x, y;
            
            if (playerNumber == 1) // Goalkeeper
            {
                x = isTeamA ? 0.05 : 0.95;
                y = 0.5;
            }
            else if (playerNumber <= 5) // 4 Defenders
            {
                x = isTeamA ? 0.2 : 0.8;
                y = 0.2 + ((playerNumber - 2) * 0.2);
            }
            else if (playerNumber <= 7) // 2 Defensive Midfielders
            {
                x = isTeamA ? 0.35 : 0.65;
                y = (playerNumber == 6) ? 0.35 : 0.65;
            }
            else if (playerNumber <= 10) // 3 Attacking Midfielders
            {
                x = isTeamA ? 0.55 : 0.45;
                y = 0.25 + ((playerNumber - 8) * 0.25);
            }
            else // 1 Striker
            {
                x = isTeamA ? 0.8 : 0.2;
                y = 0.5;
            }
            
            return (x, y);
        }
        
        private (double x, double y) Calculate_3_5_2_Position(int playerNumber, bool isTeamA)
        {
            double x, y;
            
            if (playerNumber == 1) // Goalkeeper
            {
                x = isTeamA ? 0.05 : 0.95;
                y = 0.5;
            }
            else if (playerNumber <= 4) // 3 Center Backs
            {
                x = isTeamA ? 0.15 : 0.85;
                y = 0.25 + ((playerNumber - 2) * 0.25);
            }
            else if (playerNumber <= 9) // 5 Midfielders (including wing backs)
            {
                if (playerNumber == 5 || playerNumber == 9) // Wing backs
                {
                    x = isTeamA ? 0.3 : 0.7;
                    y = (playerNumber == 5) ? 0.15 : 0.85;
                }
                else // Central midfielders
                {
                    x = isTeamA ? 0.45 : 0.55;
                    y = 0.3 + ((playerNumber - 6) * 0.2);
                }
            }
            else // 2 Forwards
            {
                x = isTeamA ? 0.75 : 0.25;
                y = (playerNumber == 10) ? 0.4 : 0.6;
            }
            
            return (x, y);
        }
        
        private (double x, double y) Calculate_5_3_2_Position(int playerNumber, bool isTeamA)
        {
            double x, y;
            
            if (playerNumber == 1) // Goalkeeper
            {
                x = isTeamA ? 0.05 : 0.95;
                y = 0.5;
            }
            else if (playerNumber <= 6) // 5 Defenders
            {
                if (playerNumber == 2 || playerNumber == 6) // Full backs
                {
                    x = isTeamA ? 0.2 : 0.8;
                    y = (playerNumber == 2) ? 0.15 : 0.85;
                }
                else // Center backs
                {
                    x = isTeamA ? 0.15 : 0.85;
                    y = 0.3 + ((playerNumber - 3) * 0.2);
                }
            }
            else if (playerNumber <= 9) // 3 Midfielders
            {
                x = isTeamA ? 0.4 : 0.6;
                y = 0.25 + ((playerNumber - 7) * 0.25);
            }
            else // 2 Forwards
            {
                x = isTeamA ? 0.7 : 0.3;
                y = (playerNumber == 10) ? 0.4 : 0.6;
            }
            
            return (x, y);
        }
        
        private (double x, double y) Calculate_4_1_4_1_Position(int playerNumber, bool isTeamA)
        {
            double x, y;
            
            if (playerNumber == 1) // Goalkeeper
            {
                x = isTeamA ? 0.05 : 0.95;
                y = 0.5;
            }
            else if (playerNumber <= 5) // 4 Defenders
            {
                x = isTeamA ? 0.2 : 0.8;
                y = 0.2 + ((playerNumber - 2) * 0.2);
            }
            else if (playerNumber == 6) // 1 Defensive Midfielder
            {
                x = isTeamA ? 0.35 : 0.65;
                y = 0.5;
            }
            else if (playerNumber <= 10) // 4 Midfielders
            {
                x = isTeamA ? 0.5 : 0.5;
                y = 0.2 + ((playerNumber - 7) * 0.2);
            }
            else // 1 Striker
            {
                x = isTeamA ? 0.8 : 0.2;
                y = 0.5;
            }
            
            return (x, y);
        }
        
        private Position GetBasePositionForRole(int playerNumber, bool isTeamA, string gameId)
        {
            // Try to get position from formation data first
            string playerId = $"{(isTeamA ? "TeamA" : "TeamB")}_{playerNumber - 1}";
            
            // Check if we have formation data for this game and team
            var formationData = isTeamA ? 
                _state.State.TeamAFormations.GetValueOrDefault(gameId) : 
                _state.State.TeamBFormations.GetValueOrDefault(gameId);
                
            if (formationData != null && formationData.BasePositions.TryGetValue(playerId, out var position))
            {
                return position;
            }
            
            // Fallback to default positioning if no formation data available
            double x, y;
            
            if (playerNumber == 1) // Goalkeeper
            {
                x = isTeamA ? 0.05 : 0.95; // Position goalkeepers closer to goal line
                y = 0.5;
            }
            else if (playerNumber <= 5) // Defenders
            {
                x = isTeamA ? 0.2 : 0.8;
                y = 0.2 + ((playerNumber - 1) * 0.15); // Spread across the defensive line
            }
            else if (playerNumber <= 8) // Midfielders
            {
                x = isTeamA ? 0.4 : 0.6;
                y = 0.25 + ((playerNumber - 5) * 0.2); // Spread across midfield
            }
            else // Forwards
            {
                x = isTeamA ? 0.7 : 0.3;
                y = 0.3 + ((playerNumber - 8) * 0.2); // Spread across forward line
            }
            
            return new Position { X = x, Y = y };
        }
        
        public async Task<FootballCommentary.Core.Models.GameState> StartGameAsync(string gameId)
        {
            if (!_state.State.Games.TryGetValue(gameId, out var game))
            {
                throw new KeyNotFoundException($"Game {gameId} not found");
            }
            
            game.Status = GameStatus.InProgress;
            game.LastUpdateTime = DateTime.UtcNow;
            game.GameStartTime = DateTime.UtcNow;
            
            await _state.WriteStateAsync();
            
            // Start game simulation
            StartGameSimulation(gameId);
            
            // Publish game start event
            await PublishGameEventAsync(new GameEvent
            {
                GameId = gameId,
                EventType = GameEventType.GameStart
            });
            
            return game;
        }
        
        public async Task<FootballCommentary.Core.Models.GameState> EndGameAsync(string gameId)
        {
            if (!_state.State.Games.TryGetValue(gameId, out var game))
            {
                throw new KeyNotFoundException($"Game {gameId} not found");
            }
            
            game.Status = GameStatus.Ended;
            game.LastUpdateTime = DateTime.UtcNow;
            
            // Stop game simulation
            if (_gameSimulationTimers.TryGetValue(gameId, out var timer))
            {
                timer.Dispose();
                _gameSimulationTimers.Remove(gameId);
            }
            
            await _state.WriteStateAsync();
            
            // Publish game end event
            await PublishGameEventAsync(new GameEvent
            {
                GameId = gameId,
                EventType = GameEventType.GameEnd
            });
            
            return game;
        }
        
        public Task<FootballCommentary.Core.Models.GameState> GetGameStateAsync(string gameId)
        {
            if (!_state.State.Games.TryGetValue(gameId, out var game))
            {
                throw new KeyNotFoundException($"Game {gameId} not found");
            }
            
            // Ensure player lists are never null when returning game state
            if (game.HomeTeam.Players == null)
            {
                game.HomeTeam.Players = new List<Player>();
                _logger.LogWarning("Had to reinitialize HomeTeam Players list for game {GameId} during GetGameStateAsync", gameId);
            }
            
            if (game.AwayTeam.Players == null)
            {
                game.AwayTeam.Players = new List<Player>();
                _logger.LogWarning("Had to reinitialize AwayTeam Players list for game {GameId} during GetGameStateAsync", gameId);
            }
            
            // Log player counts when getting game state
            _logger.LogDebug("GetGameStateAsync for game {GameId}, Home players: {HomeCount}, Away players: {AwayCount}", 
                gameId, game.HomeTeam.Players.Count, game.AwayTeam.Players.Count);
                
            return Task.FromResult(game);
        }
        
        public async Task KickBallAsync(string gameId)
        {
            if (!_state.State.Games.TryGetValue(gameId, out var game))
            {
                throw new KeyNotFoundException($"Game {gameId} not found");
            }
            
            // Apply random velocity to the ball
            game.Ball.VelocityX = (_random.NextDouble() - 0.5) * 0.1;
            game.Ball.VelocityY = (_random.NextDouble() - 0.5) * 0.1;
            
            // Update ball position
            game.Ball.Position.X = Math.Clamp(game.Ball.Position.X + game.Ball.VelocityX, 0, 1);
            game.Ball.Position.Y = Math.Clamp(game.Ball.Position.Y + game.Ball.VelocityY, 0, 1);
            
            game.LastUpdateTime = DateTime.UtcNow;
            
            await _state.WriteStateAsync();
            
            // Publish ball kick event
            await PublishGameEventAsync(new GameEvent
            {
                GameId = gameId,
                EventType = GameEventType.Pass,
                Position = new Position { X = game.Ball.Position.X, Y = game.Ball.Position.Y }
            });
            
            // Publish state update
            await PublishGameStateUpdateAsync(game);
        }
        
        public async Task SimulateGoalAsync(string gameId, string teamId, int playerId)
        {
            if (!_state.State.Games.TryGetValue(gameId, out var game))
            {
                throw new KeyNotFoundException($"Game {gameId} not found");
            }
            
            // Increment the score for the scoring team
            if (teamId == "TeamA")
            {
                game.HomeTeam.Score++;
                _logger.LogInformation("Goal scored by {TeamName} player {PlayerId} - {PlayerName}. New score: {HomeScore}-{AwayScore}", 
                    game.HomeTeam.Name, playerId, PlayerData.GetPlayerName(teamId, playerId),
                    game.HomeTeam.Score, game.AwayTeam.Score);
            }
            else if (teamId == "TeamB")
            {
                game.AwayTeam.Score++;
                _logger.LogInformation("Goal scored by {TeamName} player {PlayerId} - {PlayerName}. New score: {HomeScore}-{AwayScore}", 
                    game.AwayTeam.Name, playerId, PlayerData.GetPlayerName(teamId, playerId),
                    game.HomeTeam.Score, game.AwayTeam.Score);
            }
            
            // Reset ball position
            game.Ball.Position = new Position { X = 0.5, Y = 0.5 };
            game.Ball.VelocityX = 0;
            game.Ball.VelocityY = 0;
            
            game.LastUpdateTime = DateTime.UtcNow;
            
            await _state.WriteStateAsync();
            
            // Publish goal event
            await PublishGameEventAsync(new GameEvent
            {
                GameId = gameId,
                EventType = GameEventType.Goal,
                TeamId = teamId,
                PlayerId = playerId,
                Position = new Position { X = teamId == "TeamA" ? GOAL_POST_X_TEAM_B : GOAL_POST_X_TEAM_A, Y = 0.5 }
            });
            
            // Publish state update
            await PublishGameStateUpdateAsync(game);
        }
        
        public async Task UpdatePlayerPositionsAsync(string gameId, Dictionary<string, Position> newPositions)
        {
            if (!_state.State.Games.TryGetValue(gameId, out var game))
            {
                throw new KeyNotFoundException($"Game {gameId} not found");
            }
            
            // Update player positions
            foreach (var (playerId, position) in newPositions)
            {
                var player = game.HomeTeam.Players.FirstOrDefault(p => p.PlayerId == playerId);
                if (player != null)
                {
                    player.Position = position;
                    continue;
                }
                
                player = game.AwayTeam.Players.FirstOrDefault(p => p.PlayerId == playerId);
                if (player != null)
                {
                    player.Position = position;
                }
            }
            
            game.LastUpdateTime = DateTime.UtcNow;
            
            await _state.WriteStateAsync();
            
            // Publish state update
            await PublishGameStateUpdateAsync(game);
        }
        
        private List<Player> CreateTeamPlayers(string teamId, int count)
        {
            var players = new List<Player>();
            
            for (int i = 0; i < count; i++)
            {
                double x, y;
                
                if (i == 0) // Goalkeeper (player index 0)
                {
                    x = teamId == "TeamA" ? GOAL_POST_X_TEAM_A + 0.02 : GOAL_POST_X_TEAM_B - 0.02;
                    y = 0.5; // Center of goal
                }
                else if (i <= 4) // Defenders (player indices 1-4)
                {
                    x = teamId == "TeamA" ? 0.2 : 0.8;
                    y = 0.2 + (0.6 * (i - 1) / 3); // Spread evenly across the width
                }
                else if (i <= 8) // Midfielders (player indices 5-8)
                {
                    x = teamId == "TeamA" ? 0.4 : 0.6;
                    y = 0.2 + (0.6 * (i - 5) / 3); // Spread evenly across the width
                }
                else // Forwards (player indices 9-10)
                {
                    x = teamId == "TeamA" ? 0.65 : 0.35;
                    y = 0.35 + (0.3 * (i - 9)); // Two forwards slightly spread
                }
                
                players.Add(new Player
                {
                    PlayerId = $"{teamId}_{i}",
                    Name = $"Player {i + 1}",
                    Position = new Position { X = x, Y = y }
                });
            }
            
            return players;
        }
        
        private async Task PublishGameEventAsync(GameEvent gameEvent)
        {
            if (_streamProvider == null)
            {
                _logger.LogWarning("Stream provider is null, cannot publish game event");
                return;
            }
            
            var stream = _streamProvider.GetStream<GameEvent>(
                StreamId.Create("GameEvents", this.GetPrimaryKeyString()));
            
            await stream.OnNextAsync(gameEvent);
            _logger.LogInformation("Published game event: {EventType} for game {GameId}", 
                gameEvent.EventType, gameEvent.GameId);
        }
        
        private async Task PublishGameStateUpdateAsync(FootballCommentary.Core.Models.GameState gameState)
        {
            if (_streamProvider == null)
            {
                _logger.LogWarning("Stream provider is null, cannot publish game state update");
                return;
            }
            
            var stream = _streamProvider.GetStream<GameStateUpdate>(
                StreamId.Create("GameState", this.GetPrimaryKeyString()));
            
            var update = new GameStateUpdate
            {
                GameId = gameState.GameId,
                GameTime = gameState.GameTime,
                Status = gameState.Status,
                BallPosition = gameState.Ball.Position,
                PlayerPositions = new Dictionary<string, Position>()
            };
            
            // Add player positions
            foreach (var player in gameState.HomeTeam.Players.Concat(gameState.AwayTeam.Players))
            {
                update.PlayerPositions[player.PlayerId] = player.Position;
            }
            
            await stream.OnNextAsync(update);
        }
        
        private void StartGameSimulation(string gameId)
        {
            // Stop any existing simulation
            if (_gameSimulationTimers.TryGetValue(gameId, out var existingTimer))
            {
                existingTimer.Dispose();
            }
            
            // Create a new simulation timer that updates every 100ms
            var timerOptions = new GrainTimerCreationOptions
            {
                DueTime = TimeSpan.FromMilliseconds(100),
                Period = TimeSpan.FromMilliseconds(100),
                Interleave = true // Allow timer callbacks to interleave with grain calls
            };
            var timer = this.RegisterGrainTimer(
                async () => await SimulateGameStepAsync(gameId),
                timerOptions);
            
            _gameSimulationTimers[gameId] = timer;
            _logger.LogInformation("Started game simulation for game {GameId}", gameId);
        }
        
        private async Task SimulateGameStepAsync(string gameId)
        {
            try
            {
                if (!_state.State.Games.TryGetValue(gameId, out var game))
                {
                    _logger.LogWarning("Game {GameId} not found during simulation", gameId);
                    return;
                }
                
                // Publish state update every 5 steps (moved earlier)
                if (game.Status != GameStatus.NotStarted && game.SimulationStep % 2 == 0) // Publish every 2 steps (200ms) for smoother updates
                {
                    await PublishGameStateUpdateAsync(game);
                }
                
                // --- Handle Goal Scored State --- 
                if (game.Status == GameStatus.GoalScored)
                {
                    if (!_state.State.GoalCelebrationTicks.ContainsKey(gameId))
                    {
                        _state.State.GoalCelebrationTicks[gameId] = 20; // ~2 seconds delay (20 ticks * 100ms)
                    }

                    _state.State.GoalCelebrationTicks[gameId]--;

                    if (_state.State.GoalCelebrationTicks[gameId] <= 0)
                    {
                        _logger.LogInformation("Goal celebration ended for {GameId}. Resetting ball.", gameId);
                        // Reset ball to center
                        game.Ball.Position = new Position { X = 0.5, Y = 0.5 };
                        game.Ball.VelocityX = 0;
                        game.Ball.VelocityY = 0;
                        // Give possession to team that conceded (simple logic)
                        var concedingTeam = game.LastScoringTeamId == "TeamA" ? game.AwayTeam : game.HomeTeam;
                        if (concedingTeam != null && concedingTeam.Players != null && concedingTeam.Players.Any())
                        {
                             game.BallPossession = concedingTeam.Players[_random.Next(concedingTeam.Players.Count)].PlayerId;
                        }
                        else
                        {
                             _logger.LogWarning("Could not assign kickoff possession to team {TeamId}", concedingTeam?.TeamId);
                             game.BallPossession = string.Empty; 
                        }
                        
                        game.Status = GameStatus.InProgress; // Resume game
                        _state.State.GoalCelebrationTicks.Remove(gameId);
                        
                        // --- IMPORTANT: Save the state transition *immediately* ---
                        await _state.WriteStateAsync(); 
                        // --- ALSO IMPORTANT: Publish the reset state *immediately* ---
                        await PublishGameStateUpdateAsync(game); 
                        // ----------------------------------------------------------

                        // State is saved and published, now increment step and return
                        game.SimulationStep++; 
                        return; // Exit celebration logic
                    }
                    // else { /* Still celebrating, tick will be decremented below */ }

                    // --- Save state ONLY if still celebrating --- 
                    // We still need to increment simulation step even during celebration
                    game.SimulationStep++; 
                    await _state.WriteStateAsync(); // Save the decremented tick count
                    return; // Skip normal player/ball movement simulation during goal celebration
                }
                // ----------------------------- 

                if (game.Status != GameStatus.InProgress)
                {
                    return; // Not running or goal scored
                }
                
                // Update game time
                var realElapsed = DateTime.UtcNow - game.GameStartTime;
                // Scale real time: 1 real minute = 90 game minutes
                var scaledMinutes = realElapsed.TotalMinutes * 90;
                // Convert to TimeSpan (this will be what's displayed on screen)
                game.GameTime = TimeSpan.FromMinutes(scaledMinutes);
                
                // Randomly decide if we should publish an event
                bool publishEvent = false;
                GameEvent? gameEvent = null;
                
                // Move players - Now async
                await MoveAllPlayers(game);
                
                // Update ball based on possession
                UpdateBallPosition(game, out publishEvent, out gameEvent);
                
                // Save game state
                game.LastUpdateTime = DateTime.UtcNow;
                await _state.WriteStateAsync();
                
                // Publish game event if needed
                if (publishEvent && gameEvent != null)
                {
                    await PublishGameEventAsync(gameEvent);
                }
                
                // Increment simulation step
                game.SimulationStep++;
                
                // End game after 90 minutes (game time)
                if (game.GameTime.TotalMinutes >= 90 && game.Status == GameStatus.InProgress)
                {
                    await EndGameAsync(gameId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during game simulation for game {GameId}: {Message}", gameId, ex.Message);
            }
        }
        
        private async Task MoveAllPlayers(FootballCommentary.Core.Models.GameState game)
        {
            // Get all players from both teams
            var homeTeamPlayers = game.HomeTeam.Players;
            var awayTeamPlayers = game.AwayTeam.Players;
            var allPlayers = homeTeamPlayers.Concat(awayTeamPlayers).ToList();

            // Store the player with the ball
            Player? playerWithBall = null;
            if (!string.IsNullOrEmpty(game.BallPossession))
            {
                playerWithBall = allPlayers.FirstOrDefault(p => p.PlayerId == game.BallPossession);
            }

            // Check if it's time to update formations (every 30 seconds of game time)
            if (game.SimulationStep % 300 == 0) // Every ~30 seconds
            {
                await UpdateTeamFormations(game);
            }
            
            // Check if it's time to get new LLM suggestions (less frequent to reduce API calls)
            // Only update LLM suggestions every ~50 steps (5 seconds) instead of 10 steps
            bool useLLMSuggestions = game.SimulationStep % 50 == 0;
            
            // Check if it's been enough time since the last LLM call
            if (useLLMSuggestions)
            {
                DateTime now = DateTime.UtcNow;
                if (_state.State.LastLLMUpdateTimes.TryGetValue(game.GameId, out DateTime lastUpdate))
                {
                    // Only allow LLM calls if at least 1 second has passed
                    if ((now - lastUpdate).TotalSeconds < 1)
                    {
                        _logger.LogDebug("Skipping LLM call as only {Seconds} seconds have passed since last call", 
                            (now - lastUpdate).TotalSeconds);
                        useLLMSuggestions = false;
                    }
                }
                
                if (useLLMSuggestions)
                {
                    _state.State.LastLLMUpdateTimes[game.GameId] = now;
                }
            }
            
            // LLM movement suggestions for both teams
            Dictionary<string, (double dx, double dy)> teamAMovementSuggestions = new Dictionary<string, (double dx, double dy)>();
            Dictionary<string, (double dx, double dy)> teamBMovementSuggestions = new Dictionary<string, (double dx, double dy)>();
            
            // Get LLM movement suggestions if it's the right step
            if (useLLMSuggestions)
            {
                // Check if team A has possession
                bool isTeamAInPossession = !string.IsNullOrEmpty(game.BallPossession) && game.BallPossession.StartsWith("TeamA");
                bool isTeamBInPossession = !string.IsNullOrEmpty(game.BallPossession) && game.BallPossession.StartsWith("TeamB");
                
                // Get movement suggestions for both teams
                try
                {
                    // Run these in parallel to speed up processing
                    var teamATasks = _llmTactician.GetMovementSuggestionsAsync(
                        game, 
                        homeTeamPlayers,
                        true,
                        isTeamAInPossession);
                        
                    var teamBTasks = _llmTactician.GetMovementSuggestionsAsync(
                        game, 
                        awayTeamPlayers,
                        false,
                        isTeamBInPossession);
                    
                    // Wait for both to complete
                    await Task.WhenAll(teamATasks, teamBTasks);
                    
                    // Get results
                    teamAMovementSuggestions = await teamATasks;
                    teamBMovementSuggestions = await teamBTasks;
                        
                    _logger.LogInformation("LLM movement suggestions received for both teams. Step: {Step}", game.SimulationStep);
                    
                    // Cache these movement suggestions in the formation data
                    if (_state.State.TeamAFormations.TryGetValue(game.GameId, out var teamAFormation))
                    {
                        teamAFormation.CachedMovements = new Dictionary<string, (double dx, double dy)>(teamAMovementSuggestions);
                        teamAFormation.LastUpdateTime = DateTime.UtcNow;
                    }
                    
                    if (_state.State.TeamBFormations.TryGetValue(game.GameId, out var teamBFormation))
                    {
                        teamBFormation.CachedMovements = new Dictionary<string, (double dx, double dy)>(teamBMovementSuggestions);
                        teamBFormation.LastUpdateTime = DateTime.UtcNow;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error getting LLM movement suggestions: {Message}", ex.Message);
                }
            }
            else
            {
                // If not using LLM directly, try to use cached suggestions with variations
                if (_state.State.TeamAFormations.TryGetValue(game.GameId, out var teamAFormation) && 
                    teamAFormation.CachedMovements.Any())
                {
                    // Add variations to cached movements
                    foreach (var (playerId, movement) in teamAFormation.CachedMovements)
                    {
                        double dx = movement.dx + (_random.NextDouble() - 0.5) * 0.01;
                        double dy = movement.dy + (_random.NextDouble() - 0.5) * 0.01;
                        
                        // Clamp values
                        dx = Math.Clamp(dx, -0.1, 0.1);
                        dy = Math.Clamp(dy, -0.1, 0.1);
                        
                        teamAMovementSuggestions[playerId] = (dx, dy);
                    }
                }
                
                if (_state.State.TeamBFormations.TryGetValue(game.GameId, out var teamBFormation) && 
                    teamBFormation.CachedMovements.Any())
                {
                    // Add variations to cached movements
                    foreach (var (playerId, movement) in teamBFormation.CachedMovements)
                    {
                        double dx = movement.dx + (_random.NextDouble() - 0.5) * 0.01;
                        double dy = movement.dy + (_random.NextDouble() - 0.5) * 0.01;
                        
                        // Clamp values
                        dx = Math.Clamp(dx, -0.1, 0.1);
                        dy = Math.Clamp(dy, -0.1, 0.1);
                        
                        teamBMovementSuggestions[playerId] = (dx, dy);
                    }
                }
            }

            // Move each player based on their team's tactical objectives
            foreach (var player in allPlayers)
            {
                // Special case for goalkeepers (player #1 on each team)
                bool isGoalkeeper = player.PlayerId.EndsWith("_0"); // ID format: TeamX_0 for goalkeepers
                
                if (isGoalkeeper)
                {
                    MoveGoalkeeper(game, player, playerWithBall);
                    continue;
                }
                
                // Skip if this player has the ball
                if (player.PlayerId == game.BallPossession)
                    continue;
                
                bool isTeamA = player.PlayerId.StartsWith("TeamA");
                bool isTeamInPossession = !string.IsNullOrEmpty(game.BallPossession) && 
                    game.BallPossession.StartsWith(isTeamA ? "TeamA" : "TeamB");
                
                // Get player number for role assignment, handling potential parsing errors
                int playerNumber = 0; // Default to 0 if parsing fails
                try
                {
                    string playerIndexStr = player.PlayerId.Split('_')[1].Replace("Player", "");
                    playerNumber = int.Parse(playerIndexStr) + 1; // Add 1 to adjust for 1-based roles
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse player number from PlayerId: {PlayerId}", player.PlayerId);
                    // Optionally handle the error, e.g., skip this player or assign a default role
                    continue; // Skip moving this player if ID is invalid
                }
                
                // Calculate player's base position based on role
                Position basePosition = GetBasePositionForRole(playerNumber, isTeamA, game.GameId);
                
                // Check if we have LLM suggestions for this player
                var movementSuggestions = isTeamA ? teamAMovementSuggestions : teamBMovementSuggestions;
                if (movementSuggestions.TryGetValue(player.PlayerId, out var suggestion))
                {
                    // Apply LLM-suggested movement with some adjustments for game logic
                    double dx = suggestion.dx;
                    double dy = suggestion.dy;
                    
                    // Add some adherence to base position to prevent players from wandering too far
                    dx += (basePosition.X - player.Position.X) * POSITION_RECOVERY_WEIGHT * 0.5;
                    dy += (basePosition.Y - player.Position.Y) * POSITION_RECOVERY_WEIGHT * 0.5;
                    
                    // Add small random movement for naturalism
                    dx += (_random.NextDouble() - 0.5) * PLAYER_SPEED * 0.1;
                    dy += (_random.NextDouble() - 0.5) * PLAYER_SPEED * 0.1;
                    
                    // Avoid teammates crowding
                    AvoidTeammates(game, player, ref dx, ref dy, isTeamA);
                    
                    // Apply movement with boundary checking
                    player.Position.X = Math.Clamp(player.Position.X + dx, 0, 1);
                    player.Position.Y = Math.Clamp(player.Position.Y + dy, 0, 1);
                    
                    _logger.LogDebug("Applied AI movement for player {PlayerId}: dx={dx}, dy={dy}", 
                        player.PlayerId, dx, dy);
                }
                else
                {
                    // Fall back to rule-based movement logic
                    // Apply different movement logic based on whether team is in possession
                    if (isTeamInPossession)
                    {
                        MovePlayerWhenTeamHasPossession(game, player, playerWithBall, basePosition, isTeamA);
                    }
                    else
                    {
                        MovePlayerWhenOpponentHasPossession(game, player, playerWithBall, basePosition, isTeamA);
                    }
                }
            }
        }
        
        private void MoveGoalkeeper(FootballCommentary.Core.Models.GameState game, Player goalkeeper, Player? playerWithBall)
        {
            // Determine which team the goalkeeper belongs to
            bool isTeamA = goalkeeper.PlayerId.StartsWith("TeamA");
            double goalPostX = isTeamA ? GOAL_POST_X_TEAM_A : GOAL_POST_X_TEAM_B;
            
            // Maximum distance goalkeeper should stray from goal line
            double maxDistance = 0.08;
            
            // Optimal position based on ball position
            double optimalX;
            double optimalY;
            
            // Ball position or default to center if no ball data
            Position ballPos = game.Ball?.Position ?? new Position { X = 0.5, Y = 0.5 };
            
            // Goalkeeper stays close to goal line but can move forward slightly when ball is far away
            if ((isTeamA && ballPos.X < 0.3) || (!isTeamA && ballPos.X > 0.7))
            {
                // Ball is in own half - goalkeeper can come forward a bit
                optimalX = isTeamA ? Math.Min(goalPostX + 0.1, 0.15) : Math.Max(goalPostX - 0.1, 0.85);
            }
            else
            {
                // Ball is away - stay closer to goal line
                optimalX = goalPostX + (isTeamA ? 0.02 : -0.02);
            }
            
            // Goalkeeper tracks ball's Y position but within smaller range
            double centerY = 0.5;
            double trackingWeight = 0.6; // How much to follow the ball's Y position
            optimalY = centerY + (ballPos.Y - centerY) * trackingWeight;
            
            // Clamp to prevent goalkeeper from leaving goal area completely
            optimalY = Math.Clamp(optimalY, 0.3, 0.7);
            
            // Calculate movement speeds
            double dx = (optimalX - goalkeeper.Position.X) * 0.1;
            double dy = (optimalY - goalkeeper.Position.Y) * 0.1;
            
            // Add small random movement for more natural behavior
            dx += (_random.NextDouble() - 0.5) * 0.005;
            dy += (_random.NextDouble() - 0.5) * 0.005;
            
            // Apply movement with boundary checking
            goalkeeper.Position.X = Math.Clamp(goalkeeper.Position.X + dx, 
                isTeamA ? goalPostX : goalPostX - maxDistance, 
                isTeamA ? goalPostX + maxDistance : goalPostX);
            goalkeeper.Position.Y = Math.Clamp(goalkeeper.Position.Y + dy, 0.3, 0.7);
            
            // Goalkeeper diving logic - if ball is very close to goal and moving toward it
            bool ballMovingTowardGoal = false;
            
            // Check if ball has velocity before accessing it
            if (game.Ball != null) 
            {
                ballMovingTowardGoal = (isTeamA && game.Ball.VelocityX < 0) || (!isTeamA && game.Ball.VelocityX > 0);
            }
            
            double distanceToBall = Math.Sqrt(
                Math.Pow(goalkeeper.Position.X - ballPos.X, 2) + 
                Math.Pow(goalkeeper.Position.Y - ballPos.Y, 2));
            
            if (ballMovingTowardGoal && distanceToBall < 0.1 && _random.NextDouble() < 0.4)
            {
                // Dive toward the ball
                double diveDx = (ballPos.X - goalkeeper.Position.X) * 0.5;
                double diveDy = (ballPos.Y - goalkeeper.Position.Y) * 0.5;
                
                goalkeeper.Position.X = Math.Clamp(goalkeeper.Position.X + diveDx, 
                    isTeamA ? goalPostX - 0.03 : goalPostX - maxDistance, 
                    isTeamA ? goalPostX + maxDistance : goalPostX + 0.03);
                goalkeeper.Position.Y = Math.Clamp(goalkeeper.Position.Y + diveDy, 0.25, 0.75);
                
                // Check if goalkeeper intercepts the ball (closer than 0.03 units)
                if (distanceToBall < 0.04 && game.Ball != null)
                {
                    // Goalkeeper saves the ball
                    _logger.LogInformation("SAVE! Goalkeeper {PlayerId} makes a save!", goalkeeper.PlayerId);
                    game.BallPossession = goalkeeper.PlayerId; // Goalkeeper gets the ball
                    game.Ball.VelocityX = 0;
                    game.Ball.VelocityY = 0;
                }
            }
        }
        
        private async Task UpdateTeamFormations(FootballCommentary.Core.Models.GameState game)
        {
            try
            {
                _logger.LogInformation("Updating team formations for game {GameId}", game.GameId);
                
                // Update Team A formation
                TeamFormation teamAFormation = await _llmTactician.GetFormationSuggestionAsync(game, true);
                if (!_state.State.TeamAFormations.TryGetValue(game.GameId, out var teamAData))
                {
                    teamAData = new TeamFormationData();
                    _state.State.TeamAFormations[game.GameId] = teamAData;
                }
                
                bool teamAFormationChanged = teamAData.Formation != teamAFormation;
                teamAData.Formation = teamAFormation;
                teamAData.LastUpdateTime = DateTime.UtcNow;
                
                // Update Team B formation
                TeamFormation teamBFormation = await _llmTactician.GetFormationSuggestionAsync(game, false);
                if (!_state.State.TeamBFormations.TryGetValue(game.GameId, out var teamBData))
                {
                    teamBData = new TeamFormationData();
                    _state.State.TeamBFormations[game.GameId] = teamBData;
                }
                
                bool teamBFormationChanged = teamBData.Formation != teamBFormation;
                teamBData.Formation = teamBFormation;
                teamBData.LastUpdateTime = DateTime.UtcNow;
                
                // If formations changed, update base positions
                if (teamAFormationChanged)
                {
                    _logger.LogInformation("Team A formation changed to {Formation}", teamAFormation);
                    UpdateTeamBasePositions(game, true, teamAData);
                }
                
                if (teamBFormationChanged)
                {
                    _logger.LogInformation("Team B formation changed to {Formation}", teamBFormation);
                    UpdateTeamBasePositions(game, false, teamBData);
                }
                
                await _state.WriteStateAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating team formations: {Message}", ex.Message);
            }
        }
        
        private void UpdateTeamBasePositions(FootballCommentary.Core.Models.GameState game, bool isTeamA, TeamFormationData formationData)
        {
            var players = isTeamA ? game.HomeTeam.Players : game.AwayTeam.Players;
            formationData.BasePositions.Clear();
            
            foreach (var player in players)
            {
                if (int.TryParse(player.PlayerId.Split('_')[1], out int playerIndex))
                {
                    int playerNumber = playerIndex + 1; // Convert to 1-based player number
                    formationData.BasePositions[player.PlayerId] = CalculatePositionForFormation(playerNumber, isTeamA, formationData.Formation);
                }
            }
        }
        
        private void MovePlayerWhenTeamHasPossession(
            FootballCommentary.Core.Models.GameState game, 
            Player player, 
            Player? playerWithBall, 
            Position basePosition,
            bool isTeamA)
        {
            // Players move more dynamically when their team has possession
            
            // Determine player role
            int playerNumber = TryParsePlayerId(player.PlayerId) ?? 0;
            bool isForward = playerNumber >= 9;

            // 1. Calculate movement toward base position (Increased adherence, especially for forwards)
            double adherenceWeight = FORMATION_ADHERENCE * (isForward ? 1.5 : 1.0); // Forwards stick to position more
            double dx = (basePosition.X - player.Position.X) * adherenceWeight;
            double dy = (basePosition.Y - player.Position.Y) * adherenceWeight;

            // 2. Add forward movement bias for attacking team (Increased for forwards)
            double forwardBias = PLAYER_SPEED * (isTeamA ? 1.0 : -1.0) * (isForward ? 0.8 : 0.4); // Stronger push for forwards
            dx += forwardBias;

            // 3. Movement toward creating space / supporting player with ball
            if (playerWithBall != null)
            {
                double distToBall = Math.Sqrt(Math.Pow(player.Position.X - playerWithBall.Position.X, 2) + Math.Pow(player.Position.Y - playerWithBall.Position.Y, 2));

                // If NOT a forward, try to offer a passing option if reasonably close
                if (!isForward && distToBall > 0.1 && distToBall < PASSING_DISTANCE * 1.2)
                {
                    // Move slightly towards the ball carrier to support
                    dx += (playerWithBall.Position.X - player.Position.X) * PLAYER_SPEED * 0.2;
                    dy += (playerWithBall.Position.Y - player.Position.Y) * PLAYER_SPEED * 0.2;
                }
                 // If a forward, try to move into attacking space ahead of the ball
                else if (isForward)
                {
                    double targetX = isTeamA ? Math.Max(player.Position.X, playerWithBall.Position.X + 0.1) : Math.Min(player.Position.X, playerWithBall.Position.X - 0.1);
                    targetX = isTeamA ? Math.Min(targetX, 0.9) : Math.Max(targetX, 0.1); // Stay within bounds

                    dx += (targetX - player.Position.X) * PLAYER_SPEED * 0.6; // Move towards attacking space
                    // Add slight lateral movement to find space
                    dy += (_random.NextDouble() - 0.5) * PLAYER_SPEED * 0.4;
                }
            }

            // 4. Avoid clustering with teammates
            AvoidTeammates(game, player, ref dx, ref dy, isTeamA);

            // 5. Add small random movement for naturalism
            dx += (_random.NextDouble() - 0.5) * PLAYER_SPEED * 0.3;
            dy += (_random.NextDouble() - 0.5) * PLAYER_SPEED * 0.3;

            // Apply movement with boundary checking
            player.Position.X = Math.Clamp(player.Position.X + dx, 0, 1);
            player.Position.Y = Math.Clamp(player.Position.Y + dy, 0, 1);
        }
        
        private void MovePlayerWhenOpponentHasPossession(
            FootballCommentary.Core.Models.GameState game,
            Player player,
            Player? playerWithBall,
            Position basePosition,
            bool isTeamA)
        {
            // Defensive movement when the opponent has possession

            double dx = 0;
            double dy = 0;

            Position targetPos = playerWithBall?.Position ?? game.Ball.Position; // Target the player or the ball itself
            int playerNumber = TryParsePlayerId(player.PlayerId) ?? 0;
            bool isDefenderOrMidfielder = playerNumber >= 2 && playerNumber <= 8;

            if (isDefenderOrMidfielder)
            {
                // Check if defender should press based on zone
                bool shouldPressHigh = true;
                if (isTeamA)
                {
                    // Defenders/Midfielders are less likely to press high up the pitch
                    double opponentHalfLine = 0.6;
                    if (targetPos.X > opponentHalfLine)
                    {
                        if (_random.NextDouble() > 0.3) // 70% chance to not press high
                        {
                            shouldPressHigh = false;
                        }
                    }
                }
                else
                {
                    // Defenders/Midfielders are less likely to press high up the pitch
                    double opponentHalfLine = 0.4;
                    if (targetPos.X < opponentHalfLine)
                    {
                        if (_random.NextDouble() > 0.3) // 70% chance to not press high
                        {
                            shouldPressHigh = false;
                        }
                    }
                }

                if (shouldPressHigh)
                {
                    // 1. Strong attraction to the ball carrier
                    const double DEFENSIVE_BALL_ATTRACTION = 0.12;
                    const double TACKLE_DISTANCE = 0.05;
                    const double TACKLE_PROBABILITY = 0.35; // Increased tackle chance slightly more

                    double distToTarget = Math.Sqrt(
                        Math.Pow(player.Position.X - targetPos.X, 2) +
                        Math.Pow(player.Position.Y - targetPos.Y, 2));

                    // Move towards the target (ball/player)
                    dx += (targetPos.X - player.Position.X) / (distToTarget + 0.01) * DEFENSIVE_BALL_ATTRACTION;
                    dy += (targetPos.Y - player.Position.Y) / (distToTarget + 0.01) * DEFENSIVE_BALL_ATTRACTION;

                    // 2. Attempt Tackle if close enough
                    if (playerWithBall != null && distToTarget < TACKLE_DISTANCE)
                    {
                        if (_random.NextDouble() < TACKLE_PROBABILITY)
                        {
                            _logger.LogInformation("TACKLE! Player {DefenderId} tackles {AttackerId}", player.PlayerId, playerWithBall.PlayerId);
                            game.BallPossession = string.Empty; // Ball becomes loose
                            double tackleVelX = (_random.NextDouble() - 0.5) * 0.03; // Slightly more impactful velocity
                            double tackleVelY = (_random.NextDouble() - 0.5) * 0.03;
                            game.Ball.VelocityX = tackleVelX;
                            game.Ball.VelocityY = tackleVelY;
                            // --- IMMEDIATELY UPDATE BALL POSITION ON TACKLE ---
                            game.Ball.Position.X = Math.Clamp(playerWithBall.Position.X + tackleVelX, 0, 1);
                            game.Ball.Position.Y = Math.Clamp(playerWithBall.Position.Y + tackleVelY, 0, 1);
                            // -------------------------------------------------
                        }
                    }
                }
                else // If active defender decides not to press high
                {
                     // Fall back to passive logic (maintain position)
                     dx += (basePosition.X - player.Position.X) * POSITION_RECOVERY_WEIGHT * 2.0; // Stronger recovery
                     dy += (basePosition.Y - player.Position.Y) * POSITION_RECOVERY_WEIGHT * 2.0;
                }
            }
            else
            {
                // --- Passive Defender Logic (Others) ---
                // 1. Move strongly toward base defensive position
                dx += (basePosition.X - player.Position.X) * POSITION_RECOVERY_WEIGHT * 2.5; // Increased significantly
                dy += (basePosition.Y - player.Position.Y) * POSITION_RECOVERY_WEIGHT * 2.5; // Increased significantly

                // 2. Very weak attraction to the ball, only if in own half
                double ownHalfLine = isTeamA ? 0.5 : 0.5;
                bool ballInOwnHalf = (isTeamA && targetPos.X < ownHalfLine) || (!isTeamA && targetPos.X > ownHalfLine);

                if (ballInOwnHalf)
                {
                    const double PASSIVE_BALL_ATTRACTION = 0.01; // Very low attraction
                    double distToTarget = Math.Sqrt(
                       Math.Pow(player.Position.X - targetPos.X, 2) +
                       Math.Pow(player.Position.Y - targetPos.Y, 2));

                    // Move slightly towards the target
                    dx += (targetPos.X - player.Position.X) / (distToTarget + 0.01) * PASSIVE_BALL_ATTRACTION;
                    dy += (targetPos.Y - player.Position.Y) / (distToTarget + 0.01) * PASSIVE_BALL_ATTRACTION;
                }

                // 3. Avoid teammates (important for maintaining formation)
                AvoidTeammates(game, player, ref dx, ref dy, isTeamA);
            }

            // 4. Add small random movement for naturalism (apply to both active/passive)
            dx += (_random.NextDouble() - 0.5) * PLAYER_SPEED * 0.2;
            dy += (_random.NextDouble() - 0.5) * PLAYER_SPEED * 0.2;

            // Apply movement with boundary checking
            player.Position.X = Math.Clamp(player.Position.X + dx, 0, 1);
            player.Position.Y = Math.Clamp(player.Position.Y + dy, 0, 1);
        }
        
        private void AvoidTeammates(
            FootballCommentary.Core.Models.GameState game, 
            Player player, 
            ref double dx, 
            ref double dy, 
            bool isTeamA)
        {
            // Get list of teammates
            var teammates = isTeamA ? 
                game.HomeTeam.Players.Where(p => p.PlayerId != player.PlayerId) : 
                game.AwayTeam.Players.Where(p => p.PlayerId != player.PlayerId);
            
            foreach (var teammate in teammates)
            {
                double distance = Math.Sqrt(
                    Math.Pow(player.Position.X - teammate.Position.X, 2) +
                    Math.Pow(player.Position.Y - teammate.Position.Y, 2));
                
                // Apply repulsion when too close to teammates
                if (distance < TEAMMATE_AVOIDANCE_DISTANCE && distance > 0)
                {
                    double repulsionStrength = (TEAMMATE_AVOIDANCE_DISTANCE - distance) / TEAMMATE_AVOIDANCE_DISTANCE;
                    double repulsionX = (player.Position.X - teammate.Position.X) / distance * repulsionStrength * PLAYER_SPEED;
                    double repulsionY = (player.Position.Y - teammate.Position.Y) / distance * repulsionStrength * PLAYER_SPEED;
                    
                    dx += repulsionX;
                    dy += repulsionY;
                }
            }
        }
        
        private void UpdateBallPosition(FootballCommentary.Core.Models.GameState game, out bool publishEvent, out GameEvent? gameEvent)
        {
            publishEvent = false;
            gameEvent = null;
            
            // If no player has the ball, find the closest one
            if (string.IsNullOrEmpty(game.BallPossession))
            {
                var allPlayers = game.HomeTeam.Players.Concat(game.AwayTeam.Players);
                var closestPlayer = allPlayers.OrderBy(p => 
                    Math.Sqrt(
                        Math.Pow(p.Position.X - game.Ball.Position.X, 2) + 
                        Math.Pow(p.Position.Y - game.Ball.Position.Y, 2)
                    )).FirstOrDefault();
                
                if (closestPlayer != null)
                {
                    game.BallPossession = closestPlayer.PlayerId;
                    game.Ball.Position = closestPlayer.Position;
                    
                    publishEvent = true;
                    gameEvent = new GameEvent
                    {
                        GameId = game.GameId,
                        EventType = GameEventType.Pass,
                        TeamId = closestPlayer.PlayerId.StartsWith("TeamA") ? "TeamA" : "TeamB",
                        PlayerId = TryParsePlayerId(closestPlayer.PlayerId),
                        Position = closestPlayer.Position
                    };
                }
                return;
            }
            
            // Find the player who has the ball
            Player? playerWithBall = null;
            bool isTeamA = game.BallPossession.StartsWith("TeamA");
            Team team = isTeamA ? game.HomeTeam : game.AwayTeam;
            
            playerWithBall = team.Players.FirstOrDefault(p => p.PlayerId == game.BallPossession);
            if (playerWithBall == null)
            {
                game.BallPossession = string.Empty;
                return;
            }
            
            // Update ball position to match player
            game.Ball.Position = playerWithBall.Position;
            
            // Random chance of losing possession
            if (_random.NextDouble() < POSSESSION_CHANGE_PROBABILITY)
            {
                game.BallPossession = string.Empty;
                return;
            }
            
            // Decide what this player should do with the ball
            // If close to goal, shoot
            double goalPostX = isTeamA ? GOAL_POST_X_TEAM_B : GOAL_POST_X_TEAM_A;
            double distanceToGoal = Math.Abs(playerWithBall.Position.X - goalPostX);
            
            if (distanceToGoal < SHOOTING_DISTANCE)
            {
                // Try to score
                if (_random.NextDouble() < 0.4) // 40% chance to score when shooting
                {
                    // --- GOAL LOGIC --- 
                    // Goal!
                    string scoringTeamId = isTeamA ? "TeamA" : "TeamB";
                    if (isTeamA)
                    {
                        game.HomeTeam.Score++;
                    }
                    else
                    {
                        game.AwayTeam.Score++;
                    }

                    _logger.LogInformation("GOAL!!! Team {ScoringTeam} scored. Score: {HomeScore}-{AwayScore}", 
                        scoringTeamId, game.HomeTeam.Score, game.AwayTeam.Score);

                    // Set ball position TO THE GOAL, not center yet
                    game.Ball.Position = new Position { X = goalPostX, Y = 0.5 }; 
                    game.BallPossession = string.Empty; // No one possesses during celebration
                    game.Ball.VelocityX = 0;
                    game.Ball.VelocityY = 0;
                    game.Status = GameStatus.GoalScored; // Enter goal scored state
                    game.LastScoringTeamId = scoringTeamId; // Remember who scored for reset

                    // Publish goal event
                    publishEvent = true;
                    int? scorerPlayerId = TryParsePlayerId(playerWithBall.PlayerId);
                    gameEvent = new GameEvent
                    {
                        GameId = game.GameId,
                        EventType = GameEventType.Goal,
                        TeamId = scoringTeamId,
                        PlayerId = scorerPlayerId.HasValue ? scorerPlayerId.Value + 1 : (int?)null, 
                        Position = new Position { X = goalPostX, Y = 0.5 }
                    };
                    
                    return;
                }
                else
                {
                    // Shot but missed or saved
                    publishEvent = true;
                    gameEvent = new GameEvent
                    {
                        GameId = game.GameId,
                        EventType = GameEventType.Shot,
                        TeamId = isTeamA ? "TeamA" : "TeamB",
                        PlayerId = TryParsePlayerId(playerWithBall.PlayerId),
                        Position = playerWithBall.Position
                    };
                    
                    // Loose ball
                    game.BallPossession = string.Empty;
                    return;
                }
            }
            
            // Not close to goal, try to pass to teammates
            List<Player> teammates = team.Players.Where(p => p.PlayerId != playerWithBall.PlayerId).ToList();
            
            // Find teammates in good position to receive a pass
            var validPassTargets = teammates.Where(p => {
                double distance = Math.Sqrt(
                    Math.Pow(p.Position.X - playerWithBall.Position.X, 2) + 
                    Math.Pow(p.Position.Y - playerWithBall.Position.Y, 2));
                
                // Is teammate within passing distance?
                if (distance > PASSING_DISTANCE)
                    return false;
                
                // For Team A, prioritize teammates further up the field (higher X)
                if (isTeamA && p.Position.X <= playerWithBall.Position.X)
                    return false;
                
                // For Team B, prioritize teammates further down the field (lower X)
                if (!isTeamA && p.Position.X >= playerWithBall.Position.X)
                    return false;
                
                return true;
            }).ToList();
            
            // If we have valid targets, pass to one
            if (validPassTargets.Any())
            {
                // Pass to a random valid target
                int targetIndex = _random.Next(validPassTargets.Count);
                Player targetPlayer = validPassTargets[targetIndex];
                
                // Update possession
                game.BallPossession = targetPlayer.PlayerId;
                
                // Publish pass event
                publishEvent = true;
                gameEvent = new GameEvent
                {
                    GameId = game.GameId,
                    EventType = GameEventType.Pass,
                    TeamId = isTeamA ? "TeamA" : "TeamB",
                    PlayerId = TryParsePlayerId(playerWithBall.PlayerId),
                    Position = playerWithBall.Position
                };
            }
        }
        
        private int? TryParsePlayerId(string? playerIdString)
        {
            if (string.IsNullOrEmpty(playerIdString))
            {
                return null;
            }

            var parts = playerIdString.Split('_');
            if (parts.Length == 2 && int.TryParse(parts[1], out int id))
            {
                return id;
            }
            
            _logger.LogWarning("Could not parse PlayerId from string: {PlayerIdString}", playerIdString);
            return null; // Return null if parsing fails
        }
        
        public async Task<string> GetTacticalAnalysisAsync(string gameId)
        {
            if (!_state.State.Games.TryGetValue(gameId, out var game))
            {
                throw new KeyNotFoundException($"Game {gameId} not found");
            }
            
            // Get tactical analysis from LLM
            return await _llmTactician.GetTacticalAnalysisAsync(game);
        }
    }
}