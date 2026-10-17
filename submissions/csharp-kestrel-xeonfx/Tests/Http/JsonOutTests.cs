using Conduit.Http;
using Xunit;

namespace Conduit.Tests.Http;

[Collection(ServerState.Collection)]
public class JsonOutTests
{
    [Fact]
    public void ThePoolKeepsNoMoreThanItsBudgetAfterABurst()
    {
        var burst = new JsonOut[(int)(JsonOut.MaxPooledBytes / (16 * 1024)) + 500];
        for (int i = 0; i < burst.Length; i++) burst[i] = JsonOut.Rent();
        foreach (var j in burst) j.Dispose();

        Assert.InRange(JsonOut.PooledBytes, JsonOut.MaxPooledBytes - 16 * 1024, JsonOut.MaxPooledBytes);
        var again = JsonOut.Rent();
        Assert.Contains(again, burst); // what is kept is used again
        again.Dispose();
    }

    [Fact]
    public void AReturnedBufferComesBackEmpty()
    {
        var j = JsonOut.Rent();
        j.W.WriteStartObject();
        j.W.WriteEndObject();
        Assert.Equal("{}"u8, j.Written);
        j.Dispose();
        var next = JsonOut.Rent();
        Assert.True(next.Written.IsEmpty);
        next.Dispose();
    }
}
