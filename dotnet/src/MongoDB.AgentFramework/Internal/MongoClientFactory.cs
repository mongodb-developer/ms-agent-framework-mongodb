using MongoDB.Driver;

namespace MongoDB.AgentFramework.Internal;

internal static class MongoClientFactory
{
    public static OwnedResource<IMongoClient> FromConnectionString(
        string connectionString,
        Func<string, IMongoClient>? clientFactory = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new MongoDBConfigurationException(
                "MongoDB connection string must not be empty.");
        }

        try
        {
            IMongoClient client = (clientFactory ?? (value => new MongoClient(value)))(
                connectionString);
            return OwnedResource<IMongoClient>.Owned(client, value => value.Dispose());
        }
        catch (MongoDBConfigurationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new MongoDBConfigurationException(
                "MongoDB connection string is invalid.",
                exception);
        }
    }

    public static OwnedResource<IMongoClient> FromClient(IMongoClient client) =>
        OwnedResource<IMongoClient>.Borrowed(
            client ?? throw new ArgumentNullException(nameof(client)));
}
