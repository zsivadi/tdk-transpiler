using System.Linq;
using System.Text;
using Antlr4.Runtime;
using System.Reflection;
using System.Collections;
using Antlr4.Runtime.Tree;
using System.Collections.Generic;

namespace SqlToLinq.Core {
    public static class AstVisualizer {
        public static string ExportAntlrTreeToDot(IParseTree tree, Parser parser) {

            var sb = new StringBuilder();

            sb.AppendLine("digraph SqlAst {");
            sb.AppendLine("  node [shape=box, fontname=\"Consolas\"];");

            int counter = 0;

            int Walk(IParseTree node) {

                int id = counter++;
                string label = EscapeForDot(Trees.GetNodeText(node, parser));
                sb.AppendLine($"  n{id} [label=\"{label}\"];");

                for (int i = 0; i < node.ChildCount; i++) {
                    int childId = Walk(node.GetChild(i));
                    sb.AppendLine($"  n{id} -> n{childId};");
                }
                return id;
            }

            Walk(tree);
            sb.AppendLine("}");
            return sb.ToString();
        }

        public static string ExportLinqAstToDot(LinqNode root) {

            var sb = new StringBuilder();

            sb.AppendLine("digraph LinqAst {");
            sb.AppendLine("  node [shape=box, fontname=\"Consolas\"];");

            int counter = 0;

            int Walk(LinqNode node) {
                int id = counter++;
                sb.AppendLine($"  n{id} [label=\"{EscapeForDot(GetNodeLabel(node))}\"];");

                foreach (var (edgeLabel, child) in GetChildren(node)) {
                    int childId = Walk(child);
                    sb.AppendLine($"  n{id} -> n{childId} [label=\"{EscapeForDot(edgeLabel)}\"];");
                }
                return id;
            }

            Walk(root);
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string GetNodeLabel(LinqNode node) {

            var type = node.GetType();
            var parts = new List<string>();

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)) {

                var value = prop.GetValue(node);

                if (value == null) continue;
                if (value is LinqNode) continue;
                if (value is IEnumerable && value is not string) continue;

                parts.Add($"{prop.Name}={value}");
            }

            string typeName = type.Name.Replace("Linq", "").Replace("Node", "");
            return parts.Count > 0 ? $"{typeName} ({string.Join(", ", parts)})" : typeName;
        }

        private static IEnumerable<(string Label, LinqNode Child)> GetChildren(LinqNode node) {

            var type = node.GetType();

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)) {

                var value = prop.GetValue(node);

                if (value == null) continue;

                if (value is LinqNode childNode) {
                    yield return (prop.Name, childNode);
                    continue;
                }

                if (value is IEnumerable enumerable && value is not string) {

                    int idx = 0;
                    foreach (var item in enumerable) {

                        if (item is LinqNode itemNode) {
                            yield return ($"{prop.Name}[{idx}]", itemNode);
                        } else if (item != null) {

                            var itemType = item.GetType();
                            var nodeField = itemType.GetFields()
                                .FirstOrDefault(f => typeof(LinqNode).IsAssignableFrom(f.FieldType));

                            if (nodeField != null) {

                                var innerNode = (LinqNode)nodeField.GetValue(item);
                                if (innerNode != null) {
                                    yield return ($"{prop.Name}[{idx}]", innerNode);
                                }
                            }
                        }

                        idx++;
                    }
                }
            }
        }

        private static string EscapeForDot(string text) =>
            text?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? "";
    }
}
