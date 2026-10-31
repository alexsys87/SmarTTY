using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SmarTTY.Ssh;

/// <summary>
/// Decides which prompts of a keyboard-interactive login the saved or typed password answers and which ones only the
/// user can answer (a verification code, a one-time password, the new password of an expired account). Servers with
/// two-factor authentication send such prompts in the same conversation as the password prompt, and answering them with
/// the password or an empty text can only fail.
/// </summary>
public sealed class KeyboardInteractiveAnswers
{
    // Prompts that ask for something other than the password of the account. "new", "retype" and "confirm" are the
    // prompts of a password change: the old password must not be sent as the new one.
    private static readonly Regex NeedsUserText = new Regex(
        @"verif|\bcode\b|\btoken\b|\botp\b|one[- ]?time|pass ?code|authenticat|\b2fa\b|\bmfa\b|two[- ]?factor|\bfactor\b|\bduo\b|\boath\b|yubi|\bpin\b|\bsms\b|challenge|response|\bnew\b|re-?type|re-?enter|\bconfirm|\bagain\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex PasswordText = new Regex(
        @"password|passwort|passphrase|mot de passe|contrase|senha|\u043f\u0430\u0440\u043e\u043b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly string _password;

    private bool _firstRound = true;

    public KeyboardInteractiveAnswers(string password)
        : this(password, passwordAlreadyAccepted: false)
    {
    }

    /// <param name="passwordAlreadyAccepted">
    /// An earlier step of a chain of methods (password, then keyboard-interactive) has taken the password: a prompt that
    /// is not recognizable as asking for it is then for the user, not a password prompt in a language the patterns do not
    /// know.
    /// </param>
    public KeyboardInteractiveAnswers(string password, bool passwordAlreadyAccepted)
    {
        _password = password ?? string.Empty;
        _firstRound = !passwordAlreadyAccepted;
        PasswordGiven = passwordAlreadyAccepted;
    }

    /// <summary>Whether the password was sent in any round so far.</summary>
    public bool PasswordGiven { get; private set; }

    /// <summary>Whether the prompt asks for something the saved password cannot answer.</summary>
    public static bool NeedsUser(string prompt)
    {
        return !string.IsNullOrEmpty(prompt) && NeedsUserText.IsMatch(prompt);
    }

    public static bool LooksLikePasswordPrompt(string prompt)
    {
        return !string.IsNullOrEmpty(prompt) && PasswordText.IsMatch(prompt) && !NeedsUser(prompt);
    }

    /// <summary>
    /// Answers one round of prompts: puts the password into <paramref name="responses"/> for the first prompt that asks for
    /// it and returns the indexes of the prompts that are left for the user. A first prompt that is in no language the
    /// patterns know ("Passwort:" in a localized server) still gets the password in the first round, as it always did.
    /// </summary>
    public List<int> Fill(string[] prompts, string[] responses)
    {
        List<int> forUser = new List<int>();
        bool passwordSent = false;
        for (int i = 0; i < prompts.Length; i++)
        {
            if (NeedsUser(prompts[i]))
            {
                forUser.Add(i);
                continue;
            }
            if (!passwordSent && (LooksLikePasswordPrompt(prompts[i]) || (_firstRound && i == 0)))
            {
                responses[i] = _password;
                passwordSent = true;
                PasswordGiven = true;
                continue;
            }
            forUser.Add(i);
        }
        _firstRound = false;
        return forUser;
    }
}
