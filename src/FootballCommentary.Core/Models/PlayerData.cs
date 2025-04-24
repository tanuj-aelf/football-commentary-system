using System.Collections.Generic;

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
    }
} 