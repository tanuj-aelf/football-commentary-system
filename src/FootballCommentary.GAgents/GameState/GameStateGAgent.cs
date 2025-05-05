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
using FootballCommentary.GAgents.PlayerAgents;

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
        private readonly PlayerAgentManager _playerAgentManager;
        private IStreamProvider? _streamProvider;
        private Dictionary<string, IDisposable> _gameSimulationTimers = new Dictionary<string, IDisposable>();
        
        // Game simulation constants
        private const double FIELD_WIDTH = 1.0;
        private const double FIELD_HEIGHT = 1.0;
        private const double GOAL_WIDTH = 0.25; // Increased from 0.2 for wider goals
        private const double PLAYER_SPEED = 0.035; // Increased from 0.03 for even faster player movement
        private const double BALL_SPEED = 0.06; // Increased from 0.05 for faster ball movement
        private const double GOAL_POST_X_TEAM_A = 0.05;
        private const double GOAL_POST_X_TEAM_B = 0.95;
        private const double PASSING_DISTANCE = 0.4; // Increased from 0.35 for longer passes
        private const double SHOOTING_DISTANCE = 0.4; // Increased from 0.3 to allow shots from much further out
        private const double POSITION_RECOVERY_WEIGHT = 0.005; // Reduced from 0.008 for more free movement
        private const double BALL_ATTRACTION_WEIGHT = 0.07; // Increased from 0.06 to make players more attracted to ball
        private const double PLAYER_ROLE_ADHERENCE = 0.01; // Reduced from 0.015 to allow players to break position more
        private const double FORMATION_ADHERENCE = 0.005; // Reduced from 0.008 for more dynamic movement
        
        // Field zones for more realistic positioning
        private const double DEFENSE_ZONE = 0.3;
        private const double MIDFIELD_ZONE = 0.6;
        private const double ATTACK_ZONE = 0.7;
        private const double TEAMMATE_AVOIDANCE_DISTANCE = 0.08;
        private const double OPPONENT_AWARENESS_DISTANCE = 0.15;
        
        // List to track recent tackle attempts
        private readonly List<TackleAttempt> _recentTackleAttempts = new();
        
        // Tackle and ball control constants
        private const double PLAYER_CONTROL_DISTANCE = 0.07; // Increased from 0.06
        private const double TACKLE_DISTANCE = 0.12; // Increased from 0.1 for more tackles
        private const double BASE_TACKLE_CHANCE = 0.15; // Reduced from 0.2 to make tackles even less successful
        
        // Add a new constant for loose ball attraction
        private const double LOOSE_BALL_ATTRACTION = 0.12; // Increased from 0.09 - players even more attracted to loose balls
        
        // These constants need to be defined at class level
        private const double GOAL_X_MIN = 0.45;
        private const double GOAL_X_MAX = 0.55;
        
        public GameStateGAgent(
            ILogger<GameStateGAgent> logger,
            [PersistentState("gameState", "Default")] IPersistentState<GameStateGAgentState> state,
            ILLMService llmService,
            ILoggerFactory loggerFactory)
        {
            _logger = logger;
            _state = state;
            _llmService = llmService;
            
            // Create loggers using logger factory
            var llmTacticianLogger = loggerFactory.CreateLogger<LLMTactician>();
            var playerAgentLogger = loggerFactory.CreateLogger<PlayerAgent>();
            var playerAgentManagerLogger = loggerFactory.CreateLogger<PlayerAgentManager>();
            var requestManagerLogger = loggerFactory.CreateLogger<ParallelLLMRequestManager>();
            
            // Initialize LLM tactician
            _llmTactician = new LLMTactician(llmTacticianLogger, llmService);
            
            // Initialize player agent manager
            _playerAgentManager = new PlayerAgentManager(
                playerAgentManagerLogger,
                playerAgentLogger,
                requestManagerLogger,
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
            
            // Initialize player agents
            _playerAgentManager.InitializePlayerAgents(gameState);
            
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
            
            // Make sure player agents are initialized
            if (!_playerAgentManager.HasPlayerAgent(game.HomeTeam.Players[0].PlayerId))
            {
                _logger.LogInformation("Initializing player agents at game start for {GameId}", gameId);
                _playerAgentManager.InitializePlayerAgents(game);
            }
            
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
            
            // Reduce update interval further to 30ms
            var timerOptions = new GrainTimerCreationOptions
            {
                DueTime = TimeSpan.FromMilliseconds(30), // Changed from 50ms
                Period = TimeSpan.FromMilliseconds(30), // Changed from 50ms
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
                
                // Publish state update every step instead of every 2 steps
                if (game.Status != GameStatus.NotStarted) 
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
                
                // Check if it's time to update formations (every 30 seconds of game time)
                if (game.SimulationStep % 300 == 0) // Every ~30 seconds
                {
                    await UpdateTeamFormations(game);
                }
                
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
            try
            {
                // Check if we should use the new agent-based approach
                // Get player movement decisions from the player agent manager
                var playerMovements = await _playerAgentManager.GetPlayerMovementsAsync(game);
                
                // Process special case for goalkeeper separately
                var homeTeamPlayers = game.HomeTeam.Players;
                var awayTeamPlayers = game.AwayTeam.Players;
                var allPlayers = homeTeamPlayers.Concat(awayTeamPlayers).ToList();
                
                // Find the player with the ball
                Player? playerWithBall = null;
                if (!string.IsNullOrEmpty(game.BallPossession))
                {
                    playerWithBall = allPlayers.FirstOrDefault(p => p.PlayerId == game.BallPossession);
                }
                
                // Process movements for all players
                foreach (var player in allPlayers)
                {
                    // Special case for goalkeepers
                    bool isGoalkeeper = player.PlayerId.EndsWith("_0"); // ID format: TeamX_0 for goalkeepers
                    if (isGoalkeeper)
                    {
                        MoveGoalkeeper(game, player, playerWithBall);
                        continue;
                    }
                    
                    // Special handling for player with ball
                    if (player.PlayerId == game.BallPossession)
                    {
                        // If we have an agent-based movement for the ball possessor, use that with some adjustments
                        if (playerMovements.TryGetValue(player.PlayerId, out var movement))
                        {
                            // Apply the agent's movement decision with some gameplay adjustments
                            double dx = movement.dx;
                            double dy = movement.dy;
                            
                            // Apply movement with boundary checking
                            player.Position.X = Math.Clamp(player.Position.X + dx, 0, 1);
                            player.Position.Y = Math.Clamp(player.Position.Y + dy, 0, 1);
                            
                            // Update ball position to follow the player
                            game.Ball.Position = player.Position;
                            game.Ball.VelocityX = 0;
                            game.Ball.VelocityY = 0;
                        }
                        else
                        {
                            // Fall back to default ball possessor movement
                            MoveBallPossessor(game, player);
                        }
                        continue;
                    }
                    
                    // Apply agent-based movement or fall back to rule-based
                    if (playerMovements.TryGetValue(player.PlayerId, out var agentMovement))
                    {
                        _logger.LogDebug("Applying agent-based movement for player {PlayerId}: ({DX},{DY})", 
                            player.PlayerId, agentMovement.dx, agentMovement.dy);
                            
                        // Apply the movement with some additional game dynamics
                        double dx = agentMovement.dx;
                        double dy = agentMovement.dy;
                        
                        // Apply teammate avoidance to prevent crowding
                        AvoidTeammates(game, player, ref dx, ref dy, player.PlayerId.StartsWith("TeamA"));
                        
                        // Apply the finalized movement with boundary checking
                        player.Position.X = Math.Clamp(player.Position.X + dx, 0, 1);
                        player.Position.Y = Math.Clamp(player.Position.Y + dy, 0, 1);
                    }
                    else
                    {
                        // Fall back to rule-based behavior if no agent movement available
                        bool isTeamA = player.PlayerId.StartsWith("TeamA");
                        bool isTeamInPossession = !string.IsNullOrEmpty(game.BallPossession) && 
                            game.BallPossession.StartsWith(isTeamA ? "TeamA" : "TeamB");
                            
                        int? playerNumber = TryParsePlayerId(player.PlayerId);
                        if (!playerNumber.HasValue) continue;
                            
                        // Get player's base position from formation
                        Position basePosition = GetBasePositionForRole(playerNumber.Value + 1, isTeamA, game.GameId);
                        
                        // Apply rule-based movement based on possession
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
                
                _logger.LogDebug("Moved {Count} players using agent-based approach", playerMovements.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in MoveAllPlayers: {Message}", ex.Message);
            }
        }
        
        private void MoveGoalkeeper(FootballCommentary.Core.Models.GameState game, Player goalkeeper, Player? playerWithBall)
        {
            bool isTeamA = goalkeeper.PlayerId.StartsWith("TeamA");
            double dx = 0, dy = 0;
            
            // Get goal position
            double goalPostX = isTeamA ? GOAL_POST_X_TEAM_A : GOAL_POST_X_TEAM_B;
            
            // Get ball position or center of field if no ball
            Position ballPos = game.Ball?.Position ?? new Position { X = 0.5, Y = 0.5 };
            
            // Define the maximum distance goalkeeper can stray from goal
            double maxDistance = 0.18; // Increased from 0.15 to make goalkeepers wander more
            
            // Determine if ball is in goalkeeper's half
            bool ballInOwnHalf = (isTeamA && ballPos.X < 0.5) || (!isTeamA && ballPos.X > 0.5);
            
            if (ballInOwnHalf)
            {
                // Ball is in goalkeeper's half - be more aggressive
                double targetX = isTeamA ? 
                    Math.Min(goalPostX + 0.15, ballPos.X - 0.1) : // Increased from 0.12 - move further from goal
                    Math.Max(goalPostX - 0.15, ballPos.X + 0.1);  // Increased from 0.12 - move further from goal
                    
                // Move toward the target X
                dx = (targetX - goalkeeper.Position.X) * 0.12; // Reduced from typical 0.15 for slower goalkeeper reactions
            }
            else
            {
                // Ball is in opponent's half - stay close to goal line
                double targetX = isTeamA ? goalPostX + 0.06 : goalPostX - 0.06; // Increased from 0.05 - stay further from goal line
                
                // Move toward the target X
                dx = (targetX - goalkeeper.Position.X) * 0.08; // Reduced from 0.1 for even slower goalkeeper positioning
            }
            
            // Y movement - follow ball's Y position with significant lag
            double targetY = ballPos.Y;
            
            // Add more randomization to goalkeeper positioning - increased randomness 
            targetY += (_random.NextDouble() - 0.5) * 0.12; // Increased from 0.08 for less perfect positioning
            
            // Move toward target Y
            dy = (targetY - goalkeeper.Position.Y) * 0.12; // Reduced from 0.15 for slower reactions
            
            // Apply maximum movement speeds
            dx = Math.Clamp(dx, -PLAYER_SPEED * 0.7, PLAYER_SPEED * 0.7); // Slower than typical players
            dy = Math.Clamp(dy, -PLAYER_SPEED * 0.7, PLAYER_SPEED * 0.7); // Slower than typical players
            
            // Constrain goalkeeper to stay near goal
            goalkeeper.Position.X = Math.Clamp(goalkeeper.Position.X + dx, 
                isTeamA ? goalPostX : goalPostX - maxDistance, 
                isTeamA ? goalPostX + maxDistance : goalPostX);
            goalkeeper.Position.Y = Math.Clamp(goalkeeper.Position.Y + dy, 0.25, 0.75); // Wider range (was 0.3-0.7)
            
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
            
            // Reduced diving chance from 0.25 to 0.2 for less perfect saves
            if (ballMovingTowardGoal && distanceToBall < 0.1 && _random.NextDouble() < 0.2)
            {
                // Calculate direction to the ball for diving
                double diveDx = ballPos.X - goalkeeper.Position.X;
                double diveDy = ballPos.Y - goalkeeper.Position.Y;
                double diveDist = Math.Sqrt(diveDx * diveDx + diveDy * diveDy);
                
                if (diveDist > 0)
                {
                    // Normalize and apply a dive in the ball's direction
                    goalkeeper.Position.X += (diveDx / diveDist) * PLAYER_SPEED * 1.8; // Reduced from 2.0 
                    goalkeeper.Position.Y += (diveDy / diveDist) * PLAYER_SPEED * 1.8; // Reduced from 2.0
                    
                    // Check if goalkeeper catches the ball
                    distanceToBall = Math.Sqrt(
                        Math.Pow(goalkeeper.Position.X - ballPos.X, 2) + 
                        Math.Pow(goalkeeper.Position.Y - ballPos.Y, 2));
                    
                    // Reduced catch chance from 0.4 to 0.25 for more goals
                    if (distanceToBall < 0.05 && _random.NextDouble() < 0.25)
                    {
                        // Goalkeeper catches the ball
                        game.BallPossession = goalkeeper.PlayerId;
                        game.Ball.VelocityX = 0;
                        game.Ball.VelocityY = 0;
                        game.Ball.Position = goalkeeper.Position;
                        
                        _logger.LogInformation("Goalkeeper {GoalkeeperId} saves the shot!", goalkeeper.PlayerId);
                    }
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
            if (player.PlayerId == game.BallPossession)
            {
                // This player has the ball - logic handled in MoveBallPossessor
                return;
            }
            
            // Get player number to determine role
            int? playerNumberNullable = TryParsePlayerId(player.PlayerId);
            if (!playerNumberNullable.HasValue)
            {
                return;
            }
            
            int playerNumber = playerNumberNullable.Value + 1; // Convert to 1-based numbers
            bool isGoalkeeper = playerNumber == 1;
            bool isDefender = playerNumber >= 2 && playerNumber <= 5;
            bool isMidfielder = playerNumber >= 6 && playerNumber <= 9;
            bool isForward = playerNumber >= 10;
            
            if (isGoalkeeper)
            {
                MoveGoalkeeper(game, player, playerWithBall);
                return;
            }
            
            // Determine position on field
            double forwardDirection = isTeamA ? 1 : -1;
            double playerX = player.Position.X;
            bool inDefensiveHalf = (isTeamA && playerX < 0.5) || (!isTeamA && playerX > 0.5);
            bool inOffensiveHalf = !inDefensiveHalf;
            bool inAttackingThird = (isTeamA && playerX > 0.7) || (!isTeamA && playerX < 0.3);
            
            // Variables for movement calculation
            double dx = 0;
            double dy = 0;
            
            // Get ball possessor information
            string possessorId = game.BallPossession ?? "";
            bool teamHasPossession = !string.IsNullOrEmpty(possessorId) && 
                                    (isTeamA && possessorId.StartsWith("TeamA")) || 
                                    (!isTeamA && possessorId.StartsWith("TeamB"));
            
            // If ball possessor exists and is from same team
            if (playerWithBall != null && teamHasPossession)
            {
                double distToBallPossessor = CalculateDistance(player.Position, playerWithBall.Position);
                
                // Movement behaviors based on player role and field position
                if (isDefender)
                {
                    if (inDefensiveHalf)
                    {
                        // Defenders staying back in defensive half
                        dx += forwardDirection * PLAYER_SPEED * 0.3;
                        
                        // Maintain spacing around base position
                        dx += (basePosition.X - player.Position.X) * POSITION_RECOVERY_WEIGHT * 2.0;
                        dy += (basePosition.Y - player.Position.Y) * POSITION_RECOVERY_WEIGHT * 1.5;
                    }
                    else
                    {
                        // Defenders move forward cautiously in offensive half
                        dx += forwardDirection * PLAYER_SPEED * 0.5;
                        
                        // Make supporting runs, but don't go too far forward
                        if (playerWithBall != null && distToBallPossessor > 0.2)
                        {
                            dx += (playerWithBall.Position.X - player.Position.X) * 0.02;
                            dy += (playerWithBall.Position.Y - player.Position.Y) * 0.03;
                        }
                    }
                }
                else if (isMidfielder)
                {
                    if (inDefensiveHalf)
                    {
                        // Midfielders move forward in defensive half to support attack
                        dx += forwardDirection * PLAYER_SPEED * 0.7; // Increased from 0.6
                        
                        // Get a bit closer to ball possessor for support
                        if (playerWithBall != null && distToBallPossessor > 0.2)
                        {
                            dx += (playerWithBall.Position.X - player.Position.X) * 0.03;
                            dy += (playerWithBall.Position.Y - player.Position.Y) * 0.03;
                        }
                    }
                    else
                    {
                        // Midfielders make more aggressive forward runs in offensive half
                        dx += forwardDirection * PLAYER_SPEED * 0.9; // Increased from 0.7
                        
                        // Make supporting runs, creating passing options
                        if (playerWithBall != null)
                        {
                            // Create passing lanes and move into space
                            // Make runs into goal-scoring positions
                            if (isTeamA)
                            {
                                // Team A: Try to get to the right side for attack
                                bool isAheadOfBall = player.Position.X > playerWithBall.Position.X;
                                if (isAheadOfBall)
                                {
                                    // If already ahead, make runs toward goal
                                    dx += PLAYER_SPEED * 0.5; // More aggressive forward movement
                                    
                                    // Move toward center or goal area
                                    if (player.Position.Y < 0.4 || player.Position.Y > 0.6)
                                    {
                                        // If on the wings, cut inside
                                        dy += (0.5 - player.Position.Y) * 0.04;
                                    }
                                }
                                else
                                {
                                    // If behind, try to get ahead for receiving passes
                                    dx += PLAYER_SPEED * 0.8; // More aggressive forward movement
                                }
                            }
                            else
                            {
                                // Team B: Try to get to the left side for attack
                                bool isAheadOfBall = player.Position.X < playerWithBall.Position.X;
                                if (isAheadOfBall)
                                {
                                    // If already ahead, make runs toward goal
                                    dx -= PLAYER_SPEED * 0.5; // More aggressive forward movement
                                    
                                    // Move toward center or goal area
                                    if (player.Position.Y < 0.4 || player.Position.Y > 0.6)
                                    {
                                        // If on the wings, cut inside
                                        dy += (0.5 - player.Position.Y) * 0.04;
                                    }
                                }
                                else
                                {
                                    // If behind, try to get ahead for receiving passes
                                    dx -= PLAYER_SPEED * 0.8; // More aggressive forward movement
                                }
                            }
                        }
                    }
                }
                else if (isForward)
                {
                    // Forwards make more aggressive runs
                    if (inOffensiveHalf)
                    {
                        // Forwards make aggressive runs in final third
                        dx += forwardDirection * PLAYER_SPEED * 1.2; // Increased from 0.9 - much more aggressive
                        
                        // If in the attacking third, make runs into the box
                        if (inAttackingThird)
                        {
                            // In attacking third, make runs toward goal
                            double goalY = 0.5; // Center of goal
                            
                            // Move toward a position conducive to scoring
                            double goalPostX = isTeamA ? 0.95 : 0.05; // Goal post position
                            
                            // Make more runs into the box
                            dx += (goalPostX - player.Position.X) * 0.06; // Increased from 0.04
                            dy += (goalY - player.Position.Y) * 0.05; // Increased from 0.03
                            
                            // Make runs behind defenders
                            if (_random.NextDouble() < 0.2) // 20% chance to make a run
                            {
                                // Make a run in a random direction
                                dy += (_random.NextDouble() - 0.5) * 0.1;
                            }
                        }
                        else
                        {
                            // Not in attacking third yet, move forward aggressively
                            if (playerWithBall != null)
                            {
                                // Get ahead of the ball to create passing options
                                if ((isTeamA && player.Position.X <= playerWithBall.Position.X) ||
                                    (!isTeamA && player.Position.X >= playerWithBall.Position.X))
                                {
                                    // Move forward to get ahead of the ball
                                    dx += forwardDirection * PLAYER_SPEED * 1.3; // Very aggressive forward movement
                                }
                                else
                                {
                                    // Already ahead, make runs and find space
                                    double lateralMovement = (_random.NextDouble() - 0.5) * 0.1;
                                    dy += lateralMovement;
                                }
                            }
                        }
                    }
                    else
                    {
                        // Forwards in defensive half should move forward quickly
                        dx += forwardDirection * PLAYER_SPEED * 1.1; // Aggressive movement back to offensive position
                        
                        // Also prioritize returning to base position in Y axis
                        dy += (basePosition.Y - player.Position.Y) * POSITION_RECOVERY_WEIGHT * 1.2;
                    }
                }
            }
            
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
                dx = (basePosition.X - player.Position.X) * (FORMATION_ADHERENCE * 1.2); // Reduced from 1.5
                dy = (basePosition.Y - player.Position.Y) * (FORMATION_ADHERENCE * 1.0); // Reduced from 1.2
                
                // Add more natural movement with increased randomness for defenders
                dx += naturalMovementX * 1.2; // Increased from 1.0
                dy += naturalMovementY * 1.2; // Increased from 1.0
                
                // Add additional random movement for defenders to make them less predictable
                dx += (_random.NextDouble() - 0.5) * 0.01;
                dy += (_random.NextDouble() - 0.5) * 0.01;
                
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
                        // Defensive pressure, but with reduced effectiveness
                        double pressureFactor = PLAYER_SPEED * 0.7; // Reduced from 0.8
                        dx += (targetPos.X - player.Position.X) * pressureFactor;
                        dy += (targetPos.Y - player.Position.Y) * pressureFactor;
                        
                        // Occasionally make defensive errors (10% chance)
                        if (_random.NextDouble() < 0.1) {
                            // Random movement instead of proper defending
                            double errorX = (_random.NextDouble() - 0.5) * 0.03;
                            double errorY = (_random.NextDouble() - 0.5) * 0.03;
                            
                            // Replace calculated movement with error movement
                            dx = errorX;
                            dy = errorY;
                        }
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

                // Check if the ball is out of bounds and handle it
                if (CheckForOutOfBounds(game, ref publishEvent, ref gameEvent))
                {
                    // If the ball went out of bounds, the method has already handled it
                    return;
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
                        // Calculate shot success probability based on distance to goal
                        // Closer shots have higher probability of success
                        double successBaseProbability = 0.6; // Increased from 0.4 to 0.6 for much higher scoring
                        
                        // Distance factor: shots from very close have higher probability
                        double distanceFactor = 1.0 - (distanceToGoal / SHOOTING_DISTANCE);
                        
                        // Get player number from the ID for role determination
                        int shooterPlayerNumber = TryParsePlayerId(playerWithBall.PlayerId) ?? 0;
                        shooterPlayerNumber++; // Convert to 1-based index
                        
                        // Player role factor: forwards are better at shooting
                        double roleFactor = 1.2; // Base factor increased from 1.0 to 1.2 for all players
                        bool shooterIsForward = shooterPlayerNumber >= 9;
                        bool shooterIsMidfielder = shooterPlayerNumber >= 6 && shooterPlayerNumber <= 8;
                        if (shooterIsForward) {
                            roleFactor = 1.5; // Increased from 1.4 - Forwards are 50% better at shooting
                        } else if (shooterIsMidfielder) {
                            roleFactor = 1.4; // Increased from 1.2 - Midfielders are 40% better at shooting
                        }
                        
                        // Calculate final shot success probability
                        double shotSuccessProbability = successBaseProbability * distanceFactor * roleFactor;
                        
                        // Cap at a realistic maximum
                        shotSuccessProbability = Math.Min(shotSuccessProbability, 0.9); // Increased from 0.8 for even more scoring
                        
                        // Try to score
                        if (_random.NextDouble() < shotSuccessProbability)
                        {
                            // Check if we're already in a goal scored state or if a goal was recently scored
                            if (game.Status != GameStatus.GoalScored && !_recentGoalScored)
                            {
                                // Don't score directly here - set the ball velocity to go towards the goal
                                // Let the ball physically enter the goal area through normal movement
                                double goalX = ballPossessorIsTeamA ? 0.98 : 0.02; // Goal X position
                                double goalY = 0.5; // Goal Y position (center of goal)
                                
                                // Add a small random variation to goalY to make shots diverse
                                goalY += (_random.NextDouble() - 0.5) * 0.15;
                                
                                // Calculate direction to goal
                                double dirX = goalX - playerWithBall.Position.X;
                                double dirY = goalY - playerWithBall.Position.Y;
                                double dist = Math.Sqrt(dirX * dirX + dirY * dirY);
                                
                                // Normalize direction and apply velocity
                                double shotSpeed = 0.04 + _random.NextDouble() * 0.01; // Slightly randomized speed
                                game.Ball.VelocityX = (dirX / dist) * shotSpeed;
                                game.Ball.VelocityY = (dirY / dist) * shotSpeed;
                                
                                // Track the player who took the shot for goal attribution
                                _lastShooterPlayerId = playerWithBall.PlayerId;
                                
                                // Release the ball
                                game.BallPossession = string.Empty;
                                
                                // Update ball position for initial movement
                                game.Ball.Position.X = playerWithBall.Position.X + game.Ball.VelocityX;
                                game.Ball.Position.Y = playerWithBall.Position.Y + game.Ball.VelocityY;
                                
                                // Create a shot event
                                publishEvent = true;
                                gameEvent = new GameEvent
                                {
                                    GameId = game.GameId,
                                    EventType = GameEventType.Shot,
                                    TeamId = ballPossessorIsTeamA ? "TeamA" : "TeamB",
                                    PlayerId = TryParsePlayerId(playerWithBall.PlayerId),
                                    Position = playerWithBall.Position
                                };
                                
                                return;
                            }
                            else
                            {
                                _logger.LogWarning("Goal attempt blocked by cooldown or game already in GoalScored state");
                            }
                            
                            return;
                        }
                        else
                        {
                            // Shot but missed or saved - same logic as before
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
                            
                            // Add some velocity to the ball - direct towards goal but with more randomness
                            double goalX = ballPossessorIsTeamA ? 0.98 : 0.02;
                            double goalY = 0.5 + (_random.NextDouble() - 0.5) * 0.3; // More random Y to represent a miss
                            
                            // Calculate direction to goal with variation
                            double dirX = goalX - playerWithBall.Position.X;
                            double dirY = goalY - playerWithBall.Position.Y;
                            double dist = Math.Sqrt(dirX * dirX + dirY * dirY);
                            
                            // Add significant random variation for missed shots
                            dirX += (_random.NextDouble() - 0.5) * 0.4;
                            dirY += (_random.NextDouble() - 0.5) * 0.4;
                            
                            // Renormalize and apply velocity
                            double missSpeed = 0.03 + _random.NextDouble() * 0.02;
                            game.Ball.VelocityX = dirX * missSpeed;
                            game.Ball.VelocityY = dirY * missSpeed;
                            
                            // Update ball position
                            game.Ball.Position.X = playerWithBall.Position.X + game.Ball.VelocityX;
                            game.Ball.Position.Y = playerWithBall.Position.Y + game.Ball.VelocityY;
                            
                            return;
                        }
                    }
                    
                    // Not close to goal, try to pass to teammates
                    // Base pass probability now increased from 5% to 15% for more frequent passing
                    double passProbability = 0.08; // Reduced from 0.12 to increase shooting probability dramatically
                    
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
                        passProbability = 0.3; // Reduced from 0.35 - Defenders pass less
                        _logger.LogDebug("Defender in defensive third - increased pass probability to 30%");
                    }
                    else if (isMidfielder)
                    {
                        passProbability = 0.15; // Reduced from 0.20 - Midfielders more likely to shoot
                        _logger.LogDebug("Midfielder - base pass probability 15%");
                    }
                    else if (isForward && inAttackingThird)
                    {
                        passProbability = 0.1; // Reduced from 0.15 - Forwards much more likely to shoot
                        _logger.LogDebug("Forward in attacking third - pass probability 10%");
                    }
                    else if (isForward && !inAttackingThird)
                    {
                        passProbability = 0.2; // Reduced from 0.25 - Forwards more likely to shoot even outside attacking third
                        _logger.LogDebug("Forward outside attacking third - pass probability 20%");
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

            // Expanded goal width - massively increased Y range to make goals easier to score
            const double GOAL_X_MIN = 0.35; // Changed from 0.38 to make goal even wider
            const double GOAL_X_MAX = 0.65; // Changed from 0.62 to make goal even wider
            
            // Only count goals if the ball is moving (i.e., has velocity) - with minimal requirements
            bool ballIsMoving = Math.Abs(game.Ball.VelocityX) > 0.0001 || Math.Abs(game.Ball.VelocityY) > 0.0001; // Reduced from 0.0003
            
            // Check that the ball is moving in the right direction for a goal - very lenient requirements
            bool isValidTeamBShot = game.Ball.VelocityX < -0.0005; // Reduced from 0.001
            bool isValidTeamAShot = game.Ball.VelocityX > 0.0005;  // Reduced from 0.001

            // Check if ball is in team A's goal - expanded goal areas
            if (game.Ball.Position.X <= 0.06 && // Changed from 0.05 to 0.06
                game.Ball.Position.Y >= GOAL_X_MIN && 
                game.Ball.Position.Y <= GOAL_X_MAX &&
                ballIsMoving && isValidTeamBShot) // Added velocity direction check
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
                    int? scorerId = null;
                    
                    // If we know who shot the ball and it was from team B, credit them with the goal
                    if (!string.IsNullOrEmpty(_lastShooterPlayerId) && _lastShooterPlayerId.StartsWith("TeamB"))
                    {
                        scorerId = TryParsePlayerId(_lastShooterPlayerId);
                        _logger.LogInformation("Goal credited to shooter: {ShooterId}", _lastShooterPlayerId);
                    }
                    else
                    {
                        // Otherwise, use closest player as before
                        var closestPlayer = game.AwayTeam.Players
                            .OrderBy(p => 
                                Math.Sqrt(
                                    Math.Pow(p.Position.X - game.Ball.Position.X, 2) + 
                                    Math.Pow(p.Position.Y - game.Ball.Position.Y, 2)
                                ))
                            .Take(1)
                            .FirstOrDefault();
                        
                        scorerId = closestPlayer != null ? TryParsePlayerId(closestPlayer.PlayerId) : null;
                        _logger.LogInformation("Goal credited to closest player: {PlayerId}", closestPlayer?.PlayerId);
                    }
                    
                    // Reset the last shooter tracking
                    _lastShooterPlayerId = string.Empty;
                    
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
            // Check if ball is in team B's goal - expanded goal areas
            else if (game.Ball.Position.X >= 0.94 && // Changed from 0.95 to 0.94 
                     game.Ball.Position.Y >= GOAL_X_MIN && 
                     game.Ball.Position.Y <= GOAL_X_MAX &&
                     ballIsMoving && isValidTeamAShot) // Added velocity direction check
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
                    int? scorerId = null;
                    
                    // If we know who shot the ball and it was from team A, credit them with the goal
                    if (!string.IsNullOrEmpty(_lastShooterPlayerId) && _lastShooterPlayerId.StartsWith("TeamA"))
                    {
                        scorerId = TryParsePlayerId(_lastShooterPlayerId);
                        _logger.LogInformation("Goal credited to shooter: {ShooterId}", _lastShooterPlayerId);
                    }
                    else
                    {
                        // Otherwise, use closest player
                        var closestPlayer = game.HomeTeam.Players
                            .OrderBy(p => 
                                Math.Sqrt(
                                    Math.Pow(p.Position.X - game.Ball.Position.X, 2) + 
                                    Math.Pow(p.Position.Y - game.Ball.Position.Y, 2)
                                ))
                            .Take(1)
                            .FirstOrDefault();
                        
                        scorerId = closestPlayer != null ? TryParsePlayerId(closestPlayer.PlayerId) : null;
                        _logger.LogInformation("Goal credited to closest player: {PlayerId}", closestPlayer?.PlayerId);
                    }
                    
                    // Reset the last shooter tracking
                    _lastShooterPlayerId = string.Empty;
                    
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
            double forwardBias = PLAYER_SPEED * 0.9; // Increased base forward movement from 0.8 to 0.9
            
            if (isDefender) forwardBias *= 0.7; // Defenders more cautious
            if (isMidfielder) forwardBias *= 1.1; // Midfielders more aggressive - increased from 1.0 to 1.1
            if (isForward) forwardBias *= 1.5; // Forwards much more aggressive - increased from 1.3 to 1.5
            
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
            
            // Determine if player is in a good position to shoot - expanded the shooting zone
            if (isTeamA) 
            {
                // For Team A, good shooting positions are on the right side of the field
                inShootingZone = player.Position.X > 0.75 && // Changed from 0.8 to 0.75 to increase shooting zone
                                 player.Position.Y > GOAL_X_MIN - 0.15 && // Expanded from 0.1 to 0.15
                                 player.Position.Y < GOAL_X_MAX + 0.15; // Expanded from 0.1 to 0.15
            }
            else 
            {
                // For Team B, good shooting positions are on the left side of the field
                inShootingZone = player.Position.X < 0.25 && // Changed from 0.2 to 0.25 to increase shooting zone
                                 player.Position.Y > GOAL_X_MIN - 0.15 && // Expanded from 0.1 to 0.15
                                 player.Position.Y < GOAL_X_MAX + 0.15; // Expanded from 0.1 to 0.15
            }
            
            // Check for shooting opportunity - higher chance for forwards
            double shootingChance = 0.08; // Increased base chance from 0.05 to 0.08
            if (isForward) shootingChance = 0.25; // Forwards have much higher shooting chance - increased from 0.15 to 0.25
            if (isMidfielder) shootingChance = 0.15; // Midfielders have higher shooting chance - increased from 0.1 to 0.15
            
            // Increase shooting chance when closer to goal
            if (distanceToGoal < SHOOTING_DISTANCE * 0.5) {
                shootingChance *= 4.0; // Quadruple shooting chance when very close - increased from 3.0
            } 
            else if (distanceToGoal < SHOOTING_DISTANCE) {
                shootingChance *= 3.0; // Triple shooting chance when in shooting range - increased from 2.0
            }
            
            // If in shooting zone, consider taking a shot
            if (inShootingZone && _random.NextDouble() < shootingChance) {
                // Calculate goal position with improved accuracy toward goal center
                double goalY = 0.5; // Center of goal
                // Reduced randomness for more accurate shots - reduced from 0.1 to 0.07
                goalY += (_random.NextDouble() - 0.5) * 0.07; 
                
                // Calculate direction to goal
                double shotDirX = (goalPostX - player.Position.X);
                double shotDirY = (goalY - player.Position.Y);
                double shotDist = Math.Sqrt(shotDirX * shotDirX + shotDirY * shotDirY);
                
                // Normalize and set velocity for shot - increased power
                double shotPower = 0.06 + (_random.NextDouble() * 0.02); // Increased from 0.05 to 0.06
                double shotVelX = (shotDirX / shotDist) * shotPower;
                double shotVelY = (shotDirY / shotDist) * shotPower;
                
                // Release the ball (shoot)
                game.BallPossession = string.Empty;
                game.Ball.VelocityX = shotVelX;
                game.Ball.VelocityY = shotVelY;
                
                _logger.LogInformation("SHOT taken by {PlayerId} in shooting zone", player.PlayerId);
                
                // Set initial ball position
                game.Ball.Position.X = player.Position.X + shotVelX;
                game.Ball.Position.Y = player.Position.Y + shotVelY;
                
                // Track who shot the ball for goal attribution
                _lastShooterPlayerId = player.PlayerId;
                
                return;
            }
            
            // NEW LOGIC: Force a shot when player is extremely close to goal but hasn't shot yet
            bool isVeryCloseToGoal = false;
            if (isTeamA)
            {
                // Team A: Check if player is extremely close to right goal (Liverpool's goal in screenshot)
                isVeryCloseToGoal = player.Position.X > 0.9 && // Changed from 0.92 to be even more aggressive
                                    player.Position.Y > GOAL_X_MIN - 0.1 && // Increased zone from 0.05 to 0.1
                                    player.Position.Y < GOAL_X_MAX + 0.1; // Increased zone from 0.05 to 0.1
            }
            else
            {
                // Team B: Check if player is extremely close to left goal (Manchester's goal in screenshot)
                isVeryCloseToGoal = player.Position.X < 0.1 && // Changed from 0.08 to be even more aggressive
                                    player.Position.Y > GOAL_X_MIN - 0.1 && // Increased zone from 0.05 to 0.1
                                    player.Position.Y < GOAL_X_MAX + 0.1; // Increased zone from 0.05 to 0.1
            }

            // If player is very close to goal, force a shot instead of continuing movement
            if (isVeryCloseToGoal)
            {
                // Calculate direction toward goal center with improved accuracy
                double goalY = 0.5; // Center of goal
                
                // Reduced randomization for more accurate forced shots
                goalY += (_random.NextDouble() - 0.5) * 0.06; // Reduced from 0.1
                
                // Calculate direction vector to goal
                double shotDirX = (goalPostX - player.Position.X);
                double shotDirY = (goalY - player.Position.Y);
                double shotDist = Math.Sqrt(shotDirX * shotDirX + shotDirY * shotDirY);
                
                // Normalize and set high velocity for shot - increased power
                double shotVelX = (shotDirX / shotDist) * 0.06; // Increased from 0.05
                double shotVelY = (shotDirY / shotDist) * 0.06; // Increased from 0.05
                
                // Release the ball (shoot)
                game.BallPossession = string.Empty;
                game.Ball.VelocityX = shotVelX;
                game.Ball.VelocityY = shotVelY;
                
                // Log the forced shot
                _logger.LogInformation("FORCED SHOT by {PlayerId} - very close to goal", player.PlayerId);
                
                // Set initial ball position
                game.Ball.Position.X = player.Position.X + shotVelX;
                game.Ball.Position.Y = player.Position.Y + shotVelY;
                
                // Track who shot the ball for goal attribution
                _lastShooterPlayerId = player.PlayerId;
                
                return;
            }

            // Additional check for unexpected powerful shots when near goal area
            if ((isTeamA && player.Position.X > 0.85) || (!isTeamA && player.Position.X < 0.15))
            {
                // Player is near goal but not quite in the very close zone - give extra chance to shoot
                double opportunisticShotChance = isForward ? 0.2 : 0.1; // Forwards are more opportunistic
                
                if (_random.NextDouble() < opportunisticShotChance)
                {
                    // Calculate goal target with good accuracy
                    double goalY = 0.5; // Center of goal
                    goalY += (_random.NextDouble() - 0.5) * 0.08; // Slight randomization
                    
                    // Calculate direction to goal
                    double shotDirX = (goalPostX - player.Position.X);
                    double shotDirY = (goalY - player.Position.Y);
                    double shotDist = Math.Sqrt(shotDirX * shotDirX + shotDirY * shotDirY);
                    
                    // Set velocity for powerful shot
                    double shotPower = 0.07; // Strong shot
                    double shotVelX = (shotDirX / shotDist) * shotPower;
                    double shotVelY = (shotDirY / shotDist) * shotPower;
                    
                    // Release the ball (shoot)
                    game.BallPossession = string.Empty;
                    game.Ball.VelocityX = shotVelX;
                    game.Ball.VelocityY = shotVelY;
                    
                    _logger.LogInformation("OPPORTUNISTIC SHOT by {PlayerId} near goal area!", player.PlayerId);
                    
                    // Set initial ball position
                    game.Ball.Position.X = player.Position.X + shotVelX;
                    game.Ball.Position.Y = player.Position.Y + shotVelY;
                    
                    // Track who shot the ball for goal attribution
                    _lastShooterPlayerId = player.PlayerId;
                    
                    return;
                }
            }
            
            // Continue with existing logic...
            if (pathBlocked)
            {
                dx *= 0.2; // Slow down significantly but don't completely stop
                
                if (inShootingZone)
                {
                    // If in a shooting position and blocked, favor lateral movement to find space
                    if (_random.NextDouble() < 0.7) // 70% chance to move laterally when blocked
                    {
                        // Choose direction (+1 or -1)
                        int lateralDir = _random.NextDouble() < 0.5 ? 1 : -1;
                        dy += lateralDir * PLAYER_SPEED * 0.8;
                        _logger.LogDebug("Player finding space in shooting zone: {PlayerId}", player.PlayerId);
                    }
                    else
                    {
                        // Maybe try to pass or shoot instead of moving
                        // This is handled elsewhere in the code
                    }
                }
                else
                {
                    // Not in shooting zone, add some randomization to movement
                    dy += (_random.NextDouble() - 0.5) * PLAYER_SPEED;
                }
            }
            
            // Limit movement to field boundaries
            double newX = Math.Clamp(player.Position.X + dx, 0, 1);
            double newY = Math.Clamp(player.Position.Y + dy, 0, 1);
            
            // Update player position
            player.Position.X = newX;
            player.Position.Y = newY;
            
            // Update ball position to match player
            game.Ball.Position = player.Position;
            
            _logger.LogDebug("Moved ball possessor {PlayerId}: dx={DX}, dy={DY}, pathBlocked={PathBlocked}, inShootingZone={InShootingZone}", 
                player.PlayerId, dx, dy, pathBlocked, inShootingZone);
        }

        // Modified to track if a goal has been scored recently
        private bool _recentGoalScored = false;
        private DateTime _lastGoalTime = DateTime.MinValue;
        private const double GOAL_COOLDOWN_SECONDS = 3.0; // Increased from 1.0 to 3.0 seconds to prevent rapid duplicate goals
        private string _lastShooterPlayerId = string.Empty; // Track which player took the last shot for goal attribution

        // Add the missing CalculateDistance method
        private double CalculateDistance(Position pos1, Position pos2)
        {
            double dx = pos1.X - pos2.X;
            double dy = pos1.Y - pos2.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // New method to check if the ball has gone out of bounds
        private bool CheckForOutOfBounds(FootballCommentary.Core.Models.GameState game, ref bool publishEvent, ref GameEvent? gameEvent)
        {
            // Define the boundary area - slightly inside the actual clamp boundary
            const double BOUNDARY_THRESHOLD = 0.01; // 1% from edge is considered out
            
            // Check if ball is at the very edge of the field
            bool isAtXBoundary = game.Ball.Position.X <= BOUNDARY_THRESHOLD || game.Ball.Position.X >= (1 - BOUNDARY_THRESHOLD);
            bool isAtYBoundary = game.Ball.Position.Y <= BOUNDARY_THRESHOLD || game.Ball.Position.Y >= (1 - BOUNDARY_THRESHOLD);
            
            // Only count as out if ball has velocity (is moving toward boundary)
            bool hasVelocity = Math.Abs(game.Ball.VelocityX) > 0.001 || Math.Abs(game.Ball.VelocityY) > 0.001;
            
            if ((isAtXBoundary || isAtYBoundary) && hasVelocity)
            {
                _logger.LogInformation("BALL OUT OF BOUNDS! X: {X}, Y: {Y}", 
                    game.Ball.Position.X, game.Ball.Position.Y);
                
                // Determine which team gets the ball (opposite of last team to touch)
                string lastTeamToTouch = !string.IsNullOrEmpty(_lastShooterPlayerId) && _lastShooterPlayerId.StartsWith("TeamA") 
                    ? "TeamB" : "TeamA";
                
                // Reset player positions to formation
                ResetPlayerPositions(game, true);  // Team A
                ResetPlayerPositions(game, false); // Team B
                
                // Reset ball position to the middle of the out of bounds location
                if (isAtXBoundary)
                {
                    if (game.Ball.Position.X <= BOUNDARY_THRESHOLD)
                    {
                        // Left boundary
                        game.Ball.Position.X = 0.05;
                    }
                    else
                    {
                        // Right boundary
                        game.Ball.Position.X = 0.95;
                    }
                }
                else if (isAtYBoundary)
                {
                    if (game.Ball.Position.Y <= BOUNDARY_THRESHOLD)
                    {
                        // Top boundary
                        game.Ball.Position.Y = 0.05;
                    }
                    else
                    {
                        // Bottom boundary
                        game.Ball.Position.Y = 0.95;
                    }
                }
                
                // Stop the ball
                game.Ball.VelocityX = 0;
                game.Ball.VelocityY = 0;
                
                // Give possession to opposing team
                var possessingTeam = lastTeamToTouch == "TeamA" ? game.HomeTeam : game.AwayTeam;
                
                // Find nearest player from the team to get ball
                var nearestPlayer = possessingTeam.Players
                    .OrderBy(p => Math.Sqrt(
                        Math.Pow(p.Position.X - game.Ball.Position.X, 2) + 
                        Math.Pow(p.Position.Y - game.Ball.Position.Y, 2)
                    ))
                    .First();
                
                // Small adjustment to place player by ball
                nearestPlayer.Position.X = game.Ball.Position.X;
                nearestPlayer.Position.Y = game.Ball.Position.Y;
                
                // Give possession
                game.BallPossession = nearestPlayer.PlayerId;
                
                // Create event for out of bounds
                publishEvent = true;
                gameEvent = new GameEvent
                {
                    GameId = game.GameId,
                    EventType = GameEventType.OutOfBounds,
                    Position = new Position { X = game.Ball.Position.X, Y = game.Ball.Position.Y },
                    TeamId = lastTeamToTouch
                };
                
                return true; // Ball was out of bounds
            }
            
            return false; // Ball was not out of bounds
        }
    }
}