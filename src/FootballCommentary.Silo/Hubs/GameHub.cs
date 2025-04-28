using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Generic;
using FootballCommentary.Core.Models;
using FootballCommentary.Core.Abstractions;
using Orleans;
using System;
using Microsoft.Extensions.Logging;
using Orleans.Streams;
using System.Threading;
using System.Linq;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace FootballCommentary.Silo.Hubs
{
    // Static class to manage background polling tasks that are independent of hub instances
    public static class CommentaryPollingManager
    {
        private static readonly Dictionary<string, CancellationTokenSource> _pollingCancellationTokens = new();
        private static readonly Dictionary<string, Task> _pollingTasks = new();
        private static readonly object _lock = new object();
        
        public static void StartPolling(string gameId, string grainId, IClusterClient clusterClient, 
                                      ILogger logger, IHubContext<GameHub> hubContext)
        {
            lock (_lock)
            {
                // Cancel existing task if any
                StopPolling(gameId);
                
                // Create a new cancellation token source
                var cts = new CancellationTokenSource();
                _pollingCancellationTokens[gameId] = cts;
                
                // Create and start a new task
                var task = Task.Run(async () =>
                {
                    try
                    {
                        logger.LogInformation("Commentary polling started for game {GameId}", gameId);
                        
                        // Store the latest commentary timestamp to avoid duplicates
                        DateTime lastCommentaryTime = DateTime.UtcNow;
                        
                        var commentaryAgent = clusterClient.GetGrain<ICommentaryAgent>(grainId);
                        
                        while (!cts.Token.IsCancellationRequested)
                        {
                            try
                            {
                                // Get recent commentary
                                var recentCommentary = await commentaryAgent.GetRecentCommentaryAsync(gameId, 5);
                                
                                // Filter for only new messages
                                var newMessages = recentCommentary.Where(c => c.Timestamp > lastCommentaryTime).ToList();
                                
                                if (newMessages.Any())
                                {
                                    // Update timestamp
                                    lastCommentaryTime = newMessages.Max(c => c.Timestamp);
                                    
                                    // Send each new message
                                    foreach (var message in newMessages.OrderBy(c => c.Timestamp))
                                    {
                                        try 
                                        {
                                            // Use the hub context directly - it's thread-safe and designed for this scenario
                                            await hubContext.Clients.Group(gameId).SendAsync("ReceiveCommentary", message, cts.Token);
                                        }
                                        catch (Exception ex)
                                        {
                                            // Log but continue - don't let communication errors stop the polling
                                            logger.LogError(ex, "Error sending commentary to clients for game {GameId}: {Message}", gameId, ex.Message);
                                        }
                                    }
                                }
                                
                                // Wait before polling again
                                await Task.Delay(2000, cts.Token);
                            }
                            catch (TaskCanceledException)
                            {
                                // Normal cancellation, just exit
                                break;
                            }
                            catch (Exception ex)
                            {
                                logger.LogError(ex, "Error polling commentary for game {GameId}: {Message}", gameId, ex.Message);
                                
                                // Check if we need to exit
                                if (cts.Token.IsCancellationRequested)
                                {
                                    break;
                                }
                                
                                // Wait before retrying
                                await Task.Delay(5000, cts.Token);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error in commentary polling task for game {GameId}: {Message}", gameId, ex.Message);
                    }
                    finally
                    {
                        logger.LogInformation("Commentary polling stopped for game {GameId}", gameId);
                    }
                });
                
                _pollingTasks[gameId] = task;
            }
        }
        
        public static void StopPolling(string gameId)
        {
            lock (_lock)
            {
                // Cancel and remove any existing task
                if (_pollingCancellationTokens.TryGetValue(gameId, out var cts))
                {
                    cts.Cancel();
                    _pollingCancellationTokens.Remove(gameId);
                }
                
                // Remove the task reference
                if (_pollingTasks.ContainsKey(gameId))
                {
                    _pollingTasks.Remove(gameId);
                }
            }
        }
    }

    public class GameHub : Hub
    {
        private readonly IClusterClient _clusterClient;
        private readonly ILogger<GameHub> _logger;
        private readonly IHubContext<GameHub> _hubContext;
        private static Dictionary<string, List<string>> _gameConnections = new();
        // Dictionary to map game IDs to grain IDs
        private static Dictionary<string, string> _gameToGrainMap = new();
        private static readonly object _subscriptionLock = new object();
        private static readonly Dictionary<string, StreamSubscriptionHandle<GameStateUpdate>> _gameStateSubscriptions = new();
        // Add a dictionary to store GameEvent stream subscriptions
        private static readonly Dictionary<string, StreamSubscriptionHandle<GameEvent>> _gameEventSubscriptions = new();

        public GameHub(IClusterClient clusterClient, ILogger<GameHub> logger, IHubContext<GameHub> hubContext)
        {
            _clusterClient = clusterClient;
            _logger = logger;
            _hubContext = hubContext;
        }

        public override async Task OnConnectedAsync()
        {
            _logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            try
            {
                _logger.LogInformation("Client disconnected: {ConnectionId}, Exception: {Exception}", 
                    Context.ConnectionId, exception?.Message ?? "None");
                
                // Remove client from game groups
                foreach (var gameId in _gameConnections.Keys.ToList())
                {
                    if (_gameConnections[gameId]?.Contains(Context.ConnectionId) == true)
                    {
                        _gameConnections[gameId].Remove(Context.ConnectionId);
                        
                        try 
                        {
                            await Groups.RemoveFromGroupAsync(Context.ConnectionId, gameId);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error removing client {ConnectionId} from group {GameId}", Context.ConnectionId, gameId);
                            // Continue cleanup despite errors
                        }
                        
                        // If no clients are left in the game, stop any polling tasks
                        if (_gameConnections[gameId].Count == 0)
                        {
                            _logger.LogInformation("No clients left in game {GameId}, stopping polling", gameId);
                            CommentaryPollingManager.StopPolling(gameId);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in OnDisconnectedAsync for client {ConnectionId}", Context.ConnectionId);
            }
            
            try
            {
                await base.OnDisconnectedAsync(exception);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in base.OnDisconnectedAsync for client {ConnectionId}", Context.ConnectionId);
            }
        }

        public async Task JoinGame(string gameId)
        {
            try
            {
                if (!_gameToGrainMap.TryGetValue(gameId, out var grainId))
                {
                    _logger.LogError("No grain ID found for game {GameId}", gameId);
                    await Clients.Caller.SendAsync("Error", $"Game {gameId} not found");
                    return;
                }
                
                // Add the client to the game group
                await Groups.AddToGroupAsync(Context.ConnectionId, gameId);
                
                // Add the connection ID to the game connection list
                if (!_gameConnections.ContainsKey(gameId))
                {
                    _gameConnections[gameId] = new List<string>();
                }
                _gameConnections[gameId].Add(Context.ConnectionId);
                
                // Get the game state
                var gameStateAgent = _clusterClient.GetGrain<IGameStateAgent>(grainId);
                var gameState = await gameStateAgent.GetGameStateAsync(gameId);
                
                // Ensure Players lists are initialized and populated
                if (gameState.HomeTeam?.Players == null || gameState.HomeTeam.Players.Count == 0)
                {
                    _logger.LogWarning("Home team players list is null or empty for game {GameId}", gameId);
                    if (gameState.HomeTeam != null) gameState.HomeTeam.Players = new List<Player>();
                }
                
                if (gameState.AwayTeam?.Players == null || gameState.AwayTeam.Players.Count == 0)
                {
                    _logger.LogWarning("Away team players list is null or empty for game {GameId}", gameId);
                    if (gameState.AwayTeam != null) gameState.AwayTeam.Players = new List<Player>();
                }
                
                // Send the current game state to the caller
                await Clients.Caller.SendAsync("ReceiveGameState", gameState);
                
                // Get recent commentary
                var commentaryAgent = _clusterClient.GetGrain<ICommentaryAgent>(grainId);
                var recentCommentary = await commentaryAgent.GetRecentCommentaryAsync(gameId, 10);
                if (recentCommentary.Count > 0)
                {
                    await Clients.Caller.SendAsync("ReceiveCommentary", recentCommentary);
                }
                
                // Log the connection
                _logger.LogInformation("Client {ConnectionId} joined game {GameId}", Context.ConnectionId, gameId);
                
                // Subscribe to game streams
                SubscribeToStreams(gameId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error joining game {GameId}: {Message}", gameId, ex.Message);
                await Clients.Caller.SendAsync("Error", "Failed to join game: " + ex.Message);
            }
        }

        public async Task LeaveGame(string gameId)
        {
            if (string.IsNullOrEmpty(gameId))
                return;
                
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, gameId);
            
            if (_gameConnections.ContainsKey(gameId))
            {
                _gameConnections[gameId].Remove(Context.ConnectionId);
            }
        }

        public async Task<string> CreateGame(string homeTeam, string awayTeam)
        {
            try
            {
                _logger.LogInformation("Creating game between {HomeTeam} and {AwayTeam}", homeTeam, awayTeam);
                
                // Create a new grain ID
                var grainId = Guid.NewGuid().ToString();
                
                // Get reference to the game state grain
                var gameStateAgent = _clusterClient.GetGrain<IGameStateAgent>(grainId);
                
                // Create the game
                var gameState = await gameStateAgent.CreateGameAsync(homeTeam, awayTeam);
                
                // Store the mapping
                _gameToGrainMap[gameState.GameId] = grainId;
                
                // Log the game and grain IDs
                _logger.LogInformation("Game created with ID {GameId}, mapped to grain {GrainId}", 
                    gameState.GameId, grainId);
                
                // Send initial game state to the caller
                await Clients.Caller.SendAsync("GameCreated", gameState.GameId, gameState);
                
                // Start commentary polling
                CommentaryPollingManager.StartPolling(gameState.GameId, grainId, _clusterClient, _logger, _hubContext);
                
                return gameState.GameId;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating game: {Message}", ex.Message);
                await Clients.Caller.SendAsync("Error", "Failed to create game: " + ex.Message);
                throw;
            }
        }

        public async Task StartGame(string gameId)
        {
            try
            {
                if (!_gameToGrainMap.TryGetValue(gameId, out var grainId))
                {
                    _logger.LogError("No grain ID found for game {GameId}", gameId);
                    await Clients.Caller.SendAsync("Error", $"Game {gameId} not found");
                    return;
                }
                
                var gameStateAgent = _clusterClient.GetGrain<IGameStateAgent>(grainId);
                var gameState = await gameStateAgent.StartGameAsync(gameId);
                
                await Clients.Group(gameId).SendAsync("GameStateUpdated", gameState);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting game {GameId}: {Message}", gameId, ex.Message);
                await Clients.Caller.SendAsync("Error", "Failed to start game: " + ex.Message);
            }
        }

        public async Task EndGame(string gameId)
        {
            try
            {
                if (!_gameToGrainMap.TryGetValue(gameId, out var grainId))
                {
                    _logger.LogError("No grain ID found for game {GameId}", gameId);
                    await Clients.Caller.SendAsync("Error", $"Game {gameId} not found");
                    return;
                }
                
                var gameStateAgent = _clusterClient.GetGrain<IGameStateAgent>(grainId);
                var gameState = await gameStateAgent.EndGameAsync(gameId);
                
                // Stop commentary polling for this game
                CommentaryPollingManager.StopPolling(gameId);

                await Clients.Group(gameId).SendAsync("GameStateUpdated", gameState);
                
                // Get commentary agent reference before calling it
                var commentaryAgent = _clusterClient.GetGrain<ICommentaryAgent>(grainId);
                await commentaryAgent.GenerateGameSummaryAsync(gameId);
                
                _logger.LogInformation("Game {GameId} ended by hub request.", gameId);
                
                // Optionally notify clients game has ended
                await Clients.Group(gameId).SendAsync("GameEnded", gameId);
                
                // --- Unsubscribe from streams on game end --- 
                StreamSubscriptionHandle<GameStateUpdate>? subscriptionHandle = null;
                lock (_subscriptionLock)
                {
                    if (_gameStateSubscriptions.TryGetValue(gameId, out subscriptionHandle))
                    {
                        _gameStateSubscriptions.Remove(gameId);
                    }
                }
                // Unsubscribe outside the lock
                if (subscriptionHandle != null) 
                {
                    try 
                    {
                        await subscriptionHandle.UnsubscribeAsync();
                        _logger.LogInformation("Successfully unsubscribed from GameState stream on EndGame for {GameId}", gameId);
                    }
                    catch (Exception unsubEx)
                    {
                         _logger.LogWarning(unsubEx, "Error unsubscribing from GameState stream on EndGame for {GameId}", gameId);
                    }
                }
                CommentaryPollingManager.StopPolling(gameId); // Stop commentary polling too
                 // -------------------------------------------
                 
                // --- Unsubscribe from GameEvent stream on EndGame --- 
                StreamSubscriptionHandle<GameEvent>? gameEventSubscriptionHandle = null;
                lock (_subscriptionLock)
                {
                    if (_gameEventSubscriptions.TryGetValue(gameId, out gameEventSubscriptionHandle))
                    {
                        _gameEventSubscriptions.Remove(gameId);
                    }
                }
                if (gameEventSubscriptionHandle != null)
                {
                    try
                    {
                        await gameEventSubscriptionHandle.UnsubscribeAsync();
                        _logger.LogInformation("Successfully unsubscribed from GameEvent stream on EndGame for {GameId}", gameId);
                    }
                    catch (Exception unsubEx)
                    {
                        _logger.LogWarning(unsubEx, "Error unsubscribing from GameEvent stream on EndGame for {GameId}", gameId);
                    }
                }
                // -------------------------------------------------
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error ending game {GameId}: {Message}", gameId, ex.Message);
                await Clients.Caller.SendAsync("Error", "Failed to end game: " + ex.Message);
            }
        }

        public async Task KickBall(string gameId)
        {
            try
            {
                if (!_gameToGrainMap.TryGetValue(gameId, out var grainId))
                {
                    _logger.LogError("No grain ID found for game {GameId}", gameId);
                    await Clients.Caller.SendAsync("Error", $"Game {gameId} not found");
                    return;
                }
                
                var gameStateAgent = _clusterClient.GetGrain<IGameStateAgent>(grainId);
                await gameStateAgent.KickBallAsync(gameId);
                
                // Get updated game state and send to clients
                var gameState = await gameStateAgent.GetGameStateAsync(gameId);
                await Clients.Group(gameId).SendAsync("GameStateUpdated", gameState);
                
                // NEW: Manually fetch and broadcast latest commentary after action
                var commentaryAgent = _clusterClient.GetGrain<ICommentaryAgent>(grainId);
                var recentCommentary = await commentaryAgent.GetRecentCommentaryAsync(gameId, 1);
                if (recentCommentary.Count > 0)
                {
                    await Clients.Group(gameId).SendAsync("ReceiveCommentary", recentCommentary[0]);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error kicking ball in game {GameId}: {Message}", gameId, ex.Message);
                await Clients.Caller.SendAsync("Error", "Failed to kick ball: " + ex.Message);
            }
        }

        public async Task SimulateGoal(string gameId, string teamId, int playerId)
        {
            try
            {
                if (!_gameToGrainMap.TryGetValue(gameId, out var grainId))
                {
                    _logger.LogError("No grain ID found for game {GameId}", gameId);
                    await Clients.Caller.SendAsync("Error", $"Game {gameId} not found");
                    return;
                }
                
                _logger.LogInformation("Simulating goal for game {GameId}, team {TeamId}, player {PlayerId}", gameId, teamId, playerId);
                
                var gameStateAgent = _clusterClient.GetGrain<IGameStateAgent>(grainId);
                await gameStateAgent.SimulateGoalAsync(gameId, teamId, playerId);
                
                // Get updated game state and send to clients
                var gameState = await gameStateAgent.GetGameStateAsync(gameId);
                await Clients.Group(gameId).SendAsync("GameStateUpdated", gameState);
                
                // Fetch and broadcast latest commentary after goal
                var commentaryAgent = _clusterClient.GetGrain<ICommentaryAgent>(grainId);
                var recentCommentary = await commentaryAgent.GetRecentCommentaryAsync(gameId, 1);
                if (recentCommentary.Count > 0)
                {
                    await Clients.Group(gameId).SendAsync("ReceiveCommentary", recentCommentary[0]);
                }
                
                // Log the player name from PlayerData
                string playerName = FootballCommentary.Core.Models.PlayerData.GetPlayerName(teamId, playerId);
                _logger.LogInformation("Goal scored by {PlayerName} (ID: {PlayerId}) for team {TeamId}", 
                    playerName, playerId, teamId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error simulating goal in game {GameId}: {Message}", gameId, ex.Message);
                await Clients.Caller.SendAsync("Error", "Failed to simulate goal: " + ex.Message);
            }
        }

        private void SubscribeToStreams(string gameId)
        {
            try
            {
                // Get the grainId for the game
                if (!_gameToGrainMap.TryGetValue(gameId, out var grainId))
                {
                    _logger.LogError("Cannot subscribe to streams: No grain ID found for game {GameId}", gameId);
                    return;
                }
                
                _logger.LogInformation("Attempting to subscribe to streams for GameId: {GameId}, GrainId: {GrainId}", gameId, grainId);
                
                // Subscribe to GameEvent stream
                lock (_subscriptionLock)
                {
                    if (_gameEventSubscriptions.ContainsKey(gameId))
                    {
                        _logger.LogInformation("Already subscribed to GameEvent stream for {GameId}", gameId);
                        return; // Already subscribed
                    }
                    // Placeholder to prevent race conditions
                    _gameEventSubscriptions[gameId] = null; 
                }
                
                var streamProvider = _clusterClient.GetStreamProvider("GameEvents");
                var gameEventStream = streamProvider.GetStream<GameEvent>(StreamId.Create("GameEvents", grainId));
                
                // Asynchronously subscribe
                Task.Run(async () =>
                {
                    try 
                    {
                        var subscriptionHandle = await gameEventStream.SubscribeAsync(
                            (gameEvent, token) => OnGameEventReceived(gameEvent));
                        
                        // Store the valid handle
                        lock (_subscriptionLock)
                        {
                             _gameEventSubscriptions[gameId] = subscriptionHandle;
                        }
                        _logger.LogInformation("Successfully subscribed to GameEvent stream for {GameId}", gameId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to subscribe to GameEvent stream for {GameId}", gameId);
                        // Remove placeholder if subscription failed
                        lock (_subscriptionLock)
                        {
                            _gameEventSubscriptions.Remove(gameId); 
                        }
                    }
                });

                // NOTE: We are not subscribing to GameState updates via stream here,
                // as the hub methods (StartGame, KickBall, SimulateGoal, GetGameState)
                // fetch the state and push it manually after actions.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error subscribing to streams for game {GameId}: {Message}", gameId, ex.Message);
            }
        }

        // Handler for receiving GameEvents from the Orleans stream
        private async Task OnGameEventReceived(GameEvent gameEvent)
        {
            try
            {
                _logger.LogInformation("Received GameEvent from stream: Type={EventType}, GameId={GameId}", 
                    gameEvent.EventType, gameEvent.GameId);
                    
                // Forward the event to the relevant SignalR group
                await _hubContext.Clients.Group(gameEvent.GameId).SendAsync("ReceiveGameEvent", gameEvent);
            }
            catch (Exception ex)
            {
                 _logger.LogError(ex, "Error forwarding GameEvent to SignalR clients for GameId {GameId}", gameEvent.GameId);
            }
        }

        public async Task<FootballCommentary.Core.Models.GameState?> GetGameState(string gameId)
        {
            try
            {
                if (!_gameToGrainMap.TryGetValue(gameId, out var grainId))
                {
                    _logger.LogWarning("No grain ID found for game {GameId}", gameId);
                    await Clients.Caller.SendAsync("Error", $"Game {gameId} not found");
                    return null;
                }
                
                var gameStateAgent = _clusterClient.GetGrain<IGameStateAgent>(grainId);
                var gameState = await gameStateAgent.GetGameStateAsync(gameId);
                
                // Send the updated state to the requesting client only
                await Clients.Caller.SendAsync("GameStateUpdated", gameState);
                
                return gameState;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting game state for game {GameId}: {Message}", gameId, ex.Message);
                await Clients.Caller.SendAsync("Error", "Failed to get game state: " + ex.Message);
                return null;
            }
        }
    }
} 