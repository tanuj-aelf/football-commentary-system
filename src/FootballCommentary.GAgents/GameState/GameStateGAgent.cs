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
        private const double PLAYER_SPEED = 0.015; // Increased from 0.01
        private const double BALL_SPEED = 0.02;
        private const double GOAL_POST_X_TEAM_A = 0.05;
        private const double GOAL_POST_X_TEAM_B = 0.95;
        private const double PASSING_DISTANCE = 0.3;
        private const double SHOOTING_DISTANCE = 0.15; // Reduced from 0.3 to make shooting range more realistic
        private const double POSITION_RECOVERY_WEIGHT = 0.01; // Reduced from 0.02
        private const double BALL_ATTRACTION_WEIGHT = 0.04; // Increased from 0.03
        private const double PLAYER_ROLE_ADHERENCE = 0.03; // Reduced from 0.04
        private const double FORMATION_ADHERENCE = 0.015; // Reduced from 0.03
        
        // Field zones for more realistic positioning
        private const double DEFENSE_ZONE = 0.3;
        private const double MIDFIELD_ZONE = 0.6;
        private const double ATTACK_ZONE = 0.7;
        private const double TEAMMATE_AVOIDANCE_DISTANCE = 0.08;
        private const double OPPONENT_AWARENESS_DISTANCE = 0.15;
        
        // List to track recent tackle attempts
        private readonly List<TackleAttempt> _recentTackleAttempts = new();
        
        // Tackle and ball control constants
        private const double PLAYER_CONTROL_DISTANCE = 0.05;
        private const double TACKLE_DISTANCE = 0.08;
        private const double BASE_TACKLE_CHANCE = 0.35;
        
        // Add a new constant for loose ball attraction
        private const double LOOSE_BALL_ATTRACTION = 0.08; // How strongly players are drawn to a loose ball
        
        // These constants need to be defined at class level
        private const double GOAL_X_MIN = 0.45;
        private const double GOAL_X_MAX = 0.55;
        
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
                
                string formationName = GetFormationName(formation);
                string teamName = isTeamA ? gameState.HomeTeam.Name : gameState.AwayTeam.Name;
                
                _logger.LogInformation("{TeamName} ({TeamId}) is using formation {FormationName} ({Formation}) for game {GameId}", 
                    teamName, isTeamA ? "Team A" : "Team B", formationName, formation, gameId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initializing team formation for {TeamId} in game {GameId}", 
                    isTeamA ? "Team A" : "Team B", gameId);
            }
        }
        
        // Helper method to get a readable formation name
        private string GetFormationName(TeamFormation formation)
        {
            switch (formation)
            {
                case TeamFormation.Formation_4_4_2:
                    return "4-4-2 (Classic)";
                case TeamFormation.Formation_4_3_3:
                    return "4-3-3 (Attacking)";
                case TeamFormation.Formation_4_2_3_1:
                    return "4-2-3-1 (Modern)";
                case TeamFormation.Formation_3_5_2:
                    return "3-5-2 (Wing-back)";
                case TeamFormation.Formation_5_3_2:
                    return "5-3-2 (Defensive)";
                case TeamFormation.Formation_4_1_4_1:
                    return "4-1-4-1 (Balanced)";
                default:
                    return formation.ToString();
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
            
            // Reinitialize team formations to ensure random and different formations at game start
            _logger.LogInformation("Initializing team formations at game start for {GameId}", gameId);
            await InitializeTeamFormation(gameId, game, true);  // Team A
            await InitializeTeamFormation(gameId, game, false); // Team B
            
            // Reset player positions based on the new formations
            ResetPlayerPositions(game, true);  // Team A
            ResetPlayerPositions(game, false); // Team B
            
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
            
            // Define team-specific zones to ensure teams are on correct sides
            double defenseX, midfieldX, attackX;
            
            if (teamId == "TeamA") {
                // Team A (home) positioned on left side
                defenseX = 0.2;  // Defenders around 20% from left edge
                midfieldX = 0.35; // Midfielders around 35% from left edge
                attackX = 0.5;   // Forwards around center line or slightly ahead
            } else {
                // Team B (away) positioned on right side
                defenseX = 0.8;  // Defenders around 20% from right edge
                midfieldX = 0.65; // Midfielders around 35% from right edge
                attackX = 0.5;   // Forwards around center line or slightly behind
            }
            
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
                    x = defenseX;
                    y = 0.2 + (0.6 * (i - 1) / 3); // Spread evenly across the width
                }
                else if (i <= 8) // Midfielders (player indices 5-8)
                {
                    x = midfieldX;
                    y = 0.2 + (0.6 * (i - 5) / 3); // Spread evenly across the width
                }
                else // Forwards (player indices 9-10)
                {
                    // Keep forwards on their respective sides of the center line
                    x = teamId == "TeamA" ? 
                        Math.Min(attackX + 0.1, 0.49) : // Team A forwards slightly left of center
                        Math.Max(attackX - 0.1, 0.51);  // Team B forwards slightly right of center
                    
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
                        
                        // Flag formations for update on next simulation cycle instead of calling directly
                        _logger.LogInformation("Scheduling team formations update after goal for game {GameId}", gameId);
                        
                        // Reset player positions using current formations
                        ResetPlayerPositions(game, true);  // Team A
                        ResetPlayerPositions(game, false); // Team B
                        
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
            // Only update LLM suggestions every ~25 steps (2.5 seconds) instead of 50 steps
            bool useLLMSuggestions = game.SimulationStep % 25 == 0;
            
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
                // If not using LLM directly, generate more natural variations of cached suggestions
                if (_state.State.TeamAFormations.TryGetValue(game.GameId, out var teamAFormation) && 
                    teamAFormation.CachedMovements.Any())
                {
                    // Add variations to cached movements
                    foreach (var (playerId, movement) in teamAFormation.CachedMovements)
                    {
                        // More natural movement variation - scale with game step for continuous motion
                        double timeVariation = Math.Sin(game.SimulationStep * 0.1) * 0.007;
                        double dx = movement.dx + timeVariation + (_random.NextDouble() - 0.5) * 0.015;
                        double dy = movement.dy + timeVariation + (_random.NextDouble() - 0.5) * 0.015;
                        
                        // Clamp values
                        dx = Math.Clamp(dx, -0.1, 0.1);
                        dy = Math.Clamp(dy, -0.1, 0.1);
                        
                        teamAMovementSuggestions[playerId] = (dx, dy);
                    }
                }
                else
                {
                    // If we don't have any cached movements, create natural idle movements for all players
                    foreach (var player in homeTeamPlayers)
                    {
                        double timeVariation = Math.Sin(game.SimulationStep * 0.1 + _random.NextDouble() * 10) * 0.01;
                        double dx = timeVariation + (_random.NextDouble() - 0.5) * 0.01;
                        double dy = timeVariation + (_random.NextDouble() - 0.5) * 0.01;
                        teamAMovementSuggestions[player.PlayerId] = (dx, dy);
                    }
                }
                
                if (_state.State.TeamBFormations.TryGetValue(game.GameId, out var teamBFormation) && 
                    teamBFormation.CachedMovements.Any())
                {
                    // Add variations to cached movements
                    foreach (var (playerId, movement) in teamBFormation.CachedMovements)
                    {
                        // More natural movement variation - scale with game step for continuous motion
                        double timeVariation = Math.Sin(game.SimulationStep * 0.1 + Math.PI) * 0.007; // Offset for team B
                        double dx = movement.dx + timeVariation + (_random.NextDouble() - 0.5) * 0.015;
                        double dy = movement.dy + timeVariation + (_random.NextDouble() - 0.5) * 0.015;
                        
                        // Clamp values
                        dx = Math.Clamp(dx, -0.1, 0.1);
                        dy = Math.Clamp(dy, -0.1, 0.1);
                        
                        teamBMovementSuggestions[playerId] = (dx, dy);
                    }
                }
                else
                {
                    // If we don't have any cached movements, create natural idle movements for all players
                    foreach (var player in awayTeamPlayers)
                    {
                        double timeVariation = Math.Sin(game.SimulationStep * 0.1 + _random.NextDouble() * 10 + Math.PI) * 0.01;
                        double dx = timeVariation + (_random.NextDouble() - 0.5) * 0.01;
                        double dy = timeVariation + (_random.NextDouble() - 0.5) * 0.01;
                        teamBMovementSuggestions[player.PlayerId] = (dx, dy);
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
                
                // Special handling for player with ball - prioritize forward movement
                if (player.PlayerId == game.BallPossession)
                {
                    MoveBallPossessor(game, player);
                    continue;
                }
                
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
                    
                    // Add adherence to base position but with a weaker pull
                    dx += (basePosition.X - player.Position.X) * POSITION_RECOVERY_WEIGHT * 0.4;
                    dy += (basePosition.Y - player.Position.Y) * POSITION_RECOVERY_WEIGHT * 0.4;
                    
                    // Continuous fluctuation for more natural movement
                    double cyclicVariation = Math.Sin(game.SimulationStep * 0.08 + playerNumber * 0.5) * 0.004;
                    dx += cyclicVariation;
                    dy += cyclicVariation * (_random.NextDouble() > 0.5 ? 1 : -1);
                    
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
            // Get player number for role-based behavior
            int playerNumber = TryParsePlayerId(player.PlayerId) ?? 0;
            playerNumber += 1; // Convert to 1-based player number
            
            // Determine player zone and role
            bool isGoalkeeper = playerNumber == 1;
            bool isDefender = playerNumber >= 2 && playerNumber <= 5;
            bool isMidfielder = playerNumber >= 6 && playerNumber <= 8;
            bool isForward = playerNumber >= 9;
            
            // Different movement strategies based on roles
            double dx = 0, dy = 0;
            
            // Base movement: always have some attraction to assigned position but weaker
            double positionAdherenceWeight = FORMATION_ADHERENCE;
            
            // Add continuous natural movement using sine waves with player-specific phase
            double phase = playerNumber * 0.7;
            double naturalMovementX = Math.Sin(game.SimulationStep * 0.05 + phase) * 0.003;
            double naturalMovementY = Math.Cos(game.SimulationStep * 0.04 + phase) * 0.003;
            
            // First check if ball is loose and player should go for it
            if (string.IsNullOrEmpty(game.BallPossession) && game.Ball != null)
            {
                double distToBall = Math.Sqrt(
                    Math.Pow(player.Position.X - game.Ball.Position.X, 2) +
                    Math.Pow(player.Position.Y - game.Ball.Position.Y, 2));
                    
                // Get all players sorted by distance to ball
                var allPlayers = game.AllPlayers.OrderBy(p => 
                    Math.Sqrt(
                        Math.Pow(p.Position.X - game.Ball.Position.X, 2) + 
                        Math.Pow(p.Position.Y - game.Ball.Position.Y, 2)
                    )).ToList();
                    
                // Find closest teammates and opponents to ball
                double myRank = allPlayers.FindIndex(p => p.PlayerId == player.PlayerId);
                
                // If this player is among the closest to the ball, go for it with higher priority
                if (myRank < 3 || distToBall < 0.15)
                {
                    double ballAttractionStrength = LOOSE_BALL_ATTRACTION;
                    
                    // Adjust attraction based on distance - closer players are more attracted
                    ballAttractionStrength *= (1.0 - Math.Min(distToBall, 0.3) / 0.3) * 2.0;
                    
                    // Adjust attraction based on player role
                    if (isDefender && game.Ball.Position.X < 0.3) ballAttractionStrength *= 1.5; // Defenders prioritize ball in defense
                    if (isMidfielder) ballAttractionStrength *= 1.2; // Midfielders generally go for ball
                    if (isForward && game.Ball.Position.X > 0.7) ballAttractionStrength *= 1.5; // Forwards prioritize ball in attack
                    
                    // Move toward ball
                    dx += (game.Ball.Position.X - player.Position.X) * ballAttractionStrength;
                    dy += (game.Ball.Position.Y - player.Position.Y) * ballAttractionStrength;
                    
                    // Reduce position adherence when going for loose ball
                    positionAdherenceWeight *= 0.3;
                    
                    // Add a bit of randomness to prevent all players converging on exactly the same point
                    dx += (_random.NextDouble() - 0.5) * 0.01;
                    dy += (_random.NextDouble() - 0.5) * 0.01;
                }
            }
            
            // Role-specific adjustments
            if (isGoalkeeper) {
                // Goalkeepers stay very close to their position
                positionAdherenceWeight = FORMATION_ADHERENCE * 5.0;
                dx += (basePosition.X - player.Position.X) * positionAdherenceWeight;
                dy += (basePosition.Y - player.Position.Y) * positionAdherenceWeight;
            }
            else if (isDefender) {
                // Defenders maintain formation more strictly, rarely venture forward
                positionAdherenceWeight = FORMATION_ADHERENCE * 1.5; // Reduced from 2.0
                dx += (basePosition.X - player.Position.X) * positionAdherenceWeight;
                dy += (basePosition.Y - player.Position.Y) * positionAdherenceWeight;
                
                // Slight forward bias when team has possession but not too much
                double forwardBias = PLAYER_SPEED * (isTeamA ? 0.3 : -0.3);
                dx += forwardBias;

                // Add more natural movement
                dx += naturalMovementX;
                dy += naturalMovementY;
                
                // Defender rarely ventures too far forward
                double maxForwardPosition = isTeamA ? 0.4 : 0.6;
                if ((isTeamA && player.Position.X > maxForwardPosition) ||
                    (!isTeamA && player.Position.X < maxForwardPosition)) {
                    // If too far forward, strong pull back
                    double pullBackFactor = PLAYER_SPEED * 2.0;
                    dx += (isTeamA ? -pullBackFactor : pullBackFactor);
                }
            }
            else if (isMidfielder) {
                // Midfielders balance between attack and defense
                dx += (basePosition.X - player.Position.X) * positionAdherenceWeight;
                dy += (basePosition.Y - player.Position.Y) * positionAdherenceWeight;
                
                // Add more natural movement
                dx += naturalMovementX * 1.5;
                dy += naturalMovementY * 1.5;
                
                // Moderate forward bias when team has possession
                double forwardBias = PLAYER_SPEED * (isTeamA ? 0.5 : -0.5);
                dx += forwardBias;
                
                // Support attack but maintain some midfield presence
                if (playerWithBall != null) {
                    // If ball is in attacking third, some midfielders support attack
                    bool ballInAttackingThird = (isTeamA && playerWithBall.Position.X > 0.6) ||
                                                (!isTeamA && playerWithBall.Position.X < 0.4);
                               
                    if (ballInAttackingThird) {
                        // Randomly select midfielders to support attack (variation based on player number)
                        if (playerNumber % 2 == 0) {
                            // This midfielder supports attack
                            double supportFactor = PLAYER_SPEED * 0.6;
                            dx += (isTeamA ? supportFactor : -supportFactor);
                        }
                    }
                }
                
                // Midfielders have reasonable position limits
                double maxForwardPosition = isTeamA ? 0.7 : 0.3;
                double maxBackwardPosition = isTeamA ? 0.25 : 0.75;
                
                // Apply position limits
                if ((isTeamA && player.Position.X > maxForwardPosition) ||
                    (!isTeamA && player.Position.X < maxForwardPosition)) {
                    // Too far forward, moderate pull back
                    double pullBackFactor = PLAYER_SPEED * 1.0;
                    dx += (isTeamA ? -pullBackFactor : pullBackFactor);
                }
                else if ((isTeamA && player.Position.X < maxBackwardPosition) ||
                        (!isTeamA && player.Position.X > maxBackwardPosition)) {
                    // Too far back, moderate pull forward
                    double pullForwardFactor = PLAYER_SPEED * 1.0;
                    dx += (isTeamA ? pullForwardFactor : -pullForwardFactor);
                }
            }
            else if (isForward) {
                // Forwards focus on attacking positions
                dx += (basePosition.X - player.Position.X) * positionAdherenceWeight;
                dy += (basePosition.Y - player.Position.Y) * positionAdherenceWeight;
                
                // Add more natural movement
                dx += naturalMovementX * 2.0;
                dy += naturalMovementY * 2.0;
                
                // Strong forward bias
                double forwardBias = PLAYER_SPEED * (isTeamA ? 0.8 : -0.8);
                dx += forwardBias;
                
                // Forwards stay in attacking third most of the time
                double minForwardPosition = isTeamA ? 0.45 : 0.55;
                if ((isTeamA && player.Position.X < minForwardPosition) ||
                    (!isTeamA && player.Position.X > minForwardPosition)) {
                    // Too far back, strong pull forward
                    double pullForwardFactor = PLAYER_SPEED * 1.5;
                    dx += (isTeamA ? pullForwardFactor : -pullForwardFactor);
                }
                
                // Forwards make runs into space
                if (playerWithBall != null && playerWithBall.PlayerId != player.PlayerId) {
                    // Check if ball possessor is midfielder or defender (good time for forward runs)
                    int ballPlayerNumber = TryParsePlayerId(playerWithBall.PlayerId) ?? 0;
                    ballPlayerNumber += 1;
                    
                    if (ballPlayerNumber < 9) { // Ball with non-forward
                        // Make attacking runs
                        double runBias = PLAYER_SPEED * 1.0 * (0.5 + _random.NextDouble() * 0.5); // Variable running
                        dx += (isTeamA ? runBias : -runBias);
                        
                        // Random lateral movement to find space
                        double lateralMovement = (_random.NextDouble() - 0.5) * PLAYER_SPEED * 0.6;
                        dy += lateralMovement;
                    }
                }
            }
            
            // All players: avoid clustering with teammates
            AvoidTeammates(game, player, ref dx, ref dy, isTeamA);
            
            // Small random movement for naturalism - reduced for goalkeepers
            double randomFactor = isGoalkeeper ? 0.1 : 0.3;
            dx += (_random.NextDouble() - 0.5) * PLAYER_SPEED * randomFactor;
            dy += (_random.NextDouble() - 0.5) * PLAYER_SPEED * randomFactor;
            
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
            // Get player number for role-based behavior
            int playerNumber = TryParsePlayerId(player.PlayerId) ?? 0;
            playerNumber += 1; // Convert to 1-based player number
            
            // Determine player zone and role
            bool isGoalkeeper = playerNumber == 1;
            bool isDefender = playerNumber >= 2 && playerNumber <= 5;
            bool isMidfielder = playerNumber >= 6 && playerNumber <= 8;
            bool isForward = playerNumber >= 9;
            
            double dx = 0, dy = 0;
            Position targetPos = playerWithBall?.Position ?? game.Ball.Position;
            
            // Add continuous natural movement using sine waves with player-specific phase
            double phase = playerNumber * 0.7;
            double naturalMovementX = Math.Sin(game.SimulationStep * 0.05 + phase + Math.PI) * 0.003;
            double naturalMovementY = Math.Cos(game.SimulationStep * 0.04 + phase + Math.PI) * 0.003;
            
            // First check if ball is loose and player should go for it
            if (string.IsNullOrEmpty(game.BallPossession) && game.Ball != null)
            {
                double distToBall = Math.Sqrt(
                    Math.Pow(player.Position.X - game.Ball.Position.X, 2) +
                    Math.Pow(player.Position.Y - game.Ball.Position.Y, 2));
                    
                // Get all players sorted by distance to ball
                var allPlayers = game.AllPlayers.OrderBy(p => 
                    Math.Sqrt(
                        Math.Pow(p.Position.X - game.Ball.Position.X, 2) + 
                        Math.Pow(p.Position.Y - game.Ball.Position.Y, 2)
                    )).ToList();
                    
                // Find this player's rank in distance to ball
                int myRank = allPlayers.FindIndex(p => p.PlayerId == player.PlayerId);
                
                // If this player is among the closest to the ball, go for it with higher priority
                if (myRank < 3 || distToBall < 0.15)
                {
                    double ballAttractionStrength = LOOSE_BALL_ATTRACTION * 1.2; // Slightly higher when opponent doesn't have ball
                    
                    // Adjust attraction based on distance - closer players are more attracted
                    ballAttractionStrength *= (1.0 - Math.Min(distToBall, 0.3) / 0.3) * 2.0;
                    
                    // Adjust attraction based on player role and field position
                    bool ballInOurHalf = (isTeamA && game.Ball.Position.X < 0.5) || (!isTeamA && game.Ball.Position.X > 0.5);
                    
                    if (isDefender && ballInOurHalf) ballAttractionStrength *= 1.8; // Defenders prioritize ball in our half
                    else if (isDefender) ballAttractionStrength *= 0.7; // Defenders less interested in opponent half
                    
                    if (isMidfielder) ballAttractionStrength *= 1.3; // Midfielders generally go for ball
                    
                    if (isForward && !ballInOurHalf) ballAttractionStrength *= 1.3; // Forwards more interested in opponent half
                    else if (isForward) ballAttractionStrength *= 0.7; // Forwards less interested in our half
                    
                    // Move toward ball
                    dx += (game.Ball.Position.X - player.Position.X) * ballAttractionStrength;
                    dy += (game.Ball.Position.Y - player.Position.Y) * ballAttractionStrength;
                    
                    // Add a bit of randomness to prevent all players converging on exactly the same point
                    dx += (_random.NextDouble() - 0.5) * 0.01;
                    dy += (_random.NextDouble() - 0.5) * 0.01;
                    
                    // Return early if going for the loose ball, overriding normal defensive behavior
                    player.Position.X = Math.Clamp(player.Position.X + dx, 0, 1);
                    player.Position.Y = Math.Clamp(player.Position.Y + dy, 0, 1);
                    return;
                }
            }
            
            // Continue with normal defensive positioning if not going for loose ball
            // ... (rest of the function remains unchanged)
            
            // Defensive positioning based on player role
            if (isGoalkeeper) {
                // Goalkeeper stays near the goal, with slight adjustment based on ball position
                dx = (basePosition.X - player.Position.X) * (FORMATION_ADHERENCE * 5.0);
                
                // Goalkeepers adjust Y position based on ball position, but limited movement
                double targetY = 0.5 + (targetPos.Y - 0.5) * 0.5; // Move toward ball's Y position, but only 50%
                targetY = Math.Clamp(targetY, 0.3, 0.7); // Limit keeper's range
                dy = (targetY - player.Position.Y) * (FORMATION_ADHERENCE * 2.0);
            }
            else if (isDefender) {
                // Defenders focus on maintaining defensive shape and position
                dx = (basePosition.X - player.Position.X) * (FORMATION_ADHERENCE * 1.5); // Reduced from 2.0
                dy = (basePosition.Y - player.Position.Y) * (FORMATION_ADHERENCE * 1.2); // Reduced from 1.5
                
                // Add more natural movement
                dx += naturalMovementX;
                dy += naturalMovementY;
                
                // Defenders shift slightly toward ball side
                double ballSideShift = (targetPos.Y - 0.5) * 0.3; // Shift 30% toward ball's side
                dy += ballSideShift * PLAYER_SPEED * 0.2;
                
                // Only nearest defender actively approaches ball carrier
                if (playerWithBall != null) {
                    double distanceToTarget = Math.Sqrt(
                        Math.Pow(player.Position.X - targetPos.X, 2) +
                        Math.Pow(player.Position.Y - targetPos.Y, 2));
                    
                    // Defensive third is where defenders become active
                    bool ballInDefensiveThird = (isTeamA && targetPos.X < 0.4) || 
                                              (!isTeamA && targetPos.X > 0.6);
                              
                    // Find if this is the closest defender
                    bool isClosestDefender = false;
                    var teammates = isTeamA ? game.HomeTeam.Players : game.AwayTeam.Players;
                    var otherDefenders = teammates
                        .Where(p => {
                            // Get other player's number 
                            int otherNum = TryParsePlayerId(p.PlayerId) ?? 0;
                            otherNum += 1;
                            return p.PlayerId != player.PlayerId && otherNum >= 2 && otherNum <= 5;
                        })
                        .ToList();
                        
                    // Calculate distances for all defenders
                    var defenderDistances = otherDefenders
                        .Select(p => Math.Sqrt(
                            Math.Pow(p.Position.X - targetPos.X, 2) +
                            Math.Pow(p.Position.Y - targetPos.Y, 2)))
                        .ToList();
                        
                    // Am I the closest defender?
                    isClosestDefender = !defenderDistances.Any(d => d < distanceToTarget);
                        
                    if (ballInDefensiveThird && (isClosestDefender || distanceToTarget < 0.15)) {
                        // Defensive pressure, but still controlled
                        double pressureFactor = PLAYER_SPEED * 0.8;
                        dx += (targetPos.X - player.Position.X) * pressureFactor;
                        dy += (targetPos.Y - player.Position.Y) * pressureFactor;
                    }
                }
            }
            else if (isMidfielder) {
                // Midfielders balance between defensive duties and maintaining shape
                dx = (basePosition.X - player.Position.X) * (FORMATION_ADHERENCE * 1.2); // Reduced from 1.5
                dy = (basePosition.Y - player.Position.Y) * (FORMATION_ADHERENCE * 1.0); // Reduced from 1.2
                
                // Add more natural movement
                dx += naturalMovementX * 1.5;
                dy += naturalMovementY * 1.5;
                
                // Slight shift toward ball
                double ballSideShift = (targetPos.Y - 0.5) * 0.4; // Shift 40% toward ball's side
                dy += ballSideShift * PLAYER_SPEED * 0.3;
                
                // Middle third is where midfielders are most active
                bool ballInMiddleThird = (targetPos.X >= 0.3 && targetPos.X <= 0.7);
                
                if (playerWithBall != null && ballInMiddleThird) {
                    // Get distance to ball
                    double distanceToTarget = Math.Sqrt(
                        Math.Pow(player.Position.X - targetPos.X, 2) +
                        Math.Pow(player.Position.Y - targetPos.Y, 2));
                        
                    // Midfielder pressing - more active but selective
                    if (distanceToTarget < 0.2 && playerNumber % 2 == 0) { // Only some midfielders press
                        double pressureFactor = PLAYER_SPEED * 0.7;
                        dx += (targetPos.X - player.Position.X) * pressureFactor;
                        dy += (targetPos.Y - player.Position.Y) * pressureFactor;
                    }
                }
            }
            else if (isForward) {
                // Forwards maintain higher position but provide light pressure
                dx = (basePosition.X - player.Position.X) * FORMATION_ADHERENCE * 0.8; // Reduced pull to position
                dy = (basePosition.Y - player.Position.Y) * FORMATION_ADHERENCE * 0.8;
                
                // Add more natural movement
                dx += naturalMovementX * 2.0;
                dy += naturalMovementY * 2.0;
                
                // Forwards stay somewhat forward even when defending
                double minPosition = isTeamA ? 0.4 : 0.6;
                if ((isTeamA && player.Position.X < minPosition) ||
                    (!isTeamA && player.Position.X > minPosition)) {
                    // Pull forward to maintain attacking position
                    double pullFactor = PLAYER_SPEED * 0.5;
                    dx += (isTeamA ? pullFactor : -pullFactor);
                }
                
                // Forwards only press in attacking third
                bool ballInAttackingThird = (isTeamA && targetPos.X > 0.6) ||
                                          (!isTeamA && targetPos.X < 0.4);
                                          
                // Limited pressing from forwards - only if ball is in attacking third
                if (playerWithBall != null && ballInAttackingThird) {
                    double distanceToTarget = Math.Sqrt(
                       Math.Pow(player.Position.X - targetPos.X, 2) +
                       Math.Pow(player.Position.Y - targetPos.Y, 2));

                    if (distanceToTarget < 0.15) { // Close enough to press
                        double pressureFactor = PLAYER_SPEED * 0.6;
                        dx += (targetPos.X - player.Position.X) * pressureFactor;
                        dy += (targetPos.Y - player.Position.Y) * pressureFactor;
                    }
                }
            }
            
            // All players: avoid clustering with teammates
            AvoidTeammates(game, player, ref dx, ref dy, isTeamA);
            
            // Small random movement for naturalism - reduced for goalkeepers
            double randomFactor = isGoalkeeper ? 0.1 : 0.3;
            dx += (_random.NextDouble() - 0.5) * PLAYER_SPEED * randomFactor;
            dy += (_random.NextDouble() - 0.5) * PLAYER_SPEED * randomFactor;

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
            // Get all teammates (excluding self)
            var teammates = isTeamA 
                ? game.HomeTeam.Players.Where(p => p.PlayerId != player.PlayerId)
                : game.AwayTeam.Players.Where(p => p.PlayerId != player.PlayerId);
            
            // Define minimum comfortable distance between teammates
            const double MIN_TEAMMATE_DISTANCE = 0.1;
            
            foreach (var teammate in teammates)
            {
                double distToTeammate = Math.Sqrt(
                    Math.Pow(player.Position.X - teammate.Position.X, 2) +
                    Math.Pow(player.Position.Y - teammate.Position.Y, 2));
                    
                // If too close to teammate, add avoidance vector
                if (distToTeammate < MIN_TEAMMATE_DISTANCE)
                {
                    // Calculate vector direction away from teammate
                    double avoidanceStrength = (MIN_TEAMMATE_DISTANCE - distToTeammate) / MIN_TEAMMATE_DISTANCE;
                    avoidanceStrength = Math.Min(avoidanceStrength, 0.5); // Cap the avoidance strength
                    
                    // Add avoidance force (inversely proportional to distance)
                    dx += (player.Position.X - teammate.Position.X) * avoidanceStrength * PLAYER_SPEED * 0.8;
                    dy += (player.Position.Y - teammate.Position.Y) * avoidanceStrength * PLAYER_SPEED * 0.8;
                }
            }
        }
        
        private void UpdateBallPosition(FootballCommentary.Core.Models.GameState game, out bool publishEvent, out GameEvent? gameEvent)
        {
            const double GOAL_X_MIN = 0.45;
            const double GOAL_X_MAX = 0.55;
            publishEvent = false;
            gameEvent = null;
            
            // Maximum number of players that can attempt a tackle at once
            const int MAX_TACKLERS = 2;
            
            // Track recent tackle attempts to prevent swarm behavior
            DateTime currentTime = DateTime.UtcNow;
            
            // Reset goal cooldown if enough time has passed
            if (_recentGoalScored && (currentTime - _lastGoalTime).TotalSeconds > GOAL_COOLDOWN_SECONDS)
            {
                _recentGoalScored = false;
                _logger.LogDebug("Goal cooldown expired, allowing new goal detection");
            }
            
            // Clear old tackle attempts (older than 2 seconds)
            _recentTackleAttempts.RemoveAll(t => (currentTime - t.Time).TotalSeconds > 2);
            
            // Players who made recent tackle attempts are in a cooldown period
            var playersInTackleCooldown = _recentTackleAttempts
                .Select(t => t.PlayerId)
                .ToHashSet();

            if (string.IsNullOrEmpty(game.BallPossession))
            {
                // Update ball position based on velocity
                game.Ball.Position.X = Math.Clamp(game.Ball.Position.X + game.Ball.VelocityX, 0, 1);
                game.Ball.Position.Y = Math.Clamp(game.Ball.Position.Y + game.Ball.VelocityY, 0, 1);

                // Apply friction to gradually slow the ball
                game.Ball.VelocityX *= 0.95;
                game.Ball.VelocityY *= 0.95;

                // If the ball is very slow, consider it stopped
                if (Math.Abs(game.Ball.VelocityX) < 0.001 && Math.Abs(game.Ball.VelocityY) < 0.001)
                {
                    game.Ball.VelocityX = 0;
                    game.Ball.VelocityY = 0;
                }

                // Check if a player gets the ball
                foreach (var player in game.AllPlayers)
                {
                    double distToBall = Math.Sqrt(
                        Math.Pow(player.Position.X - game.Ball.Position.X, 2) +
                        Math.Pow(player.Position.Y - game.Ball.Position.Y, 2));

                    if (distToBall < PLAYER_CONTROL_DISTANCE)
                    {
                        game.BallPossession = player.PlayerId;
                        _logger.LogInformation("Player {PlayerId} gains possession", player.PlayerId);
                        break;
                    }
                }

                // Check if the ball went into a goal
                CheckForGoal(game, ref publishEvent, ref gameEvent);
            }
            else
            {
                // Find the player who has the ball
                Player? playerWithBall = game.AllPlayers.FirstOrDefault(p => p.PlayerId == game.BallPossession);
                
                if (playerWithBall == null)
                {
                    // This shouldn't happen, but just in case
                    game.BallPossession = string.Empty;
                    return;
                }
                
                // Update ball position to follow the player
                game.Ball.Position = playerWithBall.Position;
                game.Ball.VelocityX = 0;
                game.Ball.VelocityY = 0;

                // Get team of the ball possessor
                bool ballPossessorIsTeamA = playerWithBall.PlayerId.StartsWith("TeamA");

                // Find opponents who are close enough to tackle
                var opponents = ballPossessorIsTeamA 
                    ? game.AwayTeam.Players 
                    : game.HomeTeam.Players;
                
                // Get player numbers and roles for determining tackling priority
                int possessorNumber = TryParsePlayerId(playerWithBall.PlayerId) ?? 0;
                possessorNumber += 1; // Convert to 1-based numbers
                
                // Track potential tacklers by distance
                var potentialTacklers = new List<(Player player, double distance, int priority)>();
                
                foreach (var opponent in opponents)
                {
                    // Skip opponents who recently attempted a tackle (cooldown)
                    if (playersInTackleCooldown.Contains(opponent.PlayerId))
                        continue;
                        
                    double distToBallCarrier = Math.Sqrt(
                        Math.Pow(opponent.Position.X - playerWithBall.Position.X, 2) +
                        Math.Pow(opponent.Position.Y - playerWithBall.Position.Y, 2));
                        
                    if (distToBallCarrier < TACKLE_DISTANCE)
                    {
                        // Determine tackling priority based on player role
                        int opponentNumber = TryParsePlayerId(opponent.PlayerId) ?? 0;
                        opponentNumber += 1; // Convert to 1-based numbers
                        
                        int tacklePriority;
                        if (opponentNumber >= 2 && opponentNumber <= 5) // Defenders
                            tacklePriority = 3; // Highest priority
                        else if (opponentNumber >= 6 && opponentNumber <= 8) // Midfielders
                            tacklePriority = 2; // Medium priority
                        else if (opponentNumber >= 9) // Forwards
                            tacklePriority = 1; // Lower priority
                        else // Goalkeepers
                            tacklePriority = 0; // Lowest priority
                        
                        potentialTacklers.Add((opponent, distToBallCarrier, tacklePriority));
                    }
                }
                
                // Take only the closest players by priority and distance, up to MAX_TACKLERS
                var selectedTacklers = potentialTacklers
                    .OrderByDescending(t => t.priority) // First by role priority
                    .ThenBy(t => t.distance) // Then by distance
                    .Take(MAX_TACKLERS)
                    .ToList();
                
                // Attempt tackle for the selected players
                foreach (var (opponent, distance, _) in selectedTacklers)
                {
                    // More skilled player (by team assignment) has higher tackle chance
                    bool isDefender = opponent.PlayerId.Contains("_2") || opponent.PlayerId.Contains("_3") || 
                                     opponent.PlayerId.Contains("_4") || opponent.PlayerId.Contains("_5");
                    
                    double tackleChance = BASE_TACKLE_CHANCE;
                    
                    // Defenders have higher tackle success
                    if (isDefender)
                        tackleChance += 0.15;
                    
                    // Distance affects tackle chance
                    tackleChance *= (1.0 - (distance / TACKLE_DISTANCE));
                    
                    // Random chance for tackle success
                    if (_random.NextDouble() < tackleChance)
                    {
                        // Tackle succeeded
                        _logger.LogInformation("TACKLE! Player {DefenderId} tackles {AttackerId}", 
                            opponent.PlayerId, playerWithBall.PlayerId);
                        
                        // Add to the list of recent tackle attempts
                        _recentTackleAttempts.Add(new TackleAttempt { 
                            PlayerId = opponent.PlayerId, 
                            Time = currentTime 
                        });
                        
                        // Ball becomes loose
                        game.BallPossession = string.Empty;
                        
                        // Ball gets a random velocity from the tackle
                        double tackleVelX = (_random.NextDouble() - 0.5) * 0.03;
                        double tackleVelY = (_random.NextDouble() - 0.5) * 0.03;
                        game.Ball.VelocityX = tackleVelX;
                        game.Ball.VelocityY = tackleVelY;
                        
                        // Update ball position after tackle
                        game.Ball.Position.X = Math.Clamp(playerWithBall.Position.X + tackleVelX, 0, 1);
                        game.Ball.Position.Y = Math.Clamp(playerWithBall.Position.Y + tackleVelY, 0, 1);
                        
                        // Only one tackle can succeed at a time
                        break;
                    }
                    else
                    {
                        // Failed tackle attempt - still add to recent attempts to prevent repeated attempts
                        // by the same player in quick succession
                        _recentTackleAttempts.Add(new TackleAttempt { 
                            PlayerId = opponent.PlayerId, 
                            Time = currentTime 
                        });
                    }
                }

                // Check if the ball went into a goal (in case the tackle sent it into the goal)
                CheckForGoal(game, ref publishEvent, ref gameEvent);
                
                // If no tackle occurred and we still have possession, attempt to pass or shoot
                if (!string.IsNullOrEmpty(game.BallPossession) && game.BallPossession == playerWithBall.PlayerId)
                {
                    // Get the team with possession
                    Team team = ballPossessorIsTeamA ? game.HomeTeam : game.AwayTeam;
                    
                    // Decide what this player should do with the ball
                    // If close to goal, shoot
                    double goalPostX = ballPossessorIsTeamA ? GOAL_POST_X_TEAM_B : GOAL_POST_X_TEAM_A;
                    double distanceToGoal = Math.Abs(playerWithBall.Position.X - goalPostX);
                    
                    if (distanceToGoal < SHOOTING_DISTANCE)
                    {
                        // Try to score
                        if (_random.NextDouble() < 0.4) // 40% chance to score when shooting
                        {
                            // Check if we're already in a goal scored state or if a goal was recently scored
                            if (game.Status != GameStatus.GoalScored && !_recentGoalScored)
                            {
                                // Goal!
                                string scoringTeamId = ballPossessorIsTeamA ? "TeamA" : "TeamB";
                                if (ballPossessorIsTeamA)
                                {
                                    game.HomeTeam.Score++;
                                }
                                else
                                {
                                    game.AwayTeam.Score++;
                                }

                                _logger.LogInformation("GOAL!!! Team {ScoringTeam} scored. Score: {HomeScore}-{AwayScore}", 
                                    scoringTeamId, game.HomeTeam.Score, game.AwayTeam.Score);

                                // Set goal cooldown to prevent duplicate goals
                                _recentGoalScored = true;
                                _lastGoalTime = currentTime;

                                // Set ball position to the goal
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
                                    PlayerId = scorerPlayerId,
                                    Position = new Position { X = goalPostX, Y = 0.5 }
                                };
                            }
                            else
                            {
                                _logger.LogWarning("Goal attempt blocked by cooldown or game already in GoalScored state");
                            }
                            
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
                                TeamId = ballPossessorIsTeamA ? "TeamA" : "TeamB",
                                PlayerId = TryParsePlayerId(playerWithBall.PlayerId),
                                Position = playerWithBall.Position
                            };
                            
                            // Loose ball after shot
                            game.BallPossession = string.Empty;
                            // Add some velocity to the ball
                            double shotVelX = (_random.NextDouble() - 0.3) * 0.05;
                            double shotVelY = (_random.NextDouble() - 0.5) * 0.05;
                            game.Ball.VelocityX = shotVelX;
                            game.Ball.VelocityY = shotVelY;
                            
                            // Update ball position
                            game.Ball.Position.X = Math.Clamp(playerWithBall.Position.X + shotVelX, 0, 1);
                            game.Ball.Position.Y = Math.Clamp(playerWithBall.Position.Y + shotVelY, 0, 1);
                            
                            return;
                        }
                    }
                    
                    // Not close to goal, try to pass to teammates
                    // Base pass probability now increased from 5% to 15% for more frequent passing
                    double passProbability = 0.15; 
                    
                    // Get player number to identify role
                    int playerNumber = possessorNumber; // already parsed earlier
                    bool isDefender = playerNumber >= 2 && playerNumber <= 5;
                    bool isMidfielder = playerNumber >= 6 && playerNumber <= 8;
                    bool isForward = playerNumber >= 9;
                    
                    // Check if player is in their own defensive third
                    bool inDefensiveThird = (ballPossessorIsTeamA && playerWithBall.Position.X < 0.3) || 
                                           (!ballPossessorIsTeamA && playerWithBall.Position.X > 0.7);
                    
                    // Check if player is in attacking third
                    bool inAttackingThird = (ballPossessorIsTeamA && playerWithBall.Position.X > 0.7) || 
                                           (!ballPossessorIsTeamA && playerWithBall.Position.X < 0.3);
                    
                    // Adjust pass probability based on role and field position
                    if (isDefender && inDefensiveThird)
                    {
                        passProbability = 0.35; // 35% chance to pass when defender is in own third
                        _logger.LogDebug("Defender in defensive third - increased pass probability to 35%");
                    }
                    else if (isMidfielder)
                    {
                        passProbability = 0.22; // Midfielders pass more frequently in general
                        _logger.LogDebug("Midfielder - base pass probability 22%");
                    }
                    else if (isForward && inAttackingThird)
                    {
                        passProbability = 0.18; // Forwards in attacking third slightly more likely to shoot than pass
                        _logger.LogDebug("Forward in attacking third - pass probability 18%");
                    }
                    else if (isForward && !inAttackingThird)
                    {
                        passProbability = 0.28; // Forwards outside attacking third more likely to pass back
                        _logger.LogDebug("Forward outside attacking third - pass probability 28%");
                    }
                    
                    // Increase pass probability if there are opponents nearby (under pressure)
                    if (potentialTacklers.Count >= 1)
                    {
                        passProbability += 0.15; // +15% pass probability when under pressure
                        _logger.LogDebug("Player under pressure - pass probability increased by 15%");
                    }
                    
                    // Only attempt passes occasionally to prevent constant passing
                    if (_random.NextDouble() < passProbability)
                    {
                        List<Player> teammates = team.Players.Where(p => p.PlayerId != playerWithBall.PlayerId).ToList();
                        
                        // Find teammates in good position to receive a pass
                        var validPassTargets = teammates.Where(p => {
                            double distance = Math.Sqrt(
                                Math.Pow(p.Position.X - playerWithBall.Position.X, 2) + 
                                Math.Pow(p.Position.Y - playerWithBall.Position.Y, 2));
                            
                            // Is teammate within passing distance?
                            if (distance > PASSING_DISTANCE)
                                return false;
                            
                            // Check the receiver's role
                            int receiverNumber = 0;
                            if (int.TryParse(p.PlayerId.Split('_')[1], out receiverNumber))
                            {
                                receiverNumber += 1;
                                bool receiverIsForward = receiverNumber >= 9;
                                bool possessorIsMidfielder = playerNumber >= 6 && playerNumber <= 8;
                                
                                // Special case: Midfielders can pass to forwards more freely
                                if (possessorIsMidfielder && receiverIsForward) {
                                    // Allow midfielders to pass to forwards as long as they're not too far behind
                                    double xDiff = p.Position.X - playerWithBall.Position.X;
                                    if ((ballPossessorIsTeamA && xDiff > -0.1) || (!ballPossessorIsTeamA && xDiff < 0.1)) {
                                        return true;
                                    }
                                }
                            }
                            
                            // Regular directional passing rules
                            // For Team A, prioritize teammates further up the field (higher X)
                            if (ballPossessorIsTeamA && p.Position.X <= playerWithBall.Position.X)
                                return false;
                            
                            // For Team B, prioritize teammates further down the field (lower X)
                            if (!ballPossessorIsTeamA && p.Position.X >= playerWithBall.Position.X)
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
                                TeamId = ballPossessorIsTeamA ? "TeamA" : "TeamB",
                                PlayerId = TryParsePlayerId(playerWithBall.PlayerId),
                                Position = playerWithBall.Position
                            };
                            
                            _logger.LogInformation("Pass from {PasserId} to {ReceiverId}", 
                                playerWithBall.PlayerId, targetPlayer.PlayerId);
                        }
                        // If no valid targets, occasionally lose possession
                        else if (_random.NextDouble() < 0.2) // 20% chance to lose possession if no valid targets
                        {
                            // Ball becomes loose
                            game.BallPossession = string.Empty;
                            
                            // If in defensive third, make the ball more likely to move forward
                            double loseVelX;
                            if (inDefensiveThird)
                            {
                                // For Team A (left to right), give the ball velocity to the right (positive X)
                                // For Team B (right to left), give the ball velocity to the left (negative X)
                                loseVelX = ballPossessorIsTeamA ? 
                                    (_random.NextDouble() * 0.05) + 0.02 : // Forward for Team A
                                    -(_random.NextDouble() * 0.05) - 0.02; // Forward for Team B
                                
                                _logger.LogDebug("Ball cleared from defensive third");
                            }
                            else
                            {
                                // Normal random velocity
                                loseVelX = (_random.NextDouble() - 0.5) * 0.04;
                            }
                            
                            double loseVelY = (_random.NextDouble() - 0.5) * 0.04;
                            game.Ball.VelocityX = loseVelX;
                            game.Ball.VelocityY = loseVelY;
                            
                            // Update ball position
                            game.Ball.Position.X = Math.Clamp(playerWithBall.Position.X + loseVelX, 0, 1);
                            game.Ball.Position.Y = Math.Clamp(playerWithBall.Position.Y + loseVelY, 0, 1);
                            
                            // Publish event
                            publishEvent = true;
                            gameEvent = new GameEvent
                            {
                                GameId = game.GameId,
                                EventType = GameEventType.PossessionLost,
                                TeamId = ballPossessorIsTeamA ? "TeamA" : "TeamB",
                                PlayerId = TryParsePlayerId(playerWithBall.PlayerId),
                                Position = playerWithBall.Position
                            };
                            
                            _logger.LogInformation("Player {PlayerId} lost possession", playerWithBall.PlayerId);
                        }
                    }
                }
            }
        }

        // Class to track tackle attempts
        private class TackleAttempt
        {
            public string PlayerId { get; set; } = string.Empty;
            public DateTime Time { get; set; }
        }

        // List to track recent tackle attempts
        // private readonly List<TackleAttempt> _recentTackleAttempts = new();
        
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

        // Helper method to reset player positions based on current formation
        private void ResetPlayerPositions(FootballCommentary.Core.Models.GameState gameState, bool isTeamA)
        {
            try
            {
                var gameId = gameState.GameId;
                TeamFormationData formationData = null;
                
                // Get current formation data for the team
                if (isTeamA)
                {
                    if (!_state.State.TeamAFormations.TryGetValue(gameId, out formationData))
                    {
                        _logger.LogWarning("No formation data found for Team A in game {GameId}, using default", gameId);
                        formationData = new TeamFormationData { Formation = TeamFormation.Formation_4_4_2 };
                    }
                }
                else
                {
                    if (!_state.State.TeamBFormations.TryGetValue(gameId, out formationData))
                    {
                        _logger.LogWarning("No formation data found for Team B in game {GameId}, using default", gameId);
                        formationData = new TeamFormationData { Formation = TeamFormation.Formation_4_4_2 };
                    }
                }
                
                // Get players list for the team
                var players = isTeamA ? gameState.HomeTeam.Players : gameState.AwayTeam.Players;
                if (players == null || !players.Any())
                {
                    _logger.LogWarning("No players found for {Team} in game {GameId}", isTeamA ? "Team A" : "Team B", gameId);
                    return;
                }
                
                // Define team-specific zones to ensure teams are on correct sides
                double defenseX, midfieldX, attackX, goalieX;
                
                if (isTeamA) {
                    // Team A (home) positioned on left side
                    goalieX = 0.05;     // Goalkeeper
                    defenseX = 0.2;     // Defenders around 20% from left edge
                    midfieldX = 0.35;   // Midfielders around 35% from left edge
                    attackX = 0.45;     // Forwards ~45% (slightly behind center line)
                } else {
                    // Team B (away) positioned on right side
                    goalieX = 0.95;     // Goalkeeper
                    defenseX = 0.8;     // Defenders around 20% from right edge
                    midfieldX = 0.65;   // Midfielders around 35% from right edge
                    attackX = 0.55;     // Forwards ~55% (slightly ahead of center line)
                }
                
                // Reset each player's position based on current formation
                foreach (var player in players)
                {
                    if (int.TryParse(player.PlayerId.Split('_')[1], out int playerIndex))
                    {
                        int playerNumber = playerIndex + 1; // Convert to 1-based player number
                        
                        // Determine player's position based on formation and role
                        if (playerNumber == 1) // Goalkeeper
                        {
                            player.Position = new Position { X = goalieX, Y = 0.5 };
                        }
                        else
                        {
                            // Use formation-appropriate positioning
                            var position = CalculatePositionForFormation(playerNumber, isTeamA, formationData.Formation);
                            player.Position = position;
                            
                            // Add a small random variation to prevent players from stacking
                            player.Position.X += (_random.NextDouble() - 0.5) * 0.02;
                            player.Position.Y += (_random.NextDouble() - 0.5) * 0.02;
                            
                            // Ensure player stays in appropriate team half
                            if (isTeamA) {
                                player.Position.X = Math.Min(player.Position.X, 0.49); // Keep Team A in left half
                            } else {
                                player.Position.X = Math.Max(player.Position.X, 0.51); // Keep Team B in right half
                            }
                            
                            // Ensure player stays in bounds
                            player.Position.X = Math.Clamp(player.Position.X, 0.05, 0.95);
                            player.Position.Y = Math.Clamp(player.Position.Y, 0.05, 0.95);
                        }
                    }
                }
                
                _logger.LogInformation("Reset player positions for {Team} using {Formation} formation", 
                    isTeamA ? gameState.HomeTeam.Name : gameState.AwayTeam.Name, formationData.Formation);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resetting player positions for {Team}", isTeamA ? "Team A" : "Team B");
            }
        }

        private void CheckForGoal(FootballCommentary.Core.Models.GameState game, ref bool publishEvent, ref GameEvent? gameEvent)
        {
            // Do not check for goals if already celebrating a goal - prevents double counting
            if (game.Status == GameStatus.GoalScored)
            {
                return;
            }
            
            // Additional check for recent goal cooldown
            if (_recentGoalScored)
            {
                _logger.LogDebug("Goal check skipped due to recent goal cooldown");
                return;
            }

            const double GOAL_X_MIN = 0.45;
            const double GOAL_X_MAX = 0.55;
            
            // Check if ball is in team A's goal
            if (game.Ball.Position.X <= 0.02 && 
                game.Ball.Position.Y >= GOAL_X_MIN && 
                game.Ball.Position.Y <= GOAL_X_MAX)
            {
                // Team B scored
                game.AwayTeam.Score++;
                string scoringTeam = "TeamB";
                
                _logger.LogInformation("GOAL!!! Team {ScoringTeam} scored. Score: {HomeScore}-{AwayScore}", 
                    scoringTeam, game.HomeTeam.Score, game.AwayTeam.Score);
                
                // Set goal cooldown to prevent duplicate goals
                _recentGoalScored = true;
                _lastGoalTime = DateTime.UtcNow;
                
                // Set ball position to the goal
                game.Ball.Position = new Position { X = 0.02, Y = 0.5 };
                game.BallPossession = string.Empty; // No one possesses during celebration
                game.Ball.VelocityX = 0;
                game.Ball.VelocityY = 0;
                game.Status = GameStatus.GoalScored; // Enter goal scored state
                game.LastScoringTeamId = scoringTeam; // Remember who scored for reset
                
                // Publish goal event
                publishEvent = true;
                if (gameEvent == null)
                {
                    // Find closest Team B player as likely scorer
                    var potentialScorers = game.AwayTeam.Players
                        .OrderBy(p => 
                            Math.Sqrt(
                                Math.Pow(p.Position.X - game.Ball.Position.X, 2) + 
                                Math.Pow(p.Position.Y - game.Ball.Position.Y, 2)
                            ))
                        .Take(1)
                        .FirstOrDefault();
                        
                    int? scorerId = potentialScorers != null ? 
                        TryParsePlayerId(potentialScorers.PlayerId) : null;
                        
                    gameEvent = new GameEvent
                    {
                        GameId = game.GameId,
                        EventType = GameEventType.Goal,
                        TeamId = scoringTeam,
                        PlayerId = scorerId,
                        Position = new Position { X = 0.02, Y = 0.5 }
                    };
                }
            }
            // Check if ball is in team B's goal
            else if (game.Ball.Position.X >= 0.98 && 
                     game.Ball.Position.Y >= GOAL_X_MIN && 
                     game.Ball.Position.Y <= GOAL_X_MAX)
            {
                // Team A scored
                game.HomeTeam.Score++;
                string scoringTeam = "TeamA";
                
                _logger.LogInformation("GOAL!!! Team {ScoringTeam} scored. Score: {HomeScore}-{AwayScore}", 
                    scoringTeam, game.HomeTeam.Score, game.AwayTeam.Score);
                
                // Set goal cooldown to prevent duplicate goals
                _recentGoalScored = true;
                _lastGoalTime = DateTime.UtcNow;
                
                // Set ball position to the goal
                game.Ball.Position = new Position { X = 0.98, Y = 0.5 };
                game.BallPossession = string.Empty; // No one possesses during celebration
                game.Ball.VelocityX = 0;
                game.Ball.VelocityY = 0;
                game.Status = GameStatus.GoalScored; // Enter goal scored state
                game.LastScoringTeamId = scoringTeam; // Remember who scored for reset
                
                // Publish goal event
                publishEvent = true;
                if (gameEvent == null)
                {
                    // Find closest Team A player as likely scorer
                    var potentialScorers = game.HomeTeam.Players
                        .OrderBy(p => 
                            Math.Sqrt(
                                Math.Pow(p.Position.X - game.Ball.Position.X, 2) + 
                                Math.Pow(p.Position.Y - game.Ball.Position.Y, 2)
                            ))
                        .Take(1)
                        .FirstOrDefault();
                        
                    int? scorerId = potentialScorers != null ? 
                        TryParsePlayerId(potentialScorers.PlayerId) : null;
                        
                    gameEvent = new GameEvent
                    {
                        GameId = game.GameId,
                        EventType = GameEventType.Goal,
                        TeamId = scoringTeam,
                        PlayerId = scorerId,
                        Position = new Position { X = 0.98, Y = 0.5 }
                    };
                }
            }
        }

        // New method to handle movement of ball possessor
        private void MoveBallPossessor(FootballCommentary.Core.Models.GameState game, Player player)
        {
            bool isTeamA = player.PlayerId.StartsWith("TeamA");
            double dx = 0, dy = 0;
            
            // Forward direction depends on team (Team A moves right, Team B moves left)
            double forwardDirection = isTeamA ? 1 : -1;
            
            // Get player role
            int playerNumber = TryParsePlayerId(player.PlayerId) ?? 0;
            playerNumber += 1; // Convert to 1-based player number
            
            bool isGoalkeeper = playerNumber == 1;
            bool isDefender = playerNumber >= 2 && playerNumber <= 5;
            bool isMidfielder = playerNumber >= 6 && playerNumber <= 8;
            bool isForward = playerNumber >= 9;
            
            // Higher aggression for offensive positions
            double forwardBias = PLAYER_SPEED * 0.8; // Base forward movement
            
            if (isDefender) forwardBias *= 0.7; // Defenders more cautious
            if (isMidfielder) forwardBias *= 1.0; // Midfielders standard
            if (isForward) forwardBias *= 1.3; // Forwards more aggressive
            
            // Add forward bias
            dx += forwardDirection * forwardBias;
            
            // Check if path ahead is blocked by opponents
            var opposingTeamPlayers = isTeamA ? game.AwayTeam.Players : game.HomeTeam.Players;
            bool pathBlocked = false;
            
            // Look ahead in the player's forward direction
            double lookAheadDistance = 0.12; // Distance to check for opponents
            
            // Calculate forward position to check for obstacles
            double checkX = player.Position.X + forwardDirection * lookAheadDistance;
            
            // If at edge of field, don't move forward anymore
            if ((isTeamA && checkX > 0.95) || (!isTeamA && checkX < 0.05))
            {
                pathBlocked = true;
            }
            
            // Check for nearby opponents in path
            foreach (var opponent in opposingTeamPlayers)
            {
                double distX = Math.Abs(opponent.Position.X - checkX);
                double distY = Math.Abs(opponent.Position.Y - player.Position.Y);
                
                // If opponent is ahead and close, path is blocked
                if (distX < 0.08 && distY < 0.1)
                {
                    pathBlocked = true;
                    break;
                }
            }
            
            // Check if approaching goal
            double goalPostX = isTeamA ? GOAL_POST_X_TEAM_B : GOAL_POST_X_TEAM_A;
            double distanceToGoal = Math.Abs(player.Position.X - goalPostX);
            bool inShootingZone = false;
            
            // Determine if player is in a good position to shoot
            if (isTeamA) 
            {
                // For Team A, good shooting positions are on the right side of the field
                inShootingZone = player.Position.X > 0.8 && 
                                 player.Position.Y > GOAL_X_MIN - 0.1 && 
                                 player.Position.Y < GOAL_X_MAX + 0.1;
            }
            else 
            {
                // For Team B, good shooting positions are on the left side of the field
                inShootingZone = player.Position.X < 0.2 && 
                                 player.Position.Y > GOAL_X_MIN - 0.1 && 
                                 player.Position.Y < GOAL_X_MAX + 0.1;
            }
            
            // If close to goal (shooting range)
            if (distanceToGoal < SHOOTING_DISTANCE || inShootingZone)
            {
                // Aim more toward the goal Y-center
                double goalCenterY = 0.5;
                dy += (goalCenterY - player.Position.Y) * PLAYER_SPEED * 2.0;
                
                // Extra forward bias when in shooting zone
                if (inShootingZone)
                {
                    dx += forwardDirection * PLAYER_SPEED * 1.5;
                }
            }
            else if (pathBlocked)
            {
                // If path is blocked, try to move sideways to find space
                // Add some sideways movement to avoid the obstacle
                double sideMovement = PLAYER_SPEED * 0.6;
                
                // Use perlin-like noise based on position and time for more natural movement
                double noiseInput = game.SimulationStep * 0.1 + player.Position.X * 10 + player.Position.Y * 10;
                double noiseValue = Math.Sin(noiseInput) * Math.Cos(noiseInput * 0.7);
                
                // Use the noise to determine direction (+/-)
                dy += sideMovement * Math.Sign(noiseValue);
                
                // Reduce forward movement when blocked
                dx *= 0.3;
            }
            
            // Add subtle variance to movement
            dx += (_random.NextDouble() - 0.5) * PLAYER_SPEED * 0.2;
            dy += (_random.NextDouble() - 0.5) * PLAYER_SPEED * 0.3;
            
            // Apply movement with boundary checking
            player.Position.X = Math.Clamp(player.Position.X + dx, 0, 1);
            player.Position.Y = Math.Clamp(player.Position.Y + dy, 0, 1);
            
            // Update ball position to follow the player
            game.Ball.Position = player.Position;
            game.Ball.VelocityX = 0;
            game.Ball.VelocityY = 0;
            
            _logger.LogDebug("Moved ball possessor {PlayerId}: dx={dx}, dy={dy}, pathBlocked={PathBlocked}, inShootingZone={InShootingZone}", 
                player.PlayerId, dx, dy, pathBlocked, inShootingZone);
        }

        // Modified to track if a goal has been scored recently
        private bool _recentGoalScored = false;
        private DateTime _lastGoalTime = DateTime.MinValue;
        private const double GOAL_COOLDOWN_SECONDS = 1.0; // Don't count another goal for 1 second
    }
}