// Gives compiler-generated members their Roslyn names back (closure classes, lambdas,
// iterators, local functions, auto-property backing fields). The obfuscator renamed them
// and stripped [CompilerGenerated]; without both the decompiler does not fold them.
// Detection is structural. Tokens and IL do not change. See docs/matching.md.
// Compiled by scripts/apply-names.ps1 with Add-Type (Windows PowerShell 5.1: C# 5 only).
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

public sealed class CompilerNames
{
    enum Kind { None, StaticLambdas, Display, StructClosure, Iterator }

    struct Site { public MethodDef Method; public int Index; }

    readonly ModuleDefMD mod;
    readonly Dictionary<uint, object> defByRef;
    readonly ICustomAttributeType cgCtor;

    // token, kind, old name, new name
    public readonly List<string> Report = new List<string>();
    public readonly Dictionary<string, int> Counts = new Dictionary<string, int>(StringComparer.Ordinal);

    readonly Dictionary<MethodDef, List<Site>> newobjSites = new Dictionary<MethodDef, List<Site>>();
    readonly Dictionary<MethodDef, List<Site>> ftnSites = new Dictionary<MethodDef, List<Site>>();
    readonly Dictionary<MethodDef, List<Site>> callSites = new Dictionary<MethodDef, List<Site>>();
    readonly Dictionary<FieldDef, List<Site>> fieldSites = new Dictionary<FieldDef, List<Site>>();
    readonly Dictionary<TypeDef, List<FieldDef>> fieldSigRefs = new Dictionary<TypeDef, List<FieldDef>>();
    readonly Dictionary<TypeDef, List<MethodDef>> methodSigRefs = new Dictionary<TypeDef, List<MethodDef>>();
    readonly Dictionary<TypeDef, List<MethodDef>> localUsers = new Dictionary<TypeDef, List<MethodDef>>();
    readonly HashSet<TypeDef> typeOperands = new HashSet<TypeDef>();
    readonly HashSet<object> tokenLoads = new HashSet<object>();
    readonly Dictionary<uint, TypeDef> typeRefCache = new Dictionary<uint, TypeDef>();
    readonly Dictionary<TypeDef, Kind> kinds = new Dictionary<TypeDef, Kind>();
    readonly Dictionary<object, MethodDef> topCache = new Dictionary<object, MethodDef>();

    static readonly Regex Placeholder = new Regex(@"^[a-z]*method_\d+$", RegexOptions.CultureInvariant);

    public CompilerNames(ModuleDefMD mod, Dictionary<uint, object> defByRef, ICustomAttributeType cgCtor)
    {
        this.mod = mod; this.defByRef = defByRef; this.cgCtor = cgCtor;
    }

    public void Apply()
    {
        BuildIndex();
        Classify();
        NameClosures();
        NameThisLambdas();
        NameAutoProperties();
    }

    // ---- resolution ------------------------------------------------------------

    bool IsLocal(TypeRef tr)
    {
        var scope = tr.ResolutionScope;
        var parent = scope as TypeRef;
        if (parent != null) return IsLocal(parent);
        if (scope is ModuleDef) return true;
        var ar = scope as AssemblyRef;
        return ar != null && mod.Assembly != null && ar.Name == mod.Assembly.Name;
    }

    TypeDef TD(ITypeDefOrRef t)
    {
        if (t == null) return null;
        var td = t as TypeDef;
        if (td != null) return td;
        var ts = t as TypeSpec;
        if (ts != null) return Leaf(ts.TypeSig);
        var tr = t as TypeRef;
        if (tr == null) return null;
        TypeDef r;
        if (typeRefCache.TryGetValue(tr.MDToken.Raw, out r)) return r;
        r = IsLocal(tr) ? mod.Find(tr) : null;
        typeRefCache[tr.MDToken.Raw] = r;
        return r;
    }

    // The TypeDef a signature is about, through arrays, refs and generic instantiation.
    TypeDef Leaf(TypeSig s)
    {
        while (s != null)
        {
            var gi = s as GenericInstSig;
            if (gi != null) { s = gi.GenericType; continue; }
            var tdr = s as TypeDefOrRefSig;
            if (tdr != null) return TD(tdr.TypeDefOrRef);
            s = s.Next;
        }
        return null;
    }

    void Mentions(TypeSig s, HashSet<TypeDef> acc)
    {
        while (s != null)
        {
            var gi = s as GenericInstSig;
            if (gi != null)
            {
                Mentions(gi.GenericType, acc);
                foreach (var a in gi.GenericArguments) Mentions(a, acc);
                return;
            }
            var tdr = s as TypeDefOrRefSig;
            if (tdr != null) { var td = TD(tdr.TypeDefOrRef); if (td != null && td.IsNested) acc.Add(td); return; }
            s = s.Next;
        }
    }

    MethodDef RM(object op)
    {
        var ms = op as MethodSpec;
        if (ms != null) op = ms.Method;
        var md = op as MethodDef;
        if (md != null) return md;
        var mr = op as MemberRef;
        object o;
        if (mr != null && defByRef.TryGetValue(mr.MDToken.Raw, out o)) return o as MethodDef;
        return null;
    }

    FieldDef RF(object op)
    {
        var fd = op as FieldDef;
        if (fd != null) return fd;
        var mr = op as MemberRef;
        object o;
        if (mr != null && defByRef.TryGetValue(mr.MDToken.Raw, out o)) return o as FieldDef;
        return null;
    }

    static void Add<K, V>(Dictionary<K, List<V>> d, K k, V v)
    {
        List<V> l;
        if (!d.TryGetValue(k, out l)) { l = new List<V>(); d[k] = l; }
        l.Add(v);
    }

    static List<V> Get<K, V>(Dictionary<K, List<V>> d, K k)
    {
        List<V> l;
        return d.TryGetValue(k, out l) ? l : new List<V>();
    }

    void BuildIndex()
    {
        var acc = new HashSet<TypeDef>();
        foreach (var t in mod.GetTypes())
        {
            foreach (var f in t.Fields)
            {
                acc.Clear(); Mentions(f.FieldSig.Type, acc);
                foreach (var td in acc) Add(fieldSigRefs, td, f);
            }
            foreach (var m in t.Methods)
            {
                acc.Clear(); Mentions(m.MethodSig.RetType, acc);
                foreach (var p in m.MethodSig.Params) Mentions(p, acc);
                foreach (var td in acc) Add(methodSigRefs, td, m);
                if (!m.HasBody) continue;
                acc.Clear();
                foreach (var v in m.Body.Variables) Mentions(v.Type, acc);
                foreach (var td in acc) Add(localUsers, td, m);
                var ins = m.Body.Instructions;
                for (int i = 0; i < ins.Count; i++)
                {
                    object op = ins[i].Operand;
                    if (op == null) continue;
                    var site = new Site { Method = m, Index = i };
                    switch (ins[i].OpCode.Code)
                    {
                        case Code.Newobj: { var d = RM(op); if (d != null) Add(newobjSites, d, site); break; }
                        case Code.Ldftn:
                        case Code.Ldvirtftn: { var d = RM(op); if (d != null) Add(ftnSites, d, site); break; }
                        case Code.Call:
                        case Code.Callvirt: { var d = RM(op); if (d != null) Add(callSites, d, site); break; }
                        case Code.Ldfld:
                        case Code.Ldflda:
                        case Code.Stfld:
                        case Code.Ldsfld:
                        case Code.Ldsflda:
                        case Code.Stsfld: { var d = RF(op); if (d != null) Add(fieldSites, d, site); break; }
                        case Code.Ldtoken:
                            {
                                object d = (object)RM(op) ?? RF(op);
                                if (d != null) tokenLoads.Add(d);
                                var tt = op as ITypeDefOrRef;
                                if (tt != null) { var td = TD(tt); if (td != null) typeOperands.Add(td); }
                                break;
                            }
                        case Code.Initobj: break;
                        default:
                            {
                                var tt = op as ITypeDefOrRef;
                                if (tt != null) { var td = TD(tt); if (td != null) typeOperands.Add(td); }
                                break;
                            }
                    }
                }
            }
        }
    }

    // ---- classification ----------------------------------------------------------

    static bool IsObject(ITypeDefOrRef t) { return t != null && t.FullName == "System.Object"; }

    static List<Instruction> Code_(MethodDef m)
    {
        var l = new List<Instruction>();
        if (m == null || !m.HasBody) return l;
        foreach (var i in m.Body.Instructions) if (i.OpCode.Code != Code.Nop) l.Add(i);
        return l;
    }

    static bool IsLdarg(Instruction i, int n)
    {
        switch (i.OpCode.Code)
        {
            case Code.Ldarg_0: return n == 0;
            case Code.Ldarg_1: return n == 1;
            case Code.Ldarg:
            case Code.Ldarg_S: { var p = i.Operand as Parameter; return p != null && p.Index == n; }
        }
        return false;
    }

    static MethodDef OnlyCtor(TypeDef c)
    {
        MethodDef ctor = null;
        foreach (var m in c.Methods)
        {
            if (m.IsStaticConstructor) return null;
            if (!m.IsInstanceConstructor) continue;
            if (ctor != null) return null;
            ctor = m;
        }
        return ctor;
    }

    bool Used(MethodDef m) { return ftnSites.ContainsKey(m) || callSites.ContainsKey(m); }

    bool IsStaticLambdas(TypeDef c)
    {
        if (c.IsValueType || c.IsInterface || c.IsAbstract || !IsObject(c.BaseType)) return false;
        if (c.Interfaces.Count != 0 || c.NestedTypes.Count != 0 || c.Fields.Count == 0) return false;
        FieldDef inst = null;
        foreach (var f in c.Fields)
        {
            if (!f.IsStatic) return false;
            if (f.FieldSig.Type is ClassSig || f.FieldSig.Type is GenericInstSig)
                if (Leaf(f.FieldSig.Type) == c) { if (inst != null) return false; inst = f; }
        }
        if (inst == null) return false;
        MethodDef cctor = c.FindStaticConstructor();
        var code = Code_(cctor);
        if (code.Count != 3 || code[0].OpCode.Code != Code.Newobj || code[1].OpCode.Code != Code.Stsfld || RF(code[1].Operand) != inst) return false;
        int lambdas = 0;
        foreach (var m in c.Methods)
        {
            if (m.IsConstructor) continue;
            if (m.IsStatic || !ftnSites.ContainsKey(m)) return false;
            lambdas++;
        }
        return lambdas > 0;
    }

    // 'lambdas': the class has lambdas. A closure without any holds variables that only the
    // lambdas of nested scopes capture: recognized by the link field of their closures.
    bool IsDisplay(TypeDef c, bool lambdas = true)
    {
        if (c.IsValueType || c.IsInterface || c.IsAbstract || !IsObject(c.BaseType)) return false;
        if (c.Interfaces.Count != 0 || c.NestedTypes.Count != 0 || c.Fields.Count == 0) return false;
        foreach (var f in c.Fields) if (f.IsStatic || !f.IsPublic) return false;
        MethodDef ctor = OnlyCtor(c);
        if (ctor == null || ctor.MethodSig.Params.Count != 0) return false;
        var code = Code_(ctor);
        if (code.Count != 3 || !IsLdarg(code[0], 0) || code[1].OpCode.Code != Code.Call || code[2].OpCode.Code != Code.Ret) return false;
        int n = 0;
        foreach (var m in c.Methods)
        {
            if (m.IsConstructor) continue;
            if (m.IsStatic || m.IsVirtual || !(m.IsAssembly || m.IsPublic) || !Used(m) || tokenLoads.Contains(m)) return false;
            n++;
        }
        if ((n == 0) == lambdas || typeOperands.Contains(c) || methodSigRefs.ContainsKey(c)) return false;
        return newobjSites.ContainsKey(ctor);
    }

    bool IsStructClosure(TypeDef c)
    {
        // Roslyn gives closure structs [StructLayout(LayoutKind.Auto)]; a C# struct is sequential.
        if (!c.IsValueType || c.IsEnum || !c.IsAutoLayout || c.Fields.Count == 0 || c.Methods.Count != 0) return false;
        if (c.Interfaces.Count != 0 || c.NestedTypes.Count != 0 || typeOperands.Contains(c)) return false;
        foreach (var f in c.Fields) if (f.IsStatic || !f.IsPublic) return false;
        var users = Get(methodSigRefs, c);
        if (users.Count == 0 || !localUsers.ContainsKey(c)) return false;
        foreach (var m in users)
        {
            if (Leaf(m.MethodSig.RetType) == c) return false;
            foreach (var p in m.MethodSig.Params)
                if (Leaf(p) == c && !(p is ByRefSig && p.Next is ValueTypeSig) && !(p is ByRefSig && p.Next is GenericInstSig)) return false;
            if (!m.HasBody || !(m.IsPrivate || m.IsAssembly) || tokenLoads.Contains(m)) return false;
        }
        return true;
    }

    // The MoveNext of an iterator class. The compiler names it 'MoveNext'; the renamer gave
    // one the name of an explicit implementation ('System.Collections.IEnumerator.MoveNext'):
    // it is then known by the method it overrides.
    static MethodDef MoveNextOf(TypeDef c)
    {
        MethodDef named = c.FindMethod("MoveNext");
        if (named != null) return named;
        foreach (var m in c.Methods)
            foreach (var o in m.Overrides)
                if (o.MethodDeclaration != null && o.MethodDeclaration.Name == "MoveNext" && o.MethodDeclaration.DeclaringType != null
                    && o.MethodDeclaration.DeclaringType.FullName == "System.Collections.IEnumerator") return m;
        return null;
    }

    static bool IsIterator(TypeDef c)
    {
        if (c.IsValueType || c.IsInterface) return false;
        bool en = false;
        foreach (var i in c.Interfaces) if (i.Interface != null && i.Interface.FullName == "System.Collections.IEnumerator") en = true;
        if (!en || MoveNextOf(c) == null) return false;
        MethodDef ctor = OnlyCtor(c);
        return ctor != null && ctor.MethodSig.Params.Count == 1 && ctor.MethodSig.Params[0].ElementType == ElementType.I4;
    }

    // True when 'user' may hold or create generated type 'c': the declaring type itself or
    // another generated type nested in it.
    bool InFamily(TypeDef user, TypeDef c)
    {
        if (user == c.DeclaringType) return true;
        return user.DeclaringType == c.DeclaringType && kinds.ContainsKey(user);
    }

    void Classify()
    {
        foreach (var c in mod.GetTypes())
        {
            if (!c.IsNested) continue;
            Kind k = Kind.None;
            if (IsStaticLambdas(c)) k = Kind.StaticLambdas;
            else if (IsIterator(c)) k = Kind.Iterator;
            else if (IsDisplay(c)) k = Kind.Display;
            else if (IsStructClosure(c)) k = Kind.StructClosure;
            if (k != Kind.None) kinds[c] = k;
        }
        // Closures without lambdas: a field of a closure of the same type links to them.
        bool added = true;
        while (added)
        {
            added = false;
            foreach (var c in mod.GetTypes())
            {
                if (!c.IsNested || kinds.ContainsKey(c) || !IsDisplay(c, false)) continue;
                bool linked = false;
                foreach (var f in Get(fieldSigRefs, c))
                {
                    Kind fk;
                    if (f.DeclaringType.DeclaringType == c.DeclaringType && kinds.TryGetValue(f.DeclaringType, out fk) && fk == Kind.Display && Leaf(f.FieldSig.Type) == c) linked = true;
                }
                if (linked) { kinds[c] = Kind.Display; added = true; }
            }
        }
        // A closure only ever appears inside the family of its declaring type.
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var c in new List<TypeDef>(kinds.Keys))
            {
                Kind k = kinds[c];
                bool ok = true;
                if (k == Kind.Display || k == Kind.StructClosure || k == Kind.Iterator)
                {
                    foreach (var f in Get(fieldSigRefs, c))
                        if (f.DeclaringType != c && !(kinds.ContainsKey(f.DeclaringType) && f.DeclaringType.DeclaringType == c.DeclaringType)) ok = false;
                }
                if (k == Kind.Display)
                {
                    foreach (var s in Get(newobjSites, OnlyCtor(c)))
                        if (!InFamily(s.Method.DeclaringType, c)) ok = false;
                    foreach (var m in c.Methods)
                    {
                        foreach (var s in Get(ftnSites, m)) if (s.Method.DeclaringType != c && !InFamily(s.Method.DeclaringType, c)) ok = false;
                        foreach (var s in Get(callSites, m)) if (!m.IsConstructor && s.Method.DeclaringType != c && !InFamily(s.Method.DeclaringType, c)) ok = false;
                    }
                }
                if (k == Kind.StructClosure)
                {
                    foreach (var m in Get(methodSigRefs, c)) if (!InFamily(m.DeclaringType, c)) ok = false;
                    foreach (var m in Get(localUsers, c)) if (!InFamily(m.DeclaringType, c)) ok = false;
                }
                if (k == Kind.StaticLambdas)
                {
                    foreach (var m in c.Methods)
                        foreach (var s in Get(ftnSites, m)) if (s.Method.DeclaringType != c && !InFamily(s.Method.DeclaringType, c)) ok = false;
                }
                if (!ok) { kinds.Remove(c); changed = true; }
            }
        }
    }

    // ---- naming --------------------------------------------------------------------

    // The user-written method a generated member belongs to.
    MethodDef Top(MethodDef m, int depth)
    {
        if (m == null || depth > 24) return null;
        TypeDef t = m.DeclaringType;
        Kind k;
        if (!kinds.TryGetValue(t, out k))
        {
            // A local function of the type itself (it takes a struct closure by reference):
            // its lambdas are named after the method that declares it.
            foreach (var p in m.MethodSig.Params)
            {
                TypeDef c = Leaf(p);
                Kind ck;
                if (c == null || !(p is ByRefSig) || !kinds.TryGetValue(c, out ck) || ck != Kind.StructClosure) continue;
                MethodDef outer = null;
                foreach (var user in Get(localUsers, c))
                {
                    bool param = false;
                    foreach (var q in user.MethodSig.Params) if (Leaf(q) == c) param = true;
                    if (!param && user != m) { outer = Top(user, depth + 1); if (outer != null) break; }
                }
                if (outer != null) return outer;
            }
            return m;
        }
        object key = k == Kind.StaticLambdas ? (object)m : t;
        MethodDef r;
        if (topCache.TryGetValue(key, out r)) return r;
        topCache[key] = null;
        List<Site> sites;
        if (k == Kind.StaticLambdas) sites = Get(ftnSites, m);
        else sites = Get(newobjSites, OnlyCtor(t));
        foreach (var s in sites)
        {
            if (s.Method.DeclaringType == t && k != Kind.StaticLambdas) continue;   // iterator GetEnumerator clones itself
            if (s.Method == m) continue;
            r = Top(s.Method, depth + 1);
            if (r != null) break;
        }
        topCache[key] = r;
        return r;
    }

    MethodDef StructTop(TypeDef c)
    {
        foreach (var m in Get(localUsers, c))
        {
            bool param = false;
            foreach (var p in m.MethodSig.Params) if (Leaf(p) == c) param = true;
            if (param) continue;
            var r = Top(m, 0);
            if (r != null) return r;
        }
        return null;
    }

    void Count(string k) { int n; Counts.TryGetValue(k, out n); Counts[k] = n + 1; }

    void Log(IMDTokenProvider o, string kind, string oldName, string newName)
    {
        Report.Add("0x" + o.MDToken.Raw.ToString("X8") + "\t" + kind + "\t" + oldName + "\t" + newName);
        Count(kind);
    }

    void Mark(IHasCustomAttribute o)
    {
        if (cgCtor == null) return;
        foreach (var ca in o.CustomAttributes)
            if (ca.TypeFullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute") return;
        o.CustomAttributes.Add(new CustomAttribute(cgCtor));
    }

    void Rename(TypeDef t, string name, string kind)
    {
        string old = t.Name;
        int tick = old.IndexOf('`');
        if (tick >= 0) name += old.Substring(tick);
        t.Name = name; Mark(t); Log(t, kind, old, name);
    }
    void Rename(MethodDef m, string name, string kind) { string old = m.Name; m.Name = name; Log(m, kind, old, name); }
    void Rename(FieldDef f, string name, string kind) { string old = f.Name; f.Name = name; Log(f, kind, old, name); }

    sealed class Counters { public int Closures; public int Lambdas; }

    static string Stem(MethodDef top)
    {
        // Roslyn keeps the method name as is; explicit implementations lose their dots' meaning
        // to the decompiler anyway.
        return top.Name.String.Replace('<', '_').Replace('>', '_');
    }

    void NameClosures()
    {
        foreach (var owner in mod.GetTypes())
        {
            if (owner.NestedTypes.Count == 0) continue;
            var counters = new Dictionary<MethodDef, Counters>();
            var taken = new HashSet<string>(StringComparer.Ordinal);
            foreach (var n in owner.NestedTypes) taken.Add(n.Name);
            foreach (var c in owner.NestedTypes)
            {
                Kind k;
                if (!kinds.TryGetValue(c, out k)) continue;
                if (k == Kind.StaticLambdas) { NameStaticLambdas(owner, c, counters, taken); continue; }
                MethodDef top = k == Kind.StructClosure ? StructTop(c) : Top(k == Kind.Iterator ? MoveNextOf(c) : OnlyCtor(c), 0);
                if (top == null || top.DeclaringType != owner) { Log(c, "skipped: no owner method", c.Name, k.ToString()); continue; }
                int mo = owner.Methods.IndexOf(top);
                Counters ct;
                if (!counters.TryGetValue(top, out ct)) { ct = new Counters(); counters[top] = ct; }
                string stem = Stem(top);
                if (k == Kind.Iterator)
                {
                    string name = "<" + stem + ">d__" + mo;
                    if (!taken.Add(name)) { Count("collision Iterator"); continue; }
                    Rename(c, name, "iterator");
                    MethodDef moveNext = MoveNextOf(c);
                    if (moveNext.Name != "MoveNext") Rename(moveNext, "MoveNext", "iterator method");
                    NameIteratorFields(owner, c);
                    // try/finally blocks of the iterator body, run by MoveNext and Dispose.
                    int fin = 0;
                    foreach (var m in c.Methods)
                        if (!m.IsConstructor && !m.IsVirtual && m.MethodSig.Params.Count == 0 && m.MethodSig.RetType.ElementType == ElementType.Void && Placeholder.IsMatch(m.Name))
                            Rename(m, "<>m__Finally" + (++fin), "iterator finally");
                    continue;
                }
                string cname = "<>c__DisplayClass" + mo + "_" + ct.Closures++;
                taken.Add(cname);
                if (k == Kind.Display)
                {
                    Rename(c, cname, "closure");
                    c.IsSealed = true;
                    foreach (var m in c.Methods)
                    {
                        if (m.IsConstructor) continue;
                        int lo = ct.Lambdas++;
                        // Only a local function is ever called directly.
                        if (!callSites.ContainsKey(m)) Rename(m, "<" + stem + ">b__" + lo, "lambda");
                        else Rename(m, "<" + stem + ">g__" + LocalName(m, lo) + "|" + lo, "local function");
                    }
                }
                else
                {
                    Rename(c, cname, "closure struct");
                    foreach (var m in Get(methodSigRefs, c))
                    {
                        if (m.Name.String.StartsWith("<", StringComparison.Ordinal) || m.DeclaringType != owner) continue;
                        int lo = ct.Lambdas++;
                        Rename(m, "<" + stem + ">g__" + LocalName(m, lo) + "|" + mo + "_" + lo, "local function");
                        Mark(m);
                    }
                }
                NameClosureFields(owner, c);
            }
        }
    }

    // Lambdas that capture only 'this' are instance methods of the type itself: nothing in
    // them says so. A closure of the same method does. The compiler merges the closure of a
    // scope into the one of the method when the same lambdas capture both; a closure that
    // holds 'this' and is created inside the method (not at its start, not in a loop) was
    // not merged, so another lambda of the method captures 'this' without its variables.
    // When no other closure does, that lambda is a method of the type, taken by
    // 'ldarg.0; ldftn' in this method and referenced nowhere else.
    void NameThisLambdas()
    {
        foreach (var owner in mod.GetTypes())
        {
            foreach (var c in owner.NestedTypes)
            {
                Kind k;
                if (!kinds.TryGetValue(c, out k) || k != Kind.Display) continue;
                bool holdsThis = false;
                foreach (var f in c.Fields) if (!(f.FieldSig.Type is SZArraySig) && Leaf(f.FieldSig.Type) == owner && f.Name == "<>4__this") holdsThis = true;
                MethodDef ctor = OnlyCtor(c);
                if (!holdsThis || ctor == null) continue;
                var sites = Get(newobjSites, ctor);
                if (sites.Count != 1 || sites[0].Method.DeclaringType != owner || sites[0].Index == 0) continue;
                MethodDef m = sites[0].Method;
                // A constructor creates its closure before the base call and stores 'this' after: no evidence there.
                if (m.IsConstructor) continue;
                var ins = m.Body.Instructions;
                bool looped = false;
                for (int i = sites[0].Index + 1; i < ins.Count; i++)
                {
                    var target = ins[i].Operand as Instruction;
                    if (target != null && ins.IndexOf(target) <= sites[0].Index) looped = true;
                    var targets = ins[i].Operand as Instruction[];
                    if (targets != null) foreach (var t in targets) if (ins.IndexOf(t) <= sites[0].Index) looped = true;
                }
                if (looped) continue;
                // Another closure of the method with 'this' explains it as well.
                bool other = false;
                foreach (var c2 in owner.NestedTypes)
                {
                    Kind k2;
                    if (c2 == c || !kinds.TryGetValue(c2, out k2) || k2 != Kind.Display) continue;
                    MethodDef ctor2 = OnlyCtor(c2);
                    if (ctor2 == null || Top(ctor2, 0) != m) continue;
                    foreach (var f in c2.Fields) if (!(f.FieldSig.Type is SZArraySig) && Leaf(f.FieldSig.Type) == owner) other = true;
                }
                if (other) continue;
                int n = 0, mo = owner.Methods.IndexOf(m);
                for (int i = 1; i < ins.Count; i++)
                {
                    if (ins[i].OpCode.Code != Code.Ldftn || ins[i - 1].OpCode.Code != Code.Ldarg_0) continue;
                    MethodDef x = RM(ins[i].Operand);
                    if (x == null || x.DeclaringType != owner || x.IsStatic || x.IsVirtual || x.IsConstructor || !Placeholder.IsMatch(x.Name)) continue;
                    if (Get(ftnSites, x).Count != 1 || Get(callSites, x).Count != 0 || tokenLoads.Contains(x)) continue;
                    Rename(x, "<" + Stem(m) + ">b__" + mo + "_" + (100 + n++), "lambda (this only)");
                    Mark(x);
                }
            }
        }
    }
    // A local function named in names.tsv keeps that name.
    // No digit at the end of a generated name: the decompiler reads it as a counter and prints
    // 'Local' for 'Local6' when nothing else is called 'Local', and the name no longer pairs.
    static string LocalName(MethodDef m, int ordinal)
    {
        if (!Placeholder.IsMatch(m.Name)) return m.Name.String;
        string letters = "";
        for (int n = ordinal; ; n = n / 26 - 1)
        {
            letters = (char)('A' + n % 26) + letters;
            if (n < 26) break;
        }
        return "Local" + letters;
    }

    void NameStaticLambdas(TypeDef owner, TypeDef c, Dictionary<MethodDef, Counters> counters, HashSet<string> taken)
    {
        // A generic method gets its own <>c__N<T> class; everything else shares <>c.
        bool perMethod = c.GenericParameters.Count > owner.GenericParameters.Count;
        string name = "<>c";
        if (perMethod)
        {
            MethodDef first = null;
            foreach (var m in c.Methods) if (!m.IsConstructor && first == null) first = Top(m, 0);
            if (first == null || first.DeclaringType != owner) { Count("unresolved StaticLambdas"); return; }
            name = "<>c__" + owner.Methods.IndexOf(first);
        }
        string full = name;
        int tick = c.Name.String.IndexOf('`');
        if (tick >= 0) full += c.Name.String.Substring(tick);
        if (full != c.Name.String && !taken.Add(full)) { Log(c, "skipped: second <>c in type", c.Name, full); return; }
        Rename(c, name, "static lambdas");
        c.IsSealed = true;
        foreach (var f in c.Fields)
            if (Leaf(f.FieldSig.Type) == c && (f.FieldSig.Type is ClassSig || f.FieldSig.Type is GenericInstSig)) Rename(f, "<>9", "static lambdas instance");
        int orphan = 0;
        foreach (var m in c.Methods)
        {
            if (m.IsConstructor) continue;
            MethodDef top = Top(m, 0);
            string suffix, stem;
            if (top == null || top.DeclaringType != owner) { stem = "_"; suffix = "x_" + orphan++; Count("orphan lambda"); }
            else
            {
                Counters ct;
                if (!counters.TryGetValue(top, out ct)) { ct = new Counters(); counters[top] = ct; }
                stem = Stem(top); suffix = owner.Methods.IndexOf(top) + "_" + ct.Lambdas++;
            }
            // ldftn m; newobj; dup; stsfld cache
            foreach (var s in Get(ftnSites, m))
            {
                var ins = s.Method.Body.Instructions;
                for (int j = s.Index + 1; j < ins.Count && j <= s.Index + 4; j++)
                {
                    if (ins[j].OpCode.Code != Code.Stsfld) continue;
                    FieldDef cache = RF(ins[j].Operand);
                    if (cache != null && cache.DeclaringType == c && !cache.Name.String.StartsWith("<>9", StringComparison.Ordinal))
                        Rename(cache, "<>9__" + suffix, "lambda cache");
                    break;
                }
            }
            Rename(m, "<" + stem + ">b__" + suffix, "lambda");
        }
    }

    // <>4__this and the links to enclosing closures. Captured variables keep their names.
    void NameClosureFields(TypeDef owner, TypeDef c)
    {
        bool hasThis = false; int parents = 0;
        foreach (var f in c.Fields)
        {
            TypeDef ft = Leaf(f.FieldSig.Type);
            if (ft == null || f.FieldSig.Type is SZArraySig) continue;
            Kind fk;
            if (ft != c && ft.DeclaringType == owner && kinds.TryGetValue(ft, out fk) && (fk == Kind.Display || fk == Kind.StructClosure))
            {
                if (StoredFromThis(f, ft)) Rename(f, "CS$<>8__locals" + (++parents), "closure parent");
                continue;
            }
            if (ft == owner && !hasThis && StoredFromThis(f, owner)) { hasThis = true; Rename(f, "<>4__this", "closure this"); }
        }
    }

    // True when some instance method of 'from' stores its own 'this' into f.
    bool StoredFromThis(FieldDef f, TypeDef from)
    {
        foreach (var s in Get(fieldSites, f))
        {
            var ins = s.Method.Body.Instructions;
            if (ins[s.Index].OpCode.Code != Code.Stfld || s.Index == 0) continue;
            if (s.Method.DeclaringType == from && !s.Method.IsStatic && IsLdarg(ins[s.Index - 1], 0)) return true;
        }
        return false;
    }

    void NameIteratorFields(TypeDef owner, TypeDef c)
    {
        FieldDef state = null;
        var code = Code_(OnlyCtor(c));
        for (int i = 1; i < code.Count; i++)
            if (code[i].OpCode.Code == Code.Stfld && IsLdarg(code[i - 1], 1)) state = RF(code[i].Operand);
        if (state != null) Rename(state, "<>1__state", "iterator field");
        FieldDef current = null;
        foreach (var m in c.Methods)
        {
            if (!m.Name.String.EndsWith("get_Current", StringComparison.Ordinal)) continue;
            foreach (var i in Code_(m)) if (i.OpCode.Code == Code.Ldfld && current == null) current = RF(i.Operand);
        }
        if (current != null && current != state) Rename(current, "<>2__current", "iterator field");
        foreach (var i in code)
        {
            if (i.OpCode.Code != Code.Stfld) continue;
            FieldDef f = RF(i.Operand);
            if (f != null && f != state && f.FieldSig.Type.ElementType == ElementType.I4) Rename(f, "<>l__initialThreadId", "iterator field");
        }
        foreach (var f in c.Fields)
            if (Leaf(f.FieldSig.Type) == owner && !(f.FieldSig.Type is SZArraySig) && StoredFromThis(f, owner)) { Rename(f, "<>4__this", "iterator field"); break; }
    }

    // ---- auto-properties -------------------------------------------------------------

    // A private field touched only by one trivial getter, at most one trivial setter and
    // constructor stores compiles to the same IL as an auto-property. The getter must
    // already belong to a property: that gives the name.
    void NameAutoProperties()
    {
        foreach (var t in mod.GetTypes())
        {
            if (kinds.ContainsKey(t) || t.IsEnum || t.Properties.Count == 0) continue;
            var getterOf = new Dictionary<MethodDef, PropertyDef>();
            var setters = new HashSet<MethodDef>();
            foreach (var p in t.Properties)
            {
                if (p.GetMethod != null && p.PropertySig.Params.Count == 0) getterOf[p.GetMethod] = p;
                foreach (var s in p.SetMethods) setters.Add(s);
            }
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in t.Methods) names.Add(m.Name);
            foreach (var f in t.Fields)
            {
                if (!f.IsPrivate || f.IsLiteral || f.HasFieldRVA || tokenLoads.Contains(f)) continue;
                MethodDef getter = null, setter = null;
                bool ok = true, lateStore = false;
                foreach (var s in Get(fieldSites, f))
                {
                    MethodDef m = s.Method;
                    Code op = m.Body.Instructions[s.Index].OpCode.Code;
                    if (m.DeclaringType != t) { ok = false; break; }
                    if (IsGetter(m, f)) { if (getter != null && getter != m) { ok = false; break; } getter = m; continue; }
                    if (IsSetter(m, f)) { if (setter != null && setter != m) { ok = false; break; } setter = m; continue; }
                    if (m.IsConstructor && m.IsStatic == f.IsStatic && (op == Code.Stfld || op == Code.Stsfld))
                    {
                        // An initializer is stored before the base constructor is called. A
                        // store after that call is written in the body, where an auto-property
                        // that has a setter is assigned through it.
                        if (!f.IsStatic)
                        {
                            bool after = t.IsValueType;
                            for (int k = 0; k < s.Index && !after; k++)
                            {
                                var call = m.Body.Instructions[k];
                                var callee = call.Operand as IMethod;
                                if (call.OpCode.Code == Code.Call && callee != null && callee.Name == ".ctor") after = true;
                            }
                            if (after) lateStore = true;
                        }
                        continue;
                    }
                    ok = false; break;
                }
                PropertyDef prop;
                if (!ok || getter == null || !getterOf.TryGetValue(getter, out prop)) continue;
                if (lateStore && setter != null && !f.Name.String.EndsWith(">k__BackingField", StringComparison.Ordinal)) continue;
                // The field of an auto-property has the type of the property: a getter that returns
                // a field of a derived type is written by hand.
                if (f.FieldType.FullName != getter.MethodSig.RetType.FullName) continue;
                string x = prop.Name;
                bool attach = false;
                if (setter != null && prop.SetMethod != setter)
                {
                    // A private setter lost its name: it can take it back when nothing else claims it.
                    if (prop.SetMethod != null || setters.Contains(setter) || setter.IsVirtual || !Placeholder.IsMatch(setter.Name) || names.Contains("set_" + x)) continue;
                    attach = true;
                }
                string want = "<" + x + ">k__BackingField";
                if (f.Name != want)
                {
                    if (t.FindField(want) != null) continue;
                    Rename(f, want, "backing field");
                }
                else Count("backing field (name kept)");
                Mark(f); Mark(getter);
                if (attach)
                {
                    Rename(setter, "set_" + x, "auto-property setter");
                    names.Add("set_" + x);
                    setter.IsSpecialName = true;
                    prop.SetMethods.Add(setter);
                    setters.Add(setter);
                }
                if (prop.SetMethod != null) Mark(prop.SetMethod);
            }
        }
    }

    bool IsGetter(MethodDef m, FieldDef f)
    {
        if (m.IsConstructor || m.MethodSig.Params.Count != 0 || m.IsStatic != f.IsStatic) return false;
        var c = Code_(m);
        if (f.IsStatic) return c.Count == 2 && c[0].OpCode.Code == Code.Ldsfld && c[1].OpCode.Code == Code.Ret;
        return c.Count == 3 && IsLdarg(c[0], 0) && c[1].OpCode.Code == Code.Ldfld && c[2].OpCode.Code == Code.Ret;
    }

    bool IsSetter(MethodDef m, FieldDef f)
    {
        if (m.IsConstructor || m.MethodSig.Params.Count != 1 || m.IsStatic != f.IsStatic) return false;
        if (m.MethodSig.RetType.ElementType != ElementType.Void) return false;
        var c = Code_(m);
        if (f.IsStatic) return c.Count == 3 && IsLdarg(c[0], 0) && c[1].OpCode.Code == Code.Stsfld && c[2].OpCode.Code == Code.Ret;
        return c.Count == 4 && IsLdarg(c[0], 0) && IsLdarg(c[1], 1) && c[2].OpCode.Code == Code.Stfld && c[3].OpCode.Code == Code.Ret;
    }
}
