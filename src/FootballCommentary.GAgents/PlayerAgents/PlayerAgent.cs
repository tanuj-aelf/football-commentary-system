using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using FootballCommentary.Core.Abstractions;
using FootballCommentary.Core.Models;
using Microsoft.Extensions.Logging;
using System.Text;

namespace FootballCommentary.GAgents.PlayerAgents
{
    public class PlayerAgent
    {
        private readonly string _playerId;
        private readonly ILLMService _llmService;
        private readonly ILogger _logger;
        private readonly Dictionary<string, object> _attributes = new();
        
        // Caching for performance
        private DateTime _lastDecisionTime = DateTime.MinValue;
        private const int DECISION_CACHE_SECONDS = 3; // How long to use cached movement
        private (double dx, double dy) _cachedMovement = (0, 0);
        
        // Player role data
        public string PlayerId => _playerId;
        public string Role { get; private set; } // Goalkeeper, Defender, Midfielder, Forward
        public int PositionNumber { get; private set; }
        public bool IsTeamA { get; private set; }
        public string TeamName { get; private set; }
        
        public PlayerAgent(
            string playerId, 
            ILLMService llmService, 
            ILogger logger,
            string role,
            int positionNumber,
            bool isTeamA,
            string teamName)
        {
            _playerId = playerId;
            _llmService = llmService;
            _logger = logger;
            Role = role;
            PositionNumber = positionNumber;
            IsTeamA = isTeamA;
            TeamName = teamName;
        }
        
        public async Task<(double dx, double dy)> GetMovementDecisionAsync(
            FootballCommentary.Core.Models.GameState gameState,
            bool hasPossession,
            Position currentPosition,
            Position ballPosition)
        {
            // Check if we can use cached movement to avoid too many API calls
            if ((DateTime.UtcNow - _lastDecisionTime).TotalSeconds < DECISION_CACHE_SECONDS && 
                !hasPossession) // Don't use cache if player has possession
            {
                // Add small variations to make movement more natural
                var random = new Random();
                double dx = _cachedMovement.dx + (random.NextDouble() - 0.5) * 0.01;
                double dy = _cachedMovement.dy + (random.NextDouble() - 0.5) * 0.01;
                
                return (Math.Clamp(dx, -0.1, 0.1), Math.Clamp(dy, -0.1, 0.1));
            }
            
            try
            {
                // Generate personalized prompt for this player
                string prompt = GeneratePlayerPrompt(gameState, hasPossession, currentPosition, ballPosition);
                
                // Call LLM to get player's next movement
                _logger.LogDebug("Making LLM call for player {PlayerId} ({Role})", PlayerId, Role);
                string response = await _llmService.GenerateCommentaryAsync(prompt);
                
                // Parse response to extract movement vector
                var movement = ParseMovementResponse(response);
                
                // Cache the movement decision
                _cachedMovement = movement;
                _lastDecisionTime = DateTime.UtcNow;
                
                return movement;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting movement decision for player {PlayerId}: {Message}", PlayerId, ex.Message);
                
                // Fall back to cached movement or zero movement if no cache
                return _cachedMovement.dx != 0 || _cachedMovement.dy != 0 
                    ? _cachedMovement 
                    : (0, 0);
            }
        }
        
        private string GeneratePlayerPrompt(
            FootballCommentary.Core.Models.GameState gameState,
            bool hasPossession,
            Position currentPosition,
            Position ballPosition)
        {
            string promptTemplate = @"
You are a {0} (#{1}) for {2}. 
Current position: X:{3:F2}, Y:{4:F2}
Game time: {5}m, Score: {6}
Ball position: X:{7:F2}, Y:{8:F2}
Ball possession: {9}

Field orientation:
- Team {2} attacks {16}
- Opponent team attacks {17}
- Your goal is at X={10}
- Opponent goal is at X={11}

Teammate positions:
{12}

Opponent positions:
{13}

{14}

Based on your role as a {0}, determine the EXTREMELY AGGRESSIVE and HIGH-RISK movement vector (dx, dy) 
between -0.1 and 0.1. Take major risks to create dramatic goal-scoring opportunities!

{15}

Respond with JSON only: {{""dx"": value, ""dy"": value}}";

            // Add role-specific guidance
            string roleGuidance = Role switch 
            {
                "Goalkeeper" => "Though protecting your goal is important, you should take EXTREME risks occasionally! Rush far off your line to intercept through balls, join attacks when your team is pushing forward, and be unpredictable in your positioning to surprise opponents.",
                "Defender" => "Throw caution to the wind! Push AGGRESSIVELY forward into attack, overlap with your wingers, make daring runs through the center, and don't worry too much about getting back. Take risks with your tackles and intercepts - glory comes to the brave!",
                "Midfielder" => "Be the ultimate playmaker and risk-taker! Drive forward aggressively with the ball, attempt ambitious long-range shots, make daring through passes, and press opponents relentlessly. Your movement should constantly surprise and overwhelm the opposition.",
                "Forward" => "Play like a superstar striker! Take EXTREME risks to get into scoring positions, make direct runs at defenders, shoot from ANY angle or distance when possible, and constantly look to break behind the defense. Creating spectacular goal-scoring chances is your ONLY priority!",
                _ => "Play with maximum aggression, take huge risks, and create spectacular moments for your team!"
            };
            
            // Add possession-specific instructions
            string possessionGuidance = hasPossession
                ? "You currently have the ball - be EXTREMELY AGGRESSIVE! Look for the most direct path to goal, take on multiple defenders, attempt audacious shots from distance, or make high-risk creative passes. Fans want to see SPECTACULAR attacking play and GOALS!"
                : "You don't have the ball - be EXTREMELY PROACTIVE! Make aggressive runs into dangerous spaces, position yourself for shooting opportunities, press opponents with maximum intensity. Take major risks to create goal-scoring opportunities or win back possession!";
                
            // Get possession description
            string possession = DeterminePossessionDescription(gameState);
            
            // Get team and opponent players
            var teamPlayers = IsTeamA ? gameState.HomeTeam.Players : gameState.AwayTeam.Players;
            var opponentPlayers = IsTeamA ? gameState.AwayTeam.Players : gameState.HomeTeam.Players;
            
            // Create teammate positions string
            var teammatesInfo = new StringBuilder();
            foreach (var player in teamPlayers.Where(p => p.PlayerId != PlayerId))
            {
                string playerName = PlayerData.GetPlayerName(IsTeamA ? "TeamA" : "TeamB", 
                    TryParsePlayerNumber(player.PlayerId));
                
                string playerRole = DeterminePlayerRole(TryParsePlayerNumber(player.PlayerId));
                string playerBall = player.PlayerId == gameState.BallPossession ? " (has ball)" : "";
                
                teammatesInfo.AppendLine($"- {playerRole} {playerName}: X:{player.Position.X:F2}, Y:{player.Position.Y:F2}{playerBall}");
            }
            
            // Create opponent positions string
            var opponentsInfo = new StringBuilder();
            foreach (var player in opponentPlayers)
            {
                string playerName = PlayerData.GetPlayerName(!IsTeamA ? "TeamA" : "TeamB", 
                    TryParsePlayerNumber(player.PlayerId));
                
                string playerRole = DeterminePlayerRole(TryParsePlayerNumber(player.PlayerId));
                string playerBall = player.PlayerId == gameState.BallPossession ? " (has ball)" : "";
                
                opponentsInfo.AppendLine($"- {playerRole} {playerName}: X:{player.Position.X:F2}, Y:{player.Position.Y:F2}{playerBall}");
            }
            
            // Get goal positions
            double ownGoalX = IsTeamA ? 0.05 : 0.95;
            double opponentGoalX = IsTeamA ? 0.95 : 0.05;

            // Get proper field orientation descriptions based on team
            string teamAttackDirection = IsTeamA ? 
                "from left (X=0) to right (X=1)" : 
                "from right (X=1) to left (X=0)";
            
            string opponentAttackDirection = IsTeamA ? 
                "from right (X=1) to left (X=0)" : 
                "from left (X=0) to right (X=1)";

            // Format the prompt with player data
            return string.Format(
                promptTemplate,
                Role,
                PositionNumber,
                TeamName,
                currentPosition.X,
                currentPosition.Y,
                (int)gameState.GameTime.TotalMinutes,
                $"{gameState.HomeTeam.Score}-{gameState.AwayTeam.Score}",
                ballPosition.X,
                ballPosition.Y,
                possession,
                ownGoalX.ToString("F2"),
                opponentGoalX.ToString("F2"),
                teammatesInfo.ToString().TrimEnd(),
                opponentsInfo.ToString().TrimEnd(),
                possessionGuidance,
                roleGuidance,
                teamAttackDirection,
                opponentAttackDirection
            );
        }
        
        private string DeterminePossessionDescription(FootballCommentary.Core.Models.GameState gameState)
        {
            if (string.IsNullOrEmpty(gameState.BallPossession))
            {
                return "No player currently has possession of the ball";
            }
            
            if (gameState.BallPossession == PlayerId)
            {
                return "You have the ball";
            }
            
            bool isTeammatePossession = 
                (IsTeamA && gameState.BallPossession.StartsWith("TeamA")) ||
                (!IsTeamA && gameState.BallPossession.StartsWith("TeamB"));
                
            if (isTeammatePossession)
            {
                string playerName = "Unknown teammate";
                if (int.TryParse(gameState.BallPossession.Split('_')[1], out int playerIndex))
                {
                    playerName = PlayerData.GetPlayerName(IsTeamA ? "TeamA" : "TeamB", playerIndex + 1);
                }
                return $"Your teammate {playerName} has the ball";
            }
            else
            {
                string playerName = "Unknown opponent";
                string opponentTeam = IsTeamA ? "TeamB" : "TeamA";
                if (int.TryParse(gameState.BallPossession.Split('_')[1], out int playerIndex))
                {
                    playerName = PlayerData.GetPlayerName(opponentTeam, playerIndex + 1);
                }
                return $"Opponent {playerName} has the ball";
            }
        }
        
        private (double dx, double dy) ParseMovementResponse(string response)
        {
            try
            {
                // Try to extract JSON from response
                int startIndex = response.IndexOf('{');
                int endIndex = response.LastIndexOf('}');
                
                if (startIndex >= 0 && endIndex > startIndex)
                {
                    string jsonText = response.Substring(startIndex, endIndex - startIndex + 1);
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    };
                    
                    var movementData = JsonSerializer.Deserialize<MovementResponse>(jsonText, options);
                    
                    if (movementData != null)
                    {
                        // Ensure values are within allowed range
                        double dx = Math.Clamp(movementData.Dx, -0.1, 0.1);
                        double dy = Math.Clamp(movementData.Dy, -0.1, 0.1);
                        
                        return (dx, dy);
                    }
                }
                
                _logger.LogWarning("Failed to parse movement response for player {PlayerId}: {Response}", PlayerId, response);
                
                // Return a small random movement as fallback
                var random = new Random();
                return (
                    (random.NextDouble() - 0.5) * 0.02,
                    (random.NextDouble() - 0.5) * 0.02
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing movement response for player {PlayerId}: {Message}", PlayerId, ex.Message);
                return (0, 0);
            }
        }
        
        // Helper class for JSON deserialization
        private class MovementResponse
        {
            public double Dx { get; set; }
            public double Dy { get; set; }
        }

        // Helper method to parse player number from ID
        private int TryParsePlayerNumber(string playerId)
        {
            if (string.IsNullOrEmpty(playerId))
                return 0;
            
            var parts = playerId.Split('_');
            if (parts.Length == 2 && int.TryParse(parts[1], out int id))
            {
                return id + 1; // Convert to 1-based player number
            }
            
            return 0;
        }

        // Helper method to determine player role from player number
        private string DeterminePlayerRole(int playerNumber)
        {
            // Player numbers are 1-based
            switch (playerNumber)
            {
                case 1:
                    return "Goalkeeper";
                case 2:
                case 3:
                case 4:
                case 5:
                    return "Defender";
                case 6:
                case 7:
                case 8:
                    return "Midfielder";
                case 9:
                case 10:
                case 11:
                    return "Forward";
                default:
                    return "Player";
            }
        }
    }
} 