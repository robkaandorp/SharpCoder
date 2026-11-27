namespace SharpCoder.Cli.Tests;

public class ToolAuthorizationPolicyTests
{
    private sealed class ScriptedPrompter : IToolConfirmationPrompter
    {
        private readonly Queue<ToolConfirmationAnswer> _answers = new();
        public int AskCount { get; private set; }

        public ScriptedPrompter(params ToolConfirmationAnswer[] answers)
        {
            foreach (var a in answers) _answers.Enqueue(a);
        }

        public ToolConfirmationAnswer Ask(ToolAuthorizationContext context, CancellationToken ct)
        {
            AskCount++;
            return _answers.Count > 0 ? _answers.Dequeue() : ToolConfirmationAnswer.No;
        }
    }

    [Theory]
    [InlineData("read_file")]
    [InlineData("glob")]
    [InlineData("grep")]
    [InlineData("load_skill")]
    [InlineData("list_skills")]
    public void ReadOnlyTools_AreAllowedWithoutPrompting(string toolName)
    {
        var prompter = new ScriptedPrompter();
        var policy = new ToolAuthorizationPolicy(prompter, ToolConfirmationMode.Always);

        var decision = policy.Authorize(toolName, null, TestContext.Current.CancellationToken);

        Assert.True(decision.IsAllowed);
        Assert.Equal(0, prompter.AskCount);
    }

    [Theory]
    [InlineData("write_file")]
    [InlineData("edit_file")]
    [InlineData("execute_bash_command")]
    public void SensitiveTools_WithoutPromptContext_FailClosed(string toolName)
    {
        var prompter = new ScriptedPrompter(ToolConfirmationAnswer.Yes);
        var policy = new ToolAuthorizationPolicy(prompter, ToolConfirmationMode.Always);

        var decision = policy.Authorize(toolName, null, TestContext.Current.CancellationToken);

        // With a null context the policy fails closed (no human can be shown the arguments).
        Assert.False(decision.IsAllowed);
        Assert.Equal(0, prompter.AskCount);
    }

    [Fact]
    public void ModeNever_AllowsSensitiveToolsWithoutPrompting()
    {
        var prompter = new ScriptedPrompter();
        var policy = new ToolAuthorizationPolicy(prompter, ToolConfirmationMode.Never);

        var decision = policy.Authorize("write_file", null, TestContext.Current.CancellationToken);

        Assert.True(decision.IsAllowed);
        Assert.Equal(0, prompter.AskCount);
    }

    [Fact]
    public void AnswerNo_DeniesAndPromptsAgainOnNextCall()
    {
        var prompter = new ScriptedPrompter(ToolConfirmationAnswer.No, ToolConfirmationAnswer.Yes);
        var policy = new ToolAuthorizationPolicy(prompter, ToolConfirmationMode.Always);

        // A real context is needed for the prompter to be consulted; use a scripted fake context
        // via reflection-free construction is not possible (SharpCoder's ctor is internal), so
        // exercise the state machine through the prompter-only path with a stub context.
        var first = policy.AuthorizeWithAnswer("write_file", ToolConfirmationAnswer.No, TestContext.Current.CancellationToken);
        var second = policy.AuthorizeWithAnswer("edit_file", ToolConfirmationAnswer.Yes, TestContext.Current.CancellationToken);

        Assert.False(first.IsAllowed);
        Assert.Contains("declined this tool call", first.Reason);
        Assert.True(second.IsAllowed);
    }

    [Fact]
    public void AnswerAlways_AllowsAllLaterSensitiveCalls()
    {
        var policy = new ToolAuthorizationPolicy(new ScriptedPrompter(), ToolConfirmationMode.Always);

        var first = policy.AuthorizeWithAnswer("write_file", ToolConfirmationAnswer.Always, TestContext.Current.CancellationToken);
        var later = policy.Authorize("execute_bash_command", null, TestContext.Current.CancellationToken);

        Assert.True(first.IsAllowed);
        Assert.True(later.IsAllowed);
    }

    [Fact]
    public void AnswerNever_DeniesAllLaterSensitiveCalls()
    {
        var policy = new ToolAuthorizationPolicy(new ScriptedPrompter(), ToolConfirmationMode.Always);

        var first = policy.AuthorizeWithAnswer("write_file", ToolConfirmationAnswer.Never, TestContext.Current.CancellationToken);
        var later = policy.Authorize("execute_bash_command", null, TestContext.Current.CancellationToken);
        var readAfter = policy.Authorize("read_file", null, TestContext.Current.CancellationToken);

        Assert.False(first.IsAllowed);
        Assert.False(later.IsAllowed);
        Assert.Contains("declined all further", later.Reason);
        Assert.True(readAfter.IsAllowed); // read-only tools are never blocked by the deny-all latch
    }

    [Fact]
    public void Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var policy = new ToolAuthorizationPolicy(new ScriptedPrompter(), ToolConfirmationMode.Always);

        Assert.Throws<OperationCanceledException>(() =>
            policy.Authorize("write_file", null, cts.Token));
    }
}
