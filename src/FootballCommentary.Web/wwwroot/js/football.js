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

// Main function to render the game field based on game state
function renderGameField(canvas, gameState) {
    const ctx = canvas.getContext("2d");
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
            
            // Draw player
            drawCircle(ctx, x, y, radius, "red");
            
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
            
            // Draw player
            drawCircle(ctx, x, y, radius, "blue");
            
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
    
    // Draw the ball (white circle)
    if (gameState.ball && gameState.ball.position) {
        drawCircle(
            ctx, 
            gameState.ball.position.x * width, 
            gameState.ball.position.y * height, 
            8, 
            "white"
        );
    }
    
    // If no one has possession, make the ball more visible
    if (gameState.ballPossession === null || gameState.ballPossession === "") {
        drawCircle(
            ctx, 
            gameState.ball.position.x * width, 
            gameState.ball.position.y * height, 
            10, 
            "white"
        );
        
        // Add a black outline to the ball
        ctx.beginPath();
        ctx.arc(
            gameState.ball.position.x * width, 
            gameState.ball.position.y * height, 
            10, 
            0, 
            Math.PI * 2
        );
        ctx.strokeStyle = "black";
        ctx.lineWidth = 1;
        ctx.stroke();
    }
    
    // Draw game timer
    if (gameState.gameTime !== undefined) {
        const minutes = Math.floor(gameState.gameTime.totalMinutes) || 0;
        const seconds = Math.floor(gameState.gameTime.totalSeconds % 60) || 0;
        
        ctx.fillStyle = "white";
        ctx.font = "20px Arial";
        ctx.fillText(`${minutes}:${seconds.toString().padStart(2, '0')}`, width / 2 - 30, 20);
    }
} 