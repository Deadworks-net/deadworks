using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using DeadworksManaged.Api;
using DeadworksManaged.Telemetry;

namespace DeadworksManaged;

/// <summary>
/// Core timer engine. Maintains tick/ms dual heaps and a deferred-action queue.
/// Called once per frame from PluginLoader.DispatchGameFrame.
/// </summary>
internal static class TimerEngine
{
    private static ILogger? _logger;
    private static ILogger Logger => _logger ??= DeadworksTelemetry.CreateLogger("TimerEngine");

    private static long _currentTick;
    private static long _currentMs;
    private static readonly Stopwatch _stopwatch = new();

    // Tick-based timers: priority = absolute tick when due
    private static readonly PriorityQueue<ScheduledTask, long> _tickHeap = new();
    // Real-time timers: priority = absolute ms when due
    private static readonly PriorityQueue<ScheduledTask, long> _msHeap = new();

    // Thread-safe queue for NextTick deferrals
    private static readonly ConcurrentQueue<Action> _nextTickQueue = new();

    // Throttle: max tasks to execute per frame to prevent frame stalls
    private const int MaxTasksPerFrame = 256;
    private const int MaxNextTickPerFrame = 128;

    public static long CurrentTick => _currentTick;
    public static long CurrentMs => _currentMs;

    /// <summary>
    /// Called once per frame, before plugin OnGameFrame dispatch.
    /// </summary>
    public static void OnTick()
    {
        _currentTick++;

        if (!_stopwatch.IsRunning)
            _stopwatch.Start();

        _currentMs = _stopwatch.ElapsedMilliseconds;

        // 1. Drain NextTick queue (up to limit)
        var nextTickCount = 0;
        while (nextTickCount < MaxNextTickPerFrame && _nextTickQueue.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "NextTick callback threw");
                DeadworksMetrics.TimerErrors.Add(1);
            }
            nextTickCount++;
        }

        // 2. Pop + execute due tick-heap tasks
        var taskCount = 0;
        while (taskCount < MaxTasksPerFrame && _tickHeap.TryPeek(out var task, out var dueTick))
        {
            if (dueTick > _currentTick)
                break;

            _tickHeap.Dequeue();

            // Lazy cancellation
            if (task.Cancelled)
                continue;

            taskCount++;
            ExecuteTask(task);
        }

        // 3. Pop + execute due ms-heap tasks
        while (taskCount < MaxTasksPerFrame && _msHeap.TryPeek(out var task, out var dueMs))
        {
            if (dueMs > _currentMs)
                break;

            _msHeap.Dequeue();

            if (task.Cancelled)
                continue;

            taskCount++;
            ExecuteTask(task);
        }

        if (taskCount > 0)
            DeadworksMetrics.TimerTasksPerFrame.Record(taskCount);
    }

    private static void ExecuteTask(ScheduledTask task)
    {
        if (task.SequenceCallback != null)
        {
            ExecuteSequence(task);
            return;
        }

        try
        {
            task.Callback?.Invoke();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Timer callback threw");
            DeadworksMetrics.TimerErrors.Add(1);
        }

        if (task.Repeating && !task.Cancelled)
        {
            Reschedule(task, task.Interval);
        }
        else
        {
            FinishTask(task);
        }
    }

    private static void ExecuteSequence(ScheduledTask task)
    {
        task.RunCount++;
        var step = new StepContext(task);

        Pace pace;
        try
        {
            pace = task.SequenceCallback!(step);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Sequence callback threw");
            DeadworksMetrics.TimerErrors.Add(1);
            FinishTask(task);
            return;
        }

        switch (pace)
        {
            case WaitPace wait:
                Reschedule(task, wait.Delay);
                break;
            case DonePace:
            default:
                FinishTask(task);
                break;
        }
    }

    private static void Reschedule(ScheduledTask task, Duration delay)
    {
        if (delay.Kind == DurationKind.Ticks)
        {
            _tickHeap.Enqueue(task, _currentTick + delay.Value);
        }
        else
        {
            _msHeap.Enqueue(task, _currentMs + delay.Value);
        }
    }

    private static void FinishTask(ScheduledTask task)
    {
        task.Finished = true;
        task.Handle?.NotifyFinished();
    }

    /// <summary>Schedule a one-shot or repeating task.</summary>
    public static ScheduledTask Schedule(Duration delay, Action callback, bool repeating)
    {
        var task = new ScheduledTask
        {
            Callback = callback,
            Interval = delay,
            Repeating = repeating,
        };

        Reschedule(task, delay);
        return task;
    }

    /// <summary>Schedule a sequence.</summary>
    public static ScheduledTask ScheduleSequence(Func<IStep, Pace> callback)
    {
        var task = new ScheduledTask
        {
            SequenceCallback = callback,
            StartTick = _currentTick,
        };

        // Start on the next tick
        _tickHeap.Enqueue(task, _currentTick + 1);
        return task;
    }

    /// <summary>Enqueue an action for next-tick execution. Thread-safe.</summary>
    public static void EnqueueNextTick(Action action)
    {
        _nextTickQueue.Enqueue(action);
    }

    /// <summary>Reset engine state. Called during full unload.</summary>
    public static void Reset()
    {
        _currentTick = 0;
        _stopwatch.Reset();
        _tickHeap.Clear();
        _msHeap.Clear();

        // Drain the concurrent queue
        while (_nextTickQueue.TryDequeue(out _)) { }
    }

    /// <summary>Sequence step context implementation.</summary>
    private sealed class StepContext : IStep
    {
        private readonly ScheduledTask _task;

        public StepContext(ScheduledTask task) => _task = task;

        public int Run => _task.RunCount;
        public long ElapsedTicks => TimerEngine.CurrentTick - _task.StartTick;

        public Pace Wait(Duration delay) => new WaitPace(delay);
        public Pace Done() => DonePace.Instance;
    }
}
