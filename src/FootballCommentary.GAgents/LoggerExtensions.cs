using Microsoft.Extensions.Logging;
using System;

namespace FootballCommentary.GAgents
{
    public static class LoggerExtensions
    {
        public static ILogger<T> CreateLoggerForCategory<T>(this ILogger logger)
        {
            var loggerFactory = new LoggerFactory();
            return loggerFactory.CreateLogger<T>();
        }
    }
} 