// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

namespace Tide.Asgard.Scheduler.Expression;

// Character offset into the original expression, used for error reporting.
public readonly record struct Token(string Text, int Offset);

public static class Tokenizer
{
	// The grammar is whitespace separated words, so tokenizing is a split that
	// keeps offsets. Case handling is left to the parser because timezone ids
	// and instants are case sensitive.
	public static List<Token> Tokenize(string input)
	{
		var tokens = new List<Token>();
		var i = 0;

		while (i < input.Length)
		{
			while (i < input.Length && char.IsWhiteSpace(input[i])) i++;
			if (i >= input.Length) break;

			var start = i;
			while (i < input.Length && !char.IsWhiteSpace(input[i])) i++;
			tokens.Add(new Token(input[start..i], start));
		}

		return tokens;
	}
}
