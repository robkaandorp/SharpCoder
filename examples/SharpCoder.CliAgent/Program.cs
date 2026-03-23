using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using OllamaSharp;

using SharpCoder;

using System.Net.Http.Headers;

Console.WriteLine("Welcome to SharpCoder CLI Agent (Ollama Cloud)");

// Create a logger
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});
var logger = loggerFactory.CreateLogger<CodingAgent>();

// Ollama Cloud API requires an API key — see https://docs.ollama.com/cloud
var apiKey = Environment.GetEnvironmentVariable("OLLAMA_API_KEY");
if (string.IsNullOrEmpty(apiKey))
{
    Console.WriteLine("Error: OLLAMA_API_KEY environment variable is not set.");
    Console.WriteLine("Create an API key at https://ollama.com/settings/keys");
    return;
}

// Create HttpClient with Bearer auth for Ollama Cloud
var httpClient = new HttpClient { BaseAddress = new Uri("https://ollama.com") };
httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

var ollamaClient = new OllamaApiClient(httpClient);
ollamaClient.SelectedModel = "gpt-oss:120b";

IChatClient chatClient = ollamaClient;

// Instantiate the agent
var agent = new CodingAgent(chatClient, new AgentOptions
{
    WorkDirectory = Directory.GetCurrentDirectory(),
    EnableBash = true,
    EnableFileOps = true,
    MaxSteps = 10,
    Logger = logger
});

// Run it!
Console.WriteLine($"\nUsing model: gpt-oss:120b via Ollama Cloud");
Console.WriteLine("Executing Task: Add a new file called 'hello.txt' that says 'Hello from SharpCoder'");
var result = await agent.ExecuteAsync("Add a new file called 'hello.txt' that says 'Hello from SharpCoder'");

Console.WriteLine($"\nAgent finished! Status: {result.Status}");
Console.WriteLine($"Message: {result.Message}");
