using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace LazyRay.Tools
{
    /// <summary>
    /// Execute C# expressions and menu items in the Unity Editor.
    /// Uses reflection-based evaluation — no domain reload triggered.
    /// 
    /// Modes:
    ///   menu_item  — EditorApplication.ExecuteMenuItem("Window/General/Console")
    ///   evaluate   — Evaluate expression chains: "Selection.activeGameObject.name"
    ///   list_types — Find available types by name fragment
    /// </summary>
    public class ExecuteCodeTool : IMcpTool
    {
        public string Name => "execute_code";
        public string Description =>
            "Execute C# code in Unity Editor (no domain reload).\n" +
            "Actions:\n" +
            "- menu_item: Run a Unity menu item (e.g. 'GameObject/Create Empty')\n" +
            "- evaluate: Evaluate a C# expression chain (e.g. 'Selection.activeGameObject.name')\n" +
            "- list_menus: Search available menu items\n" +
            "- list_types: Search for types by name";

        public bool IsDestructive => true;

        public JObject InputSchema => JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": { ""type"": ""string"", ""description"": ""Action: menu_item, evaluate, list_menus, list_types"" },
                ""expression"": { ""type"": ""string"", ""description"": ""C# expression to evaluate or menu item path"" },
                ""search"": { ""type"": ""string"", ""description"": ""Search query for list_menus/list_types"" }
            },
            ""required"": [""action""]
        }");

        // Common namespace → type mappings for resolution
        private static readonly Dictionary<string, Type> _typeShortcuts = new()
        {
            // UnityEditor
            ["Selection"] = typeof(Selection),
            ["EditorApplication"] = typeof(EditorApplication),
            ["AssetDatabase"] = typeof(AssetDatabase),
            ["EditorGUIUtility"] = typeof(EditorGUIUtility),
            ["EditorPrefs"] = typeof(EditorPrefs),
            ["EditorSceneManager"] = typeof(UnityEditor.SceneManagement.EditorSceneManager),
            ["PrefabUtility"] = typeof(PrefabUtility),
            ["Undo"] = typeof(Undo),
            ["SceneView"] = typeof(SceneView),
            ["EditorWindow"] = typeof(EditorWindow),
            ["PlayerSettings"] = typeof(PlayerSettings),
            ["EditorSettings"] = typeof(EditorSettings),
            
            // UnityEngine
            ["GameObject"] = typeof(GameObject),
            ["Transform"] = typeof(Transform),
            ["Camera"] = typeof(Camera),
            ["Light"] = typeof(Light),
            ["Application"] = typeof(Application),
            ["Debug"] = typeof(Debug),
            ["Physics"] = typeof(Physics),
            ["Resources"] = typeof(Resources),
            ["Time"] = typeof(Time),
            ["Mathf"] = typeof(Mathf),
            ["Vector3"] = typeof(Vector3),
            ["Vector2"] = typeof(Vector2),
            ["Quaternion"] = typeof(Quaternion),
            ["Color"] = typeof(Color),
            ["Material"] = typeof(Material),
            ["Shader"] = typeof(Shader),
            ["Texture2D"] = typeof(Texture2D),
            ["Renderer"] = typeof(Renderer),
            ["MeshRenderer"] = typeof(MeshRenderer),
            ["MeshFilter"] = typeof(MeshFilter),
            ["Collider"] = typeof(Collider),
            ["Rigidbody"] = typeof(Rigidbody),
            ["AudioSource"] = typeof(AudioSource),
            ["Canvas"] = typeof(Canvas),
            ["SceneManager"] = typeof(UnityEngine.SceneManagement.SceneManager),
            ["LayerMask"] = typeof(LayerMask),
            ["RenderSettings"] = typeof(RenderSettings),
            ["QualitySettings"] = typeof(QualitySettings),
            ["Screen"] = typeof(Screen),
            ["SystemInfo"] = typeof(SystemInfo),
        };

        public string Execute(JObject input)
        {
            string action = input.Value<string>("action");
            return action switch
            {
                "menu_item" => DoMenuItem(input),
                "evaluate" => DoEvaluate(input),
                "list_menus" => DoListMenus(input),
                "list_types" => DoListTypes(input),
                _ => $"ERROR: Unknown action '{action}'. Use: menu_item, evaluate, list_menus, list_types"
            };
        }

        // ─── Menu Item ─────────────────────────────────────────────

        private string DoMenuItem(JObject input)
        {
            string menuPath = input.Value<string>("expression");
            if (string.IsNullOrEmpty(menuPath)) return "ERROR: 'expression' required (menu item path)";

            bool executed = EditorApplication.ExecuteMenuItem(menuPath);
            return executed
                ? $"Executed menu item: {menuPath}"
                : $"ERROR: Menu item not found or failed: {menuPath}";
        }

        // ─── Expression Evaluator ──────────────────────────────────

        private string DoEvaluate(JObject input)
        {
            string expr = input.Value<string>("expression");
            if (string.IsNullOrEmpty(expr)) return "ERROR: 'expression' required";

            try
            {
                var result = EvaluateExpression(expr);
                return FormatResult(result, expr);
            }
            catch (Exception ex)
            {
                return $"ERROR evaluating '{expr}': {ex.Message}";
            }
        }

        private object EvaluateExpression(string expression)
        {
            expression = expression.Trim();

            // Handle string literals
            if (expression.StartsWith("\"") && expression.EndsWith("\""))
                return expression.Substring(1, expression.Length - 2);

            // Handle numeric literals
            if (int.TryParse(expression, out int intVal)) return intVal;
            if (float.TryParse(expression, out float floatVal)) return floatVal;
            if (expression == "true") return true;
            if (expression == "false") return false;
            if (expression == "null") return null;

            // Split into tokens respecting parentheses and brackets
            var tokens = TokenizeExpression(expression);
            if (tokens.Count == 0) return null;

            // Resolve root token (type or instance)
            object current = ResolveRoot(tokens[0], out Type currentType);

            // Walk the chain
            for (int i = 1; i < tokens.Count; i++)
            {
                if (current == null && currentType == null)
                    throw new Exception($"Cannot access '{tokens[i]}' on null");

                var token = tokens[i];
                Type targetType = current != null ? current.GetType() : currentType;
                bool isStatic = current == null;

                // Method call: token ends with ")"
                if (token.Contains("("))
                {
                    int parenStart = token.IndexOf('(');
                    string methodName = token.Substring(0, parenStart);
                    string argsStr = token.Substring(parenStart + 1, token.Length - parenStart - 2);
                    var args = ParseArguments(argsStr);

                    current = InvokeMethod(targetType, current, methodName, args, isStatic);
                    currentType = current?.GetType();
                }
                // Indexer: token starts with "["
                else if (token.StartsWith("["))
                {
                    string indexStr = token.Trim('[', ']');
                    if (int.TryParse(indexStr, out int index))
                    {
                        if (current is Array arr)
                            current = arr.GetValue(index);
                        else if (current is System.Collections.IList list)
                            current = list[index];
                        else
                            throw new Exception($"Cannot index {targetType.Name} with [{index}]");
                    }
                    else
                    {
                        // String indexer (dictionary-like)
                        var indexer = targetType.GetProperty("Item", new[] { typeof(string) });
                        if (indexer != null)
                            current = indexer.GetValue(current, new object[] { indexStr.Trim('"', '\'') });
                        else
                            throw new Exception($"No string indexer on {targetType.Name}");
                    }
                    currentType = current?.GetType();
                }
                // Property or field
                else
                {
                    var flags = BindingFlags.Public | BindingFlags.NonPublic |
                                (isStatic ? BindingFlags.Static : BindingFlags.Instance | BindingFlags.Static);

                    var prop = targetType.GetProperty(token, flags);
                    if (prop != null)
                    {
                        current = prop.GetValue(isStatic ? null : current);
                        currentType = current?.GetType() ?? prop.PropertyType;
                        continue;
                    }

                    var field = targetType.GetField(token, flags);
                    if (field != null)
                    {
                        current = field.GetValue(isStatic ? null : current);
                        currentType = current?.GetType() ?? field.FieldType;
                        continue;
                    }

                    throw new Exception($"'{token}' not found on {targetType.Name}");
                }
            }

            return current;
        }

        private object ResolveRoot(string token, out Type rootType)
        {
            // Check shortcuts first
            if (_typeShortcuts.TryGetValue(token, out rootType))
                return null; // static type, no instance

            // Try to find type in all assemblies
            rootType = FindType(token);
            if (rootType != null) return null;

            // Could be a method call on a known type
            if (token.Contains("("))
            {
                int parenStart = token.IndexOf('(');
                string typePart = token.Substring(0, parenStart);
                if (_typeShortcuts.TryGetValue(typePart, out rootType))
                {
                    // It's actually a constructor or the first token includes a method call
                    // Fall through to let the caller handle it
                }
            }

            throw new Exception($"Unknown type or variable: '{token}'. Use list_types to search.");
        }

        private List<string> TokenizeExpression(string expr)
        {
            var tokens = new List<string>();
            var current = new StringBuilder();
            int depth = 0;

            for (int i = 0; i < expr.Length; i++)
            {
                char c = expr[i];

                if (c == '(' || c == '[') depth++;
                if (c == ')' || c == ']') depth--;

                if (c == '.' && depth == 0)
                {
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                    }
                }
                else
                {
                    current.Append(c);
                }
            }

            if (current.Length > 0)
                tokens.Add(current.ToString());

            return tokens;
        }

        private object[] ParseArguments(string argsStr)
        {
            if (string.IsNullOrWhiteSpace(argsStr)) return Array.Empty<object>();

            var args = new List<object>();
            var parts = SplitArgs(argsStr);

            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (trimmed.StartsWith("\"") && trimmed.EndsWith("\""))
                    args.Add(trimmed.Substring(1, trimmed.Length - 2));
                else if (trimmed == "true") args.Add(true);
                else if (trimmed == "false") args.Add(false);
                else if (trimmed == "null") args.Add(null);
                else if (int.TryParse(trimmed, out int i)) args.Add(i);
                else if (float.TryParse(trimmed, out float f)) args.Add(f);
                else
                {
                    // Try to evaluate as sub-expression
                    try { args.Add(EvaluateExpression(trimmed)); }
                    catch { args.Add(trimmed); }
                }
            }

            return args.ToArray();
        }

        private List<string> SplitArgs(string argsStr)
        {
            var result = new List<string>();
            var current = new StringBuilder();
            int depth = 0;
            bool inString = false;

            foreach (char c in argsStr)
            {
                if (c == '"') inString = !inString;
                if (!inString)
                {
                    if (c == '(' || c == '[') depth++;
                    if (c == ')' || c == ']') depth--;
                    if (c == ',' && depth == 0)
                    {
                        result.Add(current.ToString());
                        current.Clear();
                        continue;
                    }
                }
                current.Append(c);
            }

            if (current.Length > 0) result.Add(current.ToString());
            return result;
        }

        private object InvokeMethod(Type type, object instance, string name, object[] args, bool isStatic)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic |
                        (isStatic ? BindingFlags.Static : BindingFlags.Instance | BindingFlags.Static);

            // Find best matching method
            var methods = type.GetMethods(flags).Where(m => m.Name == name).ToArray();
            if (methods.Length == 0)
                throw new Exception($"Method '{name}' not found on {type.Name}");

            // Try to find exact match by parameter count first
            foreach (var method in methods.Where(m => m.GetParameters().Length == args.Length))
            {
                try
                {
                    var parameters = method.GetParameters();
                    var convertedArgs = new object[args.Length];
                    bool valid = true;

                    for (int i = 0; i < args.Length; i++)
                    {
                        try
                        {
                            convertedArgs[i] = ConvertArg(args[i], parameters[i].ParameterType);
                        }
                        catch
                        {
                            valid = false;
                            break;
                        }
                    }

                    if (valid)
                        return method.Invoke(isStatic ? null : instance, convertedArgs);
                }
                catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
            }

            // Fallback: try generic invoke
            var fallback = methods.First(m => m.GetParameters().Length == args.Length);
            try { return fallback.Invoke(isStatic ? null : instance, args); }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }

        private object ConvertArg(object arg, Type targetType)
        {
            if (arg == null) return null;
            if (targetType.IsAssignableFrom(arg.GetType())) return arg;
            if (targetType == typeof(string)) return arg.ToString();
            if (targetType.IsEnum && arg is string s) return Enum.Parse(targetType, s);
            return Convert.ChangeType(arg, targetType);
        }

        private Type FindType(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = asm.GetTypes().FirstOrDefault(t => t.Name == name);
                    if (type != null) return type;
                }
                catch { }
            }
            return null;
        }

        // ─── List Menu Items ───────────────────────────────────────

        private string DoListMenus(JObject input)
        {
            string search = input.Value<string>("search") ?? "";

            // Common Unity menu paths — we can't enumerate all menus via public API,
            // but we can provide a useful reference
            var commonMenus = new[]
            {
                "File/Save", "File/Save As...", "File/New Scene", "File/Open Scene",
                "Edit/Undo", "Edit/Redo", "Edit/Select All", "Edit/Deselect All",
                "Edit/Play", "Edit/Pause", "Edit/Step",
                "Edit/Project Settings...", "Edit/Preferences...",
                "GameObject/Create Empty", "GameObject/Create Empty Child",
                "GameObject/3D Object/Cube", "GameObject/3D Object/Sphere",
                "GameObject/3D Object/Capsule", "GameObject/3D Object/Cylinder",
                "GameObject/3D Object/Plane", "GameObject/3D Object/Quad",
                "GameObject/Light/Directional Light", "GameObject/Light/Point Light",
                "GameObject/Light/Spot Light",
                "GameObject/Camera",
                "GameObject/UI/Canvas", "GameObject/UI/Text - TextMeshPro",
                "GameObject/UI/Button - TextMeshPro", "GameObject/UI/Image",
                "Component/Add...",
                "Window/General/Console", "Window/General/Inspector",
                "Window/General/Hierarchy", "Window/General/Project",
                "Window/General/Scene",
                "Assets/Refresh", "Assets/Reimport All",
                "Tools/LazyRay/Start Server", "Tools/LazyRay/Stop Server",
                "Tools/LazyRay/Restart Server",
            };

            var filtered = string.IsNullOrEmpty(search)
                ? commonMenus
                : commonMenus.Where(m => m.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();

            if (filtered.Length == 0)
                return $"No menu items matching '{search}'. Note: this is a subset of common menus, not exhaustive.";

            var sb = new StringBuilder($"Menu items{(string.IsNullOrEmpty(search) ? "" : $" matching '{search}'")}:\n");
            foreach (var menu in filtered)
                sb.AppendLine($"  {menu}");
            sb.AppendLine("\nUse action='menu_item' with expression='<path>' to execute.");
            return sb.ToString();
        }

        // ─── List Types ────────────────────────────────────────────

        private string DoListTypes(JObject input)
        {
            string search = input.Value<string>("search") ?? "";
            if (string.IsNullOrEmpty(search)) return "ERROR: 'search' required for list_types";

            var sb = new StringBuilder();

            // Check shortcuts first
            var shortcutMatches = _typeShortcuts
                .Where(kv => kv.Key.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                .Take(20).ToList();

            if (shortcutMatches.Count > 0)
            {
                sb.AppendLine("Quick-access types (use directly in expressions):");
                foreach (var kv in shortcutMatches)
                    sb.AppendLine($"  {kv.Key} → {kv.Value.FullName}");
                sb.AppendLine();
            }

            // Search all loaded assemblies
            var typeMatches = new List<string>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var type in asm.GetTypes())
                    {
                        if (type.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 && type.IsPublic)
                        {
                            typeMatches.Add(type.FullName);
                            if (typeMatches.Count >= 30) break;
                        }
                    }
                }
                catch { }
                if (typeMatches.Count >= 30) break;
            }

            if (typeMatches.Count > 0)
            {
                sb.AppendLine($"Types matching '{search}' (first 30):");
                foreach (var t in typeMatches.OrderBy(t => t))
                    sb.AppendLine($"  {t}");
            }

            return sb.Length > 0 ? sb.ToString() : $"No types found matching '{search}'";
        }

        // ─── Result Formatting ─────────────────────────────────────

        private string FormatResult(object result, string expr)
        {
            if (result == null) return $"{expr} = null";

            var type = result.GetType();
            var sb = new StringBuilder();
            sb.AppendLine($"{expr} = {FormatValue(result)}");
            sb.AppendLine($"  Type: {type.FullName}");

            // For Unity objects, show extra info
            if (result is GameObject go)
            {
                sb.AppendLine($"  Active: {go.activeSelf} | Layer: {LayerMask.LayerToName(go.layer)} | Tag: {go.tag}");
                sb.AppendLine($"  Position: {go.transform.position}");
                sb.AppendLine($"  Components: {string.Join(", ", go.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name))}");
                sb.AppendLine($"  Children: {go.transform.childCount}");
            }
            else if (result is Component comp)
            {
                sb.AppendLine($"  GameObject: {comp.gameObject.name}");
                // Show serialized properties
                var so = new SerializedObject(comp);
                var prop = so.GetIterator();
                if (prop.NextVisible(true))
                {
                    int shown = 0;
                    do
                    {
                        if (prop.name == "m_Script") continue;
                        sb.AppendLine($"  .{prop.name}: {GetPropValue(prop)}");
                        if (++shown > 10) { sb.AppendLine("  ... (more)"); break; }
                    } while (prop.NextVisible(false));
                }
                so.Dispose();
            }
            else if (result is UnityEngine.Object uObj)
            {
                sb.AppendLine($"  Name: {uObj.name}");
            }
            else if (result is System.Collections.IEnumerable enumerable && !(result is string))
            {
                int count = 0;
                foreach (var item in enumerable)
                {
                    sb.AppendLine($"  [{count}]: {FormatValue(item)}");
                    if (++count >= 20) { sb.AppendLine("  ... (more)"); break; }
                }
                if (count == 0) sb.AppendLine("  (empty)");
            }

            return sb.ToString();
        }

        private string FormatValue(object val)
        {
            if (val == null) return "null";
            if (val is string s) return $"\"{s}\"";
            if (val is GameObject go) return $"GameObject '{go.name}'";
            if (val is Component c) return $"{c.GetType().Name} on '{c.gameObject.name}'";
            if (val is UnityEngine.Object obj) return $"{obj.GetType().Name} '{obj.name}'";
            return val.ToString();
        }

        private string GetPropValue(SerializedProperty prop) => prop.propertyType switch
        {
            SerializedPropertyType.Integer => prop.intValue.ToString(),
            SerializedPropertyType.Boolean => prop.boolValue.ToString(),
            SerializedPropertyType.Float => prop.floatValue.ToString("F3"),
            SerializedPropertyType.String => $"\"{prop.stringValue}\"",
            SerializedPropertyType.ObjectReference => prop.objectReferenceValue != null
                ? $"{prop.objectReferenceValue.name} ({prop.objectReferenceValue.GetType().Name})" : "None",
            SerializedPropertyType.Enum => prop.enumDisplayNames.Length > prop.enumValueIndex && prop.enumValueIndex >= 0
                ? prop.enumDisplayNames[prop.enumValueIndex] : prop.enumValueIndex.ToString(),
            SerializedPropertyType.Vector3 => prop.vector3Value.ToString(),
            SerializedPropertyType.Vector2 => prop.vector2Value.ToString(),
            SerializedPropertyType.Color => prop.colorValue.ToString(),
            _ => $"({prop.propertyType})"
        };
    }
}
