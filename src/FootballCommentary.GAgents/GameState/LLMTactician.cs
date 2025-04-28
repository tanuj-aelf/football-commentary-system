using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FootballCommentary.Core.Abstractions;
using FootballCommentary.Core.Models;
using Microsoft.Extensions.Logging;

namespace FootballCommentary.GAgents.GameState
{
    /// <summary>
    /// Handles LLM-based football tactics and player movement decisions
    /// </summary>
    public class LLMTactician
    {
        private readonly ILogger<LLMTactician> _logger;
        private readonly ILLMService _llmService;
        
        // Movement suggestion cache to reduce LLM API calls
        private Dictionary<string, Dictionary<string, (double dx, double dy)>> _cachedMovementSuggestions = new();
        private Dictionary<string, DateTime> _movementCacheTimestamps = new();
        private const int MOVEMENT_CACHE_SECONDS = 3; // How long to use cached movements
        
        public LLMTactician(ILogger<LLMTactician> logger, ILLMService llmService)
        {
            _logger = logger;
            _llmService = llmService;
        }
        
        public async Task<Dictionary<string, (double dx, double dy)>> GetMovementSuggestionsAsync(
            FootballCommentary.Core.Models.GameState gameState,
            List<Player> players,
            bool isTeamA,
            bool hasPossession)
        {
            try
            {
                // Cache key based on team and game ID
                string cacheKey = $"{gameState.GameId}_{(isTeamA ? "TeamA" : "TeamB")}";
                
                // Check if we have recent cached suggestions
                if (_cachedMovementSuggestions.TryGetValue(cacheKey, out var cachedSuggestions) &&
                    _movementCacheTimestamps.TryGetValue(cacheKey, out var timestamp))
                {
                    if ((DateTime.UtcNow - timestamp).TotalSeconds < MOVEMENT_CACHE_SECONDS)
                    {
                        _logger.LogDebug("Using cached movement suggestions for {TeamKey}", cacheKey);
                        
                        // Add small random variations to cached movements to make them more dynamic
                        var suggestions = new Dictionary<string, (double dx, double dy)>();
                        Random random = new Random();
                        
                        foreach (var (playerId, movement) in cachedSuggestions)
                        {
                            // Apply small random variation to keep movement natural
                            double dx = movement.dx + (random.NextDouble() - 0.5) * 0.02;
                            double dy = movement.dy + (random.NextDouble() - 0.5) * 0.02;
                            
                            // Clamp to reasonable range
                            dx = Math.Clamp(dx, -0.1, 0.1);
                            dy = Math.Clamp(dy, -0.1, 0.1);
                            
                            suggestions[playerId] = (dx, dy);
                        }
                        
                        return suggestions;
                    }
                }
                
                // Create game state context for the LLM
                var teamName = isTeamA ? gameState.HomeTeam.Name : gameState.AwayTeam.Name;
                var opponentName = isTeamA ? gameState.AwayTeam.Name : gameState.HomeTeam.Name;
                var score = $"{gameState.HomeTeam.Score}-{gameState.AwayTeam.Score}";
                var gameTimeMinutes = (int)gameState.GameTime.TotalMinutes;
                
                // Extract player with ball info
                string playerWithBallId = gameState.BallPossession ?? "";
                bool isOwnTeamWithBall = !string.IsNullOrEmpty(playerWithBallId) && 
                    ((isTeamA && playerWithBallId.StartsWith("TeamA")) || 
                     (!isTeamA && playerWithBallId.StartsWith("TeamB")));
                
                // Simplified prompt to reduce token count and speed up response
                string promptTemplate = 
                    "As a football tactical AI, suggest movement vectors (dx, dy) " +
                    "for team {0} players ({1} possession). " +
                    "Time: {2}m, Score: {3}, Ball at X:{4}, Y:{5}. " +
                    "{6}" +
                    "Respond with JSON only: {{\"PlayerID\": {{\"dx\": value, \"dy\": value}}, ...}} " +
                    "with dx/dy values between -0.1 and 0.1.";
                
                // Add player information in a more compact format
                var playerInfo = new StringBuilder();
                foreach (var player in players)
                {
                    playerInfo.AppendLine($"{player.PlayerId}: ({player.Position.X:F2}, {player.Position.Y:F2})");
                }
                
                string possession = hasPossession ? "in" : "out of";
                string prompt = string.Format(
                    promptTemplate,
                    teamName,
                    possession,
                    gameTimeMinutes,
                    score,
                    gameState.Ball.Position.X.ToString("F2"),
                    gameState.Ball.Position.Y.ToString("F2"),
                    playerInfo.ToString());
                
                _logger.LogDebug("LLM Movement Prompt: {prompt}", prompt);
                
                // Get suggestions from LLM
                string response = await _llmService.GenerateCommentaryAsync(prompt);
                
                if (string.IsNullOrEmpty(response))
                {
                    _logger.LogWarning("LLM returned empty response for movement suggestions");
                    return new Dictionary<string, (double dx, double dy)>();
                }
                
                // Extract JSON from the response
                var jsonRegex = new Regex(@"\{(?:[^{}]|(?<open>\{)|(?<close-open>\}))+(?(open)(?!))\}");
                var match = jsonRegex.Match(response);
                
                if (!match.Success)
                {
                    _logger.LogWarning("Could not extract JSON from LLM response: {response}", response);
                    return new Dictionary<string, (double dx, double dy)>();
                }
                
                string jsonContent = match.Value;
                _logger.LogDebug("Extracted JSON: {json}", jsonContent);
                
                // Parse the JSON
                var movementSuggestions = new Dictionary<string, (double dx, double dy)>();
                
                try
                {
                    var jsonObject = System.Text.Json.JsonDocument.Parse(jsonContent).RootElement;
                    
                    foreach (var property in jsonObject.EnumerateObject())
                    {
                        string playerId = property.Name;
                        var movementData = property.Value;
                        
                        if (movementData.TryGetProperty("dx", out var dxElement) &&
                            movementData.TryGetProperty("dy", out var dyElement))
                        {
                            double dx = dxElement.GetDouble();
                            double dy = dyElement.GetDouble();
                            
                            // Clamp values to reasonable range
                            dx = Math.Clamp(dx, -0.1, 0.1);
                            dy = Math.Clamp(dy, -0.1, 0.1);
                            
                            movementSuggestions[playerId] = (dx, dy);
                        }
                    }
                    
                    // Cache the suggestions for future use
                    _cachedMovementSuggestions[cacheKey] = new Dictionary<string, (double dx, double dy)>(movementSuggestions);
                    _movementCacheTimestamps[cacheKey] = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error parsing LLM movement JSON: {Message}", ex.Message);
                }
                
                return movementSuggestions;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting LLM movement suggestions: {Message}", ex.Message);
                return new Dictionary<string, (double dx, double dy)>();
            }
        }
        
        public async Task<TeamFormation> GetFormationSuggestionAsync(
            FootballCommentary.Core.Models.GameState gameState,
            bool isTeamA)
        {
            try
            {
                var teamName = isTeamA ? gameState.HomeTeam.Name : gameState.AwayTeam.Name;
                var opponentName = isTeamA ? gameState.AwayTeam.Name : gameState.HomeTeam.Name;
                var score = $"{gameState.HomeTeam.Score}-{gameState.AwayTeam.Score}";
                var gameTimeMinutes = (int)gameState.GameTime.TotalMinutes;
                
                string promptTemplate = 
                    "As a football manager, suggest ONE tactical formation for {0} against {1}. " +
                    "Current score: {2}, Game time: {3} minutes. " +
                    "Choose one of these formations: 4-4-2, 4-3-3, 4-2-3-1, 3-5-2, 5-3-2, 4-1-4-1. " +
                    "Reply with ONLY the formation numbers (e.g., '4-3-3') and no other text.";
                
                string prompt = string.Format(
                    promptTemplate,
                    teamName,
                    opponentName,
                    score,
                    gameTimeMinutes);
                
                _logger.LogDebug("LLM Formation Prompt: {prompt}", prompt);
                
                string response = await _llmService.GenerateCommentaryAsync(prompt);
                
                if (string.IsNullOrEmpty(response))
                {
                    _logger.LogWarning("LLM returned empty response for formation suggestion");
                    return TeamFormation.Formation_4_4_2; // Default
                }
                
                // Clean up response to extract just the formation
                response = response.Trim();
                
                // Match known formation patterns
                if (response.Contains("4-4-2"))
                    return TeamFormation.Formation_4_4_2;
                if (response.Contains("4-3-3"))
                    return TeamFormation.Formation_4_3_3;
                if (response.Contains("4-2-3-1"))
                    return TeamFormation.Formation_4_2_3_1;
                if (response.Contains("3-5-2"))
                    return TeamFormation.Formation_3_5_2;
                if (response.Contains("5-3-2"))
                    return TeamFormation.Formation_5_3_2;
                if (response.Contains("4-1-4-1"))
                    return TeamFormation.Formation_4_1_4_1;
                
                // Default formation if no match
                _logger.LogWarning("Could not parse formation from LLM response: {response}", response);
                return TeamFormation.Formation_4_4_2;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting formation suggestion: {Message}", ex.Message);
                return TeamFormation.Formation_4_4_2; // Default on error
            }
        }
        
        public async Task<string> GetTacticalAnalysisAsync(FootballCommentary.Core.Models.GameState gameState)
        {
            try
            {
                string promptTemplate = 
                    "Analyze the current tactical situation in this football match: " +
                    "{0} ({1}) vs {2} ({3}), time: {4} minutes. " +
                    "Provide a brief tactical assessment of the match situation.";
                
                string prompt = string.Format(
                    promptTemplate,
                    gameState.HomeTeam.Name,
                    gameState.HomeTeam.Score,
                    gameState.AwayTeam.Name,
                    gameState.AwayTeam.Score,
                    (int)gameState.GameTime.TotalMinutes);
                
                string analysis = await _llmService.GenerateCommentaryAsync(prompt);
                return string.IsNullOrEmpty(analysis) ? 
                    "Match in progress." : 
                    analysis;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting tactical analysis: {Message}", ex.Message);
                return "Unable to provide tactical analysis at this time.";
            }
        }
    }
} 