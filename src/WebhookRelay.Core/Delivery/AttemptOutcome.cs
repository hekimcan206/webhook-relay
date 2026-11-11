using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace WebhookRelay.Core.Delivery;

public enum OutcomeKind
{
    /// <summary>2xx: the receiver accepted the message.</summary>
    Delivered,

    /// <summary>Anything else that may succeed later: non-2xx, timeout, refused or reset connection.</summary>
    Retryable,

    /// <summary>410 Gone: the receiver says the endpoint no longer exists. Stop sending to it.</summary>
    Gone,
}

/// <summary>Result of one HTTP attempt, classified for the retry and breaker logic.</summary>
/// <param name="ErrorKind">A stable, low-cardinality label (for metrics and the replay panel), never raw exception text.</param>
public readonly record struct AttemptOutcome(OutcomeKind Kind, int? StatusCode, string? ErrorKind, TimeSpan? RetryAfter = null)
{
    public bool Succeeded => Kind == OutcomeKind.Delivered;
}

public static class OutcomeClassifier
{
    /// <summary>
    /// Standard Webhooks: only 2xx is success, 410 disables the endpoint, everything else is retried. 3xx is a failure
    /// because redirects are deliberately not followed (a redirect could point the relay at an internal address).
    /// </summary>
    public static AttemptOutcome FromStatus(int statusCode, string? retryAfterHeader = null, DateTimeOffset? now = null)
    {
        if (statusCode is >= 200 and < 300)
        {
            return new(OutcomeKind.Delivered, statusCode, null);
        }

        if (statusCode == 410)
        {
            return new(OutcomeKind.Gone, statusCode, "gone");
        }

        var kind = statusCode switch
        {
            >= 500 => "http_5xx",
            429 => "http_429",
            >= 400 => "http_4xx",
            >= 300 => "http_3xx",
            _ => "http_other",
        };
        var retryAfter = statusCode is 429 or 503 ? ParseRetryAfter(retryAfterHeader, now ?? DateTimeOffset.UtcNow) : null;
        return new(OutcomeKind.Retryable, statusCode, kind, retryAfter);
    }

    public static AttemptOutcome FromException(Exception exception, bool timedOut)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (timedOut)
        {
            return new(OutcomeKind.Retryable, null, "timeout");
        }

        var kind = Unwrap(exception) switch
        {
            EgressBlockedException => "egress_blocked",
            SocketException { SocketErrorCode: SocketError.ConnectionRefused } => "connection_refused",
            SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain } => "dns",
            SocketException { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted } => "connection_reset",
            SocketException => "socket",
            IOException => "connection_reset",
            HttpRequestException { HttpRequestError: HttpRequestError.ResponseEnded } => "connection_reset",
            HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => "tls",
            HttpRequestException => "http_error",
            _ => "error",
        };
        return new(OutcomeKind.Retryable, null, kind);
    }

    /// <summary>Retry-After as delta-seconds or an HTTP date (RFC 9110 §10.2.3).</summary>
    public static TimeSpan? ParseRetryAfter(string? value, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }

        if (DateTimeOffset.TryParseExact(value, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            var delta = date - now;
            return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
        }

        return null;
    }

    private static Exception Unwrap(Exception exception)
    {
        // The interesting cause (blocked egress, refused socket) is usually two levels down inside HttpRequestException.
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is EgressBlockedException or SocketException)
            {
                return current;
            }
        }

        return exception is HttpRequestException { InnerException: IOException io } ? io : exception;
    }
}

/// <summary>Raised when the egress policy refuses to connect to an address (private, loopback, link-local...).</summary>
public sealed class EgressBlockedException : Exception
{
    public EgressBlockedException()
    {
    }

    public EgressBlockedException(string message)
        : base(message)
    {
    }

    public EgressBlockedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public static EgressBlockedException For(IPAddress address) =>
        new($"Delivery to {address} is blocked by the egress policy (not a public unicast address).");
}
