using System.Threading.Tasks;
using Conduit.Data;
using Conduit.Http;
using Microsoft.AspNetCore.Http;

namespace Conduit.Endpoints;

public static class Tags
{
    // GET /api/tags
    public static async Task List(HttpContext ctx)
    {
        JsonOut? j = null;
        try
        {
            using var read = Db.Reads.Tags.Rent();
            await using var r = await read.Command.ExecuteReaderAsync(ctx.RequestAborted);
            await r.ReadAsync();
            j = JsonOut.Rent();
            var w = j.W;
            w.WriteStartObject();
            w.WritePropertyName(Names.Tags);
            if (r.IsDBNull(0)) { w.WriteStartArray(); w.WriteEndArray(); }
            else Rows.WriteRaw(w, r, 0);
            w.WriteEndObject();
        }
        catch
        {
            j?.Dispose(); // the response is the error, not this buffer
            throw;
        }
        await Responses.Send(ctx, 200, j);
    }
}
