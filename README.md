# AI Football Commentary System

Real-time AI Football Game Master and Commentary System built with Orleans, SignalR, and the Aevatar Framework.

## Demo
https://github.com/user-attachments/assets/0c1dbcc7-22cd-4ef3-94b0-6608cb0f5b95

## Overview

This application demonstrates the use of AI agents with the Aevatar framework to create a live sports commentary system. The system includes:

1. A football (soccer) simulation with realistic player movements and ball physics
2. Role-based AI player behavior with distinct movement patterns for goalkeepers, defenders, midfielders, and forwards
3. Dynamic team formations and tactical adaptations during gameplay
4. A real-time commentary system powered by Google Gemini AI that generates contextual, engaging commentary
5. SignalR integration for pushing live updates to connected clients
6. A responsive web-based visualization with real-time rendering of the match

## Architecture

The application is built with the following components:

- **FootballCommentary.Core**: Core domain models and abstractions
- **FootballCommentary.GAgents**: AI agent implementations using the Aevatar framework
- **FootballCommentary.Silo**: Backend host application with Orleans silo and SignalR hub
- **FootballCommentary.Web**: Frontend web application

## Key AI Agents

### GameStateGAgent
- Implements advanced simulation of game state, player movements, and ball physics
- Features realistic player behavior based on roles (goalkeeper, defender, midfielder, forward)
- Simulates natural player movements with momentum, team formation awareness, and tactical positioning
- Manages ball physics including velocity, collisions, and goal detection
- Detects and publishes various game events (goals, passes, shots, tackles, saves)
- Provides a streaming API for real-time updates with 30ms refresh cycles

### CommentaryGAgent
- Subscribes to game events and generates natural language commentary in real-time
- Uses Google Gemini API to create varied, contextual, and engaging commentary
- Supports different commentary types: play-by-play, background, summary, and match analysis
- Includes throttling logic to prevent commentary overflow during rapid game events
- Publishes commentary messages via SignalR to connected clients

## Game Simulation Features

The simulation includes several realistic gameplay elements:

- **Player Movement**: Position-aware movements with natural variations, momentum, and formation adherence
- **Ball Physics**: Realistic ball behavior with velocity, direction, and friction simulation
- **Role-Based Behavior**: Players act differently based on their position (e.g., goalkeepers stay near goal, forwards press higher)
- **Team Formations**: Support for different formations (4-4-2, 4-3-3, etc.) that influence player positioning
- **Game Events**: Simulation of passes, shots, tackles, saves, and goals with appropriate physics and animations
- **Time Scaling**: Compressed match time (90 minutes of game time in a few minutes of real time)
- **Score Tracking**: Automatic goal detection and score management

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
4. Watch as players move intelligently based on their positions and game situation
5. Click "Kick Ball" to apply random velocity to the ball
6. Observe the AI commentary system providing real-time commentary on the game events
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
