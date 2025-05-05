using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FootballCommentary.Core.Abstractions;
using FootballCommentary.Core.Models;
using Microsoft.Extensions.Logging;

namespace FootballCommentary.GAgents.PlayerAgents
{
    public class PlayerAgentManager
    {
        private readonly ILogger<PlayerAgentManager> _logger;
        private readonly ILogger<PlayerAgent> _playerLogger;
        private readonly ILLMService _llmService;
        private readonly ParallelLLMRequestManager _requestManager;
        private readonly Dictionary<string, PlayerAgent> _playerAgents = new();
        
        // Maximum player agents to update with fresh LLM decisions per cycle
        private const int MAX_API_CALLS_PER_CYCLE = 9;
        
        public PlayerAgentManager(
            ILogger<PlayerAgentManager> logger,
            ILogger<PlayerAgent> playerLogger,
            ILogger<ParallelLLMRequestManager> requestManagerLogger, 
            ILLMService llmService)
        {
            _logger = logger;
            _playerLogger = playerLogger;
            _llmService = llmService;
            _requestManager = new ParallelLLMRequestManager(llmService, requestManagerLogger, MAX_API_CALLS_PER_CYCLE);
        }
        
        public bool HasPlayerAgent(string playerId) => _playerAgents.ContainsKey(playerId);
        
        public void InitializePlayerAgents(FootballCommentary.Core.Models.GameState gameState)
        {
            _logger.LogInformation("Initializing player agents for game {GameId}", gameState.GameId);
            
            // Clear existing agents if any
            _playerAgents.Clear();
            
            // Initialize Team A players
            foreach (var player in gameState.HomeTeam.Players)
            {
                int playerNumber = TryParsePlayerId(player.PlayerId);
                string role = DeterminePlayerRole(playerNumber, true);
                
                _playerAgents[player.PlayerId] = new PlayerAgent(
                    player.PlayerId,
                    _llmService,
                    _playerLogger,
                    role,
                    playerNumber,
                    isTeamA: true,
                    teamName: gameState.HomeTeam.Name);
                    
                _logger.LogDebug("Created agent for {Team} player {PlayerId} as {Role}", 
                    gameState.HomeTeam.Name, player.PlayerId, role);
            }
            
            // Initialize Team B players
            foreach (var player in gameState.AwayTeam.Players)
            {
                int playerNumber = TryParsePlayerId(player.PlayerId);
                string role = DeterminePlayerRole(playerNumber, false);
                
                _playerAgents[player.PlayerId] = new PlayerAgent(
                    player.PlayerId,
                    _llmService,
                    _playerLogger,
                    role,
                    playerNumber,
                    isTeamA: false,
                    teamName: gameState.AwayTeam.Name);
                    
                _logger.LogDebug("Created agent for {Team} player {PlayerId} as {Role}", 
                    gameState.AwayTeam.Name, player.PlayerId, role);
            }
            
            _logger.LogInformation("Initialized {Count} player agents", _playerAgents.Count);
        }
        
        public async Task<Dictionary<string, (double dx, double dy)>> GetPlayerMovementsAsync(
            FootballCommentary.Core.Models.GameState gameState)
        {
            if (_playerAgents.Count == 0)
            {
                _logger.LogWarning("No player agents initialized. Call InitializePlayerAgents first.");
                return new Dictionary<string, (double dx, double dy)>();
            }
            
            // Select which players to prioritize for API calls
            var prioritizedPlayers = PrioritizePlayersForDecisions(gameState);
            
            _logger.LogDebug("Prioritized {Count} players for LLM decisions", prioritizedPlayers.Count);
            
            // Get movement decisions in parallel
            var movements = await _requestManager.GetMovementsInParallelAsync(gameState, prioritizedPlayers);
            
            return movements;
        }
        
        private List<PlayerAgent> PrioritizePlayersForDecisions(FootballCommentary.Core.Models.GameState gameState)
        {
            var allPlayers = gameState.AllPlayers.ToList();
            
            // Calculate priority score for each player
            var playerPriorities = _playerAgents.Values.Select(agent => 
            {
                var player = allPlayers.FirstOrDefault(p => p.PlayerId == agent.PlayerId);
                if (player == null) return (agent, 0.0);
                
                double priorityScore = CalculatePlayerPriorityScore(agent, player, gameState);
                return (agent, priorityScore);
            })
            .OrderByDescending(x => x.Item2)
            .Take(MAX_API_CALLS_PER_CYCLE)
            .Select(x => x.agent)
            .ToList();
            
            return playerPriorities;
        }
        
        private double CalculatePlayerPriorityScore(
            PlayerAgent agent, 
            Player player, 
            FootballCommentary.Core.Models.GameState gameState)
        {
            double score = 0;
            
            // Highest priority: Player with the ball
            if (player.PlayerId == gameState.BallPossession)
                score += 100;
            
            // High priority: Players near the ball
            double distToBall = CalculateDistance(player.Position, gameState.Ball.Position);
            if (distToBall < 0.15)
                score += (1 - distToBall / 0.15) * 50;
            
            // Get all players positions for contextual analysis
            var teamPlayers = agent.IsTeamA ? gameState.HomeTeam.Players : gameState.AwayTeam.Players;
            var opponentPlayers = agent.IsTeamA ? gameState.AwayTeam.Players : gameState.HomeTeam.Players;
            
            // Identify game situation: attacking, defending, transition, etc.
            bool isAttacking = IsTeamAttacking(agent.IsTeamA, gameState);
            bool isTeamInPossession = IsTeamInPossession(agent.IsTeamA, gameState);
            
            // Role-based situational priorities
            if (agent.Role == "Forward" && isAttacking)
            {
                // Prioritize forwards in attacking situations
                score += 40;
                
                // Higher priority for forwards in goal-scoring positions
                if (IsInGoalScoringPosition(player, agent.IsTeamA, gameState))
                    score += 30;
                
                // Check if this forward is making a run behind defense
                if (IsMakingRunBehindDefense(player, agent.IsTeamA, gameState, opponentPlayers))
                    score += 35;
            }
            else if (agent.Role == "Midfielder" && isTeamInPossession)
            {
                // Prioritize midfielders when in possession for creative decisions
                score += 30;
                
                // Higher priority if in open space - good for passing
                if (IsInOpenSpace(player, opponentPlayers))
                    score += 20;
            }
            else if (agent.Role == "Defender" && !isTeamInPossession)
            {
                // Prioritize defenders when defending
                score += 20;
                
                // Critical if this defender is the last line of defense
                if (IsLastDefender(player, agent.IsTeamA, gameState, teamPlayers, opponentPlayers))
                    score += 25;
                
                // Critical if marking an opponent with the ball
                if (IsMarkingBallPossessor(player, agent.IsTeamA, gameState, opponentPlayers))
                    score += 30;
            }
            else if (agent.Role == "Goalkeeper")
            {
                // Goalkeeper priority based on ball distance to goal
                double ballDistToGoal = CalculateDistanceToBallFromGoal(agent.IsTeamA, gameState);
                if (ballDistToGoal < 0.3) // Ball is close to goal
                    score += (1 - ballDistToGoal / 0.3) * 50;
            }
            
            // Boost score for attacking players even when not in optimal positions
            if (agent.Role == "Forward" && !isAttacking)
                score += 15;
                
            if (agent.Role == "Midfielder")
                score += 10;
            
            // Important: Players involved in key tactical scenarios
            if (IsInKeySwitchOfPlayPosition(player, agent.IsTeamA, gameState, teamPlayers))
                score += 25;
            
            // Increase priority for players who need to reposition based on formation
            if (IsOutOfPosition(player, agent.IsTeamA, gameState))
                score += 15;
            
            return score;
        }
        
        // Helper methods for tactical analysis
        
        private bool IsTeamAttacking(bool isTeamA, FootballCommentary.Core.Models.GameState gameState)
        {
            // Team is attacking if ball is in opponent's half
            return (isTeamA && gameState.Ball.Position.X > 0.5) || 
                   (!isTeamA && gameState.Ball.Position.X < 0.5);
        }
        
        private bool IsTeamInPossession(bool isTeamA, FootballCommentary.Core.Models.GameState gameState)
        {
            if (string.IsNullOrEmpty(gameState.BallPossession)) return false;
            
            return (isTeamA && gameState.BallPossession.StartsWith("TeamA")) ||
                   (!isTeamA && gameState.BallPossession.StartsWith("TeamB"));
        }
        
        private bool IsInGoalScoringPosition(Player player, bool isTeamA, FootballCommentary.Core.Models.GameState gameState)
        {
            // For Team A, goal scoring position is near right side of field (X > 0.8)
            // For Team B, goal scoring position is near left side of field (X < 0.2)
            bool inAttackingThird = (isTeamA && player.Position.X > 0.8) || 
                                  (!isTeamA && player.Position.X < 0.2);
                                  
            // Also consider Y-position (central is better for shooting)
            bool inCentralArea = player.Position.Y > 0.3 && player.Position.Y < 0.7;
            
            return inAttackingThird && inCentralArea;
        }
        
        private bool IsMakingRunBehindDefense(
            Player player, 
            bool isTeamA, 
            FootballCommentary.Core.Models.GameState gameState,
            List<Player> opponentPlayers)
        {
            // For Team A, making a run means player is moving toward right side (X > 0.6)
            // For Team B, making a run means player is moving toward left side (X < 0.4)
            bool inAdvancedPosition = (isTeamA && player.Position.X > 0.6) || 
                                    (!isTeamA && player.Position.X < 0.4);
            
            if (!inAdvancedPosition) return false;
            
            // Check if player is behind the defensive line
            int defendersBehindPlayer = 0;
            foreach (var opponent in opponentPlayers)
            {
                if (opponent.PlayerId.EndsWith("_0")) continue; // Skip goalkeeper
                
                if ((isTeamA && opponent.Position.X > player.Position.X) ||
                    (!isTeamA && opponent.Position.X < player.Position.X))
                {
                    defendersBehindPlayer++;
                }
            }
            
            // If fewer than 2 defenders behind the player, they're behind the defense
            return defendersBehindPlayer < 2;
        }
        
        private bool IsInOpenSpace(Player player, List<Player> opponentPlayers)
        {
            // Check if player has space around them (no opponent within 0.15 units)
            const double OPEN_SPACE_THRESHOLD = 0.15;
            
            foreach (var opponent in opponentPlayers)
            {
                double distance = CalculateDistance(player.Position, opponent.Position);
                if (distance < OPEN_SPACE_THRESHOLD)
                {
                    return false; // Player is marked/pressured
                }
            }
            
            return true; // Player is in open space
        }
        
        private bool IsLastDefender(
            Player player, 
            bool isTeamA, 
            FootballCommentary.Core.Models.GameState gameState,
            List<Player> teamPlayers,
            List<Player> opponentPlayers)
        {
            // Skip if not a defender
            if (!player.PlayerId.EndsWith("_1") && 
                !player.PlayerId.EndsWith("_2") && 
                !player.PlayerId.EndsWith("_3") && 
                !player.PlayerId.EndsWith("_4"))
            {
                return false;
            }
            
            // For Team A, last defender means closest to left side (X < 0.3)
            // For Team B, last defender means closest to right side (X > 0.7)
            bool inDefensiveThird = (isTeamA && player.Position.X < 0.3) || 
                                    (!isTeamA && player.Position.X > 0.7);
            
            if (!inDefensiveThird) return false;
            
            // Get opponent with the ball
            Player? opponentWithBall = null;
            if (!string.IsNullOrEmpty(gameState.BallPossession))
            {
                opponentWithBall = opponentPlayers.FirstOrDefault(p => p.PlayerId == gameState.BallPossession);
            }
            
            if (opponentWithBall == null) return false;
            
            // Check if this player is the closest defender to the opponent with the ball
            double playerDistToBallPossessor = CalculateDistance(player.Position, opponentWithBall.Position);
            
            bool isClosest = true;
            foreach (var teammate in teamPlayers)
            {
                // Skip non-defenders and self
                if (teammate.PlayerId == player.PlayerId) continue;
                if (!teammate.PlayerId.EndsWith("_1") && 
                    !teammate.PlayerId.EndsWith("_2") && 
                    !teammate.PlayerId.EndsWith("_3") && 
                    !teammate.PlayerId.EndsWith("_4")) continue;
                
                double teammateDistToBallPossessor = CalculateDistance(teammate.Position, opponentWithBall.Position);
                if (teammateDistToBallPossessor < playerDistToBallPossessor)
                {
                    isClosest = false;
                    break;
                }
            }
            
            return isClosest;
        }
        
        private bool IsMarkingBallPossessor(
            Player player, 
            bool isTeamA, 
            FootballCommentary.Core.Models.GameState gameState,
            List<Player> opponentPlayers)
        {
            // Get opponent with the ball
            if (string.IsNullOrEmpty(gameState.BallPossession) ||
                (isTeamA && gameState.BallPossession.StartsWith("TeamA")) ||
                (!isTeamA && gameState.BallPossession.StartsWith("TeamB")))
            {
                return false; // No opponent has the ball
            }
            
            Player? opponentWithBall = opponentPlayers.FirstOrDefault(p => p.PlayerId == gameState.BallPossession);
            if (opponentWithBall == null) return false;
            
            // Check if player is close to the opponent with the ball
            double distance = CalculateDistance(player.Position, opponentWithBall.Position);
            return distance < 0.12; // Close enough to be considered marking
        }
        
        private bool IsInKeySwitchOfPlayPosition(
            Player player, 
            bool isTeamA, 
            FootballCommentary.Core.Models.GameState gameState,
            List<Player> teamPlayers)
        {
            // Check if team has possession
            if (string.IsNullOrEmpty(gameState.BallPossession) ||
                (isTeamA && !gameState.BallPossession.StartsWith("TeamA")) ||
                (!isTeamA && !gameState.BallPossession.StartsWith("TeamB")))
            {
                return false; // Team doesn't have possession
            }
            
            // Get player with the ball
            Player? playerWithBall = teamPlayers.FirstOrDefault(p => p.PlayerId == gameState.BallPossession);
            if (playerWithBall == null) return false;
            
            // Switch of play position means player is on opposite flank from ball possessor
            bool ballOnLeftSide = playerWithBall.Position.Y < 0.3;
            bool ballOnRightSide = playerWithBall.Position.Y > 0.7;
            
            if (ballOnLeftSide && player.Position.Y > 0.7)
                return true; // Ball on left, player on right
            
            if (ballOnRightSide && player.Position.Y < 0.3)
                return true; // Ball on right, player on left
            
            return false;
        }
        
        private bool IsOutOfPosition(Player player, bool isTeamA, FootballCommentary.Core.Models.GameState gameState)
        {
            // This would require knowledge of the expected formation positions
            // Simplified version: check if player is far from typical position based on role
            
            if (player.PlayerId.EndsWith("_0")) // Goalkeeper
            {
                double goalX = isTeamA ? 0.05 : 0.95;
                double distance = Math.Abs(player.Position.X - goalX);
                return distance > 0.1; // Goalkeeper too far from goal
            }
            
            // For outfield players, check based on basic positional guidelines
            int playerNumber = TryParsePlayerId(player.PlayerId);
            string role = DeterminePlayerRole(playerNumber + 1, isTeamA);
            
            if (role == "Defender")
            {
                // Defenders should generally stay in defensive half
                bool inWrongHalf = (isTeamA && player.Position.X > 0.7) || 
                                  (!isTeamA && player.Position.X < 0.3);
                return inWrongHalf;
            }
            else if (role == "Midfielder")
            {
                // Midfielders should generally stay central
                bool tooWide = player.Position.Y < 0.2 || player.Position.Y > 0.8;
                return tooWide;
            }
            
            // For forwards, being out of position is less of an issue
            return false;
        }
        
        private double CalculateDistanceToBallFromGoal(bool isTeamA, FootballCommentary.Core.Models.GameState gameState)
        {
            double goalX = isTeamA ? 0.05 : 0.95;
            double goalY = 0.5;
            
            double dx = goalX - gameState.Ball.Position.X;
            double dy = goalY - gameState.Ball.Position.Y;
            
            return Math.Sqrt(dx * dx + dy * dy);
        }
        
        private string DeterminePlayerRole(int playerNumber, bool isTeamA)
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
                    return "Unknown";
            }
        }
        
        private int TryParsePlayerId(string playerIdString)
        {
            if (string.IsNullOrEmpty(playerIdString))
            {
                return 0;
            }

            var parts = playerIdString.Split('_');
            if (parts.Length == 2 && int.TryParse(parts[1], out int id))
            {
                return id + 1; // Convert to 1-based numbers
            }
            
            _logger.LogWarning("Could not parse PlayerId from string: {PlayerIdString}", playerIdString);
            return 0;
        }
        
        private double CalculateDistance(Position pos1, Position pos2)
        {
            double dx = pos1.X - pos2.X;
            double dy = pos1.Y - pos2.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
} 