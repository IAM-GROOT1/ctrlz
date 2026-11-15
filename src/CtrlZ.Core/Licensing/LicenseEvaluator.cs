using System.Security.Cryptography;
using System.Text;

namespace CtrlZ.Core.Licensing;

/// <summary>
/// Applies machine binding, expiry, offline grace and a conservative clock check without
/// contacting a server. It is deliberately transparent and easy to test.
/// </summary>
public static class LicenseEvaluator
{
    public static LicenseSnapshot Evaluate(
        LicenseTokenPayload? payload,
        string machineId,
        DateTimeOffset now,
        DateTimeOffset? lastSuccessfulCheck,
        int graceDays,
        string? tokenError = null,
        DateTimeOffset? lastAttempt = null)
    {
        if (payload is null)
        {
            return new LicenseSnapshot
            {
                Status = LicenseStatus.Invalid,
                Message = tokenError ?? "No valid license is stored.",
                LastCheckUtc = lastSuccessfulCheck,
                LastAttemptUtc = lastAttempt,
                MachineId = machineId
            };
        }

        if (!FixedEquals(payload.MachineId, machineId))
        {
            return new LicenseSnapshot
            {
                Status = LicenseStatus.MachineMismatch,
                Message = "This license is bound to a different PC.",
                Payload = payload,
                LastCheckUtc = lastSuccessfulCheck,
                LastAttemptUtc = lastAttempt,
                MachineId = machineId
            };
        }

        if (payload.ExpiresUtc <= now)
        {
            return new LicenseSnapshot
            {
                Status = LicenseStatus.Expired,
                Message = $"This license expired on {payload.ExpiresUtc.ToLocalTime():d}.",
                Payload = payload,
                LastCheckUtc = lastSuccessfulCheck,
                LastAttemptUtc = lastAttempt,
                MachineId = machineId
            };
        }

        var check = lastSuccessfulCheck ?? now;
        var clockWarning = now < check.AddHours(-1);
        // If the clock moved backwards, use the recorded check time for grace accounting so
        // rolling the clock back cannot extend a license indefinitely.
        var effectiveNow = clockWarning ? check : now;
        var grace = Math.Max(1, graceDays);
        var graceEnd = check.AddDays(grace);
        var remaining = (graceEnd - effectiveNow).TotalDays;
        if (remaining <= 0)
        {
            return new LicenseSnapshot
            {
                Status = LicenseStatus.Expired,
                Message = $"Offline grace expired. Connect to revalidate the license.",
                Payload = payload,
                LastCheckUtc = lastSuccessfulCheck,
                LastAttemptUtc = lastAttempt,
                GraceDaysRemaining = 0,
                ClockWarning = clockWarning,
                MachineId = machineId
            };
        }

        var offlineFor = effectiveNow - check;
        var status = offlineFor > TimeSpan.FromDays(1) ? LicenseStatus.OfflineGrace : LicenseStatus.Active;
        var message = status == LicenseStatus.OfflineGrace
            ? $"Offline grace: {Math.Ceiling(remaining)} day(s) left. Reconnect when possible."
            : "License active.";
        if (clockWarning)
        {
            message = "License active, but the system clock looks wrong. It will be checked again.";
        }

        return new LicenseSnapshot
        {
            Status = status,
            Message = message,
            Payload = payload,
            LastCheckUtc = lastSuccessfulCheck,
            LastAttemptUtc = lastAttempt,
            GraceDaysRemaining = remaining,
            ClockWarning = clockWarning,
            MachineId = machineId
        };
    }

    private static bool FixedEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
