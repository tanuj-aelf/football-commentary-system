using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using FootballCommentary.Core.Abstractions;
using FootballCommentary.GAgents.Services;

namespace FootballCommentary.GAgents
{
    public static class FootballCommentaryGAgentsModule
    {
        public static IServiceCollection AddFootballCommentaryGAgents(this IServiceCollection services, IConfiguration configuration)
        {
            // Register HttpClientFactory
            services.AddHttpClient("GeminiApi", client =>
            {
                client.DefaultRequestHeaders.Add("Accept", "application/json");
            });
            
            // Register services
            services.AddSingleton<ILLMService, LLMService>();
            
            // The GAgents are automatically registered with Orleans 
            // through the grain interface discovery mechanism
            
            return services;
        }
    }
} 