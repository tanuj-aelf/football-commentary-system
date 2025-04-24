# Football Commentary System

Real-time AI Football Game Master and Commentary System built with Orleans, SignalR, and the Aevatar Framework.

## Screenshots
![Uploading Screenshot 2025-04-24 at 8.49.24 AM.png…]()

![Uploading Screenshot 2025-04-24 at 8.49.39 AM.png…]()

![Uploading Screenshot 2025-04-24 at 8.49.52 AM.png…]()

## Overview

This application demonstrates the use of AI agents with the Aevatar framework to create a live sports commentary system. The system includes:

1. A simulated football (soccer) game with AI-controlled players
2. A real-time commentary system that generates natural language commentary on game events using Google Gemini AI
3. SignalR integration for pushing updates to connected clients
4. A web-based visualization of the game

## Architecture

The application is built with the following components:

- **FootballCommentary.Core**: Core domain models and abstractions
- **FootballCommentary.GAgents**: AI agent implementations using the Aevatar framework
- **FootballCommentary.Silo**: Backend host application with Orleans silo and SignalR hub
- **FootballCommentary.Web**: Frontend web application

## Key AI Agents

### GameStateGAgent
- Manages the game state, player positions, and ball physics
- Detects and publishes game events (goals, passes, etc.)
- Provides a streaming API for real-time updates

### CommentaryGAgent
- Subscribes to game events and generates natural language commentary
- Uses Google Gemini API to create varied and engaging commentary
- Publishes commentary messages via SignalR to connected clients

## Getting Started

### Prerequisites

- .NET 9.0 SDK or later
- Web browser

### Running the Application

1. Clone the repository

2. Build the solution
```bash
cd football-commentary-system
dotnet build
```

3. Run the Silo application (Orleans silo and SignalR hub)
```bash
cd src/FootballCommentary.Silo
dotnet run
```

4. Run the Web application in a separate terminal
```bash
cd src/FootballCommentary.Web
dotnet run
```

5. Open your browser and navigate to http://localhost:5000

## Using the Application

1. When the web application loads, enter team names and click "Create Game"
2. Click "Start Game" to begin the simulation
3. The red team represents Team A and the blue team represents Team B
4. Click "Kick Ball" to apply random velocity to the ball
5. Use the "Goal Team A" and "Goal Team B" buttons to simulate goals
6. Watch as the AI commentary system provides real-time commentary on the game events
7. Click "End Game" to finish the current game

## Technologies Used

- **Orleans**: For distributed actor model and grain-based architecture
- **SignalR**: For real-time web communication
- **Aevatar Framework**: For AI agent implementation
- **Google Gemini API**: For generating natural language commentary
- **Blazor**: For the web-based frontend

## Configuration

The application uses the settings in the `.env` file to configure the LLM (Google Gemini) API. By default, it will use the Gemini API with the provided API key.

## License

See the [LICENSE](LICENSE) file for details. 
