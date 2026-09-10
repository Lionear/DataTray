namespace DataTray.Core.Connections.Import;

/// <summary>
/// Reads one entry out of the OS credential store that <b>another application</b> wrote (SE-238).
/// </summary>
/// <remarks>
/// This is deliberately read-only and deliberately dumb: it takes the service name the other application
/// filed its secret under and hands back whatever the OS is willing to give. The OS is the gatekeeper —
/// it prompts, or refuses, according to its own policy, on behalf of the logged-in user. That is the whole
/// basis on which reading another client's password is acceptable here: we do not decide access, we ask
/// for it. A backend must therefore never suppress a prompt, and must treat refusal as an ordinary answer.
///
/// It does <b>not</b> cover secrets sealed with a key that ships inside the other application (DBeaver's
/// <c>credentials-config.json</c>). Those are out of scope by decision, not by omission: the OS never
/// gets a say, so none of the reasoning above applies.
/// </remarks>
public interface IForeignSecretLookup
{
    /// <summary>The secret filed under <paramref name="service"/>, or null when there is none, the store is
    /// locked, or the user declined. All three are ordinary outcomes, not errors.</summary>
    string? Find(string service);
}
