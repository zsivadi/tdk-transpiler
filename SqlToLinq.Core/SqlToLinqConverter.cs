using Antlr4.Runtime;

namespace SqlToLinq.Core {
    public static class SqlToLinqConverter {

        // Pipeline: SQL text -> parse tree -> SqlVisitor -> intermediate tree -> ICodeGenerator -> code
        // The C# generator is used when no generator is given.

        public static string Convert(string sqlInput) =>
            Convert(sqlInput, new CSharpCodeGenerator());

        public static string Convert(string sqlInput, ICodeGenerator generator) {
            var (code, _, _) = ConvertWithAst(sqlInput, generator);
            return code;
        }

        public static (string LinqCode, string SqlAstDot, string LinqAstDot) ConvertWithAst(string sqlInput) =>
            ConvertWithAst(sqlInput, new CSharpCodeGenerator());

        public static (string LinqCode, string SqlAstDot, string LinqAstDot) ConvertWithAst(string sqlInput, ICodeGenerator generator) {

            var (tree, parser) = Parse(sqlInput);
            string sqlAstDot = AstVisualizer.ExportAntlrTreeToDot(tree, parser);

            LinqNode linqAst = new SqlVisitor().Visit(tree);
            string linqAstDot = AstVisualizer.ExportLinqAstToDot(linqAst);

            return (generator.Generate(linqAst), sqlAstDot, linqAstDot);
        }

        // Builds only the intermediate tree, without generating code

        public static LinqNode BuildAst(string sqlInput) {
            var (tree, _) = Parse(sqlInput);
            return new SqlVisitor().Visit(tree);
        }

        private static (SqlParserParser.QueryContext Tree, SqlParserParser Parser) Parse(string sqlInput) {

            var inputStream = new AntlrInputStream(sqlInput);

            var lexer = new SqlParserLexer(inputStream);
            lexer.RemoveErrorListeners();
            lexer.AddErrorListener(ThrowingErrorListener.Instance);

            var tokens = new CommonTokenStream(lexer);
            var parser = new SqlParserParser(tokens);

            parser.RemoveErrorListeners();
            parser.AddErrorListener(ThrowingErrorListener.Instance);

            return (parser.query(), parser);
        }
    }
}
