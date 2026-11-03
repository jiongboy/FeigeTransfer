using Feige.Core.Storage;
using Xunit;

namespace Feige.Core.Tests;

public sealed class UnreadCounterTests
{
    [Fact]
    public void CountsPersistAndClear()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "unread.json");
        try
        {
            var counter = new UnreadCounter(path);
            string group = UnreadCounter.GroupKey(Guid.NewGuid());
            counter.Increment("A"); counter.Increment("A"); counter.Increment(group);
            Assert.Equal(3, counter.Total());
            Assert.Equal(1, counter.Total(key => key.StartsWith("group:", StringComparison.Ordinal)));

            var reloaded = new UnreadCounter(path);
            Assert.Equal(2, reloaded.Get("A"));
            Assert.True(reloaded.Clear("A"));
            Assert.False(reloaded.Clear("A"));
            Assert.Equal(1, new UnreadCounter(path).Total());
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
    }

    [Fact]
    public void CorruptFileStartsEmpty()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "not json");
            Assert.Equal(0, new UnreadCounter(path).Total());
        }
        finally { File.Delete(path); }
    }
}
