using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using TextViewer.Services;

namespace TextViewer.Tests.Services;

/// <summary>
/// Feature: unified-scan-pass, Property 4: State machine transition validity
/// Validates: Requirements 5.7
///
/// For any sequence of scan events (success, failure, cancel), ScanState SHALL only
/// transition through valid forward edges: NotStarted → ScanInProgress → ScanComplete,
/// or to Failed/Cancelled from any active state. Backward transitions SHALL never occur.
/// Failed and Cancelled are terminal.
/// </summary>
public class StateMachineTransitionPropertyTests
{
    /// <summary>
    /// Valid direct state transitions per unified scan design.
    /// </summary>
    private static readonly HashSet<(ScanState From, ScanState To)> ValidTransitions = new()
    {
        (ScanState.NotStarted, ScanState.ScanInProgress),
        (ScanState.NotStarted, ScanState.Failed),          // open error
        (ScanState.ScanInProgress, ScanState.ScanComplete),
        (ScanState.ScanInProgress, ScanState.Failed),
        (ScanState.ScanInProgress, ScanState.Cancelled),
    };

    /// <summary>
    /// Terminal states — no further transitions possible.
    /// </summary>
    private static readonly HashSet<ScanState> TerminalStates = new()
    {
        ScanState.ScanComplete,
        ScanState.Failed,
        ScanState.Cancelled,
    };

    /// <summary>
    /// Forward ordering of states. Backward transitions are forbidden.
    /// </summary>
    private static int StateOrder(ScanState s) => s switch
    {
        ScanState.NotStarted => 0,
        ScanState.ScanInProgress => 1,
        ScanState.ScanComplete => 2,
        ScanState.Failed => 3,
        ScanState.Cancelled => 3,
        _ => -1
    };

    /// <summary>
    /// Computes reachable states from a given state via valid transitions (transitive closure).
    /// </summary>
    private static HashSet<ScanState> ReachableFrom(ScanState from)
    {
        var reachable = new HashSet<ScanState>();
        var queue = new Queue<ScanState>();
        queue.Enqueue(from);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var (f, t) in ValidTransitions)
            {
                if (f == current && reachable.Add(t))
                    queue.Enqueue(t);
            }
        }

        return reachable;
    }

    /// <summary>
    /// Scan scenarios that exercise different transition paths.
    /// </summary>
    public enum ScanScenario
    {
        NonExistentFile,
        ValidFileSuccess,
        CancelledDuringScan,
    }

    private static Arbitrary<ScanScenario> ScanScenarios()
    {
        var gen = Gen.Elements(
            ScanScenario.NonExistentFile,
            ScanScenario.ValidFileSuccess,
            ScanScenario.CancelledDuringScan);
        return Arb.From(gen);
    }

    /// <summary>
    /// Captures state transitions by polling FileIndex state during scan.
    /// Polling may miss intermediate states — property validates reachability.
    /// </summary>
    private static async Task<List<ScanState>> CaptureStateTransitions(
        FileIndex fileIndex,
        CancellationTokenSource? cts = null,
        int cancelAfterMs = 0)
    {
        var transitions = new List<ScanState> { fileIndex.State };
        var previousState = fileIndex.State;

        var scanTask = Task.Run(async () =>
        {
            if (cts != null && cancelAfterMs > 0)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(cancelAfterMs);
                    try { cts.Cancel(); } catch (ObjectDisposedException) { }
                });
            }

            await fileIndex.StartScanAsync();
        });

        while (!scanTask.IsCompleted)
        {
            var currentState = fileIndex.State;
            if (currentState != previousState)
            {
                transitions.Add(currentState);
                previousState = currentState;
            }
            await Task.Delay(1);
        }

        await scanTask;
        var finalState = fileIndex.State;
        if (finalState != previousState)
            transitions.Add(finalState);

        return transitions;
    }

    /// <summary>
    /// Property 4: State machine transition validity
    ///
    /// For any sequence of scan events (success, failure, cancel), ScanState SHALL only
    /// transition through valid forward edges: NotStarted → ScanInProgress → ScanComplete,
    /// or to Failed/Cancelled from any active state. Backward transitions SHALL never occur.
    /// Failed and Cancelled are terminal.
    ///
    /// **Validates: Requirements 5.7**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property ScanState_OnlyTransitionsThroughValidForwardEdges()
    {
        return Prop.ForAll(
            ScanScenarios(),
            scenario =>
            {
                return RunScenarioAndValidateTransitions(scenario).GetAwaiter().GetResult();
            });
    }

    private async Task<Property> RunScenarioAndValidateTransitions(ScanScenario scenario)
    {
        var logger = NullLogger<FileIndex>.Instance;
        List<ScanState> transitions;

        switch (scenario)
        {
            case ScanScenario.NonExistentFile:
            {
                var nonExistentPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "nonexistent.txt");
                using var cts = new CancellationTokenSource();
                using var fileIndex = new FileIndex(nonExistentPath, cts.Token, logger);
                transitions = await CaptureStateTransitions(fileIndex);
                break;
            }

            case ScanScenario.ValidFileSuccess:
            {
                var tempFile = Path.GetTempFileName();
                try
                {
                    await File.WriteAllTextAsync(tempFile, "Line 1\nLine 2\nLine 3\n");
                    using var cts = new CancellationTokenSource();
                    using var fileIndex = new FileIndex(tempFile, cts.Token, logger);
                    transitions = await CaptureStateTransitions(fileIndex);
                }
                finally
                {
                    File.Delete(tempFile);
                }
                break;
            }

            case ScanScenario.CancelledDuringScan:
            {
                var tempFile = Path.GetTempFileName();
                try
                {
                    var content = string.Join("\n", Enumerable.Range(0, 10000).Select(i => new string('x', 100)));
                    await File.WriteAllTextAsync(tempFile, content);
                    using var cts = new CancellationTokenSource();
                    using var fileIndex = new FileIndex(tempFile, cts.Token, logger);
                    transitions = await CaptureStateTransitions(fileIndex, cts, cancelAfterMs: 1);
                }
                finally
                {
                    File.Delete(tempFile);
                }
                break;
            }

            default:
                return false.Label($"Unknown scenario: {scenario}");
        }

        // 1. Initial state must be NotStarted
        if (transitions[0] != ScanState.NotStarted)
        {
            return false.Label(
                $"Initial state should be NotStarted but was {transitions[0]} for {scenario}");
        }

        // 2. Each observed transition must be reachable via valid forward edges
        for (int i = 0; i < transitions.Count - 1; i++)
        {
            var from = transitions[i];
            var to = transitions[i + 1];

            if (from == to) continue;

            var reachable = ReachableFrom(from);
            if (!reachable.Contains(to))
            {
                var seq = string.Join(" → ", transitions);
                return false.Label(
                    $"Invalid transition: {from} → {to}. Sequence [{seq}] for {scenario}. " +
                    $"Reachable from {from}: [{string.Join(", ", reachable)}]");
            }
        }

        // 3. No backward transitions
        for (int i = 0; i < transitions.Count - 1; i++)
        {
            var from = transitions[i];
            var to = transitions[i + 1];

            if (from == to) continue;

            if (StateOrder(to) < StateOrder(from))
            {
                var seq = string.Join(" → ", transitions);
                return false.Label(
                    $"Backward transition: {from} (order={StateOrder(from)}) → {to} (order={StateOrder(to)}). " +
                    $"Sequence [{seq}] for {scenario}");
            }
        }

        // 4. Final state must be terminal
        var finalState = transitions[^1];
        if (!TerminalStates.Contains(finalState))
        {
            return false.Label(
                $"Final state {finalState} is not terminal for {scenario}");
        }

        // 5. No transitions after terminal state
        bool hitTerminal = false;
        for (int i = 0; i < transitions.Count; i++)
        {
            if (hitTerminal && transitions[i] != transitions[i - 1])
            {
                var seq = string.Join(" → ", transitions);
                return false.Label(
                    $"Transition after terminal state at index {i}: {transitions[i-1]} → {transitions[i]}. " +
                    $"Sequence [{seq}] for {scenario}");
            }
            if (TerminalStates.Contains(transitions[i]))
                hitTerminal = true;
        }

        // 6. Scenario-specific expected outcomes
        switch (scenario)
        {
            case ScanScenario.NonExistentFile:
                if (finalState != ScanState.Failed)
                    return false.Label($"NonExistentFile should end Failed but got {finalState}");
                break;

            case ScanScenario.ValidFileSuccess:
                if (finalState != ScanState.ScanComplete)
                    return false.Label($"ValidFileSuccess should end ScanComplete but got {finalState}");
                break;

            case ScanScenario.CancelledDuringScan:
                // May complete before cancellation takes effect
                if (finalState != ScanState.Cancelled && finalState != ScanState.ScanComplete)
                    return false.Label($"CancelledDuringScan should end Cancelled or ScanComplete but got {finalState}");
                break;
        }

        var allTransitions = string.Join(" → ", transitions);
        return true.Label($"Valid transitions for {scenario}: [{allTransitions}]");
    }
}
