using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace LazyRay.Tools
{
    /// <summary>
    /// v6.9: game_design — the senior game designer half of the duo
    /// (scene_design handles space; this handles ARCHITECTURE):
    ///
    ///   structure  create a whole container skeleton in one call
    ///              (paths like "Systems/Audio" — missing parents auto-created)
    ///   create     GameObject with components + initial property values,
    ///              parent chain auto-created, scene-targeted
    ///   configure  add components / set multiple properties on an existing
    ///              object in one call
    ///   wire       set object-reference fields: point a component's field at
    ///              another GameObject, component or asset — the dependency
    ///              wiring a senior does by dragging in the Inspector
    ///
    /// Value forms for properties:
    ///   numbers/bools/strings as-is · "[x,y,z]"/"[x,y,z,w]" vectors ·
    ///   "#RRGGBB" colors · enums by name or int ·
    ///   references: "scene:Path/To/Object" (GameObject),
    ///   "scene:Path/To/Object:ComponentType" (component),
    ///   "asset:Assets/Path/File.asset" (any asset)
    /// All operations undoable.
    /// </summary>
    public class GameDesignTool : IMcpTool
    {
        public string Name => "game_design";

        public string Description =>
            "Hierarchy architecture: structure (container skeletons), create (GameObject + components + values), " +
            "configure (add components/set properties), wire (object-reference fields between components/assets). Multi-scene aware.";

        public bool IsDestructive => true;

        public JObject InputSchema => JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": { ""type"": ""string"", ""enum"": [""structure"", ""create"", ""configure"", ""wire"", ""wire_event"", ""create_asset""], ""description"": ""Operation"" },
                ""path"": { ""type"": ""string"", ""description"": ""Target GameObject path (create: full path incl. name; missing parents auto-created)"" },
                ""paths"": { ""type"": ""string"", ""description"": ""structure: JSON array of paths, e.g. [\""Systems/Audio\"",\""Systems/UI\""]"" },
                ""components"": { ""type"": ""string"", ""description"": ""JSON: [\""BoxCollider\""] or {\""Rigidbody\"":{\""mass\"":2},\""BoxCollider\"":{\""isTrigger\"":true}}"" },
                ""properties"": { ""type"": ""string"", ""description"": ""configure/wire: JSON {\""ComponentType.field\"": value} or {\""field\"": value} with 'component'"" },
                ""component"": { ""type"": ""string"", ""description"": ""Component type for properties/wire when not prefixed"" },
                ""position"": { ""type"": ""string"", ""description"": ""[x,y,z] local position"" },
                ""rotation"": { ""type"": ""string"", ""description"": ""[x,y,z] local euler"" },
                ""tag"": { ""type"": ""string"" },
                ""layer"": { ""type"": ""string"", ""description"": ""Layer name"" },
                ""is_static"": { ""type"": ""boolean"" },
                ""scene"": { ""type"": ""string"", ""description"": ""Scene name (fragment ok) for new root objects in multi-scene setups"" },
                ""event"": { ""type"": ""string"", ""description"": ""wire_event: UnityEvent field name (e.g. 'onClick', 'OnRaised')"" },
                ""listener"": { ""type"": ""string"", ""description"": ""wire_event: 'scene:Path[:Component]' or 'asset:Path'. Omit to LIST current listeners"" },
                ""method"": { ""type"": ""string"", ""description"": ""wire_event: method name on the listener"" },
                ""arg"": { ""type"": ""string"", ""description"": ""wire_event arg: number/bool/text/'scene:..'/'asset:..' (omit for void)"" }
            },
            ""required"": [""action""]
        }");

        public string Execute(JObject input)
        {
            string action = input.Value<string>("action");
            try
            {
                return action switch
                {
                    "structure" => Structure(input),
                    "create"    => Create(input),
                    "configure" => Configure(input),
                    "wire"      => Wire(input),
                    "wire_event" => WireEvent(input),
                    "create_asset" => CreateAsset(input),
                    _ => $"ERROR: Unknown action '{action}'. Use structure, create, configure, wire, wire_event, create_asset."
                };
            }
            catch (Exception ex)
            {
                return $"ERROR: game_design/{action} failed: {ex.Message}";
            }
        }

        // ─── Path & scene plumbing ─────────────────────────────────────

        private static Scene? ResolveScene(string fragment)
        {
            if (string.IsNullOrEmpty(fragment)) return null;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var sc = SceneManager.GetSceneAt(i);
                if (sc.isLoaded && sc.name.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                    return sc;
            }
            return null;
        }

        private static string GetPath(Transform t)
        {
            var sb = new StringBuilder(t.name);
            while (t.parent != null) { t = t.parent; sb.Insert(0, t.name + "/"); }
            return sb.ToString();
        }

        private static GameObject FindByPath(string path, Scene? scene = null)
        {
            if (string.IsNullOrEmpty(path)) return null;
            foreach (var t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (scene.HasValue && t.gameObject.scene != scene.Value) continue;
                if (GetPath(t) == path) return t.gameObject;
            }
            // Fallback: unique name match.
            var byName = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(t => t.name == path && (!scene.HasValue || t.gameObject.scene == scene.Value)).ToList();
            return byName.Count == 1 ? byName[0].gameObject : null;
        }

        /// <summary>Get-or-create every segment of a path. New roots go to the target scene.</summary>
        private static GameObject EnsurePath(string path, Scene? scene, out int created)
        {
            created = 0;
            var segments = path.Split('/').Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
            Transform current = null;
            var walked = new StringBuilder();

            foreach (var seg in segments)
            {
                walked.Append(walked.Length > 0 ? "/" : "").Append(seg);
                Transform next = current == null
                    ? FindByPath(walked.ToString(), scene)?.transform
                    : current.Find(seg);

                if (next == null)
                {
                    var go = new GameObject(seg);
                    if (current != null)
                        go.transform.SetParent(current, false);
                    else if (scene.HasValue)
                        SceneManager.MoveGameObjectToScene(go, scene.Value);
                    Undo.RegisterCreatedObjectUndo(go, $"LazyRay: Create {seg}");
                    next = go.transform;
                    created++;
                }
                current = next;
            }
            return current?.gameObject;
        }

        private static Type ResolveType(string typeName)
        {
            var direct = Type.GetType(typeName);
            if (direct != null) return direct;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(typeName) ??
                        asm.GetTypes().FirstOrDefault(x =>
                            typeof(Component).IsAssignableFrom(x) &&
                            string.Equals(x.Name, typeName, StringComparison.OrdinalIgnoreCase));
                if (t != null) return t;
            }
            return null;
        }

        // ─── Actions ───────────────────────────────────────────────────

        private string Structure(JObject input)
        {
            string raw = input.Value<string>("paths");
            if (string.IsNullOrEmpty(raw)) return "ERROR: structure needs 'paths' (JSON array of container paths).";
            var scene = ResolveScene(input.Value<string>("scene"));

            JArray arr;
            try { arr = JArray.Parse(raw); }
            catch { return "ERROR: 'paths' must be a JSON array of strings."; }

            int totalCreated = 0;
            var lines = new List<string>();
            foreach (var p in arr)
            {
                var go = EnsurePath(p.ToString(), scene, out int created);
                totalCreated += created;
                lines.Add($"  {GetPath(go.transform)}{(created > 0 ? $" (+{created} new)" : " (existed)")}");
            }
            return $"Structure ensured ({totalCreated} object(s) created{(scene.HasValue ? $", scene: {scene.Value.name}" : "")}):\n" +
                   string.Join("\n", lines);
        }

        private string Create(JObject input)
        {
            string path = input.Value<string>("path");
            if (string.IsNullOrEmpty(path)) return "ERROR: create needs 'path' (full path including the new object's name).";
            var scene = ResolveScene(input.Value<string>("scene"));

            var go = EnsurePath(path, scene, out int created);
            if (go == null) return $"ERROR: could not create '{path}'.";

            var notes = new List<string>();
            if (created == 0) notes.Add("already existed — configuring it");

            var pos = ParseVec3(input.Value<string>("position"));
            if (pos.HasValue) go.transform.localPosition = pos.Value;
            var rot = ParseVec3(input.Value<string>("rotation"));
            if (rot.HasValue) go.transform.localEulerAngles = rot.Value;

            string tag = input.Value<string>("tag");
            if (!string.IsNullOrEmpty(tag)) { try { go.tag = tag; } catch { notes.Add($"tag '{tag}' not defined"); } }
            string layer = input.Value<string>("layer");
            if (!string.IsNullOrEmpty(layer))
            {
                int l = LayerMask.NameToLayer(layer);
                if (l >= 0) go.layer = l; else notes.Add($"layer '{layer}' not defined");
            }
            if (input.Value<bool?>("is_static") ?? false) go.isStatic = true;

            string compResult = ApplyComponents(go, input.Value<string>("components"), notes);
            if (compResult != null) return compResult;

            return $"Created '{GetPath(go.transform)}'{(scene.HasValue ? $" [scene: {scene.Value.name}]" : "")}" +
                   (notes.Count > 0 ? $" ({string.Join("; ", notes)})" : "");
        }

        private string Configure(JObject input)
        {
            string path = input.Value<string>("path");
            var notes = new List<string>();

            // v7.0: asset targets — configure ScriptableObjects and other
            // assets directly by "Assets/..." path.
            if (path != null && path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(path);
                if (asset == null) return $"ERROR: asset '{path}' not found.";
                string propsRaw2 = input.Value<string>("properties");
                if (string.IsNullOrEmpty(propsRaw2)) return "ERROR: configure on an asset needs 'properties'.";
                JObject po;
                try { po = JObject.Parse(propsRaw2); }
                catch { return "ERROR: 'properties' must be a JSON object."; }
                string e2 = SetOnComponent(asset, po, notes);
                if (e2 != null) return e2;
                AssetDatabase.SaveAssetIfDirty(asset);
                return $"Configured asset '{path}': {string.Join("; ", notes)}";
            }

            var go = FindByPath(path);
            if (go == null) return $"ERROR: '{path}' not found.";

            string compResult = ApplyComponents(go, input.Value<string>("components"), notes);
            if (compResult != null) return compResult;

            string propsRaw = input.Value<string>("properties");
            if (!string.IsNullOrEmpty(propsRaw))
            {
                string err = ApplyProperties(go, propsRaw, input.Value<string>("component"), notes);
                if (err != null) return err;
            }

            return $"Configured '{GetPath(go.transform)}': {(notes.Count > 0 ? string.Join("; ", notes) : "no changes requested")}";
        }

        private string Wire(JObject input)
        {
            string path = input.Value<string>("path");
            // Asset targets route through Configure (same property machinery).
            if (path != null && path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                return Configure(input);

            var go = FindByPath(path);
            if (go == null) return $"ERROR: '{path}' not found.";
            string propsRaw = input.Value<string>("properties");
            if (string.IsNullOrEmpty(propsRaw)) return "ERROR: wire needs 'properties' (JSON of field: reference).";

            var notes = new List<string>();
            string err = ApplyProperties(go, propsRaw, input.Value<string>("component"), notes);
            if (err != null) return err;
            return $"Wired '{GetPath(go.transform)}': {string.Join("; ", notes)}";
        }

        // ─── v7.0: ScriptableObject assets & UnityEvent wiring ────────

        private string CreateAsset(JObject input)
        {
            string typeName = input.Value<string>("component");
            string path = input.Value<string>("path");
            if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(path))
                return "ERROR: create_asset needs 'component' (ScriptableObject type) and 'path' (Assets/.../Name.asset).";
            if (!path.EndsWith(".asset")) path += ".asset";

            var type = ResolveType(typeName);
            if (type == null) return $"ERROR: type '{typeName}' not found.";
            if (!typeof(ScriptableObject).IsAssignableFrom(type))
                return $"ERROR: '{type.FullName}' is not a ScriptableObject.";

            string dir = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
            if (!AssetDatabase.IsValidFolder(dir))
                return $"ERROR: folder '{dir}' does not exist in the AssetDatabase. Create it first (avoids a costly Refresh).";
            if (AssetDatabase.LoadMainAssetAtPath(path) != null)
                return $"ERROR: an asset already exists at '{path}'.";

            var so = ScriptableObject.CreateInstance(type);
            AssetDatabase.CreateAsset(so, path);

            var notes = new List<string>();
            string propsRaw = input.Value<string>("properties");
            if (!string.IsNullOrEmpty(propsRaw))
            {
                try
                {
                    string err = SetOnComponent(so, JObject.Parse(propsRaw), notes);
                    if (err != null) notes.Add(err);
                }
                catch { notes.Add("properties: invalid JSON, asset created without them"); }
            }
            AssetDatabase.SaveAssetIfDirty(so);

            return $"Created asset '{path}' ({type.Name})" +
                   (notes.Count > 0 ? $": {string.Join("; ", notes)}" : "");
        }

        private string WireEvent(JObject input)
        {
            string path = input.Value<string>("path");
            string compName = input.Value<string>("component");
            string eventName = input.Value<string>("event");
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(eventName))
                return "ERROR: wire_event needs 'path' and 'event' (UnityEvent field name).";

            // Resolve the host: component on a scene object, or an asset (SO).
            UnityEngine.Object host;
            string hostDesc;
            if (path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                host = AssetDatabase.LoadMainAssetAtPath(path);
                hostDesc = path;
                if (host == null) return $"ERROR: asset '{path}' not found.";
            }
            else
            {
                var go = FindByPath(path);
                if (go == null) return $"ERROR: '{path}' not found.";
                if (string.IsNullOrEmpty(compName)) return "ERROR: wire_event on a scene object needs 'component'.";
                var type = ResolveType(compName);
                var comp = type != null ? go.GetComponent(type) : null;
                if (comp == null) return $"ERROR: '{path}' has no {compName}.";
                host = comp;
                hostDesc = $"{GetPath(go.transform)}:{comp.GetType().Name}";
            }

            // Locate the UnityEvent via reflection (field, incl. [SerializeField] private, or property).
            var hostType = host.GetType();
            object evObj = null;
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var field = hostType.GetField(eventName, flags);
            if (field != null) evObj = field.GetValue(host);
            else
            {
                var propInfo = hostType.GetProperty(eventName, flags);
                if (propInfo != null) evObj = propInfo.GetValue(host);
            }
            if (evObj is not UnityEventBase unityEvent)
            {
                var candidates = hostType.GetFields(flags)
                    .Where(f2 => typeof(UnityEventBase).IsAssignableFrom(f2.FieldType))
                    .Select(f2 => f2.Name).Take(12).ToList();
                return $"ERROR: '{eventName}' is not a UnityEvent on {hostType.Name}." +
                       (candidates.Count > 0 ? $" Available: {string.Join(", ", candidates)}" : " No UnityEvent fields found.");
            }

            string listenerSpec = input.Value<string>("listener");

            // Query mode: no listener → LIST current persistent listeners.
            if (string.IsNullOrEmpty(listenerSpec))
            {
                int n = unityEvent.GetPersistentEventCount();
                if (n == 0) return $"'{hostDesc}.{eventName}': 0 persistent listeners.";
                var sb = new StringBuilder($"'{hostDesc}.{eventName}': {n} persistent listener(s):");
                for (int i = 0; i < n; i++)
                {
                    var tgt = unityEvent.GetPersistentTarget(i);
                    sb.Append($"\n  [{i}] {(tgt != null ? tgt.name + " (" + tgt.GetType().Name + ")" : "<missing>")}.{unityEvent.GetPersistentMethodName(i)}");
                }
                return sb.ToString();
            }

            var (listenerObj, lerr) = ResolveReference(listenerSpec);
            if (lerr != null) return $"ERROR: listener — {lerr}";
            string method = input.Value<string>("method");
            if (string.IsNullOrEmpty(method)) return "ERROR: wire_event needs 'method'.";

            Undo.RecordObject(host, "LazyRay: Wire Event");
            string argRaw = input.Value<string>("arg");
            string added;

            try
            {
                var lType = listenerObj.GetType();
                if (string.IsNullOrEmpty(argRaw))
                {
                    var mi = lType.GetMethod(method, Type.EmptyTypes)
                        ?? throw new Exception($"{lType.Name}.{method}() (no-arg) not found");
                    var del = (UnityAction)Delegate.CreateDelegate(typeof(UnityAction), listenerObj, mi);
                    UnityEventTools.AddVoidPersistentListener(unityEvent, del);
                    added = $"{listenerObj.name}.{method}()";
                }
                else if (bool.TryParse(argRaw, out bool bv))
                {
                    var mi = lType.GetMethod(method, new[] { typeof(bool) })
                        ?? throw new Exception($"{lType.Name}.{method}(bool) not found");
                    UnityEventTools.AddBoolPersistentListener(unityEvent,
                        (UnityAction<bool>)Delegate.CreateDelegate(typeof(UnityAction<bool>), listenerObj, mi), bv);
                    added = $"{listenerObj.name}.{method}({bv})";
                }
                else if (int.TryParse(argRaw, out int iv))
                {
                    var mi = lType.GetMethod(method, new[] { typeof(int) })
                        ?? throw new Exception($"{lType.Name}.{method}(int) not found");
                    UnityEventTools.AddIntPersistentListener(unityEvent,
                        (UnityAction<int>)Delegate.CreateDelegate(typeof(UnityAction<int>), listenerObj, mi), iv);
                    added = $"{listenerObj.name}.{method}({iv})";
                }
                else if (float.TryParse(argRaw, System.Globalization.NumberStyles.Float,
                         System.Globalization.CultureInfo.InvariantCulture, out float fv))
                {
                    var mi = lType.GetMethod(method, new[] { typeof(float) })
                        ?? throw new Exception($"{lType.Name}.{method}(float) not found");
                    UnityEventTools.AddFloatPersistentListener(unityEvent,
                        (UnityAction<float>)Delegate.CreateDelegate(typeof(UnityAction<float>), listenerObj, mi), fv);
                    added = $"{listenerObj.name}.{method}({fv})";
                }
                else if (argRaw.StartsWith("scene:") || argRaw.StartsWith("asset:"))
                {
                    var (argObj, aerr) = ResolveReference(argRaw);
                    if (aerr != null) return $"ERROR: arg — {aerr}";
                    var mi = lType.GetMethods(flags)
                        .FirstOrDefault(m => m.Name == method && m.GetParameters().Length == 1 &&
                                             m.GetParameters()[0].ParameterType.IsInstanceOfType(argObj))
                        ?? throw new Exception($"{lType.Name}.{method}({argObj.GetType().Name}) not found");
                    var argType = mi.GetParameters()[0].ParameterType;
                    var del = Delegate.CreateDelegate(typeof(UnityAction<>).MakeGenericType(argType), listenerObj, mi);
                    var adder = typeof(UnityEventTools).GetMethods()
                        .First(m => m.Name == "AddObjectPersistentListener" && m.IsGenericMethod)
                        .MakeGenericMethod(argType);
                    adder.Invoke(null, new object[] { unityEvent, del, argObj });
                    added = $"{listenerObj.name}.{method}({argObj.name})";
                }
                else // string arg
                {
                    var mi = lType.GetMethod(method, new[] { typeof(string) })
                        ?? throw new Exception($"{lType.Name}.{method}(string) not found");
                    UnityEventTools.AddStringPersistentListener(unityEvent,
                        (UnityAction<string>)Delegate.CreateDelegate(typeof(UnityAction<string>), listenerObj, mi), argRaw);
                    added = $"{listenerObj.name}.{method}(\"{argRaw}\")";
                }
            }
            catch (Exception ex)
            {
                return $"ERROR: could not add listener: {ex.Message}";
            }

            EditorUtility.SetDirty(host);
            if (host is Component c2)
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(c2.gameObject.scene);
            else
                AssetDatabase.SaveAssetIfDirty(host);

            return $"Listener added: {added} -> '{hostDesc}.{eventName}' " +
                   $"(persistent #{unityEvent.GetPersistentEventCount() - 1})";
        }

        // ─── Components & properties ───────────────────────────────────

        /// <summary>components: ["BoxCollider"] or {"Rigidbody":{"mass":2}}. Returns error string or null.</summary>
        private string ApplyComponents(GameObject go, string raw, List<string> notes)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            JToken parsed;
            try { parsed = JToken.Parse(raw); }
            catch { return "ERROR: 'components' must be a JSON array or object."; }

            var entries = new List<(string type, JObject props)>();
            if (parsed is JArray ja)
                foreach (var t in ja) entries.Add((t.ToString(), null));
            else if (parsed is JObject jo)
                foreach (var kv in jo) entries.Add((kv.Key, kv.Value as JObject));

            foreach (var (typeName, props) in entries)
            {
                var type = ResolveType(typeName);
                if (type == null) { notes.Add($"type '{typeName}' NOT FOUND"); continue; }

                // Unity gotcha: in the Editor, GetComponent for a missing
                // component returns a FAKE null (overloaded ==, not reference
                // null) — '??' does NOT fall through. Use '==' explicitly.
                var comp = go.GetComponent(type);
                if (comp == null) comp = Undo.AddComponent(go, type);
                if (comp == null) { notes.Add($"{type.Name}: could not add"); continue; }
                notes.Add($"+{type.Name}");

                if (props != null)
                {
                    string err = SetOnComponent(comp, props, notes);
                    if (err != null) notes.Add(err);
                }
            }
            return null;
        }

        /// <summary>properties: {"ComponentType.field": v} or {"field": v} + component param.</summary>
        private string ApplyProperties(GameObject go, string raw, string defaultComponent, List<string> notes)
        {
            JObject props;
            try { props = JObject.Parse(raw); }
            catch { return "ERROR: 'properties' must be a JSON object."; }

            foreach (var kv in props)
            {
                string compName = defaultComponent, field = kv.Key;
                int dot = kv.Key.IndexOf('.');
                if (dot > 0) { compName = kv.Key.Substring(0, dot); field = kv.Key.Substring(dot + 1); }
                if (string.IsNullOrEmpty(compName))
                    return $"ERROR: '{kv.Key}' — prefix with 'ComponentType.' or pass 'component'.";

                var type = ResolveType(compName);
                var comp = type != null ? go.GetComponent(type) : null;
                if (comp == null) { notes.Add($"'{compName}' not on object"); continue; }

                string err = SetOnComponent(comp, new JObject { [field] = kv.Value }, notes);
                if (err != null) notes.Add(err);
            }
            return null;
        }

        private string SetOnComponent(UnityEngine.Object comp, JObject fields, List<string> notes)
        {
            var so = new SerializedObject(comp);
            Undo.RecordObject(comp, $"LazyRay: Configure {comp.GetType().Name}");

            foreach (var kv in fields)
            {
                var prop = so.FindProperty(kv.Key)
                        ?? so.FindProperty("m_" + char.ToUpperInvariant(kv.Key[0]) + kv.Key.Substring(1))
                        ?? so.FindProperty("_" + kv.Key);
                if (prop == null) { notes.Add($"{comp.GetType().Name}.{kv.Key}: field not found"); continue; }

                string err = SetProperty(prop, kv.Value);
                notes.Add(err ?? $"{comp.GetType().Name}.{kv.Key} = {Compact(kv.Value)}");
            }
            so.ApplyModifiedProperties();
            return null;
        }

        private static string Compact(JToken v) =>
            v.ToString(Newtonsoft.Json.Formatting.None).Trim('"');

        private string SetProperty(SerializedProperty prop, JToken value)
        {
            string s = value.Type == JTokenType.String ? value.ToString() : null;
            try
            {
                switch (prop.propertyType)
                {
                    case SerializedPropertyType.Integer: prop.intValue = value.Value<int>(); break;
                    case SerializedPropertyType.Float:   prop.floatValue = value.Value<float>(); break;
                    case SerializedPropertyType.Boolean: prop.boolValue = value.Value<bool>(); break;
                    case SerializedPropertyType.String:  prop.stringValue = value.ToString(); break;
                    case SerializedPropertyType.Enum:
                        if (value.Type == JTokenType.Integer) prop.enumValueIndex = value.Value<int>();
                        else
                        {
                            int idx = Array.FindIndex(prop.enumNames, n => string.Equals(n, s, StringComparison.OrdinalIgnoreCase));
                            if (idx < 0) return $"{prop.name}: enum '{s}' not in [{string.Join(",", prop.enumNames)}]";
                            prop.enumValueIndex = idx;
                        }
                        break;
                    case SerializedPropertyType.Vector3:
                        var v3 = ParseVec3(s); if (!v3.HasValue) return $"{prop.name}: expected [x,y,z]";
                        prop.vector3Value = v3.Value; break;
                    case SerializedPropertyType.Vector2:
                        var arr2 = JArray.Parse(s);
                        prop.vector2Value = new Vector2((float)arr2[0], (float)arr2[1]); break;
                    case SerializedPropertyType.Color:
                        if (!ColorUtility.TryParseHtmlString(s, out var c)) return $"{prop.name}: expected #RRGGBB";
                        prop.colorValue = c; break;
                    case SerializedPropertyType.ObjectReference:
                        var (obj, refErr) = ResolveReference(s);
                        if (refErr != null) return $"{prop.name}: {refErr}";
                        prop.objectReferenceValue = obj; break;
                    default:
                        return $"{prop.name}: unsupported property type {prop.propertyType}";
                }
            }
            catch (Exception ex) { return $"{prop.name}: {ex.Message}"; }
            return null;
        }

        /// <summary>"scene:Path" | "scene:Path:ComponentType" | "asset:Assets/..." | "none"</summary>
        private (UnityEngine.Object, string) ResolveReference(string spec)
        {
            if (string.IsNullOrEmpty(spec) || spec == "none" || spec == "null")
                return (null, null);

            if (spec.StartsWith("asset:", StringComparison.OrdinalIgnoreCase))
            {
                string p = spec.Substring(6);
                var asset = AssetDatabase.LoadMainAssetAtPath(p);
                return asset != null ? (asset, null) : (null, $"asset '{p}' not found");
            }

            if (spec.StartsWith("scene:", StringComparison.OrdinalIgnoreCase))
            {
                string body = spec.Substring(6);
                string compType = null;
                int lastColon = body.LastIndexOf(':');
                if (lastColon > 0) { compType = body.Substring(lastColon + 1); body = body.Substring(0, lastColon); }

                var go = FindByPath(body);
                if (go == null) return (null, $"scene object '{body}' not found");
                if (compType == null) return (go, null);

                var type = ResolveType(compType);
                var comp = type != null ? go.GetComponent(type) : null;
                return comp != null ? ((UnityEngine.Object)comp, null) : (null, $"'{body}' has no {compType}");
            }

            return (null, $"reference must start with 'scene:' or 'asset:' (got '{spec}')");
        }

        private static Vector3? ParseVec3(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            try
            {
                var a = JArray.Parse(raw);
                return new Vector3((float)a[0], (float)a[1], (float)a[2]);
            }
            catch { return null; }
        }
    }
}
