using System;
using System.Collections.Generic;
using System.ClientModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using SharpCoder;

Console.WriteLine("Welcome to SharpCoder CLI Agent");

// Create a logger
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});
var logger = loggerFactory.CreateLogger<CodingAgent>();

// A dummy client just so it compiles and runs without keys
IChatClient chatClient = new DummyChatClient();

// 2. Instantiate the agent
var agent = new CodingAgent(chatClient, new AgentOptions 
{
    WorkDirectory = Directory.GetCurrentDirectory(),
    EnableBash = true,       
    EnableFileOps = true,    
    MaxSteps = 10,
    Logger = logger
});

// 3. Run it!
Console.WriteLine("\nExecuting Task: Add a new file called 'hello.txt' that says 'Hello from SharpCoder'");
var result = await agent.ExecuteAsync("Add a new file called 'hello.txt' that says 'Hello from SharpCoder'");

Console.WriteLine($"\nAgent finished! Status: {result.Status}");
Console.WriteLine($"Message: {result.Message}");

// --- DUMMY CLIENT FOR TESTING ---
class DummyChatClient : IChatClient
{
    public ChatClientMetadata Metadata => new("DummyClient");

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "I am a dummy client. I did the work.")));
    }

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant };
    }
#pragma warning restore CS1998

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
