namespace Agentics.MitID.Testing;

/// <summary>
/// Constants for MitID's pre-production environment.
/// </summary>
/// <remarks>
/// <para>
/// The host is a constant and not a setting, deliberately. Everything this
/// library does — reading client credentials out of an unauthenticated
/// bootstrap document, searching the identity register, answering a code-app
/// transaction on a user's behalf — is only defensible because the identities
/// involved are invented by MitID's own test-person generator and refer to
/// nobody. A configurable base URL would turn a test helper into something that
/// looks like it could be pointed at production. It cannot, and should not read
/// as though it could.
/// </para>
/// </remarks>
public static class MitIdTestEnvironment
{
    /// <summary>The MitID pre-production host. Test only, by construction.</summary>
    public const string BaseUrl = "https://pp.mitid.dk";

    /// <summary>
    /// The PIN the MitID Test Tool gives every simulated code app. It is a
    /// published default, not a secret — see the remarks on <see cref="MitIdApprover"/>.
    /// </summary>
    public const string DefaultPin = "112233";
}
