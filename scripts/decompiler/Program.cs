// decompile <assembly> -o <dir> [-r <refdir>]... [-lv <LanguageVersion>] [--without <Transform>]... [--with <Option>]... [--hints <file>] [--units <file>] [--map <file>] [--list]
// decompile <module> -o <file.il> --il
//
// Same output as 'ilspycmd -p --nested-directories' for the same engine version, with one
// addition: --without removes a transform from the decompiler's IL or C# pipeline by type
// name. --list prints both pipelines.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

internal sealed class ProjectDecompiler : WholeProjectDecompiler
{
    private readonly HashSet<string> without;
    public readonly HashSet<string> Removed = new HashSet<string>();

    public ProjectDecompiler(DecompilerSettings settings, UniversalAssemblyResolver resolver, HashSet<string> without)
        : base(settings, resolver, null, resolver, null)
    {
        this.without = without;
    }

    // WholeProjectDecompiler creates its CSharpDecompiler instances itself, with no hook.
    // The base class therefore writes the whole project first; the code files are then
    // decompiled again here with the reduced pipeline, grouped and named the same way.
    public void RewriteCodeFiles(PEFile module, string targetDirectory)
    {
        var metadata = module.Metadata;
        var ts = new DecompilerTypeSystem(module, AssemblyResolver, Settings);
        var unitSettings = new Lazy<DecompilerSettings>(() =>
        {
            var s = Settings.Clone();
            s.SetLanguageVersion(LanguageVersion.CSharp5);
            return s;
        });
        var unitTypes = new Lazy<DecompilerTypeSystem>(() => new DecompilerTypeSystem(module, AssemblyResolver, unitSettings.Value));
        var files = metadata.GetTopLevelTypeDefinitions()
            .Where(td => IncludeTypeWhenDecompilingProject(module, td))
            .GroupBy(h =>
            {
                var type = metadata.GetTypeDefinition(h);
                string file = SanitizeFileName(metadata.GetString(type.Name) + ".cs");
                string ns = metadata.GetString(type.Namespace);
                if (string.IsNullOrEmpty(ns)) return file;
                string dir = Settings.UseNestedDirectoriesForNamespaces ? CleanUpPath(ns) : CleanUpDirectoryName(ns);
                return Path.Combine(dir, file);
            }, StringComparer.OrdinalIgnoreCase).ToList();
        // DECOMP_ONLY=<file name>[;<file name>]: these files alone, for a quick look (debugging).
        string only = Environment.GetEnvironmentVariable("DECOMP_ONLY");
        if (only != null)
        {
            var names = new HashSet<string>(only.Split(';'), StringComparer.OrdinalIgnoreCase);
            files = files.Where(g => names.Contains(Path.GetFileNameWithoutExtension(g.Key))).ToList();
        }
        System.Threading.Tasks.Parallel.ForEach(files, file =>
        {
            string path = Path.Combine(targetDirectory, file.Key);
            if (only != null) Directory.CreateDirectory(Path.GetDirectoryName(path));
            else if (!File.Exists(path)) throw new InvalidOperationException("not written by the project decompiler: " + file.Key);
            // A type of a unit compiled with /debug+ (docs/units.tsv) is read at C# 5: later
            // syntax folds away locals that such a compilation keeps in slots.
            bool keeps = file.Any(h => Program.KeepsLocals(System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(h)));
            var d = keeps ? new CSharpDecompiler(unitTypes.Value, unitSettings.Value) : new CSharpDecompiler(ts, Settings);
            d.AstTransforms.Add(new ICSharpCode.Decompiler.CSharp.Transforms.EscapeInvalidIdentifiers());
            d.AstTransforms.Add(new ICSharpCode.Decompiler.CSharp.Transforms.RemoveCLSCompliantAttribute());
            Program.Strip(d, without, Removed);
            var tree = d.DecompileTypes(file.ToArray());
            using (var w = new StreamWriter(path, false, new System.Text.UTF8Encoding(false)))
                tree.AcceptVisitor(new ICSharpCode.Decompiler.CSharp.OutputVisitor.CSharpOutputVisitor(w, Settings.CSharpFormattingOptions));
        });
    }
}

// DECOMP_DEBUG=<full name>[;<full name>]: prints the tree of these methods once the transforms
// of this project have run (RestoreStatements prints it as they find it).
internal sealed class DebugDump : ICSharpCode.Decompiler.IL.Transforms.IILTransform
{
    public void Run(ICSharpCode.Decompiler.IL.ILFunction function, ICSharpCode.Decompiler.IL.Transforms.ILTransformContext context)
    {
        string debug = Environment.GetEnvironmentVariable("DECOMP_DEBUG");
        if (debug == null) return;
        var functions = function.Descendants.OfType<ICSharpCode.Decompiler.IL.ILFunction>().ToList();
        functions.Add(function);
        foreach (var f in functions)
            if (f.Method != null && debug.Split(';').Contains(f.Method.FullName)) Console.Error.WriteLine("== after == " + f.Method.FullName + " " + f.Body);
    }
}

internal static class Program
{
    // Transforms of this project, enabled by --with <name>.
    internal static readonly HashSet<string> With = new HashSet<string>(StringComparer.Ordinal);

    // Per-method options (--hints): metadata token of a method -> option names.
    internal static readonly Dictionary<int, HashSet<string>> Hints = new Dictionary<int, HashSet<string>>();

    // Compilation units (--units, docs/units.tsv): token of a top-level type -> unit name.
    internal static readonly Dictionary<int, string> Units = new Dictionary<int, string>();

    // True for a top-level type of a unit whose compiler keeps every declared local in a slot:
    // the main compiler with /debug+, and the C# compiler of the .NET Framework (csc20, csc35, csc40).
    internal static bool KeepsLocals(int typeToken)
    {
        string unit;
        return Units.TryGetValue(typeToken, out unit) && (unit == "debugplus" || unit.StartsWith("csc", StringComparison.Ordinal));
    }

    internal static bool KeepsLocals(IMethod method)
    {
        ITypeDefinition type = method == null ? null : method.DeclaringTypeDefinition;
        if (type == null) return false;
        while (type.DeclaringTypeDefinition != null) type = type.DeclaringTypeDefinition;
        return !type.MetadataToken.IsNil && KeepsLocals(System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(type.MetadataToken));
    }

    // Lambda or local function token -> token of the method it is decompiled with (--map).
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> Family = new System.Collections.Concurrent.ConcurrentDictionary<int, int>();

    internal static bool Enabled(string option, System.Reflection.Metadata.EntityHandle method)
    {
        HashSet<string> set = null;
        if (!method.IsNil) Hints.TryGetValue(System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(method), out set);
        // "-Option" in the hints: never for this method, even in a trial with --with.
        if (set != null && set.Contains("-" + option)) return false;
        return With.Contains(option) || (set != null && set.Contains(option));
    }

    // Runs a stock IL transform unless the method being decompiled has the option.
    private sealed class Unless : ICSharpCode.Decompiler.IL.Transforms.IILTransform
    {
        private readonly ICSharpCode.Decompiler.IL.Transforms.IILTransform inner;
        private readonly string option;
        public Unless(ICSharpCode.Decompiler.IL.Transforms.IILTransform inner, string option) { this.inner = inner; this.option = option; }
        public string InnerName { get { return inner.GetType().Name; } }
        public void Run(ICSharpCode.Decompiler.IL.ILFunction function, ICSharpCode.Decompiler.IL.Transforms.ILTransformContext context)
        {
            if (function.Method != null && Enabled(option, function.Method.MetadataToken)) return;
            // A chain of 'if' on a string is no 'switch': the IL tells them apart (RestoreShapes).
            if (option == "NoSwitchOnStringTransform" && function.Method != null && RestoreShapes.NoStringSwitch(function, context)) return;
            inner.Run(function, context);
        }
    }

    private static string NameOf(ICSharpCode.Decompiler.IL.Transforms.IILTransform t)
    {
        var u = t as Unless;
        return u != null ? u.InnerName : t.GetType().Name;
    }

    internal static void Strip(CSharpDecompiler d, HashSet<string> without, HashSet<string> removed)
    {
        // Options that switch a stock transform off for one method: "No" + its name.
        for (int i = 0; i < d.ILTransforms.Count; i++)
        {
            string n = d.ILTransforms[i].GetType().Name;
            if (n != "TransformDisplayClassUsage" && n != "ILInlining" && n != "AssignVariableNames") d.ILTransforms[i] = new Unless(d.ILTransforms[i], "No" + n);
            // Same transform, which also notes where each closure is created (RestoreCaptureScope).
            if (n == "TransformDisplayClassUsage") d.ILTransforms[i] = new TrackClosures();
        }
        for (int i = 0; i < d.AstTransforms.Count; i++)
            if (d.AstTransforms[i].GetType().Name == "DeclareVariables")
            {
                d.AstTransforms.Insert(i + 1, new HoistCapturedDeclarations());
                if (With.Contains("RestoreCaptureScope")) d.AstTransforms.Insert(i + 1, new RestoreClosures());
                if (With.Contains("RestoreLambdaLocals")) d.AstTransforms.Insert(i + 1, new RestoreLambdaLocals());
                break;
            }
        for (int i = 0; i < d.AstTransforms.Count; i++)
            if (d.AstTransforms[i].GetType().Name == "IntroduceUsingDeclarations") { d.AstTransforms.Insert(i, new RestoreSyntax()); d.AstTransforms.Insert(i + 1, new RestoreShapesSyntax()); break; }
        for (int i = 0; i < d.AstTransforms.Count; i++)
            if (d.AstTransforms[i].GetType().Name == "IntroduceUsingDeclarations") { d.AstTransforms.Insert(i, new RestoreLayoutSyntax()); break; }
        for (int i = 0; i < d.AstTransforms.Count; i++)
            if (d.AstTransforms[i].GetType().Name == "IntroduceUsingDeclarations") { d.AstTransforms.Insert(i, new RestoreTrailingReturn()); d.AstTransforms.Insert(i, new RestoreFormsSyntax()); break; }
        for (int i = 0; i < d.AstTransforms.Count; i++)
            if (d.AstTransforms[i].GetType().Name == "PrettifyAssignments") { if (With.Contains("RestoreCaptureScope")) d.AstTransforms.Insert(i, new RestoreMembers()); break; }
        for (int i = 0; i < d.AstTransforms.Count; i++)
            if (d.AstTransforms[i].GetType().Name == "IntroduceUsingDeclarations") { if (With.Contains("RestoreCaptureScope")) d.AstTransforms.Insert(i, new RestoreCapturedSteps()); break; }
        for (int i = 0; i < d.ILTransforms.Count; i++)
            if (NameOf(d.ILTransforms[i]) == "ILInlining") { d.ILTransforms.Insert(i + 1, new RestoreEarlyTemps()); break; }
        if (With.Contains("RestoreEarlyReturn"))
        {
            // After the nesting heuristics, before the trailing 'return;' is dropped.
            int at = d.ILTransforms.Count;
            for (int i = 0; i < d.ILTransforms.Count; i++)
                if (NameOf(d.ILTransforms[i]) == "RemoveRedundantReturn") { at = i; break; }
            d.ILTransforms.Insert(at, new RestoreEarlyReturn());
        }
        {
            // Per method or with --with: Enabled() decides inside.
            int at = d.ILTransforms.Count;
            for (int i = 0; i < d.ILTransforms.Count; i++)
                if (NameOf(d.ILTransforms[i]) == "RemoveRedundantReturn") { at = i; break; }
            d.ILTransforms.Insert(at, new DebugDump());   // runs after the others
            d.ILTransforms.Insert(at, new RestoreShapes());
            d.ILTransforms.Insert(at, new RestoreBranches());
            d.ILTransforms.Insert(at, new RestoreConditional());
            d.ILTransforms.Insert(at, new RestoreCallVirt());
            d.ILTransforms.Insert(at, new RestoreExpressions());
            d.ILTransforms.Insert(at, new RestoreOrder());
            d.ILTransforms.Insert(at, new RestoreForms());   // second parallel series, lot w1: after RestoreLoops
            d.ILTransforms.Insert(at, new RestoreLoops());
            d.ILTransforms.Insert(at, new RestoreStatements());
            d.ILTransforms.Insert(at, new RestoreLocals());
            d.ILTransforms.Insert(at, new RestoreShapesEarly());   // runs before the others
            d.ILTransforms.Insert(at, new RestoreLayout());        // and this one before it
        }
        if (With.Contains("RestoreFromIL"))
        {
            int at = d.ILTransforms.Count;
            for (int i = 0; i < d.ILTransforms.Count; i++)
                if (NameOf(d.ILTransforms[i]) == "RemoveRedundantReturn") { at = i; break; }
            d.ILTransforms.Insert(at, new RestoreFromIL());
            d.AstTransforms.Add(new NegationToUnaryMinus());
        }
        d.AstTransforms.Add(new RestoreShapesLast());
        d.AstTransforms.Add(new RestoreSwitchLayout());
        d.AstTransforms.Add(new RestoreUnitSyntax());
        for (int i = d.ILTransforms.Count - 1; i >= 0; i--)
        {
            string n = NameOf(d.ILTransforms[i]);
            if (without.Contains(n)) { d.ILTransforms.RemoveAt(i); lock (removed) removed.Add(n); }
        }
        for (int i = d.AstTransforms.Count - 1; i >= 0; i--)
        {
            string n = d.AstTransforms[i].GetType().Name;
            if (without.Contains(n)) { d.AstTransforms.RemoveAt(i); lock (removed) removed.Add(n); }
        }
    }

    private static int Main(string[] args)
    {
        string input = null, output = null;
        var refs = new List<string>();
        var without = new HashSet<string>(StringComparer.Ordinal);
        var lv = LanguageVersion.Latest;
        bool list = false, il = false, ilHash = false;
        string mapPath = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o": output = args[++i]; break;
                case "-r": refs.Add(args[++i]); break;
                case "-lv": lv = (LanguageVersion)Enum.Parse(typeof(LanguageVersion), args[++i]); break;
                case "--without": without.Add(args[++i]); break;
                case "--with": With.Add(args[++i]); break;
                case "--hints":
                    foreach (string line in File.ReadAllLines(args[++i]))
                    {
                        string[] c = line.Split('	');
                        if (c.Length < 2 || !c[0].StartsWith("0x")) continue;
                        int token = Convert.ToInt32(c[0].Substring(2), 16);
                        HashSet<string> set;
                        if (!Hints.TryGetValue(token, out set)) { set = new HashSet<string>(StringComparer.Ordinal); Hints[token] = set; }
                        foreach (string o in c[1].Split(',')) if (o.Trim().Length > 0) set.Add(o.Trim());
                    }
                    break;
                case "--units":
                    foreach (string line in File.ReadAllLines(args[++i]))
                    {
                        string[] c = line.Split('	');
                        if (c.Length < 2 || !c[0].StartsWith("0x02")) continue;
                        Units[Convert.ToInt32(c[0].Substring(2), 16)] = c[1].Trim();
                    }
                    break;
                case "--map": mapPath = args[++i]; break;
                case "--list": list = true; break;
                case "--il": il = true; break;
                case "--il-hash": ilHash = true; break;
                default: input = args[i]; break;
            }
        }
        if (list)
        {
            Console.WriteLine("IL transforms:");
            foreach (var t in CSharpDecompiler.GetILTransforms()) Console.WriteLine("  " + t.GetType().Name);
            Console.WriteLine("C# transforms:");
            foreach (var t in CSharpDecompiler.GetAstTransforms()) Console.WriteLine("  " + t.GetType().Name);
            return 0;
        }
        if (input == null || output == null)
        {
            Console.Error.WriteLine("usage: decompile <assembly> -o <dir> [-r <refdir>] [-lv <version>] [--without <Transform>] [--list]");
            return 2;
        }
        input = Path.GetFullPath(input);
        output = Path.GetFullPath(output);
        if (ilHash)
        {
            // decompile <module> -o <file.tsv> --il-hash [-r <refdir>]: one line per method with
            // a body: token, name, hash of its body as this engine's disassembler prints it
            // (scripts/dev/outside-check.ps1). Left out of the text: where the body is in the
            // file, the size of its header and '.maxstack', which scripts/match.ps1 leaves out too.
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            using (var unit = new PEFile(input))
            using (var w = new StreamWriter(output, false, new System.Text.UTF8Encoding(false)))
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                var r2 = new UniversalAssemblyResolver(input, false, unit.Metadata.DetectTargetFrameworkId());
                foreach (string r in refs) r2.AddSearchDirectory(Path.GetFullPath(r));
                var types = new DecompilerTypeSystem(unit, r2, new DecompilerSettings(LanguageVersion.CSharp7_3) { ThrowOnAssemblyResolveErrors = false });
                var metadata = unit.Metadata;
                w.WriteLine("token\tname\tlines\thash\tmasked");
                var selfScope = new System.Text.RegularExpressions.Regex(@"(?<=<PrivateImplementationDetails>)\{[0-9A-Fa-f]+\}|\['" + System.Text.RegularExpressions.Regex.Escape(unit.Metadata.GetString(unit.Metadata.GetModuleDefinition().Name)) + @"'\]");
                var closureField = new System.Text.RegularExpressions.Regex(@"(?<=<>c__DisplayClass#'::)[^\s(]+(?=\s|$)");
                // Second hash: the same text with the ordinals of compiler-generated names
                // masked ('<M>b__12_3', '<>c__DisplayClass12_0', the data fields of
                // <PrivateImplementationDetails>, named after a hash the obfuscator replaced).
                // Those numbers follow the order of the members in the compiled source.
                var generated = new System.Text.RegularExpressions.Regex(
                    @"(?<=<>c__DisplayClass)\d+_\d+|(?<=>b__)\d+(_\d+)?|(?<=<>9__)\d+(_\d+)?|(?<=>d__)\d+|(?<=>g__\w+\|)\d+(_\d+)?|(?<=CS\$<>8__locals)\d+|(?<=<>m__Finally)\d+|(?<=<PrivateImplementationDetails>)(\{[0-9A-Fa-f]+\})?'?(/'?__StaticArrayInitTypeSize=\d+'?)?::\S+");
                foreach (var handle in metadata.MethodDefinitions)
                {
                    var definition = metadata.GetMethodDefinition(handle);
                    if (definition.RelativeVirtualAddress == 0) continue;
                    var method = types.MainModule.GetDefinition(handle);
                    var buffer = new StringWriter();
                    var disassembler = new ICSharpCode.Decompiler.Disassembler.MethodBodyDisassembler(new PlainTextOutput(buffer), System.Threading.CancellationToken.None);
                    disassembler.DetectControlStructure = false;
                    disassembler.Disassemble(unit, handle);
                    var kept = buffer.ToString().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0
                        && !l.StartsWith("// Method begins at RVA") && !l.StartsWith("// Header size") && !l.StartsWith(".maxstack")).ToList();
                    string name = method.DeclaringType.ReflectionName + "::" + method.Name + "`" + method.TypeParameters.Count
                        + "(" + string.Join(",", method.Parameters.Select(p => p.Type.ReflectionName)) + "):" + method.ReturnType.ReflectionName;
                    string hash = BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", kept)))).Replace("-", "");
                    string maskedText = generated.Replace(string.Join("\n", kept), "#");
                    // Three more names that say nothing of the code: the {token} the names stage
                    // adds to the <PrivateImplementationDetails> of a merged assembly, a reference
                    // of the module to itself written with its file name (the obfuscator left
                    // some), and the fields of a closure, named after captured variables.
                    maskedText = selfScope.Replace(maskedText, "");
                    maskedText = closureField.Replace(maskedText, "#");
                    string masked = BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(maskedText))).Replace("-", "");
                    // IL_TEXT=<file>: the hashed text of every method, to read a difference.
                    string textPath = Environment.GetEnvironmentVariable("IL_TEXT");
                    if (textPath != null) File.AppendAllText(textPath, "== " + name + "\n" + maskedText + "\n");
                    w.WriteLine("0x" + System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(handle).ToString("X8") + "\t" + name + "\t" + kept.Count + "\t" + hash + "\t" + masked);
                }
            }
            return 0;
        }
        if (il)
        {
            // decompile <module> -o <file.il> --il: the whole module as IL source that ilasm
            // assembles (the module of the unit 'il', scripts/make-il-unit.ps1).
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            using (var unit = new PEFile(input))
            using (var w = new StreamWriter(output, false, new System.Text.UTF8Encoding(false)))
            {
                var text = new PlainTextOutput(w);
                var disassembler = new ICSharpCode.Decompiler.Disassembler.ReflectionDisassembler(text, System.Threading.CancellationToken.None);
                // Protected blocks as a table at the end of each body, in the order of the method's
                // own table: nested '.try' blocks would let ilasm order it.
                disassembler.DetectControlStructure = false;
                disassembler.WriteAssemblyReferences(unit.Metadata);
                if (unit.Metadata.IsAssembly) disassembler.WriteAssemblyHeader(unit);
                text.WriteLine();
                disassembler.WriteModuleHeader(unit);
                text.WriteLine();
                disassembler.WriteModuleContents(unit);
            }
            return 0;
        }
        Directory.CreateDirectory(output);

        var module = new PEFile(input);
        var resolver = new UniversalAssemblyResolver(input, false, module.Metadata.DetectTargetFrameworkId());
        foreach (string r in refs) resolver.AddSearchDirectory(Path.GetFullPath(r));
        var settings = new DecompilerSettings(lv)
        {
            ThrowOnAssemblyResolveErrors = false,
            RemoveDeadCode = false,
            RemoveDeadStores = false,
            // '$"{a}: {b}"' compiles to string.Concat when every hole is a string: a call to
            // string.Format in the IL was written as one.
            StringInterpolation = !With.Contains("NoStringInterpolation"),
            // A range variable of a query is read-only: calling a method on a struct copies it
            // first. 'from s in list select s.M()' is not 'list.Select(s => s.M())'.
            QueryExpressions = !With.Contains("NoQueryExpressions"),
            UseSdkStyleProjectFormat = WholeProjectDecompiler.CanUseSdkStyleProjectFormat(module),
            UseNestedDirectoriesForNamespaces = true,
        };
        var decompiler = new ProjectDecompiler(settings, resolver, without);
        bool quick = Environment.GetEnvironmentVariable("DECOMP_ONLY") != null;
        if (!quick) decompiler.DecompileProject(module, output);
        if (quick || without.Count > 0 || With.Count > 0 || Hints.Count > 0 || Units.Count > 0) decompiler.RewriteCodeFiles(module, output);
        if (mapPath != null)
        {
            var lines = new List<string> { "token	owner" };
            foreach (var kv in Family.OrderBy(k => k.Key)) lines.Add("0x" + kv.Key.ToString("X8") + "	0x" + kv.Value.ToString("X8"));
            File.WriteAllLines(mapPath, lines);
        }
        foreach (string w in without.Where(w => !decompiler.Removed.Contains(w)))
        {
            Console.Error.WriteLine("no such transform: " + w);
            return 1;
        }
        return 0;
    }
}
