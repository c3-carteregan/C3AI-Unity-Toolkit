using System;

namespace C3AI.Voice
{
    /// <summary>
    /// Static utility class for keyword detection in transcribed text.
    /// Can be used by any speech-to-text implementation.
    /// </summary>
    public static class KeywordUtilities
    {
        /// <summary>
        /// Checks if the text contains the specified keyword.
        /// </summary>
        /// <param name="text">The transcribed text to search in.</param>
        /// <param name="keyword">The keyword to search for.</param>
        /// <param name="requireWordBoundary">If true, the keyword must appear as a whole word (not part of another word).</param>
        /// <returns>True if the keyword is found according to the specified criteria.</returns>
        public static bool ContainsKeyword(string text, string keyword, bool requireWordBoundary = true)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(keyword)) 
                return false;

            string lowerText = text.ToLowerInvariant();
            string lowerKeyword = keyword.ToLowerInvariant();

            int index = lowerText.IndexOf(lowerKeyword, StringComparison.Ordinal);
            if (index < 0) 
                return false;
            
            if (!requireWordBoundary) 
                return true;

            // Check word boundaries
            bool leftBoundary = index == 0 || !char.IsLetterOrDigit(lowerText[index - 1]);
            bool rightBoundary = index + lowerKeyword.Length >= lowerText.Length || 
                                 !char.IsLetterOrDigit(lowerText[index + lowerKeyword.Length]);
            
            return leftBoundary && rightBoundary;
        }

        /// <summary>
        /// Checks if the text contains any of the specified keywords.
        /// </summary>
        /// <param name="text">The transcribed text to search in.</param>
        /// <param name="keywords">Array of keywords to search for.</param>
        /// <param name="requireWordBoundary">If true, keywords must appear as whole words.</param>
        /// <returns>True if any keyword is found.</returns>
        public static bool ContainsAnyKeyword(string text, string[] keywords, bool requireWordBoundary = true)
        {
            if (string.IsNullOrEmpty(text) || keywords == null || keywords.Length == 0)
                return false;

            foreach (string keyword in keywords)
            {
                if (ContainsKeyword(text, keyword, requireWordBoundary))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Checks if the text contains any of the specified keywords and returns the matched keyword.
        /// </summary>
        /// <param name="text">The transcribed text to search in.</param>
        /// <param name="keywords">Array of keywords to search for.</param>
        /// <param name="matchedKeyword">The keyword that was matched, or null if none found.</param>
        /// <param name="requireWordBoundary">If true, keywords must appear as whole words.</param>
        /// <returns>True if any keyword is found.</returns>
        public static bool TryGetMatchedKeyword(string text, string[] keywords, out string matchedKeyword, bool requireWordBoundary = true)
        {
            matchedKeyword = null;

            if (string.IsNullOrEmpty(text) || keywords == null || keywords.Length == 0)
                return false;

            foreach (string keyword in keywords)
            {
                if (ContainsKeyword(text, keyword, requireWordBoundary))
                {
                    matchedKeyword = keyword;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks if the text starts with the specified keyword (wake word detection).
        /// </summary>
        /// <param name="text">The transcribed text to search in.</param>
        /// <param name="keyword">The wake word to check for.</param>
        /// <param name="requireWordBoundary">If true, the keyword must be followed by a word boundary.</param>
        /// <returns>True if the text starts with the keyword.</returns>
        public static bool StartsWithKeyword(string text, string keyword, bool requireWordBoundary = true)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(keyword))
                return false;

            string lowerText = text.ToLowerInvariant().TrimStart();
            string lowerKeyword = keyword.ToLowerInvariant();

            if (!lowerText.StartsWith(lowerKeyword, StringComparison.Ordinal))
                return false;

            if (!requireWordBoundary)
                return true;

            // Check right boundary
            bool rightBoundary = lowerKeyword.Length >= lowerText.Length ||
                                 !char.IsLetterOrDigit(lowerText[lowerKeyword.Length]);

            return rightBoundary;
        }

        /// <summary>
        /// Extracts the command portion of text after removing the wake word.
        /// </summary>
        /// <param name="text">The full transcribed text.</param>
        /// <param name="wakeWord">The wake word to remove from the beginning.</param>
        /// <returns>The text after the wake word, trimmed. Returns original text if wake word not found at start.</returns>
        public static string ExtractCommandAfterWakeWord(string text, string wakeWord)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(wakeWord))
                return text ?? string.Empty;

            string trimmedText = text.TrimStart();
            string lowerText = trimmedText.ToLowerInvariant();
            string lowerWakeWord = wakeWord.ToLowerInvariant();

            if (lowerText.StartsWith(lowerWakeWord, StringComparison.Ordinal))
            {
                string remainder = trimmedText.Substring(wakeWord.Length).TrimStart();
                
                // Remove common filler words that might follow the wake word
                string[] fillers = { ",", ".", "!", "?" };
                foreach (string filler in fillers)
                {
                    if (remainder.StartsWith(filler))
                    {
                        remainder = remainder.Substring(filler.Length).TrimStart();
                        break;
                    }
                }

                return remainder;
            }

            return text;
        }
    }
}
