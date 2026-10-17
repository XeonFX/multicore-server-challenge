using System;
using System.Buffers;
using System.Text.Json;
using Conduit.Http;
using Npgsql;

namespace Conduit.Endpoints;

/// <summary>JSON fields read straight from result columns.</summary>
static class Rows
{
    public static string? NullableString(NpgsqlDataReader r, int c) => r.IsDBNull(c) ? null : r.GetString(c);

    /// <summary>A JSON value Postgres built (a bytea column), copied into the response through a pooled buffer.</summary>
    public static void WriteRaw(Utf8JsonWriter w, NpgsqlDataReader r, int c)
    {
        int length = (int)r.GetBytes(c, 0, null, 0, 0);
        byte[] buf = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            r.GetBytes(c, 0, buf, 0, length);
            w.WriteRawValue(buf.AsSpan(0, length), skipInputValidation: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    /// <summary>A profile's username, bio and image from columns <paramref name="c"/> to c + 2, in order.</summary>
    public static void WritePerson(Utf8JsonWriter w, NpgsqlDataReader r, int c)
    {
        w.WriteString(Names.Username, r.GetString(c));
        JsonFields.WriteNullableString(w, Names.Bio, NullableString(r, c + 1));
        JsonFields.WriteNullableString(w, Names.Image, NullableString(r, c + 2));
    }
}
