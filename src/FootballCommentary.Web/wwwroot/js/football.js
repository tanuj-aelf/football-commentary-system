// Global state variables for game data and animation
let latestGameState = null;
let previousGameState = null;
let interpolatedState = null; // Added for smooth interpolation
let interpolationProgress = 0; // Progress between states (0 to 1)
let lastUpdateTime = 0; // Timestamp of last state update
let stateDuration = 400; // Duration between server updates in ms (increased from 250 for slower movement)
let lastServerUpdateTime = 0; // Track when we last received a server update
let serverUpdateThreshold = 2000; // If no update in 2 seconds, reset interpolation
let lastStateSignature = ""; // To detect actual changes in state
let stateHashCounter = 0; // To track unique states
let isPassing = false;
let passData = {}; // { startX, startY, endX, endY, startTime, duration }
let canvas = null;
let ctx = null;
let animationFrameId = null; // To potentially cancel the loop if needed
let isInitialized = false; // Flag to track if canvas/animation loop is set up
let animationFrameCounter = 0; // Counter for animation frames
let goalCelebrationStart = null; // Time when goal celebration started
let goalCelebrationTeam = null; // Which team scored (for text display)
let skipLogFrames = 120; // Only log every 120 frames to reduce console spam
let lastBallPosition = null; // Track last ball position for anti-swarming logic
let matchRestarting = false; // Flag to indicate match is restarting (kickoff)
let kickoffAnimationStart = null; // Time when kickoff animation started
let kickoffAnimationDuration = 3500; // Duration of kickoff animation in ms (increased to 3.5 seconds for slower, more realistic movement)
let blockStateUpdates = false; // Flag to block state updates during crucial animations
let ignoreServerUpdatesUntil = 0; // Timestamp until which server updates should be ignored
let playerMovementSpeeds = {}; // Store movement speeds for each player to make movement natural

// Helper function to draw a circle
function drawCircle(ctx, x, y, radius, color) {
    ctx.beginPath();
    ctx.arc(x, y, radius, 0, Math.PI * 2);
    ctx.fillStyle = color;
    ctx.fill();
    ctx.closePath();
}

// Helper function to draw the football field
function drawField(ctx, width, height) {
    // Draw grass background
    ctx.fillStyle = "#4CAF50";
    ctx.fillRect(0, 0, width, height);
    
    // Draw field markings
    ctx.strokeStyle = "white";
    ctx.lineWidth = 2;
    
    // Draw outer boundary
    ctx.strokeRect(0, 0, width, height);
    
    // Draw center line
    ctx.beginPath();
    ctx.moveTo(width / 2, 0);
    ctx.lineTo(width / 2, height);
    ctx.stroke();
    
    // Draw center circle
    ctx.beginPath();
    ctx.arc(width / 2, height / 2, 50, 0, Math.PI * 2);
    ctx.stroke();
    
    // Draw goal areas
    const goalWidth = 20;
    const goalHeight = 100;
    
    // Team A goal (left)
    ctx.strokeStyle = "white";
    ctx.beginPath();
    ctx.moveTo(0, height / 2 - goalHeight / 2);
    ctx.lineTo(goalWidth, height / 2 - goalHeight / 2);
    ctx.lineTo(goalWidth, height / 2 + goalHeight / 2);
    ctx.lineTo(0, height / 2 + goalHeight / 2);
    ctx.stroke();
    
    // Team B goal (right)
    ctx.beginPath();
    ctx.moveTo(width, height / 2 - goalHeight / 2);
    ctx.lineTo(width - goalWidth, height / 2 - goalHeight / 2);
    ctx.lineTo(width - goalWidth, height / 2 + goalHeight / 2);
    ctx.lineTo(width, height / 2 + goalHeight / 2);
    ctx.stroke();
}

// Function to enforce player spacing to prevent swarming around the ball
function preventPlayerSwarm(state) {
    if (!state || !state.ball || !state.ball.position) return state;
    
    // Clone state to avoid modifying the original
    const result = JSON.parse(JSON.stringify(state));
    const ballPos = result.ball.position;
    
    // Determine which team has ball possession
    const teamWithPossession = result.ballPossession ? result.ballPossession.split('_')[0] : null;
    const isHomeTeamPossession = teamWithPossession === "TeamA";
    const isAwayTeamPossession = teamWithPossession === "TeamB";
    
    // Define the maximum number of players allowed within close range of the ball
    // Attacking team (with possession) gets more players near ball than defending team
    const maxAttackingPlayersNearBall = 3; // Team with possession
    const maxDefendingPlayersNearBall = 2; // Team without possession
    
    // Different thresholds for different areas
    const veryCloseThreshold = 0.08; // Very close to ball - minimum distance
    const closeRangeThreshold = 0.15; // Standard close range threshold
    const mediumRangeThreshold = 0.25; // Medium range for tracking players
    
    // Track players who are close to the ball
    const homeTeamNearBall = [];
    const awayTeamNearBall = [];
    
    // Calculate distances for all players to the ball
    if (result.homeTeam && result.homeTeam.players) {
        result.homeTeam.players.forEach(player => {
            if (player.position) {
                const dx = player.position.x - ballPos.x;
                const dy = player.position.y - ballPos.y;
                const distance = Math.sqrt(dx * dx + dy * dy);
                player._distanceToBall = distance;
                
                if (distance < closeRangeThreshold) {
                    homeTeamNearBall.push(player);
                }
            }
        });
    }
    
    if (result.awayTeam && result.awayTeam.players) {
        result.awayTeam.players.forEach(player => {
            if (player.position) {
                const dx = player.position.x - ballPos.x;
                const dy = player.position.y - ballPos.y;
                const distance = Math.sqrt(dx * dx + dy * dy);
                player._distanceToBall = distance;
                
                if (distance < closeRangeThreshold) {
                    awayTeamNearBall.push(player);
                }
            }
        });
    }
    
    // Sort players by distance to ball
    homeTeamNearBall.sort((a, b) => a._distanceToBall - b._distanceToBall);
    awayTeamNearBall.sort((a, b) => a._distanceToBall - b._distanceToBall);
    
    // Determine max players for each team based on possession
    const maxHomeTeamNearBall = isHomeTeamPossession ? maxAttackingPlayersNearBall : maxDefendingPlayersNearBall;
    const maxAwayTeamNearBall = isAwayTeamPossession ? maxAttackingPlayersNearBall : maxDefendingPlayersNearBall;
    
    // Enforce minimum distance between players on the same team
    function enforceMinimumDistance(players, minDistance = 0.07) {
        for (let i = 0; i < players.length; i++) {
            for (let j = i + 1; j < players.length; j++) {
                const p1 = players[i];
                const p2 = players[j];
                
                const dx = p2.position.x - p1.position.x;
                const dy = p2.position.y - p1.position.y;
                const distSquared = dx*dx + dy*dy;
                
                if (distSquared < minDistance * minDistance) {
                    // Too close, move them apart
                    const dist = Math.sqrt(distSquared);
                    const moveX = dx / dist * (minDistance - dist) / 2;
                    const moveY = dy / dist * (minDistance - dist) / 2;
                    
                    // Move both players in opposite directions
                    p1.position.x -= moveX;
                    p1.position.y -= moveY;
                    p2.position.x += moveX;
                    p2.position.y += moveY;
                    
                    // Clamp to field boundaries
                    p1.position.x = Math.max(0.05, Math.min(0.95, p1.position.x));
                    p1.position.y = Math.max(0.05, Math.min(0.95, p1.position.y));
                    p2.position.x = Math.max(0.05, Math.min(0.95, p2.position.x));
                    p2.position.y = Math.max(0.05, Math.min(0.95, p2.position.y));
                }
            }
        }
    }
    
    // First, ensure minimum distances between all players
    if (result.homeTeam && result.homeTeam.players) {
        enforceMinimumDistance(result.homeTeam.players);
    }
    
    if (result.awayTeam && result.awayTeam.players) {
        enforceMinimumDistance(result.awayTeam.players);
    }
    
    // Move excess home team players away from the ball
    if (homeTeamNearBall.length > maxHomeTeamNearBall) {
        // First check if any players are too close to the ball
        for (let i = 0; i < homeTeamNearBall.length; i++) {
            const player = homeTeamNearBall[i];
            if (player._distanceToBall < veryCloseThreshold) {
                // This player is too close to the ball, move them away immediately
                const dirX = player.position.x - ballPos.x;
                const dirY = player.position.y - ballPos.y;
                const mag = Math.sqrt(dirX * dirX + dirY * dirY);
                
                if (mag > 0.001) {
                    // Normalize and use as movement direction
                    const normX = dirX / mag;
                    const normY = dirY / mag;
                    // Move player away from ball more aggressively (but slowed down)
                    player.position.x += normX * 0.03;  // Reduced from 0.05
                    player.position.y += normY * 0.03;  // Reduced from 0.05
                    
                    // Clamp to field boundaries
                    player.position.x = Math.max(0.05, Math.min(0.95, player.position.x));
                    player.position.y = Math.max(0.05, Math.min(0.95, player.position.y));
                }
            }
        }
        
        // Then handle excess players beyond the allowed maximum
        for (let i = maxHomeTeamNearBall; i < homeTeamNearBall.length; i++) {
            const player = homeTeamNearBall[i];
            
            // Move away from ball while maintaining some natural movement
            const dirX = player.position.x - ballPos.x;
            const dirY = player.position.y - ballPos.y;
            const mag = Math.sqrt(dirX * dirX + dirY * dirY);
            
            if (mag > 0.001) {
                // Normalize and use as movement direction
                const normX = dirX / mag;
                const normY = dirY / mag;
                
                // Move player away from ball - stronger force for those further down the list (but slowed down)
                const forceFactor = 0.012 * (1 + (i - maxHomeTeamNearBall) * 0.2);  // Reduced from 0.02
                player.position.x += normX * forceFactor;
                player.position.y += normY * forceFactor;
                
                // Add a bit of sideways movement to create space for attacking (slight reduction)
                if (isHomeTeamPossession) {
                    // Add some tactical positioning - move to sides to create passing lanes
                    const sidewaysFactor = 0.006 * Math.sin(player._distanceToBall * 10);  // Reduced from 0.01
                    player.position.y += sidewaysFactor;
                }
                
                // Clamp to field boundaries
                player.position.x = Math.max(0.05, Math.min(0.95, player.position.x));
                player.position.y = Math.max(0.05, Math.min(0.95, player.position.y));
            }
        }
    }
    
    // More aggressive handling for away team (defending team) swarming
    if (awayTeamNearBall.length > maxAwayTeamNearBall) {
        // First check if any players are too close to the ball
        for (let i = 0; i < awayTeamNearBall.length; i++) {
            const player = awayTeamNearBall[i];
            if (player._distanceToBall < veryCloseThreshold) {
                // This player is too close to the ball, move them away immediately
                const dirX = player.position.x - ballPos.x;
                const dirY = player.position.y - ballPos.y;
                const mag = Math.sqrt(dirX * dirX + dirY * dirY);
                
                if (mag > 0.001) {
                    // Normalize and use as movement direction
                    const normX = dirX / mag;
                    const normY = dirY / mag;
                    // Move player away from ball more aggressively (but slowed down)
                    player.position.x += normX * 0.036; // Reduced from 0.06
                    player.position.y += normY * 0.036; // Reduced from 0.06
                    
                    // Clamp to field boundaries
                    player.position.x = Math.max(0.05, Math.min(0.95, player.position.x));
                    player.position.y = Math.max(0.05, Math.min(0.95, player.position.y));
                }
            }
        }
        
        // Then handle excess players beyond the allowed maximum - more aggressively for away team
        for (let i = maxAwayTeamNearBall; i < awayTeamNearBall.length; i++) {
            const player = awayTeamNearBall[i];
            
            // Move away from ball
            const dirX = player.position.x - ballPos.x;
            const dirY = player.position.y - ballPos.y;
            const mag = Math.sqrt(dirX * dirX + dirY * dirY);
            
            if (mag > 0.001) {
                // Normalize and use as movement direction
                const normX = dirX / mag;
                const normY = dirY / mag;
                
                // Move player away from ball - stronger force for those further down the list (but slowed down)
                // Use higher values for away team (defending team) to prevent crowding
                const forceFactor = 0.024 * (1 + (i - maxAwayTeamNearBall) * 0.3);  // Reduced from 0.04
                player.position.x += normX * forceFactor;
                player.position.y += normY * forceFactor;
                
                // If this is the defending team, add some defensive positioning (slight reduction)
                if (!isAwayTeamPossession) {
                    // Add tactical defensive formation - spread out to block passing lanes
                    const playerNumber = parseInt(player.playerId.split('_')[1], 10) || 0;
                    const spreadFactor = 0.012 * Math.sin(playerNumber * Math.PI / 5);  // Reduced from 0.02
                    player.position.y += spreadFactor;
                }
                
                // Clamp to field boundaries
                player.position.x = Math.max(0.05, Math.min(0.95, player.position.x));
                player.position.y = Math.max(0.05, Math.min(0.95, player.position.y));
            }
        }
    }
    
    // Clean up temporary properties
    if (result.homeTeam && result.homeTeam.players) {
        result.homeTeam.players.forEach(player => {
            delete player._distanceToBall;
        });
    }
    
    if (result.awayTeam && result.awayTeam.players) {
        result.awayTeam.players.forEach(player => {
            delete player._distanceToBall;
        });
    }
    
    return result;
}

// Generate a state signature to detect real changes
function getStateSignature(state) {
    if (!state) return "";
    
    let signature = "";
    
    // Add game status and possession to signature
    signature += `status:${state.status}|possession:${state.ballPossession}|`;
    
    // Add ball position
    if (state.ball && state.ball.position) {
        signature += `ball:${state.ball.position.x.toFixed(3)},${state.ball.position.y.toFixed(3)}|`;
    }
    
    // Add home team positions (sample first 3 players to reduce computation)
    if (state.homeTeam && state.homeTeam.players && state.homeTeam.players.length > 0) {
        for (let i = 0; i < Math.min(3, state.homeTeam.players.length); i++) {
            const player = state.homeTeam.players[i];
            if (player && player.position) {
                signature += `homeP${i}:${player.position.x.toFixed(3)},${player.position.y.toFixed(3)}|`;
            }
        }
    }
    
    // Add away team positions (sample first 3 players to reduce computation)
    if (state.awayTeam && state.awayTeam.players && state.awayTeam.players.length > 0) {
        for (let i = 0; i < Math.min(3, state.awayTeam.players.length); i++) {
            const player = state.awayTeam.players[i];
            if (player && player.position) {
                signature += `awayP${i}:${player.position.x.toFixed(3)},${player.position.y.toFixed(3)}|`;
            }
        }
    }
    
    return signature;
}

// Helper function to interpolate between two positions
function interpolatePosition(pos1, pos2, progress) {
    if (!pos1 || !pos2) return pos1 || pos2;
    
    // Calculate distance between positions
    const dx = pos2.x - pos1.x;
    const dy = pos2.y - pos1.y;
    const distSquared = dx * dx + dy * dy;
    
    // If positions are very far apart (teleport), don't interpolate
    if (distSquared > 0.1) {
        return pos2; // Just use the target position to avoid long visual traveling
    }
    
    return {
        x: pos1.x + (pos2.x - pos1.x) * progress,
        y: pos1.y + (pos2.y - pos1.y) * progress
    };
}

// Helper function to interpolate between two game states
function interpolateGameState(state1, state2, progress) {
    if (!state1 || !state2) return state2 || state1;
    
    // Create a deep copy of state1 as the base
    const result = JSON.parse(JSON.stringify(state1));
    
    // Interpolate ball position
    if (state1.ball?.position && state2.ball?.position) {
        result.ball.position = interpolatePosition(state1.ball.position, state2.ball.position, progress);
    }
    
    // Interpolate home team player positions
    if (state1.homeTeam?.players && state2.homeTeam?.players) {
        state1.homeTeam.players.forEach((player, index) => {
            if (index < state2.homeTeam.players.length && player.position && state2.homeTeam.players[index].position) {
                result.homeTeam.players[index].position = interpolatePosition(
                    player.position,
                    state2.homeTeam.players[index].position,
                    progress
                );
            }
        });
    }
    
    // Interpolate away team player positions
    if (state1.awayTeam?.players && state2.awayTeam?.players) {
        state1.awayTeam.players.forEach((player, index) => {
            if (index < state2.awayTeam.players.length && player.position && state2.awayTeam.players[index].position) {
                result.awayTeam.players[index].position = interpolatePosition(
                    player.position,
                    state2.awayTeam.players[index].position,
                    progress
                );
            }
        });
    }
    
    // Keep non-position data from the latest state
    result.ballPossession = state2.ballPossession;
    result.status = state2.status;
    result.gameTime = state2.gameTime;
    
    return result;
}

// Helper to find the player object currently possessing the ball
function findPossessingPlayer(gameState) {
    if (!gameState || gameState.ballPossession === null || gameState.ballPossession === "") {
        return null;
    }
    const playerId = gameState.ballPossession;
    let player = gameState.homeTeam?.players?.find(p => p.playerId === playerId);
    if (!player) {
        player = gameState.awayTeam?.players?.find(p => p.playerId === playerId);
    }
    return player;
}

// Main function to render the game field based on game state
function renderGameField(canvas, gameState, currentAnimatedBallPosition) {
    if (!ctx) {
        console.error("renderGameField called but ctx is null!");
        return;
    }
    if (!gameState) {
        console.warn("renderGameField called with null gameState");
        return;
    }

    // Log the state being rendered (less frequently)
    if (animationFrameCounter % skipLogFrames === 1) {
         console.log(`Rendering Frame: ${animationFrameCounter}, Ball Possession: ${gameState.ballPossession}, Status: ${gameState.status}`);
         if (gameState.homeTeam && gameState.homeTeam.players && gameState.homeTeam.players.length > 0) {
             const firstPlayer = gameState.homeTeam.players[0];
             console.log(`  First Home Player (${firstPlayer.playerId}) Pos: x=${firstPlayer.position?.x?.toFixed(3)}, y=${firstPlayer.position?.y?.toFixed(3)}`);
         }
         if (gameState.ball && gameState.ball.position) {
              console.log(`  Ball Pos: x=${gameState.ball.position?.x?.toFixed(3)}, y=${gameState.ball.position?.y?.toFixed(3)}`);
         }
    }

    const width = canvas.width;
    const height = canvas.height;
    
    // Clear canvas
    ctx.clearRect(0, 0, width, height);
    
    // Draw field
    drawField(ctx, width, height);
    
    // Draw players from Team A (red circles)
    if (gameState.homeTeam && gameState.homeTeam.players) {
        gameState.homeTeam.players.forEach(player => {
            const playerHasBall = gameState.ballPossession === player.playerId;
            const x = player.position.x * width;
            const y = player.position.y * height;
            const radius = playerHasBall ? 12 : 10;
            
            // Draw player circle
            drawCircle(ctx, x, y, radius, "red");
            
            // Draw player number
            try {
                const playerNumber = player.playerId.split('_')[1]; // Assumes format like TeamA_1
                ctx.fillStyle = 'white'; // Number color
                ctx.font = '10px Arial';  // Number font
                ctx.textAlign = 'center';
                ctx.textBaseline = 'middle';
                ctx.fillText(playerNumber, x, y);
            } catch (e) {
                console.warn(`Could not parse player number from ID: ${player.playerId}`);
            }

            // If this player has the ball, highlight them
            if (playerHasBall) {
                ctx.beginPath();
                ctx.arc(x, y, radius + 3, 0, Math.PI * 2);
                ctx.strokeStyle = "yellow";
                ctx.lineWidth = 2;
                ctx.stroke();
            }
        });
    }
    
    // Draw players from Team B (blue circles)
    if (gameState.awayTeam && gameState.awayTeam.players) {
        gameState.awayTeam.players.forEach(player => {
            const playerHasBall = gameState.ballPossession === player.playerId;
            const x = player.position.x * width;
            const y = player.position.y * height;
            const radius = playerHasBall ? 12 : 10;
            
            // Draw player circle
            drawCircle(ctx, x, y, radius, "blue");

            // Draw player number
            try {
                const playerNumber = player.playerId.split('_')[1]; // Assumes format like TeamB_5
                ctx.fillStyle = 'white'; // Number color
                ctx.font = '10px Arial';  // Number font
                ctx.textAlign = 'center';
                ctx.textBaseline = 'middle';
                ctx.fillText(playerNumber, x, y);
            } catch (e) {
                 console.warn(`Could not parse player number from ID: ${player.playerId}`);
            }
            
            // If this player has the ball, highlight them
            if (playerHasBall) {
                ctx.beginPath();
                ctx.arc(x, y, radius + 3, 0, Math.PI * 2);
                ctx.strokeStyle = "yellow";
                ctx.lineWidth = 2;
                ctx.stroke();
            }
        });
    }
    
    // --- Ball Drawing Logic ---
    if (currentAnimatedBallPosition) {
         // Draw ball at the animated position during a pass
         drawCircle(
             ctx,
             currentAnimatedBallPosition.x * width,
             currentAnimatedBallPosition.y * height,
             8, // Ball radius
             "white"
         );
         // Add a black outline to the animated ball
         ctx.beginPath();
         ctx.arc(
             currentAnimatedBallPosition.x * width,
             currentAnimatedBallPosition.y * height,
             8, 0, Math.PI * 2
         );
         ctx.strokeStyle = "black";
         ctx.lineWidth = 1;
         ctx.stroke();

    } else if (gameState.ball && gameState.ball.position) {
        // Draw ball based on gameState if not currently animating a pass
        const ballX = gameState.ball.position.x * width;
        const ballY = gameState.ball.position.y * height;
        const possessingPlayer = findPossessingPlayer(gameState);

        // Draw the ball even if a player possesses it (like original)
        // Highlight around the player shows possession clearly.
        let ballRadius = 8;
        let ballColor = "white";

        if (!possessingPlayer) {
             // Make ball slightly larger and outlined if free (and not mid-pass)
             ballRadius = 10;
             drawCircle(ctx, ballX, ballY, ballRadius, ballColor);
             ctx.beginPath();
             ctx.arc(ballX, ballY, ballRadius, 0, Math.PI * 2);
             ctx.strokeStyle = "black";
             ctx.lineWidth = 1;
             ctx.stroke();
         } else {
              // Draw standard ball if possessed
              drawCircle(ctx, ballX, ballY, ballRadius, ballColor);
         }
    }
    
    // Draw GOAL! text if in celebration mode
    if (gameState.status === 4 && goalCelebrationStart) {
        // Calculate goal text properties based on elapsed time
        const elapsedCelebration = performance.now() - goalCelebrationStart;
        const textScale = 1 + 0.2 * Math.sin(elapsedCelebration / 150); // Pulsating effect
        
        // Draw "GOAL!" text
        ctx.save(); // Save current context state
        
        ctx.font = `bold ${48 * textScale}px Arial`;
        ctx.fillStyle = "red";
        ctx.strokeStyle = "white";
        ctx.lineWidth = 3;
        ctx.textAlign = "center";
        ctx.textBaseline = "middle";
        
        // Determine which team scored
        let teamName = "GOAL!";
        if (goalCelebrationTeam) {
            if (goalCelebrationTeam === "TeamA") {
                teamName = gameState.homeTeam?.name || "Home Team";
                ctx.fillStyle = "#FF3333"; // Red for home team
            } else {
                teamName = gameState.awayTeam?.name || "Away Team";
                ctx.fillStyle = "#3333FF"; // Blue for away team
            }
        }
        
        // First draw text stroke for visibility
        ctx.strokeText("GOAL!", width / 2, height / 2 - 30);
        ctx.fillText("GOAL!", width / 2, height / 2 - 30);
        
        // Draw scored by text
        ctx.font = `bold ${24 * textScale}px Arial`;
        ctx.strokeText(`${teamName} SCORES!`, width / 2, height / 2 + 20);
        ctx.fillText(`${teamName} SCORES!`, width / 2, height / 2 + 20);
        
        ctx.restore(); // Restore context state
    }
}

// Helper function to apply kickoff formation
function applyKickoffFormation(state) {
    if (!state) return state;
    
    // Clone state to avoid modifying the original
    const result = JSON.parse(JSON.stringify(state));
    
    // Determine which team scored last (if any) to determine kickoff team
    // After a goal, the team that conceded takes the kickoff
    const kickoffTeam = goalCelebrationTeam === "TeamA" ? "TeamB" : "TeamA";
    
    // Center the ball
    if (result.ball && result.ball.position) {
        result.ball.position.x = 0.5; // Center X
        result.ball.position.y = 0.5; // Center Y
    }
    
    // Clear ball possession
    result.ballPossession = null;
    
    // Position home team (TeamA) players in kickoff formation
    if (result.homeTeam && result.homeTeam.players) {
        result.homeTeam.players.forEach((player, index) => {
            if (player.position) {
                // Get player number for positioning
                let playerNum = 1;
                try {
                    playerNum = parseInt(player.playerId.split('_')[1], 10) || (index + 1);
                } catch (e) {
                    playerNum = index + 1;
                }
                
                // Apply kickoff formation based on player number
                const position = getKickoffPosition(playerNum, true, kickoffTeam === "TeamA");
                player.position.x = position.x;
                player.position.y = position.y;
            }
        });
    }
    
    // Position away team (TeamB) players in kickoff formation
    if (result.awayTeam && result.awayTeam.players) {
        result.awayTeam.players.forEach((player, index) => {
            if (player.position) {
                // Get player number for positioning
                let playerNum = 1;
                try {
                    playerNum = parseInt(player.playerId.split('_')[1], 10) || (index + 1);
                } catch (e) {
                    playerNum = index + 1;
                }
                
                // Apply kickoff formation based on player number
                const position = getKickoffPosition(playerNum, false, kickoffTeam === "TeamB");
                player.position.x = position.x;
                player.position.y = position.y;
            }
        });
    }
    
    return result;
}

// Helper function to get kickoff positions for players
function getKickoffPosition(playerNumber, isHomeTeam, hasPossession) {
    // Standard 4-4-2 formation with kickoff positions
    const position = { x: 0, y: 0 };
    
    if (isHomeTeam) {
        // Home team (TeamA) is on the left side
        switch (playerNumber) {
            case 1: // Goalkeeper
                position.x = 0.1;
                position.y = 0.5;
                break;
            case 2: // Right back
                position.x = 0.2;
                position.y = 0.25;
                break;
            case 3: // Center back
                position.x = 0.2;
                position.y = 0.4;
                break;
            case 4: // Center back
                position.x = 0.2;
                position.y = 0.6;
                break;
            case 5: // Left back
                position.x = 0.2;
                position.y = 0.75;
                break;
            case 6: // Right midfielder
                position.x = 0.35;
                position.y = 0.25;
                break;
            case 7: // Center midfielder
                position.x = 0.35;
                position.y = 0.4;
                break;
            case 8: // Center midfielder
                position.x = 0.35;
                position.y = 0.6;
                break;
            case 9: // Left midfielder
                position.x = 0.35;
                position.y = 0.75;
                break;
            case 10: // Striker
                position.x = hasPossession ? 0.45 : 0.4;
                position.y = 0.4;
                break;
            case 11: // Striker
                position.x = hasPossession ? 0.45 : 0.4;
                position.y = 0.6;
                break;
            default:
                // Fallback positioning
                position.x = 0.3;
                position.y = 0.5 + (playerNumber * 0.05);
        }
    } else {
        // Away team (TeamB) is on the right side
        switch (playerNumber) {
            case 1: // Goalkeeper
                position.x = 0.9;
                position.y = 0.5;
                break;
            case 2: // Right back
                position.x = 0.8;
                position.y = 0.25;
                break;
            case 3: // Center back
                position.x = 0.8;
                position.y = 0.4;
                break;
            case 4: // Center back
                position.x = 0.8;
                position.y = 0.6;
                break;
            case 5: // Left back
                position.x = 0.8;
                position.y = 0.75;
                break;
            case 6: // Right midfielder
                position.x = 0.65;
                position.y = 0.25;
                break;
            case 7: // Center midfielder
                position.x = 0.65;
                position.y = 0.4;
                break;
            case 8: // Center midfielder
                position.x = 0.65;
                position.y = 0.6;
                break;
            case 9: // Left midfielder
                position.x = 0.65;
                position.y = 0.75;
                break;
            case 10: // Striker
                position.x = hasPossession ? 0.55 : 0.6;
                position.y = 0.4;
                break;
            case 11: // Striker
                position.x = hasPossession ? 0.55 : 0.6;
                position.y = 0.6;
                break;
            default:
                // Fallback positioning
                position.x = 0.7;
                position.y = 0.5 + (playerNumber * 0.05);
        }
    }
    
    return position;
}

// Helper function to calculate natural movement speed based on distance
function calculateMovementSpeed(currentX, currentY, targetX, targetY) {
    const dx = targetX - currentX;
    const dy = targetY - currentY;
    const distance = Math.sqrt(dx * dx + dy * dy);
    
    // Base speed plus distance-based adjustment
    // Slower for small movements, faster for longer distances
    // All values reduced by ~40% for more realistic movement
    let speed;
    if (distance < 0.05) {
        // Very close - move very slowly
        speed = 0.0006 + distance * 0.006;  // Reduced from 0.001 + distance * 0.01
    } else if (distance < 0.2) {
        // Medium distance - normal movement
        speed = 0.0018 + distance * 0.009;  // Reduced from 0.003 + distance * 0.015
    } else {
        // Far away - faster movement
        speed = 0.004 + distance * 0.012;  // Reduced from 0.007 + distance * 0.02
    }
    
    // Add a small random variation for more natural movement
    speed *= (0.9 + Math.random() * 0.2);
    
    return speed;
}

// Helper function to get player ID key for movement tracking
function getPlayerKey(teamId, playerIdx) {
    return `${teamId}_${playerIdx}`;
}

// Animation loop using requestAnimationFrame
function animationLoop(timestamp) {
    animationFrameCounter++;
    if (animationFrameCounter % skipLogFrames === 0) { // Reduced logging frequency
        console.log(`Animation loop running - Frame: ${animationFrameCounter}`);
    }

    // Guard against running if not initialized or context lost
    if (!isInitialized || !ctx || !latestGameState) { 
        console.warn(`Animation loop called but not initialized or context missing. Frame: ${animationFrameCounter}`);
        // Optionally try to re-initialize or stop the loop
        // animationFrameId = requestAnimationFrame(animationLoop); 
        return; 
    }
    
    // Check if we've gone too long without a server update
    const timeSinceLastUpdate = timestamp - lastServerUpdateTime;
    if (timeSinceLastUpdate > serverUpdateThreshold && !matchRestarting) {
        // If it's been too long since the last server update, stop interpolating
        // but don't do this during kickoff animation
        interpolationProgress = 1;
        interpolatedState = latestGameState;
        
        if (animationFrameCounter % skipLogFrames === 0) {
            console.log(`Server update timeout (${timeSinceLastUpdate.toFixed(0)}ms) - using latest state directly`);
        }
    } else {
        // Calculate interpolation progress
        if (previousGameState && latestGameState) {
            // Handle kickoff animation first (takes precedence)
            if (matchRestarting && kickoffAnimationStart) {
                // During kickoff, ensure we use the kickoff formation regardless of other updates
                const kickoffProgress = Math.min((timestamp - kickoffAnimationStart) / kickoffAnimationDuration, 1);
                
                if (kickoffProgress >= 1) {
                    // Kickoff animation complete
                    matchRestarting = false;
                    kickoffAnimationStart = null;
                    blockStateUpdates = false;
                    
                    // Start accepting server updates again
                    ignoreServerUpdatesUntil = 0;
                    console.log("Kickoff complete - resuming normal game updates");
                    
                    // Reset the interpolation to use latest state
                    interpolationProgress = 1;
                    previousGameState = latestGameState;
                } else {
                    // Block state updates until kickoff is complete
                    blockStateUpdates = true;
                    
                    // Apply kickoff formation with natural movement
                    const kickoffState = applyKickoffFormation(latestGameState);
                    
                    // Use the current interpolated state as the base to avoid jumps
                    if (!interpolatedState) {
                        interpolatedState = JSON.parse(JSON.stringify(latestGameState));
                    }
                    
                    // Smoothly move players to their kickoff positions using variable speeds
                    if (interpolatedState.homeTeam && interpolatedState.homeTeam.players && 
                        kickoffState.homeTeam && kickoffState.homeTeam.players) {
                        interpolatedState.homeTeam.players.forEach((player, idx) => {
                            if (player.position && idx < kickoffState.homeTeam.players.length && 
                                kickoffState.homeTeam.players[idx].position) {
                                
                                const target = kickoffState.homeTeam.players[idx].position;
                                const playerKey = getPlayerKey("TeamA", idx);
                                
                                // Calculate or retrieve movement speed
                                if (!playerMovementSpeeds[playerKey]) {
                                    playerMovementSpeeds[playerKey] = calculateMovementSpeed(
                                        player.position.x, 
                                        player.position.y, 
                                        target.x, 
                                        target.y
                                    );
                                }
                                
                                // Apply natural easing movement with variable speed
                                const speed = playerMovementSpeeds[playerKey] * (kickoffProgress < 0.3 ? 1.5 : 1.0);
                                const dx = target.x - player.position.x;
                                const dy = target.y - player.position.y;
                                
                                // Ease movement as player gets closer to target
                                const distToTarget = Math.sqrt(dx*dx + dy*dy);
                                const moveStep = Math.min(distToTarget, speed);
                                
                                if (distToTarget > 0.001) {
                                    player.position.x += (dx / distToTarget) * moveStep;
                                    player.position.y += (dy / distToTarget) * moveStep;
                                }
                            }
                        });
                    }
                    
                    if (interpolatedState.awayTeam && interpolatedState.awayTeam.players && 
                        kickoffState.awayTeam && kickoffState.awayTeam.players) {
                        interpolatedState.awayTeam.players.forEach((player, idx) => {
                            if (player.position && idx < kickoffState.awayTeam.players.length && 
                                kickoffState.awayTeam.players[idx].position) {
                                
                                const target = kickoffState.awayTeam.players[idx].position;
                                const playerKey = getPlayerKey("TeamB", idx);
                                
                                // Calculate or retrieve movement speed
                                if (!playerMovementSpeeds[playerKey]) {
                                    playerMovementSpeeds[playerKey] = calculateMovementSpeed(
                                        player.position.x, 
                                        player.position.y, 
                                        target.x, 
                                        target.y
                                    );
                                }
                                
                                // Apply natural easing movement with variable speed
                                const speed = playerMovementSpeeds[playerKey] * (kickoffProgress < 0.3 ? 1.5 : 1.0);
                                const dx = target.x - player.position.x;
                                const dy = target.y - player.position.y;
                                
                                // Ease movement as player gets closer to target
                                const distToTarget = Math.sqrt(dx*dx + dy*dy);
                                const moveStep = Math.min(distToTarget, speed);
                                
                                if (distToTarget > 0.001) {
                                    player.position.x += (dx / distToTarget) * moveStep;
                                    player.position.y += (dy / distToTarget) * moveStep;
                                }
                            }
                        });
                    }
                    
                    // Move ball to center
                    if (interpolatedState.ball && interpolatedState.ball.position) {
                        interpolatedState.ball.position.x = 0.5; // Center X
                        interpolatedState.ball.position.y = 0.5; // Center Y
                    }
                    
                    // Clear ball possession during kickoff
                    interpolatedState.ballPossession = null;
                }
            } else {
                // Regular interpolation (not during kickoff)
                // Reset player movement speeds when not in kickoff
                playerMovementSpeeds = {};
                
                // If this is a new update, reset the timer
                if (timestamp - lastUpdateTime > stateDuration) {
                    // Only use interpolation for small movements to avoid jumps during teleports
                    interpolationProgress = 1;  // Fully transition to latestGameState
                    previousGameState = latestGameState; // Reset previous to latest to prepare for next update
                    lastUpdateTime = timestamp;
                } else {
                    // Calculate smooth progress with easeOutQuad function for natural movement
                    interpolationProgress = Math.min((timestamp - lastUpdateTime) / stateDuration, 1);
                    interpolationProgress = interpolationProgress * (2 - interpolationProgress); // Ease out quad
                }
                
                // Create interpolated state
                interpolatedState = interpolateGameState(previousGameState, latestGameState, interpolationProgress);
                
                // Apply anti-swarming logic to prevent too many players around the ball
                interpolatedState = preventPlayerSwarm(interpolatedState);
                
                // Add subtle natural movement to players when they seem stationary
                // This makes them look more alive even when not moving much
                if (interpolationProgress > 0.95) {
                    // Apply subtle movement only when interpolation is nearly complete
                    applySubtleMovement(interpolatedState, timestamp);
                }
            }
        } else {
            // If we don't have two states to interpolate between, just use the latest
            interpolatedState = latestGameState;
        }
    }

    let currentAnimatedBallPosition = null;

    // Special handling for goal celebration (status = 4)
    if (latestGameState.status === 4 && goalCelebrationStart) {
        const elapsedCelebration = timestamp - goalCelebrationStart;
        // Pulsating effect on the ball during celebration
        const pulseScale = 1 + 0.3 * Math.sin(elapsedCelebration / 150); 
        
        // Move players in celebration pattern (small circular movements)
        if (interpolatedState.homeTeam && interpolatedState.homeTeam.players) {
            // Determine which team is celebrating (moves more)
            const isScoringTeam = goalCelebrationTeam === "TeamA";
            const movementScale = isScoringTeam ? 1.0 : 0.3; // Scoring team moves more
            
            interpolatedState.homeTeam.players.forEach((player, idx) => {
                if (player.position) {
                    const angle = (elapsedCelebration / 300) + (idx * Math.PI / 5);
                    const radius = 0.02 * movementScale; // Small movement radius
                    // Add oscillating motion to players
                    player.position.x += Math.cos(angle) * radius * 0.01;
                    player.position.y += Math.sin(angle) * radius * 0.01;
                    // Clamp positions to field boundaries
                    player.position.x = Math.max(0.05, Math.min(0.95, player.position.x));
                    player.position.y = Math.max(0.05, Math.min(0.95, player.position.y));
                }
            });
        }
        
        // Also animate away team during celebration
        if (interpolatedState.awayTeam && interpolatedState.awayTeam.players) {
            // Determine which team is celebrating (moves more)
            const isScoringTeam = goalCelebrationTeam === "TeamB";
            const movementScale = isScoringTeam ? 1.0 : 0.3; // Scoring team moves more
            
            interpolatedState.awayTeam.players.forEach((player, idx) => {
                if (player.position) {
                    const angle = (elapsedCelebration / 400) - (idx * Math.PI / 6);
                    const radius = 0.015 * movementScale; // Smaller movement radius
                    // Add oscillating motion to players
                    player.position.x += Math.cos(angle) * radius * 0.01;
                    player.position.y += Math.sin(angle) * radius * 0.01;
                    // Clamp positions to field boundaries
                    player.position.x = Math.max(0.05, Math.min(0.95, player.position.x));
                    player.position.y = Math.max(0.05, Math.min(0.95, player.position.y));
                }
            });
        }
        
        // Animate the ball for celebration
        if (interpolatedState.ball && interpolatedState.ball.position) {
            // Make the ball bounce during celebration
            const bounceFactor = Math.abs(Math.sin(elapsedCelebration / 200) * 0.05);
            currentAnimatedBallPosition = {
                x: interpolatedState.ball.position.x,
                y: interpolatedState.ball.position.y - bounceFactor
            };
        }
    }
    
    // Handle ball passing animation
    else if (isPassing && passData.startTime) {
        const passProgress = Math.min((timestamp - passData.startTime) / passData.duration, 1);
        
        // If pass is complete
        if (passProgress >= 1) {
            isPassing = false;
        } else {
            // Animate ball along pass trajectory with slight arc
            const easedProgress = passProgress * (2 - passProgress); // Ease out quad
            
            // Add an arc to the pass
            const arcHeight = 0.05; // Maximum height of the arc
            const arcFactor = Math.sin(easedProgress * Math.PI) * arcHeight;
            
            currentAnimatedBallPosition = {
                x: passData.startX + (passData.endX - passData.startX) * easedProgress,
                y: passData.startY + (passData.endY - passData.startY) * easedProgress - arcFactor
            };
        }
    }

    // Render the current interpolated state
    renderGameField(canvas, interpolatedState, currentAnimatedBallPosition);
    
    // Continue the animation loop
    animationFrameId = requestAnimationFrame(animationLoop);
}

// Function to add subtle movement to players to make them look more alive
function applySubtleMovement(state, timestamp) {
    if (!state) return;
    
    // Use timestamp to create subtle oscillation
    const t = timestamp / 1000;
    
    // Apply to home team
    if (state.homeTeam && state.homeTeam.players) {
        state.homeTeam.players.forEach((player, idx) => {
            if (player.position) {
                // Use player index to offset the oscillation
                const offset = idx * 0.5;
                const wobble = Math.sin(t + offset) * 0.00018; // Reduced from 0.0003
                
                // Apply a very subtle movement
                player.position.x += Math.cos(t * 0.7 + offset) * 0.00012; // Reduced from 0.0002
                player.position.y += wobble;
            }
        });
    }
    
    // Apply to away team
    if (state.awayTeam && state.awayTeam.players) {
        state.awayTeam.players.forEach((player, idx) => {
            if (player.position) {
                // Use player index to offset the oscillation
                const offset = idx * 0.5;
                const wobble = Math.sin(t + offset + 1.5) * 0.00018; // Reduced from 0.0003
                
                // Apply a very subtle movement
                player.position.x += Math.cos(t * 0.7 + offset + 1.5) * 0.00012; // Reduced from 0.0002
                player.position.y += wobble;
            }
        });
    }
}

/**
 * Main handler for receiving game state updates from the server
 * This can handle both SignalR push updates and direct responses from hub methods
 */
function updateGameState(newGameState) {
    // Deep copy the incoming state to avoid potential reference issues
    try {
        if (newGameState) {
            // Check if we should ignore updates during kickoff
            if (blockStateUpdates || performance.now() < ignoreServerUpdatesUntil) {
                if (animationFrameCounter % skipLogFrames === 0) {
                    console.log("Ignoring server update during kickoff or other critical animation");
                }
                return; // Skip this update
            }
            
            // Generate signature for incoming state to detect real changes
            const newSignature = getStateSignature(newGameState);
            
            // Only update if there are actual changes based on signature
            if (newSignature !== lastStateSignature) {
                if (animationFrameCounter % skipLogFrames === 0) {
                    console.log(`State change detected - updating state (signatures different)`);
                }
                
                // Store previous state for interpolation
                if (latestGameState) {
                    previousGameState = JSON.parse(JSON.stringify(latestGameState));
                }
                
                // Update the latest state
                latestGameState = JSON.parse(JSON.stringify(newGameState));
                
                // Add metadata to track state
                latestGameState._stateId = ++stateHashCounter;
                latestGameState._receivedAt = performance.now();
                
                // Store signature for future comparisons
                lastStateSignature = newSignature;
                
                // Reset interpolation
                interpolationProgress = 0;
                lastUpdateTime = performance.now();
                lastServerUpdateTime = performance.now();
                
                // Track ball position for anti-swarming logic
                if (latestGameState.ball?.position) {
                    lastBallPosition = {
                        x: latestGameState.ball.position.x,
                        y: latestGameState.ball.position.y
                    };
                }
                
                // Check for match restart conditions (after goal or match start)
                if (previousGameState && previousGameState.status !== latestGameState.status) {
                    const prevStatus = previousGameState ? previousGameState.status : -1;
                    const newStatus = latestGameState.status;
                    
                    // Status 4 = GoalScored, Status 1 = InProgress, Status 0 = NotStarted
                    // If transitioning from GoalScored to InProgress, or from NotStarted to InProgress
                    if ((prevStatus === 4 && newStatus === 1) || (prevStatus === 0 && newStatus === 1)) {
                        console.log("Match starting or restarting after goal - applying kickoff formation");
                        
                        // Reset player movement speeds for kickoff
                        playerMovementSpeeds = {};
                        
                        // Trigger kickoff animation
                        matchRestarting = true;
                        kickoffAnimationStart = performance.now();
                        
                        // Block server updates during kickoff animation and for a short period after
                        blockStateUpdates = true;
                        ignoreServerUpdatesUntil = performance.now() + kickoffAnimationDuration + 1000;
                        
                        // Skip interpolation for immediate kickoff feedback
                        interpolationProgress = 1;
                    }
                }
                
                // If status or possession changed, skip interpolation for immediate feedback
                const statusChanged = previousGameState && previousGameState.status !== latestGameState.status;
                const possessionChanged = previousGameState && previousGameState.ballPossession !== latestGameState.ballPossession;
                
                if (statusChanged || possessionChanged) {
                    interpolationProgress = 1;
                    console.log(`Critical state change (status: ${statusChanged}, possession: ${possessionChanged}) - skipping interpolation`);
                }
                
                // If this is a goal state, trigger celebration
                if (newGameState.status === 4 && !goalCelebrationStart) {
                    goalCelebrationStart = performance.now();
                    
                    // Determine which team scored based on any score changes
                    if (previousGameState && 
                        previousGameState.homeTeam?.score !== undefined && 
                        latestGameState.homeTeam?.score !== undefined) {
                        if (latestGameState.homeTeam.score > previousGameState.homeTeam.score) {
                            goalCelebrationTeam = "TeamA";
                            console.log("Home team goal celebration started!");
                        } else if (latestGameState.awayTeam?.score > previousGameState.awayTeam?.score) {
                            goalCelebrationTeam = "TeamB";
                            console.log("Away team goal celebration started!");
                        } else {
                            goalCelebrationTeam = null;
                            console.log("Goal celebration started (unknown team)!");
                        }
                    } else {
                        goalCelebrationTeam = null;
                        console.log("Goal celebration started (unknown team)!");
                    }
                    
                    // Auto-end celebration after 3 seconds
                    setTimeout(() => {
                        goalCelebrationStart = null;
                        console.log("Goal celebration ended!");
                        // Note: Keep goalCelebrationTeam to know which team should kickoff
                    }, 3000);
                }
            } else {
                // Skip updates with identical signatures to avoid repetitive animations
                if (animationFrameCounter % (skipLogFrames * 10) === 0) {
                    console.log("Received identical state update - ignoring to prevent animation repetition");
                }
                return; // Return early, don't process this update
            }
            
            // --- Initialize Canvas and Start Animation Loop ONCE --- 
            if (!isInitialized && newGameState) {
                console.log("First valid GameState received, attempting initialization...");
                canvas = document.getElementById("gameCanvas"); 
                if (canvas) {
                    ctx = canvas.getContext("2d");
                    if (ctx) {
                        console.log("Canvas context obtained successfully. Initializing animation loop.");
                        isInitialized = true; // Set flag AFTER getting context
                        
                        // Stop any previous loop (paranoid check)
                        if (animationFrameId) {
                            cancelAnimationFrame(animationFrameId);
                        }
                        // Start the animation loop
                        animationFrameId = requestAnimationFrame(animationLoop);
                        console.log("Animation loop started by updateGameState.");
                    } else {
                         console.error("Failed to get 2D context from canvas element 'gameCanvas'.");
                         // Do not set isInitialized = true, will retry on next update
                    }
                } else {
                    console.error("Initialization attempt: Canvas element with id 'gameCanvas' not found! Will retry on next update.");
                     // Do not set isInitialized = true, will retry on next update
                }
            }
        }
    } catch (error) {
        console.error("Error in updateGameState:", error);
    }
}

// Function to trigger a ball pass animation between two points
function animateBallPass(startX, startY, endX, endY, duration = 750) {
    isPassing = true;
    passData = {
        startX: startX,
        startY: startY,
        endX: endX,
        endY: endY,
        startTime: performance.now(),
        duration: duration
    };
}

// Expose methods to the global scope for Blazor to call
window.updateGameState = updateGameState;

// Initialization function - REMOVED as initialization is triggered by updateGameState
/*
function initializeGameAnimation() {
    // ... removed implementation ...
}
*/

// Ensure initialization runs after the DOM is loaded
// You might call initializeGameAnimation() from your main script
// after setting up the SignalR connection. For example:
// document.addEventListener('DOMContentLoaded', initializeGameAnimation);
// Or if using modules, export initializeGameAnimation and call it appropriately.

// If this script is loaded globally, this ensures init runs:
/* // REMOVED Automatic Initialization
if (document.readyState === "loading") {
    document.addEventListener('DOMContentLoaded', initializeGameAnimation);
} else {
    // DOMContentLoaded has already fired
    initializeGameAnimation();
}
*/ 