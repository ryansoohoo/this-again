using System.Text.RegularExpressions;

// Shared command-line text normalization, in the command asmdef so the parser (CommandRegistry) and the
// console UI collapse whitespace identically.
public static class CommandText
{
    // Trim the ends and collapse internal whitespace runs to single spaces.
    public static string Collapse(string s) => Regex.Replace(s.Trim(), @"\s+", " ");
}
