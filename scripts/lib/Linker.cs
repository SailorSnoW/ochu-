// Links the compilation units into the main build: one module with every method body.
//
// The main build compiles the whole source tree, so it declares every type and member. A
// unit (docs/units.tsv) only gives other bodies for some of them: the linker puts the body
// of each unit method in place of the main build's body of the same method. Members are
// paired by rendered name (IlMatch's Namer, the pairing of scripts/match.ps1), and every
// operand of a moved body is re-pointed, by name, at the definition of the main build.
//
//   - a unit that gives whole types: every method with a body of those types;
//   - the unit 'il': the methods its module defines. One the main build does not declare
//     is added ('<Module>::.cctor'). One that implements an interface method under a plain
//     name takes its '.override' along, and the forwarding explicit implementation the
//     decompiler printed for it in C# goes away.
//
// Compiled by scripts/link.ps1 with Add-Type next to IlMatch.cs (Windows PowerShell 5.1: C# 5 only).
using System;
using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

public sealed class Linker
{
    readonly ModuleDefMD main;
    readonly Namer mainNamer;
    readonly Importer importer;
    readonly Dictionary<string, TypeDef> types = new Dictionary<string, TypeDef>(StringComparer.Ordinal);
    readonly Dictionary<string, MethodDef> methods = new Dictionary<string, MethodDef>(StringComparer.Ordinal);
    readonly Dictionary<string, FieldDef> fields = new Dictionary<string, FieldDef>(StringComparer.Ordinal);
    public readonly List<string> Log = new List<string>();
    public readonly List<string> Problems = new List<string>();

    public Linker(ModuleDefMD main)
    {
        this.main = main;
        mainNamer = new Namer(null, main);
        importer = new Importer(main);
        foreach (var t in main.GetTypes())
        {
            Put(types, mainNamer.Type(t), t);
            foreach (var m in t.Methods) Put(methods, mainNamer.Method(m), m);
            foreach (var f in t.Fields) Put(fields, mainNamer.Field(f), f);
        }
    }

    static void Put<T>(Dictionary<string, T> d, string key, T value) { if (!d.ContainsKey(key)) d[key] = value; }

    static string TopLevel(TypeDef t)
    {
        while (t.DeclaringType != null) t = t.DeclaringType;
        return t.FullName;
    }

    // One unit being linked: its module, and how its references read.
    sealed class Unit
    {
        public string Name; public ModuleDefMD Module; public Namer Namer;
    }

    Unit Open(string name, ModuleDefMD module)
    {
        var u = new Unit { Name = name, Module = module, Namer = new Namer(null, module) };
        u.Namer.Host = mainNamer;
        return u;
    }

    bool IsMainAssembly(TypeRef tr)
    {
        IResolutionScope scope = tr.ResolutionScope;
        while (scope is TypeRef) scope = ((TypeRef)scope).ResolutionScope;
        var ar = scope as AssemblyRef;
        return ar != null && main.Assembly != null && ar.Name == main.Assembly.Name;
    }

    ITypeDefOrRef MapType(Unit u, ITypeDefOrRef t, string where)
    {
        if (t == null) return null;
        var ts = t as TypeSpec;
        if (ts != null) return new TypeSpecUser(MapSig(u, ts.TypeSig, where));
        var td = t as TypeDef;
        var tr = t as TypeRef;
        if (td != null || (tr != null && IsMainAssembly(tr)))
        {
            TypeDef found;
            string key = u.Namer.Type(t);
            if (types.TryGetValue(key, out found)) return found;
            Problems.Add(u.Name + ": type '" + key + "' is not in the main build (" + where + ")");
            return null;
        }
        return (ITypeDefOrRef)importer.Import((IType)tr);
    }

    TypeSig MapSig(Unit u, TypeSig s, string where)
    {
        if (s == null) return null;
        switch (s.ElementType)
        {
            case ElementType.Class: return new ClassSig(MapType(u, ((ClassSig)s).TypeDefOrRef, where));
            case ElementType.ValueType: return new ValueTypeSig(MapType(u, ((ValueTypeSig)s).TypeDefOrRef, where));
            case ElementType.GenericInst:
                {
                    var gi = (GenericInstSig)s;
                    var r = new GenericInstSig((ClassOrValueTypeSig)MapSig(u, gi.GenericType, where));
                    foreach (var a in gi.GenericArguments) r.GenericArguments.Add(MapSig(u, a, where));
                    return r;
                }
            case ElementType.SZArray: return new SZArraySig(MapSig(u, s.Next, where));
            case ElementType.Array:
                {
                    var a = (ArraySig)s;
                    return new ArraySig(MapSig(u, s.Next, where), a.Rank, new List<uint>(a.Sizes), new List<int>(a.LowerBounds));
                }
            case ElementType.ByRef: return new ByRefSig(MapSig(u, s.Next, where));
            case ElementType.Ptr: return new PtrSig(MapSig(u, s.Next, where));
            case ElementType.Pinned: return new PinnedSig(MapSig(u, s.Next, where));
            case ElementType.CModReqd: return new CModReqdSig(MapType(u, ((ModifierSig)s).Modifier, where), MapSig(u, s.Next, where));
            case ElementType.CModOpt: return new CModOptSig(MapType(u, ((ModifierSig)s).Modifier, where), MapSig(u, s.Next, where));
            case ElementType.Var: return new GenericVar(((GenericSig)s).Number);
            case ElementType.MVar: return new GenericMVar(((GenericSig)s).Number);
            case ElementType.FnPtr: return new FnPtrSig(MapSig(u, ((FnPtrSig)s).MethodSig, where));
            case ElementType.Sentinel: return new SentinelSig();
            default:
                {
                    var c = s as CorLibTypeSig;
                    if (c != null)
                    {
                        var r = main.CorLibTypes.GetCorLibTypeSig(c.TypeDefOrRef);
                        if (r != null) return r;
                    }
                    throw new NotSupportedException("signature element " + s.ElementType);
                }
        }
    }

    MethodSig MapSig(Unit u, MethodSig s, string where)
    {
        if (s == null) return null;
        var r = new MethodSig(s.CallingConvention, s.GenParamCount, MapSig(u, s.RetType, where));
        foreach (var p in s.Params) r.Params.Add(MapSig(u, p, where));
        if (s.ParamsAfterSentinel != null)
        {
            r.ParamsAfterSentinel = new List<TypeSig>();
            foreach (var p in s.ParamsAfterSentinel) r.ParamsAfterSentinel.Add(MapSig(u, p, where));
        }
        return r;
    }

    IMethod MapMethod(Unit u, IMethod m, string where)
    {
        var ms = m as MethodSpec;
        if (ms != null)
        {
            var inner = MapMethod(u, ms.Method, where) as IMethodDefOrRef;
            if (inner == null) return null;
            var inst = new GenericInstMethodSig();
            foreach (var a in ms.GenericInstMethodSig.GenericArguments) inst.GenericArguments.Add(MapSig(u, a, where));
            return new MethodSpecUser(inner, inst);
        }
        string key = u.Namer.Method(m);
        MethodDef found;
        var md = m as MethodDef;
        if (md != null)
        {
            if (methods.TryGetValue(key, out found)) return found;
            Problems.Add(u.Name + ": method '" + key + "' is not in the main build (" + where + ")");
            return null;
        }
        var mr = (MemberRef)m;
        var parent = mr.Class as ITypeDefOrRef;
        if (parent == null) { Problems.Add(u.Name + ": reference '" + key + "' has a parent that is no type (" + where + ")"); return null; }
        var mapped = MapType(u, parent, where);
        if (mapped == null) return null;
        var owner = mapped as TypeDef;
        if (owner != null)
        {
            if (methods.TryGetValue(key, out found)) return found;
            found = owner.FindMethod(mr.Name, MapSig(u, mr.MethodSig, where));
            if (found != null) return found;
            Problems.Add(u.Name + ": method '" + key + "' is not in the main build (" + where + ")");
            return null;
        }
        return new MemberRefUser(main, mr.Name, MapSig(u, mr.MethodSig, where), mapped);
    }

    IField MapField(Unit u, IField f, string where)
    {
        string key = u.Namer.Field(f);
        FieldDef found;
        var fd = f as FieldDef;
        if (fd != null)
        {
            if (fields.TryGetValue(key, out found)) return found;
            Problems.Add(u.Name + ": field '" + key + "' is not in the main build (" + where + ")");
            return null;
        }
        var mr = (MemberRef)f;
        var mapped = MapType(u, mr.Class as ITypeDefOrRef, where);
        if (mapped == null) return null;
        var owner = mapped as TypeDef;
        if (owner != null)
        {
            if (fields.TryGetValue(key, out found)) return found;
            found = owner.FindField(mr.Name);
            if (found != null) return found;
            Problems.Add(u.Name + ": field '" + key + "' is not in the main build (" + where + ")");
            return null;
        }
        return new MemberRefUser(main, mr.Name, new FieldSig(MapSig(u, mr.FieldSig.Type, where)), mapped);
    }

    CilBody Copy(Unit u, MethodDef sm, MethodDef tm)
    {
        string where = u.Namer.Method(sm);
        var sb = sm.Body;
        var body = new CilBody(sb.InitLocals, new List<Instruction>(), new List<ExceptionHandler>(), new List<Local>());
        body.MaxStack = sb.MaxStack;
        body.KeepOldMaxStack = true;
        var locals = new Dictionary<Local, Local>();
        foreach (var l in sb.Variables)
        {
            var n = new Local(MapSig(u, l.Type, where));
            body.Variables.Add(n);
            locals[l] = n;
        }
        var map = new Dictionary<Instruction, Instruction>();
        foreach (var i in sb.Instructions)
        {
            var n = new Instruction(i.OpCode, null);
            map[i] = n;
            body.Instructions.Add(n);
        }
        for (int k = 0; k < sb.Instructions.Count; k++)
        {
            object op = sb.Instructions[k].Operand;
            object mapped;
            var one = op as Instruction;
            var many = op as IList<Instruction>;
            var local = op as Local;
            var par = op as Parameter;
            if (op == null) mapped = null;
            else if (one != null) mapped = map[one];
            else if (many != null)
            {
                var arr = new Instruction[many.Count];
                for (int j = 0; j < arr.Length; j++) arr[j] = map[many[j]];
                mapped = arr;
            }
            else if (local != null) mapped = locals[local];
            else if (par != null) mapped = tm.Parameters[par.Index];
            else if (op is ITypeDefOrRef) mapped = MapType(u, (ITypeDefOrRef)op, where);
            else if (op is IField && ((IField)op).IsField) mapped = MapField(u, (IField)op, where);
            else if (op is IMethod && ((IMethod)op).IsMethod) mapped = MapMethod(u, (IMethod)op, where);
            else if (op is MethodSig) mapped = MapSig(u, (MethodSig)op, where);
            else mapped = op;   // constants, strings
            body.Instructions[k].Operand = mapped;
        }
        foreach (var eh in sb.ExceptionHandlers)
        {
            var n = new ExceptionHandler(eh.HandlerType);
            n.TryStart = eh.TryStart == null ? null : map[eh.TryStart];
            n.TryEnd = eh.TryEnd == null ? null : map[eh.TryEnd];
            n.HandlerStart = eh.HandlerStart == null ? null : map[eh.HandlerStart];
            n.HandlerEnd = eh.HandlerEnd == null ? null : map[eh.HandlerEnd];
            n.FilterStart = eh.FilterStart == null ? null : map[eh.FilterStart];
            n.CatchType = eh.CatchType == null ? null : MapType(u, eh.CatchType, where);
            body.ExceptionHandlers.Add(n);
        }
        return body;
    }

    // A unit that gives whole types: every body of those types comes from it.
    public int LinkTypes(string name, ModuleDefMD module, ICollection<string> topLevelTypes)
    {
        var u = Open(name, module);
        var given = new HashSet<string>(StringComparer.Ordinal);
        int moved = 0;
        foreach (var t in module.GetTypes())
        {
            if (!topLevelTypes.Contains(TopLevel(t))) continue;
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                string key = u.Namer.Method(m);
                MethodDef target;
                if (!methods.TryGetValue(key, out target) || !target.HasBody) { Problems.Add(name + ": no method '" + key + "' with a body in the main build"); continue; }
                int before = Problems.Count;
                var body = Copy(u, m, target);
                if (Problems.Count != before) continue;
                target.Body = body;
                given.Add(key);
                moved++;
            }
        }
        foreach (var t in main.GetTypes())
        {
            if (!topLevelTypes.Contains(TopLevel(t))) continue;
            foreach (var m in t.Methods)
                if (m.HasBody && !given.Contains(mainNamer.Method(m))) Problems.Add(name + ": the unit has no body for '" + mainNamer.Method(m) + "'");
        }
        Log.Add(name + "\t" + moved + " bodies");
        return moved;
    }

    // The unit 'il': each method its module defines.
    public int LinkMethods(string name, ModuleDefMD module)
    {
        var u = Open(name, module);
        int moved = 0;
        foreach (var t in module.GetTypes())
        {
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                string where = u.Namer.Method(m);
                TypeDef owner;
                if (t.IsGlobalModuleType) owner = main.GlobalType;
                else if (!types.TryGetValue(u.Namer.Type(t), out owner)) { Problems.Add(name + ": no type '" + u.Namer.Type(t) + "' in the main build"); continue; }
                var sig = MapSig(u, m.MethodSig, where);
                MethodDef target = owner.FindMethod(m.Name, sig);
                if (target == null)
                {
                    target = new MethodDefUser(m.Name, sig, m.ImplAttributes, m.Attributes);
                    owner.Methods.Add(target);
                    Log.Add(name + "\tadded " + where);
                }
                target.Attributes = m.Attributes;
                target.ImplAttributes = m.ImplAttributes;
                int before = Problems.Count;
                var body = Copy(u, m, target);
                var overrides = new List<MethodOverride>();
                foreach (var o in m.Overrides)
                {
                    var decl = MapMethod(u, o.MethodDeclaration, where) as IMethodDefOrRef;
                    if (decl != null) overrides.Add(new MethodOverride(target, decl));
                }
                if (Problems.Count != before) continue;
                target.Body = body;
                moved++;
                if (overrides.Count == 0) continue;
                target.Overrides.Clear();
                foreach (var o in overrides) target.Overrides.Add(o);
                // Another method of the type that implements the same declaration: the
                // forwarder the decompiler wrote in C#. One implementation is enough.
                var declared = new HashSet<string>(StringComparer.Ordinal);
                foreach (var o in overrides) declared.Add(mainNamer.Method(o.MethodDeclaration));
                foreach (var other in new List<MethodDef>(owner.Methods))
                {
                    if (other == target) continue;
                    bool same = false;
                    foreach (var o in other.Overrides) if (declared.Contains(mainNamer.Method(o.MethodDeclaration))) same = true;
                    if (!same) continue;
                    string gone = mainNamer.Method(other);
                    owner.Methods.Remove(other);
                    Log.Add(name + "\tremoved the forwarder " + gone);
                }
            }
        }
        Log.Add(name + "\t" + moved + " bodies");
        return moved;
    }

    // A resource the compiler could not embed under its name (a name made of Unicode spaces
    // is no name for MSBuild): it was embedded under a stand-in, and gets its name here.
    public void RenameResource(string from, string to)
    {
        foreach (var r in main.Resources)
            if (r.Name == from) { r.Name = to; Log.Add("resource\tnamed (" + to.Length + " characters) in place of '" + from + "'"); return; }
        Problems.Add("no resource '" + from + "' to rename");
    }
}
