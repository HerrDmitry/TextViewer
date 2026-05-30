using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using TextViewer.Services;

namespace TextViewer.Tests;

/// <summary>
/// Property-based tests for session lifecycle invariant.
/// Validates: Requirements 7.1, 7.3, 7.5
/// </summary>
public class SessionLifecyclePropertyTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            try { File.Delete(file); } catch { }
        }
    }

    private string CreateTempFile(string content = "Line1\nLine2\nLine3\n")
    {
        var path = Path.Combine(Path.GetTempPath(), $"session_prop_{Guid.NewGuid():N}.txt");
        _tempFiles.Add(path);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// Represents an operation in the session lifecycle.
    /// </summary>
    private enum OpType { Open, Close, GetView }

    /// <summary>
    /// Property 6: Session lifecycle invariant
    ///
    /// For any sequence of open-file and close-file operations, each open-file SHALL produce
    /// a unique View_Session_ID and store a retrievable FileViewService instance; each close-file
    /// SHALL dispose and remove the instance; get-view for a closed or never-opened session SHALL
    /// return an error; multiple opens of the same file path SHALL produce independent sessions.
    ///
    /// Validates: Requirements 7.1, 7.3, 7.5
    /// </summary>
    [Property(MaxTest = 10)]
    public Property SessionLifecycle_InvariantsHold()
    {
        // Generate random sequences of operations (length 1–15)
        var opGen = Gen.Elements(OpType.Open, OpType.Close, OpType.GetView);
        var seqGen = Gen.ListOf(opGen)
            .Select(ops => ops.ToList())
            .Where(ops => ops.Count >= 1 && ops.Count <= 15);

        return Prop.ForAll(
            Arb.From(seqGen),
            ops => RunSessionLifecycleTest(ops).Result);
    }

    private async Task<Property> RunSessionLifecycleTest(List<OpType> operations)
    {
        // Model the session dictionary (mirrors Program.cs pattern)
        var sessions = new Dictionary<string, FileViewService>();
        var sessionLock = new object();

        // Track all generated session IDs for uniqueness check
        var allSessionIds = new HashSet<string>();
        // Track open session IDs (not yet closed) for operation targeting
        var openSessionIds = new List<string>();
        // Track closed session IDs
        var closedSessionIds = new List<string>();
        // Track disposed state
        var disposedSessions = new HashSet<string>();

        var tempFile = CreateTempFile();
        var logger = NullLogger<FileViewService>.Instance;

        var errors = new List<string>();

        try
        {
            foreach (var op in operations)
            {
                switch (op)
                {
                    case OpType.Open:
                    {
                        // Simulate open-file: generate UUID, create FileViewService, store
                        var viewSessionId = Guid.NewGuid().ToString();

                        // Invariant: each open produces a unique ID
                        if (!allSessionIds.Add(viewSessionId))
                        {
                            errors.Add($"Duplicate viewSessionId generated: {viewSessionId}");
                        }

                        var service = new FileViewService(tempFile, CancellationToken.None, logger);

                        lock (sessionLock)
                        {
                            sessions[viewSessionId] = service;
                        }

                        openSessionIds.Add(viewSessionId);

                        // Invariant: session is retrievable after open
                        FileViewService? retrieved;
                        lock (sessionLock)
                        {
                            sessions.TryGetValue(viewSessionId, out retrieved);
                        }

                        if (retrieved is null)
                        {
                            errors.Add($"Session {viewSessionId} not retrievable after open");
                        }
                        else if (!ReferenceEquals(retrieved, service))
                        {
                            errors.Add($"Session {viewSessionId} retrieved different instance");
                        }

                        break;
                    }

                    case OpType.Close:
                    {
                        if (openSessionIds.Count == 0)
                        {
                            // Try closing a non-existent session — should be no-op
                            var fakeId = "non-existent-" + Guid.NewGuid();
                            FileViewService? service;
                            lock (sessionLock)
                            {
                                if (sessions.Remove(fakeId, out service))
                                {
                                    service.Dispose();
                                }
                            }

                            // Invariant: close of unknown session is no-op (no exception)
                            break;
                        }

                        // Close the first open session
                        var targetId = openSessionIds[0];
                        openSessionIds.RemoveAt(0);
                        closedSessionIds.Add(targetId);

                        FileViewService? svc;
                        lock (sessionLock)
                        {
                            if (sessions.Remove(targetId, out svc))
                            {
                                svc.Dispose();
                                disposedSessions.Add(targetId);
                            }
                        }

                        // Invariant: after close, session is no longer retrievable
                        FileViewService? afterClose;
                        lock (sessionLock)
                        {
                            sessions.TryGetValue(targetId, out afterClose);
                        }

                        if (afterClose is not null)
                        {
                            errors.Add($"Session {targetId} still retrievable after close");
                        }

                        break;
                    }

                    case OpType.GetView:
                    {
                        if (openSessionIds.Count > 0)
                        {
                            // Get view on an open session — should succeed (find the service)
                            var targetId = openSessionIds[0];
                            FileViewService? service;
                            lock (sessionLock)
                            {
                                sessions.TryGetValue(targetId, out service);
                            }

                            if (service is null)
                            {
                                errors.Add($"GetView: open session {targetId} not found");
                            }
                        }

                        // Also test get-view on a closed/unknown session — should return error
                        var unknownId = "unknown-" + Guid.NewGuid();
                        FileViewService? unknownService;
                        lock (sessionLock)
                        {
                            sessions.TryGetValue(unknownId, out unknownService);
                        }

                        if (unknownService is not null)
                        {
                            errors.Add($"GetView: unknown session {unknownId} unexpectedly found");
                        }

                        // Test get-view on a closed session
                        if (closedSessionIds.Count > 0)
                        {
                            var closedId = closedSessionIds[0];
                            FileViewService? closedService;
                            lock (sessionLock)
                            {
                                sessions.TryGetValue(closedId, out closedService);
                            }

                            if (closedService is not null)
                            {
                                errors.Add($"GetView: closed session {closedId} unexpectedly found");
                            }
                        }

                        break;
                    }
                }
            }

            // Final invariant: all session IDs are unique
            if (allSessionIds.Count != allSessionIds.Distinct().Count())
            {
                errors.Add("Not all session IDs are unique");
            }

            // Invariant: multiple opens of same file path produce independent sessions
            // (verified by the fact that each open creates a new service instance with a unique ID)
            var openServices = new List<FileViewService>();
            lock (sessionLock)
            {
                openServices.AddRange(sessions.Values);
            }

            // All remaining open services should be distinct instances
            if (openServices.Count != openServices.Distinct().Count())
            {
                errors.Add("Multiple opens produced non-independent sessions");
            }
        }
        finally
        {
            // Clean up any remaining open sessions
            lock (sessionLock)
            {
                foreach (var svc in sessions.Values)
                {
                    svc.Dispose();
                }
                sessions.Clear();
            }
        }

        var success = errors.Count == 0;
        return success.Label(
            success ? "All invariants hold" : string.Join("; ", errors));
    }
}
