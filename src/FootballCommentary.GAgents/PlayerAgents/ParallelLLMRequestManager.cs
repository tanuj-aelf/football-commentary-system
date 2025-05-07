using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FootballCommentary.Core.Abstractions;
using FootballCommentary.Core.Models;
using Microsoft.Extensions.Logging;

namespace FootballCommentary.GAgents.PlayerAgents
{
    public class ParallelLLMRequestManager
    {
        private readonly SemaphoreSlim _throttler;
        private readonly ILLMService _llmService;
        private readonly ILogger _logger;
        private readonly Dictionary<string, DateTime> _lastApiCallTimes = new();
        private readonly Dictionary<string, (double dx, double dy)> _movementCache = new();
        
        // Time between API calls for the same player - increased due to larger context
        private readonly TimeSpan _minTimeBetweenCalls = TimeSpan.FromSeconds(2);
        
        // Failure tracking
        private readonly Dictionary<string, int> _failureCount = new();
        private const int MAX_FAILURES_BEFORE_TIMEOUT = 4;
        private readonly Dictionary<string, DateTime> _timeoutUntil = new();
        private readonly TimeSpan _timeoutDuration = TimeSpan.FromSeconds(30);
        
        // Configure with max concurrent requests and rate limiting
        public ParallelLLMRequestManager(
            ILLMService llmService, 
            ILogger<ParallelLLMRequestManager> logger,
            int maxConcurrentRequests = 11) // Increased from 7 to 11 for more parallel decision-making
        {
            _throttler = new SemaphoreSlim(maxConcurrentRequests);
            _llmService = llmService;
            _logger = logger;
        }
        
        public async Task<Dictionary<string, (double dx, double dy)>> GetMovementsInParallelAsync(
            FootballCommentary.Core.Models.GameState gameState,
            List<PlayerAgent> prioritizedPlayers)
        {
            _logger.LogDebug("Getting movement decisions for {Count} players in parallel", prioritizedPlayers.Count);
            
            var results = new Dictionary<string, (double dx, double dy)>();
            var tasks = new List<Task<(string playerId, (double dx, double dy) movement)>>();
            
            // Start all tasks in parallel but throttled
            foreach (var player in prioritizedPlayers)
            {
                var playerObj = gameState.AllPlayers.FirstOrDefault(p => p.PlayerId == player.PlayerId);
                if (playerObj == null) continue;
                
                // Skip players that are in timeout due to repeated failures
                if (_timeoutUntil.TryGetValue(player.PlayerId, out var timeoutEnd) && 
                    DateTime.UtcNow < timeoutEnd)
                {
                    _logger.LogDebug("Player {PlayerId} is in timeout until {TimeoutEnd}", 
                        player.PlayerId, timeoutEnd);
                    
                    // Use cached movement for players in timeout
                    if (_movementCache.TryGetValue(player.PlayerId, out var cachedMovement))
                    {
                        results[player.PlayerId] = AddVariation(cachedMovement);
                    }
                    
                    continue;
                }
                
                tasks.Add(ProcessPlayerAsync(player, gameState, playerObj));
            }
            
            try
            {
                // Wait for all tasks to complete with timeout
                await Task.WhenAll(tasks);
                
                // Collect results
                foreach (var task in tasks)
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        var (playerId, movement) = task.Result;
                        results[playerId] = movement;
                        
                        // Update cache
                        _movementCache[playerId] = movement;
                        
                        // Reset failure count on success
                        _failureCount[playerId] = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing player movement requests in parallel");
            }
            
            return results;
        }
        
        private async Task<(string playerId, (double dx, double dy) movement)> ProcessPlayerAsync(
            PlayerAgent player, 
            FootballCommentary.Core.Models.GameState gameState,
            Player playerObj)
        {
            try
            {
                // Check if we're throttling this player's API calls
                bool useCache = false;
                if (_lastApiCallTimes.TryGetValue(player.PlayerId, out var lastCallTime))
                {
                    var timeSinceLastCall = DateTime.UtcNow - lastCallTime;
                    useCache = timeSinceLastCall < _minTimeBetweenCalls;
                }
                
                // If throttled and we have cached movement, use that
                if (useCache && _movementCache.TryGetValue(player.PlayerId, out var cachedMovement))
                {
                    _logger.LogDebug("Using cached movement for player {PlayerId} due to rate limiting", player.PlayerId);
                    return (player.PlayerId, AddVariation(cachedMovement));
                }
                
                // Check if this player has the ball
                bool hasPossession = gameState.BallPossession == player.PlayerId;
                
                // Acquire throttling semaphore
                await _throttler.WaitAsync();
                try
                {
                    // Log more details about the player making the API call
                    _logger.LogDebug("Making LLM API call for {Role} {PlayerId}, has ball: {HasBall}, position: ({X},{Y})", 
                        player.Role, player.PlayerId, hasPossession, 
                        playerObj.Position.X.ToString("F2"), playerObj.Position.Y.ToString("F2"));
                    
                    // Record this API call
                    _lastApiCallTimes[player.PlayerId] = DateTime.UtcNow;
                    
                    // Get player's movement decision
                    var movement = await player.GetMovementDecisionAsync(
                        gameState,
                        hasPossession,
                        playerObj.Position,
                        gameState.Ball.Position);
                    
                    _logger.LogDebug("Player {PlayerId} decided movement: ({DX},{DY})", 
                        player.PlayerId, movement.dx.ToString("F3"), movement.dy.ToString("F3"));
                    
                    return (player.PlayerId, movement);
                }
                catch (Exception ex)
                {
                    // Track failures
                    if (!_failureCount.ContainsKey(player.PlayerId))
                    {
                        _failureCount[player.PlayerId] = 0;
                    }
                    
                    _failureCount[player.PlayerId]++;
                    
                    // If player has failed too many times, put them in timeout
                    if (_failureCount[player.PlayerId] >= MAX_FAILURES_BEFORE_TIMEOUT)
                    {
                        _timeoutUntil[player.PlayerId] = DateTime.UtcNow.Add(_timeoutDuration);
                        _logger.LogWarning("Player {PlayerId} has failed {FailCount} times and is now in timeout until {TimeoutEnd}",
                            player.PlayerId, _failureCount[player.PlayerId], _timeoutUntil[player.PlayerId]);
                    }
                    
                    throw; // Re-throw to be caught by caller
                }
                finally
                {
                    _throttler.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing player {PlayerId}", player.PlayerId);
                
                // Fall back to cached movement or random small movement
                if (_movementCache.TryGetValue(player.PlayerId, out var fallbackMovement))
                {
                    return (player.PlayerId, AddVariation(fallbackMovement));
                }
                
                // Generate a small random movement as last resort
                var random = new Random();
                return (player.PlayerId, (
                    (random.NextDouble() - 0.5) * 0.02,
                    (random.NextDouble() - 0.5) * 0.02
                ));
            }
        }
        
        private (double dx, double dy) AddVariation((double dx, double dy) movement)
        {
            // Add small random variations to make cached movements more natural
            var random = new Random();
            double dx = movement.dx + (random.NextDouble() - 0.5) * 0.01;
            double dy = movement.dy + (random.NextDouble() - 0.5) * 0.01;
            
            return (Math.Clamp(dx, -0.1, 0.1), Math.Clamp(dy, -0.1, 0.1));
        }
    }
} 