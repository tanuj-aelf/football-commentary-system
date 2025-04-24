// Global variable to store the current game state
// let currentGameState = null; // No longer needed here, managed in football.js

// Render the game field on the canvas
/* // REMOVED - Rendering is handled by football.js animation loop
function renderGameField(canvas, gameState) {
    if (!canvas || !gameState) return;
    
    currentGameState = gameState;
    const ctx = canvas.getContext('2d');
    const width = canvas.width;
    const height = canvas.height;
    
    // Clear the canvas
    ctx.clearRect(0, 0, width, height);
    
    // Draw the field
    ctx.fillStyle = '#4CAF50';
    ctx.fillRect(0, 0, width, height);
    
    // Draw the middle line
    ctx.strokeStyle = 'white';
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.moveTo(width / 2, 0);
    ctx.lineTo(width / 2, height);
    ctx.stroke();
    
    // Draw the middle circle
    ctx.beginPath();
    ctx.arc(width / 2, height / 2, 50, 0, 2 * Math.PI);
    ctx.stroke();
    
    // Draw the goals
    // Left goal
    ctx.fillStyle = '#cccccc';
    ctx.fillRect(0, height / 2 - 40, 10, 80);
    
    // Right goal
    ctx.fillRect(width - 10, height / 2 - 40, 10, 80);
    
    // Draw the ball
    if (gameState.ball && gameState.ball.position) {
        const ballX = gameState.ball.position.x * width;
        const ballY = gameState.ball.position.y * height;
        
        ctx.fillStyle = 'white';
        ctx.beginPath();
        ctx.arc(ballX, ballY, 7, 0, 2 * Math.PI);
        ctx.fill();
        
        ctx.strokeStyle = 'black';
        ctx.lineWidth = 1;
        ctx.beginPath();
        ctx.arc(ballX, ballY, 7, 0, 2 * Math.PI);
        ctx.stroke();
    }
    
    // Draw team A players (red)
    if (gameState.homeTeam && gameState.homeTeam.players) {
        gameState.homeTeam.players.forEach(player => {
            if (player.position) {
                const x = player.position.x * width;
                const y = player.position.y * height;
                
                ctx.fillStyle = '#FF0000';
                ctx.beginPath();
                ctx.arc(x, y, 10, 0, 2 * Math.PI);
                ctx.fill();
            }
        });
    }
    
    // Draw team B players (blue)
    if (gameState.awayTeam && gameState.awayTeam.players) {
        gameState.awayTeam.players.forEach(player => {
            if (player.position) {
                const x = player.position.x * width;
                const y = player.position.y * height;
                
                ctx.fillStyle = '#0000FF';
                ctx.beginPath();
                ctx.arc(x, y, 10, 0, 2 * Math.PI);
                ctx.fill();
            }
        });
    }
}
*/

// New function to update game canvas (called from Blazor)
/* // REMOVED - Rendering is handled by football.js animation loop
function updateGameCanvas(canvas, homePlayers, awayPlayers, ball) {
    if (!canvas) return;
    
    const ctx = canvas.getContext('2d');
    const width = canvas.width;
    const height = canvas.height;
    
    // Clear the canvas
    ctx.clearRect(0, 0, width, height);
    
    // Draw the field
    ctx.fillStyle = '#4CAF50';
    ctx.fillRect(0, 0, width, height);
    
    // Draw the middle line
    ctx.strokeStyle = 'white';
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.moveTo(width / 2, 0);
    ctx.lineTo(width / 2, height);
    ctx.stroke();
    
    // Draw the middle circle
    ctx.beginPath();
    ctx.arc(width / 2, height / 2, 50, 0, 2 * Math.PI);
    ctx.stroke();
    
    // Draw the goals
    // Left goal
    ctx.fillStyle = '#cccccc';
    ctx.fillRect(0, height / 2 - 40, 10, 80);
    
    // Right goal
    ctx.fillRect(width - 10, height / 2 - 40, 10, 80);
    
    // Draw home team players (red)
    if (homePlayers) {
        console.log('Home players:', homePlayers);
        for (let i = 0; i < homePlayers.length; i++) {
            const player = homePlayers[i];
            if (player && player.Position) {
                const x = player.Position.X * width;
                const y = player.Position.Y * height;
                
                ctx.fillStyle = '#FF0000';
                ctx.beginPath();
                ctx.arc(x, y, 10, 0, 2 * Math.PI);
                ctx.fill();
                
                // Add player number
                ctx.fillStyle = 'white';
                ctx.font = '10px Arial';
                ctx.textAlign = 'center';
                ctx.textBaseline = 'middle';
                ctx.fillText(i+1, x, y);
            }
        }
    }
    
    // Draw away team players (blue)
    if (awayPlayers) {
        console.log('Away players:', awayPlayers);
        for (let i = 0; i < awayPlayers.length; i++) {
            const player = awayPlayers[i];
            if (player && player.Position) {
                const x = player.Position.X * width;
                const y = player.Position.Y * height;
                
                ctx.fillStyle = '#0000FF';
                ctx.beginPath();
                ctx.arc(x, y, 10, 0, 2 * Math.PI);
                ctx.fill();
                
                // Add player number
                ctx.fillStyle = 'white';
                ctx.font = '10px Arial';
                ctx.textAlign = 'center';
                ctx.textBaseline = 'middle';
                ctx.fillText(i+1, x, y);
            }
        }
    }
    
    // Draw the ball
    if (ball && ball.Position) {
        const ballX = ball.Position.X * width;
        const ballY = ball.Position.Y * height;
        
        ctx.fillStyle = 'white';
        ctx.beginPath();
        ctx.arc(ballX, ballY, 7, 0, 2 * Math.PI);
        ctx.fill();
        
        ctx.strokeStyle = 'black';
        ctx.lineWidth = 1;
        ctx.beginPath();
        ctx.arc(ballX, ballY, 7, 0, 2 * Math.PI);
        ctx.stroke();
    }
    
    console.log("Canvas updated with new game state");
}
*/

// Draw the football field
function drawField(ctx, width, height) {
    // Draw the grass
    ctx.fillStyle = '#4CAF50';
    ctx.fillRect(0, 0, width, height);
    
    // Draw the middle line
    ctx.strokeStyle = 'white';
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.moveTo(width / 2, 0);
    ctx.lineTo(width / 2, height);
    ctx.stroke();
    
    // Draw the middle circle
    ctx.beginPath();
    ctx.arc(width / 2, height / 2, 50, 0, 2 * Math.PI);
    ctx.stroke();
    
    // Draw the goals
    // Left goal
    ctx.fillStyle = '#cccccc';
    ctx.fillRect(0, height / 2 - 40, 10, 80);
    
    // Right goal
    ctx.fillRect(width - 10, height / 2 - 40, 10, 80);
}

// Draw a player
function drawPlayer(ctx, player, color, width, height) {
    if (!player || !player.position) return;
    
    const x = player.position.x * width;
    const y = player.position.y * height;
    
    // Draw player circle
    ctx.fillStyle = color;
    ctx.beginPath();
    ctx.arc(x, y, 10, 0, 2 * Math.PI);
    ctx.fill();
    
    // Draw player number/identifier
    ctx.fillStyle = 'white';
    ctx.font = '10px Arial';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    ctx.fillText(player.playerId.split('_')[1], x, y);
}

// Draw the ball
function drawBall(ctx, position, width, height) {
    const x = position.x * width;
    const y = position.y * height;
    
    // Draw ball
    ctx.fillStyle = 'white';
    ctx.beginPath();
    ctx.arc(x, y, 7, 0, 2 * Math.PI);
    ctx.fill();
    
    // Draw ball pattern
    ctx.strokeStyle = 'black';
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.arc(x, y, 7, 0, 2 * Math.PI);
    ctx.stroke();
    
    ctx.beginPath();
    ctx.moveTo(x - 3, y);
    ctx.lineTo(x + 3, y);
    ctx.stroke();
    
    ctx.beginPath();
    ctx.moveTo(x, y - 3);
    ctx.lineTo(x, y + 3);
    ctx.stroke();
} 