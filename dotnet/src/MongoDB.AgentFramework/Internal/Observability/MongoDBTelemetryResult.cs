namespace MongoDB.AgentFramework.Internal.Observability;

/// <summary>
/// The classification an instrumented operation's own success-handling code returns to
/// <see cref="MongoDBTelemetry.TrackAsync{T}"/>: the stable outcome
/// (<see cref="MongoDBTelemetryOutcome.Success"/> or <see cref="MongoDBTelemetryOutcome.Empty"/> -- never
/// <see cref="MongoDBTelemetryOutcome.Failed"/> or <see cref="MongoDBTelemetryOutcome.Cancelled"/>, which
/// <see cref="MongoDBTelemetry.TrackAsync{T}"/> itself derives from how the wrapped action completed), the
/// result count if the operation has one, and the candidate bucket if the operation has a candidate/topK
/// amplification concept.
/// </summary>
/// <param name="Outcome">One of <see cref="MongoDBTelemetryOutcome.Success"/> or
/// <see cref="MongoDBTelemetryOutcome.Empty"/>.</param>
/// <param name="ResultCount">The number of items the operation produced, or <see langword="null"/> if the
/// operation has no result-count concept (for example a void delete of a single well-known resource).</param>
/// <param name="CandidateBucket">The bucketed candidate/topK amplification the operation requested, from
/// <see cref="MongoDBCandidateBucket.Bucket"/>, or <see langword="null"/> if not applicable.</param>
internal readonly record struct MongoDBTelemetryResult(string Outcome, int? ResultCount, string? CandidateBucket);
