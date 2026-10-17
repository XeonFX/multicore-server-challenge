using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Conduit.Security;

/// <summary>
/// bcrypt jobs on dedicated threads, run up to four interleaved (<see cref="Bcrypt.Run(ReadOnlySpan{BcryptJob})"/>):
/// four hashes cost little more CPU than one. One worker at a time collects a batch, waiting up to the window after
/// the first job arrived for more to join, while the others compute. Logins arrive tens of milliseconds apart, so
/// without the wait nearly every hash would run alone at full cost.
/// </summary>
public sealed class BcryptQueue
{
    /// <summary>A queued job; it completes with the job once a worker has run it.</summary>
    sealed class Pending(BcryptJob job, CancellationToken aborted) : TaskCompletionSource<BcryptJob>(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        public readonly BcryptJob Job = job;
        public readonly CancellationToken Aborted = aborted;
        public readonly long Enqueued = Stopwatch.GetTimestamp();
    }

    readonly BlockingCollection<Pending> _jobs = new();
    readonly Lock _collecting = new();
    readonly long _window;
    readonly int _nice;
    long _batches;

    /// <summary>
    /// Starts <paramref name="threads"/> workers. With <paramref name="nice"/> above 0 they run on Linux as
    /// SCHED_BATCH at that nice, so when the CPUs are saturated logins wait instead of the other requests. Off by
    /// default: at 12k users on 4 CPUs nice 5 kept every other request fast but timed out a third of the logins.
    /// </summary>
    public BcryptQueue(int threads, TimeSpan window, int nice = 0)
    {
        _window = (long)(window.TotalSeconds * Stopwatch.Frequency);
        _nice = nice;
        for (int i = 0; i < threads; i++)
            new Thread(Work) { IsBackground = true, Name = $"bcrypt-{i}" }.Start();
    }

    /// <summary>Batches run so far.</summary>
    public long Batches => Interlocked.Read(ref _batches);

    /// <summary>Completes with <paramref name="job"/> once it has run; cancelled, unrun, if
    /// <paramref name="aborted"/> fires before a worker takes it.</summary>
    public Task<BcryptJob> RunAsync(BcryptJob job, CancellationToken aborted)
    {
        var p = new Pending(job, aborted);
        _jobs.Add(p);
        return p.Task;
    }

    [DllImport("libc")] static extern int gettid();
    [DllImport("libc")] static extern int setpriority(int which, int who, int prio);
    [DllImport("libc")] static extern int sched_setscheduler(int pid, int policy, ref int param);

    void Work()
    {
        if (_nice > 0 && OperatingSystem.IsLinux())
        {
            const int PrioProcess = 0, SchedBatch = 3;
            int tid = gettid(), param = 0;
            sched_setscheduler(tid, SchedBatch, ref param);
            setpriority(PrioProcess, tid, _nice);
        }
        var batch = new Pending[Bcrypt.MaxLanes];
        var jobs = new BcryptJob[Bcrypt.MaxLanes];
        while (true)
        {
            int n;
            lock (_collecting) n = Collect(batch);
            Interlocked.Increment(ref _batches);
            for (int i = 0; i < n; i++) jobs[i] = batch[i].Job;
            try
            {
                Bcrypt.Run(jobs.AsSpan(0, n));
            }
            catch (Exception e)
            {
                for (int i = 0; i < n; i++) batch[i].SetException(e);
                n = 0;
            }
            for (int i = 0; i < n; i++) batch[i].SetResult(batch[i].Job);
            Array.Clear(batch);
            Array.Clear(jobs);
        }
    }

    /// <summary>Blocks for a first job, then takes more until the batch is full or the window after the first
    /// job's arrival has passed (a backlog older than the window is taken without waiting).</summary>
    int Collect(Pending[] batch)
    {
        int n = 0;
        long deadline = 0;
        while (n < batch.Length)
        {
            Pending? p;
            if (n == 0) p = _jobs.Take();
            else
            {
                long left = deadline - Stopwatch.GetTimestamp();
                int ms = left > 0 ? (int)((left * 1000 + Stopwatch.Frequency - 1) / Stopwatch.Frequency) : 0;
                if (!_jobs.TryTake(out p, ms)) break;
            }
            // A client that gave up while its job was queued gets no hash: under overload that CPU goes to the
            // logins still waiting.
            if (p.Aborted.IsCancellationRequested)
            {
                p.SetCanceled(p.Aborted);
                continue;
            }
            if (n == 0) deadline = p.Enqueued + _window;
            batch[n++] = p;
        }
        return n;
    }
}
