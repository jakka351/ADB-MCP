using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AdbMcp.Safety
{
    /// <summary>Outcome of checking a shell command against policy.</summary>
    public sealed class ShellDecision
    {
        public bool Allowed { get; set; }
        public bool RequiresConfirmation { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>
    /// The shell tool is treated as a loaded weapon. A command runs only if it matches
    /// the configured allowlist. Anything else is refused — unless the operator has
    /// explicitly enabled confirm-to-override, in which case it becomes a gated action.
    /// </summary>
    public sealed class ShellPolicy
    {
        private readonly List<Regex> _allow = new List<Regex>();
        private readonly bool _allowUnlistedWithConfirm;

        public ShellPolicy(IEnumerable<string> allowlist, bool allowUnlistedWithConfirm)
        {
            _allowUnlistedWithConfirm = allowUnlistedWithConfirm;
            if (allowlist != null)
            {
                foreach (var pattern in allowlist)
                {
                    if (string.IsNullOrWhiteSpace(pattern)) continue;
                    try { _allow.Add(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)); }
                    catch (ArgumentException) { /* skip malformed pattern */ }
                }
            }
        }

        public bool IsAllowlisted(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;
            var trimmed = command.Trim();
            foreach (var re in _allow)
                if (re.IsMatch(trimmed)) return true;
            return false;
        }

        public ShellDecision Evaluate(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return new ShellDecision { Allowed = false, Reason = "Empty command." };

            if (IsAllowlisted(command))
                return new ShellDecision { Allowed = true, RequiresConfirmation = false };

            if (_allowUnlistedWithConfirm)
                return new ShellDecision
                {
                    Allowed = true,
                    RequiresConfirmation = true,
                    Reason = "Command is not on the allowlist; it will run only with confirm=true."
                };

            return new ShellDecision
            {
                Allowed = false,
                Reason = "Blocked by shell allowlist. This command does not match any permitted pattern, " +
                         "and confirm-to-override is disabled. Use a specific tool (tap, type_text, open_app, …) " +
                         "or ask the operator to widen the allowlist."
            };
        }
    }
}
