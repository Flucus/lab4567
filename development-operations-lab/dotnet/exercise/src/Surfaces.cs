using System.Text;
using Microsoft.Data.Sqlite;

namespace Training.Operations;

// Authorised local adapters, not actual storage services, URLs, or durable workers.
public sealed class Surfaces(Fixture fixture)
{
    private readonly Dictionary<string, string> cache = new(StringComparer.Ordinal);
    public Action AfterInitialCheck = () => { };
    public Content Read(Identity? who, int id)
    {
        lock (fixture.PolicyLock)
        {
            if (who is null) return Content.Denied(401);
            if (!fixture.Allowed(who, id)) return Content.Denied(403);
            return Retrieve(id);
        }
    }
    private Content Retrieve(int id)
    {
        try { return new(200, fixture.Materialize(id)); }
        catch (Exception failure) when (failure is SqliteException or InvalidOperationException)
        { return Content.Denied(500); }
    }
    public Content Preview(Identity? who, int id) => Read(who, id);
    public Content Download(Identity? who, int id) => Read(who, id);
    public Content Storage(Identity? who, int id) => Read(who, id);
    public Content Export(Identity? who, IReadOnlyList<int>? ids)
    {
        lock (fixture.PolicyLock)
        {
            if (who is null) return Content.Denied(401);
            if (ids is null || ids.Count == 0 || ids.Count > 10) return Content.Denied(400);
            foreach (int id in ids) if (!fixture.Allowed(who, id)) return Content.Denied(403);
            var output = new StringBuilder();
            foreach (int id in ids)
            {
                var result = Retrieve(id);
                if (result.Status != 200) return Content.Denied(result.Status);
                output.Append(result.Body);
            }
            return new(200, output.ToString());
        }
    }
    public Content Cached(Identity? who, int id, string name)
    {
        lock (fixture.PolicyLock)
        {
            if (who is null) return Content.Denied(401);
            if (!fixture.Allowed(who, id)) return Content.Denied(403);
            string key = who.Supplier + ":" + id;
            if (cache.TryGetValue(key, out var body)) return new(200, body);
            var result = Retrieve(id);
            if (result.Status == 200) cache[key] = result.Body;
            return result;
        }
    }
    public Job? Enqueue(Identity? who, int id)
    {
        lock (fixture.PolicyLock) return fixture.Allowed(who, id) ? new(who!, id) : null;
    }
    public Content Run(Job? job) => job is null ? Content.Denied(403) : Read(job.Requester, job.DocumentId);
    public Content Deliver(Job? job, Content completed)
    {
        lock (fixture.PolicyLock)
            return job is null || !fixture.Allowed(job.Requester, job.DocumentId) ? Content.Denied(403) : completed;
    }
    public Content GuardedRead(Identity? who, int id)
    {
        if (!fixture.Allowed(who, id)) return Content.Denied(403);
        AfterInitialCheck();
        lock (fixture.PolicyLock)
        {
            if (!fixture.Allowed(who, id)) return Content.Denied(403);
            return Retrieve(id);
        }
    }
}