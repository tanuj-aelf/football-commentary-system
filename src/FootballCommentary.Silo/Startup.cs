using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using FootballCommentary.Silo.Hubs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Connections;

namespace FootballCommentary.Silo
{
    public class Startup
    {
        private readonly IConfiguration _configuration;

        public Startup(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public void ConfigureServices(IServiceCollection services)
        {
            // Services are already registered in Program.cs

            // Add antiforgery filter for SignalR endpoints
            services.AddMvc(options =>
            {
                options.Filters.Add(new IgnoreAntiforgeryTokenAttribute());
            });
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseRouting();
            app.UseCors();
            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
                endpoints.MapHub<GameHub>("/gamehub", options =>
                {
                    options.Transports = HttpTransportType.WebSockets | 
                                        HttpTransportType.LongPolling | 
                                        HttpTransportType.ServerSentEvents;
                    options.ApplicationMaxBufferSize = 102400; // 100 KB
                    options.TransportMaxBufferSize = 102400; // 100 KB
                });
            });
        }
    }
} 