using FootballCommentary.Core.Models;
using Orleans;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FootballCommentary.Core.Abstractions
{
    public interface ICommentaryAgent : IGrainWithStringKey
    {
        /// <summary>
        /// Gets a description of the agent
        /// </summary>
        Task<string> GetDescriptionAsync();
        
        /// <summary>
        /// Generates commentary for a game event
        /// </summary>
        Task<CommentaryMessage> GenerateEventCommentaryAsync(GameEvent gameEvent);
        
        /// <summary>
        /// Generates a summary of the current game state
        /// </summary>
        Task<CommentaryMessage> GenerateGameSummaryAsync(string gameId);
        
        /// <summary>
        /// Generates a final match summary
        /// </summary>
        Task<CommentaryMessage> GenerateMatchSummaryAsync(string gameId);
        
        /// <summary>
        /// Generates background commentary
        /// </summary>
        Task<CommentaryMessage> GenerateBackgroundCommentaryAsync(string gameId);
        
        /// <summary>
        /// Gets recent commentary messages
        /// </summary>
        Task<List<CommentaryMessage>> GetRecentCommentaryAsync(string gameId, int count = 10);
        
        /// <summary>
        /// Initializes commentary for a new game
        /// </summary>
        Task InitializeGameCommentaryAsync(string gameId, GameState initialState);
        
        /// <summary>
        /// Processes game state updates
        /// </summary>
        Task ProcessGameStateUpdateAsync(GameState gameState);
    }
} 