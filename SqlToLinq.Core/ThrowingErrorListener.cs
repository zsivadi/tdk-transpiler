using Antlr4.Runtime;
using System;
using System.IO;

namespace SqlToLinq.Core {

    public class SqlSyntaxException : Exception {
        public SqlSyntaxException(string message) : base(message) { }
    }

    public class ThrowingErrorListener : IAntlrErrorListener<IToken>, IAntlrErrorListener<int> {

        public static readonly ThrowingErrorListener Instance = new ThrowingErrorListener();

        public void SyntaxError(TextWriter output, IRecognizer recognizer, IToken offendingSymbol,
            int line, int charPositionInLine, string msg, RecognitionException e) {
            throw new SqlSyntaxException($"[SQL syntax error] {line}. row, {charPositionInLine}. column: {msg}");
        }

        public void SyntaxError(TextWriter output, IRecognizer recognizer, int offendingSymbol,
            int line, int charPositionInLine, string msg, RecognitionException e) {
            throw new SqlSyntaxException($"[SQL lexer error] {line}. row, {charPositionInLine}. column: {msg}");
        }
    }
}