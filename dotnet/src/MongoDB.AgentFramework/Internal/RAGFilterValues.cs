using MongoDB.Bson;

namespace MongoDB.AgentFramework.Internal;

internal static class RAGFilterValues
{
    public static BsonValue ToBsonValue(object value, string paramName)
    {
        if (value is null)
        {
            throw new MongoDBConfigurationException($"{paramName} must not contain a null value.");
        }

        return value switch
        {
            string text => new BsonString(text),
            bool flag => new BsonBoolean(flag),
            int int32 => new BsonInt32(int32),
            long int64 => new BsonInt64(int64),
            double float64 => new BsonDouble(float64),
            decimal @decimal => new BsonDecimal128(@decimal),
            DateTime dateTime => new BsonDateTime(dateTime.ToUniversalTime()),
            DateTimeOffset dateTimeOffset => new BsonDateTime(dateTimeOffset.UtcDateTime),
            ObjectId objectId => new BsonObjectId(objectId),
            _ => throw new MongoDBConfigurationException(
                $"{paramName} type '{value.GetType().Name}' is not a supported filter value type."),
        };
    }
}
