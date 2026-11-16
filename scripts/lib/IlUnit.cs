// Builds the module that carries the methods of the unit 'il' (docs/units.tsv): the methods
// of the reference that no C# compiler of the project gives back, kept as IL.
//
// The module declares the types of those methods (their nesting and nothing else) and the
// methods themselves, with the bodies of the names stage. Everything a body mentions in the
// program becomes a reference to the assembly of the main build, by name. The decompiler's
// disassembler then prints the module as IL source (src/il/unit.il), which ilasm assembles
// during the build (scripts/build.ps1).
// Compiled by scripts/make-il-unit.ps1 with Add-Type (Windows PowerShell 5.1: C# 5 only).
using System;
using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

public static class IlUnit
{
    public static List<string> Build(ModuleDefMD source, IList<uint> tokens, string path)
    {
        var report = new List<string>();
        var module = new ModuleDefUser("il.dll", Guid.Empty, source.CorLibTypes.AssemblyRef);
        module.Kind = ModuleKind.Dll;
        module.RuntimeVersion = source.RuntimeVersion;
        var assembly = new AssemblyDefUser("il", new Version(0, 0, 0, 0));
        assembly.Modules.Add(module);
        var global = new TypeDefUser(UTF8String.Empty, "<Module>", null);
        module.Types.Add(global);
        var importer = new Importer(module);
        var types = new Dictionary<TypeDef, TypeDef>();
        types[source.GlobalType] = global;

        foreach (uint token in tokens)
        {
            var sm = source.ResolveToken(token) as MethodDef;
            if (sm == null || !sm.HasBody) throw new InvalidOperationException("no method with a body at 0x" + token.ToString("X8"));
            TypeDef host = Host(sm.DeclaringType, module, importer, types);
            var m = new MethodDefUser(sm.Name, importer.Import(sm.MethodSig), sm.ImplAttributes, sm.Attributes);
            foreach (var gp in sm.GenericParameters) m.GenericParameters.Add(new GenericParamUser(gp.Number, gp.Flags, gp.Name));
            foreach (var pd in sm.ParamDefs) m.ParamDefs.Add(new ParamDefUser(pd.Name, pd.Sequence, pd.Attributes));
            host.Methods.Add(m);
            foreach (var o in sm.Overrides)
                m.Overrides.Add(new MethodOverride(m, (IMethodDefOrRef)importer.Import(o.MethodDeclaration)));
            m.Body = Copy(sm, m, importer);
            report.Add("0x" + token.ToString("X8") + "\t" + sm.FullName);
        }
        var options = new dnlib.DotNet.Writer.ModuleWriterOptions(module);
        options.Logger = DummyLogger.NoThrowInstance;
        module.Write(path, options);
        return report;
    }

    // The type of the unit's module that stands for a type of the program: same name, same
    // nesting, no member of its own.
    static TypeDef Host(TypeDef st, ModuleDef module, Importer importer, Dictionary<TypeDef, TypeDef> types)
    {
        TypeDef t;
        if (types.TryGetValue(st, out t)) return t;
        t = new TypeDefUser(st.Namespace, st.Name, st.BaseType == null ? null : (ITypeDefOrRef)importer.Import((IType)st.BaseType));
        t.Attributes = st.Attributes;
        foreach (var gp in st.GenericParameters) t.GenericParameters.Add(new GenericParamUser(gp.Number, gp.Flags, gp.Name));
        if (st.DeclaringType != null) Host(st.DeclaringType, module, importer, types).NestedTypes.Add(t);
        else module.Types.Add(t);
        types[st] = t;
        return t;
    }

    static CilBody Copy(MethodDef sm, MethodDef m, Importer importer)
    {
        var sb = sm.Body;
        var body = new CilBody(sb.InitLocals, new List<Instruction>(), new List<ExceptionHandler>(), new List<Local>());
        body.MaxStack = sb.MaxStack;
        body.KeepOldMaxStack = true;
        var locals = new Dictionary<Local, Local>();
        foreach (var l in sb.Variables)
        {
            var n = new Local(importer.Import(l.Type));
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
            else if (par != null) mapped = m.Parameters[par.Index];
            else if (op is ITypeDefOrRef) mapped = importer.Import((IType)op);
            else if (op is IField && ((IField)op).IsField) mapped = importer.Import((IField)op);
            else if (op is IMethod && ((IMethod)op).IsMethod) mapped = importer.Import((IMethod)op);
            else if (op is MethodSig) mapped = importer.Import((MethodSig)op);
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
            n.CatchType = eh.CatchType == null ? null : (ITypeDefOrRef)importer.Import((IType)eh.CatchType);
            body.ExceptionHandlers.Add(n);
        }
        return body;
    }
}
