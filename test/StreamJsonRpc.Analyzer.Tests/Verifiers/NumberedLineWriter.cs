// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Text;
using Microsoft.CodeAnalysis.Text;

internal class NumberedLineWriter : TextWriter
{
    internal static readonly string FileSeparator = new string('=', 140);

    private readonly ITestOutputHelper logger;
    private readonly StringBuilder lineBuilder = new StringBuilder();
    private int lineNumber;

    internal NumberedLineWriter(ITestOutputHelper logger)
    {
        this.logger = logger;
    }

    public override Encoding Encoding => Encoding.Unicode;

    public override void WriteLine(string? value)
    {
        this.logger.WriteLine($"{++this.lineNumber,6}: {this.lineBuilder}{value}");
        this.lineBuilder.Clear();
    }

    public override void Write(string? value)
    {
        if (value is null)
        {
            return;
        }

        if (value.EndsWith("\r\n", StringComparison.Ordinal))
        {
            this.WriteLine(value.Substring(0, value.Length - 2));
        }
        else if (value.EndsWith("\n", StringComparison.Ordinal))
        {
            this.WriteLine(value.Substring(0, value.Length - 1));
        }
        else
        {
            this.lineBuilder.Append(value);
        }
    }

    internal static void LogSyntaxTree(SyntaxTree? tree, CancellationToken cancellationToken)
    {
        if (tree is null)
        {
            return;
        }

        ITestOutputHelper logger = TestContext.Current.TestOutputHelper ?? throw new InvalidOperationException();
        logger.WriteLine(FileSeparator);
        logger.WriteLine($"{tree.FilePath} content:");
        logger.WriteLine(FileSeparator);
        using NumberedLineWriter lineWriter = new(logger);
        tree.GetRoot(cancellationToken).WriteTo(lineWriter);
        lineWriter.WriteLine(string.Empty);
    }

    internal static void LogSource(string filename, SourceText text, CancellationToken cancellationToken)
    {
        ITestOutputHelper logger = TestContext.Current.TestOutputHelper ?? throw new InvalidOperationException();
        logger.WriteLine(FileSeparator);
        logger.WriteLine($"{filename} content:");
        logger.WriteLine(FileSeparator);
        using NumberedLineWriter lineWriter = new(logger);
        foreach (TextLine line in text.Lines)
        {
            lineWriter.WriteLine(line);
        }

        lineWriter.WriteLine(string.Empty);
    }
}
