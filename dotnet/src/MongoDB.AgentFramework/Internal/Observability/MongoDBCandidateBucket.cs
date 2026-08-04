namespace MongoDB.AgentFramework.Internal.Observability;

/// <summary>
/// Buckets a raw candidate/topK count into a small, stable set of ranges so telemetry never carries an
/// unrestricted numeric value as a searchable/groupable field (docs/spec/observability-security.md: "Candidate
/// bucket | Bounded bucket, not raw unrestricted value"). The exact result count for a single operation is a
/// legitimate log/activity field on its own (see <see cref="MongoDBTelemetryResult"/>); this bucket exists
/// specifically for the *candidate* (requested amplification, e.g. <c>numCandidates</c>/<c>topK</c>) value,
/// which is meaningful to observe in aggregate but must never become a high-cardinality dimension.
/// </summary>
internal static class MongoDBCandidateBucket
{
    public const string Zero = "0";
    public const string OneToTen = "1-10";
    public const string ElevenToHundred = "11-100";
    public const string HundredOneToThousand = "101-1000";
    public const string ThousandPlus = "1000+";

    /// <summary>Returns the stable bucket for <paramref name="candidateCount"/>, or <see langword="null"/>
    /// when the operation has no candidate/amplification concept at all (the field is then omitted entirely,
    /// rather than recorded as a meaningless zero).</summary>
    public static string? Bucket(int? candidateCount)
    {
        if (candidateCount is not int count)
        {
            return null;
        }

        return count switch
        {
            <= 0 => Zero,
            <= 10 => OneToTen,
            <= 100 => ElevenToHundred,
            <= 1000 => HundredOneToThousand,
            _ => ThousandPlus,
        };
    }
}
