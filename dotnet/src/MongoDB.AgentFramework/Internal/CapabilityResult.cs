using System.Collections.ObjectModel;

namespace MongoDB.AgentFramework.Internal;

internal sealed class CapabilityResult
{
    public CapabilityResult(
        string name,
        bool supported,
        string? remediation = null,
        IReadOnlyDictionary<string, string>? detectedValues = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Capability name must not be empty.", nameof(name));
        }

        if (!supported && string.IsNullOrWhiteSpace(remediation))
        {
            throw new ArgumentException(
                "Unsupported capabilities require remediation guidance.",
                nameof(remediation));
        }

        Name = name;
        Supported = supported;
        Remediation = remediation;
        DetectedValues = new ReadOnlyDictionary<string, string>(
            detectedValues is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(detectedValues, StringComparer.Ordinal));
    }

    public string Name { get; }

    public bool Supported { get; }

    public string? Remediation { get; }

    public IReadOnlyDictionary<string, string> DetectedValues { get; }

    public void Require()
    {
        if (!Supported)
        {
            throw new MongoDBCapabilityException(
                $"MongoDB capability '{Name}' is unavailable. {Remediation}");
        }
    }
}
