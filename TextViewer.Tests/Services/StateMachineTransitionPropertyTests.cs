using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using TextViewer.Services;

namespace TextViewer.Tests.Services;

/// <summary>
/// Property-based tests for state machine transition validity.
/// Validates: Requirements 7.1, 7.2, 7.3, 7.4, 7.5
/// </summary>
public class StateMachineTransitionPropertyTests
{
    /// <summary>
    /// Valid state transitions per the design state diagram (direct edges).
    /// </summary>
    private static readonly HashSet<(ScanState From, ScanState To)> ValidTransitions = new()
    {
        (ScanState.NotStarted, ScanState.QuickScanInProgress),
        (ScanState.NotStarted, ScanState.Failed),
        (ScanState.QuickScanInProgress, ScanState.QuickScanComplete),
        (ScanState.QuickScanInProgress, ScanState.Failed),
        (ScanState.QuickScanInProgress, ScanState.Cancelled),
        (ScanState.QuickScanComplete, ScanState.FullScanInProgress),
        (ScanState.FullScanInProgress, ScanState.FullScanComplete),
        (ScanState.FullScanInProgress, ScanState.Failed),
        (ScanState.FullScanInProgress, ScanState.Cancelled),
    };

    /// <summary>
    /// Computes the set of states reachable from a given state via valid transitions.
    /// Used to validate that observed (possibly non-adjacent) state changes are consistent
    /// with the state diagram — polling may miss intermediate states.
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
                {
                    queue.Enqueue(t);
                }
            }
        }

        return reachable;
    }

    /// <summary>
    /// Terminal states — states from which no further transitions are possible.
    /// </summary>
    private static readonly HashSet<ScanState> TerminalStates = new()
    {
        ScanState.QuickScanComplete, // Full_Scan not yet implemented, stops here
        ScanState.FullScanComplete,
        ScanState.Failed,
        ScanState.Cancelled,
    };

    /// <summary>
    /// States from which Failed or Cancelled can be reached directly.
    /// </summary>
    private static readonly HashSet<ScanState> ValidSourcesForFailedCancelled = new()
    {
        ScanState.NotStarted, // open error → Failed only
        ScanState.QuickScanInProgress,
        ScanState.FullScanInProgress,
    };

    /// <summary>
    /// Represents a scan scenario that exercises different state transition paths.
    /// </summary>
    public enum ScanScenario
    {
        /// <summary>File does not exist → NotStarted → Failed</summary>
        NonExistentFile,
        /// <summary>Valid file, no cancellation → NotStarted → ... → QuickScanComplete</summary>
        ValidFileSuccess,
        /// <summary>Valid file, cancelled during scan → NotStarted → ... → Cancelled</summary>
        CancelledDuringScan,
    }

    /// <summary>
    /// Generates random scan scenarios.
    /// </summary>
    private static Arbitrary<ScanScenario> ScanScenarios()
    {
        var gen = Gen.Elements(
            ScanScenario.NonExistentFile,
            ScanScenario.ValidFileSuccess,
            ScanScenario.CancelledDuringScan);
        return Arb.From(gen);
    }

    /// <summary>
    /// Captures state transitions by polling the FileIndex state during scan execution.
    /// Note: polling may miss intermediate states — the property validates reachability.
    /// </summary>
    private static async Task<List<ScanState>> CaptureStateTransitions(
        FileIndex fileIndex,
        CancellationTokenSource? cts = null,
        int cancelAfterMs = 0)
    {
        var transitions = new List<ScanState> { fileIndex.State };
        var previousState = fileIndex.State;

        // Start the scan in a background task
        var scanTask = Task.Run(async () =>
        {
            if (cts != null && cancelAfterMs > 0)
            {
                // Schedule cancellation after a short delay
                _ = Task.Run(async () =>
                {
                    await Task.Delay(cancelAfterMs);
                    cts.Cancel();
                });
            }

            await fileIndex.StartScanAsync();
        });

        // Poll state transitions until scan completes
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

        // Ensure we capture the final state
        await scanTask;
        var finalState = fileIndex.State;
        if (finalState != previousState)
        {
            transitions.Add(finalState);
        }

        return transitions;
    }

    /// <summary>
    /// Property 6: State machine transition validity
    /// For any sequence of scan events (success, failure, cancellation), the ScanState SHALL
    /// only transition through valid edges: NotStarted→QuickScanInProgress→QuickScanComplete→
    /// FullScanInProgress→FullScanComplete, with Failed or Cancelled reachable from any
    /// InProgress state or from NotStarted (on open error), and no other transitions permitted.
    ///
    /// Since polling may miss intermediate states, we validate that each observed state is
    /// reachable from the previously observed state via valid edges (transitive closure).
    ///
    /// **Validates: Requirements 7.1, 7.2, 7.3, 7.4, 7.5**
    /// </summary>
    [Property(MaxTest = 10)]
    public Property ScanState_OnlyTransitionsThroughValidEdges()
    {
        return Prop.ForAll(
            ScanScenarios(),
            scenario =>
            {
                var result = RunScenarioAndValidateTransitions(scenario).GetAwaiter().GetResult();
                return result;
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
                // Use a non-existent path to trigger NotStarted → Failed
                var nonExistentPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "nonexistent.txt");
                using var cts = new CancellationTokenSource();
                using var fileIndex = new FileIndex(nonExistentPath, cts.Token, logger);

                transitions = await CaptureStateTransitions(fileIndex);
                break;
            }

            case ScanScenario.ValidFileSuccess:
            {
                // Create a temp file with some content
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
                // Create a temp file with enough content to allow cancellation during scan
                var tempFile = Path.GetTempFileName();
                try
                {
                    // Write enough data to give time for cancellation
                    var content = string.Join("\n", Enumerable.Range(0, 10000).Select(i => new string('x', 100)));
                    await File.WriteAllTextAsync(tempFile, content);
                    using var cts = new CancellationTokenSource();
                    using var fileIndex = new FileIndex(tempFile, cts.Token, logger);

                    // Cancel very quickly to try to catch it during scan
                    transitions = await CaptureStateTransitions(fileIndex, cts, cancelAfterMs: 1);
                }
                finally
                {
                    File.Delete(tempFile);
                }
                break;
            }

            default:
                return false.ToProperty().Label($"Unknown scenario: {scenario}");
        }

        // Validate: initial state is NotStarted
        if (transitions[0] != ScanState.NotStarted)
        {
            return false.ToProperty().Label(
                $"Initial state should be NotStarted but was {transitions[0]} for scenario {scenario}");
        }

        // Validate: each observed state is reachable from the previous observed state
        // via valid edges (transitive closure accounts for missed intermediate states)
        for (int i = 0; i < transitions.Count - 1; i++)
        {
            var from = transitions[i];
            var to = transitions[i + 1];

            // Same state observed twice is fine (polling artifact)
            if (from == to) continue;

            var reachable = ReachableFrom(from);
            if (!reachable.Contains(to))
            {
                var transitionList = string.Join(" → ", transitions);
                return false.ToProperty().Label(
                    $"Invalid transition: {from} → {to} is not reachable via valid edges. " +
                    $"Observed sequence [{transitionList}] for scenario {scenario}. " +
                    $"Reachable from {from}: [{string.Join(", ", reachable)}]");
            }
        }

        // Validate: final state is a valid terminal state
        var finalState = transitions[^1];
        if (!TerminalStates.Contains(finalState))
        {
            return false.ToProperty().Label(
                $"Final state {finalState} is not a valid terminal state for scenario {scenario}");
        }

        // Validate: Failed/Cancelled are only reachable from valid source states
        // Check that if we observe Failed or Cancelled, the last non-terminal state before it
        // was a valid source (NotStarted for Failed only, or InProgress states for both)
        if (finalState == ScanState.Failed || finalState == ScanState.Cancelled)
        {
            // Find the last state before the terminal state
            var lastNonTerminal = transitions.Count >= 2 ? transitions[^2] : transitions[0];

            // The last observed state before Failed/Cancelled must be one from which
            // Failed/Cancelled is directly reachable
            var directlyReachable = ValidTransitions
                .Where(t => t.To == finalState)
                .Select(t => t.From)
                .ToHashSet();

            // Since we may have missed intermediate states, check if any valid source
            // is reachable from the last observed non-terminal state
            var reachableFromLast = ReachableFrom(lastNonTerminal);
            reachableFromLast.Add(lastNonTerminal); // include self

            var validPath = reachableFromLast.Intersect(directlyReachable).Any()
                         || directlyReachable.Contains(lastNonTerminal);

            if (!validPath)
            {
                var transitionList = string.Join(" → ", transitions);
                return false.ToProperty().Label(
                    $"{finalState} not reachable from valid source. " +
                    $"Last observed: {lastNonTerminal}. " +
                    $"Valid sources for {finalState}: [{string.Join(", ", directlyReachable)}]. " +
                    $"Sequence [{transitionList}] for scenario {scenario}");
            }
        }

        // Validate scenario-specific expected outcomes
        switch (scenario)
        {
            case ScanScenario.NonExistentFile:
                if (finalState != ScanState.Failed)
                {
                    return false.ToProperty().Label(
                        $"NonExistentFile should end in Failed but got {finalState}");
                }
                break;

            case ScanScenario.ValidFileSuccess:
                // Should reach QuickScanComplete (or further if Full_Scan is implemented)
                if (finalState != ScanState.QuickScanComplete &&
                    finalState != ScanState.FullScanInProgress &&
                    finalState != ScanState.FullScanComplete)
                {
                    return false.ToProperty().Label(
                        $"ValidFileSuccess should reach QuickScanComplete or later but got {finalState}");
                }
                break;

            case ScanScenario.CancelledDuringScan:
                // May end in Cancelled or QuickScanComplete (if scan finishes before cancellation takes effect)
                if (finalState != ScanState.Cancelled &&
                    finalState != ScanState.QuickScanComplete &&
                    finalState != ScanState.FullScanComplete)
                {
                    return false.ToProperty().Label(
                        $"CancelledDuringScan should end in Cancelled or Complete but got {finalState}");
                }
                break;
        }

        var allTransitions = string.Join(" → ", transitions);
        return true.ToProperty().Label($"Valid transitions for {scenario}: [{allTransitions}]");
    }
}
