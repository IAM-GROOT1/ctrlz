namespace CtrlZ.Core.Licensing;

public enum LicenseStatus
{
    Missing,
    Invalid,
    MachineMismatch,
    Expired,
    OfflineGrace,
    Active
}

public sealed class LicenseTokenPayload
{
    public int SchemaVersion { get; set; } = 1;
    public string LicenseId { get; set; } = string.Empty;
    public string Plan { get; set; } = "pro";
    public List<string> Features { get; set; } = new();
    public string MachineId { get; set; } = string.Empty;
    public DateTimeOffset IssuedUtc { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
    public string? Nonce { get; set; }
}

public sealed class LicenseTokenVerification
{
    public LicenseTokenPayload? Payload { get; init; }
    public bool IsValid { get; init; }
    public string? Error { get; init; }
}

public sealed class LicenseSnapshot
{
    public LicenseStatus Status { get; init; } = LicenseStatus.Missing;
    public bool IsProEnabled => Status is LicenseStatus.Active or LicenseStatus.OfflineGrace;
    public string Message { get; init; } = string.Empty;
    public LicenseTokenPayload? Payload { get; init; }
    public DateTimeOffset? LastCheckUtc { get; init; }
    public DateTimeOffset? LastAttemptUtc { get; init; }
    public double GraceDaysRemaining { get; init; }
    public bool ClockWarning { get; init; }
    public string MachineId { get; init; } = string.Empty;

    public bool HasFeature(string featureId) =>
        IsProEnabled &&
        Payload?.Features?.Any(id => id.Equals(featureId, StringComparison.OrdinalIgnoreCase)) == true;
}

public sealed class LicenseException : Exception
{
    public LicenseException(string message) : base(message)
    {
    }

    public LicenseException(string message, Exception inner) : base(message, inner)
    {
    }
}
