namespace Agentics.MitID.Testing;

/// <summary>
/// The text a human would have read in the MitID app before approving —
/// already base64-decoded.
/// </summary>
/// <param name="ServiceProvider">Who is asking, e.g. "Commute Connects (TEST)".</param>
/// <param name="Header">The heading, e.g. "Log på hos Commute Connects (TEST)".</param>
/// <param name="Body">The body line, when the relying party sent one.</param>
public sealed record MitIdReference(string ServiceProvider, string Header, string Body)
{
    /// <inheritdoc />
    public override string ToString() =>
        string.IsNullOrEmpty(Header) ? ServiceProvider : $"{ServiceProvider}: {Header}";
}

/// <summary>The outcome of answering a pending MitID transaction.</summary>
/// <param name="Answered">True when a transaction was found and answered.</param>
/// <param name="Confirmed">True for an approval, false for a refusal.</param>
/// <param name="Ticket">The transaction id, useful in test output.</param>
/// <param name="Reference">What was approved.</param>
public sealed record MitIdApproval(bool Answered, bool Confirmed, string Ticket, MitIdReference Reference);

/// <summary>One registered test identity.</summary>
/// <param name="UserId">The bruger-ID typed on the MitID login page.</param>
/// <param name="IdentityUuid">MitID's identity id.</param>
/// <param name="AuthenticatorId">The enrolled code app — the "phone".</param>
/// <param name="Pin">The code app's PIN.</param>
/// <param name="Name">The invented name, when known.</param>
public sealed record MitIdTestUser(
    string UserId,
    string IdentityUuid,
    string AuthenticatorId,
    string Pin,
    string? Name = null);

/// <summary>
/// Thrown when a MitID call fails, or when nothing was waiting to approve.
/// </summary>
public sealed class MitIdException : Exception
{
    /// <summary>Creates the exception.</summary>
    public MitIdException(string message) : base(message) { }

    /// <summary>Creates the exception.</summary>
    public MitIdException(string message, Exception inner) : base(message, inner) { }

    /// <summary>
    /// True when the call succeeded but there was no transaction to answer —
    /// usually because the login page has not reached "Åbn MitID app og godkend",
    /// or because nobody pressed "Åbn app på anden enhed".
    /// </summary>
    public bool NothingPending { get; init; }
}
