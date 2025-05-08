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

POSITIONAL AWARENESS:
- Defenders primarily operate in defensive third (X: 0.0-0.3 for Team A, X: 0.7-1.0 for Team B)
- Midfielders control the middle third (X: 0.3-0.7 for both teams)
- Forwards focus on attacking third (X: 0.7-1.0 for Team A, X: 0.0-0.3 for Team B)
- Your current zone: {22}
- Your ideal zone based on role: {23}

DEFENSIVE TRANSITION PRIORITY: 
- When your team loses possession, IMMEDIATELY retreat toward your defensive half
- Forwards should drop to midfield (max X: 0.6 for Team A, min X: 0.4 for Team B)
- Midfielders must recover central positions quickly to protect defensive shape
- ALL players must track back when ball is in your defensive third

Teammate positions:
{12}

Opponent positions:
{13}

{14}

Based on your role as a {18} in a {19} formation, determine a FAST, HIGHLY DYNAMIC movement vector (dx, dy) 
between -0.1 and 0.1. Movement is now MUCH FASTER in the simulation!

SPATIAL ROLE INSTRUCTIONS:
{24}

{15}

Respond with JSON only: {{""dx"": value, ""dy"": value}}";

            // Add role-specific guidance
            string roleGuidance = Role switch 
            {
                "Goalkeeper" => "GOALKEEPER POSITIONING: Stay within 0.15 of your goal line. Your primary role is to protect the goal. Position yourself strategically between the ball and the goal. When you have possession, distribute the ball QUICKLY and INTELLIGENTLY to a teammate to start an attack. Make excellent passing decisions considering the entire field layout.",
                
                "Defender" => "DEFENDER POSITIONING: Your home territory is the defensive third. Maintain the defensive line with other defenders. Push AGGRESSIVELY forward into attack when appropriate but be prepared to sprint back into defensive positions when possession changes. Watch gaps between defenders. Know when to step up for offside traps. Be aware of your center of gravity when defending 1v1.",
                
                "Midfielder" => "MIDFIELDER POSITIONING: Control the central areas of the pitch. Be constantly aware of space - find pockets between opponent lines. Make well-timed runs into attacking positions when appropriate. Cover defensively when teammates push forward. Balance the team's shape both horizontally and vertically. Create triangles with teammates for passing options.",
                
                "Forward" => "FORWARD POSITIONING: Focus on the attacking third but make intelligent movements in all zones. Time runs to break defensive lines. Create separation from defenders using quick directional changes. Find shooting positions with clear angles to goal. Make runs that create space for teammates. Position yourself between defenders to create indecision.",
                
                _ => "TACTICAL POSITIONING: Maintain excellent positioning relative to teammates and opponents. Be aware of space, time runs, and keep tactical shape."
            };
            
            // Add formation role specific guidance with enhanced spatial awareness
            string formationRoleGuidance = "";
            if (!string.IsNullOrEmpty(FormationRole))
            {
                formationRoleGuidance = FormationRole switch
                {
                    "Full Back" => "As a Full Back, maintain width during build-up (stay within 0.15 of touchline). Make overlapping runs along the flanks to support attacks. Time forward runs to coincide with midfielder possession. Recover defensively at high speed when possession is lost - sprint recovery paths should be diagonal toward your goal.",
                    
                    "Center Back" => "As a Center Back, be the defensive anchor between 0-0.3 (Team A) or 0.7-1.0 (Team B). Control the space between defense and midfield. Step forward to intercept when appropriate but maintain defensive depth. Position yourself to see all attackers and adjust based on their movements. Create clean passing angles for building play from the back.",
                    
                    "Wing Back" => "As a Wing Back, own your entire flank from defense to attack. Maintain width during build-up (0.8-1.0 or 0.0-0.2 on Y-axis). Time your forward runs to create overloads. Recognize when to invert into midfield to create numerical advantages. Track back with high-intensity sprints when possession changes.",
                    
                    "Defensive Midfielder" => "As a Defensive Midfielder, control the crucial space between defense and midfield. Screen passing lanes to opposition attackers. Position yourself to intercept counters early. Offer passing support at specific angles from defenders. Maintain central defensive shape. Create 'round the corner' passing options for teammates under pressure.",
                    
                    "Central Midfielder" => "As a Central Midfielder, control the tempo from the engine room. Find space between opponent lines. Constantly scan and adjust position based on ball location. Create passing triangles with teammates. Time forward runs to arrive late in the box. Create overloads in key areas by shifting position intelligently.",
                    
                    "Wide Midfielder" or "Winger" => "As a wide player, maintain optimal width (0.8-1.0 or 0.0-0.2 on Y-axis) during build-up. Create 1v1 isolation opportunities against defenders. Time diagonal runs behind the defense to stretch opponents. Position yourself at the back post for crosses from the opposite flank. Tuck inside to create overloads when appropriate.",
                    
                    "Central Attacking Midfielder" => "As a #10, find and operate in pockets of space between opposition midfield and defense. Position yourself to receive between lines. Make late runs into the box timed with wide player crosses. Create space for forwards with your movement. Adjust position to offer passing lanes to teammates in tight spaces.",
                    
                    "Striker" or "Center Forward" => "As the focal point of attack, alternate between stretching the defense with runs behind and dropping to link play. Position yourself between and behind center backs to create confusion. Time runs to exploit gaps between defenders. Position your body to protect the ball with your back to goal. Create shooting angles through intelligent movement.",
                    
                    "Lone Striker" => "As a Lone Striker, master varied movements to occupy multiple defenders. Alternate between stretching the defense with runs and dropping deep to link play. Time your movements to create space for midfield runners. Position yourself to receive with back to goal when needed. Create separation from defenders using quick changes of direction.",
                    
                    "Wing Forward" => "As a Wing Forward, maintain high and wide positions to stretch defenses. Time diagonal runs behind fullbacks. Position yourself between fullback and centerback to create decision problems. Create separation using quick accelerations and changes of direction. Adjust position to attack the back post when crosses come from opposite side.",
                    
                    _ => "Maintain excellent positional awareness for your role. Adapt your position dynamically based on ball location, teammate movements and opponent positions."
                };
                
                roleGuidance += "\n\n" + formationRoleGuidance;
            }
            
            // Add possession-specific instructions with enhanced retreat logic
            string possessionGuidance = "";
            bool opponentHasPossession = !string.IsNullOrEmpty(gameState.BallPossession) && 
                                     ((IsTeamA && gameState.BallPossession.StartsWith("TeamB")) || 
                                      (!IsTeamA && gameState.BallPossession.StartsWith("TeamA")));
            bool ballInOwnHalf = (IsTeamA && gameState.Ball.Position.X < 0.5) || 
                                 (!IsTeamA && gameState.Ball.Position.X > 0.5);
            bool playerInOpponentHalf = (IsTeamA && currentPosition.X > 0.5) || 
                                       (!IsTeamA && currentPosition.X < 0.5);

            if (hasPossession) {
                possessionGuidance = "You currently have the ball - make DYNAMIC, PURPOSEFUL movements! Move at HIGH SPEED with intention. Look for the most direct path to goal, take on defenders with quick direction changes, attempt shots from promising positions, or make creative passes. Your movement should be FAST and DECISIVE!";
            }
            else if (opponentHasPossession) {
                // Enhanced retreat logic when opponent has the ball
                if (playerInOpponentHalf) {
                    possessionGuidance = "URGENT DEFENSIVE TRANSITION! Opponent has possession while you're in their half. Make an IMMEDIATE defensive recovery run toward your own half. Your PRIMARY objective is to regain defensive shape.";
                    
                    if (ballInOwnHalf) {
                        possessionGuidance += " Ball is in your defensive half - this is CRITICAL! Sprint back to defensive position at maximum speed!";
                    }
                    
                    // Role-specific retreat instructions
                    if (Role == "Forward") {
                        possessionGuidance += " As a forward, retreat to at least the halfway line to provide defensive support.";
                    }
                    else if (Role == "Midfielder") {
                        possessionGuidance += " As a midfielder, recover central position immediately to shield your defensive line.";
                    }
                }
                else if (ballInOwnHalf) {
                    possessionGuidance = "DEFENSIVE EMERGENCY! Opponent has possession in your half. Take up compact defensive position, close passing lanes, and support teammates. Your movement must prioritize defensive solidarity over attacking options.";
                }
                else {
                    possessionGuidance = "Opponent has possession, but not in your half yet. Maintain defensive shape, track offensive players, and prepare to drop deeper if opponent advances. Stay connected with teammates to maintain compact defensive structure.";
                }
            }
            else {
                // Neither team has clear possession
                possessionGuidance = "Ball is loose - be PROACTIVE! Position yourself to either win possession or quickly transition to defensive shape if opponent gains the ball. Your movement should anticipate the next phase of play!";
                
                if (playerInOpponentHalf && ballInOwnHalf) {
                    possessionGuidance += " WARNING: You're advanced while the ball is in your defensive half - strongly consider retreating to provide defensive support!";
                }
            }

            // Get tactical nuance
            string tacticalNuance = "";
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

            // Determine current zone and ideal zone
            string currentZone = DetermineCurrentZone(currentPosition, IsTeamA);
            string idealZone = DetermineIdealZoneForRole(Role, IsTeamA);

            // Special spatial instructions based on role and formation 
            string spatialInstructions = GetSpatialInstructionsForRole(Role, FormationRole, IsTeamA, CurrentFormation);

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
                BasePosition.Y,  // {21}
                currentZone, // {22}
                idealZone, // {23}
                spatialInstructions // {24}
            );
        }
        
        private string DetermineCurrentZone(Position position, bool isTeamA)
        {
            // Determine the current third (defensive, middle, attacking) based on X position
            string thirdX;
            if ((isTeamA && position.X < 0.3) || (!isTeamA && position.X > 0.7))
            {
                thirdX = "Defensive Third";
            }
            else if ((isTeamA && position.X > 0.7) || (!isTeamA && position.X < 0.3))
            {
                thirdX = "Attacking Third";
            }
            else
            {
                thirdX = "Middle Third";
            }

            // Determine vertical zone (left, center, right) based on Y position
            string zoneY;
            if (position.Y < 0.3)
            {
                zoneY = "Left Flank";
            }
            else if (position.Y > 0.7)
            {
                zoneY = "Right Flank";
            }
            else
            {
                zoneY = "Central Area";
            }

            return $"{thirdX}, {zoneY}";
        }

        private string DetermineIdealZoneForRole(string role, bool isTeamA)
        {
            switch (role)
            {
                case "Goalkeeper":
                    return isTeamA ? "Defensive Third (X: 0.0-0.1), Central Area" : "Defensive Third (X: 0.9-1.0), Central Area";
                case "Defender":
                    return isTeamA ? "Defensive Third (X: 0.1-0.3), varies by position" : "Defensive Third (X: 0.7-0.9), varies by position";
                case "Midfielder":
                    return "Middle Third (X: 0.3-0.7), varies by specific midfield role";
                case "Forward":
                    return isTeamA ? "Attacking Third (X: 0.7-1.0), varies by forward role" : "Attacking Third (X: 0.0-0.3), varies by forward role";
                default:
                    return "Varies based on specific role";
            }
        }

        private string GetSpatialInstructionsForRole(string role, string formationRole, bool isTeamA, TeamFormation formation)
        {
            string baseInstructions = "";
            
            // Add formation-specific positioning for each role
            switch (formation)
            {
                case TeamFormation.Formation_4_4_2:
                    if (role == "Midfielder" && (formationRole == "Central Midfielder" || formationRole.Contains("Central")))
                    {
                        baseInstructions = "In the 4-4-2, central midfielders must maintain compact horizontal spacing (no more than 0.15 apart in Y-axis). Maintain a central position to provide defensive cover and passing options. When possession is lost, your first movement must be to recover centrally.";
                    }
                    else if (role == "Forward")
                    {
                        baseInstructions = "In the 4-4-2, forwards should work as a pair - one can drop deeper while the other stretches the defense. Maintain approximately 0.2 separation in the Y-axis for optimal spacing. When team loses possession, immediately drop back toward midfield to apply pressure and prevent easy progression.";
                    }
                    break;
                    
                case TeamFormation.Formation_4_3_3:
                    if (role == "Forward" && formationRole == "Wing Forward")
                    {
                        baseInstructions = "In the 4-3-3, wing forwards should maintain high and wide positions (Y: 0.15-0.25 or 0.75-0.85) to stretch the defense when in possession. Tuck inside to create compact defensive shape when defending. When possession is lost, immediately track back along your flank.";
                    }
                    else if (role == "Midfielder")
                    {
                        baseInstructions = "In the 4-3-3, midfielders form a triangle - with defensive midfielder behind and two ahead. Maintain triangular spacing for passing options. On losing possession, the entire midfield must quickly recover position to prevent counter-attacks.";
                    }
                    break;
                    
                case TeamFormation.Formation_4_2_3_1:
                    if (role == "Midfielder" && formationRole == "Defensive Midfielder")
                    {
                        baseInstructions = "In the 4-2-3-1, defensive midfielders work as a double pivot with balanced spacing. One can step forward while the other covers, but never both forward simultaneously. Your defensive positioning is critical - always prioritize defensive shape over attacking opportunities.";
                    }
                    else if (role == "Midfielder" && formationRole == "Central Attacking Midfielder")
                    {
                        baseInstructions = "In the 4-2-3-1, as the #10, find and operate in spaces between lines. Position yourself centrally but float to either side based on where space opens up. When possession is lost, you must immediately drop to connect with the defensive midfielders.";
                    }
                    break;
                    
                case TeamFormation.Formation_3_5_2:
                    if (formationRole == "Wing Back")
                    {
                        baseInstructions = "In the 3-5-2, wing backs must provide width in attack and defense across the ENTIRE flank. Your vertical positioning should be balanced with the opposite wing back - if one advances, the other should be more conservative. Your recovery sprints after possession loss are CRITICAL.";
                    }
                    else if (role == "Defender" && formationRole == "Center Back")
                    {
                        baseInstructions = "In the 3-5-2, the central defender of the three should hold position while the wide center backs can step into midfield when appropriate. Maintain triangular spacing between the three defenders. Never all push forward simultaneously.";
                    }
                    break;
                    
                default:
                    // No specific formation instructions
                    break;
            }
            
            // Add universal spatial instructions for each role
            string universalInstructions = role switch
            {
                "Goalkeeper" => "As goalkeeper, position yourself on an imaginary arc between the goalposts, adjusting based on ball position. Come off your line decisively for through balls but maintain goal coverage.",
                
                "Defender" => "As a defender, position yourself to see both the ball and attacking players simultaneously. Maintain the defensive line with teammates, stepping up in unison for offside traps. Cover space behind teammates who step forward. When possession changes, your first priority is to reestablish defensive shape.",
                
                "Midfielder" => "As a midfielder, constantly scan and adjust position to create passing triangles with teammates. Find pockets of space between opponent lines. Cover defensively when teammates push forward to maintain team balance. When possession is lost, your FIRST MOVEMENT must be defensive recovery - never remain high up the pitch when the opponent counters.",
                
                "Forward" => "As a forward, make diagonal runs that start from outside defender's vision. Use quick changes of pace and direction to create separation. Position yourself between defenders to force them to make decisions. During defensive phases, drop back to at least midfield position (X: 0.5) to provide an outlet and defensive support.",
                
                _ => "Maintain optimal spacing with teammates. Adjust position constantly based on ball location and teammates' movements."
            };
            
            // Add defensive transition guidance for EVERY role
            string defensiveTransitionGuidance = role switch
            {
                "Goalkeeper" => "When possession is lost, immediately assess if you need to take a deeper position to prepare for shots or through balls.",
                
                "Defender" => "When possession is lost, your first priority is to reestablish defensive shape and track runners. Never get caught forward during opposition counter-attacks.",
                
                "Midfielder" => "When possession is lost, immediately transition to defensive shape. If you're in the opponent's half, sprint back to at least the midfield line. This defensive recovery is MORE IMPORTANT than attacking positioning.",
                
                "Forward" => "When possession is lost, you must drop back to support team defense, especially if you're in the opponent's half. Your defensive recovery run should target the midfield line (X: 0.5) as a minimum retreat position.",
                
                _ => "When possession is lost, prioritize defensive recovery position over attacking opportunities."
            };
            
            // Combine instructions
            if (!string.IsNullOrEmpty(baseInstructions))
            {
                return baseInstructions + "\n\n" + universalInstructions + "\n\n" + defensiveTransitionGuidance;
            }
            
            return universalInstructions + "\n\n" + defensiveTransitionGuidance;
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