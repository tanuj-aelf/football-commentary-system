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

namespace FootballCommentary.GAgents.GameState
{
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
    }

    public class GameStateGAgent : Grain, IGameStateAgent
    {
        private readonly ILogger<GameStateGAgent> _logger;
        private readonly IPersistentState<GameStateGAgentState> _state;
        private readonly Random _random = new Random();
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
            [PersistentState("gameState", "Default")] IPersistentState<GameStateGAgentState> state)
        {
            _logger = logger;
            _state = state;
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
        
        public async Task SimulateGoalAsync(string gameId, string teamId)
        {
            if (!_state.State.Games.TryGetValue(gameId, out var game))
            {
                throw new KeyNotFoundException($"Game {gameId} not found");
            }
            
            // Increment the score for the scoring team
            if (teamId == "TeamA")
            {
                game.HomeTeam.Score++;
            }
            else if (teamId == "TeamB")
            {
                game.AwayTeam.Score++;
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
                Position = new Position { X = teamId == "TeamA" ? 0.9 : 0.1, Y = 0.5 }
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
                double x = teamId == "TeamA" ? 0.2 : 0.8;
                double y = 0.1 + (0.8 * i / (count - 1));
                
                players.Add(new Player
                {
                    PlayerId = $"{teamId}_Player{i}",
                    Name = $"Player {i}",
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
            var timer = this.RegisterTimer(
                async _ => await SimulateGameStepAsync(gameId),
                null,
                TimeSpan.FromMilliseconds(100),  // start after 100ms
                TimeSpan.FromMilliseconds(100)); // run every 100ms
            
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
                
                if (game.Status != GameStatus.InProgress)
                {
                    return;
                }
                
                // Update game time
                var elapsed = DateTime.UtcNow - game.GameStartTime;
                game.GameTime = elapsed;
                
                // Randomly decide if we should publish an event
                bool publishEvent = false;
                GameEvent? gameEvent = null;
                
                // Move players
                MoveAllPlayers(game);
                
                // Update ball based on possession
                UpdateBallPosition(game, out publishEvent, out gameEvent);
                
                // Save game state
                game.LastUpdateTime = DateTime.UtcNow;
                await _state.WriteStateAsync();
                
                // Publish state update every 5 steps (500ms)
                if (game.SimulationStep % 5 == 0)
                {
                    await PublishGameStateUpdateAsync(game);
                }
                
                // Publish game event if needed
                if (publishEvent && gameEvent != null)
                {
                    await PublishGameEventAsync(gameEvent);
                }
                
                // Increment simulation step
                game.SimulationStep++;
                
                // End game after 5 minutes (game time)
                if (game.GameTime.TotalMinutes >= 5 && game.Status == GameStatus.InProgress)
                {
                    await EndGameAsync(gameId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during game simulation for game {GameId}: {Message}", gameId, ex.Message);
            }
        }
        
        private void MoveAllPlayers(FootballCommentary.Core.Models.GameState game)
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

            // Move each player based on their team's tactical objectives
            foreach (var player in allPlayers)
            {
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
                Position basePosition = GetBasePositionForRole(playerNumber, isTeamA);
                
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
        
        private Position GetBasePositionForRole(int playerNumber, bool isTeamA)
        {
            // Assign roles based on player numbers (1-11)
            // 1: Goalkeeper
            // 2-5: Defenders
            // 6-8: Midfielders
            // 9-11: Forwards
            double x, y;
            
            if (playerNumber == 1) // Goalkeeper
            {
                x = isTeamA ? 0.1 : 0.9;
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
        
        private void MovePlayerWhenTeamHasPossession(
            FootballCommentary.Core.Models.GameState game, 
            Player player, 
            Player? playerWithBall, 
            Position basePosition,
            bool isTeamA)
        {
            // Players move more dynamically when their team has possession
            
            // 1. Calculate movement toward base position (formation adherence)
            double dx = (basePosition.X - player.Position.X) * FORMATION_ADHERENCE;
            double dy = (basePosition.Y - player.Position.Y) * FORMATION_ADHERENCE;
            
            // 2. Add forward movement bias for attacking team
            double forwardBias = isTeamA ? PLAYER_SPEED * 0.4 : -PLAYER_SPEED * 0.4;
            dx += forwardBias;
            
            // 3. Movement toward creating space for passing lanes
            if (playerWithBall != null)
            {
                // Calculate vector from ball player to this player
                double ballToPlayerX = player.Position.X - playerWithBall.Position.X;
                double ballToPlayerY = player.Position.Y - playerWithBall.Position.Y;
                
                // Normalize the vector
                double distance = Math.Sqrt(ballToPlayerX * ballToPlayerX + ballToPlayerY * ballToPlayerY);
                
                if (distance > 0 && distance < PASSING_DISTANCE)
                {
                    // Move to create better passing angles
                    double moveX = ballToPlayerX / distance * PLAYER_SPEED * 0.5;
                    double moveY = ballToPlayerY / distance * PLAYER_SPEED * 0.5;
                    
                    // Adjust to maintain forward progress
                    moveX = isTeamA ? Math.Max(0, moveX) : Math.Min(0, moveX);
                    
                    dx += moveX;
                    dy += moveY;
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
            
            // 1. Move toward defensive position
            double defX = isTeamA ? 0.3 : 0.7; // Defensive line position
            double dx = (defX - player.Position.X) * POSITION_RECOVERY_WEIGHT;
            double dy = (basePosition.Y - player.Position.Y) * POSITION_RECOVERY_WEIGHT;
            
            // 2. Add attraction to the ball when it's close
            if (playerWithBall != null)
            {
                double distToBall = Math.Sqrt(
                    Math.Pow(player.Position.X - game.Ball.Position.X, 2) +
                    Math.Pow(player.Position.Y - game.Ball.Position.Y, 2));
                
                if (distToBall < OPPONENT_AWARENESS_DISTANCE)
                {
                    double ballBiasX = (game.Ball.Position.X - player.Position.X) * BALL_ATTRACTION_WEIGHT;
                    double ballBiasY = (game.Ball.Position.Y - player.Position.Y) * BALL_ATTRACTION_WEIGHT;
                    
                    dx += ballBiasX;
                    dy += ballBiasY;
                }
            }
            
            // 3. Add small random movement for naturalism
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
            bool canShoot = false;
            double goalPostX = isTeamA ? GOAL_POST_X_TEAM_B : GOAL_POST_X_TEAM_A;
            double distanceToGoal = Math.Abs(playerWithBall.Position.X - goalPostX);
            
            if (distanceToGoal < SHOOTING_DISTANCE)
            {
                canShoot = true;
                
                // Try to score
                if (_random.NextDouble() < 0.4) // 40% chance to score when shooting
                {
                    // Goal!
                    if (isTeamA)
                    {
                        game.HomeTeam.Score++;
                    }
                    else
                    {
                        game.AwayTeam.Score++;
                    }
                    
                    // Reset ball and positions
                    game.Ball.Position = new Position { X = 0.5, Y = 0.5 };
                    game.BallPossession = string.Empty;
                    
                    // Publish goal event
                    publishEvent = true;
                    gameEvent = new GameEvent
                    {
                        GameId = game.GameId,
                        EventType = GameEventType.Goal,
                        TeamId = isTeamA ? "TeamA" : "TeamB",
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
                    Position = playerWithBall.Position
                };
            }
        }
    }
} 