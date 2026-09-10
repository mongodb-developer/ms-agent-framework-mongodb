using MongoDB.AgentFramework.Samples.Ingestion;

namespace MongoDB.AgentFramework.Samples.Ingestion.Tests;

/// <summary>An in-memory <see cref="IParentLookup"/> substitute used only by offline retriever tests.</summary>
internal sealed class FakeParentLookup : IParentLookup
{
    private readonly Dictionary<string, Dictionary<string, ParentDocument>> _parentsByTenant;

    public FakeParentLookup(IEnumerable<(string TenantId, ParentDocument Parent)> parents)
    {
        _parentsByTenant = new Dictionary<string, Dictionary<string, ParentDocument>>(StringComparer.Ordinal);
        foreach ((string tenantId, ParentDocument parent) in parents)
        {
            if (!_parentsByTenant.TryGetValue(tenantId, out Dictionary<string, ParentDocument>? byId))
            {
                byId = new Dictionary<string, ParentDocument>(StringComparer.Ordinal);
                _parentsByTenant[tenantId] = byId;
            }

            byId[parent.ParentId] = parent;
        }
    }

    public IReadOnlyList<string>? LastRequestedParentIds { get; private set; }

    public Task<IReadOnlyList<ParentDocument>> FindParentsAsync(
        IReadOnlyList<string> parentIds,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastRequestedParentIds = parentIds;

        if (!_parentsByTenant.TryGetValue(tenantId, out Dictionary<string, ParentDocument>? byId))
        {
            return Task.FromResult<IReadOnlyList<ParentDocument>>([]);
        }

        IReadOnlyList<ParentDocument> found = [.. parentIds
            .Where(byId.ContainsKey)
            .Select(id => byId[id])];
        return Task.FromResult(found);
    }
}
