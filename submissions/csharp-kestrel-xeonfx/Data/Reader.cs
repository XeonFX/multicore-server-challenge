using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Npgsql;

namespace Conduit.Data;

/// <summary>
/// Read groups. Reads from concurrent requests queue up, and loops, each on its own connection, send what has queued
/// as one <see cref="NpgsqlBatch"/>: one Sync for the group, so Postgres starts and ends one implicit transaction and
/// flushes its replies once, instead of once per statement (31 of a statement's 180 µs of Postgres CPU went to that
/// on a 2-vCPU x86 box). Read Committed takes a fresh snapshot for every statement, so each read sees what it would alone.
/// A read completes as soon as its rows are read; a failing statement fails only its own read, and the reads after it,
/// which Postgres skipped, run again as the next group.
/// </summary>
public sealed class Reader
{
    /// <summary>The most reads in one batch: the first read's reply waits until Postgres flushes, at the group's end
    /// or when its 8 KB send buffer fills.</summary>
    public const int MaxGroup = 32;

    /// <summary>
    /// The most reads waiting for a loop. Near the knee every user can have a read in flight, so this is far above
    /// the workload's (tens of thousands of users make a few thousand concurrent reads); it only stops a stalled
    /// database from piling up requests without limit. More are refused at once (503).
    /// </summary>
    public const int MaxQueued = 65536;

    readonly Channel<Job> _jobs = Channel.CreateUnbounded<Job>();
    readonly NpgsqlDataSource _source;
    int _queued;

    /// <param name="source">Opens the loops' connections; they must not be multiplexed.</param>
    public Reader(NpgsqlDataSource source) => _source = source;

    /// <summary>Reads waiting for a loop.</summary>
    public int Queued => Volatile.Read(ref _queued);

    public void Start(int loops)
    {
        for (int i = 0; i < loops; i++) _ = Loop();
    }

    /// <summary>Takes no more reads. Each loop runs what has queued, closes its connection and ends.</summary>
    public void Complete() => _jobs.Writer.TryComplete();

    /// <summary>
    /// Queues <paramref name="command"/>. <paramref name="read"/> gets the reader on its result set, before the first
    /// row, and reads the rows it needs; the task completes with what it returns. If <paramref name="aborted"/> fires
    /// before a loop takes the read into a group, it is cancelled without running.
    /// </summary>
    public Task<T> Run<T>(NpgsqlBatchCommand command, Func<NpgsqlDataReader, ValueTask<T>> read, CancellationToken aborted)
    {
        var job = new Job<T>(command, read, aborted);
        if (aborted.IsCancellationRequested) job.Cancel();
        else if (Interlocked.Increment(ref _queued) > MaxQueued)
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Increment(ref Stats.ReadsRefused);
            job.Fail(new ServerBusyException("too many reads are waiting"));
        }
        else if (!_jobs.Writer.TryWrite(job))
        {
            Interlocked.Decrement(ref _queued);
            job.Fail(new InvalidOperationException("the reader is stopped"));
        }
        return job.Task;
    }

    async Task Loop()
    {
        var group = new List<Job>(MaxGroup);
        NpgsqlConnection? conn = null;
        while (true)
        {
            Job first;
            // ReadAsync hands each read to one waiting loop; WaitToReadAsync would wake them all.
            try { first = await _jobs.Reader.ReadAsync(); }
            catch (ChannelClosedException) { break; }
            Take(first, group);
            while (group.Count < MaxGroup && _jobs.Reader.TryRead(out var job)) Take(job, group);
            if (group.Count == 0) continue;
            Interlocked.Increment(ref Stats.ReadGroups);
            Interlocked.Add(ref Stats.ReadsGrouped, group.Count);
            Stats.Max(ref Stats.ReadGroupMax, group.Count);
            try
            {
                conn ??= await _source.OpenConnectionAsync();
                await RunGroup(conn, group);
            }
            catch (Exception e)
            {
                // The connection broke or never opened: the reads not answered yet fail, and the next group reconnects.
                foreach (var job in group) job.Fail(e);
                if (conn is not null)
                {
                    try { await conn.DisposeAsync(); } catch { }
                    conn = null;
                }
            }
            group.Clear();
        }
        if (conn is not null) await conn.DisposeAsync();
    }

    void Take(Job job, List<Job> group)
    {
        Interlocked.Decrement(ref _queued);
        // Its client is gone and nothing ran yet: the read is dropped.
        if (job.Aborted.IsCancellationRequested) job.Cancel();
        else group.Add(job);
    }

    /// <summary>Runs the group's reads as one batch; a failing statement fails its read and the rest run again.</summary>
    static async Task RunGroup(NpgsqlConnection conn, List<Job> group)
    {
        for (int from = 0; from < group.Count;)
        {
            int i = from;
            try
            {
                await using var batch = conn.CreateBatch();
                for (int j = from; j < group.Count; j++) batch.BatchCommands.Add(group[j].Command);
                await using var r = await batch.ExecuteReaderAsync();
                for (; i < group.Count; i++)
                {
                    if (i > from) await r.NextResultAsync();
                    await group[i].Read(r);
                }
                return;
            }
            catch (PostgresException e) when (conn.State == ConnectionState.Open)
            {
                // Statement i failed; Postgres skipped the ones after it (the batch has one Sync).
                group[i].Fail(e);
                Interlocked.Increment(ref Stats.ReadReruns);
                from = i + 1;
            }
        }
    }

    abstract class Job(NpgsqlBatchCommand command, CancellationToken aborted)
    {
        public readonly NpgsqlBatchCommand Command = command;
        public readonly CancellationToken Aborted = aborted;

        /// <summary>Reads its result set and completes; a Postgres error is the statement's and goes to the group.</summary>
        public abstract ValueTask Read(NpgsqlDataReader r);
        public abstract void Fail(Exception e);
        public abstract void Cancel();
    }

    sealed class Job<T>(NpgsqlBatchCommand command, Func<NpgsqlDataReader, ValueTask<T>> read, CancellationToken aborted) : Job(command, aborted)
    {
        // Continuations run on the thread pool, not on the loop reading the next result set.
        readonly TaskCompletionSource<T> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Task => _done.Task;

        public override async ValueTask Read(NpgsqlDataReader r)
        {
            T result;
            try
            {
                result = await read(r);
            }
            catch (Exception e) when (e is not PostgresException and not NpgsqlException)
            {
                _done.TrySetException(e); // a bug in one read's handler fails that read, not the group
                return;
            }
            _done.TrySetResult(result);
        }

        public override void Fail(Exception e) => _done.TrySetException(e);
        public override void Cancel() => _done.TrySetCanceled(Aborted);
    }
}
