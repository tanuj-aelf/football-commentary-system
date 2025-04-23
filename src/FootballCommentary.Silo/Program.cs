using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;
using OrleansDashboard;
using DotNetEnv;
using FootballCommentary.GAgents;
using System.Net;
using System.Reflection;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using FootballCommentary.Silo.Hubs;

namespace FootballCommentary.Silo
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            // Load environment variables from .env file
            Env.Load();
            
            // Configure Serilog
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("Orleans", LogEventLevel.Warning)
                .MinimumLevel.Override("Orleans.Runtime", LogEventLevel.Warning)
                .MinimumLevel.Override("FootballCommentary", LogEventLevel.Debug)
                .Enrich.FromLogContext()
                .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
            
            try
            {
                Log.Information("Starting Football Commentary System Silo");
                
                // Create and configure the host
                var host = Host.CreateDefaultBuilder(args)
                    .ConfigureAppConfiguration((hostContext, config) =>
                    {
                        config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                        config.AddJsonFile($"appsettings.{hostContext.HostingEnvironment.EnvironmentName}.json", optional: true, reloadOnChange: true);
                        config.AddEnvironmentVariables();
                        config.AddCommandLine(args);
                    })
                    .UseSerilog()
                    .UseOrleans(siloBuilder =>
                    {
                        siloBuilder.UseLocalhostClustering()
                            .Configure<EndpointOptions>(options =>
                            {
                                options.AdvertisedIPAddress = IPAddress.Loopback;
                                options.SiloPort = 11111;
                                options.GatewayPort = 30000;
                            })
                            .AddMemoryGrainStorage("Default")
                            .AddMemoryGrainStorage("PubSubStore")
                            .AddMemoryStreams("GameEvents")
                            .AddMemoryStreams("Commentary")
                            .ConfigureLogging(logging => 
                            {
                                logging.AddConsole();
                                logging.SetMinimumLevel(LogLevel.Information);
                                logging.AddFilter("FootballCommentary", LogLevel.Debug);
                            });
                    })
                    .ConfigureServices((hostContext, services) =>
                    {
                        // Add GAgents services
                        services.AddFootballCommentaryGAgents(hostContext.Configuration);
                        
                        // Add controllers for API
                        services.AddControllers();
                        
                        // Add SignalR
                        services.AddSignalR(options =>
                        {
                            // Set bigger message size for game state objects
                            options.MaximumReceiveMessageSize = 102400; // 100 KB
                        }).AddHubOptions<GameHub>(options =>
                        {
                            // Disable antiforgery token validation for SignalR hub
                            options.EnableDetailedErrors = true;
                        });
                        
                        // Add CORS
                        services.AddCors(options =>
                        {
                            options.AddDefaultPolicy(
                                policy =>
                                {
                                    policy.WithOrigins("http://localhost:7002", "http://localhost:5000", "https://localhost:5001")
                                        .AllowAnyHeader()
                                        .AllowAnyMethod()
                                        .AllowCredentials()
                                        .SetIsOriginAllowed(_ => true); // Allow all origins (for development only)
                                });
                        });
                    })
                    .ConfigureWebHostDefaults(webBuilder =>
                    {
                        webBuilder.UseStartup<Startup>();
                        webBuilder.UseUrls("http://localhost:7002");
                    })
                    .Build();
                
                await host.RunAsync();
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Host terminated unexpectedly");
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }
    }
} 