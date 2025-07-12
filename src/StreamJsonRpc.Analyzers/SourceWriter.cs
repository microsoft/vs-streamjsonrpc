// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

// Originally copied from https://github.com/eiriktsarpalis/PolyType/blob/main/src/PolyType.Roslyn/SourceWriter.cs
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.Text;

namespace StreamJsonRpc.Analyzers;

/// <summary>
/// A utility class for generating indented source code.
/// </summary>
internal class SourceWriter
{
    /// <summary>
    /// Horizontal whitespace regex: apply double negation on \s to exclude \r and \n.
    /// </summary>
    private const string HWSR = @"[^\S\r\n]*";

    private static readonly Regex NullAssignmentLineRegex =
        new(@$"{HWSR}\w+{HWSR}={HWSR}null{HWSR},?{HWSR}\r?\n", RegexOptions.Compiled);

    private readonly StringBuilder builder = new();

    private int indentation;

    /// <summary>
    /// Initializes a new instance of the <see cref="SourceWriter"/> class.
    /// </summary>
    public SourceWriter()
    {
        this.IndentationChar = '\t';
        this.CharsPerIndentation = 1;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SourceWriter"/> class
    /// with the specified indentation settings.
    /// </summary>
    /// <param name="indentationChar">The indentation character to be used.</param>
    /// <param name="charsPerIndentation">The number of characters per indentation to be applied.</param>
    public SourceWriter(char indentationChar, int charsPerIndentation)
    {
        if (!char.IsWhiteSpace(indentationChar))
        {
            throw new ArgumentOutOfRangeException(nameof(indentationChar));
        }

        if (charsPerIndentation < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(charsPerIndentation));
        }

        this.IndentationChar = indentationChar;
        this.CharsPerIndentation = charsPerIndentation;
    }

    /// <summary>
    /// Gets the character used for indentation.
    /// </summary>
    public char IndentationChar { get; }

    /// <summary>
    /// Gets the number of characters per indentation.
    /// </summary>
    public int CharsPerIndentation { get; }

    /// <summary>
    /// Gets the length of the generated source text.
    /// </summary>
    public int Length => this.builder.Length;

    /// <summary>
    /// Gets or sets the current indentation level.
    /// </summary>
    public int Indentation
    {
        get => this.indentation;
        set
        {
            if (value < 0)
            {
                Throw();
                static void Throw() => throw new ArgumentOutOfRangeException(nameof(value));
            }

            this.indentation = value;
        }
    }

    /// <summary>
    /// Appends a single character and then a new line.
    /// </summary>
    /// <param name="value">The value to write.</param>
    public void WriteLine(char value)
    {
        this.AddIndentation();
        this.builder.Append(value);
        this.builder.AppendLine();
    }

    /// <summary>
    /// Appends a new line with the specified text.
    /// </summary>
    /// <param name="text">The text to append.</param>
    /// <param name="trimNullAssignmentLines">Trims any lines containing 'Identifier = null,' assignments.</param>
    /// <param name="disableIndentation">Append text without preserving the current indentation.</param>
    public void WriteLine(
        [StringSyntax("c#-test")] string text,
        bool trimNullAssignmentLines = false,
        bool disableIndentation = false)
    {
        if (trimNullAssignmentLines)
        {
            // Since the ns2.0 Regex class doesn't support spans,
            // use Regex.Replace to preprocess the string instead
            // of doing a line-by-line replacement.
            text = NullAssignmentLineRegex.Replace(text, string.Empty);
        }

        if (this.indentation == 0 || disableIndentation)
        {
            this.builder.AppendLine(text);
            return;
        }

        bool isFinalLine;
        ReadOnlySpan<char> remainingText = text.AsSpan();
        do
        {
            ReadOnlySpan<char> nextLine = GetNextLine(ref remainingText, out isFinalLine);

            this.AddIndentation();
            this.AppendSpan(nextLine);
            this.builder.AppendLine();
        }
        while (!isFinalLine);
    }

    /// <summary>
    /// Appends a new line to the source text.
    /// </summary>
    public void WriteLine() => this.builder.AppendLine();

    /// <summary>
    /// Encodes the currently written source to a <see cref="SourceText"/> instance.
    /// </summary>
    /// <returns>The <see cref="SourceText"/>.</returns>
    public SourceText ToSourceText()
    {
        if (this.indentation != 0 || this.builder.Length == 0)
        {
            throw new InvalidOperationException("Nothing was written.");
        }

        return SourceText.From(this.builder.ToString(), Encoding.UTF8);
    }

    private static ReadOnlySpan<char> GetNextLine(ref ReadOnlySpan<char> remainingText, out bool isFinalLine)
    {
        if (remainingText.IsEmpty)
        {
            isFinalLine = true;
            return default;
        }

        ReadOnlySpan<char> next;
        ReadOnlySpan<char> rest;

        int lineLength = remainingText.IndexOf('\n');
        if (lineLength == -1)
        {
            lineLength = remainingText.Length;
            isFinalLine = true;
            rest = default;
        }
        else
        {
            rest = remainingText[(lineLength + 1)..];
            isFinalLine = false;
        }

        if ((uint)lineLength > 0 && remainingText[lineLength - 1] == '\r')
        {
            lineLength--;
        }

        next = remainingText[..lineLength];
        remainingText = rest;
        return next;
    }

    private void AddIndentation()
        => this.builder.Append(this.IndentationChar, this.CharsPerIndentation * this.indentation);

    private unsafe void AppendSpan(ReadOnlySpan<char> span)
    {
        fixed (char* ptr = span)
        {
            this.builder.Append(ptr, span.Length);
        }
    }
}
