/**
 * Specialized rendering for the Football Commentary System
 * This script handles the rendering of the game state on the canvas
 */

// Add a function to force a browser repaint
function forceRepaint() {
    // Force a browser repaint by temporarily modifying a watched DOM property
    // This trick forces the browser to update the visual display
    requestAnimationFrame(() => {
        document.body.style.opacity = 0.999;
        setTimeout(() => {
            document.body.style.opacity = 1;
        }, 0);
    });
}

// Rename the function to avoid conflicts with gameConnection.js
function renderGameFieldFromRenderGameFieldJs(canvas, gameState) {
    if (!canvas || !gameState) {
        console.log("Can't render: missing canvas or game state");
        return;
    }
    
    console.log("Rendering game field with state:", gameState);
    
    const ctx = canvas.getContext('2d');
    const width = canvas.width;
    const height = canvas.height;
    
    // Clear canvas and draw field
    drawBackground(ctx, width, height);
    
    // Handle property case differences between C# and JavaScript
    // C# uses PascalCase (HomeTeam) while some JavaScript code might expect camelCase (homeTeam)
    const homeTeam = gameState.HomeTeam || gameState.homeTeam;
    const awayTeam = gameState.AwayTeam || gameState.awayTeam;
    
    // Draw home team (red)
    if (homeTeam && Array.isArray(homeTeam.Players || homeTeam.players)) {
        const players = homeTeam.Players || homeTeam.players;
        console.log(`Drawing ${players.length} home players`);
        
        players.forEach((player, index) => {
            if (player) {
                // Handle Position vs position property naming
                const position = player.Position || player.position;
                
                if (position) {
                    // Handle X/Y vs x/y property naming  
                    const x = (position.X !== undefined ? position.X : position.x) * width;
                    const y = (position.Y !== undefined ? position.Y : position.y) * height;
                    
                    // Draw player
                    drawPlayer(ctx, x, y, 'red', index + 1);
                    
                    // Handle BallPossession vs ballPossession
                    const ballPossession = gameState.BallPossession || gameState.ballPossession;
                    const playerId = player.PlayerId || player.playerId;
                    
                    // Highlight player with ball
                    if (ballPossession === playerId) {
                        drawBallPossession(ctx, x, y);
                    }
                }
            }
        });
    } else {
        console.log("No home team players to draw");
    }
    
    // Draw away team (blue)
    if (awayTeam && Array.isArray(awayTeam.Players || awayTeam.players)) {
        const players = awayTeam.Players || awayTeam.players;
        console.log(`Drawing ${players.length} away players`);
        
        players.forEach((player, index) => {
            if (player) {
                // Handle Position vs position property naming
                const position = player.Position || player.position;
                
                if (position) {
                    // Handle X/Y vs x/y property naming
                    const x = (position.X !== undefined ? position.X : position.x) * width;
                    const y = (position.Y !== undefined ? position.Y : position.y) * height;
                    
                    // Draw player
                    drawPlayer(ctx, x, y, 'blue', index + 1);
                    
                    // Handle BallPossession vs ballPossession
                    const ballPossession = gameState.BallPossession || gameState.ballPossession;
                    const playerId = player.PlayerId || player.playerId;
                    
                    // Highlight player with ball
                    if (ballPossession === playerId) {
                        drawBallPossession(ctx, x, y);
                    }
                }
            }
        });
    } else {
        console.log("No away team players to draw");
    }
    
    // Draw the ball
    const ball = gameState.Ball || gameState.ball;
    if (ball) {
        const ballPosition = ball.Position || ball.position;
        
        if (ballPosition) {
            const x = (ballPosition.X !== undefined ? ballPosition.X : ballPosition.x);
            const y = (ballPosition.Y !== undefined ? ballPosition.Y : ballPosition.y);
            
            console.log(`Drawing ball at (${x}, ${y})`);
            const ballX = x * width;
            const ballY = y * height;
            drawBall(ctx, ballX, ballY);
        }
    }
    
    // Draw scores
    const homeScore = homeTeam ? (homeTeam.Score || homeTeam.score || 0) : 0;
    const awayScore = awayTeam ? (awayTeam.Score || awayTeam.score || 0) : 0;
    drawScores(ctx, width, homeScore, awayScore);
    
    // Draw game time
    const gameTime = gameState.GameTime || gameState.gameTime;
    if (gameTime) {
        drawGameTime(ctx, width, gameTime);
    }
    
    // Force a browser repaint to ensure the canvas updates are immediately visible
    forceRepaint();
}

// Also expose the original name for backward compatibility
function renderGameField(canvas, gameState) {
    // Call our renamed function
    renderGameFieldFromRenderGameFieldJs(canvas, gameState);
}

// Draw the background field
function drawBackground(ctx, width, height) {
    // Clear canvas
    ctx.clearRect(0, 0, width, height);
    
    // Draw grass background
    ctx.fillStyle = "#4CAF50";
    ctx.fillRect(0, 0, width, height);
    
    // Draw boundary line
    ctx.strokeStyle = "white";
    ctx.lineWidth = 2;
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
    // Left goal
    ctx.fillStyle = "#cccccc";
    ctx.fillRect(0, height / 2 - 40, 10, 80);
    
    // Right goal
    ctx.fillRect(width - 10, height / 2 - 40, 10, 80);
}

// Draw a player
function drawPlayer(ctx, x, y, color, number) {
    // Player circle
    ctx.fillStyle = color === 'red' ? '#FF0000' : '#0000FF';
    ctx.beginPath();
    ctx.arc(x, y, 10, 0, Math.PI * 2);
    ctx.fill();
    
    // Player number
    ctx.fillStyle = 'white';
    ctx.font = '10px Arial';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    ctx.fillText(number, x, y);
}

// Draw ball possession indicator
function drawBallPossession(ctx, x, y) {
    ctx.beginPath();
    ctx.arc(x, y, 14, 0, Math.PI * 2);
    ctx.strokeStyle = 'yellow';
    ctx.lineWidth = 2;
    ctx.stroke();
}

// Draw the ball
function drawBall(ctx, x, y) {
    // Ball
    ctx.fillStyle = 'white';
    ctx.beginPath();
    ctx.arc(x, y, 7, 0, Math.PI * 2);
    ctx.fill();
    
    // Ball outline
    ctx.strokeStyle = 'black';
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.arc(x, y, 7, 0, Math.PI * 2);
    ctx.stroke();
    
    // Ball pattern
    ctx.beginPath();
    ctx.moveTo(x - 3, y);
    ctx.lineTo(x + 3, y);
    ctx.stroke();
    
    ctx.beginPath();
    ctx.moveTo(x, y - 3);
    ctx.lineTo(x, y + 3);
    ctx.stroke();
}

// Draw scores
function drawScores(ctx, width, homeScore, awayScore) {
    ctx.fillStyle = 'white';
    ctx.font = 'bold 16px Arial';
    ctx.textAlign = 'center';
    
    // Draw home team score on left side
    ctx.fillText(`${homeScore}`, width * 0.25, 20);
    
    // Draw away team score on right side
    ctx.fillText(`${awayScore}`, width * 0.75, 20);
}

// Draw game time
function drawGameTime(ctx, width, gameTime) {
    const minutes = Math.floor(gameTime.TotalMinutes || gameTime.totalMinutes || 0);
    const seconds = Math.floor((gameTime.TotalSeconds || gameTime.totalSeconds || 0) % 60);
    
    ctx.fillStyle = 'white';
    ctx.font = 'bold 20px Arial';
    ctx.textAlign = 'center';
    ctx.fillText(`${minutes}:${seconds.toString().padStart(2, '0')}`, width / 2, 20);
} 