using SharpCoder;

namespace SharpCoder.Cli.Tests;

public class SessionFileWriterTests
{
    [Fact]
    public async Task SaveAtomicAsync_OverwritesExistingSessionWithValidJson()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cli-session-{Guid.NewGuid():N}.json");
        var tempLeftovers = Array.Empty<string>();
        try
        {
            // A pre-existing file containing a complete session: an interrupted save must not
            // leave a truncated/unloadable file behind.
            var first = AgentSession.Create("first-session");
            await first.SaveAsync(path, TestContext.Current.CancellationToken);

            var second = AgentSession.Create("second-session");
            second.MessageHistory.Add(new Microsoft.Extensions.AI.ChatMessage(
                Microsoft.Extensions.AI.ChatRole.User, "hello"));

            await SessionFileWriter.SaveAtomicAsync(second, path, TestContext.Current.CancellationToken);

            var loaded = await AgentSession.LoadAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal("second-session", loaded.SessionId);
            Assert.Single(loaded.MessageHistory);
            Assert.Equal("hello", loaded.MessageHistory[0].Text);

            // The temp file must have been consumed by the atomic move.
            tempLeftovers = Directory.GetFiles(Path.GetDirectoryName(path)!,
                Path.GetFileName(path) + ".*.tmp");
            Assert.Empty(tempLeftovers);
        }
        finally
        {
            foreach (var file in tempLeftovers) File.Delete(file);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveAtomicAsync_CreatesMissingParentDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cli-session-dir-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "nested", "session.json");
        try
        {
            var session = AgentSession.Create("nested-session");
            await SessionFileWriter.SaveAtomicAsync(session, path, TestContext.Current.CancellationToken);

            Assert.True(File.Exists(path));
            var loaded = await AgentSession.LoadAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal("nested-session", loaded.SessionId);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
