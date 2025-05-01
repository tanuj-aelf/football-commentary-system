// Global state variables for game data and animation
let latestGameState = null;
let previousGameState = null;
let interpolatedState = null; // Added for smooth interpolation
let interpolationProgress = 0; // Progress between states (0 to 1)
let lastUpdateTime = 0; // Timestamp of last state update
let stateDuration = 50; // Reduced further from 70ms
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
let kickoffAnimationDuration = 5000; // Duration of kickoff animation in ms (increased to 5 seconds for more realistic movement)
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
    // Increase these values to allow more players near the ball but prevent complete swarming
    const maxAttackingPlayersNearBall = 4; // Increased from 3 to allow more offensive options
    const maxDefendingPlayersNearBall = 3; // Increased from 2 to allow more defense but not overwhelming
    
    // Different thresholds for different areas - adjusted to provide more space
    const veryCloseThreshold = 0.05; // Reduced from 0.06 - minimum distance players can get to ball carrier
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
                
                // Respect team half boundaries - home team should generally stay on left side
                if (player.position.x > 0.55 && result.status !== 4) { // Not during goal celebration
                    // Gently nudge player back to their side (unless they're attacking)
                    if (!isHomeTeamPossession || distance > 0.2) {
                        player.position.x = Math.max(player.position.x - 0.01, 0.45);
                    }
                }
                
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
                
                // Respect team half boundaries - away team should generally stay on right side
                if (player.position.x < 0.45 && result.status !== 4) { // Not during goal celebration
                    // Gently nudge player back to their side (unless they're attacking)
                    if (!isAwayTeamPossession || distance > 0.2) {
                        player.position.x = Math.min(player.position.x + 0.01, 0.55);
                    }
                }
                
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
                    // Too close, move them apart - use gentler force
                    const dist = Math.sqrt(distSquared);
                    const moveX = dx / dist * (minDistance - dist) * 0.25; // Reduced from 0.3
                    const moveY = dy / dist * (minDistance - dist) * 0.25; // Reduced from 0.3
                    
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
    
    // Only apply crowding prevention in normal play (not during kickoffs or goal celebrations)
    // Check for status InProgress (1) and not recently after a goal or restart
    if (result.status === 1 && !matchRestarting) {
        // Give special protection to the player with the ball
        const ballPossessorId = result.ballPossession;
        if (ballPossessorId) {
            // Find possessing player team
            const isHomePossession = ballPossessorId.startsWith('TeamA');
            const playerWithBall = isHomePossession 
                ? result.homeTeam?.players?.find(p => p.playerId === ballPossessorId)
                : result.awayTeam?.players?.find(p => p.playerId === ballPossessorId);
                
            if (playerWithBall) {
                // Create a protective bubble around ball possessor
                const opposingPlayers = isHomePossession ? awayTeamNearBall : homeTeamNearBall;
                
                // Keep track of how many defenders are already close
                let closeDefenderCount = 0;
                
                // Process each opposing player
                opposingPlayers.forEach(defender => {
                    const dx = defender.position.x - playerWithBall.position.x;
                    const dy = defender.position.y - playerWithBall.position.y;
                    const distance = Math.sqrt(dx * dx + dy * dy);
                    
                    // If too close to ball carrier and we already have enough defenders
                    if (distance < veryCloseThreshold && closeDefenderCount >= 1) {
                        // Push defender away more strongly
                        const pushFactor = 0.01; // Reduced from original repulsion values
                        const pushX = dx === 0 ? (Math.random() - 0.5) * 0.01 : dx / Math.abs(dx) * pushFactor;
                        const pushY = dy === 0 ? (Math.random() - 0.5) * 0.01 : dy / Math.abs(dy) * pushFactor;
                        
                        defender.position.x += pushX;
                        defender.position.y += pushY;
                    }
                    
                    // Count defenders that are close enough to challenge
                    if (distance < veryCloseThreshold * 1.5) {
                        closeDefenderCount++;
                    }
                });
            }
        }

        // Regular swarming prevention logic - with reduced forces
        // Move excess home team players away from the ball - with less force
        if (homeTeamNearBall.length > maxHomeTeamNearBall) {
            // First check if any players are too close to the ball
            for (let i = 0; i < homeTeamNearBall.length; i++) {
                const player = homeTeamNearBall[i];
                if (player._distanceToBall < veryCloseThreshold) {
                    // This player is too close to the ball, move them away gently
                    const dirX = player.position.x - ballPos.x;
                    const dirY = player.position.y - ballPos.y;
                    const mag = Math.sqrt(dirX * dirX + dirY * dirY);
                    
                    if (mag > 0.001) {
                        // Normalize and use as movement direction
                        const normX = dirX / mag;
                        const normY = dirY / mag;
                        // Move player away from ball gently (reduced further)
                        player.position.x += normX * 0.01; // Reduced from 0.015
                        player.position.y += normY * 0.01; // Reduced from 0.015
                        
                        // Clamp to field boundaries
                        player.position.x = Math.max(0.05, Math.min(0.95, player.position.x));
                        player.position.y = Math.max(0.05, Math.min(0.95, player.position.y));
                    }
                }
            }
            
            // Then handle excess players beyond the allowed maximum - with gentler forces
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
                    
                    // Move player away from ball with gentler force (further reduced)
                    const forceFactor = 0.005 * (1 + (i - maxHomeTeamNearBall) * 0.05); // Reduced from 0.008
                    player.position.x += normX * forceFactor;
                    player.position.y += normY * forceFactor;
                    
                    // Reduced sideways movement
                    if (isHomeTeamPossession) {
                        // Add minimal tactical positioning
                        const sidewaysFactor = 0.002 * Math.sin(player._distanceToBall * 10); // Reduced from 0.003
                        player.position.y += sidewaysFactor;
                    }
                    
                    // Clamp to field boundaries
                    player.position.x = Math.max(0.05, Math.min(0.95, player.position.x));
                    player.position.y = Math.max(0.05, Math.min(0.95, player.position.y));
                }
            }
        }
        
        // Apply similar gentler handling for away team - with even more reduced forces for defense
        if (awayTeamNearBall.length > maxAwayTeamNearBall) {
            // First check if any players are too close to the ball
            for (let i = 0; i < awayTeamNearBall.length; i++) {
                const player = awayTeamNearBall[i];
                if (player._distanceToBall < veryCloseThreshold) {
                    // This player is too close to the ball, move them away gently
                    const dirX = player.position.x - ballPos.x;
                    const dirY = player.position.y - ballPos.y;
                    const mag = Math.sqrt(dirX * dirX + dirY * dirY);
                    
                    if (mag > 0.001) {
                        // Normalize and use as movement direction
                        const normX = dirX / mag;
                        const normY = dirY / mag;
                        // Move player away from ball gently (reduced further)
                        player.position.x += normX * 0.01; // Reduced from 0.018
                        player.position.y += normY * 0.01; // Reduced from 0.018
                        
                        // Clamp to field boundaries
                        player.position.x = Math.max(0.05, Math.min(0.95, player.position.x));
                        player.position.y = Math.max(0.05, Math.min(0.95, player.position.y));
                    }
                }
            }
            
            // Then handle excess players beyond the allowed maximum - gentler forces
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
                    
                    // Move player away from ball - with gentler force
                    const forceFactor = 0.006 * (1 + (i - maxAwayTeamNearBall) * 0.07); // Reduced from 0.012
                    player.position.x += normX * forceFactor;
                    player.position.y += normY * forceFactor;
                    
                    // Reduced tactical positioning
                    if (!isAwayTeamPossession) {
                        // Minimal tactical defensive positioning
                        const playerNumber = parseInt(player.playerId.split('_')[1], 10) || 0;
                        const spreadFactor = 0.003 * Math.sin(playerNumber * Math.PI / 5); // Reduced from 0.006
                        player.position.y += spreadFactor;
                    }
                    
                    // Clamp to field boundaries
                    player.position.x = Math.max(0.05, Math.min(0.95, player.position.x));
                    player.position.y = Math.max(0.05, Math.min(0.95, player.position.y));
                }
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
    
    const dx = pos2.x - pos1.x;
    const dy = pos2.y - pos1.y;
    
    // Use faster easing function (quadratic instead of cubic)
    const easedProgress = 1 - Math.pow(1 - progress, 2);
    
    return {
        x: pos1.x + dx * easedProgress,
        y: pos1.y + dy * easedProgress
    };
}

// Helper function to interpolate between two game states
function interpolateGameState(state1, state2, progress) {
    if (!state1 || !state2) return state2 || state1;
    
    // Create a deep copy of state1 as the base
    const result = JSON.parse(JSON.stringify(state1));
    
    // Use cubic easing for smoother transitions
    let easedProgress = 1 - Math.pow(1 - progress, 3); // Cubic ease out
    
    // Interpolate ball position - use different easing for ball
    if (state1.ball?.position && state2.ball?.position) {
        // Ball should move a bit faster than players for realism
        const ballEasedProgress = 1 - Math.pow(1 - progress, 2.5); // Slightly faster easing
        result.ball.position = interpolatePosition(state1.ball.position, state2.ball.position, ballEasedProgress);
    }
    
    // Interpolate home team player positions
    if (state1.homeTeam?.players && state2.homeTeam?.players) {
        state1.homeTeam.players.forEach((player, index) => {
            if (index < state2.homeTeam.players.length && player.position && state2.homeTeam.players[index].position) {
                // Calculate player-specific easing based on role
                // Goalkeepers move more deliberately, strikers more dynamically
                let playerEasing = easedProgress;
                const playerId = player.playerId || '';
                const playerNumber = parseInt(playerId.split('_')[1] || '0', 10);
                
                // Goalkeepers (0) move more deliberately, forwards (9, 10) more dynamically
                if (playerNumber === 0) {
                    playerEasing = 1 - Math.pow(1 - progress, 3.5); // Slower goalkeeper
                } else if (playerNumber >= 9) {
                    playerEasing = 1 - Math.pow(1 - progress, 2.8); // Quicker forwards
                }
                
                result.homeTeam.players[index].position = interpolatePosition(
                    player.position,
                    state2.homeTeam.players[index].position,
                    playerEasing
                );
            }
        });
    }
    
    // Interpolate away team player positions with similar role-based easing
    if (state1.awayTeam?.players && state2.awayTeam?.players) {
        state1.awayTeam.players.forEach((player, index) => {
            if (index < state2.awayTeam.players.length && player.position && state2.awayTeam.players[index].position) {
                // Calculate player-specific easing based on role
                let playerEasing = easedProgress;
                const playerId = player.playerId || '';
                const playerNumber = parseInt(playerId.split('_')[1] || '0', 10);
                
                // Goalkeepers (0) move more deliberately, forwards (9, 10) more dynamically
                if (playerNumber === 0) {
                    playerEasing = 1 - Math.pow(1 - progress, 3.5); // Slower goalkeeper
                } else if (playerNumber >= 9) {
                    playerEasing = 1 - Math.pow(1 - progress, 2.8); // Quicker forwards
                }
                
                result.awayTeam.players[index].position = interpolatePosition(
                    player.position,
                    state2.awayTeam.players[index].position,
                    playerEasing
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
    
    // Ensure all players are in their own half and respect the center line with minimal changes
    // Home team (TeamA) positioned on left side, Away team (TeamB) on right
    if (result.homeTeam && result.homeTeam.players) {
        result.homeTeam.players.forEach((player, index) => {
            if (player.position) {
                // Get player number
                let playerNum = 1;
                try {
                    playerNum = parseInt(player.playerId.split('_')[1], 10) || (index + 1);
                } catch (e) {
                    playerNum = index + 1;
                }
                
                // Only adjust positions if needed - make minimal changes
                // Always fix goalkeeper position
                if (playerNum === 1) {
                    player.position.x = 0.1;
                    player.position.y = 0.5;
                } 
                // For all other players
                else {
                    // Ensure TeamA players are on left side
                    if (player.position.x > 0.49) {
                        // Player is incorrectly positioned in opponent's half, fix by mirroring
                        player.position.x = 0.49 - (player.position.x - 0.49);
                    }
                    
                    // If we're the kickoff team, let one player be close to center line
                    const isKickoffTeam = kickoffTeam === "TeamA";
                    
                    // For kickoff team, position one player nearby center
                    if (isKickoffTeam && playerNum === 10) {
                        player.position.x = 0.48; // Just behind center line
                        // Keep y position with small adjustment to center
                        player.position.y = 0.5 + (player.position.y - 0.5) * 0.5;
                    }
                    
                    // Ensure players stay in bounds
                    player.position.x = Math.max(0.05, Math.min(0.49, player.position.x));
                    player.position.y = Math.max(0.05, Math.min(0.95, player.position.y));
                }
            }
        });
    }
    
    // Apply similar logic for away team (TeamB)
    if (result.awayTeam && result.awayTeam.players) {
        result.awayTeam.players.forEach((player, index) => {
            if (player.position) {
                // Get player number
                let playerNum = 1;
                try {
                    playerNum = parseInt(player.playerId.split('_')[1], 10) || (index + 1);
                } catch (e) {
                    playerNum = index + 1;
                }
                
                // Only adjust positions if needed - make minimal changes
                // Always fix goalkeeper position
                if (playerNum === 1) {
                    player.position.x = 0.9;
                    player.position.y = 0.5;
                } 
                // For all other players
                else {
                    // Ensure TeamB players are on right side
                    if (player.position.x < 0.51) {
                        // Player is incorrectly positioned in opponent's half, fix by mirroring
                        player.position.x = 0.51 + (0.51 - player.position.x);
                    }
                    
                    // If we're the kickoff team, let one player be close to center line
                    const isKickoffTeam = kickoffTeam === "TeamB";
                    
                    // For kickoff team, position one player nearby center
                    if (isKickoffTeam && playerNum === 10) {
                        player.position.x = 0.52; // Just ahead of center line
                        // Keep y position with small adjustment to center
                        player.position.y = 0.5 + (player.position.y - 0.5) * 0.5;
                    }
                    
                    // Ensure players stay in bounds
                    player.position.x = Math.max(0.51, Math.min(0.95, player.position.x));
                    player.position.y = Math.max(0.05, Math.min(0.95, player.position.y));
                }
            }
        });
    }
    
    return result;
}

// Helper function to calculate natural movement speed based on distance
function calculateMovementSpeed(currentX, currentY, targetX, targetY) {
    const dx = targetX - currentX;
    const dy = targetY - currentY;
    const distance = Math.sqrt(dx * dx + dy * dy);
    
    // Further increase base speed values for faster animations
    let speed;
    if (distance < 0.05) {
        speed = 0.0008 + distance * 0.008;  // Increased from 0.0006 + distance * 0.006
    } else if (distance < 0.2) {
        speed = 0.0022 + distance * 0.011; // Increased from 0.0018 + distance * 0.009
    } else {
        speed = 0.0045 + distance * 0.015; // Increased from 0.0035 + distance * 0.012
    }
    
    // Keep random variation
    speed *= (0.9 + Math.random() * 0.1); 
    
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
                
                // Apply cubic easing to make the motion more natural
                const easedKickoffProgress = 1 - Math.pow(1 - kickoffProgress, 3);
                
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
                                // Different roles move at different speeds
                                let speedMultiplier = 1.0;
                                if (idx === 0) speedMultiplier = 0.8; // Goalkeepers move slower
                                else if (idx >= 9) speedMultiplier = 1.2; // Forwards move faster
                                
                                const speed = playerMovementSpeeds[playerKey] * 
                                    speedMultiplier * 
                                    (kickoffProgress < 0.3 ? 1.2 : 1.0); // Initial acceleration
                                
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
                                // Different roles move at different speeds
                                let speedMultiplier = 1.0;
                                if (idx === 0) speedMultiplier = 0.8; // Goalkeepers move slower
                                else if (idx >= 9) speedMultiplier = 1.2; // Forwards move faster
                                
                                const speed = playerMovementSpeeds[playerKey] * 
                                    speedMultiplier * 
                                    (kickoffProgress < 0.3 ? 1.2 : 1.0); // Initial acceleration
                                
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
                    // Calculate smooth progress with improved easing for natural movement
                    interpolationProgress = Math.min((timestamp - lastUpdateTime) / stateDuration, 1);
                    
                    // Use cubic ease-out for more natural movement
                    // x = 1 - (1-t)³
                    if (interpolationProgress < 1) {
                        // Only apply easing if we're still interpolating
                        // Easing is now handled in the interpolateGameState function
                    }
                }
                
                // Create interpolated state with enhanced movement
                interpolatedState = interpolateGameState(previousGameState, latestGameState, interpolationProgress);
                
                // Apply anti-swarming logic to prevent too many players around the ball
                interpolatedState = preventPlayerSwarm(interpolatedState);
                
                // Add subtle natural movement to players when they seem stationary
                // This makes them look more alive even when not moving much
                if (interpolationProgress > 0.90) {
                    // Apply subtle movement only when interpolation is mostly complete
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
        const pulseScale = 1 + 0.3 * Math.sin(elapsedCelebration / 200); // Slowed down pulsing
        
        // Move players in celebration pattern (small circular movements)
        if (interpolatedState.homeTeam && interpolatedState.homeTeam.players) {
            // Determine which team is celebrating (moves more)
            const isScoringTeam = goalCelebrationTeam === "TeamA";
            const movementScale = isScoringTeam ? 1.0 : 0.3; // Scoring team moves more
            
            interpolatedState.homeTeam.players.forEach((player, idx) => {
                if (player.position) {
                    // Slower celebration movements
                    const angle = (elapsedCelebration / 400) + (idx * Math.PI / 5); // Slowed from 300
                    const radius = 0.015 * movementScale; // Reduced from 0.02
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
                    // Slower celebration movements
                    const angle = (elapsedCelebration / 500) - (idx * Math.PI / 6); // Slowed from 400
                    const radius = 0.012 * movementScale; // Reduced from 0.015
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
            const bounceFactor = Math.abs(Math.sin(elapsedCelebration / 300) * 0.04); // Slowed from 200, reduced amplitude
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
            // Use a cubic ease-out for more natural motion
            const easedProgress = 1 - Math.pow(1 - passProgress, 3);
            
            // Add an arc to the pass
            const arcHeight = 0.04; // Reduced from 0.05 for lower arc
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
    const t = timestamp / 1200; // Slowed down from 1000
    
    // Apply to home team
    if (state.homeTeam && state.homeTeam.players) {
        state.homeTeam.players.forEach((player, idx) => {
            if (player.position) {
                // Use player index to offset the oscillation
                const offset = idx * 0.7; // Increased from 0.5 for more varied movement
                
                // Different player roles move differently
                let moveFactor = 0.00008; // Base movement factor (reduced from 0.00012)
                let wobbleFactor = 0.00010; // Base wobble factor (reduced from 0.00018)
                
                // Goalkeepers move less, forwards move more
                if (idx === 0) { // Goalkeeper
                    moveFactor *= 0.5;
                    wobbleFactor *= 0.5;
                } else if (idx >= 9) { // Forwards
                    moveFactor *= 1.2;
                    wobbleFactor *= 1.2;
                }
                
                // Create natural-looking movement patterns with different frequencies
                const wobble = Math.sin(t + offset) * wobbleFactor;
                const sideMotion = Math.cos(t * 0.6 + offset) * moveFactor;
                
                // Consider ball possession - players with the ball move differently
                const hasBall = player.playerId === state.ballPossession;
                if (hasBall) {
                    // Player with ball moves more as they control it
                    player.position.x += Math.cos(t * 1.2 + offset) * moveFactor * 1.5;
                    player.position.y += Math.sin(t * 1.5 + offset) * wobbleFactor * 1.5;
                } else {
                    // Standard subtle movement
                    player.position.x += sideMotion;
                    player.position.y += wobble;
                }
            }
        });
    }
    
    // Apply to away team with slight variations
    if (state.awayTeam && state.awayTeam.players) {
        state.awayTeam.players.forEach((player, idx) => {
            if (player.position) {
                // Use player index to offset the oscillation
                const offset = idx * 0.7 + 2.1; // Different phase than home team
                
                // Different player roles move differently
                let moveFactor = 0.00008; // Base movement factor
                let wobbleFactor = 0.00010; // Base wobble factor
                
                // Goalkeepers move less, forwards move more
                if (idx === 0) { // Goalkeeper
                    moveFactor *= 0.5;
                    wobbleFactor *= 0.5;
                } else if (idx >= 9) { // Forwards
                    moveFactor *= 1.2;
                    wobbleFactor *= 1.2;
                }
                
                const wobble = Math.sin(t * 0.9 + offset) * wobbleFactor;
                const sideMotion = Math.cos(t * 0.7 + offset) * moveFactor;
                
                // Consider ball possession - players with the ball move differently
                const hasBall = player.playerId === state.ballPossession;
                if (hasBall) {
                    // Player with ball moves more as they control it
                    player.position.x += Math.cos(t * 1.3 + offset) * moveFactor * 1.5;
                    player.position.y += Math.sin(t * 1.4 + offset) * wobbleFactor * 1.5;
                } else {
                    // Standard subtle movement
                    player.position.x += sideMotion;
                    player.position.y += wobble;
                }
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