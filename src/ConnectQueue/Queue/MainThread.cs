using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using ConnectQueue.ApiManager;

namespace ConnectQueue.Queue;

internal static class MainThread
{
    private static readonly ConcurrentQueue<Action> Queue = new();
    private static readonly Stopwatch Clock = new();
    private static int _mainThreadId = -1;

    internal static bool IsMainThread => Thread.CurrentThread.ManagedThreadId == Volatile.Read(ref _mainThreadId);

    internal static void Capture()
    {
        Volatile.Write(ref _mainThreadId, Thread.CurrentThread.ManagedThreadId);
    }

    internal static void Run(Action action)
    {
        if (action == null) return;

        if (IsMainThread)
        {
            Invoke(action);
            return;
        }

        Queue.Enqueue(action);
    }

    internal static void RunNextTick(Action action)
    {
        if (action != null) Queue.Enqueue(action);
    }

    internal static void Pump()
    {
        if (Queue.IsEmpty) return;

        Clock.Restart();
        while (Clock.Elapsed.TotalMilliseconds < 2.0 && Queue.TryDequeue(out Action action))
            Invoke(action);
        Clock.Stop();
    }

    internal static void Clear()
    {
        while (Queue.TryDequeue(out _))
        { }
    }

    private static void Invoke(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            LogManager.Error($"Queued action threw: {exception}");
        }
    }
}