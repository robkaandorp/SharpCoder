using System.Diagnostics;
using System.Globalization;

namespace SharpCoder.ProcessFixture;

/// <summary>
/// Test-only process fixture used by <c>SharpCoder.Tests</c> to build a real
/// root -&gt; child -&gt; grandchild process tree with recorded identities.
/// <para>
/// Modes:
/// <list type="bullet">
///   <item><c>root</c> — prints its identity, spawns the <c>child</c> (inheriting the redirected
///   stdout/stderr handles it was started with), publishes its readiness record and waits for the
///   whole tree to become ready.</item>
///   <item><c>child</c> — same, but spawns the <c>grandchild</c>.</item>
///   <item><c>grandchild</c> — leaf process; also used standalone as an unrelated sentinel.</item>
///   <item><c>readiness</c> — barrier helper: waits until the expected readiness records exist in
///   the rendezvous directory and prints them.</item>
/// </list>
/// </para>
/// <para>
/// Readiness is published as an atomically renamed record file per role in a rendezvous directory,
/// so waiters rendezvous on a real signal instead of a fixed sleep. Every mode enforces a maximum
/// lifetime purely as a safety net so a crashed test can never leak an immortal process.
/// </para>
/// </summary>
internal static class Program
{
    private const int DefaultLifetimeMs = 120_000;
    private const int DefaultBarrierTimeoutMs = 30_000;

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: SharpCoder.ProcessFixture <root|child|grandchild|readiness> [options]");
            return 2;
        }

        var mode = args[0].Trim().ToLowerInvariant();
        var options = ParseOptions(args);

        try
        {
            return mode switch
            {
                "root" => RunTreeNode(options, role: "root", nextRole: "child"),
                "child" => RunTreeNode(options, role: "child", nextRole: "grandchild"),
                "grandchild" => RunTreeNode(options, role: "grandchild", nextRole: null),
                "readiness" => RunReadinessBarrier(options),
                _ => Unknown(mode)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FIXTURE-ERROR mode={mode} {ex.GetType().Name}: {ex.Message}");
            return 3;
        }
    }

    private static int Unknown(string mode)
    {
        Console.Error.WriteLine($"FIXTURE-ERROR unknown mode '{mode}'");
        return 2;
    }

    // ------------------------------------------------------------------
    // Tree nodes
    // ------------------------------------------------------------------

    private static int RunTreeNode(Options options, string role, string? nextRole)
    {
        var identity = options.IdentityFor(role);
        var pid = Environment.ProcessId;
        var outputToken = OutputToken(identity);

        // Identity goes to BOTH streams so redirected stdout and stderr capture can be asserted.
        // out=<token> is derived from the identity and never appears on any command line, so tests
        // can prove that captured output was not copied somewhere it must not be.
        Console.Out.WriteLine($"FIXTURE-STDOUT role={role} id={identity} pid={pid} out={outputToken}");
        Console.Out.Flush();
        Console.Error.WriteLine($"FIXTURE-STDERR role={role} id={identity} pid={pid} out={outputToken}");
        Console.Error.Flush();

        Process? spawned = null;
        if (nextRole is not null && options.Depth(role) < options.MaxDepth)
        {
            // No redirection: the spawned process INHERITS this process' stdout/stderr handles.
            // That is exactly the "descendant keeps the inherited pipe open" vector under test.
            var startInfo = new ProcessStartInfo
            {
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true
            };

            var (fileName, prefixArguments) = SelfLaunchCommand();
            startInfo.FileName = fileName;
            foreach (var argument in prefixArguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            foreach (var argument in options.ArgumentsFor(nextRole))
            {
                startInfo.ArgumentList.Add(argument);
            }

            spawned = Process.Start(startInfo);
        }

        // Publish readiness only after the spawn attempt, so a visible record means "this node did
        // its part". The rendezvous file is written to a temp name and atomically renamed.
        PublishReadiness(options.RendezvousDirectory, role, identity, pid, spawned?.Id);

        // Barrier: wait for every expected role to publish readiness (no fixed sleeps).
        var ready = WaitForRoles(options.RendezvousDirectory, options.ExpectedRoles, options.BarrierTimeoutMs);
        if (!ready)
        {
            Console.Error.WriteLine($"FIXTURE-ERROR role={role} readiness barrier timed out after {options.BarrierTimeoutMs}ms");
            return 4;
        }

        if (options.ExitAfterReady.Contains(role))
        {
            // Root-exited vector: leave the tree running while this node exits cleanly.
            Console.Out.WriteLine($"FIXTURE-EXITING role={role} id={identity} pid={pid}");
            Console.Out.Flush();
            return 0;
        }

        // Workload placeholder. This wait is the process' lifetime, never a readiness proof; the
        // bound exists purely so a crashed test cannot leak an immortal process.
        Thread.Sleep(options.LifetimeMs);
        return 0;
    }

    /// <summary>
    /// Derives the output-only marker printed by a node. It is a deterministic transform of the
    /// identity and is never passed on a command line, so a test can assert that captured
    /// stdout/stderr content did not leak into logs or exception payloads.
    /// </summary>
    private static string OutputToken(string identity) => "OUT-" + identity;

    /// <summary>
    /// Resolves how to relaunch this same fixture. When the process was started through the
    /// <c>dotnet</c> muxer (<c>dotnet fixture.dll ...</c>) the muxer is reused, so the descendant
    /// does not depend on an apphost finding a runtime. Otherwise the apphost path is reused.
    /// </summary>
    private static (string FileName, IReadOnlyList<string> PrefixArguments) SelfLaunchCommand()
    {
        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Environment.ProcessPath is unavailable.");

        var assemblyPath = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        var executableName = Path.GetFileNameWithoutExtension(executablePath);

        if (!string.IsNullOrEmpty(assemblyPath) &&
            string.Equals(executableName, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return (executablePath, new[] { assemblyPath! });
        }

        return (executablePath, Array.Empty<string>());
    }

    // ------------------------------------------------------------------
    // Readiness barrier
    // ------------------------------------------------------------------

    private static int RunReadinessBarrier(Options options)
    {
        var ready = WaitForRoles(options.RendezvousDirectory, options.ExpectedRoles, options.BarrierTimeoutMs);
        foreach (var role in options.ExpectedRoles)
        {
            var record = TryReadRecord(options.RendezvousDirectory, role);
            Console.Out.WriteLine(record is null
                ? $"FIXTURE-READY role={role} MISSING"
                : $"FIXTURE-READY {record}");
        }

        Console.Out.Flush();
        return ready ? 0 : 4;
    }

    /// <summary>
    /// Waits until every expected role has published a readiness record. The wait rendezvouses on
    /// the records themselves; the timeout is only a failure guard.
    /// </summary>
    private static bool WaitForRoles(string directory, IReadOnlyList<string> roles, int timeoutMs)
    {
        if (roles.Count == 0) return true;

        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < timeoutMs)
        {
            if (roles.All(role => File.Exists(RecordPath(directory, role))))
            {
                return true;
            }

            Thread.Sleep(15);
        }

        return roles.All(role => File.Exists(RecordPath(directory, role)));
    }

    private static void PublishReadiness(string directory, string role, string identity, int pid, int? spawnedPid)
    {
        Directory.CreateDirectory(directory);
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"role={role} id={identity} pid={pid} spawned={(spawnedPid?.ToString(CultureInfo.InvariantCulture) ?? "none")}");

        var finalPath = RecordPath(directory, role);
        var tempPath = finalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tempPath, line);

        // Atomic publish: a waiter never observes a partially written record.
        File.Move(tempPath, finalPath, overwrite: true);
    }

    private static string? TryReadRecord(string directory, string role)
    {
        try
        {
            var path = RecordPath(directory, role);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string RecordPath(string directory, string role) =>
        Path.Combine(directory, role + ".ready");

    // ------------------------------------------------------------------
    // Options
    // ------------------------------------------------------------------

    private sealed class Options
    {
        public string RendezvousDirectory { get; set; } = Path.GetTempPath();
        public string RootId { get; set; } = "root";
        public string ChildId { get; set; } = "child";
        public string GrandchildId { get; set; } = "grandchild";
        public int LifetimeMs { get; set; } = DefaultLifetimeMs;
        public int BarrierTimeoutMs { get; set; } = DefaultBarrierTimeoutMs;
        public int MaxDepth { get; set; } = 2;
        public HashSet<string> ExitAfterReady { get; } = new(StringComparer.Ordinal);
        public List<string> ExpectedRoles { get; } = new();

        public string IdentityFor(string role) => role switch
        {
            "root" => RootId,
            "child" => ChildId,
            _ => GrandchildId
        };

        public int Depth(string role) => role switch
        {
            "root" => 0,
            "child" => 1,
            _ => 2
        };

        public IEnumerable<string> ArgumentsFor(string role)
        {
            yield return role;
            yield return "--dir";
            yield return RendezvousDirectory;
            yield return "--root-id";
            yield return RootId;
            yield return "--child-id";
            yield return ChildId;
            yield return "--grandchild-id";
            yield return GrandchildId;
            yield return "--lifetime-ms";
            yield return LifetimeMs.ToString(CultureInfo.InvariantCulture);
            yield return "--barrier-timeout-ms";
            yield return BarrierTimeoutMs.ToString(CultureInfo.InvariantCulture);
            yield return "--max-depth";
            yield return MaxDepth.ToString(CultureInfo.InvariantCulture);

            if (ExpectedRoles.Count > 0)
            {
                yield return "--expect";
                yield return string.Join(",", ExpectedRoles);
            }

            foreach (var exiting in ExitAfterReady)
            {
                yield return "--exit-after-ready";
                yield return exiting;
            }
        }
    }

    private static Options ParseOptions(string[] args)
    {
        var options = new Options();

        for (var i = 1; i < args.Length; i++)
        {
            var name = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {name}.");

            switch (name)
            {
                case "--dir":
                    options.RendezvousDirectory = Next();
                    break;
                case "--root-id":
                    options.RootId = Next();
                    break;
                case "--child-id":
                    options.ChildId = Next();
                    break;
                case "--grandchild-id":
                    options.GrandchildId = Next();
                    break;
                case "--lifetime-ms":
                    options.LifetimeMs = int.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                case "--barrier-timeout-ms":
                    options.BarrierTimeoutMs = int.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                case "--max-depth":
                    options.MaxDepth = int.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                case "--exit-after-ready":
                    options.ExitAfterReady.Add(Next());
                    break;
                case "--expect":
                    foreach (var role in Next().Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        options.ExpectedRoles.Add(role.Trim());
                    }

                    break;
                default:
                    throw new ArgumentException($"Unknown option '{name}'.");
            }
        }

        if (options.ExpectedRoles.Count == 0)
        {
            options.ExpectedRoles.Add("root");
        }

        // Guard rails: never allow an unbounded lifetime from a malformed invocation.
        if (options.LifetimeMs <= 0 || options.LifetimeMs > DefaultLifetimeMs) options.LifetimeMs = DefaultLifetimeMs;
        if (options.BarrierTimeoutMs <= 0 || options.BarrierTimeoutMs > DefaultBarrierTimeoutMs) options.BarrierTimeoutMs = DefaultBarrierTimeoutMs;

        return options;
    }
}
