using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using FootballCommentary.Core.Abstractions;
using FootballCommentary.Core.Models;
using Microsoft.Extensions.Logging;
using System.Text;
using FootballCommentary.GAgents.GameState;

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
        
        // Formation data
        public TeamFormation CurrentFormation { get; private set; }
        public string FormationRole { get; private set; } // Specific role in formation (e.g. "Central Defender", "Attacking Midfielder")
        public Position BasePosition { get; private set; } // Base position in current formation
        
        public PlayerAgent(
            string playerId, 
            ILLMService llmService, 
            ILogger logger,
            string role,
            int positionNumber,
            bool isTeamA,
            string teamName,
            TeamFormation formation = TeamFormation.Formation_4_4_2,
            string formationRole = "",
            Position basePosition = null)
        {
            _playerId = playerId;
            _llmService = llmService;
            _logger = logger;
            Role = role;
            PositionNumber = positionNumber;
            IsTeamA = isTeamA;
            TeamName = teamName;
            CurrentFormation = formation;
            FormationRole = formationRole ?? DetermineFormationRole(formation, positionNumber);
            BasePosition = basePosition ?? new Position { X = 0.5, Y = 0.5 };
        }
        
        // Method to update formation information when formation changes
        public void UpdateFormation(TeamFormation formation, string formationRole, Position basePosition)
        {
            CurrentFormation = formation;
            FormationRole = formationRole ?? DetermineFormationRole(formation, PositionNumber);
            BasePosition = basePosition;
            _logger.LogDebug("Player {PlayerId} formation updated to {Formation}, role: {Role}", 
                PlayerId, formation, FormationRole);
                
            // Clear movement cache when formation changes
            _lastDecisionTime = DateTime.MinValue;
        }
        
        // Helper method to determine specific formation role based on position number and formation
        public string DetermineFormationRole(TeamFormation formation, int positionNumber)
        {
            // Player 1 is always goalkeeper
            if (positionNumber == 1)
                return "Goalkeeper";
                
            // Based on formation and position number, determine specific role
            switch (formation)
            {
                case TeamFormation.Formation_4_4_2:
                    if (positionNumber >= 2 && positionNumber <= 5)
                        return positionNumber == 2 || positionNumber == 5 ? "Full Back" : "Center Back";
                    else if (positionNumber >= 6 && positionNumber <= 9)
                        return positionNumber == 6 || positionNumber == 9 ? "Winger" : "Center Midfielder";
                    else
                        return "Striker";
                
                case TeamFormation.Formation_4_3_3:
                    if (positionNumber >= 2 && positionNumber <= 5)
                        return positionNumber == 2 || positionNumber == 5 ? "Full Back" : "Center Back";
                    else if (positionNumber >= 6 && positionNumber <= 8)
                        return positionNumber == 7 ? "Central Midfielder" : "Defensive Midfielder";
                    else
                        return positionNumber == 10 ? "Center Forward" : "Wing Forward";
                
                case TeamFormation.Formation_4_2_3_1:
                    if (positionNumber >= 2 && positionNumber <= 5)
                        return positionNumber == 2 || positionNumber == 5 ? "Full Back" : "Center Back";
                    else if (positionNumber == 6 || positionNumber == 7)
                        return "Defensive Midfielder";
                    else if (positionNumber >= 8 && positionNumber <= 10)
                        return positionNumber == 9 ? "Central Attacking Midfielder" : "Wing Midfielder";
                    else
                        return "Lone Striker";
                
                case TeamFormation.Formation_3_5_2:
                    if (positionNumber >= 2 && positionNumber <= 4)
                        return "Center Back";
                    else if (positionNumber == 5 || positionNumber == 9)
                        return "Wing Back";
                    else if (positionNumber >= 6 && positionNumber <= 8)
                        return positionNumber == 7 ? "Central Midfielder" : "Defensive Midfielder";
                    else
                        return "Striker";
                
                case TeamFormation.Formation_5_3_2:
                    if (positionNumber == 2 || positionNumber == 6)
                        return "Wing Back";
                    else if (positionNumber >= 3 && positionNumber <= 5)
                        return "Center Back";
                    else if (positionNumber >= 7 && positionNumber <= 9)
                        return positionNumber == 8 ? "Central Midfielder" : "Wide Midfielder";
                    else
                        return "Striker";
                
                case TeamFormation.Formation_4_1_4_1:
                    if (positionNumber >= 2 && positionNumber <= 5)
                        return positionNumber == 2 || positionNumber == 5 ? "Full Back" : "Center Back";
                    else if (positionNumber == 6)
                        return "Defensive Midfielder";
                    else if (positionNumber >= 7 && positionNumber <= 10)
                        return positionNumber == 8 || positionNumber == 9 ? "Central Midfielder" : "Winger";
                    else
                        return "Lone Striker";
                
                default:
                    // Fallback to standard roles
                    return Role;
            }
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
You are a {0} (#{1}) for {2}, playing as a {18} in a {19} formation.
Current position: X:{3:F2}, Y:{4:F2}
Base position in formation: X:{20:F2}, Y:{21:F2}
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

Based on your role as a {18} in a {19} formation, determine the EXTREMELY AGGRESSIVE and HIGH-RISK movement vector (dx, dy) 
between -0.1 and 0.1. Take major risks to create dramatic goal-scoring opportunities!

{15}

Respond with JSON only: {{""dx"": value, ""dy"": value}}";

            // Add role-specific guidance
            string roleGuidance = Role switch 
            {
                "Goalkeeper" => "Your primary role is to protect the goal. When you have possession, distribute the ball QUICKLY and INTELLIGENTLY to a teammate to start an attack. Look for safe, effective passes. Occasionally, if a clear opportunity arises and the team is pushing, you can support play further up, but prioritize safe distribution first.",
                "Defender" => "Throw caution to the wind! Push AGGRESSIVELY forward into attack, overlap with your wingers, make daring runs through the center. Take risks with your tackles and intercepts - glory comes to the brave! However, be mindful of recovering your defensive shape and protecting your team's half, especially on turnovers.",
                "Midfielder" => "Be the ultimate playmaker and risk-taker! Drive forward aggressively with the ball, attempt ambitious long-range shots, make daring through passes, and press opponents relentlessly. Your movement should constantly surprise and overwhelm the opposition.",
                "Forward" => "Play like a superstar striker! Take EXTREME risks to get into scoring positions, make direct runs at defenders, shoot from ANY angle or distance when possible, and constantly look to break behind the defense. Creating spectacular goal-scoring chances is your ONLY priority!",
                _ => "Play with maximum aggression, take huge risks, and create spectacular moments for your team!"
            };
            
            // Add formation role specific guidance
            string formationRoleGuidance = "";
            if (!string.IsNullOrEmpty(FormationRole))
            {
                formationRoleGuidance = FormationRole switch
                {
                    "Full Back" => "As a Full Back, make aggressive overlapping runs, push high up the field, provide width, and deliver crosses. Balance this with timely recovery to your defensive duties in your team's half.",
                    "Center Back" => "Even as a Center Back, look for opportunities to step into midfield with the ball or join attacks. Your primary duty is to organize the defense and protect your goal area within your team's half.",
                    "Wing Back" => "As a Wing Back, you should be constantly bombing forward, providing width in attack, and creating overloads. Be prepared to track back quickly to defend your flank in your team's half.",
                    "Defensive Midfielder" => "Despite being a Defensive Midfielder, make late runs into the box and attempt shots. Your core role is to shield the defense, win the ball back in midfield, and maintain your position in your team's half when defending.",
                    "Central Midfielder" => "As a Central Midfielder, control the tempo, make box-to-box runs, play risky through passes, and get into shooting positions. Remember to contribute defensively and hold your shape.",
                    "Wide Midfielder" or "Winger" => "As a wide player, take on defenders 1v1, cut inside for shots, make runs behind the defense, and deliver dangerous crosses. Also, track back to support your full-back.",
                    "Central Attacking Midfielder" => "As a #10, be the creative spark! Take risks with through balls, shoot from distance, and make late runs into the box. When possession is lost, apply pressure or recover your position.",
                    "Striker" or "Center Forward" => "As the focal point of attack, constantly look for shooting opportunities, make runs behind defenders, and get into the box for crosses.",
                    "Lone Striker" => "As a Lone Striker, you must be unpredictable! Drop deep to create, spin in behind, and always be in position to finish attacks.",
                    "Wing Forward" => "From your wide forward position, cut inside for shots, make diagonal runs behind the defense, and link up with central attackers.",
                    _ => "Play with intelligence according to your position, but take risks when opportunities arise!"
                };
                
                roleGuidance += "\n\n" + formationRoleGuidance;
            }
            
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
                
            // Get formation name for display
            string formationName = GetFormationDisplayName(CurrentFormation);

            // Conditional tactical nuance for prompt
            string tacticalNuance = "";
            bool opponentHasPossession = !string.IsNullOrEmpty(gameState.BallPossession) && 
                                     ((IsTeamA && gameState.BallPossession.StartsWith("TeamB")) || 
                                      (!IsTeamA && gameState.BallPossession.StartsWith("TeamA")));
            bool ballInOwnHalf = (IsTeamA && gameState.Ball.Position.X < 0.5) || 
                                 (!IsTeamA && gameState.Ball.Position.X > 0.5);

            if (Role == "Midfielder" || Role == "Forward")
            {
                if (opponentHasPossession && ballInOwnHalf) {
                    tacticalNuance = "The opponent has possession in your team's half. Prioritize regaining defensive shape and supporting your defenders. Balance aggression with tactical discipline.";
                }
                else if (!opponentHasPossession && ballInOwnHalf && hasPossession) {
                     tacticalNuance = "Your team has possession deep in your own half. Focus on secure build-up play and creating safe passing options. Extreme forward runs might be too risky now.";
                }
                else if (!opponentHasPossession && ballInOwnHalf && !hasPossession) {
                     tacticalNuance = "The ball is loose in your team's half, or a teammate deep has it. Position yourself to support build-up or transition quickly if possession is won. Avoid overcommitting forward.";
                }
            }
            if (!string.IsNullOrEmpty(tacticalNuance))
            {
                possessionGuidance += "\n\nTACTICAL SITUATION: " + tacticalNuance;
            }

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
                opponentAttackDirection,
                FormationRole, // {18}
                formationName,  // {19}
                BasePosition.X, // {20}
                BasePosition.Y  // {21}
            );
        }
        
        // Helper to get a readable formation name
        private string GetFormationDisplayName(TeamFormation formation)
        {
            return formation switch
            {
                TeamFormation.Formation_4_4_2 => "4-4-2",
                TeamFormation.Formation_4_3_3 => "4-3-3",
                TeamFormation.Formation_4_2_3_1 => "4-2-3-1",
                TeamFormation.Formation_3_5_2 => "3-5-2", 
                TeamFormation.Formation_5_3_2 => "5-3-2",
                TeamFormation.Formation_4_1_4_1 => "4-1-4-1",
                _ => formation.ToString()
            };
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