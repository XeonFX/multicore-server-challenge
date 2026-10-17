using System;
using System.Collections.Concurrent;
using Npgsql;

namespace Conduit.Data;

/// <summary>
/// A read statement with a pool of commands for it. A command keeps its connection object and typed parameters from
/// one use to the next, so a read allocates no command, connection, parameter collection or parameters: rent one,
/// set its values, run it, and the lease returns it. The pool holds as many commands as were ever in use at once
/// (each request holds one at most), so it needs no cap.
/// <para>
/// On a multiplexed data source each pooled command keeps a connection of its own open. Such a connection holds no
/// Postgres connection while no command runs, so a read neither opens nor closes one, nor goes through the data source
/// command's wrapper (an async state machine of its own per read). Opening it does no I/O once the data source has
/// connected, which <see cref="Db.Init(NpgsqlConnectionStringBuilder, int, bool, int)"/> does before the server takes
/// requests. Without multiplexing an open connection would hold one of the pool's Postgres connections, so there the
/// pooled command is a data source command, which opens and closes its connection on every read.
/// </para>
/// </summary>
public sealed class Read
{
    readonly NpgsqlDataSource _source;
    readonly bool _multiplexed;
    readonly string _sql;
    readonly Type[] _parameters;
    readonly ConcurrentQueue<NpgsqlCommand> _free = new();

    /// <param name="parameters">The parameters' CLR types, in order ($1, $2, ...): long or string.</param>
    public Read(NpgsqlDataSource source, string sql, params Type[] parameters)
    {
        _source = source;
        _multiplexed = new NpgsqlConnectionStringBuilder(source.ConnectionString).Multiplexing;
        _sql = sql;
        _parameters = parameters;
    }

    public Lease Rent()
    {
        if (_free.TryDequeue(out var cmd)) return new Lease(this, cmd);
        cmd = _multiplexed ? new NpgsqlCommand(_sql, _source.OpenConnection()) : _source.CreateCommand(_sql);
        foreach (var t in _parameters)
            cmd.Parameters.Add(t == typeof(long) ? new NpgsqlParameter<long>() : t == typeof(string) ? new NpgsqlParameter<string>()
                : throw new NotSupportedException(t.Name));
        return new Lease(this, cmd);
    }

    void Return(NpgsqlCommand cmd) => _free.Enqueue(cmd);

    /// <summary>One use of a pooled command; disposing it (after its reader) hands the command back.</summary>
    public readonly struct Lease(Read read, NpgsqlCommand cmd) : IDisposable
    {
        public NpgsqlCommand Command => cmd;

        public Lease Set(int i, long value)
        {
            ((NpgsqlParameter<long>)cmd.Parameters[i]).TypedValue = value;
            return this;
        }

        public Lease Set(int i, string value)
        {
            ((NpgsqlParameter<string>)cmd.Parameters[i]).TypedValue = value;
            return this;
        }

        public void Dispose() => read.Return(cmd);
    }
}

/// <summary>Every read statement on one data source, each with its pool of commands.</summary>
public sealed class Reads(NpgsqlDataSource source)
{
    static readonly Type L = typeof(long), S = typeof(string);

    public readonly Read Tags = new(source, Sql.Tags);
    public readonly Read ArticleBySlug = new(source, Sql.ArticleBySlug, L, S);
    public readonly Read Comments = new(source, Sql.Comments, L, S);
    public readonly Read Profile = new(source, Sql.Profile, L, S);
    public readonly Read CurrentUser = new(source, Sql.CurrentUser, L);
    public readonly Read Login = new(source, Sql.Login, S);

    /// <summary>By <see cref="Sql.ListKind"/>: viewer, limit, offset, then the filter (the feed: limit + offset).</summary>
    public readonly Read[] List =
    [
        new(source, Sql.List[(int)Sql.ListKind.Global], L, L, L),
        new(source, Sql.List[(int)Sql.ListKind.Tag], L, L, L, S),
        new(source, Sql.List[(int)Sql.ListKind.Author], L, L, L, S),
        new(source, Sql.List[(int)Sql.ListKind.Favorited], L, L, L, S),
        new(source, Sql.List[(int)Sql.ListKind.Feed], L, L, L, L),
    ];
}

