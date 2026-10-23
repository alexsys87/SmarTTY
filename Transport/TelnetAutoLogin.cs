using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SmarTTY.Transport;

/// <summary>
/// Types the user name and the password of a telnet connection when the server asks for them.
///
/// It watches the text the server sends. When the text ends with a login prompt ("login:", "Username:", ...) the
/// user name is sent, when it ends with a password prompt ("Password:", ...) the password. Each of the two is sent
/// at most once per connection: if the server asks again (a wrong password), nothing is resent, so a bad password
/// can not lock the account by itself. It also stops by itself after a timeout, so a later prompt of some program
/// that is run in the session never gets the password.
///
/// A prompt is recognized only at the end of what was received: "Last login: Mon Oct 19" does not count, a
/// "login:" that the server is waiting after does. Colors and other escape sequences in the text are ignored.
/// A device that asks only for a password (a router line password) is served by leaving the user name empty.
/// </summary>
internal sealed class TelnetAutoLogin
{
    public const string DefaultLoginPrompts = "login:;username:;user name:;user:";

    public const string DefaultPasswordPrompts = "password:;passcode:";

    public const int DefaultTimeoutSeconds = 30;

    // What is kept of the received text: a prompt is short and always at the end.
    private const int MaxTailLength = 400;

    // CSI sequences (colors, cursor movement) and the other two-character escapes.
    private static readonly Regex EscapeSequences = new Regex(@"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B[@-Z\\-_]", RegexOptions.Compiled);

    private enum Stage
    {
        WaitingForLogin,
        WaitingForPassword,
        Finished
    }

    private readonly string _userName;

    private readonly string _password;

    private readonly string[] _loginPrompts;

    private readonly string[] _passwordPrompts;

    private readonly long _timeoutMs;

    private readonly Func<long> _nowMs;

    private readonly string _lineEnding;

    private readonly long _startedMs;

    private readonly StringBuilder _tail = new StringBuilder();

    private Stage _stage;

    /// <summary>Told what the automatic login does (for the trace of a connection); never the user name or the password.</summary>
    public Action<string> Log { get; set; }

    /// <summary>What is configured, without the values: "user name yes, password yes".</summary>
    public string Describe()
    {
        return "user name " + (_userName.Length > 0 ? "yes" : "no") + ", password " + (_password.Length > 0 ? "yes" : "no");
    }

    /// <param name="userName">Empty: the server is not asked for a user name.</param>
    /// <param name="password">Empty: no password is sent.</param>
    /// <param name="loginPrompts">Texts a login prompt ends with, separated by ';' (empty: the default list).</param>
    /// <param name="passwordPrompts">Texts a password prompt ends with, separated by ';' (empty: the default list).</param>
    /// <param name="timeoutSeconds">How long after the start the prompts are watched; 0 is the default.</param>
    /// <param name="nowMs">A clock in milliseconds; only the tests replace it.</param>
    /// <param name="lineEnding">What ends the user name and the password: the same as the Enter key of the terminal sends,
    /// because many servers take CR and LF for two line ends and would read the extra one as an empty answer to the
    /// next prompt (null: CR LF).</param>
    public TelnetAutoLogin(string userName, string password, string loginPrompts, string passwordPrompts, int timeoutSeconds = 0, Func<long> nowMs = null, string lineEnding = null)
    {
        _lineEnding = string.IsNullOrEmpty(lineEnding) ? "\r\n" : lineEnding;
        _userName = userName ?? "";
        _password = password ?? "";
        _loginPrompts = SplitPrompts(loginPrompts, DefaultLoginPrompts);
        _passwordPrompts = SplitPrompts(passwordPrompts, DefaultPasswordPrompts);
        _timeoutMs = (timeoutSeconds > 0 ? timeoutSeconds : DefaultTimeoutSeconds) * 1000L;
        if (nowMs == null)
        {
            // Environment.TickCount64 does not exist on .NET Framework.
            Stopwatch clock = Stopwatch.StartNew();
            nowMs = () => clock.ElapsedMilliseconds;
        }
        _nowMs = nowMs;
        _startedMs = _nowMs();
        _stage = _userName.Length > 0 ? Stage.WaitingForLogin : _password.Length > 0 ? Stage.WaitingForPassword : Stage.Finished;
    }

    /// <summary>True when there is nothing left to send (done, timed out or nothing to send at all).</summary>
    public bool IsFinished => _stage == Stage.Finished;

    /// <summary>
    /// Takes a piece of the text received from the server. Returns what has to be sent in answer, each item a whole
    /// line with its line end (normally none, one item when a prompt was recognized).
    /// </summary>
    public IList<string> Feed(byte[] data, int offset, int count)
    {
        if (_stage == Stage.Finished)
        {
            return Array.Empty<string>();
        }
        if (_nowMs() - _startedMs > _timeoutMs)
        {
            _stage = Stage.Finished;
            _tail.Clear();
            Log?.Invoke("automatic login: gave up, no prompt was recognised within " + (_timeoutMs / 1000) + " s");
            return Array.Empty<string>();
        }
        for (int i = offset; i < offset + count; i++)
        {
            _tail.Append((char)data[i]);
        }
        if (_tail.Length > MaxTailLength)
        {
            _tail.Remove(0, _tail.Length - MaxTailLength);
        }

        string visible = EscapeSequences.Replace(_tail.ToString(), "").ToLowerInvariant().TrimEnd(' ', '\t', '\0');
        if (_stage == Stage.WaitingForLogin && EndsWithAny(visible, _loginPrompts))
        {
            _tail.Clear();
            _stage = _password.Length > 0 ? Stage.WaitingForPassword : Stage.Finished;
            Log?.Invoke("automatic login: login prompt recognised, user name sent");
            return new[] { _userName + _lineEnding };
        }
        // A password prompt before any login prompt is a server that took the user name from the telnet
        // NEW-ENVIRON option (the client announces USER there) and skips its own "login:" question.
        bool passwordFirst = _stage == Stage.WaitingForLogin && _password.Length > 0;
        if ((_stage == Stage.WaitingForPassword || passwordFirst) && EndsWithAny(visible, _passwordPrompts))
        {
            _tail.Clear();
            _stage = Stage.Finished;
            Log?.Invoke(passwordFirst
                ? "automatic login: password prompt came first (user name was passed by the telnet environment option), password sent"
                : "automatic login: password prompt recognised, password sent");
            return new[] { _password + _lineEnding };
        }
        return Array.Empty<string>();
    }

    private static bool EndsWithAny(string text, string[] prompts)
    {
        return prompts.Any(prompt => text.EndsWith(prompt, StringComparison.Ordinal));
    }

    private static string[] SplitPrompts(string prompts, string fallback)
    {
        string[] result = (string.IsNullOrWhiteSpace(prompts) ? fallback : prompts)
            .Split(';')
            .Select(prompt => prompt.Trim().ToLowerInvariant())
            .Where(prompt => prompt.Length > 0)
            .ToArray();
        return result.Length > 0 ? result : fallback.Split(';');
    }
}
