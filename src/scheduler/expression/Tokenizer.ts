// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

export interface Token {
    readonly text: string;
    // Character offset into the original expression, used for error reporting.
    readonly offset: number;
}

// The grammar is whitespace separated words, so tokenizing is a split that
// keeps offsets. Case is normalised here; values that are case sensitive
// (timezone ids and instants) are re-read from the raw text by the parser.
export function tokenize(input: string): Token[] {
    const tokens: Token[] = [];
    let i = 0;

    while (i < input.length) {
        while (i < input.length && isSpace(input[i])) i++;
        if (i >= input.length) break;

        const start = i;
        while (i < input.length && !isSpace(input[i])) i++;
        tokens.push({ text: input.slice(start, i), offset: start });
    }

    return tokens;
}

function isSpace(ch: string): boolean {
    return ch === " " || ch === "\t" || ch === "\n" || ch === "\r";
}
