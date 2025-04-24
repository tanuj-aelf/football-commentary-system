// Global state variables for game data and animation
let latestGameState = null;
let previousGameState = null;
let isPassing = false;
let passData = {}; // { startX, startY, endX, endY, startTime, duration }
let canvas = null;
let ctx = null;
let animationFrameId = null; // To potentially cancel the loop if needed
let isInitialized = false; // Flag to track if canvas/animation loop is set up
let animationFrameCounter = 0; // Counter for animation frames
let goalCelebrationStart = null; // Time when goal celebration started

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

    // Log the state being rendered
    if (animationFrameCounter % 60 === 1) { // Log roughly once per second, offset from loop log
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
}

// Animation loop using requestAnimationFrame
function animationLoop(timestamp) {
    animationFrameCounter++;
    if (animationFrameCounter % 60 === 0) { // Log roughly every second (assuming 60fps)
        console.log(`Animation loop running - Frame: ${animationFrameCounter}`);
    }

    // Guard against running if not initialized or context lost
    if (!isInitialized || !ctx || !latestGameState) { 
        console.warn(`Animation loop called but not initialized or context missing. Frame: ${animationFrameCounter}`);
        // Optionally try to re-initialize or stop the loop
        // animationFrameId = requestAnimationFrame(animationLoop); 
        return; 
    }

    let currentAnimatedBallPosition = null;

    // Special handling for goal celebration (status = 4)
    if (latestGameState.status === 4 && goalCelebrationStart) {
        const elapsedCelebration = timestamp - goalCelebrationStart;
        // Pulsating effect on the ball during celebration
        const pulseScale = 1 + 0.3 * Math.sin(elapsedCelebration / 150); 
        
        // We'll draw the ball at the goal position but with a pulsing effect
        renderGameField(canvas, latestGameState, null); // Render players and field
        
        // Draw a pulsating ball and optional "GOAL!" text
        if (latestGameState.ball?.position) {
            const goalX = latestGameState.ball.position.x * canvas.width;
            const goalY = latestGameState.ball.position.y * canvas.height;
            
            // Pulsating ball
            ctx.beginPath();
            ctx.arc(goalX, goalY, 10 * pulseScale, 0, Math.PI * 2);
            ctx.fillStyle = "yellow"; // Bright celebratory color
            ctx.fill();
            ctx.strokeStyle = "red";
            ctx.lineWidth = 2;
            ctx.stroke();
            
            // "GOAL!" text
            ctx.font = "bold 48px Arial";
            ctx.fillStyle = "red";
            ctx.textAlign = "center";
            ctx.textBaseline = "middle";
            ctx.fillText("GOAL!", canvas.width / 2, canvas.height / 2);
        }
        
        // Request the next frame
        animationFrameId = requestAnimationFrame(animationLoop);
        return; // Skip the normal rendering path
    }

    // Calculate ball position if a pass is in progress
    if (isPassing) {
        const elapsed = timestamp - passData.startTime;
        const t = Math.min(1, elapsed / passData.duration); // Progress factor (0 to 1)

        // Linear interpolation
        const bx = passData.startX + t * (passData.endX - passData.startX);
        const by = passData.startY + t * (passData.endY - passData.startY);
        currentAnimatedBallPosition = { x: bx, y: by };

        // Check if pass animation is complete
        if (t >= 1) {
            isPassing = false;
            // The ball visually reaches the destination. The *next* gameState update
            // from the server should confirm the receiver has possession or the ball is loose.
        }
    }

    // Render the entire field with the potentially animated ball position
    renderGameField(canvas, latestGameState, currentAnimatedBallPosition);

    // Request the next frame
    animationFrameId = requestAnimationFrame(animationLoop);
}

/**
 * Main handler for receiving game state updates from the server
 * This can handle both SignalR push updates and direct responses from hub methods
 */
function updateGameState(newGameState) {
    // Deep copy the incoming state to avoid potential reference issues
    if (newGameState) {
        try {
            // Convert any custom types like GameStateUpdate to plain object
            // By serializing and deserializing to JSON
            if (typeof newGameState === 'object') {
                // If it's a SignalR GameStateUpdate object (has different structure)
                if (newGameState.hasOwnProperty('gameId') && 
                    newGameState.hasOwnProperty('ballPosition') && 
                    newGameState.hasOwnProperty('status')) {
                    
                    console.log("Received GameStateUpdate object, converting to GameState structure");
                    
                    // If we already have a state, just update the relevant parts
                    if (latestGameState) {
                        const updatedState = { ...latestGameState };
                        updatedState.status = newGameState.status;
                        
                        // Update ball position if available
                        if (newGameState.ballPosition && updatedState.ball) {
                            updatedState.ball.position = newGameState.ballPosition;
                        }
                        
                        // Update game time if available
                        if (newGameState.gameTime) {
                            updatedState.gameTime = newGameState.gameTime;
                        }
                        
                        // Use the updated state for the rendering logic
                        newGameState = updatedState;
                    }
                    // If this is the first update, it won't have enough info - wait for full state
                    else {
                        console.log("Ignoring GameStateUpdate without existing state");
                        return;
                    }
                }
            }
        } catch (error) {
            console.error("Error processing game state:", error);
            return; // Skip processing this update if it caused an error
        }
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
    // --------------------------------------------------------

    // Store states for pass detection etc. (only if initialization succeeded or was already done)
    if (isInitialized) {
        // --- Handle Game Status Changes ---
        const prevStatus = latestGameState?.status;
        const newStatus = newGameState?.status;
        
        // Check for GoalScored status (4)
        if (newStatus === 4) { // GameStatus.GoalScored = 4
            console.log("GOAL SCORED! Ball at position:", newGameState.ball.position.x, newGameState.ball.position.y);
            // Keep track of goal celebration start time if this is the first GoalScored state
            if (!goalCelebrationStart) {
                goalCelebrationStart = performance.now();
                console.log("Goal celebration started");
            }
        }
        // When transitioning back from GoalScored to InProgress
        else if (prevStatus === 4 && newStatus === 1) { // GoalScored to InProgress 
            console.log("Goal celebration ended, game resuming at center");
            goalCelebrationStart = null; // Reset celebration timer
        }
        
        previousGameState = latestGameState; // Store the old state for comparison
        latestGameState = newGameState;

        if (!latestGameState) return; // Don't do anything if the new state is invalid

        // --- Pass Detection and Animation Trigger ---
        if (previousGameState && latestGameState && !isPassing) { // Only detect pass start if not already passing
            const prevPossessionId = previousGameState.ballPossession;
            const currentPossessionId = latestGameState.ballPossession;
            const prevBallPos = previousGameState.ball?.position;
            const currentBallPos = latestGameState.ball?.position;

            const passer = findPossessingPlayer(previousGameState); // Player who had the ball last state
            const receiver = findPossessingPlayer(latestGameState); // Player who has the ball now (if any)

            // Scenario 1: Player had ball, now nobody does (pass initiated, ball in transit)
            if (passer && (currentPossessionId === null || currentPossessionId === "") && currentBallPos && prevBallPos) {
                const passerX = passer.position.x;
                const passerY = passer.position.y;

                // Use the new ball position as the target if it's different from passer's pos
                 // Check if ball actually moved significantly from the passer
                 const dx = currentBallPos.x - passerX;
                 const dy = currentBallPos.y - passerY;
                 const distSq = dx*dx + dy*dy; // Use squared distance for efficiency

                 // Heuristic threshold: 0.01 distance (relative to field size 1x1) squared
                 if (distSq > 0.0001) {
                    console.log(`Pass detected from ${passer.playerId} towards ${currentBallPos.x.toFixed(2)}, ${currentBallPos.y.toFixed(2)}`);
                    isPassing = true;
                    passData = {
                        startX: passerX,
                        startY: passerY,
                        endX: currentBallPos.x, // Target the ball's new reported position
                        endY: currentBallPos.y,
                        startTime: performance.now(),
                        duration: 400 // Pass duration in milliseconds (adjust as needed)
                    };
                 }
            }
            // Scenario 2: Direct pass (Player A had ball, now Player B has it)
            else if (passer && receiver && currentPossessionId !== prevPossessionId) {
                 console.log(`Direct pass detected from ${passer.playerId} to ${receiver.playerId}`);
                 isPassing = true;
                 passData = {
                     startX: passer.position.x,
                     startY: passer.position.y,
                     endX: receiver.position.x, // Target receiver's current position
                     endY: receiver.position.y,
                     startTime: performance.now(),
                     duration: 400 // Pass duration in milliseconds
                 };
            }
            // Scenario 3: Pass completion (Ball was loose/passing, now player has it)
            // No animation needed here, just let the next state render normally.
            // The isPassing flag will be reset by the animation loop itself when time expires.
        }

        // Render immediately on first update AFTER initialization
        // The animation loop takes over afterwards.
        // This avoids a flicker if the first render happens much later.
        if (!previousGameState && latestGameState) {
            renderGameField(canvas, latestGameState, null);
        }

    } else {
         // If not initialized yet, just store the latest state so it's available when init runs
         // This prevents losing the very first state if init fails temporarily
         latestGameState = newGameState;
         console.log("Stored initial game state while waiting for initialization.");
    }

    // Removed initial render call from here if !previousGameState
    // it's now handled after successful initialization.
}

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