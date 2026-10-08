using System.Text;

namespace DarkFactory.Orchestrator.Dashboard;

/// <summary>
/// <c>factory dashboard set-password</c>: reads the new password twice without echo and stores its
/// PBKDF2 hash in the keychain (written through <c>security -i</c> stdin, never argv). Only the hash is kept.
/// </summary>
public static class SetPassword
{
    public const int MinLength = 12;

    /// <param name="readSecret">Prompts with the given text and reads one line without echo; null at end of input.</param>
    public static int Run(ISecretStore secrets, Func<string, string?> readSecret, TextWriter output, TextWriter error)
    {
        var password = readSecret("New dashboard password: ");
        if (password is null || password.Length < MinLength)
        {
            error.WriteLine($"The password must be at least {MinLength} characters.");
            return 2;
        }
        if (readSecret("Repeat it: ") != password)
        {
            error.WriteLine("The passwords do not match; nothing was stored.");
            return 2;
        }
        var hash = DashboardAuth.HashPassword(password);
        secrets.Set(SecretAccounts.DashboardPasswordHash, hash);
        if (secrets.Get(SecretAccounts.DashboardPasswordHash) != hash)
        {
            error.WriteLine($"The keychain did not return the stored hash (service '{SecretAccounts.Service}', account '{SecretAccounts.DashboardPasswordHash}').");
            return 1;
        }
        output.WriteLine($"Stored the dashboard password hash in the keychain (service '{SecretAccounts.Service}', account '{SecretAccounts.DashboardPasswordHash}'). A running `factory work` uses it at the next login.");
        return 0;
    }

    /// <summary>Reads a line from the console with echo off (or from redirected stdin, for scripts).</summary>
    public static string? ReadConsoleSecret(string prompt)
    {
        Console.Error.Write(prompt);
        if (Console.IsInputRedirected)
        {
            return Console.In.ReadLine();
        }
        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                return text.ToString();
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0)
                {
                    text.Length--;
                }
            }
            else if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key is ConsoleKey.C or ConsoleKey.D)
            {
                Console.Error.WriteLine();
                return null;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                text.Append(key.KeyChar);
            }
        }
    }
}
