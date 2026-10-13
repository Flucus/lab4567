package training.operations;

import java.sql.Connection;
import java.sql.SQLException;
import java.sql.PreparedStatement;
import java.sql.ResultSet;
import static training.operations.Contract.*;

/** Supplier-scoped parameterized search adapter. */
public final class Search {
    private final Fixture fixture;
    public Search(Fixture fixture) { this.fixture = fixture; }
    public Response search(String authorization, String name) {
        Identity identity = fixture.authenticate(authorization);
        if (identity == null) return Response.empty(401);
        synchronized (fixture.policyLock) {
            if (!fixture.hasScope(identity)) return Response.empty(403);
            if (name == null || name.isBlank() || name.length() > 100) return Response.empty(400);
            fixture.logs.add("search:accepted");
            try {
                try (Connection c = fixture.connection(); PreparedStatement s = c.prepareStatement(
                        "SELECT Id, Name FROM Documents WHERE SupplierId = ? AND Name = ? ORDER BY Id")) {
                    s.setQueryTimeout(2);
                    s.setMaxRows(100);
                    s.setString(1, identity.supplier());
                    s.setString(2, name);
                    fixture.searchQueries.incrementAndGet();
                    try (ResultSet rs = s.executeQuery()) { return new Response(200, Fixture.summaries(rs)); }
                }
            } catch (SQLException failure) {
                return Response.empty(500);
            }
        }
    }
}