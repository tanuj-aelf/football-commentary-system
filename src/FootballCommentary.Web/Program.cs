using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Http;
using DotNetEnv;
using FootballCommentary.Web.Configuration;
using System;

public class Program
{
    public static void Main(string[] args)
    {
        // Load .env file at the very beginning
        LoadEnvFile();
        
        var builder = WebApplication.CreateBuilder(args);

        // Add services to the container
        builder.Services.AddRazorPages();
        builder.Services.AddServerSideBlazor();
        builder.Services.AddControllers();

        // Load and register LlmConfiguration
        var llmConfig = new LlmConfiguration
        {
            SelectedModel = Environment.GetEnvironmentVariable("MODEL"),
            GoogleApiKey = Environment.GetEnvironmentVariable("GOOGLE_GEMINI_API_KEY"),
            GoogleModel = Environment.GetEnvironmentVariable("GOOGLE_GEMINI_MODEL"),
            AzureApiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY"),
            AzureEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT"),
            AzureDeploymentName = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME"),
            AzureModelName = Environment.GetEnvironmentVariable("AZURE_OPENAI_MODEL_NAME")
        };

        // Set Active configuration based on SelectedModel
        if (llmConfig.IsGoogleSelected)
        {
            llmConfig.ActiveApiKey = llmConfig.GoogleApiKey;
            llmConfig.ActiveModelName = llmConfig.GoogleModel;
            llmConfig.ActiveEndpoint = null; 
            llmConfig.ActiveDeploymentName = null;
            Console.WriteLine($"LLM Configuration: Google Gemini selected. Model: {llmConfig.ActiveModelName}");
        }
        else if (llmConfig.IsAzureSelected)
        {
            llmConfig.ActiveApiKey = llmConfig.AzureApiKey;
            llmConfig.ActiveModelName = llmConfig.AzureModelName;
            llmConfig.ActiveEndpoint = llmConfig.AzureEndpoint;
            llmConfig.ActiveDeploymentName = llmConfig.AzureDeploymentName;
            Console.WriteLine($"LLM Configuration: Azure OpenAI selected. Deployment: {llmConfig.ActiveDeploymentName}, Model: {llmConfig.ActiveModelName}");
        }
        else
        {
            Console.WriteLine($"Warning: MODEL environment variable ('{llmConfig.SelectedModel}') is not set correctly to 'google' or 'openai'. LLM features may not work.");
        }

        builder.Services.AddSingleton(llmConfig);

        // Configure antiforgery
        builder.Services.AddAntiforgery(options => 
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = ".AspNetCore.Antiforgery";
            options.Cookie.SameSite = SameSiteMode.None;
            options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() 
                ? CookieSecurePolicy.None 
                : CookieSecurePolicy.Always;
        });

        // Add CORS
        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(
                policy =>
                {
                    policy.WithOrigins("http://localhost:7002")
                          .AllowAnyHeader()
                          .AllowAnyMethod()
                          .AllowCredentials();
                });
        });

        var app = builder.Build();

        // Configure the HTTP request pipeline
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error");
            app.UseHsts();
        }

        app.UseHttpsRedirection();
        app.UseStaticFiles();
        app.UseRouting();
        app.UseCors();
        app.UseAuthorization();

        app.MapControllers();
        app.MapRazorPages();
        app.MapBlazorHub();
        app.MapFallbackToPage("/_Host");

        app.Run();
    }

    private static void LoadEnvFile()
    {
        // Determine the path to the .env file relative to the project directory
        // Assumes .env file is in the workspace root, two levels up from src/FootballCommentary.Web
        var projectDir = Directory.GetCurrentDirectory(); // Should be src/FootballCommentary.Web
        var solutionDir = Directory.GetParent(projectDir)?.Parent?.FullName;
        if (solutionDir != null)
        {
            var envPath = Path.Combine(solutionDir, ".env");
            if (File.Exists(envPath))
            {
                Env.Load(envPath);
                Console.WriteLine($"Loaded .env file from: {envPath}");
            }
            else
            {
                Console.WriteLine($"Warning: .env file not found at: {envPath}");
            }
        }
        else
        {
            Console.WriteLine("Warning: Could not determine solution directory to find .env file.");
        }
    }
} 