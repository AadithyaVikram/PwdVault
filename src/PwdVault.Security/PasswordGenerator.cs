using System.Security.Cryptography;

namespace PwdVault.Security;

public static class PasswordGenerator
{
    public static string Generate(int length = 24, bool upper = true, bool lower = true,
        bool digits = true, bool symbols = true)
    {
        var groups = new List<string>();
        if (upper) groups.Add("ABCDEFGHIJKLMNOPQRSTUVWXYZ");
        if (lower) groups.Add("abcdefghijklmnopqrstuvwxyz");
        if (digits) groups.Add("0123456789");
        if (symbols) groups.Add("!@#$%^&*()-_=+[]{}:,.?");
        if (groups.Count == 0 || length < Math.Max(8, groups.Count) || length > 256)
            throw new ArgumentException("Choose a character set and a length from 8 to 256.");
        string alphabet = string.Concat(groups);
        // Rejection sampling keeps the distribution uniform over all valid passwords.
        char[] chars = new char[length];
        try
        {
            do
            {
                for (int i = 0; i < chars.Length; i++)
                    chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
            } while (groups.Any(group => !chars.Any(group.Contains)));
            return new string(chars);
        }
        finally { Array.Clear(chars); }
    }
}
