using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using SharpCoder;

Console.WriteLine("Welcome to SharpCoder CLI Agent (Ollama Cloud)");

// Create a logger
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});
var logger = loggerFactory.CreateLogger<CodingAgent>();

// Check for Ollama Cloud API Key
var apiKey = Environment.GetEnvironmentVariable("OLLAMA_API_KEY");
if (string.IsNullOrEmpty(apiKey))
{
    Console.WriteLine("Warning: OLLAMA_API_KEY environment variable is not set. Proceeding anyway, but requests may fail.");
    apiKey = "dummy_key"; // Provide dummy key to avoid null ref exception in header value
}

// Create custom HttpClient for Ollama Cloud
var httpClient = new HttpClient { BaseAddress = new Uri("https://ollama.com") };
httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

// Instantiate Ollama Chat Client pointed to Cloud (using the constructor with Uri, modelId, HttpClient)
IChatClient chatClient = new OllamaChatClient(new Uri("https://ollama.com"), "gpt-oss:120b", httpClient);

// Instantiate the agent
var agent = new CodingAgent(chatClient, new AgentOptions 
{
    WorkDirectory = System.IO.Directory.GetCurrentDirectory(),
    EnableBash = true,       
    EnableFileOps = true,    
    MaxSteps = 10,
    Logger = logger
});

// Run it!
Console.WriteLine("\nExecuting Task: Add a new file called 'hello.txt' that says 'Hello from SharpCoder'");
var result = await agent.ExecuteAsync("Add a new file called 'hello.txt' that says 'Hello from SharpCoder'");

Console.WriteLine($"\nAgent finished! Status: {result.Status}");
Console.WriteLine($"Message: {result.Message}");
