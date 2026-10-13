using Microsoft.Data.Sqlite;

namespace Training.Operations;

// Supplier-scoped parameterized search adapter.
public sealed class Search(Fixture fixture)
{
    public Response Find(string? authorization, string? name)
    {
        var identity = fixture.Authenticate(authorization);
        if (identity is null) return Response.Empty(401);
        lock (fixture.PolicyLock)
        {
            if (!fixture.HasScope(identity)) return Response.Empty(403);
            if (string.IsNullOrWhiteSpace(name) || name.Length > 100) return Response.Empty(400);
            fixture.Logs.Enqueue("search:accepted");
            try
            {
                return fixture.WithDatabase(connection =>
                {
                    using var command = connection.CreateCommand();
                    command.CommandTimeout = 2;
                    command.CommandText = "SELECT Id, Name FROM Documents WHERE SupplierId = $supplier AND Name = $name ORDER BY Id LIMIT 100";
                    command.Parameters.AddWithValue("$supplier", identity.Supplier!);
                    command.Parameters.AddWithValue("$name", name);
                    Interlocked.Increment(ref fixture.SearchQueries);
                    using var reader = command.ExecuteReader();
                    return new Response(200, Fixture.Rows(reader, false));
                });
            }
            catch (Exception failure) when (failure is SqliteException or InvalidOperationException)
            {
                return Response.Empty(500);
            }
        }
    }
}