using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;
using OrleansDashboard;
using DotNetEnv;
using FootballCommentary.GAgents;
using FootballCommentary.Web.Configuration;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
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
            // Load .env right at the start and get the dictionary
            var envVars = LoadEnvFile();

            // Configure Serilog first
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
                
                var host = Host.CreateDefaultBuilder(args)
                    .ConfigureAppConfiguration((hostContext, config) =>
                    {
                        // Standard config sources first
                        config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                        config.AddJsonFile($"appsettings.{hostContext.HostingEnvironment.EnvironmentName}.json", optional: true, reloadOnChange: true);
                        config.AddEnvironmentVariables(); // Standard env vars 
                        config.AddCommandLine(args); // Command line args
                        
                        // Add .env variables LAST to give them highest precedence
                        if (envVars.Any())
                        {
                            var nonNullEnvVars = envVars
                                .Where(kvp => kvp.Value != null)
                                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!);
                                
                            if(nonNullEnvVars.Any())
                            {
                                config.AddInMemoryCollection(nonNullEnvVars);
                                Log.Information("Added .env variables to IConfiguration from dictionary (highest precedence).");
                            }
                        }
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
                        // Register LlmConfiguration using a factory that reads from IConfiguration
                        services.AddSingleton(sp => { 
                            Console.WriteLine("--- LlmConfiguration Factory START ---"); // Simple console log
                            var configuration = sp.GetRequiredService<IConfiguration>();
                            // var logger = sp.GetRequiredService<ILogger<Program>>(); // Reverted logger for simplicity here

                            var modelValueFromConfig = configuration["MODEL"];
                            // logger.LogInformation("Factory: MODEL read from IConfiguration: '{ModelValue}'", modelValueFromConfig);
                            Console.WriteLine($"--- Factory: MODEL read from IConfiguration: '{modelValueFromConfig}' ---"); // Simple console log

                            var llmConfig = new LlmConfiguration
                            {
                                SelectedModel = modelValueFromConfig,
                                GoogleApiKey = configuration["GOOGLE_GEMINI_API_KEY"],
                                GoogleModel = configuration["GOOGLE_GEMINI_MODEL"],
                                AzureApiKey = configuration["AZURE_OPENAI_API_KEY"],
                                AzureEndpoint = configuration["AZURE_OPENAI_ENDPOINT"],
                                AzureDeploymentName = configuration["AZURE_OPENAI_DEPLOYMENT_NAME"],
                                AzureModelName = configuration["AZURE_OPENAI_MODEL_NAME"]
                            };

                            // Logic to set Active... properties based on SelectedModel
                            if (llmConfig.IsGoogleSelected)
                            {
                                // ... set active Google ...
                                llmConfig.ActiveApiKey = llmConfig.GoogleApiKey;
                                llmConfig.ActiveModelName = llmConfig.GoogleModel;
                                llmConfig.ActiveEndpoint = null;
                                llmConfig.ActiveDeploymentName = null;
                                // logger.LogInformation("LLM Configuration (Silo - Via IConfiguration): Google Gemini selected. Model: {ModelName}", llmConfig.ActiveModelName);
                                Console.WriteLine($"--- LLM Config: Google Gemini selected. Model: {llmConfig.ActiveModelName} ---"); // Simple console log
                            }
                            else if (llmConfig.IsAzureSelected)
                            {
                                // ... set active Azure ...
                                llmConfig.ActiveApiKey = llmConfig.AzureApiKey;
                                llmConfig.ActiveModelName = llmConfig.AzureModelName;
                                llmConfig.ActiveEndpoint = llmConfig.AzureEndpoint;
                                llmConfig.ActiveDeploymentName = llmConfig.AzureDeploymentName;
                                // logger.LogInformation("LLM Configuration (Silo - Via IConfiguration): Azure OpenAI selected. Deployment: {Deployment}, Model: {ModelName}", llmConfig.ActiveDeploymentName, llmConfig.ActiveModelName);
                                Console.WriteLine($"--- LLM Config: Azure OpenAI selected. Deployment: {llmConfig.ActiveDeploymentName}, Model: {llmConfig.ActiveModelName} ---"); // Simple console log
                            }
                            else
                            {
                                // logger.LogWarning("Warning (Silo - Via IConfiguration): MODEL value ('{ModelValue}') from IConfiguration is not 'google' or 'openai'. LLM features may not work.", llmConfig.SelectedModel);
                                Console.WriteLine($"--- WARNING: LLM Config: MODEL value ('{llmConfig.SelectedModel}') is not 'google' or 'openai'. ---"); // Simple console log
                            }
                            Console.WriteLine("--- LlmConfiguration Factory END ---"); // Simple console log
                            return llmConfig;
                        });
                        
                        // Add GAgents services (should receive the LlmConfiguration registered above)
                        services.AddFootballCommentaryGAgents(hostContext.Configuration); // Pass original config if needed elsewhere in GAgents
                        
                        // Add controllers for API
                        services.AddControllers();
                        
                        // Add SignalR
                        services.AddSignalR(options =>
                        {
                            options.MaximumReceiveMessageSize = 102400; // 100 KB
                        }).AddHubOptions<GameHub>(options =>
                        {
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
                                        .SetIsOriginAllowed(_ => true);
                                });
                        });

                        // Log end of ConfigureServices
                        var tempLogger = services.BuildServiceProvider().GetService<ILogger<Program>>();
                        tempLogger?.LogInformation("--- ConfigureServices delegate completed. ---");
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

        // Modify LoadEnvFile to read directly into a Dictionary using Env.TraversePath().Load()
        private static Dictionary<string, string?> LoadEnvFile()
        {
             var configVars = new Dictionary<string, string?>();
             var neededKeys = new HashSet<string> { 
                "MODEL", "GOOGLE_GEMINI_API_KEY", "GOOGLE_GEMINI_MODEL",
                "AZURE_OPENAI_API_KEY", "AZURE_OPENAI_ENDPOINT", 
                "AZURE_OPENAI_DEPLOYMENT_NAME", "AZURE_OPENAI_MODEL_NAME" 
             };

            try
            {
                // Find and load .env file, returning key-value pairs
                var allEnvVarsEnum = Env.TraversePath().Load(); 
                // Convert the IEnumerable to a Dictionary
                var allEnvVarsDict = allEnvVarsEnum.ToDictionary(kv => kv.Key, kv => kv.Value);
                Console.WriteLine($"Read {allEnvVarsDict.Count} variables via Env.TraversePath().Load()");

                // Populate the dictionary with only the needed keys
                foreach (var key in neededKeys)
                {
                    // Use the dictionary now
                    if (allEnvVarsDict.TryGetValue(key, out var value))
                    {
                        configVars[key] = value;
                    }
                    else
                    {
                        configVars[key] = null; // Ensure key exists even if not in .env
                    }
                }
                Log.Information("Extracted required keys from .env traversal load.");
            }
            catch (Exception ex)
            {
                 // Corrected Log.Error format
                 Log.Error(ex, "Error loading/reading .env file via traversal: {Message}", ex.Message);
            }
            
            // Log if the critical MODEL key wasn't found/extracted
            if (!configVars.ContainsKey("MODEL") || string.IsNullOrEmpty(configVars["MODEL"])) 
            {
                Log.Warning("MODEL key was not found or is empty after reading .env file.");
            }
            
            return configVars;
        }
    }
} 