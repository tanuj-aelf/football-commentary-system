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
                
                // Build the prompt with current tactical situation
                string promptTemplate = 
                    "As an AI football tactician, suggest movement vectors (dx, dy) " +
                    "for the following players in team {0} who are {1} possession. " +
                    "Game time: {2} minutes, Score: {3}. " +
                    "Ball is at position X:{4}, Y:{5}. " +
                    "{6}" +
                    "Respond with a JSON object mapping player IDs to movement vectors (dx, dy values between -0.1 and 0.1) " +
                    "in the format: {{\"PlayerID\": {{\"dx\": value, \"dy\": value}}, ...}}";
                
                // Add player information
                var playerInfo = new StringBuilder();
                foreach (var player in players)
                {
                    playerInfo.AppendLine($"Player {player.PlayerId} at position X:{player.Position.X:F2}, Y:{player.Position.Y:F2}");
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
                var suggestions = new Dictionary<string, (double dx, double dy)>();
                
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
                            
                            suggestions[playerId] = (dx, dy);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error parsing LLM movement JSON: {Message}", ex.Message);
                }
                
                return suggestions;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting LLM movement suggestions: {Message}", ex.Message);
                return new Dictionary<string, (double dx, double dy)>();
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