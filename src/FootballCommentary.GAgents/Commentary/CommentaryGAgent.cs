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

namespace FootballCommentary.GAgents.Commentary
{
    [GenerateSerializer]
    public class CommentaryLogEvent
    {
        [Id(0)] public string Message { get; set; } = string.Empty;
        [Id(1)] public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    [GenerateSerializer]
    public class CommentaryGAgentState
    {
        [Id(0)] public Dictionary<string, List<CommentaryMessage>> GameCommentary { get; set; } = new();
        [Id(1)] public Dictionary<string, FootballCommentary.Core.Models.GameState> GameStates { get; set; } = new();
        [Id(2)] public DateTime LastCommentaryTime { get; set; } = DateTime.MinValue;
        [Id(3)] public List<CommentaryLogEvent> LogEvents { get; set; } = new();
        [Id(4)] public bool IsGameEnded { get; set; } = false;
    }

    public class CommentaryGAgent : Grain, ICommentaryAgent
    {
        private readonly ILogger<CommentaryGAgent> _logger;
        private readonly ILLMService _llmService;
        private readonly IPersistentState<CommentaryGAgentState> _state;
        private readonly IGrainFactory _grainFactory;
        private readonly Random _random = new Random();
        
        // Throttling settings
        private static readonly TimeSpan CommentaryThrottleInterval = TimeSpan.FromSeconds(7);
        private static readonly HashSet<GameEventType> ThrottledEventTypes = new HashSet<GameEventType>
        {
            GameEventType.Pass,
            GameEventType.Shot,
            GameEventType.Tackle,
            GameEventType.Save,
            GameEventType.OutOfBounds // Add other frequent, less critical events if needed
        };

        private IStreamProvider? _streamProvider;
        private IDisposable? _backgroundCommentaryTimer;
        private StreamSubscriptionHandle<GameEvent>? _gameEventSubscription;
        private StreamSubscriptionHandle<GameStateUpdate>? _gameStateSubscription;
        
        public CommentaryGAgent(
            ILogger<CommentaryGAgent> logger,
            ILLMService llmService,
            [PersistentState("commentary", "Default")] IPersistentState<CommentaryGAgentState> state,
            IGrainFactory grainFactory)
        {
            _logger = logger;
            _llmService = llmService;
            _state = state;
            _grainFactory = grainFactory;
        }
        
        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            await base.OnActivateAsync(cancellationToken);
            
            _streamProvider = this.GetStreamProvider("GameEvents");
            _logger.LogInformation("CommentaryGAgent activated with key: {Key}", this.GetPrimaryKeyString());
            
            try
            {
                // Subscribe to game events
                var eventStreamId = StreamId.Create("GameEvents", this.GetPrimaryKeyString());
                _gameEventSubscription = await _streamProvider.GetStream<GameEvent>(eventStreamId)
                    .SubscribeAsync(OnGameEventAsync);
                
                // Subscribe to game state updates
                var stateStreamId = StreamId.Create("GameState", this.GetPrimaryKeyString());
                _gameStateSubscription = await _streamProvider.GetStream<GameStateUpdate>(stateStreamId)
                    .SubscribeAsync(OnGameStateUpdateAsync);
                
                _logger.LogInformation("Successfully subscribed to game event and state streams");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error subscribing to streams: {Message}", ex.Message);
            }
            
            // Start background commentary timer using the newer RegisterGrainTimer
            var timerOptions = new GrainTimerCreationOptions
            {
                DueTime = TimeSpan.FromMinutes(1),
                Period = TimeSpan.FromMinutes(3),
                Interleave = true // Allow timer callbacks to interleave with grain calls
            };
            _backgroundCommentaryTimer = this.RegisterGrainTimer(
                async () => await GenerateBackgroundCommentaryAsync(this.GetPrimaryKeyString()),
                timerOptions);
        }
        
        public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            if (_gameEventSubscription != null)
                await _gameEventSubscription.UnsubscribeAsync();
                
            if (_gameStateSubscription != null)
                await _gameStateSubscription.UnsubscribeAsync();
                
            _backgroundCommentaryTimer?.Dispose();
            
            await base.OnDeactivateAsync(reason, cancellationToken);
        }
        
        private async Task OnGameEventAsync(GameEvent gameEvent, StreamSequenceToken? token = null)
        {
            _logger.LogInformation("Received game event: {EventType} for game {GameId}", 
                gameEvent.EventType, gameEvent.GameId);

            // Check if game already ended (robustness check)
            if (_state.State.IsGameEnded && gameEvent.EventType != GameEventType.GameEnd) 
            {
                _logger.LogWarning("Game {GameId} already ended (state flag), skipping event {EventType}.", gameEvent.GameId, gameEvent.EventType);
                return;
            }

            // Handle Game End specifically
            if (gameEvent.EventType == GameEventType.GameEnd)
            {
                _logger.LogInformation("Game {GameId} ended. Setting flag and initiating cleanup.", gameEvent.GameId);
                if (!_state.State.IsGameEnded)
                {
                   _state.State.IsGameEnded = true;
                   await _state.WriteStateAsync(); 
                   _logger.LogInformation("IsGameEnded flag set to true for game {GameId}.", gameEvent.GameId);
                }
                
                var finalCommentary = await GenerateEventCommentaryAsync(gameEvent); // Generate final commentary
                if (finalCommentary != null && !string.IsNullOrEmpty(finalCommentary.Text)) 
                {
                    await StoreAndPublishCommentary(gameEvent.GameId, finalCommentary);
                }

                // Cleanup resources
                _backgroundCommentaryTimer?.Dispose();
                _backgroundCommentaryTimer = null;
                if (_gameEventSubscription != null) { try { await _gameEventSubscription.UnsubscribeAsync(); } catch { /* Ignore */ } _gameEventSubscription = null; }
                if (_gameStateSubscription != null) { try { await _gameStateSubscription.UnsubscribeAsync(); } catch { /* Ignore */ } _gameStateSubscription = null; }
                _logger.LogInformation("Cleanup complete for ended game {GameId}.", gameEvent.GameId);
                
                // DeactivateOnIdle(); 
                return; 
            }
                
            // --- Throttling Logic for other events ---
            bool shouldGenerate = true;
            if (ThrottledEventTypes.Contains(gameEvent.EventType))
            {
                if ((DateTime.UtcNow - _state.State.LastCommentaryTime) < CommentaryThrottleInterval)
                {
                    _logger.LogDebug("Skipping commentary generation for throttled event {EventType} due to interval.", gameEvent.EventType);
                    shouldGenerate = false;
                }
            }
            // --- End Throttling Logic ---

            if (shouldGenerate)
            {
                var commentary = await GenerateEventCommentaryAsync(gameEvent);
                if (commentary != null && !string.IsNullOrEmpty(commentary.Text))
                {
                    await StoreAndPublishCommentary(gameEvent.GameId, commentary);
                }
            }
            else
            {
                 // Optionally log that generation was skipped due to throttling
                 // _logger.LogInformation("Skipped commentary for event {EventType} due to throttling", gameEvent.EventType);
            }
        }
        
        private async Task OnGameStateUpdateAsync(GameStateUpdate update, StreamSequenceToken? token = null)
        {
            // Rebuild the game state from the update
            if (!_state.State.GameStates.TryGetValue(update.GameId, out var gameState))
            {
                 _logger.LogWarning("Received state update for unknown game: {GameId}. Attempting to fetch state.", update.GameId);
                 try 
                 {
                     // Get the GameStateAgent and fetch the full state
                     var gameStateAgent = _grainFactory.GetGrain<IGameStateAgent>(this.GetPrimaryKeyString()); // Assuming GameId is the key
                     var fullGameState = await gameStateAgent.GetGameStateAsync(update.GameId);
                     if (fullGameState != null) 
                     {
                         // Initialize commentary with the fetched state
                         await InitializeGameCommentaryAsync(update.GameId, fullGameState);
                         gameState = fullGameState; // Use the fetched state
                         _logger.LogInformation("Successfully fetched and initialized state for game {GameId}", update.GameId);
                     }
                     else 
                     {
                          _logger.LogError("Failed to fetch state for unknown game {GameId}. Update cannot be processed.", update.GameId);
                          return; // Cannot proceed without state
                     }
                 }
                 catch (Exception ex)
                 {
                     _logger.LogError(ex, "Error fetching state for unknown game {GameId}", update.GameId);
                     return; // Cannot proceed
                 }
            }

            // Ensure gameState is not null after potential fetching
            if (gameState == null)
            {
                 _logger.LogError("GameState is null for game {GameId} after update/fetch attempt. Update cannot be processed.", update.GameId);
                 return;
            }
            
            // Update ball position
            gameState.Ball.Position = update.BallPosition;
            
            // Update game status
            gameState.Status = update.Status;
            
            // Update game time
            gameState.GameTime = update.GameTime;
            
            // Save updated state (redundant if InitializeGameCommentaryAsync saved?)
            _state.State.GameStates[update.GameId] = gameState;
            // Consider removing this WriteStateAsync if InitializeGameCommentaryAsync always writes
            // await _state.WriteStateAsync(); 
            
            // Process the game state update
            await ProcessGameStateUpdateAsync(gameState);
        }
        
        public Task<string> GetDescriptionAsync()
        {
            return Task.FromResult("Football match commentary agent that generates real-time commentary based on game events.");
        }
        
        public async Task<CommentaryMessage> GenerateEventCommentaryAsync(GameEvent gameEvent)
        {
            if (_state.State.IsGameEnded && gameEvent.EventType != GameEventType.GameEnd) // Allow final GameEnd commentary generation
            {
                _logger.LogInformation("GenerateEventCommentaryAsync: Game {GameId} ended, skipping generation for event {EventType}.", gameEvent.GameId, gameEvent.EventType);
                return new CommentaryMessage { GameId = gameEvent.GameId, Text = string.Empty };
            }

            try
            {
                // Get the current game state if available
                FootballCommentary.Core.Models.GameState? gameState = null;
                _state.State.GameStates.TryGetValue(gameEvent.GameId, out gameState);
                
                // Get player name if available
                string playerName = PlayerData.GetPlayerName(gameEvent.TeamId, gameEvent.PlayerId);
                string playerContext = (gameEvent.PlayerId != null && playerName != $"Player {gameEvent.PlayerId}" && playerName != "a player") 
                                     ? $" by player {playerName} (ID: {gameEvent.PlayerId})" 
                                     : "";

                string promptTemplate = "Provide concise, exciting football commentary for this game event: {0}. Context: {1}{2}";
                string eventDescription = gameEvent.EventType.ToString();
                string additionalContext = string.Empty;
                
                if (gameState != null)
                {
                    additionalContext = $"Current score is {gameState.HomeTeam.Name} {gameState.HomeTeam.Score} - {gameState.AwayTeam.Score} {gameState.AwayTeam.Name}.";
                                       
                    if (gameEvent.TeamId == "TeamA")
                        additionalContext += $", Event involves team: {gameState.HomeTeam.Name}";
                    else if (gameEvent.TeamId == "TeamB")
                        additionalContext += $", Event involves team: {gameState.AwayTeam.Name}";
                }
                
                string prompt = string.Format(promptTemplate, eventDescription, additionalContext, playerContext);
                _logger.LogInformation("LLM Prompt: {prompt}", prompt); // Log the prompt for debugging

                string commentaryText = await _llmService.GenerateCommentaryAsync(prompt);
                
                // Fallback in case LLM fails
                if (string.IsNullOrEmpty(commentaryText))
                {
                    _logger.LogWarning("LLM Service failed or returned empty ({Result}), using fallback commentary for event {EventType}", 
                        commentaryText == null ? "null" : "empty string", 
                        gameEvent.EventType);
                    commentaryText = GetFallbackCommentary(gameEvent, gameState);
                }
                
                var commentaryMessage = new CommentaryMessage
                {
                    GameId = gameEvent.GameId,
                    Text = commentaryText,
                    Timestamp = DateTime.UtcNow,
                    Type = DetermineCommentaryType(gameEvent.EventType)
                };
                
                return commentaryMessage;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating event commentary: {Message}", ex.Message);
                return new CommentaryMessage
                {
                    GameId = gameEvent.GameId,
                    Text = GetFallbackCommentary(gameEvent, gameState: null),
                    Timestamp = DateTime.UtcNow,
                    Type = CommentaryType.Factual
                };
            }
        }
        
        public async Task<CommentaryMessage> GenerateGameSummaryAsync(string gameId)
        {
            if (_state.State.IsGameEnded)
            {
                 _logger.LogInformation("GenerateGameSummaryAsync: Game {GameId} ended, skipping summary generation.", gameId);
                return new CommentaryMessage { GameId = gameId, Text = string.Empty, Type = CommentaryType.Summary };
            }

            try
            {
                var gameState = await GetGameStateForCommentaryAsync(gameId);
                
                if (gameState == null)
                {
                    return new CommentaryMessage
                    {
                        GameId = gameId,
                        Text = "Game summary not available at this time.",
                        Type = CommentaryType.Summary
                    };
                }
                
                string promptTemplate = "Provide a brief summary of the current state of the football match. " +
                                      "Team A: {0} ({1}), Team B: {2} ({3}). Game time: {4} minutes.";
                
                string prompt = string.Format(
                    promptTemplate,
                    gameState.HomeTeam.Name,
                    gameState.HomeTeam.Score,
                    gameState.AwayTeam.Name,
                    gameState.AwayTeam.Score,
                    (int)gameState.GameTime.TotalMinutes);
                
                string summaryText = await _llmService.GenerateCommentaryAsync(prompt);
                
                // Fallback if LLM fails
                if (string.IsNullOrEmpty(summaryText))
                {
                    summaryText = $"Current score: {gameState.HomeTeam.Name} {gameState.HomeTeam.Score} - " +
                                 $"{gameState.AwayTeam.Score} {gameState.AwayTeam.Name}. " +
                                 $"We're at {(int)gameState.GameTime.TotalMinutes} minutes into the match.";
                }
                
                var summaryMessage = new CommentaryMessage
                {
                    GameId = gameId,
                    Text = summaryText,
                    Timestamp = DateTime.UtcNow,
                    Type = CommentaryType.Summary
                };
                
                if (!_state.State.GameCommentary.ContainsKey(gameId))
                    _state.State.GameCommentary[gameId] = new List<CommentaryMessage>();
                    
                _state.State.GameCommentary[gameId].Add(summaryMessage);
                await _state.WriteStateAsync();
                
                await PublishCommentaryMessageAsync(summaryMessage);
                
                return summaryMessage;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating game summary: {Message}", ex.Message);
                return new CommentaryMessage
                {
                    GameId = gameId,
                    Text = "Game in progress. Stay tuned for updates.",
                    Timestamp = DateTime.UtcNow,
                    Type = CommentaryType.Summary
                };
            }
        }
        
        public async Task<CommentaryMessage> GenerateMatchSummaryAsync(string gameId)
        {
            try
            {
                var gameState = await GetGameStateForCommentaryAsync(gameId);
                
                if (gameState == null)
                {
                    return new CommentaryMessage
                    {
                        GameId = gameId,
                        Text = "Match summary not available at this time.",
                        Type = CommentaryType.Summary
                    };
                }
                
                string promptTemplate = "The football match has ended. Provide a final summary. " +
                                      "Final score - Team A: {0} ({1}), Team B: {2} ({3}).";
                
                string prompt = string.Format(
                    promptTemplate,
                    gameState.HomeTeam.Name,
                    gameState.HomeTeam.Score,
                    gameState.AwayTeam.Name,
                    gameState.AwayTeam.Score);
                
                string summaryText = await _llmService.GenerateCommentaryAsync(prompt);
                
                // Fallback if LLM fails
                if (string.IsNullOrEmpty(summaryText))
                {
                    summaryText = $"Full time: {gameState.HomeTeam.Name} {gameState.HomeTeam.Score} - " +
                                 $"{gameState.AwayTeam.Score} {gameState.AwayTeam.Name}. ";
                    
                    if (gameState.HomeTeam.Score > gameState.AwayTeam.Score)
                    {
                        summaryText += $"{gameState.HomeTeam.Name} wins!";
                    }
                    else if (gameState.AwayTeam.Score > gameState.HomeTeam.Score)
                    {
                        summaryText += $"{gameState.AwayTeam.Name} wins!";
                    }
                    else
                    {
                        summaryText += "The match ends in a draw.";
                    }
                }
                
                var summaryMessage = new CommentaryMessage
                {
                    GameId = gameId,
                    Text = summaryText,
                    Timestamp = DateTime.UtcNow,
                    Type = CommentaryType.Summary
                };
                
                if (!_state.State.GameCommentary.ContainsKey(gameId))
                    _state.State.GameCommentary[gameId] = new List<CommentaryMessage>();
                    
                _state.State.GameCommentary[gameId].Add(summaryMessage);
                await _state.WriteStateAsync();
                
                await PublishCommentaryMessageAsync(summaryMessage);
                
                return summaryMessage;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating match summary: {Message}", ex.Message);
                return new CommentaryMessage
                {
                    GameId = gameId,
                    Text = "The match has concluded. Thank you for joining us.",
                    Timestamp = DateTime.UtcNow,
                    Type = CommentaryType.Summary
                };
            }
        }
        
        public async Task<CommentaryMessage> GenerateBackgroundCommentaryAsync(string gameId)
        {
            if (_state.State.IsGameEnded)
            {
                 _logger.LogInformation("GenerateBackgroundCommentaryAsync: Game {GameId} ended, skipping background commentary.", gameId);
                return new CommentaryMessage { GameId = gameId, Text = string.Empty, Type = CommentaryType.Background };
            }

            // Don't generate background commentary if we've had a recent commentary
            if ((DateTime.UtcNow - _state.State.LastCommentaryTime).TotalMinutes < 2)
            {
                return new CommentaryMessage 
                { 
                    GameId = gameId,
                    Text = string.Empty,
                    Type = CommentaryType.Background
                };
            }
            
            // Random chance to skip (30%)
            if (_random.Next(10) < 3)
            {
                return new CommentaryMessage 
                { 
                    GameId = gameId,
                    Text = string.Empty,
                    Type = CommentaryType.Background
                };
            }
            
            try
            {
                var gameState = await GetGameStateForCommentaryAsync(gameId);
                
                if (gameState == null || gameState.Status != GameStatus.InProgress)
                {
                    return new CommentaryMessage 
                    { 
                        GameId = gameId,
                        Text = string.Empty,
                        Type = CommentaryType.Background
                    };
                }
                
                string prompt = "Provide some general background commentary about the atmosphere " +
                               "or the overall flow of the football match.";
                
                string commentaryText = await _llmService.GenerateCommentaryAsync(prompt);
                
                // Fallback if LLM fails
                if (string.IsNullOrEmpty(commentaryText))
                {
                    string[] backgroundComments = new string[]
                    {
                        "The atmosphere is electric here today!",
                        "Both teams showing good energy in this match.",
                        "The managers look tense on the sidelines.",
                        "It's a beautiful day for football.",
                        "The pitch looks in excellent condition.",
                        "Fans are in full voice, creating a fantastic atmosphere.",
                        "Players are showing great determination today.",
                        "The technical ability on display is impressive.",
                        "Both teams have prepared well for this encounter.",
                        "We're seeing some tactical adjustments from both sides."
                    };
                    
                    commentaryText = backgroundComments[_random.Next(backgroundComments.Length)];
                }
                
                var commentaryMessage = new CommentaryMessage
                {
                    GameId = gameId,
                    Text = commentaryText,
                    Timestamp = DateTime.UtcNow,
                    Type = CommentaryType.Background
                };
                
                if (!_state.State.GameCommentary.ContainsKey(gameId))
                    _state.State.GameCommentary[gameId] = new List<CommentaryMessage>();
                    
                _state.State.GameCommentary[gameId].Add(commentaryMessage);
                _state.State.LastCommentaryTime = DateTime.UtcNow;
                await _state.WriteStateAsync();
                
                await PublishCommentaryMessageAsync(commentaryMessage);
                
                return commentaryMessage;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating background commentary: {Message}", ex.Message);
                return new CommentaryMessage 
                { 
                    GameId = gameId,
                    Text = string.Empty,
                    Type = CommentaryType.Background
                };
            }
        }
        
        public Task<List<CommentaryMessage>> GetRecentCommentaryAsync(string gameId, int count = 10)
        {
            if (!_state.State.GameCommentary.ContainsKey(gameId))
                return Task.FromResult(new List<CommentaryMessage>());
                
            var commentary = _state.State.GameCommentary[gameId]
                .OrderByDescending(c => c.Timestamp)
                .Take(count)
                .ToList();
                
            return Task.FromResult(commentary);
        }
        
        public async Task InitializeGameCommentaryAsync(string gameId, FootballCommentary.Core.Models.GameState gameState)
        {
            if (_state.State.GameStates == null)
                _state.State.GameStates = new Dictionary<string, FootballCommentary.Core.Models.GameState>();
                
            _state.State.GameStates[gameId] = gameState;
            
            string promptTemplate = "Provide an exciting introduction for a football match between {0} and {1}.";
            string prompt = string.Format(promptTemplate, gameState.HomeTeam.Name, gameState.AwayTeam.Name);
            
            string welcomeText = await _llmService.GenerateCommentaryAsync(prompt);
            
            // Fallback if LLM fails
            if (string.IsNullOrEmpty(welcomeText))
            {
                welcomeText = $"Welcome to today's match between {gameState.HomeTeam.Name} and {gameState.AwayTeam.Name}! " +
                             "We're looking forward to an exciting game of football.";
            }
            
            var welcomeMessage = new CommentaryMessage
            {
                GameId = gameId,
                Text = welcomeText,
                Timestamp = DateTime.UtcNow,
                Type = CommentaryType.Excitement
            };
            
            if (!_state.State.GameCommentary.ContainsKey(gameId))
                _state.State.GameCommentary[gameId] = new List<CommentaryMessage>();
                
            _state.State.GameCommentary[gameId].Add(welcomeMessage);
            await _state.WriteStateAsync();
            
            await PublishCommentaryMessageAsync(welcomeMessage);
        }
        
        public async Task ProcessGameStateUpdateAsync(FootballCommentary.Core.Models.GameState gameState)
        {
            string gameId = gameState.GameId;
            
            if (_state.State.GameStates == null)
                _state.State.GameStates = new Dictionary<string, FootballCommentary.Core.Models.GameState>();
                
            // Get the previous game state (if any)
            FootballCommentary.Core.Models.GameState? previousState = null;
            _state.State.GameStates.TryGetValue(gameId, out previousState);
            
            // Update the stored game state
            _state.State.GameStates[gameId] = gameState;
            await _state.WriteStateAsync();
            
            // If this is a new game, initialize commentary
            if (previousState == null)
            {
                await InitializeGameCommentaryAsync(gameId, gameState);
                return;
            }
            
            // Check for significant changes that might warrant commentary
            if (gameState.Status != previousState.Status)
            {
                // Status changed - might generate commentary depending on the phase
                if (gameState.Status == GameStatus.InProgress && previousState.Status == GameStatus.NotStarted)
                {
                    var message = new CommentaryMessage
                    {
                        GameId = gameId,
                        Text = "And the ball is in play! The match is underway.",
                        Type = CommentaryType.Factual
                    };
                    
                    await PublishCommentaryMessageAsync(message);
                }
                else if (gameState.Status == GameStatus.Ended && previousState.Status == GameStatus.InProgress)
                {
                    await GenerateMatchSummaryAsync(gameId);
                }
            }
            
            // Check if a significant amount of time has passed
            int currentMinute = (int)gameState.GameTime.TotalMinutes;
            int previousMinute = (int)(previousState?.GameTime.TotalMinutes ?? 0);
            
            if (currentMinute > previousMinute && currentMinute % 5 == 0)
            {
                // Generate commentary every 5 minutes of game time
                await GenerateGameSummaryAsync(gameId);
            }
        }
        
        private Task<FootballCommentary.Core.Models.GameState?> GetGameStateForCommentaryAsync(string gameId)
        {
            try
            {
                // Try to get from our local cache first
                if (_state.State.GameStates != null && _state.State.GameStates.TryGetValue(gameId, out var cachedState))
                {
                    return Task.FromResult<FootballCommentary.Core.Models.GameState?>(cachedState);
                }
                
                // If not in cache, return null (could also try to fetch from GameState agent instead)
                return Task.FromResult<FootballCommentary.Core.Models.GameState?>(null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting game state for commentary: {Message}", ex.Message);
                return Task.FromResult<FootballCommentary.Core.Models.GameState?>(null);
            }
        }
        
        private async Task PublishCommentaryMessageAsync(CommentaryMessage message)
        {
            try
            {
                if (_streamProvider == null)
                {
                    _logger.LogWarning("Stream provider is null, cannot publish commentary message");
                    return;
                }
                
                var stream = _streamProvider.GetStream<CommentaryMessage>(
                    StreamId.Create("Commentary", this.GetPrimaryKeyString()));
                
                await stream.OnNextAsync(message);
                _logger.LogInformation("Published commentary: {Text}", message.Text);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error publishing commentary message: {Message}", ex.Message);
            }
        }
        
        private CommentaryType DetermineCommentaryType(GameEventType eventType)
        {
            switch (eventType)
            {
                case GameEventType.Goal:
                    return CommentaryType.Excitement;
                    
                case GameEventType.GameStart:
                case GameEventType.GameEnd:
                    return CommentaryType.Factual;
                    
                case GameEventType.Pass:
                case GameEventType.Shot:
                case GameEventType.Save:
                case GameEventType.Tackle:
                    return CommentaryType.Analysis;
                    
                default:
                    return CommentaryType.Factual;
            }
        }
        
        private string GetFallbackCommentary(GameEvent gameEvent, FootballCommentary.Core.Models.GameState? gameState)
        {
            string playerName = PlayerData.GetPlayerName(gameEvent.TeamId, gameEvent.PlayerId);
            string teamName = gameEvent.TeamId == "TeamA" && gameState != null 
                                ? gameState.HomeTeam.Name 
                                : (gameEvent.TeamId == "TeamB" && gameState != null ? gameState.AwayTeam.Name : gameEvent.TeamId ?? "the team");
            
            switch (gameEvent.EventType)
            {
                case GameEventType.Goal:
                    return $"GOAL! {playerName} scores for {teamName}!";
                    
                case GameEventType.Pass:
                    return $"Nice pass from {playerName}.";
                    
                case GameEventType.Shot:
                    return $"{playerName} takes a shot! That was close.";
                    
                case GameEventType.Save:
                    return "Great save by the goalkeeper!";
                    
                case GameEventType.Tackle:
                    return $"Good tackle by {playerName}, winning back possession.";
                    
                case GameEventType.OutOfBounds:
                    return "The ball goes out of play.";
                    
                case GameEventType.Foul:
                    return "The referee calls a foul.";
                    
                case GameEventType.GameStart:
                    return "The match is underway!";
                    
                case GameEventType.GameEnd:
                    return "The referee blows the final whistle!";
                    
                default:
                    return $"Event: {gameEvent.EventType}";
            }
        }

        // Helper method to reduce code duplication
        private async Task StoreAndPublishCommentary(string gameId, CommentaryMessage commentary)
        {
            // Store the commentary
            if (!_state.State.GameCommentary.ContainsKey(gameId))
                _state.State.GameCommentary[gameId] = new List<CommentaryMessage>();
                
            _state.State.GameCommentary[gameId].Add(commentary);
            
            // Update last commentary time whenever we store/publish something
            _state.State.LastCommentaryTime = commentary.Timestamp; 

            await _state.WriteStateAsync();
            
            // Publish the commentary
            await PublishCommentaryMessageAsync(commentary);
        }
    }
} 