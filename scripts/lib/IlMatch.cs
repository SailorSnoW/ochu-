// Per-method IL comparison between a reference assembly and a candidate build.
// Compiled by scripts/match.ps1 with Add-Type (Windows PowerShell 5.1: C# 5 only).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

// Compiler-generated names carry ordinals (<M>b__12_3, <>c__DisplayClass12_0) that depend
// on the member order of the compiled source and on the compiler's own traversal: the
// names stage cannot predict them. Both sides are therefore renamed to a canonical form
// before comparison: ordinals become ranks among the members generated for the same
// method, closure and iterator fields become their index. Two bodies still have to be
// identical to match; only the pairing ignores the ordinals.
public static class Canonical
{
    static readonly Regex Lambda = new Regex(@"^<(.*)>b__(\d+)(?:_(\d+))?$", RegexOptions.CultureInvariant);
    static readonly Regex LocalFn = new Regex(@"^<(.*)>g__(.*)\|(\d+)(?:_(\d+))?$", RegexOptions.CultureInvariant);
    static readonly Regex Display = new Regex(@"^<>c__DisplayClass(\d+)_(\d+)$", RegexOptions.CultureInvariant);
    static readonly Regex StaticN = new Regex(@"^<>c__(\d+)$", RegexOptions.CultureInvariant);
    static readonly Regex Iter = new Regex(@"^<(.*)>d__(\d+)$", RegexOptions.CultureInvariant);
    static readonly Regex Cache = new Regex(@"^<>9__(\d+)_(\d+)$", RegexOptions.CultureInvariant);

    sealed class Item { public uint Token; public long Order; public string Format; }

    // Which order the generated members of one method are ranked in.
    //
    // The ordinal in a generated name is not in the reference, and the order of its types is
    // no substitute: the obfuscator sorts types, nested ones included, by their new name
    // (all 673 adjacent pairs of obfuscated nested types are in ordinal order). It leaves
    // methods and fields where they were (half of the adjacent pairs in order).
    //
    //   Closure classes are ranked by where they are created: the path of instruction
    //   indices from the user-written method down to the 'newobj', through the 'ldftn' of
    //   the lambdas on the way. Both sides read their own code.
    //   Lambdas and local functions are ranked by their position in their type. The compiler
    //   emits them in source order, which is not the order of the ordinals it names them
    //   with (those follow its scope tree: the lambdas of a block before those of the blocks
    //   inside it).
    sealed class Sites
    {
        struct Site { public MethodDef Method; public int Index; }
        readonly Dictionary<MethodDef, List<Site>> uses = new Dictionary<MethodDef, List<Site>>();
        readonly Dictionary<TypeDef, List<Site>> created = new Dictionary<TypeDef, List<Site>>();
        readonly Dictionary<MethodDef, int[]> paths = new Dictionary<MethodDef, int[]>();
        readonly Dictionary<TypeDef, long> order = new Dictionary<TypeDef, long>();
        readonly TypeDef owner;
        static readonly int[] Unknown = new int[] { int.MaxValue };

        static bool Generated(MethodDef m, TypeDef owner)
        {
            return m.DeclaringType != owner || m.Name.String.StartsWith("<", StringComparison.Ordinal);
        }

        public Sites(TypeDef owner, List<TypeDef> scopes)
        {
            this.owner = owner;
            if (scopes.Count < 2) return;
            var family = new HashSet<TypeDef>(scopes);
            foreach (var t in scopes)
            {
                foreach (var m in t.Methods)
                {
                    if (!m.HasBody) continue;
                    var ins = m.Body.Instructions;
                    HashSet<TypeDef> structs = null;
                    foreach (var v in m.Body.Variables)
                    {
                        TypeDef vt = v.Type == null ? null : v.Type.ScopeType as TypeDef;
                        if (vt != null && vt.IsValueType && vt != owner && family.Contains(vt)) { if (structs == null) structs = new HashSet<TypeDef>(); structs.Add(vt); }
                    }
                    for (int i = 0; i < ins.Count; i++)
                    {
                        object op = ins[i].Operand;
                        var site = new Site { Method = m, Index = i };
                        var im = op as IMethod;
                        if (im != null && im.IsMethod)
                        {
                            MethodDef target = im as MethodDef;
                            if (target == null && im.DeclaringType != null && family.Contains(im.DeclaringType.ScopeType as TypeDef)) target = im.ResolveMethodDef();
                            if (target == null || target == m || !family.Contains(target.DeclaringType)) continue;
                            if (ins[i].OpCode.Code == Code.Newobj && target.DeclaringType != owner) AddSite(created, target.DeclaringType, site);
                            else if (!target.IsConstructor && Generated(target, owner)) AddSite(uses, target, site);
                            continue;
                        }
                        var fd = op as IField;
                        if (fd != null && structs != null && fd.DeclaringType != null)
                        {
                            TypeDef ft = fd.DeclaringType.ScopeType as TypeDef;
                            if (ft != null && structs.Contains(ft)) AddSite(created, ft, site);
                        }
                    }
                }
            }
            var keys = new List<KeyValuePair<TypeDef, int[]>>();
            foreach (var kv in created) keys.Add(new KeyValuePair<TypeDef, int[]>(kv.Key, Best(kv.Value, 0)));
            keys.Sort(delegate (KeyValuePair<TypeDef, int[]> a, KeyValuePair<TypeDef, int[]> b)
            {
                int c = Compare(a.Value, b.Value);
                return c != 0 ? c : a.Key.MDToken.Raw.CompareTo(b.Key.MDToken.Raw);
            });
            for (int i = 0; i < keys.Count; i++) order[keys[i].Key] = i;
        }

        static void AddSite<T>(Dictionary<T, List<Site>> d, T key, Site s)
        {
            List<Site> l;
            if (!d.TryGetValue(key, out l)) { l = new List<Site>(); d[key] = l; }
            l.Add(s);
        }

        static int Compare(int[] a, int[] b)
        {
            for (int i = 0; i < a.Length && i < b.Length; i++) if (a[i] != b[i]) return a[i].CompareTo(b[i]);
            return a.Length.CompareTo(b.Length);
        }

        int[] Best(List<Site> sites, int depth)
        {
            int[] best = Unknown;
            foreach (var s in sites)
            {
                int[] p = Path(s.Method, depth + 1);
                var key = new int[p.Length + 1];
                Array.Copy(p, key, p.Length);
                key[p.Length] = s.Index;
                if (Compare(key, best) < 0) best = key;
            }
            return best;
        }

        // From the user-written method to this one: where each lambda on the way is taken.
        int[] Path(MethodDef m, int depth)
        {
            // Overloads share a name: the user-written method comes first, by position.
            if (!Generated(m, owner)) return new int[] { (int)m.MDToken.Rid };
            int[] p;
            if (paths.TryGetValue(m, out p)) return p;
            paths[m] = Unknown;
            List<Site> sites;
            p = depth > 16 || !uses.TryGetValue(m, out sites) ? Unknown : Best(sites, depth);
            paths[m] = p;
            return p;
        }

        // Closures nobody creates in the family come last, in their own order.
        public long Order(TypeDef closure)
        {
            long o;
            return order.TryGetValue(closure, out o) ? o : 1000000L + closure.MDToken.Rid;
        }
    }

    static string Base(string name, out string arity)
    {
        int tick = name.IndexOf('`');
        arity = tick < 0 ? "" : name.Substring(tick);
        return tick < 0 ? name : name.Substring(0, tick);
    }

    static void Add(Dictionary<string, List<Item>> groups, string key, uint token, long order, string format)
    {
        List<Item> l;
        if (!groups.TryGetValue(key, out l)) { l = new List<Item>(); groups[key] = l; }
        l.Add(new Item { Token = token, Order = order, Format = format });
    }

    static void Rank(Dictionary<string, List<Item>> groups, Dictionary<uint, string> canon)
    {
        foreach (var l in groups.Values)
        {
            l.Sort(delegate (Item a, Item b) { int c = a.Order.CompareTo(b.Order); return c != 0 ? c : a.Token.CompareTo(b.Token); });
            for (int i = 0; i < l.Count; i++) canon[l[i].Token] = l[i].Format.Replace("{r}", i.ToString(CultureInfo.InvariantCulture));
        }
    }

    static int Int(Group g) { return int.Parse(g.Value, CultureInfo.InvariantCulture); }

    public static Dictionary<uint, string> Build(ModuleDef mod)
    {
        var canon = new Dictionary<uint, string>();
        foreach (var owner in mod.GetTypes())
        {
            string arity;
            if (owner.DeclaringType != null && Base(owner.Name, out arity).StartsWith("<", StringComparison.Ordinal)) continue;
            var scopes = new List<TypeDef>();
            scopes.Add(owner);
            foreach (var n in owner.NestedTypes) if (Base(n.Name, out arity).StartsWith("<", StringComparison.Ordinal)) scopes.Add(n);

            // Method ordinal -> method name, read from the generated names themselves.
            var moName = new Dictionary<int, string>();
            foreach (var s in scopes)
            {
                Match d = s == owner ? Match.Empty : Display.Match(Base(s.Name, out arity));
                foreach (var m in s.Methods)
                {
                    Match x = Lambda.Match(m.Name);
                    string stem = null; Group ordinal = null;
                    if (x.Success) { stem = x.Groups[1].Value; if (x.Groups[3].Success) ordinal = x.Groups[2]; }
                    else
                    {
                        x = LocalFn.Match(m.Name);
                        if (x.Success) { stem = x.Groups[1].Value; if (x.Groups[4].Success) ordinal = x.Groups[3]; }
                    }
                    if (stem == null) continue;
                    if (ordinal != null) moName[Int(ordinal)] = stem;
                    else if (d.Success) moName[Int(d.Groups[1])] = stem;
                }
            }

            var groups = new Dictionary<string, List<Item>>(StringComparer.Ordinal);
            var sites = new Sites(owner, scopes);
            foreach (var s in scopes)
            {
                if (s == owner) continue;
                string b = Base(s.Name, out arity);
                Match x = Display.Match(b);
                if (x.Success)
                {
                    int mo = Int(x.Groups[1]);
                    string stem;
                    if (!moName.TryGetValue(mo, out stem)) stem = "?" + mo;
                    Add(groups, "D|" + stem, s.MDToken.Raw, sites.Order(s), "<>c__DisplayClass[" + stem + "#{r}]" + arity);
                    continue;
                }
                x = StaticN.Match(b);
                if (x.Success)
                {
                    int mo = Int(x.Groups[1]);
                    string stem;
                    if (!moName.TryGetValue(mo, out stem)) stem = "?" + mo;
                    Add(groups, "S|" + stem, s.MDToken.Raw, mo, "<>c__[" + stem + "#{r}]" + arity);
                    continue;
                }
                x = Iter.Match(b);
                if (x.Success) Add(groups, "I|" + x.Groups[1].Value, s.MDToken.Raw, Int(x.Groups[2]), "<" + x.Groups[1].Value + ">d__#{r}" + arity);
            }
            foreach (var s in scopes)
            {
                foreach (var m in s.Methods)
                {
                    Match x = Lambda.Match(m.Name);
                    if (x.Success)
                    {
                        // Ranked by position in the type: see the note on Sites.
                        Add(groups, "L|" + s.MDToken.Raw + "|" + x.Groups[1].Value, m.MDToken.Raw, m.MDToken.Rid, "<" + x.Groups[1].Value + ">b__#{r}");
                        continue;
                    }
                    x = LocalFn.Match(m.Name);
                    if (x.Success)
                        Add(groups, "G|" + s.MDToken.Raw + "|" + x.Groups[1].Value + "|" + x.Groups[2].Value, m.MDToken.Raw, m.MDToken.Rid, "<" + x.Groups[1].Value + ">g__" + x.Groups[2].Value + "#{r}");
                }
            }
            Rank(groups, canon);

            foreach (var s in scopes)
            {
                if (s == owner) continue;
                string b = Base(s.Name, out arity);
                if (b == "<>c" || StaticN.IsMatch(b))
                {
                    foreach (var f in s.Fields)
                    {
                        Match x = Cache.Match(f.Name);
                        if (!x.Success) continue;
                        string tail = ">b__" + x.Groups[1].Value + "_" + x.Groups[2].Value;
                        foreach (var m in s.Methods)
                        {
                            string c;
                            if (m.Name.String.EndsWith(tail, StringComparison.Ordinal) && canon.TryGetValue(m.MDToken.Raw, out c)) { canon[f.MDToken.Raw] = "<>9__" + c; break; }
                        }
                    }
                }
                else if (Display.IsMatch(b) || Iter.IsMatch(b))
                {
                    for (int i = 0; i < s.Fields.Count; i++) canon[s.Fields[i].MDToken.Raw] = "$" + i;
                }
            }
        }
        return canon;
    }
}

public sealed class MethodResult
{
    public uint Token;        // reference token
    public string Status;     // match | diff | missing
    public double Score;      // 1.0 = identical, LCS ratio otherwise
    public string Category;   // osu | global | lib | obf
    public int Size;          // reference instruction count
    public string Name;       // reference method, rendered with the names module
    public string Note;       // header differences that do not affect Status
    public string Cause;      // diff only: coarse class of the difference, see IlMatch.Cause
    public string Shape;      // diff only: "ret R/C br R/C loc R/C" counts, reference/candidate
    public string Unit = "";  // compilation unit the candidate method comes from ("" = the main build)
}

// A compilation unit other than the main build (docs/units.tsv): a module built from the same
// source by another compiler or with other options, or assembled from IL. Types lists the
// top-level types it gives whole; null means every method the module defines replaces the
// method of the same name in the main build.
public sealed class UnitModule
{
    public string Name;
    public byte[] Data;
    public HashSet<string> Types;
}

// Renders types, methods and fields as text. When a names module is given, every
// TypeDef / MethodDef / FieldDef of the rendered module is looked up by token in the names
// module, so that IL taken from one pipeline stage reads with the names of another.
// Generic parameters are positional (!0, !!0): their names never matter.
public sealed class Namer
{
    readonly ModuleDefMD names;
    readonly Dictionary<uint, string> cache = new Dictionary<uint, string>();

    readonly ModuleDefMD self;
    HashSet<string> classRefs, valueRefs;

    void CollectSig(TypeSig s)
    {
        while (s != null)
        {
            var gi = s as GenericInstSig;
            if (gi != null) { CollectSig(gi.GenericType); foreach (var a in gi.GenericArguments) CollectSig(a); return; }
            var tdr = s as TypeDefOrRefSig;
            if (tdr != null)
            {
                if (tdr.TypeDefOrRef is TypeRef) (s.ElementType == ElementType.ValueType ? valueRefs : classRefs).Add(tdr.TypeDefOrRef.FullName);
                return;
            }
            s = s.Next;
        }
    }

    // True when t is known to be a reference type. A type of another assembly is known by
    // the signatures of this module that mention it as class or as valuetype.
    public bool IsReference(ITypeDefOrRef t)
    {
        if (t == null) return false;
        var td = (t as TypeDef) ?? Internal(t as TypeRef) ?? HostType(t);
        if (td != null) return !td.IsValueType;
        var ts = t as TypeSpec;
        if (ts != null)
        {
            TypeSig sig = ts.TypeSig;
            if (sig is SZArraySig || sig is ArraySig) return true;
            var gi = sig as GenericInstSig;
            if (gi != null && gi.GenericType != null) return gi.GenericType.ElementType == ElementType.Class;
            return false;
        }
        if (classRefs == null)
        {
            classRefs = new HashSet<string>(StringComparer.Ordinal); valueRefs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var type in self.GetTypes())
            {
                foreach (var f in type.Fields) CollectSig(f.FieldSig.Type);
                foreach (var m in type.Methods)
                {
                    CollectSig(m.MethodSig.RetType);
                    foreach (var p in m.MethodSig.Params) CollectSig(p);
                    if (m.HasBody) foreach (var v in m.Body.Variables) CollectSig(v.Type);
                }
            }
            foreach (var mr in self.GetMemberRefs())
            {
                if (mr.FieldSig != null) CollectSig(mr.FieldSig.Type);
                if (mr.MethodSig != null) { CollectSig(mr.MethodSig.RetType); foreach (var p in mr.MethodSig.Params) CollectSig(p); }
            }
        }
        string name = t.FullName;
        return classRefs.Contains(name) && !valueRefs.Contains(name);
    }

    // For the module of a unit: the namer of the main build. A reference of the unit to the
    // assembly of the main build is rendered as the main build renders its definition
    // (canonical names of generated members, explicit implementations).
    public Namer Host;

    TypeDef HostType(ITypeDefOrRef t)
    {
        var tr = t as TypeRef;
        if (Host == null || tr == null || Host.self.Assembly == null) return null;
        IResolutionScope scope = tr.ResolutionScope;
        while (scope is TypeRef) scope = ((TypeRef)scope).ResolutionScope;
        var ar = scope as AssemblyRef;
        if (ar == null || ar.Name != Host.self.Assembly.Name) return null;
        return Host.self.Find(tr);
    }

    TypeDef HostOwner(MemberRef mr)
    {
        var t = mr.Class as ITypeDefOrRef;
        return t == null ? null : HostType(t.ScopeType);
    }

    // Token -> canonical name of compiler-generated types, methods and fields.
    readonly Dictionary<uint, string> canon;

    // self: the module being rendered. names: the module that carries its names, or null.
    public Namer(ModuleDefMD names, ModuleDefMD self)
    {
        this.names = names;
        this.self = self;
        canon = Canonical.Build(names ?? self);
    }

    public TypeDef NamedType(TypeDef td)
    {
        if (names == null) return td;
        var n = names.ResolveToken(td.MDToken.Raw) as TypeDef;
        return n ?? td;
    }

    string DefName(TypeDef td)
    {
        string s;
        if (cache.TryGetValue(td.MDToken.Raw, out s)) return s;
        TypeDef named = NamedType(td);
        string generated;
        if (td.DeclaringType != null && canon.TryGetValue(td.MDToken.Raw, out generated))
            s = DefName(td.DeclaringType) + "/" + generated;
        else s = named.FullName;
        // Merged assemblies each had their own <PrivateImplementationDetails>; the names
        // stage tells them apart with a {token} suffix. A recompilation has only one.
        const string pid = "<PrivateImplementationDetails>";
        if (s.StartsWith(pid + "{", StringComparison.Ordinal))
        {
            int close = s.IndexOf('}');
            if (close > 0) s = pid + s.Substring(close + 1);
        }
        cache[td.MDToken.Raw] = s;
        return s;
    }

    string MethodName(MethodDef md)
    {
        string c;
        if (canon.TryGetValue(md.MDToken.Raw, out c)) return c;
        // An explicit interface implementation is named after the interface as the compiler
        // spelled it ("System.Collections.Generic.IEnumerator<T>.get_Current"): that text
        // does not follow renames. Name it by what it implements instead.
        if (md.IsPrivate && md.HasOverrides)
        {
            IMethod decl = md.Overrides[0].MethodDeclaration;
            string declName = decl.Name;
            var dd = decl as MethodDef;
            var dr = decl as MemberRef;
            if (dd != null) declName = PlainMethodName(dd);
            else if (dr != null)
            {
                var owner = LocalOwner(dr);
                var def = owner == null ? null : owner.FindMethod(dr.Name, dr.MethodSig);
                if (def != null) declName = PlainMethodName(def);
            }
            return "[" + Type(decl.DeclaringType) + "]." + declName;
        }
        return PlainMethodName(md);
    }

    string PlainMethodName(MethodDef md)
    {
        if (names == null) return md.Name;
        var n = names.ResolveToken(md.MDToken.Raw) as MethodDef;
        return n != null ? n.Name.String : md.Name.String;
    }

    string FieldName(FieldDef fd)
    {
        string c;
        if (canon.TryGetValue(fd.MDToken.Raw, out c)) return c;
        if (names == null) return fd.Name;
        var n = names.ResolveToken(fd.MDToken.Raw) as FieldDef;
        return n != null ? n.Name.String : fd.Name.String;
    }

    public string Type(ITypeDefOrRef t)
    {
        if (t == null) return "null";
        var td = t as TypeDef;
        if (td != null) return DefName(td);
        var ts = t as TypeSpec;
        if (ts != null) return Sig(ts.TypeSig);
        td = Internal(t as TypeRef);
        if (td != null) return DefName(td);
        td = HostType(t);
        if (td != null) return Host.DefName(td);
        return t.FullName;
    }

    public string Sig(TypeSig s)
    {
        if (s == null) return "null";
        switch (s.ElementType)
        {
            case ElementType.Class:
            case ElementType.ValueType:
                return Type(((TypeDefOrRefSig)s).TypeDefOrRef);
            case ElementType.GenericInst:
                {
                    var gi = (GenericInstSig)s;
                    var sb = new StringBuilder(Sig(gi.GenericType)).Append('<');
                    for (int i = 0; i < gi.GenericArguments.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Sig(gi.GenericArguments[i])); }
                    return sb.Append('>').ToString();
                }
            case ElementType.SZArray: return Sig(s.Next) + "[]";
            case ElementType.Array: return Sig(s.Next) + "[rank " + ((ArraySig)s).Rank + "]";
            case ElementType.ByRef: return Sig(s.Next) + "&";
            case ElementType.Ptr: return Sig(s.Next) + "*";
            case ElementType.Pinned: return Sig(s.Next) + " pinned";
            case ElementType.Var: return "!" + ((GenericSig)s).Number;
            case ElementType.MVar: return "!!" + ((GenericSig)s).Number;
            case ElementType.CModReqd: return Sig(s.Next) + " modreq(" + Type(((ModifierSig)s).Modifier) + ")";
            case ElementType.CModOpt: return Sig(s.Next) + " modopt(" + Type(((ModifierSig)s).Modifier) + ")";
            case ElementType.FnPtr: return "fnptr " + Signature(((FnPtrSig)s).MethodSig, "", "");
            default: return s.FullName;
        }
    }

    public string Signature(MethodSig sig, string owner, string name)
    {
        var sb = new StringBuilder();
        if (sig == null) return owner + "::" + name;
        if (sig.HasThis) sb.Append("instance ");
        sb.Append(Sig(sig.RetType)).Append(' ').Append(owner).Append("::").Append(name);
        if (sig.GenParamCount > 0) sb.Append('`').Append(sig.GenParamCount);
        sb.Append('(');
        for (int i = 0; i < sig.Params.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Sig(sig.Params[i])); }
        if (sig.ParamsAfterSentinel != null && sig.ParamsAfterSentinel.Count > 0)
        {
            sb.Append(",...");
            foreach (var p in sig.ParamsAfterSentinel) sb.Append(',').Append(Sig(p));
        }
        return sb.Append(')').ToString();
    }

    string Owner(MemberRef mr)
    {
        var t = mr.Class as ITypeDefOrRef;
        if (t != null) return Type(t);
        var md = mr.Class as MethodDef;
        if (md != null) return DefName(md.DeclaringType);
        return mr.Class == null ? "null" : mr.Class.ToString();
    }

    // Signature comparison fails on some references to generic methods. The name is enough
    // when a single method of the type has it with that many parameters.
    static MethodDef OnlyMethod(TypeDef owner, MemberRef mr)
    {
        MethodDef found = null;
        int n = mr.MethodSig == null ? -1 : mr.MethodSig.Params.Count;
        foreach (var m in owner.Methods)
        {
            if (m.Name != mr.Name || m.MethodSig.Params.Count != n) continue;
            if (found != null) return null;
            found = m;
        }
        return found;
    }

    // The type declared in this module that a MemberRef points into, or null.
    static TypeDef LocalOwner(MemberRef mr)
    {
        var t = mr.Class as ITypeDefOrRef;
        if (t == null) return null;
        return (t.ScopeType as TypeDef) ?? Internal(t.ScopeType as TypeRef);
    }

    // A TypeRef that points back into its own module (the obfuscator left a few): its
    // definition. null for a reference to another assembly.
    static readonly Dictionary<TypeRef, TypeDef> internalRefs = new Dictionary<TypeRef, TypeDef>();
    static TypeDef Internal(TypeRef tr)
    {
        if (tr == null) return null;
        TypeDef d;
        if (internalRefs.TryGetValue(tr, out d)) return d;
        IResolutionScope scope = tr.ResolutionScope;
        while (scope is TypeRef) scope = ((TypeRef)scope).ResolutionScope;
        var ar = scope as AssemblyRef;
        var mod = tr.Module;
        bool local = scope is ModuleDef || (ar != null && mod != null && mod.Assembly != null && ar.Name == mod.Assembly.Name);
        d = local && mod != null ? mod.Find(tr) : null;
        internalRefs[tr] = d;
        return d;
    }

    public string Method(IMethod m)
    {
        if (m == null) return "null";
        var ms = m as MethodSpec;
        if (ms != null)
        {
            var sb = new StringBuilder(Method(ms.Method)).Append('<');
            var inst = ms.GenericInstMethodSig;
            if (inst != null)
                for (int i = 0; i < inst.GenericArguments.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Sig(inst.GenericArguments[i])); }
            return sb.Append('>').ToString();
        }
        var md = m as MethodDef;
        if (md != null) return Signature(md.MethodSig, DefName(md.DeclaringType), MethodName(md));
        var mr = m as MemberRef;
        if (mr != null)
        {
            string name = mr.Name;
            var local = LocalOwner(mr);
            if (local != null)
            {
                var def = local.FindMethod(mr.Name, mr.MethodSig) ?? OnlyMethod(local, mr);
                if (def != null) name = MethodName(def);
            }
            else
            {
                var hosted = HostOwner(mr);
                var def = hosted == null ? null : (hosted.FindMethod(mr.Name, mr.MethodSig) ?? OnlyMethod(hosted, mr));
                if (def != null) name = Host.MethodName(def);
            }
            return Signature(mr.MethodSig, Owner(mr), name);
        }
        return m.FullName;
    }

    public string Field(IField f)
    {
        if (f == null) return "null";
        var fd = f as FieldDef;
        if (fd != null && fd.HasFieldRVA && fd.InitialValue != null)
        {
            // Array-initializer data: the compiler names the field after a hash of its
            // content and the obfuscator renamed it. Compare the content.
            using (var sha = System.Security.Cryptography.SHA1.Create())
                return "data[" + fd.InitialValue.Length + "] " + BitConverter.ToString(sha.ComputeHash(fd.InitialValue)).Replace("-", "");
        }
        if (fd != null) return Sig(fd.FieldSig.Type) + " " + DefName(fd.DeclaringType) + "::" + FieldName(fd);
        var mr = f as MemberRef;
        if (mr != null && mr.FieldSig != null)
        {
            string name = mr.Name;
            var local = LocalOwner(mr);
            if (local != null)
            {
                var def = local.FindField(mr.Name, mr.FieldSig);
                if (def != null) name = FieldName(def);
            }
            else
            {
                var hosted = HostOwner(mr);
                var def = hosted == null ? null : (hosted.FindField(mr.Name, mr.FieldSig) ?? hosted.FindField(mr.Name));
                if (def != null) name = Host.FieldName(def);
            }
            return Sig(mr.FieldSig.Type) + " " + Owner(mr) + "::" + name;
        }
        return f.FullName;
    }

    public string Member(object o)
    {
        var mr = o as MemberRef;
        if (mr != null) return mr.FieldSig != null ? Field(mr) : Method(mr);
        var m = o as IMethod;
        if (m != null && (o is MethodDef || o is MethodSpec)) return Method(m);
        var f = o as FieldDef;
        if (f != null) return Field(f);
        var t = o as ITypeDefOrRef;
        if (t != null) return Type(t);
        var sig = o as MethodSig;
        if (sig != null) return Signature(sig, "", "");
        return null;
    }
}

public static class IlMatch
{
    // byToken = true: both files are stages of the same binary (same metadata tokens,
    // different names). Methods are paired by token and member operands compared by token.
    // byToken = false: candidate is a recompilation. Pairing and operands use rendered names.

    public static string Category(TypeDef t)
    {
        while (t.DeclaringType != null) t = t.DeclaringType;
        string ns = UTF8String.ToSystemStringOrEmpty(t.Namespace);
        if (ns.Length == 0) return "global";
        if (ns == "Obfuscator" || ns.StartsWith("Obfuscator.", StringComparison.Ordinal)) return "obf";
        if (ns == "osu" || ns.StartsWith("osu.", StringComparison.Ordinal)
            || ns == "osu_common" || ns.StartsWith("osu_common.", StringComparison.Ordinal)) return "osu";
        return "lib";
    }

    // Equivalent readings switched off, for measuring what each one absorbs
    // (scripts/dev/strict-match.ps1): slots, ldelem, ldobj, starg, branch-form, nops, pushes,
    // const-slots, dead-stores. Empty in normal use.
    public static readonly HashSet<string> Off = new HashSet<string>(StringComparer.Ordinal);

    internal static string Key(MethodDef m, Namer n, bool byToken)
    {
        return byToken ? m.MDToken.Raw.ToString("X8") : n.Method(m);
    }

    static string Operand(object o, int self, Dictionary<Instruction, int> idx, Namer n, bool byToken)
    {
        if (o == null) return "";
        var target = o as Instruction;
        if (target != null) return Rel(target, self, idx);
        var targets = o as IList<Instruction>;
        if (targets != null)
        {
            var sb = new StringBuilder("(");
            for (int i = 0; i < targets.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Rel(targets[i], self, idx)); }
            return sb.Append(')').ToString();
        }
        var local = o as Local;
        if (local != null) return "V_" + local.Index;
        var par = o as Parameter;
        if (par != null) return "A_" + par.Index;
        var s = o as string;
        if (s != null) return "\"" + s + "\"";
        if (byToken)
        {
            var tp = o as IMDTokenProvider;
            if (tp != null) return "[" + tp.MDToken.Raw.ToString("X8") + "]";
        }
        string member = n.Member(o);
        if (member != null) return member;
        if (o is double) return ((double)o).ToString("R", CultureInfo.InvariantCulture);
        if (o is float) return ((float)o).ToString("R", CultureInfo.InvariantCulture);
        return Convert.ToString(o, CultureInfo.InvariantCulture);
    }

    static string Rel(Instruction target, int self, Dictionary<Instruction, int> idx)
    {
        int t;
        if (target == null || !idx.TryGetValue(target, out t)) return "@?";
        int d = t - self;
        return d >= 0 ? "@+" + d : "@" + d;
    }

    static int Index(Instruction i, Dictionary<Instruction, int> idx, int end)
    {
        int v;
        if (i == null) return end;
        return idx.TryGetValue(i, out v) ? v : -1;
    }

    // Instruction lines only. Branch targets are relative instruction counts so that one
    // inserted instruction does not make every later branch differ.
    //
    // A recompilation (byToken = false) is not compared with the bytes of the reference but
    // with what they stand for, because the obfuscator re-encoded the bodies. Both sides
    // are read in one canonical encoding (docs/matching.md):
    //   - locals numbered as the obfuscator numbers them (Slots);
    //   - 'ldelem T' / 'stelem T' on a reference type as ldelem.ref / stelem.ref, and the
    //     long 'starg' as 'starg.s': forms no C# compiler emits there;
    //   - short or long branches chosen again from the sizes of that encoding. Both the
    //     compiler and the obfuscator take the short form whenever the target is in reach,
    //     so the form in the file only tells the size of the instructions in between.
    public static List<string> Lines(MethodDef m, Namer n, bool byToken)
    {
        var ins = m.Body.Instructions;
        var idx = new Dictionary<Instruction, int>();
        for (int i = 0; i < ins.Count; i++) idx[ins[i]] = i;
        var lines = new List<string>(ins.Count);
        if (byToken)
        {
            for (int i = 0; i < ins.Count; i++)
            {
                string op = Operand(ins[i].Operand, i, idx, n, byToken);
                lines.Add(op.Length == 0 ? ins[i].OpCode.Name : ins[i].OpCode.Name + " " + op);
            }
            return lines;
        }

        int[] slots = Slots(m);
        if (Off.Contains("slots")) for (int i = 0; i < slots.Length; i++) slots[i] = i;
        bool keepForm = Off.Contains("branch-form");
        var size = new int[ins.Count];
        var branch = new bool[ins.Count];
        for (int i = 0; i < ins.Count; i++)
        {
            Code code = ins[i].OpCode.Code;
            Local l = ins[i].Operand as Local ?? ins[i].GetLocal(m.Body.Variables);
            if (l != null)
            {
                int slot = slots[l.Index];
                lines.Add(LocalOp(code, slot));
                size[i] = (code == Code.Ldloca || code == Code.Ldloca_S) ? (slot < 256 ? 2 : 4) : (slot < 4 ? 1 : slot < 256 ? 2 : 4);
            }
            else if (!Off.Contains("ldelem") && (code == Code.Ldelem || code == Code.Stelem) && n.IsReference(ins[i].Operand as ITypeDefOrRef))
            {
                lines.Add(code == Code.Ldelem ? "ldelem.ref" : "stelem.ref");
                size[i] = 1;
            }
            else if (!Off.Contains("ldobj") && code == Code.Ldobj && ins[i].Operand is ITypeDefOrRef && ((ITypeDefOrRef)ins[i].Operand).FullName == "System.IntPtr")
            {
                // The same load, written long: 3 in the reference, which has no 'ldind.i'; a
                // recompilation has 3 'ldind.i' there and no 'ldobj IntPtr' (docs/matching.md).
                lines.Add("ldind.i");
                size[i] = 1;
            }
            else if (!Off.Contains("ldobj") && (code == Code.Ldobj || code == Code.Stobj) && ins[i].Operand is ITypeDefOrRef && ((ITypeDefOrRef)ins[i].Operand).FullName == "System.Byte")
            {
                // Same case for a byte: 1 'ldobj Byte' and 1 'stobj Byte' in the reference
                // (AssemblyBoundKey.XorWithKey, 'a[i] ^= v' from an older compiler), none in a
                // recompilation, which has 'ldind.u1' and 'stind.i1' there.
                lines.Add(code == Code.Ldobj ? "ldind.u1" : "stind.i1");
                size[i] = 1;
            }
            else if (!Off.Contains("starg") && code == Code.Starg && ((Parameter)ins[i].Operand).Index < 256)
            {
                lines.Add("starg.s " + Operand(ins[i].Operand, i, idx, n, byToken));
                size[i] = 2;
            }
            else if (ins[i].Operand is Instruction && !keepForm)
            {
                branch[i] = true;
                lines.Add(null);
                size[i] = 2;
            }
            else
            {
                string op = Operand(ins[i].Operand, i, idx, n, byToken);
                lines.Add(op.Length == 0 ? ins[i].OpCode.Name : ins[i].OpCode.Name + " " + op);
                size[i] = ins[i].GetSize();
            }
        }
        // Smallest encoding: every branch short, then long where the target is out of reach.
        var offset = new int[ins.Count + 1];
        for (bool grown = true; grown; )
        {
            grown = false;
            for (int i = 0; i < ins.Count; i++) offset[i + 1] = offset[i] + size[i];
            for (int i = 0; i < ins.Count; i++)
            {
                if (!branch[i] || size[i] != 2) continue;
                int t;
                if (!idx.TryGetValue((Instruction)ins[i].Operand, out t)) continue;
                int d = offset[t] - offset[i + 1];
                if (d < -128 || d > 127) { size[i] = 5; grown = true; }
            }
        }
        for (int i = 0; i < ins.Count; i++)
        {
            if (!branch[i]) continue;
            string name = ins[i].OpCode.Name;
            if (name.EndsWith(".s", StringComparison.Ordinal)) name = name.Substring(0, name.Length - 2);
            lines[i] = name + (size[i] == 2 ? ".s " : " ") + Operand(ins[i].Operand, i, idx, n, byToken);
        }
        return lines;
    }

    // The numbering of locals the obfuscator gives a method, as a map from the slots of the
    // body to those numbers. The reference no longer has the compiler's numbering: locals
    // are numbered by first appearance in the instructions, then, when a local past slot 3
    // is loaded and stored more often than one of the first four (which have one-byte
    // instructions), sorted by that count, most used first. ldloca has no short form and is
    // not counted. Measured in docs/matching.md. The result does not depend on the
    // numbering the body comes with, so both sides are read through it: a recompiled method
    // is compared as the obfuscator would have left it. Locals that are never used keep
    // their order, at the end.
    public static int[] Slots(MethodDef m)
    {
        var vars = m.Body.Variables;
        int n = vars.Count;
        var map = new int[n];
        if (n < 2) return map;
        var count = new int[n];
        var order = new List<int>(n);
        var seen = new bool[n];
        foreach (var ins in m.Body.Instructions)
        {
            Local l = ins.Operand as Local ?? ins.GetLocal(vars);
            if (l == null) continue;
            if (!seen[l.Index]) { seen[l.Index] = true; order.Add(l.Index); }
            if (ins.OpCode.Code != Code.Ldloca && ins.OpCode.Code != Code.Ldloca_S) count[l.Index]++;
        }
        for (int i = 0; i < n; i++) if (!seen[i]) order.Add(i);
        int min = int.MaxValue, max = 0;
        for (int i = 0; i < n; i++) { if (i < 4) min = Math.Min(min, count[order[i]]); else max = Math.Max(max, count[order[i]]); }
        if (n > 4 && max > min)
        {
            // Stable: equal counts stay in order of first appearance.
            var sorted = new List<int>(n);
            foreach (int v in order)
            {
                int at = sorted.Count;
                while (at > 0 && count[sorted[at - 1]] < count[v]) at--;
                sorted.Insert(at, v);
            }
            order = sorted;
        }
        for (int i = 0; i < n; i++) map[order[i]] = i;
        return map;
    }

    static string LocalOp(Code code, int slot)
    {
        string op;
        switch (code)
        {
            case Code.Ldloca: case Code.Ldloca_S: return (slot < 256 ? "ldloca.s V_" : "ldloca V_") + slot;
            case Code.Stloc: case Code.Stloc_S: case Code.Stloc_0: case Code.Stloc_1: case Code.Stloc_2: case Code.Stloc_3: op = "stloc"; break;
            default: op = "ldloc"; break;
        }
        return slot < 4 ? op + "." + slot : (slot < 256 ? op + ".s V_" : op + " V_") + slot;
    }

    // Locals and exception handlers: part of the match.
    public static List<string> Frame(MethodDef m, Namer n, bool byToken)
    {
        var b = m.Body;
        var ins = b.Instructions;
        var idx = new Dictionary<Instruction, int>();
        for (int i = 0; i < ins.Count; i++) idx[ins[i]] = i;
        var f = new List<string>();
        int[] slots = byToken || Off.Contains("slots") ? null : Slots(m);
        var locals = new string[b.Variables.Count];
        foreach (var v in b.Variables)
        {
            int slot = slots == null ? v.Index : slots[v.Index];
            locals[slot] = "local " + slot + " " + (byToken ? TokenText(v.Type) : n.Sig(v.Type));
        }
        f.AddRange(locals);
        foreach (var eh in b.ExceptionHandlers)
        {
            string ct = eh.CatchType == null ? "" : (byToken ? "[" + eh.CatchType.MDToken.Raw.ToString("X8") + "]" : n.Type(eh.CatchType));
            f.Add(string.Format("eh {0} try {1}-{2} handler {3}-{4} filter {5} {6}", eh.HandlerType,
                Index(eh.TryStart, idx, ins.Count), Index(eh.TryEnd, idx, ins.Count),
                Index(eh.HandlerStart, idx, ins.Count), Index(eh.HandlerEnd, idx, ins.Count),
                eh.FilterStart == null ? -1 : Index(eh.FilterStart, idx, ins.Count), ct));
        }
        return f;
    }

    // byToken mode: describe a local by the tokens in its signature.
    static string TokenText(TypeSig sig)
    {
        var sb = new StringBuilder();
        while (sig != null)
        {
            sb.Append(sig.ElementType);
            var td = sig as TypeDefOrRefSig;
            // A type of the core library (I4, Char...) is told by its element type: the
            // reference the reader makes for it is not a row of the file, and its number
            // moves when another stage adds type references.
            if (td != null && !(sig is CorLibTypeSig) && td.TypeDefOrRef != null) sb.Append('[').Append(td.TypeDefOrRef.MDToken.Raw.ToString("X8")).Append(']');
            var gi = sig as GenericInstSig;
            if (gi != null)
            {
                sb.Append('<');
                if (gi.GenericType != null) sb.Append(TokenText(gi.GenericType));
                foreach (var a in gi.GenericArguments) sb.Append(',').Append(TokenText(a));
                sb.Append('>');
                break;
            }
            var gv = sig as GenericSig;
            if (gv != null) sb.Append(gv.Number);
            sb.Append(' ');
            sig = sig.Next;
        }
        return sb.ToString();
    }

    static string Op(string line) { int sp = line.IndexOf(' '); return sp < 0 ? line : line.Substring(0, sp); }

    static bool IsFlow(string op)
    {
        return op == "ret" || op == "br" || op == "br.s" || op == "leave" || op == "leave.s" || op == "switch" || op == "endfinally"
            || op.StartsWith("brtrue", StringComparison.Ordinal) || op.StartsWith("brfalse", StringComparison.Ordinal)
            || op.StartsWith("beq", StringComparison.Ordinal) || op.StartsWith("bne", StringComparison.Ordinal)
            || op.StartsWith("bge", StringComparison.Ordinal) || op.StartsWith("bgt", StringComparison.Ordinal)
            || op.StartsWith("ble", StringComparison.Ordinal) || op.StartsWith("blt", StringComparison.Ordinal);
    }

    static bool IsLocalOp(string op)
    {
        return op.StartsWith("ldloc", StringComparison.Ordinal) || op.StartsWith("stloc", StringComparison.Ordinal) || op == "dup" || op == "pop";
    }

    static List<string> Bag(List<string> lines, int level)
    {
        // level 0: everything but control flow. 1: also without local traffic and the
        // comparisons a branch can absorb. Sorted, to compare as multisets.
        var l = new List<string>();
        foreach (var x in lines)
        {
            string op = Op(x);
            if (IsFlow(op)) continue;
            if (level >= 1 && (IsLocalOp(op) || op == "ceq" || op == "cgt" || op == "clt" || op == "cgt.un" || op == "clt.un" || op == "ldc.i4.0" || op == "ldc.i4.1" || op == "ldnull" || op == "nop")) continue;
            l.Add(x);
        }
        l.Sort(StringComparer.Ordinal);
        return l;
    }

    static int CountOps(List<string> lines, bool ret)
    {
        int n = 0;
        foreach (var x in lines) { string op = Op(x); if (ret ? op == "ret" : (IsFlow(op) && op != "ret")) n++; }
        return n;
    }

    public static string Shape(List<string> a, List<string> b, int la, int lb)
    {
        return "ret " + CountOps(a, true) + "/" + CountOps(b, true) + " br " + CountOps(a, false) + "/" + CountOps(b, false) + " loc " + la + "/" + lb;
    }

    // Coarse class of a difference between two instruction listings:
    //   frame      same instructions: only locals or exception handlers differ
    //   operands   same opcodes in the same order: a name, a type or a constant differs
    //   branch-form same instructions in the same order, branches differ in form or target
    //   layout     same instructions apart from control flow, in another order
    //   layout+locals  the same once local variable traffic and comparisons are ignored too
    //   code       anything else: the instructions themselves differ
    public static string Cause(List<string> a, List<string> b)
    {
        if (Same(a, b)) return "frame";
        string slots = SlotCause(a, b);
        if (slots != null) return slots;
        if (a.Count == b.Count)
        {
            bool ops = true, rest = true;
            for (int i = 0; i < a.Count; i++)
            {
                string oa = Op(a[i]), ob = Op(b[i]);
                if (oa != ob) ops = false;
                if (a[i] != b[i] && !(IsFlow(oa) && IsFlow(ob))) rest = false;
            }
            if (rest) return "branch-form";
            if (ops) return "operands";
        }
        if (Same(Bag(a, 0), Bag(b, 0))) return "layout";
        if (Same(Bag(a, 1), Bag(b, 1))) return "layout+locals";
        return "code";
    }

    // Slot of a local variable instruction, -1 for anything else. kind: ldloc, stloc or ldloca.
    internal static int Slot(string line, out string kind)
    {
        kind = null;
        if (!line.StartsWith("ldloc", StringComparison.Ordinal) && !line.StartsWith("stloc", StringComparison.Ordinal)) return -1;
        kind = line.StartsWith("ldloca", StringComparison.Ordinal) ? "ldloca" : line.Substring(0, 5);
        int v = line.IndexOf(" V_", StringComparison.Ordinal);
        if (v >= 0) return int.Parse(line.Substring(v + 3), CultureInfo.InvariantCulture);
        int dot = line.LastIndexOf('.');
        int n;
        return dot >= 0 && int.TryParse(line.Substring(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out n) ? n : -1;
    }

    // The same instructions once local slots are renamed (a: reference, b: candidate):
    //   slots-order  one to one: the locals are numbered in another order
    //   slots-reuse  the reference uses one slot where the candidate has several
    //   slots-split  the reference has several slots where the candidate uses one
    //   slots-mixed  both
    // null when anything else differs.
    internal static string SlotCause(List<string> a, List<string> b)
    {
        if (a.Count != b.Count) return null;
        var ab = new Dictionary<int, int>(); var ba = new Dictionary<int, int>();
        bool reuse = false, split = false;
        for (int i = 0; i < a.Count; i++)
        {
            string ka, kb;
            int sa = Slot(a[i], out ka), sb = Slot(b[i], out kb);
            if (sa < 0 || sb < 0) { if (a[i] != b[i]) return null; continue; }
            if (ka != kb) return null;
            int o;
            if (ab.TryGetValue(sa, out o)) { if (o != sb) reuse = true; } else ab[sa] = sb;
            if (ba.TryGetValue(sb, out o)) { if (o != sa) split = true; } else ba[sb] = sa;
        }
        return reuse && split ? "slots-mixed" : reuse ? "slots-reuse" : split ? "slots-split" : "slots-order";
    }

    static bool Same(List<string> a, List<string> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        return true;
    }

    static int[,] LcsTable(List<string> a, List<string> b)
    {
        var t = new int[a.Count + 1, b.Count + 1];
        for (int i = a.Count - 1; i >= 0; i--)
            for (int j = b.Count - 1; j >= 0; j--)
                t[i, j] = string.Equals(a[i], b[j], StringComparison.Ordinal) ? t[i + 1, j + 1] + 1 : Math.Max(t[i + 1, j], t[i, j + 1]);
        return t;
    }

    static double Similarity(List<string> a, List<string> b)
    {
        if (a.Count + b.Count == 0) return 1.0;
        int common;
        if ((long)a.Count * b.Count > 4000000L)
        {
            // Too large for the quadratic table: common prefix plus common suffix.
            int p = 0;
            while (p < a.Count && p < b.Count && a[p] == b[p]) p++;
            int s = 0;
            while (s < a.Count - p && s < b.Count - p && a[a.Count - 1 - s] == b[b.Count - 1 - s]) s++;
            common = p + s;
        }
        else common = LcsTable(a, b)[0, 0];
        return 2.0 * common / (a.Count + b.Count);
    }

    // A candidate method, with the namer of its module and the unit it comes from.
    sealed class Cand { public MethodDef M; public Namer N; public string Unit; }

    static string TopLevel(TypeDef t)
    {
        while (t.DeclaringType != null) t = t.DeclaringType;
        return t.FullName;
    }

    // The candidate methods by key. The main build gives every type no unit claims; a unit
    // gives its types whole, or (Types == null) the methods it defines, each in place of the
    // main build's method of the same name.
    static Dictionary<string, List<Cand>> IndexCandidates(ModuleDefMD cm, Namer cn, bool byToken, IList<UnitModule> units, List<ModuleDefMD> opened)
    {
        var d = new Dictionary<string, List<Cand>>(StringComparer.Ordinal);
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        if (units != null) foreach (var u in units) if (u.Types != null) foreach (var name in u.Types) claimed.Add(name);
        foreach (var t in cm.GetTypes())
        {
            if (claimed.Contains(TopLevel(t))) continue;
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                string k = Key(m, cn, byToken);
                List<Cand> l;
                if (!d.TryGetValue(k, out l)) { l = new List<Cand>(1); d[k] = l; }
                l.Add(new Cand { M = m, N = cn, Unit = "" });
            }
        }
        if (units == null) return d;
        foreach (var u in units)
        {
            var um = Read(u.Data, byToken);
            opened.Add(um);
            var un = new Namer(null, um);
            un.Host = cn;
            var replaced = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in um.GetTypes())
            {
                if (u.Types != null && !u.Types.Contains(TopLevel(t))) continue;
                foreach (var m in t.Methods)
                {
                    if (!m.HasBody) continue;
                    string k = Key(m, un, byToken);
                    List<Cand> l;
                    if (u.Types == null && replaced.Add(k)) d.Remove(k);
                    if (!d.TryGetValue(k, out l)) { l = new List<Cand>(1); d[k] = l; }
                    l.Add(new Cand { M = m, N = un, Unit = u.Name });
                }
            }
        }
        return d;
    }

    internal static Dictionary<string, List<MethodDef>> IndexBodies(ModuleDefMD mod, Namer n, bool byToken)
    {
        var d = new Dictionary<string, List<MethodDef>>(StringComparer.Ordinal);
        foreach (var t in mod.GetTypes())
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                string k = Key(m, n, byToken);
                List<MethodDef> l;
                if (!d.TryGetValue(k, out l)) { l = new List<MethodDef>(1); d[k] = l; }
                l.Add(m);
            }
        return d;
    }

    // names may be null: the reference then carries its own names.
    // A module to compare. A recompilation is read through the canonical encoding, of which
    // this is a part (docs/matching.md).
    static ModuleDefMD Read(byte[] data, bool byToken)
    {
        var module = ModuleDefMD.Load(data);
        if (byToken) return module;
        foreach (var t in module.GetTypes())
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                if (!Off.Contains("nops")) DropFreeNops(m.Body);
                if (!Off.Contains("pushes")) DropDeadPushes(m.Body);
                if (m.Body.Variables.Count > 0 && !Off.Contains("const-slots")) FoldConstantSlots(m.Body);
                if (m.Body.Variables.Count > 0 && !Off.Contains("dead-stores")) DropDeadStores(m.Body);
            }
        return module;
    }

    // A 'nop' that no jump goes to and that bounds no protected block is not read. The
    // compiler emits one in front of a 'try' when a label was marked there, even a label
    // nothing jumps to (the end of a loop). The reference has 220 'nop', every one a jump
    // target or the start of a handler; a recompilation has four more, free ones, in the four
    // methods that differed by that alone (docs/matching.md).
    static void DropFreeNops(CilBody body)
    {
        var ins = body.Instructions;
        bool any = false;
        foreach (var x in ins) if (x.OpCode.Code == Code.Nop) { any = true; break; }
        if (!any) return;
        var held = new HashSet<Instruction>();
        foreach (var x in ins)
        {
            var one = x.Operand as Instruction;
            var many = x.Operand as Instruction[];
            if (one != null) held.Add(one);
            if (many != null) foreach (var y in many) if (y != null) held.Add(y);
        }
        foreach (var eh in body.ExceptionHandlers)
        {
            if (eh.TryStart != null) held.Add(eh.TryStart);
            if (eh.TryEnd != null) held.Add(eh.TryEnd);
            if (eh.HandlerStart != null) held.Add(eh.HandlerStart);
            if (eh.HandlerEnd != null) held.Add(eh.HandlerEnd);
            if (eh.FilterStart != null) held.Add(eh.FilterStart);
        }
        for (int i = ins.Count - 1; i >= 0; i--)
            if (ins[i].OpCode.Code == Code.Nop && !held.Contains(ins[i])) ins.RemoveAt(i);
    }

    // 'dup; stloc x' on a slot nothing reads is read as 'dup; pop', and the slot leaves the
    // list. It is what the compiler emits for an assignment used as a value whose variable
    // is never read ('a[0] = (x = e)'); the reference has no slot that is only written, and
    // one 'dup; pop', which no compiler emits (0 in a recompilation): the store was changed
    // after compilation (docs/matching.md).
    static void DropDeadStores(CilBody body)
    {
        var ins = body.Instructions;
        var vars = body.Variables;
        // By Local, not by index: the indices are stale once another reading removed a slot.
        var stored = new HashSet<Local>(); var kept = new HashSet<Local>();
        for (int i = 0; i < ins.Count; i++)
        {
            Code code = ins[i].OpCode.Code;
            Local l = ins[i].Operand as Local ?? ins[i].GetLocal(vars);
            if (l == null) continue;
            if (IsStoreCode(code) && i > 0 && ins[i - 1].OpCode.Code == Code.Dup) stored.Add(l);
            else kept.Add(l);
        }
        var gone = new List<Local>();
        foreach (var l in stored) if (!kept.Contains(l)) gone.Add(l);
        if (gone.Count == 0) return;
        foreach (var x in ins)
        {
            Code code = x.OpCode.Code;
            if (x.Operand is Local || !(IsStoreCode(code) || IsLoadCode(code))) continue;
            Local l = x.GetLocal(vars);
            x.OpCode = IsStoreCode(code) ? OpCodes.Stloc : OpCodes.Ldloc;
            x.Operand = l;
        }
        foreach (var x in ins)
            if (IsStoreCode(x.OpCode.Code) && gone.Contains(x.Operand as Local)) { x.OpCode = OpCodes.Pop; x.Operand = null; }
        foreach (var l in gone) vars.Remove(l);
    }

    static bool IsPlainPush(Code c)
    {
        switch (c)
        {
            case Code.Ldloc: case Code.Ldloc_S: case Code.Ldloc_0: case Code.Ldloc_1: case Code.Ldloc_2: case Code.Ldloc_3:
            case Code.Ldarg: case Code.Ldarg_S: case Code.Ldarg_0: case Code.Ldarg_1: case Code.Ldarg_2: case Code.Ldarg_3:
            case Code.Ldc_I4: case Code.Ldc_I4_S: case Code.Ldc_I4_M1: case Code.Ldc_I4_0: case Code.Ldc_I4_1: case Code.Ldc_I4_2:
            case Code.Ldc_I4_3: case Code.Ldc_I4_4: case Code.Ldc_I4_5: case Code.Ldc_I4_6: case Code.Ldc_I4_7: case Code.Ldc_I4_8:
            case Code.Ldc_I8: case Code.Ldc_R4: case Code.Ldc_R8: case Code.Ldnull:
                return true;
        }
        return false;
    }

    static bool IsPlainConversion(Code c)
    {
        switch (c)
        {
            case Code.Conv_I1: case Code.Conv_I2: case Code.Conv_I4: case Code.Conv_I8: case Code.Conv_U1: case Code.Conv_U2:
            case Code.Conv_U4: case Code.Conv_U8: case Code.Conv_R4: case Code.Conv_R8: case Code.Conv_I: case Code.Conv_U: case Code.Conv_R_Un:
                return true;
        }
        return false;
    }

    // A value dropped as soon as it is there: a conversion right before a 'pop' is not read,
    // nor a variable, an argument or a constant pushed right before one, with that 'pop'.
    // The compiler leaves both ('a.Length;' as a statement is 'ldlen; conv.i4; pop', a label
    // of a 64-bit 'switch' that leads nowhere is 'ldloc; ldc; conv.u8; pop; pop'); the
    // reference has neither form anywhere, a recompilation has them in the two methods that
    // differed by that (docs/matching.md). What may have an
    // effect stays: 'ldsfld; pop', 'ldfld; pop', 'ldlen; pop', an operation and its 'pop'.
    static void DropDeadPushes(CilBody body)
    {
        var ins = body.Instructions;
        bool any = false;
        foreach (var x in ins) if (x.OpCode.Code == Code.Pop) { any = true; break; }
        if (!any) return;
        var held = new HashSet<Instruction>();
        foreach (var x in ins)
        {
            var one = x.Operand as Instruction;
            var many = x.Operand as Instruction[];
            if (one != null) held.Add(one);
            if (many != null) foreach (var y in many) if (y != null) held.Add(y);
        }
        // The start of a block stays where it is; its end (the first instruction after it) moves
        // with the jumps.
        var bounds = new HashSet<Instruction>();
        foreach (var eh in body.ExceptionHandlers)
        {
            if (eh.TryStart != null) bounds.Add(eh.TryStart);
            if (eh.HandlerStart != null) bounds.Add(eh.HandlerStart);
            if (eh.FilterStart != null) bounds.Add(eh.FilterStart);
            if (eh.TryEnd != null) held.Add(eh.TryEnd);
            if (eh.HandlerEnd != null) held.Add(eh.HandlerEnd);
        }
        for (bool changed = true; changed; )
        {
            changed = false;
            for (int i = 1; i < ins.Count; i++)
            {
                if (ins[i].OpCode.Code != Code.Pop || held.Contains(ins[i]) || bounds.Contains(ins[i]) || bounds.Contains(ins[i - 1])) continue;
                Code before = ins[i - 1].OpCode.Code;
                if (held.Contains(ins[i - 1]))
                {
                    // A jump to a value that is dropped at once is read as a jump to what follows
                    // (BanchoClient.InitializeClientHash: the reference jumps straight there).
                    if (!IsPlainPush(before) || i + 1 >= ins.Count) continue;
                    Instruction gone = ins[i - 1], next = ins[i + 1];
                    foreach (var x in ins)
                    {
                        if (x.Operand == gone) x.Operand = next;
                        var many = x.Operand as Instruction[];
                        if (many != null) for (int k = 0; k < many.Length; k++) if (many[k] == gone) many[k] = next;
                    }
                    foreach (var eh in body.ExceptionHandlers)
                    {
                        if (eh.TryEnd == gone) eh.TryEnd = next;
                        if (eh.HandlerEnd == gone) eh.HandlerEnd = next;
                    }
                    held.Remove(gone); held.Add(next);
                    ins.RemoveAt(i); ins.RemoveAt(i - 1); changed = true; break;
                }
                if (IsPlainConversion(before)) { ins.RemoveAt(i - 1); changed = true; break; }
                if (IsPlainPush(before)) { ins.RemoveAt(i); ins.RemoveAt(i - 1); changed = true; break; }
                // 'a; b; pop; pop' with b plain: b and its 'pop' go, 'a; pop' stays to be read again.
            }
        }
    }

    static bool IsShortConstant(Code c)
    {
        return c == Code.Ldnull || c == Code.Ldc_I4_M1 || (c >= Code.Ldc_I4_0 && c <= Code.Ldc_I4_8);
    }

    static bool IsStoreCode(Code c)
    {
        return c == Code.Stloc || c == Code.Stloc_S || c == Code.Stloc_0 || c == Code.Stloc_1 || c == Code.Stloc_2 || c == Code.Stloc_3;
    }

    static bool IsLoadCode(Code c)
    {
        return c == Code.Ldloc || c == Code.Ldloc_S || c == Code.Ldloc_0 || c == Code.Ldloc_1 || c == Code.Ldloc_2 || c == Code.Ldloc_3;
    }

    // A slot written once, by a one-byte constant ('ldc.i4.1; stloc t'), and read once further
    // down the same straight line is read as the constant where it is loaded. The reference
    // has no such slot left (the compiler's temporary for the constant of a lifted comparison,
    // 'n == true', is gone wherever its slot had no other use), a recompilation has one per
    // such comparison: measured in docs/matching.md. A slot with another store or another
    // read, a longer constant, a jump or a jump target in between: left as it is.
    static void FoldConstantSlots(CilBody body)
    {
        var ins = body.Instructions;
        var vars = body.Variables;
        int n = vars.Count;
        var stores = new int[n]; var loads = new int[n]; var other = new bool[n];
        var storeAt = new int[n]; var loadAt = new int[n];
        for (int i = 0; i < ins.Count; i++)
        {
            Code code = ins[i].OpCode.Code;
            Local l = ins[i].Operand as Local ?? ins[i].GetLocal(vars);
            if (l == null) continue;
            if (IsStoreCode(code)) { stores[l.Index]++; storeAt[l.Index] = i; }
            else if (IsLoadCode(code)) { loads[l.Index]++; loadAt[l.Index] = i; }
            else other[l.Index] = true;
        }
        var targets = new HashSet<Instruction>();
        foreach (var x in ins)
        {
            var one = x.Operand as Instruction;
            var many = x.Operand as Instruction[];
            if (one != null) targets.Add(one);
            if (many != null) foreach (var y in many) if (y != null) targets.Add(y);
        }
        foreach (var eh in body.ExceptionHandlers)
        {
            if (eh.TryStart != null) targets.Add(eh.TryStart);
            if (eh.TryEnd != null) targets.Add(eh.TryEnd);
            if (eh.HandlerStart != null) targets.Add(eh.HandlerStart);
            if (eh.HandlerEnd != null) targets.Add(eh.HandlerEnd);
            if (eh.FilterStart != null) targets.Add(eh.FilterStart);
        }
        var folded = new List<Local>();
        var dead = new List<Instruction>();
        for (int v = 0; v < n; v++)
        {
            if (stores[v] != 1 || loads[v] != 1 || other[v]) continue;
            int s = storeAt[v], e = loadAt[v];
            if (s < 1 || e <= s || !IsShortConstant(ins[s - 1].OpCode.Code)) continue;
            bool straight = !targets.Contains(ins[s - 1]);
            for (int j = s; j <= e && straight; j++)
            {
                if (targets.Contains(ins[j])) straight = false;
                FlowControl flow = ins[j].OpCode.FlowControl;
                if (j < e && flow != FlowControl.Next && flow != FlowControl.Call && flow != FlowControl.Meta) straight = false;
            }
            if (!straight) continue;
            ins[e].OpCode = ins[s - 1].OpCode;
            ins[e].Operand = null;
            dead.Add(ins[s - 1]); dead.Add(ins[s]);
            folded.Add(vars[v]);
        }
        if (folded.Count == 0) return;
        foreach (var x in dead) ins.Remove(x);
        // The short forms name a slot by its number: give every one its Local before the
        // folded slots leave the list.
        foreach (var x in ins)
        {
            Code code = x.OpCode.Code;
            if (x.Operand is Local || !(IsStoreCode(code) || IsLoadCode(code))) continue;
            Local l = x.GetLocal(vars);
            x.OpCode = IsStoreCode(code) ? OpCodes.Stloc : OpCodes.Ldloc;
            x.Operand = l;
        }
        foreach (var l in folded) vars.Remove(l);
    }

    public static List<MethodResult> Compare(byte[] reference, byte[] names, byte[] candidate, bool byToken)
    {
        return Compare(reference, names, candidate, byToken, null);
    }

    public static List<MethodResult> Compare(byte[] reference, byte[] names, byte[] candidate, bool byToken, IList<UnitModule> units)
    {
        var rm = Read(reference, byToken);
        var nm = names == null ? null : ModuleDefMD.Load(names);
        var cm = Read(candidate, byToken);
        var rn = new Namer(nm, rm);
        var cn = new Namer(null, cm);
        var opened = new List<ModuleDefMD>();
        var cand = IndexCandidates(cm, cn, byToken, units, opened);
        var results = new List<MethodResult>();
        foreach (var t in rm.GetTypes())
        {
            string cat = Category(rn.NamedType(t));
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                var r = new MethodResult();
                r.Token = m.MDToken.Raw; r.Category = cat; r.Name = rn.Method(m); r.Note = "";
                List<string> rl = Lines(m, rn, byToken);
                r.Size = rl.Count;
                List<Cand> cl;
                if (!cand.TryGetValue(Key(m, rn, byToken), out cl)) { r.Status = "missing"; r.Score = 0; results.Add(r); continue; }
                List<string> rf = Frame(m, rn, byToken);
                double best = -1; bool matched = false; MethodDef bestM = null; Namer cn2 = cn;
                foreach (var c in cl)
                {
                    List<string> l = Lines(c.M, c.N, byToken);
                    if (Same(rl, l) && Same(rf, Frame(c.M, c.N, byToken))) { matched = true; bestM = c.M; cn2 = c.N; r.Unit = c.Unit; break; }
                    double s = Similarity(rl, l);
                    if (s > best) { best = s; bestM = c.M; cn2 = c.N; r.Unit = c.Unit; }
                }
                r.Status = matched ? "match" : "diff";
                r.Score = matched ? 1.0 : Math.Min(best, 0.9999);
                var note = new List<string>();
                if (cl.Count > 1) note.Add("ambiguous x" + cl.Count);
                if (!matched && Same(rl, Lines(bestM, cn2, byToken))) note.Add("locals/eh only");
                if (m.Body.MaxStack != bestM.Body.MaxStack) note.Add("maxstack " + m.Body.MaxStack + "/" + bestM.Body.MaxStack);
                if (m.Body.InitLocals != bestM.Body.InitLocals) note.Add("initlocals " + m.Body.InitLocals + "/" + bestM.Body.InitLocals);
                r.Note = string.Join("; ", note.ToArray());
                r.Cause = matched ? "" : Cause(rl, Lines(bestM, cn2, byToken));
                r.Shape = matched ? "" : Shape(rl, Lines(bestM, cn2, byToken), m.Body.Variables.Count, bestM.Body.Variables.Count);
                results.Add(r);
            }
        }
        rm.Dispose(); cm.Dispose();
        foreach (var o in opened) o.Dispose();
        if (nm != null) nm.Dispose();
        return results;
    }

    // Unified listing of one method: ' ' common, '-' reference only, '+' candidate only.
    public static string Show(byte[] reference, byte[] names, byte[] candidate, bool byToken, uint token)
    {
        return Show(reference, names, candidate, byToken, token, null);
    }

    public static string Show(byte[] reference, byte[] names, byte[] candidate, bool byToken, uint token, IList<UnitModule> units)
    {
        var rm = Read(reference, byToken);
        var nm = names == null ? null : ModuleDefMD.Load(names);
        var cm = Read(candidate, byToken);
        var rn = new Namer(nm, rm);
        var cn = new Namer(null, cm);
        var sb = new StringBuilder();
        var m = rm.ResolveToken(token) as MethodDef;
        if (m == null || !m.HasBody) return "not a method with a body in the reference: 0x" + token.ToString("X8");
        sb.AppendLine("reference: " + rn.Method(m));
        List<Cand> cl;
        if (!IndexCandidates(cm, cn, byToken, units, new List<ModuleDefMD>()).TryGetValue(Key(m, rn, byToken), out cl)) return sb.Append("missing in candidate").ToString();
        var c = cl[0];
        if (c.Unit != "") sb.AppendLine("candidate from unit: " + c.Unit);
        var a = Frame(m, rn, byToken); a.AddRange(Lines(m, rn, byToken));
        var b = Frame(c.M, c.N, byToken); b.AddRange(Lines(c.M, c.N, byToken));
        // A large method: the lines both sides share at the start and at the end are set
        // aside, the table is built on what lies between.
        int head = 0, tail = 0;
        if ((long)a.Count * b.Count > 4000000L)
        {
            while (head < a.Count && head < b.Count && a[head] == b[head]) head++;
            while (tail < a.Count - head && tail < b.Count - head && a[a.Count - 1 - tail] == b[b.Count - 1 - tail]) tail++;
            for (int k = 0; k < head; k++) sb.AppendLine("  " + a[k]);
            var ends = a.GetRange(a.Count - tail, tail);
            a = a.GetRange(head, a.Count - head - tail); b = b.GetRange(head, b.Count - head - tail);
            if ((long)a.Count * b.Count > 16000000L) return sb.Append("too large for a diff").ToString();
            Walk(sb, a, b);
            foreach (var line in ends) sb.AppendLine("  " + line);
            return sb.ToString();
        }
        Walk(sb, a, b);
        return sb.ToString();
    }

    static void Walk(StringBuilder sb, List<string> a, List<string> b)
    {
        var t = LcsTable(a, b);
        int i = 0, j = 0;
        while (i < a.Count || j < b.Count)
        {
            if (i < a.Count && j < b.Count && a[i] == b[j]) { sb.AppendLine("  " + a[i]); i++; j++; }
            else if (j >= b.Count || (i < a.Count && t[i + 1, j] >= t[i, j + 1])) { sb.AppendLine("- " + a[i]); i++; }
            else { sb.AppendLine("+ " + b[j]); j++; }
        }
    }

    // Every differing method as a listing of its changed lines only ('-' reference,
    // '+' candidate), for searching recurring patterns. Header: token, cause, name.
    public static void Dump(byte[] reference, byte[] names, byte[] candidate, bool byToken, string path)
    {
        Dump(reference, names, candidate, byToken, path, null);
    }

    public static void Dump(byte[] reference, byte[] names, byte[] candidate, bool byToken, string path, IList<UnitModule> units)
    {
        var rm = Read(reference, byToken);
        var nm = names == null ? null : ModuleDefMD.Load(names);
        var cm = Read(candidate, byToken);
        var rn = new Namer(nm, rm);
        var cn = new Namer(null, cm);
        var cand = IndexCandidates(cm, cn, byToken, units, new List<ModuleDefMD>());
        using (var w = new System.IO.StreamWriter(path, false, new UTF8Encoding(false)))
        {
            foreach (var type in rm.GetTypes())
                foreach (var m in type.Methods)
                {
                    if (!m.HasBody) continue;
                    List<Cand> cl;
                    if (!cand.TryGetValue(Key(m, rn, byToken), out cl)) continue;
                    var a = Frame(m, rn, byToken); var al = Lines(m, rn, byToken); a.AddRange(al);
                    List<string> b = null, bl = null; double best = -1;
                    foreach (var c in cl)
                    {
                        var l = Lines(c.M, c.N, byToken);
                        var f = Frame(c.M, c.N, byToken); f.AddRange(l);
                        if (Same(a, f)) { b = null; best = 2; break; }
                        double sc = Similarity(al, l);
                        if (sc > best) { best = sc; b = f; bl = l; }
                    }
                    if (best == 2 || b == null) continue;
                    w.WriteLine("@@ 0x" + m.MDToken.Raw.ToString("X8") + "\t" + Cause(al, bl) + "\t" + Category(rn.NamedType(type)) + "\t" + rn.Method(m));
                    if ((long)a.Count * b.Count > 4000000L) { w.WriteLine("(too large)"); continue; }
                    var t = LcsTable(a, b);
                    int i = 0, j = 0;
                    while (i < a.Count || j < b.Count)
                    {
                        if (i < a.Count && j < b.Count && a[i] == b[j]) { i++; j++; }
                        else if (j >= b.Count || (i < a.Count && t[i + 1, j] >= t[i, j + 1])) { w.WriteLine("- " + a[i]); i++; }
                        else { w.WriteLine("+ " + b[j]); j++; }
                    }
                }
        }
    }
}
