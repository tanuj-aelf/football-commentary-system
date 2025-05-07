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
        private const double FORMATION_ADHERENCE = 0.015; // Increased from 0.005 for more dynamic movement
        
        // Field zones for more realistic positioning
        private const double DEFENSE_ZONE = 0.3;
        private const double MIDFIELD_ZONE = 0.6;
        private const double ATTACK_ZONE = 0.7;
        private const double TEAMMATE_AVOIDANCE_DISTANCE = 0.15; // Increased from 0.08 to match MIN_TEAMMATE_DISTANCE
        private const double OPPONENT_AWARENESS_DISTANCE = 0.15;
        
        // List to track recent tackle attempts
        private readonly List<TackleAttempt> _recentTackleAttempts = new();
        
        // Tackle and ball control constants
        private const double PLAYER_CONTROL_DISTANCE = 0.07; // Increased from 0.06
        private const double TACKLE_DISTANCE = 0.12; // Increased from 0.1 for more tackles
        private const double BASE_TACKLE_CHANCE = 0.15; // Reduced from 0.2 to make tackles even less successful
        
        // Add a new constant for loose ball attraction
        private const double LOOSE_BALL_ATTRACTION = 0.09; // Decreased from 0.12 to reduce clustering around loose balls
        
        // These constants need to be defined at class level
        private const double GOAL_X_MIN = 0.45;
        private const double GOAL_X_MAX = 0.55;
        
        // Add this field after the other private fields in the GameStateGAgent class (around line 100)
        private readonly Dictionary<string, TimeSpan> _latestGameTimes = new Dictionary<string, TimeSpan>();
        
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
            _playerAgentManager.InitializePlayerAgents(gameState, 
                                                 _state.State.TeamAFormations.GetValueOrDefault(gameId), 
                                                 _state.State.TeamBFormations.GetValueOrDefault(gameId));
            
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
                
                // Update player agents with formation information if they exist
                if (_playerAgentManager.HasPlayerAgent(players.FirstOrDefault()?.PlayerId))
                {
                    _logger.LogInformation("Updating player agents with formation information for {Team}", 
                        isTeamA ? "Team A" : "Team B");
                    
                    _playerAgentManager.UpdatePlayerFormations(
                        gameId, 
                        isTeamA, 
                        formation, 
                        formationData.BasePositions);
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
            
            // Reset game time tracking to ensure we start from 0
            game.GameTime = TimeSpan.Zero;
            _latestGameTimes[gameId] = TimeSpan.Zero;
            
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
                _playerAgentManager.InitializePlayerAgents(game, 
                                                     _state.State.TeamAFormations.GetValueOrDefault(gameId), 
                                                     _state.State.TeamBFormations.GetValueOrDefault(gameId));
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
            
            // Ensure game time is at least 90 minutes when ending
            if (game.GameTime.TotalMinutes < 90)
            {
                game.GameTime = TimeSpan.FromMinutes(90);
                // Also update the latest game time tracking
                _latestGameTimes[gameId] = TimeSpan.FromMinutes(90);
                _logger.LogInformation("Setting final game time to 90 minutes for game {GameId}", gameId);
            }
            
            game.Status = GameStatus.Ended;
            game.LastUpdateTime = DateTime.UtcNow;
            game.FinalGameTime = game.GameTime; // Store the final game time
            
            // Stop game simulation
            if (_gameSimulationTimers.TryGetValue(gameId, out var timer))
            {
                timer.Dispose();
                _gameSimulationTimers.Remove(gameId);
            }
            
            // Clean up the latest game time tracking
            _latestGameTimes.Remove(gameId);
            
            await _state.WriteStateAsync();
            
            // Publish final game state update with the correct time
            await PublishGameStateUpdateAsync(game);
            
            // Publish game end event
            await PublishGameEventAsync(new GameEvent
            {
                GameId = gameId,
                EventType = GameEventType.GameEnd,
                GameTime = game.GameTime // Include the final game time in the event
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
                
                // Update game time with more precise calculation
                var realElapsed = DateTime.UtcNow - game.GameStartTime;
                
                // Scale real time: 1.5 real minutes = 90 game minutes
                // This ensures we're using the same time scale as the client
                double scaleFactor = 90 / 1.5; // 90 minutes / 1.5 minutes = 60
                var scaledMinutes = realElapsed.TotalMinutes * scaleFactor;
                
                // Convert to TimeSpan (this will be what's displayed on screen)
                var calculatedGameTime = TimeSpan.FromMinutes(scaledMinutes);
                
                // Check if we have a stored latest time for this game
                if (!_latestGameTimes.TryGetValue(gameId, out var latestTime))
                {
                    // First time tracking this game
                    _latestGameTimes[gameId] = calculatedGameTime;
                    latestTime = calculatedGameTime;
                }
                // Only update the time if the calculated time is greater than our stored latest time
                else if (calculatedGameTime > latestTime)
                {
                    _latestGameTimes[gameId] = calculatedGameTime;
                    latestTime = calculatedGameTime;
                }
                else
                {
                    _logger.LogDebug("Calculated time {CalculatedTime} is less than or equal to latest time {LatestTime}, keeping latest time", 
                        calculatedGameTime, latestTime);
                }
                
                // Always use the latest valid time
                game.GameTime = latestTime;
                
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
                // To avoid early termination, ensure we've actually reached 90 minutes
                if (game.GameTime.TotalMinutes >= 90 && game.Status == GameStatus.InProgress)
                {
                    _logger.LogInformation("Game {GameId} reached 90 minutes, ending game. Total real time elapsed: {RealMinutes:F2} minutes", 
                        gameId, realElapsed.TotalMinutes);
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
                    
                    // Update player agents with new formation information
                    _playerAgentManager.UpdatePlayerFormations(
                        game.GameId,
                        true, // isTeamA
                        teamAFormation,
                        teamAData.BasePositions);
                }
                
                if (teamBFormationChanged)
                {
                    _logger.LogInformation("Team B formation changed to {Formation}", teamBFormation);
                    UpdateTeamBasePositions(game, false, teamBData);
                    
                    // Update player agents with new formation information
                    _playerAgentManager.UpdatePlayerFormations(
                        game.GameId,
                        false, // isTeamA
                        teamBFormation,
                        teamBData.BasePositions);
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
            
            // Check if the ball is in the team's own defensive half
            bool ballInOwnDefensiveHalf = (isTeamA && game.Ball.Position.X < 0.5) || (!isTeamA && game.Ball.Position.X > 0.5);
            
            // Check if player is far into the attacking half
            bool deepInAttackingHalf = (isTeamA && playerX > 0.75) || (!isTeamA && playerX < 0.25);
            
            // Variables for movement calculation
            double dx = 0;
            double dy = 0;
            
            // Get ball possessor information
            string possessorId = game.BallPossession ?? "";
            bool teamHasPossession = !string.IsNullOrEmpty(possessorId) && 
                                    (isTeamA && possessorId.StartsWith("TeamA")) || 
                                    (!isTeamA && possessorId.StartsWith("TeamB"));
            
            // NEW: Count players in attacking half to implement team shape discipline
            int teamPlayersInAttackingHalf = 0;
            var teamPlayers = isTeamA ? game.HomeTeam.Players : game.AwayTeam.Players;
            foreach (var teammate in teamPlayers)
            {
                bool teammateInAttackingHalf = (isTeamA && teammate.Position.X > 0.5) || 
                                  (!isTeamA && teammate.Position.X < 0.5);
                if (teammateInAttackingHalf)
                {
                    teamPlayersInAttackingHalf++;
                }
            }
            
            // NEW: Define strict maximum forward positions by role
            double maxForwardPosition;
            if (isDefender) {
                maxForwardPosition = isTeamA ? 0.55 : 0.45; // Defenders barely cross midfield
                // Even stricter if too many players forward already
                if (teamPlayersInAttackingHalf > 5) {
                    maxForwardPosition = isTeamA ? 0.45 : 0.55; // Keep defenders in own half
                }
            } else if (isMidfielder) {
                // Holding midfielders (6, 8) are more restricted than attacking midfielders (7, 9)
                if (playerNumber == 6 || playerNumber == 8) {
                    maxForwardPosition = isTeamA ? 0.65 : 0.35; // Holding midfielders
                    if (teamPlayersInAttackingHalf > 5) {
                        maxForwardPosition = isTeamA ? 0.55 : 0.45; // More restrictive when too many forward
                    }
                } else {
                    maxForwardPosition = isTeamA ? 0.8 : 0.2; // Attacking midfielders
                    if (teamPlayersInAttackingHalf > 6) {
                        maxForwardPosition = isTeamA ? 0.7 : 0.3; // Still restrict if too many forward
                    }
                }
            } else {
                // Forwards
                maxForwardPosition = isTeamA ? 0.95 : 0.05; // Can go all the way
            }
            
            // NEW: Enforce position discipline FIRST before any other movement calculations
            // If player is too far forward beyond allowed position, strong retreat force
            if ((isTeamA && playerX > maxForwardPosition) || (!isTeamA && playerX < maxForwardPosition)) {
                // Calculate retreat target slightly behind max position
                double retreatTargetX = isTeamA ? maxForwardPosition - 0.05 : maxForwardPosition + 0.05;
                
                // Very strong retreat force - overrides other movement incentives
                double retreatForce = 0.05; // Strong absolute force value
                dx = isTeamA ? -retreatForce : retreatForce;
                
                // Also pull toward base position Y to maintain team shape
                dy = (basePosition.Y - player.Position.Y) * 0.03;
                
                _logger.LogDebug("ENFORCING RETREAT: Player {PlayerId} ({Role}) forced to retreat from {Position} to max allowed {MaxPos}",
                    player.PlayerId, isDefender ? "Defender" : isMidfielder ? "Midfielder" : "Forward", playerX, maxForwardPosition);
                
                // Apply movement with boundary checking
                player.Position.X = Math.Clamp(player.Position.X + dx, 0, 1);
                player.Position.Y = Math.Clamp(player.Position.Y + dy, 0, 1);
                
                // Skip other movement calculations for this player since retreat takes priority
                return;
            }
            
            // If ball possessor exists and is from same team
            if (playerWithBall != null && teamHasPossession)
            {
                double distToBallPossessor = CalculateDistance(player.Position, playerWithBall.Position);
                
                // Movement behaviors based on player role and field position
                if (isDefender)
                {
                    // Defenders should generally stay back, regardless of possession
                    double defensivePositionX = isTeamA ? 0.25 : 0.75; // More conservative defensive position
                    
                    // Stronger formation adherence for defenders
                    double formationAdherenceMultiplier = 3.0; // Increased from 2.5
                    
                    // Different behavior based on ball position
                    if (ballInOwnDefensiveHalf) {
                        // Ball in own half - defenders stay deep and provide cover
                        dx += (defensivePositionX - player.Position.X) * (FORMATION_ADHERENCE * formationAdherenceMultiplier * 1.5);
                        dy += (basePosition.Y - player.Position.Y) * (FORMATION_ADHERENCE * formationAdherenceMultiplier);
                    } else {
                        // Ball in opponent half - maintain defensive line at or near midfield
                        double defensiveLineX = isTeamA ? 0.45 : 0.55; // Just behind midfield
                        
                        // Extremely strong pull to defensive line
                        dx += (defensiveLineX - player.Position.X) * (FORMATION_ADHERENCE * formationAdherenceMultiplier * 2.0);
                        dy += (basePosition.Y - player.Position.Y) * (FORMATION_ADHERENCE * formationAdherenceMultiplier);
                    }
                }
                else if (isMidfielder)
                {
                    // Midfielders: More balanced between attack and defense
                    // Differentiate between holding midfielders (6, 8) and attacking midfielders (7, 9)
                    bool isHoldingMidfielder = (playerNumber == 6 || playerNumber == 8);
                    
                    if (ballInOwnDefensiveHalf)
                    {
                        // When ball is in own half, midfielders provide closer support
                        // Holding midfielders stay deeper
                        if (isHoldingMidfielder) {
                            double supportPositionX = isTeamA ? 0.35 : 0.65; // Deeper support position
                            dx += (supportPositionX - player.Position.X) * (FORMATION_ADHERENCE * 3.0);
                            dy += (basePosition.Y - player.Position.Y) * (FORMATION_ADHERENCE * 2.0);
                        } else {
                            // Attacking midfielders provide support but can be slightly higher
                            double supportPositionX = isTeamA ? 0.45 : 0.55; // Higher support position
                            dx += (supportPositionX - player.Position.X) * (FORMATION_ADHERENCE * 2.5);
                            dy += (basePosition.Y - player.Position.Y) * (FORMATION_ADHERENCE * 1.5);
                            
                            // Move toward ball possessor for support
                            if (distToBallPossessor > 0.15) {
                                dx += (playerWithBall.Position.X - player.Position.X) * 0.02;
                                dy += (playerWithBall.Position.Y - player.Position.Y) * 0.03;
                            }
                        }
                    }
                    else // Ball in opponent's half
                    {
                        // Check if we already have too many players in attacking half
                        if (teamPlayersInAttackingHalf > 6 && isHoldingMidfielder) {
                            // One holding midfielder should always stay back for balance
                            double balancePositionX = isTeamA ? 0.45 : 0.55; // Just behind midfield
                            dx += (balancePositionX - player.Position.X) * (FORMATION_ADHERENCE * 3.0);
                            dy += (basePosition.Y - player.Position.Y) * (FORMATION_ADHERENCE * 2.0);
                            
                            _logger.LogDebug("Holding midfielder {PlayerId} maintaining balance as too many players forward", player.PlayerId);
                        } else {
                            // Midfielder can move forward with more freedom, but still with position discipline
                            double forwardPositionX = isTeamA ? 
                                Math.Min(playerWithBall.Position.X + 0.1, maxForwardPosition) : 
                                Math.Max(playerWithBall.Position.X - 0.1, maxForwardPosition);
                            
                            // More moderate forward movement to maintain balance
                            dx += (forwardPositionX - player.Position.X) * (FORMATION_ADHERENCE * 1.5);
                            // Keep width position based on role
                            dy += (basePosition.Y - player.Position.Y) * (FORMATION_ADHERENCE * 1.0);
                        }
                    }
                }
                else if (isForward)
                {
                    // Forwards: Most aggressive attacking movement
                    if (ballInOwnDefensiveHalf)
                    {
                        // If ball is in own half, forwards drop deeper to provide options
                        double supportPositionX = isTeamA ? 0.5 : 0.5; // At midfield line
                        dx += (supportPositionX - player.Position.X) * (FORMATION_ADHERENCE * 1.5);
                        dy += (basePosition.Y - player.Position.Y) * (FORMATION_ADHERENCE * 1.2);
                    }
                    else // Ball in opponent's half
                    {
                        // Aggressive attacking runs, but still maintain some structure
                        
                        // Stagger forwards - not all forwards should push highest line
                        if (playerNumber == 10) { // First forward stays slightly deeper
                            double forwardPositionX = isTeamA ? 0.8 : 0.2; 
                            dx += (forwardPositionX - player.Position.X) * (FORMATION_ADHERENCE * 1.2);
                        } else { // Second forward can push highest line
                            double forwardPositionX = isTeamA ? 0.9 : 0.1;
                            dx += (forwardPositionX - player.Position.X) * (FORMATION_ADHERENCE * 1.0);
                        }
                        
                        // Width based on base position
                        dy += (basePosition.Y - player.Position.Y) * (FORMATION_ADHERENCE * 0.8);
                        
                        // Add some randomness for unpredictable forward runs
                        if (_random.NextDouble() < 0.1) {
                            dy += (_random.NextDouble() - 0.5) * 0.02;
                        }
                    }
                }
            }
            
            // Apply teammate avoidance to prevent crowding
            AvoidTeammates(game, player, ref dx, ref dy, player.PlayerId.StartsWith("TeamA"));
            
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
            
            // NEW: Count players in attacking half for defensive shape coordination
            int teamPlayersInAttackingHalf = 0;
            var teamPlayers = isTeamA ? game.HomeTeam.Players : game.AwayTeam.Players;
            foreach (var teammate in teamPlayers)
            {
                bool teammateInAttackingHalf = (isTeamA && teammate.Position.X > 0.5) || 
                                  (!isTeamA && teammate.Position.X < 0.5);
                if (teammateInAttackingHalf)
                {
                    teamPlayersInAttackingHalf++;
                }
            }
            
            // NEW: Define strict maximum forward positions by role for defensive positioning
            double maxForwardPosition;
            if (isDefender) {
                maxForwardPosition = isTeamA ? 0.5 : 0.5; // Defenders at midfield max
                if (teamPlayersInAttackingHalf > 3) {
                    maxForwardPosition = isTeamA ? 0.4 : 0.6; // More restrictive when too many forward
                }
            } else if (isMidfielder) {
                // Holding midfielders (6, 8) have different restrictions than attacking midfielders (7, 9)
                if (playerNumber == 6 || playerNumber == 8) {
                    maxForwardPosition = isTeamA ? 0.55 : 0.45; // Defensive midfielders
                } else {
                    maxForwardPosition = isTeamA ? 0.65 : 0.35; // Attacking midfielders
                }
            } else {
                // Forwards have more freedom but still limited
                maxForwardPosition = isTeamA ? 0.8 : 0.2;
            }
            
            // NEW: Check if player needs to retreat from beyond max position FIRST
            bool playerTooFarForward = (isTeamA && player.Position.X > maxForwardPosition) || 
                                     (!isTeamA && player.Position.X < maxForwardPosition);
            
            if (playerTooFarForward && playerWithBall != null) {
                // Calculate retreat position (behind max forward position)
                double retreatTargetX = isTeamA ? maxForwardPosition - 0.1 : maxForwardPosition + 0.1;
                double retreatStrength = FORMATION_ADHERENCE * 5.0; // Very strong retreat force
                
                // Strong pull back to defensive position
                dx = (retreatTargetX - player.Position.X) * retreatStrength;
                
                // Maintain Y position based on role
                dy = (basePosition.Y - player.Position.Y) * (FORMATION_ADHERENCE * 2.0);
                
                _logger.LogDebug("DEFENSIVE RETREAT: Player {PlayerId} ({Role}) forced to retreat defensively from {Position}",
                    player.PlayerId, isDefender ? "Defender" : isMidfielder ? "Midfielder" : "Forward", player.Position.X);
                
                // Apply the retreat move and skip other calculations
                player.Position.X = Math.Clamp(player.Position.X + dx, 0, 1);
                player.Position.Y = Math.Clamp(player.Position.Y + dy, 0, 1);
                return;
            }
            
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
                
                // NEW: Check if player should go for ball based on tactical position
                bool shouldGoForBall = false;
                
                // Only the closest 2 players should go for loose balls, unless the ball is in our defensive third
                bool ballInDefensiveThird = (isTeamA && game.Ball.Position.X < 0.3) || 
                                          (!isTeamA && game.Ball.Position.X > 0.7);
                
                // Loose ball in our defensive third - more players can go for it
                if (ballInDefensiveThird && distToBall < 0.2) {
                    shouldGoForBall = myRank < 4; // Up to 4 players can go for it in defensive third
                }
                // Loose ball elsewhere - fewer players should go for it to maintain shape
                else if (distToBall < 0.15) {
                    shouldGoForBall = myRank < 2; // Only 2 closest players go for it
                }
                
                // If player should go for loose ball
                if (shouldGoForBall)
                {
                    double ballAttractionStrength = LOOSE_BALL_ATTRACTION;
                    
                    // Adjust attraction based on distance - closer players are more attracted
                    ballAttractionStrength *= (1.0 - Math.Min(distToBall, 0.3) / 0.3) * 1.7;
                    
                    // Adjust attraction based on player role and field position
                    bool isLooseBallInOurHalf = (isTeamA && game.Ball.Position.X < 0.5) || (!isTeamA && game.Ball.Position.X > 0.5);
                    
                    if (isDefender && isLooseBallInOurHalf) ballAttractionStrength *= 1.8; // Defenders prioritize ball in our half
                    else if (isDefender) ballAttractionStrength *= 0.6; // Defenders less interested in opponent half
                    
                    if (isMidfielder) ballAttractionStrength *= 1.2; // Midfielders generally go for ball
                    
                    if (isForward && !isLooseBallInOurHalf) ballAttractionStrength *= 1.1; // Forwards more interested in opponent half
                    else if (isForward) ballAttractionStrength *= 0.6; // Forwards less interested in our half
                    
                    // Move toward ball
                    dx += (game.Ball.Position.X - player.Position.X) * ballAttractionStrength;
                    dy += (game.Ball.Position.Y - player.Position.Y) * ballAttractionStrength;
                    
                    // Add a bit of randomness to prevent all players converging on exactly the same point
                    dx += (_random.NextDouble() - 0.5) * 0.01;
                    dy += (_random.NextDouble() - 0.5) * 0.01;
                    
                    // Apply movement with boundary checking
                    player.Position.X = Math.Clamp(player.Position.X + dx, 0, 1);
                    player.Position.Y = Math.Clamp(player.Position.Y + dy, 0, 1);
                    return;
                }
            }
            
            // Determine if the ball is in team's defensive half or in opponent's half
            bool ballInDefensiveHalf = (isTeamA && targetPos.X < 0.5) || (!isTeamA && targetPos.X > 0.5);
            
            // Check if player needs to retreat from opponent's half
            bool playerInOpponentHalf = (isTeamA && player.Position.X > 0.5) || (!isTeamA && player.Position.X < 0.5);
            
            // Stronger retreat force when opponent has the ball and player is in their half
            if (playerInOpponentHalf && playerWithBall != null) {
                // Calculate retreat position (slightly behind midfield)
                double retreatTargetX = isTeamA ? 0.45 : 0.55;
                double retreatStrength = FORMATION_ADHERENCE * 4.0; // Increased from 3.5
                
                // Apply retreat force based on player role (all stronger now)
                if (isDefender) {
                    // Defenders retreat most urgently to their defensive positions
                    retreatStrength *= 1.8; // Increased from 1.5
                    retreatTargetX = isTeamA ? 0.35 : 0.65; // Deeper position for defenders
                } else if (isMidfielder) {
                    // Midfielders retreat to midfield
                    retreatStrength *= 1.5; // Increased from 1.3
                } else if (isForward) {
                    // Even forwards retreat, but not as deep
                    retreatStrength *= 1.2; // Increased from 1.1
                }
                
                // Strong pull back to own half
                dx = (retreatTargetX - player.Position.X) * retreatStrength;
                
                // Maintain some of the Y position relative to base position
                dy = (basePosition.Y - player.Position.Y) * (FORMATION_ADHERENCE * 2.0); // Increased from 1.5
                
                // Add natural movements and continue to the normal positioning logic with reduced effect
                dx += naturalMovementX;
                dy += naturalMovementY;
                
                // Apply movement with boundary checking
                player.Position.X = Math.Clamp(player.Position.X + dx, 0, 1);
                player.Position.Y = Math.Clamp(player.Position.Y + dy, 0, 1);
                return; // Skip other calculations since retreat takes priority
            }
            
            // Check if ball is in player's team's defensive half (indicating a direct threat)
            bool ballThreateningOwnGoal = (isTeamA && targetPos.X < 0.4) || (!isTeamA && targetPos.X > 0.6);

            // Defensive positioning based on player role
            if (isGoalkeeper) {
                // Goalkeeper stays near the goal, with slight adjustment based on ball position
                dx = (basePosition.X - player.Position.X) * (FORMATION_ADHERENCE * 7.0);
                
                // Goalkeepers adjust Y position based on ball position, but limited movement
                double targetY = 0.5 + (targetPos.Y - 0.5) * 0.5; // Move toward ball's Y position, but only 50%
                targetY = Math.Clamp(targetY, 0.3, 0.7); // Limit keeper's range
                dy = (targetY - player.Position.Y) * (FORMATION_ADHERENCE * 3.0);
                
                // Apply natural movements for slight variation
                dx += naturalMovementX * 0.5;
                dy += naturalMovementY * 0.5;
            }
            else if (isDefender) {
                // Defenders focus on maintaining defensive shape and marking
                if (ballThreateningOwnGoal) {
                    // When ball is threatening our goal, defenders drop deeper
                    double defensivePositionX = isTeamA ? 0.2 : 0.8; // Deeper defensive position
                    dx = (defensivePositionX - player.Position.X) * (FORMATION_ADHERENCE * 4.0); // Increased pull
                    
                    // Shift toward ball side but maintain defensive line
                    double ballSideShift = (targetPos.Y - 0.5) * 0.4; // Shift 40% toward ball's side
                    double targetY = basePosition.Y + ballSideShift;
                    targetY = Math.Clamp(targetY, 0.2, 0.8); // Ensure not too wide
                    dy = (targetY - player.Position.Y) * (FORMATION_ADHERENCE * 3.0);
                    
                    // Only nearest defender actively approaches ball carrier
                    if (playerWithBall != null) {
                        double distanceToTarget = Math.Sqrt(
                            Math.Pow(player.Position.X - targetPos.X, 2) +
                            Math.Pow(player.Position.Y - targetPos.Y, 2));
                        
                        // Find if this is the closest defender
                        var otherDefenders = teamPlayers
                            .Where(p => {
                                int otherNum = TryParsePlayerId(p.PlayerId) ?? 0;
                                otherNum += 1;
                                return p.PlayerId != player.PlayerId && otherNum >= 2 && otherNum <= 5;
                            });
                        
                        bool isClosestDefender = !otherDefenders.Any(p => 
                            CalculateDistance(p.Position, targetPos) < distanceToTarget);
                        
                        if (isClosestDefender && distanceToTarget < 0.15) {
                            // Adjust movement to close down attacker
                            dx = (targetPos.X - player.Position.X) * 0.05;
                            dy = (targetPos.Y - player.Position.Y) * 0.05;
                        }
                    }
                }
                else {
                    // Ball not directly threatening - maintain defensive shape with more compactness
                    double defensiveLineX;
                    if (ballInDefensiveHalf) {
                        // Ball in our half - defenders drop a bit deeper
                        defensiveLineX = isTeamA ? 0.3 : 0.7;
                    } else {
                        // Ball in opponent half - defenders push to midfield
                        defensiveLineX = isTeamA ? 0.4 : 0.6;
                    }
                    
                    // Strong pull toward defensive line
                    dx = (defensiveLineX - player.Position.X) * (FORMATION_ADHERENCE * 3.5);
                    
                    // Maintain horizontal spacing based on formation role
                    dy = (basePosition.Y - player.Position.Y) * (FORMATION_ADHERENCE * 2.5);
                    
                    // Shift slightly toward ball side to maintain defensive compactness
                    double ballSideShift = (targetPos.Y - 0.5) * 0.3;
                    dy += ballSideShift * 0.02;
                }
                
                // Add natural movement for defenders
                dx += naturalMovementX;
                dy += naturalMovementY;
            }
            else if (isMidfielder) {
                bool isHoldingMidfielder = (playerNumber == 6 || playerNumber == 8);
                
                // Midfielders - balance between pressing and defensive shape
                if (ballThreateningOwnGoal) {
                    // Ball threatening goal - midfielders drop to help defense
                    double supportPositionX = isTeamA ? 0.3 : 0.7; // Deep support position
                    dx = (supportPositionX - player.Position.X) * (FORMATION_ADHERENCE * 3.5);
                    
                    // Holding midfielders tighter to center, wide midfielders maintain width
                    double supportYFactor = isHoldingMidfielder ? 0.8 : 0.5;
                    double targetY = 0.5 + (basePosition.Y - 0.5) * supportYFactor;
                    dy = (targetY - player.Position.Y) * (FORMATION_ADHERENCE * 2.5);
                    
                    _logger.LogDebug("Midfielder {PlayerId} dropping deep as ball is threatening", player.PlayerId);
                }
                else if (ballInDefensiveHalf) {
                    // Ball in our half but not threatening - midfielders form compact block
                    double blockPositionX = isTeamA ? 0.4 : 0.6; // Form midfield block
                    dx = (blockPositionX - player.Position.X) * (FORMATION_ADHERENCE * 3.0);
                    
                    // Get compact horizontally but maintain lanes
                    double compactYFactor = 0.7; // Bring slightly toward center from base position
                    double targetY = 0.5 + (basePosition.Y - 0.5) * compactYFactor;
                    dy = (targetY - player.Position.Y) * (FORMATION_ADHERENCE * 2.0);
                    
                    // Holding midfielders track nearest opponent
                    if (isHoldingMidfielder && playerWithBall != null) {
                        double distanceToTarget = Math.Sqrt(
                            Math.Pow(player.Position.X - targetPos.X, 2) +
                            Math.Pow(player.Position.Y - targetPos.Y, 2));
                        
                        if (distanceToTarget < 0.2) {
                            // Move to press/track opponent
                            dx += (targetPos.X - player.Position.X) * 0.04;
                            dy += (targetPos.Y - player.Position.Y) * 0.04;
                        }
                    }
                }
                else {
                    // Ball in opponent half - midfielders push up but maintain balance
                    double pressPositionX;
                    if (isHoldingMidfielder) {
                        // Holding midfielders stay near midfield
                        pressPositionX = isTeamA ? 0.5 : 0.5;
                    } else {
                        // Attacking midfielders push higher to press
                        pressPositionX = isTeamA ? 0.55 : 0.45;
                    }
                    
                    dx = (pressPositionX - player.Position.X) * (FORMATION_ADHERENCE * 2.5);
                    dy = (basePosition.Y - player.Position.Y) * (FORMATION_ADHERENCE * 1.8);
                }
                
                // Add natural movement for midfielders
                dx += naturalMovementX * 1.5;
                dy += naturalMovementY * 1.5;
            }
            else if (isForward) {
                // Forwards - first line of defense, pressing and cutting passing lanes
                
                // Check if this forward should press or drop based on team tactics
                bool shouldPress = (playerNumber == 10) || (teamPlayersInAttackingHalf < 3);
                
                if (ballInDefensiveHalf) {
                    // Ball in our half - forwards drop to midfield at most
                    double dropPositionX = isTeamA ? 0.45 : 0.55; // Drop to midfield
                    dx = (dropPositionX - player.Position.X) * (FORMATION_ADHERENCE * 2.5);
                    dy = (basePosition.Y - player.Position.Y) * (FORMATION_ADHERENCE * 1.5);
                }
                else {
                    // Ball in opponent half - press or cut passing lanes
                    if (shouldPress && playerWithBall != null) {
                        double distanceToTarget = Math.Sqrt(
                            Math.Pow(player.Position.X - targetPos.X, 2) +
                            Math.Pow(player.Position.Y - targetPos.Y, 2));
                        
                        if (distanceToTarget < 0.25) { // Within pressing distance
                            // Press intensely
                            dx = (targetPos.X - player.Position.X) * 0.08;
                            dy = (targetPos.Y - player.Position.Y) * 0.08;
                        } else {
                            // Position to cut passing lanes
                            double pressPositionX = isTeamA ? 0.6 : 0.4; // Press position
                            dx = (pressPositionX - player.Position.X) * (FORMATION_ADHERENCE * 2.0);
                            
                            // Position between ball and goal
                            double interceptY = 0.5 + (targetPos.Y - 0.5) * 0.7;
                            dy = (interceptY - player.Position.Y) * (FORMATION_ADHERENCE * 1.5);
                        }
                    } else {
                        // Not pressing - maintain higher position to be ready for counter
                        double counterPositionX = isTeamA ? 0.55 : 0.45; // Just ahead of midfield
                        dx = (counterPositionX - player.Position.X) * (FORMATION_ADHERENCE * 1.8);
                        dy = (basePosition.Y - player.Position.Y) * (FORMATION_ADHERENCE * 1.2);
                    }
                }
                
                // Add natural movement for forwards
                dx += naturalMovementX * 2.0;
                dy += naturalMovementY * 2.0;
            }
            
            // All players: avoid clustering with teammates
            AvoidTeammates(game, player, ref dx, ref dy, isTeamA);
            
            // Small random movement for naturalism - reduced for goalkeepers
            double randomFactor = isGoalkeeper ? 0.1 : 0.2; // Reduced from 0.3
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
            const double MIN_TEAMMATE_DISTANCE = 0.15; // Increased from 0.1 for better spacing
            
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
                    avoidanceStrength = Math.Min(avoidanceStrength * 1.5, 0.8); // Increased avoidance strength cap from 0.5 to 0.8 and multiplier
                    
                    // Add avoidance force (inversely proportional to distance)
                    dx += (player.Position.X - teammate.Position.X) * avoidanceStrength * PLAYER_SPEED * 1.2; // Increased multiplier from 0.8 to 1.2
                    dy += (player.Position.Y - teammate.Position.Y) * avoidanceStrength * PLAYER_SPEED * 1.2; // Increased multiplier from 0.8 to 1.2
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
                    bool isGoalkeeper = playerNumber == 1; // Added check for goalkeeper
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
                    if (isGoalkeeper)
                    {
                        passProbability = 0.9; // Goalkeepers should almost always try to distribute
                        _logger.LogDebug("Goalkeeper has ball - pass probability set to 90%");
                    }
                    else if (isDefender && inDefensiveThird)
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
                            
                            // Ensure player stays in appropriate team half more strictly
                            if (isTeamA) {
                                // Team A (left half) - X should be less than 0.5
                                // Allow slight overlap near centerline but not deep into opponent half
                                player.Position.X = Math.Clamp(player.Position.X, 0.02, 0.48); 
                            } else {
                                // Team B (right half) - X should be greater than 0.5
                                player.Position.X = Math.Clamp(player.Position.X, 0.52, 0.98);
                            }
                            
                            // Ensure player stays in Y-bounds (within field lines)
                            player.Position.Y = Math.Clamp(player.Position.Y, 0.02, 0.98);
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
            
            // If player is a Goalkeeper, they should not move much with the ball, just hold position to distribute.
            if (isGoalkeeper)
            {
                dx = 0; // Goalkeeper holds position X
                dy = 0; // Goalkeeper holds position Y
                
                // Minimal random movement to appear active while waiting for pass opportunity
                dx += (_random.NextDouble() - 0.5) * PLAYER_SPEED * 0.1; 
                dy += (_random.NextDouble() - 0.5) * PLAYER_SPEED * 0.1;

                _logger.LogDebug("Goalkeeper {PlayerId} has possession, holding position to distribute.", player.PlayerId);
            }
            else // Logic for outfield players
            {
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
                
                // Update ball position to match player - This must be outside the if/else for goalkeepers
                game.Ball.Position = player.Position; 
                
                // _logger.LogDebug moved into respective blocks to avoid logging pathBlocked for GKs
                if (isGoalkeeper)
                {
                    _logger.LogDebug("Moved ball possessor (GK) {PlayerId}: dx={DX}, dy={DY}", 
                        player.PlayerId, dx, dy);
                }
                else
                {
                    _logger.LogDebug("Moved ball possessor (Outfield) {PlayerId}: dx={DX}, dy={DY}, pathBlocked={PathBlocked}, inShootingZone={InShootingZone}", 
                        player.PlayerId, dx, dy, pathBlocked, inShootingZone); // pathBlocked and inShootingZone are local to the else block
                }
            }
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