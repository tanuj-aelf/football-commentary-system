using System.Collections.Generic;
using System.Linq;

namespace FootballCommentary.Core.Models
{
    /// <summary>
    /// Provides static data related to players, such as names.
    /// </summary>
    public static class PlayerData
    {
        // Player names for Team A (Home Team)
        public static readonly List<string> TeamAPlayerNames = new List<string>
        {
            "Android-13", "Nexus-7", "Cyberion", "ProtoBot", "MechaPrime",
            "Synthoid-X", "Voltaron", "IronClad", "AlphaUnit", "OmegaDroid", "GuardBot-V"
        };

        // Player names for Team B (Away Team)
        public static readonly List<string> TeamBPlayerNames = new List<string>
        {
            "MetaVillain", "ByteReaper", "GlitchLord", "VirusPrime", "DataWraith",
            "CodeBreaker", "PixelTerror", "LogicBomb", "FirewallX", "RootkitRex", "Spamurai"
        };

        /// <summary>
        /// Gets the player name based on the team ID and player ID (index).
        /// Assumes player IDs are 1-based.
        /// </summary>
        /// <param name="teamId">The ID of the team (e.g., "TeamA" or "TeamB").</param>
        /// <param name="playerId">The 1-based ID of the player.</param>
        /// <returns>The player's name, or a generic name if not found.</returns>
        public static string GetPlayerName(string? teamId, int? playerId)
        {
            if (playerId == null || playerId < 1) return "a player"; // Handle null or invalid ID

            int index = playerId.Value - 1; // Convert 1-based ID to 0-based index

            if (teamId == "TeamA" && index >= 0 && index < TeamAPlayerNames.Count)
            {
                return TeamAPlayerNames[index];
            }
            else if (teamId == "TeamB" && index >= 0 && index < TeamBPlayerNames.Count)
            {
                return TeamBPlayerNames[index];
            }

            return $"Player {playerId}"; // Fallback if team ID or player ID is invalid
        }
        
        /// <summary>
        /// Gets the player ID based on the player name and team ID.
        /// </summary>
        /// <param name="teamId">The ID of the team (e.g., "TeamA" or "TeamB").</param>
        /// <param name="playerName">The name of the player to look up.</param>
        /// <returns>The 1-based player ID, or null if not found.</returns>
        public static int? GetPlayerIdByName(string teamId, string playerName)
        {
            if (string.IsNullOrEmpty(playerName)) return null;
            
            // Try to find player in Team A
            if (teamId == "TeamA" || string.IsNullOrEmpty(teamId))
            {
                int index = TeamAPlayerNames.FindIndex(name => name.Equals(playerName, System.StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    return index + 1; // Convert 0-based index to 1-based ID
                }
            }
            
            // Try to find player in Team B
            if (teamId == "TeamB" || string.IsNullOrEmpty(teamId))
            {
                int index = TeamBPlayerNames.FindIndex(name => name.Equals(playerName, System.StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    return index + 1; // Convert 0-based index to 1-based ID
                }
            }
            
            return null; // Player not found
        }
        
        /// <summary>
        /// Determines which team a player belongs to based on their name.
        /// </summary>
        /// <param name="playerName">The name of the player.</param>
        /// <returns>The team ID ("TeamA" or "TeamB"), or null if not found.</returns>
        public static string? GetTeamIdByPlayerName(string playerName)
        {
            if (string.IsNullOrEmpty(playerName)) return null;
            
            // Check Team A first
            if (TeamAPlayerNames.Any(name => name.Equals(playerName, System.StringComparison.OrdinalIgnoreCase)))
            {
                return "TeamA";
            }
            
            // Then check Team B
            if (TeamBPlayerNames.Any(name => name.Equals(playerName, System.StringComparison.OrdinalIgnoreCase)))
            {
                return "TeamB";
            }
            
            return null; // Player not found in either team
        }
    }
} 