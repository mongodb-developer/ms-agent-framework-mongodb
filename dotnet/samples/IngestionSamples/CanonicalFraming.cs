using System.Buffers.Binary;
using System.Text;

namespace MongoDB.AgentFramework.Samples.Ingestion;

/// <summary>
/// Builds an unambiguous canonical byte sequence from one or more (possibly <see langword="null"/>) string fields,
/// for use as a hash preimage by <see cref="DeterministicId"/> and <see cref="ContentHash.ComputeFramed"/>.
/// </summary>
/// <remarks>
/// Delimiter-joined concatenation (for example <c>string.Join('\u001f', tenantId, sourceId)</c>) is ambiguous:
/// different field-boundary splits of the same underlying characters can produce the exact same joined string
/// whenever a field's own content contains the delimiter (or when field lengths merely shift, for example
/// <c>"ab" + "c"</c> versus <c>"a" + "bc"</c>). Two logically distinct tuples could then silently hash/derive an ID
/// identically. This type instead frames each field with a presence marker byte (distinguishing <see
/// langword="null"/> from an empty string) followed, when present, by a fixed-width big-endian UTF-8 byte-length
/// prefix and then the field's own UTF-8 bytes. Because every field's length is recorded before its bytes, no
/// combination of field values -- including ones containing embedded delimiter or other control characters -- can
/// ever be reinterpreted as a different split of fields.
/// </remarks>
public static class CanonicalFraming
{
    /// <summary>Builds the canonical framed byte sequence for <paramref name="fields"/>, in order.</summary>
    public static byte[] Frame(params string?[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        using var stream = new MemoryStream();
        Span<byte> lengthPrefix = stackalloc byte[4];
        foreach (string? field in fields)
        {
            if (field is null)
            {
                // 0 is the "field is null" marker; never followed by a length prefix or bytes, so a null field can
                // never be confused with a zero-length (empty string) field, which uses marker 1 below.
                stream.WriteByte(0);
                continue;
            }

            stream.WriteByte(1);
            byte[] bytes = Encoding.UTF8.GetBytes(field);
            BinaryPrimitives.WriteUInt32BigEndian(lengthPrefix, (uint)bytes.Length);
            stream.Write(lengthPrefix);
            stream.Write(bytes);
        }

        return stream.ToArray();
    }
}
