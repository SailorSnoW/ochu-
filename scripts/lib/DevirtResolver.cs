// Resolves the references of the VM stream against the module the methods come from.
// A reference id is a position in the stream (VirtualMachine.ReadVmToken): kind 1 is a metadata
// token, otherwise a descriptor (ReadTokenData) that names the member (docs/pipeline.md).
// C# 5: compiled by Add-Type together with Devirt.cs.
using System;
using System.Collections.Generic;
using dnlib.DotNet;

public sealed class VmResolver
{
    private readonly ModuleDefMD module;
    private readonly VmStreamFile file;
    private readonly Dictionary<string, TypeDef> ownTypes = new Dictionary<string, TypeDef>(StringComparer.Ordinal);
    private readonly Dictionary<string, TypeRef> typeRefs = new Dictionary<string, TypeRef>(StringComparer.Ordinal);
    private readonly Dictionary<string, AssemblyRef> assemblyRefs = new Dictionary<string, AssemblyRef>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, object> cache = new Dictionary<int, object>();

    public VmResolver(ModuleDefMD module, VmStreamFile file)
    {
        this.module = module;
        this.file = file;
        foreach (TypeDef t in module.GetTypes()) ownTypes[t.ReflectionFullName] = t;
        foreach (AssemblyRef a in module.GetAssemblyRefs()) assemblyRefs[a.Name] = a;
        foreach (TypeRef t in module.GetTypeRefs())
        {
            IAssembly scope = t.DefinitionAssembly;
            if (scope != null) typeRefs[t.ReflectionFullName + ", " + scope.Name] = t;
        }
    }

    private Dictionary<string, MemberRef> memberRefs;
    private Dictionary<string, MethodSpec> methodSpecs;
    private Dictionary<string, TypeSpec> typeSpecs;
    private Dictionary<string, TypeRef> typeRefRows;

    // The row the module already has for a reference made here (a member of another
    // assembly, a generic instance), or the reference itself when it has none. The code
    // written back then names its members with the tokens the rest of the module uses:
    // scripts/lib/UndoInlining.cs tells the copies of a pasted method by them.
    public object Existing(object reference)
    {
        if (memberRefs == null)
        {
            memberRefs = new Dictionary<string, MemberRef>(StringComparer.Ordinal);
            methodSpecs = new Dictionary<string, MethodSpec>(StringComparer.Ordinal);
            typeSpecs = new Dictionary<string, TypeSpec>(StringComparer.Ordinal);
            typeRefRows = new Dictionary<string, TypeRef>(StringComparer.Ordinal);
            for (uint rid = 1; rid <= module.TablesStream.TypeRefTable.Rows; rid++)
            {
                TypeRef row = module.ResolveTypeRef(rid);
                IAssembly scope = row == null ? null : row.DefinitionAssembly;
                if (scope == null) continue;
                string key = row.ReflectionFullName + ", " + scope.Name;
                if (!typeRefRows.ContainsKey(key)) typeRefRows[key] = row;
            }
            for (uint rid = 1; rid <= module.TablesStream.MemberRefTable.Rows; rid++)
            {
                MemberRef row = module.ResolveMemberRef(rid);
                if (row != null && !memberRefs.ContainsKey(Key(row))) memberRefs[Key(row)] = row;
            }
            for (uint rid = 1; rid <= module.TablesStream.MethodSpecTable.Rows; rid++)
            {
                MethodSpec row = module.ResolveMethodSpec(rid);
                if (row != null && !methodSpecs.ContainsKey(row.FullName)) methodSpecs[row.FullName] = row;
            }
            for (uint rid = 1; rid <= module.TablesStream.TypeSpecTable.Rows; rid++)
            {
                TypeSpec row = module.ResolveTypeSpec(rid);
                if (row != null && !typeSpecs.ContainsKey(row.FullName)) typeSpecs[row.FullName] = row;
            }
        }
        MemberRef member = reference as MemberRef;
        if (member != null && member.Rid == 0)
        {
            MemberRef found;
            return memberRefs.TryGetValue(Key(member), out found) ? found : reference;
        }
        MethodSpec spec = reference as MethodSpec;
        if (spec != null && spec.Rid == 0)
        {
            MethodSpec found;
            return methodSpecs.TryGetValue(spec.FullName, out found) ? found : reference;
        }
        TypeSpec type = reference as TypeSpec;
        if (type != null && type.Rid == 0)
        {
            TypeSpec found;
            return typeSpecs.TryGetValue(type.FullName, out found) ? found : reference;
        }
        // A type of the core library (System.Byte in 'newarr'): the signature has a
        // reference of its own making, not the row of the module.
        TypeRef named = reference as TypeRef;
        if (named != null)
        {
            IAssembly scope = named.DefinitionAssembly;
            TypeRef found;
            if (scope != null && typeRefRows.TryGetValue(named.ReflectionFullName + ", " + scope.Name, out found)) return found;
        }
        return reference;
    }

    private static string Key(MemberRef member)
    {
        IAssembly scope = member.DeclaringType == null ? null : member.DeclaringType.DefinitionAssembly;
        return (member.IsFieldRef ? "field " : "method ") + member.FullName + " @" + (scope == null ? "" : scope.Name.String);
    }

    private sealed class RefFinder : IAssemblyRefFinder
    {
        private readonly ModuleDefMD module;
        public RefFinder(ModuleDefMD module) { this.module = module; }
        public AssemblyRef FindAssemblyRef(TypeRef nonNestedTypeRef) { return module.CorLibTypes.AssemblyRef; }
    }

    // What a reference is: a TypeSig, an IMethod, an IField or a string.
    public object Resolve(int id)
    {
        object found;
        if (cache.TryGetValue(id, out found)) return found;
        VmReader r = file.At(id);
        byte kind = r.ReadByte();
        if (kind == 1)
        {
            uint token = unchecked((uint)r.ReadInt32());
            found = module.ResolveToken(token);
            if (found == null) throw new FormatException(string.Format("token 0x{0:X8} does not resolve (reference {1})", token, id));
            ITypeDefOrRef type = found as ITypeDefOrRef;
            if (type != null) found = type.ToTypeSig();
        }
        else
        {
            byte sub = r.ReadByte();
            switch (sub)
            {
                case 4: found = ReadType(r, id); break;
                case 0: found = ReadMethod(r, id); break;
                case 3: found = ReadField(r, id); break;
                case 2: found = r.ReadString(); break;
                default: throw new FormatException(string.Format("descriptor {0} at {1}", sub, id));
            }
        }
        cache[id] = found;
        return found;
    }

    public TypeSig ResolveType(int id)
    {
        TypeSig sig = Resolve(id) as TypeSig;
        if (sig == null) throw new FormatException("reference " + id + " is not a type");
        return sig;
    }

    public string Describe(int id)
    {
        try
        {
            object o = Resolve(id);
            string s = o as string;
            if (s != null) return "\"" + s + "\"";
            IMDTokenProvider withToken = o as IMDTokenProvider;
            TypeSig sig = o as TypeSig;
            if (sig != null) return "type " + sig.FullName;
            return string.Format("{0} [0x{1:X8}]", o, withToken == null ? 0 : withToken.MDToken.Raw);
        }
        catch (Exception e) { return "!! " + e.Message; }
    }

    private TypeSig ReadType(VmReader r, int id)
    {
        string name = r.ReadString();
        bool generic = r.ReadBoolean();
        bool parameter = r.ReadBoolean();
        int typeIndex = r.ReadInt32(), methodIndex = r.ReadInt32();
        int[] arguments = new int[r.Read7Bit()];
        for (int i = 0; i < arguments.Length; i++) arguments[i] = r.ReadInt32();
        // The name: 'Type, Assembly', the type with its suffixes ([] & *). The VM cuts it at
        // the first comma (ResolveTypeFromToken); names of the obfuscator hold '=' and '$' and no comma.
        int comma = name.IndexOf(',');
        string typeName = comma < 0 ? name : name.Substring(0, comma);
        string assembly = comma < 0 ? "" : name.Substring(comma + 1).Trim();
        int next = assembly.IndexOf(',');
        if (next >= 0) assembly = assembly.Substring(0, next).Trim();
        List<string> suffixes = new List<string>();
        while (true)
        {
            if (typeName.EndsWith("[]", StringComparison.Ordinal)) { suffixes.Insert(0, "[]"); typeName = typeName.Substring(0, typeName.Length - 2); }
            else if (typeName.EndsWith("&", StringComparison.Ordinal)) { suffixes.Insert(0, "&"); typeName = typeName.Substring(0, typeName.Length - 1); }
            else if (typeName.EndsWith("*", StringComparison.Ordinal)) { suffixes.Insert(0, "*"); typeName = typeName.Substring(0, typeName.Length - 1); }
            else break;
        }
        TypeSig sig;
        if (parameter)
        {
            // A generic parameter: of the declaring type (first index) or of the method.
            sig = typeIndex != -1 ? (TypeSig)new GenericVar(typeIndex) : new GenericMVar(methodIndex);
        }
        else if (assembly == module.Assembly.Name)
        {
            TypeDef own;
            if (!ownTypes.TryGetValue(typeName, out own)) throw new FormatException("no such type in the module: " + typeName);
            sig = own.ToTypeSig();
        }
        else
        {
            sig = TypeNameParser.ParseAsTypeSigReflection(module, typeName + ", " + name.Substring(comma + 1).Trim(), new RefFinder(module));
            if (sig == null) throw new FormatException("type name not understood: " + name);
            sig = Own(sig);
        }
        if (generic)
        {
            // The name is the generic definition (with its array or by-ref suffixes); the
            // arguments come apart.
            List<TypeSig> args = new List<TypeSig>();
            foreach (int a in arguments) args.Add(ResolveType(a));
            sig = Instantiate(sig, args);
        }
        foreach (string suffix in suffixes)
            sig = suffix == "[]" ? (TypeSig)new SZArraySig(sig) : suffix == "&" ? (TypeSig)new ByRefSig(sig) : new PtrSig(sig);
        return sig;
    }

    private TypeSig Instantiate(TypeSig sig, List<TypeSig> args)
    {
        ClassOrValueTypeSig definition = sig as ClassOrValueTypeSig;
        if (definition != null) return new GenericInstSig(definition, args);
        GenericInstSig inst = sig as GenericInstSig;
        if (inst != null) return new GenericInstSig(inst.GenericType, args);
        SZArraySig array = sig as SZArraySig;
        if (array != null) return new SZArraySig(Instantiate(array.Next, args));
        ByRefSig byRef = sig as ByRefSig;
        if (byRef != null) return new ByRefSig(Instantiate(byRef.Next, args));
        PtrSig pointer = sig as PtrSig;
        if (pointer != null) return new PtrSig(Instantiate(pointer.Next, args));
        throw new NotSupportedException("generic instance of " + sig.FullName);
    }

    // The parser makes a TypeRef for every name: types of the module itself become their
    // TypeDef, others the TypeRef the module already has when there is one.
    private TypeSig Own(TypeSig sig)
    {
        if (sig == null) return null;
        if (sig is CorLibTypeSig) return sig;
        ClassOrValueTypeSig leaf = sig as ClassOrValueTypeSig;
        if (leaf != null)
        {
            ITypeDefOrRef type = Own(leaf.TypeDefOrRef);
            CorLibTypeSig known = module.CorLibTypes.GetCorLibTypeSig(type);
            if (known != null) return known;
            return IsValueType(type) ? (TypeSig)new ValueTypeSig(type) : new ClassSig(type);
        }
        GenericInstSig inst = sig as GenericInstSig;
        if (inst != null)
        {
            List<TypeSig> args = new List<TypeSig>();
            foreach (TypeSig a in inst.GenericArguments) args.Add(Own(a));
            return new GenericInstSig((ClassOrValueTypeSig)Own(inst.GenericType), args);
        }
        SZArraySig array = sig as SZArraySig;
        if (array != null) return new SZArraySig(Own(array.Next));
        ArraySig multi = sig as ArraySig;
        if (multi != null) return new ArraySig(Own(multi.Next), multi.Rank, multi.Sizes, multi.LowerBounds);
        ByRefSig byRef = sig as ByRefSig;
        if (byRef != null) return new ByRefSig(Own(byRef.Next));
        PtrSig pointer = sig as PtrSig;
        if (pointer != null) return new PtrSig(Own(pointer.Next));
        return sig;
    }

    private ITypeDefOrRef Own(ITypeDefOrRef type)
    {
        TypeRef reference = type as TypeRef;
        if (reference == null) return type;
        IAssembly scope = reference.DefinitionAssembly;
        string name = reference.ReflectionFullName;
        if (scope == null || scope.Name == module.Assembly.Name)
        {
            TypeDef own;
            if (ownTypes.TryGetValue(name, out own)) return own;
            throw new FormatException("no such type in the module: " + name);
        }
        TypeRef existing;
        if (typeRefs.TryGetValue(name + ", " + scope.Name, out existing)) return existing;
        // A type the module does not reference yet: the new TypeRef is kept, on the AssemblyRef
        // the module already has for that assembly when there is one.
        AssemblyRef known;
        TypeRef outer = reference;
        while (outer.ResolutionScope is TypeRef) outer = (TypeRef)outer.ResolutionScope;
        if (assemblyRefs.TryGetValue(scope.Name, out known)) outer.ResolutionScope = known;
        typeRefs[name + ", " + scope.Name] = reference;
        return reference;
    }

    private static bool IsValueType(ITypeDefOrRef type)
    {
        TypeDef def = type.ResolveTypeDef();
        if (def == null) throw new FormatException("cannot resolve " + type.FullName + " (value type or class?)");
        return def.IsValueType;
    }

    private static ITypeDefOrRef Scope(TypeSig sig)
    {
        ITypeDefOrRef type = sig.ToTypeDefOrRef();
        if (type == null) throw new FormatException("no type for " + sig.FullName);
        return type;
    }

    private IMethod ReadMethod(VmReader r, int id)
    {
        int declaring = r.ReadInt32();
        byte flags = r.ReadByte();
        string name = r.ReadString();
        int returns = r.ReadInt32();
        int[] parameters = new int[r.Read7Bit()];
        for (int i = 0; i < parameters.Length; i++) parameters[i] = r.ReadInt32();
        int[] generics = new int[r.Read7Bit()];
        for (int i = 0; i < generics.Length; i++) generics[i] = r.ReadInt32();
        bool isStatic = (flags & 1) != 0;
        TypeSig owner = ResolveType(declaring);
        TypeSig returnType = ResolveType(returns);
        List<TypeSig> args = new List<TypeSig>();
        foreach (int p in parameters) args.Add(ResolveType(p));
        MethodSig sig = isStatic ? MethodSig.CreateStatic(returnType, args.ToArray()) : MethodSig.CreateInstance(returnType, args.ToArray());
        if (generics.Length > 0)
        {
            sig.Generic = true;
            sig.GenParamCount = (uint)generics.Length;
        }
        ITypeDefOrRef scope = Scope(owner);
        TypeDef def = scope as TypeDef;
        if (def != null)
        {
            SigComparer comparer = new SigComparer();
            foreach (MethodDef m in def.Methods)
                if (m.Name == name && comparer.Equals(m.MethodSig, sig)) return Closed(m, generics);
            throw new FormatException(string.Format("no method {0}::{1} {2} (reference {3})", def.FullName, name, sig, id));
        }
        // A method of another assembly. The stream names it with its types as they are at
        // the call (List<string>::Add(string)); a reference is written with the signature of
        // the definition (Add(!0)): the definition is the method of that name whose
        // signature gives the one of the stream once the generic arguments are put in.
        List<TypeSig> typeArguments = null;
        GenericInstSig instance = owner as GenericInstSig;
        if (instance != null) typeArguments = new List<TypeSig>(instance.GenericArguments);
        List<TypeSig> methodArguments = null;
        if (generics.Length > 0)
        {
            methodArguments = new List<TypeSig>();
            foreach (int g in generics) methodArguments.Add(ResolveType(g));
        }
        if (typeArguments != null || methodArguments != null)
        {
            TypeDef definition = scope.ResolveTypeDef();
            if (definition == null) throw new FormatException("cannot resolve " + scope.FullName + " (reference " + id + ")");
            SigComparer comparer = new SigComparer();
            MethodDef found = null;
            foreach (MethodDef m in definition.Methods)
            {
                if (m.Name != name || m.MethodSig.Params.Count != args.Count || m.IsStatic != isStatic || m.MethodSig.GenParamCount != generics.Length) continue;
                List<TypeSig> closedParameters = new List<TypeSig>();
                foreach (TypeSig t in m.MethodSig.Params) closedParameters.Add(Substitute(t, typeArguments, methodArguments));
                TypeSig closedReturn = Substitute(m.MethodSig.RetType, typeArguments, methodArguments);
                MethodSig closed = isStatic ? MethodSig.CreateStatic(closedReturn, closedParameters.ToArray()) : MethodSig.CreateInstance(closedReturn, closedParameters.ToArray());
                closed.Generic = sig.Generic;
                closed.GenParamCount = sig.GenParamCount;
                if (!comparer.Equals(closed, sig) && !comparer.Equals(m.MethodSig, sig)) continue;
                if (found != null) throw new FormatException(string.Format("two methods fit {0}::{1} (reference {2})", scope.FullName, name, id));
                found = m;
            }
            if (found == null) throw new FormatException(string.Format("no method {0}::{1} {2} (reference {3})", scope.FullName, name, sig, id));
            sig = new Importer(module).Import(found.MethodSig);
        }
        return (IMethod)Existing(Closed((IMethodDefOrRef)Existing(new MemberRefUser(module, name, sig, scope)), generics));
    }

    // A signature of a definition with the generic arguments of a use put in.
    private static TypeSig Substitute(TypeSig sig, List<TypeSig> typeArguments, List<TypeSig> methodArguments)
    {
        if (sig == null) return null;
        GenericMVar ofMethod = sig as GenericMVar;
        if (ofMethod != null) return methodArguments != null && ofMethod.Number < methodArguments.Count ? methodArguments[(int)ofMethod.Number] : sig;
        GenericVar ofType = sig as GenericVar;
        if (ofType != null) return typeArguments != null && ofType.Number < typeArguments.Count ? typeArguments[(int)ofType.Number] : sig;
        GenericInstSig inst = sig as GenericInstSig;
        if (inst != null)
        {
            List<TypeSig> args = new List<TypeSig>();
            foreach (TypeSig a in inst.GenericArguments) args.Add(Substitute(a, typeArguments, methodArguments));
            return new GenericInstSig(inst.GenericType, args);
        }
        SZArraySig array = sig as SZArraySig;
        if (array != null) return new SZArraySig(Substitute(array.Next, typeArguments, methodArguments));
        ByRefSig byRef = sig as ByRefSig;
        if (byRef != null) return new ByRefSig(Substitute(byRef.Next, typeArguments, methodArguments));
        PtrSig pointer = sig as PtrSig;
        if (pointer != null) return new PtrSig(Substitute(pointer.Next, typeArguments, methodArguments));
        return sig;
    }

    // A generic method with its arguments.
    private IMethod Closed(IMethodDefOrRef method, int[] generics)
    {
        if (generics.Length == 0) return method;
        List<TypeSig> args = new List<TypeSig>();
        foreach (int g in generics) args.Add(ResolveType(g));
        return new MethodSpecUser(method, new GenericInstMethodSig(args));
    }

    private IField ReadField(VmReader r, int id)
    {
        int declaring = r.ReadInt32();
        string name = r.ReadString();
        r.ReadBoolean();
        ITypeDefOrRef scope = Scope(ResolveType(declaring));
        TypeDef def = scope as TypeDef;
        if (def != null)
        {
            FieldDef found = null;
            foreach (FieldDef f in def.Fields)
                if (f.Name == name)
                {
                    if (found != null) throw new FormatException("two fields named " + name + " in " + def.FullName);
                    found = f;
                }
            if (found == null) throw new FormatException(string.Format("no field {0}::{1} (reference {2})", def.FullName, name, id));
            return found;
        }
        // A field of another assembly: its type is needed to make the reference.
        TypeDef resolved = scope.ResolveTypeDef();
        if (resolved != null)
            foreach (FieldDef f in resolved.Fields)
                if (f.Name == name) return (IField)Existing(new MemberRefUser(module, name, new FieldSig(module.Import(f.FieldSig.Type)), scope));
        throw new FormatException(string.Format("cannot resolve field {0}::{1} (reference {2})", scope.FullName, name, id));
    }
}
