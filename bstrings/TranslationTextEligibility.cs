#nullable enable

using System;
using System.Text;

namespace bstrings;

internal static class TranslationTextEligibility
{
    internal static bool ShouldTranslate(
        string text,
        int minimumCharacters,
        int maximumCharacters,
        int minimumLetters = 1
    )
    {
        var characterCount = 0;
        var letterCount = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            characterCount++;
            if (characterCount > maximumCharacters)
            {
                return false;
            }
            if (Rune.IsLetter(rune))
            {
                letterCount++;
            }
        }

        return characterCount >= minimumCharacters
            && letterCount >= minimumLetters
            && !IsObviousMachineIdentifier(text);
    }

    internal static bool IsObviousMachineIdentifier(string text)
    {
        var value = text.AsSpan().Trim();
        if (value.IsEmpty)
        {
            return false;
        }

        var sawToken = false;
        var insideToken = false;
        var hasMachineSyntax = false;
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                if (insideToken && !hasMachineSyntax)
                {
                    return false;
                }
                insideToken = false;
                hasMachineSyntax = false;
                continue;
            }

            sawToken = true;
            insideToken = true;
            if (rune.IsAscii && Rune.IsLetterOrDigit(rune))
            {
                continue;
            }
            if (rune.Value is '_' or '$' or '%' or '{' or '}' or '\\' or '/' or '@' or ':' or '=')
            {
                hasMachineSyntax = true;
                continue;
            }
            if (rune.Value is '-' or '.')
            {
                continue;
            }
            return false;
        }
        return sawToken && (!insideToken || hasMachineSyntax);
    }
}
