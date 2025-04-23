using FootballCommentary.Core.Models;
using Orleans;
using System.Threading.Tasks;

namespace FootballCommentary.Core.Abstractions
{
    public interface IGameStateAgent : IGrainWithStringKey
    {
        /// <summary>
        /// Creates a new game with the specified team names
        /// </summary>
        Task<GameState> CreateGameAsync(string teamA, string teamB);
        
        /// <summary>
        /// Starts the game
        /// </summary>
        Task<GameState> StartGameAsync(string gameId);
        
        /// <summary>
        /// Ends the game
        /// </summary>
        Task<GameState> EndGameAsync(string gameId);
        
        /// <summary>
        /// Gets the current state of the game
        /// </summary>
        Task<GameState> GetGameStateAsync(string gameId);
        
        /// <summary>
        /// Kicks the ball with random velocity
        /// </summary>
        Task KickBallAsync(string gameId);
        
        /// <summary>
        /// Simulates a goal for the specified team
        /// </summary>
        Task SimulateGoalAsync(string gameId, string teamId);
        
        /// <summary>
        /// Updates player positions
        /// </summary>
        Task UpdatePlayerPositionsAsync(string gameId, Dictionary<string, Position> newPositions);
        
        /// <summary>
        /// Gets a description of the agent
        /// </summary>
        Task<string> GetDescriptionAsync();
    }
} 