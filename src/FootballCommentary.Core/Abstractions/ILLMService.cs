using System.Threading.Tasks;

namespace FootballCommentary.Core.Abstractions
{
    public interface ILLMService
    {
        Task<string> GenerateCommentaryAsync(string prompt);
        
        Task<string> GenerateContentAsync(string systemPrompt, string userPrompt);
    }
} 