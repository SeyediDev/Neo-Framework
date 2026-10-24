using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Fanasa.HrReadCandidate;

public enum HrReadEndpointKind { Chart, History }

public sealed record HrReadTarget(HrReadEndpointKind Kind, Guid TenantId, long? Revision);

/// <summary>
/// Bounded local wire primitives. These prove neither authentication nor admission and are not a host receiver.
/// The frame matches the seven-part framing in the pinned source597 GatewayContextWire.
/// </summary>
public static class HrReadWire
{
    // These are allocation limits for this local primitive, not approved operational profile settings.
    public const int MaximumMethodBytes = 16;
    public const int MaximumTargetBytes = 8192;
    public const int MaximumAuthorizationBytes = 21848;
    public const int MaximumEnvelopeBytes = 21848;
    public const int MaximumIdempotencyBytes = 128;
    public const int MaximumBodyBytes = 1048576;
    public const int MaximumFrameBytes = 65536;
    public const int MinimumKeyBytes = 32;
    public const int MaximumKeyBytes = 4096;

    private const string TargetPrefix = "/api/organization-hr/v1/tenants/";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Only exact backend GET chart/history paths are recognized. No URI normalization is performed.</summary>
    public static bool TryParseTarget(string method, string rawTarget, out HrReadTarget? target)
    {
        target = null;
        if (method != "GET" || rawTarget is null || rawTarget.Length > MaximumTargetBytes
            || !rawTarget.StartsWith(TargetPrefix, StringComparison.Ordinal)) return false;
        var tail = rawTarget.AsSpan(TargetPrefix.Length);
        if (tail.Length < 36 || !Guid.TryParseExact(tail[..36], "D", out var tenant)
            || tenant == Guid.Empty || !tail[..36].SequenceEqual(tenant.ToString("D").AsSpan())) return false;
        tail = tail[36..];
        if (tail.SequenceEqual("/chart"))
        {
            target = new(HrReadEndpointKind.Chart, tenant, null);
            return true;
        }
        const string history = "/history/";
        if (!tail.StartsWith(history, StringComparison.Ordinal)) return false;
        var revisionText = tail[history.Length..];
        if (!long.TryParse(revisionText, NumberStyles.None, CultureInfo.InvariantCulture, out var revision)
            || revision <= 0 || !revisionText.SequenceEqual(revision.ToString(CultureInfo.InvariantCulture).AsSpan()))
            return false;
        target = new(HrReadEndpointKind.History, tenant, revision);
        return true;
    }

    /// <summary>
    /// Probes the actual stream, without trusting Content-Length or seek support. A nonempty probe consumes one byte;
    /// its caller must reject the request. Cancellation and I/O failures propagate to the caller's closed path.
    /// </summary>
    public static async ValueTask<bool> IsActuallyEmptyAsync(Stream body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        cancellationToken.ThrowIfCancellationRequested();
        var probe = new byte[1];
        var count = await body.ReadAsync(probe.AsMemory(), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return count == 0;
    }

    /// <summary>
    /// Frames exact strings and hashes exact body bytes. The envelope is opaque here; its Base64/JSON/schema,
    /// headers, TLS peer, token, timing, authority and approval require separate host validation.
    /// An absent Idempotency-Key is represented by an empty string, never an invented business identifier.
    /// </summary>
    public static byte[] BuildFrame(string method, string rawTarget, string authorization,
        string rawEnvelopeBase64, string idempotencyKey, ReadOnlySpan<byte> body)
    {
        var methodBytes = EncodeBounded(method, MaximumMethodBytes, false, nameof(method));
        var targetBytes = EncodeBounded(rawTarget, MaximumTargetBytes, false, nameof(rawTarget));
        var authorizationBytes = EncodeBounded(authorization, MaximumAuthorizationBytes, false, nameof(authorization));
        var envelopeBytes = EncodeBounded(rawEnvelopeBase64, MaximumEnvelopeBytes, false, nameof(rawEnvelopeBase64));
        var idempotencyBytes = EncodeBounded(idempotencyKey, MaximumIdempotencyBytes, true, nameof(idempotencyKey));
        if (body.Length > MaximumBodyBytes) throw new ArgumentOutOfRangeException(nameof(body));
        using var frame = new MemoryStream();
        Append(frame, StrictUtf8.GetBytes("fanasa.context.v1"));
        Append(frame, methodBytes);
        Append(frame, targetBytes);
        Append(frame, Hash(authorizationBytes));
        Append(frame, Hash(body));
        Append(frame, Hash(idempotencyBytes));
        Append(frame, envelopeBytes);
        if (frame.Length > MaximumFrameBytes) throw new ArgumentOutOfRangeException(nameof(rawEnvelopeBase64));
        return frame.ToArray();
    }

    public static byte[] Sign(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> key)
    {
        if (frame.Length is <= 0 or > MaximumFrameBytes) throw new ArgumentOutOfRangeException(nameof(frame));
        if (key.Length is < MinimumKeyBytes or > MaximumKeyBytes) throw new ArgumentOutOfRangeException(nameof(key));
        return HMACSHA256.HashData(key, frame);
    }

    /// <summary>Verifies a 32-byte HMAC in constant time. A successful comparison alone grants no authority.</summary>
    public static bool Verify(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> key, ReadOnlySpan<byte> signature)
    {
        if (frame.Length is <= 0 or > MaximumFrameBytes || key.Length is < MinimumKeyBytes or > MaximumKeyBytes
            || signature.Length != 32) return false;
        return CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key, frame), signature);
    }

    private static byte[] EncodeBounded(string value, int maximumBytes, bool allowEmpty, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if ((!allowEmpty && value.Length == 0) || value.Length > maximumBytes)
            throw new ArgumentOutOfRangeException(parameterName);
        // Strict encoding rejects unpaired surrogates rather than signing replacement characters.
        if (StrictUtf8.GetByteCount(value) > maximumBytes) throw new ArgumentOutOfRangeException(parameterName);
        return StrictUtf8.GetBytes(value);
    }

    private static byte[] Hash(ReadOnlySpan<byte> bytes)
        => StrictUtf8.GetBytes(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());

    private static void Append(Stream frame, byte[] bytes)
    {
        frame.Write(StrictUtf8.GetBytes(bytes.Length.ToString(CultureInfo.InvariantCulture) + ":"));
        frame.Write(bytes);
    }
}
