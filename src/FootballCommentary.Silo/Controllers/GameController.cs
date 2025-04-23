using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using FootballCommentary.Core.Abstractions;
using FootballCommentary.Core.Models;
using System.Collections.Generic;
using Orleans;

namespace FootballCommentary.Silo.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class GameController : ControllerBase
    {
        private readonly IClusterClient _clusterClient;
        private readonly ILogger<GameController> _logger;

        public GameController(IClusterClient clusterClient, ILogger<GameController> logger)
        {
            _clusterClient = clusterClient;
            _logger = logger;
        }

        [HttpPost("create")]
        public async Task<IActionResult> CreateGame([FromBody] CreateGameRequest request)
        {
            try
            {
                var gameId = Guid.NewGuid().ToString();
                var gameStateAgent = _clusterClient.GetGrain<IGameStateAgent>(gameId);
                
                var gameState = await gameStateAgent.CreateGameAsync(request.TeamA, request.TeamB);
                
                return Ok(new { GameId = gameId, GameState = gameState });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating game: {Message}", ex.Message);
                return StatusCode(500, "Failed to create game: " + ex.Message);
            }
        }

        [HttpGet("{gameId}")]
        public async Task<IActionResult> GetGameState(string gameId)
        {
            try
            {
                var gameStateAgent = _clusterClient.GetGrain<IGameStateAgent>(gameId);
                var gameState = await gameStateAgent.GetGameStateAsync(gameId);
                
                return Ok(gameState);
            }
            catch (KeyNotFoundException)
            {
                return NotFound($"Game {gameId} not found");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting game state: {Message}", ex.Message);
                return StatusCode(500, "Failed to get game state: " + ex.Message);
            }
        }

        [HttpPost("{gameId}/start")]
        public async Task<IActionResult> StartGame(string gameId)
        {
            try
            {
                var gameStateAgent = _clusterClient.GetGrain<IGameStateAgent>(gameId);
                var gameState = await gameStateAgent.StartGameAsync(gameId);
                
                return Ok(gameState);
            }
            catch (KeyNotFoundException)
            {
                return NotFound($"Game {gameId} not found");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting game: {Message}", ex.Message);
                return StatusCode(500, "Failed to start game: " + ex.Message);
            }
        }

        [HttpPost("{gameId}/end")]
        public async Task<IActionResult> EndGame(string gameId)
        {
            try
            {
                var gameStateAgent = _clusterClient.GetGrain<IGameStateAgent>(gameId);
                var gameState = await gameStateAgent.EndGameAsync(gameId);
                
                return Ok(gameState);
            }
            catch (KeyNotFoundException)
            {
                return NotFound($"Game {gameId} not found");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error ending game: {Message}", ex.Message);
                return StatusCode(500, "Failed to end game: " + ex.Message);
            }
        }

        [HttpPost("{gameId}/kick")]
        public async Task<IActionResult> KickBall(string gameId)
        {
            try
            {
                var gameStateAgent = _clusterClient.GetGrain<IGameStateAgent>(gameId);
                await gameStateAgent.KickBallAsync(gameId);
                
                var gameState = await gameStateAgent.GetGameStateAsync(gameId);
                return Ok(gameState);
            }
            catch (KeyNotFoundException)
            {
                return NotFound($"Game {gameId} not found");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error kicking ball: {Message}", ex.Message);
                return StatusCode(500, "Failed to kick ball: " + ex.Message);
            }
        }

        [HttpPost("{gameId}/goal")]
        public async Task<IActionResult> SimulateGoal(string gameId, [FromBody] GoalRequest request)
        {
            try
            {
                var gameStateAgent = _clusterClient.GetGrain<IGameStateAgent>(gameId);
                await gameStateAgent.SimulateGoalAsync(gameId, request.TeamId);
                
                var gameState = await gameStateAgent.GetGameStateAsync(gameId);
                return Ok(gameState);
            }
            catch (KeyNotFoundException)
            {
                return NotFound($"Game {gameId} not found");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error simulating goal: {Message}", ex.Message);
                return StatusCode(500, "Failed to simulate goal: " + ex.Message);
            }
        }

        [HttpGet("{gameId}/commentary")]
        public async Task<IActionResult> GetCommentary(string gameId, [FromQuery] int count = 10)
        {
            try
            {
                var commentaryAgent = _clusterClient.GetGrain<ICommentaryAgent>(gameId);
                var commentary = await commentaryAgent.GetRecentCommentaryAsync(gameId, count);
                
                return Ok(commentary);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting commentary: {Message}", ex.Message);
                return StatusCode(500, "Failed to get commentary: " + ex.Message);
            }
        }
    }

    public class CreateGameRequest
    {
        public required string TeamA { get; set; }
        public required string TeamB { get; set; }
    }

    public class GoalRequest
    {
        public required string TeamId { get; set; }
    }
} 