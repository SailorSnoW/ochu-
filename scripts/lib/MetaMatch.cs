// Compares what scripts/lib/IlMatch.cs leaves out: the declarations. Types, fields and
// methods of the reference against those of a recompilation, paired by the same rendered
// names (IlMatch's Namer), with their flags, base types, interfaces, custom attributes,
// constants, P/Invoke data, member order, and the resources of the module.
// See docs/matching.md. Compiled by scripts/match-meta.ps1 with Add-Type
// next to IlMatch.cs (Windows PowerShell 5.1: C# 5 only).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using dnlib.DotNet;

public sealed class MetaDiff
{
    public string Kind;       // what differs
    public string Item;       // the type or member, rendered
    public string Reference;
    public string Candidate;
    public string Category;   // osu | global | lib | obf | ""
}

public static class MetaMatch
{
    static string Arg(object v, Namer n)
    {
        if (v == null) return "null";
        var sig = v as TypeSig;
        if (sig != null) return "typeof(" + n.Sig(sig) + ")";
        var list = v as IList<CAArgument>;
        if (list != null)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < list.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Arg(list[i].Value, n)); }
            return sb.Append(']').ToString();
        }
        if (v is CAArgument) return Arg(((CAArgument)v).Value, n);
        if (v is UTF8String) return "\"" + v + "\"";
        return Convert.ToString(v, CultureInfo.InvariantCulture);
    }

    static List<string> Attributes(IHasCustomAttribute o, Namer n)
    {
        var l = new List<string>();
        foreach (var ca in o.CustomAttributes)
        {
            var sb = new StringBuilder(ca.AttributeType == null ? "?" : n.Type(ca.AttributeType)).Append('(');
            for (int i = 0; i < ca.ConstructorArguments.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Arg(ca.ConstructorArguments[i].Value, n)); }
            var named = new List<string>();
            foreach (var na in ca.NamedArguments) named.Add(na.Name + "=" + Arg(na.Argument.Value, n));
            named.Sort(StringComparer.Ordinal);
            foreach (string s in named) sb.Append(';').Append(s);
            l.Add(sb.Append(')').ToString());
        }
        l.Sort(StringComparer.Ordinal);
        return l;
    }

    sealed class Side
    {
        public ModuleDefMD Module; public Namer Namer;
        public Dictionary<string, TypeDef> Types = new Dictionary<string, TypeDef>(StringComparer.Ordinal);
        public Dictionary<string, MethodDef> Methods = new Dictionary<string, MethodDef>(StringComparer.Ordinal);
        public Dictionary<string, FieldDef> Fields = new Dictionary<string, FieldDef>(StringComparer.Ordinal);
        public List<string> Ambiguous = new List<string>();

        public Side(ModuleDefMD module, Namer namer)
        {
            Module = module; Namer = namer;
            foreach (var t in module.GetTypes())
            {
                Put(Types, namer.Type(t), t);
                foreach (var m in t.Methods) Put(Methods, namer.Method(m), m);
                foreach (var f in t.Fields) Put(Fields, namer.Field(f), f);
            }
        }

        void Put<T>(Dictionary<string, T> d, string key, T value)
        {
            if (d.ContainsKey(key)) { Ambiguous.Add(key); return; }
            d[key] = value;
        }
    }

    static string Join(List<string> l) { return string.Join(" | ", l.ToArray()); }

    static Dictionary<string, string> Win32(ModuleDefMD m)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (m.Win32Resources == null) return d;
        using (var sha = System.Security.Cryptography.SHA1.Create())
            foreach (var type in m.Win32Resources.Root.Directories)
                foreach (var name in type.Directories)
                    foreach (var lang in name.Data)
                    {
                        var reader = lang.Data;
                        reader.Position = 0;
                        byte[] data = reader.ReadBytes((int)reader.Length);
                        d["type " + type.Name + " name " + name.Name + " language " + lang.Name] = data.Length + " " + BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
                    }
        return d;
    }

    public static List<MetaDiff> Compare(byte[] reference, byte[] names, byte[] candidate)
    {
        var rm = ModuleDefMD.Load(reference);
        var nm = names == null ? null : ModuleDefMD.Load(names);
        var cm = ModuleDefMD.Load(candidate);
        var r = new Side(rm, new Namer(nm, rm));
        var c = new Side(cm, new Namer(null, cm));
        // Flags, attributes and member order are read in the names module when there is one:
        // it is the reference with the properties, the events and the [CompilerGenerated]
        // marks the names stage puts back. The tokens are the same.
        var diffs = new List<MetaDiff>();
        Action<string, string, string, string, TypeDef> add = (kind, item, a, b, owner) =>
            diffs.Add(new MetaDiff { Kind = kind, Item = item, Reference = a ?? "", Candidate = b ?? "", Category = owner == null ? "" : IlMatch.Category(r.Namer.NamedType(owner)) });

        foreach (var kv in r.Types)
        {
            TypeDef rt = kv.Value, ct;
            if (!c.Types.TryGetValue(kv.Key, out ct)) { add("type missing in candidate", kv.Key, "", "", rt); continue; }
            TypeDef named = nm == null ? rt : (nm.ResolveToken(rt.MDToken.Raw) as TypeDef ?? rt);
            if (r.Namer.Type(rt.BaseType) != c.Namer.Type(ct.BaseType)) add("type base", kv.Key, r.Namer.Type(rt.BaseType), c.Namer.Type(ct.BaseType), rt);
            var ri = new List<string>(); foreach (var i in rt.Interfaces) ri.Add(r.Namer.Type(i.Interface));
            var ci = new List<string>(); foreach (var i in ct.Interfaces) ci.Add(c.Namer.Type(i.Interface));
            ri.Sort(StringComparer.Ordinal); ci.Sort(StringComparer.Ordinal);
            if (Join(ri) != Join(ci)) add("type interfaces", kv.Key, Join(ri), Join(ci), rt);
            if (named.Attributes != ct.Attributes) add("type flags", kv.Key, (named.Attributes ^ ct.Attributes & named.Attributes).ToString() == "" ? named.Attributes.ToString() : named.Attributes.ToString(), ct.Attributes.ToString(), rt);
            if (rt.GenericParameters.Count != ct.GenericParameters.Count) add("type generic parameters", kv.Key, rt.GenericParameters.Count.ToString(), ct.GenericParameters.Count.ToString(), rt);
            var ra = Attributes(named, new Namer(null, nm ?? rm)); var ca = Attributes(ct, c.Namer);
            if (Join(ra) != Join(ca)) add("type attributes", kv.Key, Join(ra), Join(ca), rt);
            if ((rt.ClassLayout == null) != (ct.ClassLayout == null) || (rt.ClassLayout != null && (rt.ClassLayout.PackingSize != ct.ClassLayout.PackingSize || rt.ClassLayout.ClassSize != ct.ClassLayout.ClassSize)))
                add("type layout", kv.Key, rt.ClassLayout == null ? "none" : rt.ClassLayout.PackingSize + "/" + rt.ClassLayout.ClassSize, ct.ClassLayout == null ? "none" : ct.ClassLayout.PackingSize + "/" + ct.ClassLayout.ClassSize, rt);
            // Member order, on the members both sides have.
            var ro = new List<string>(); foreach (var m in rt.Methods) { string k = r.Namer.Method(m); if (c.Methods.ContainsKey(k)) ro.Add(k); }
            var co = new List<string>(); foreach (var m in ct.Methods) { string k = c.Namer.Method(m); if (r.Methods.ContainsKey(k)) co.Add(k); }
            if (Join(ro) != Join(co)) add("method order", kv.Key, "", "", rt);
            ro.Clear(); co.Clear();
            foreach (var f in rt.Fields) { string k = r.Namer.Field(f); if (c.Fields.ContainsKey(k)) ro.Add(k); }
            foreach (var f in ct.Fields) { string k = c.Namer.Field(f); if (r.Fields.ContainsKey(k)) co.Add(k); }
            if (Join(ro) != Join(co)) add("field order", kv.Key, "", "", rt);
        }
        foreach (var kv in c.Types) if (!r.Types.ContainsKey(kv.Key)) add("type only in candidate", kv.Key, "", "", null);

        var plainNames = new Namer(null, nm ?? rm);
        foreach (var kv in r.Methods)
        {
            MethodDef rd = kv.Value, cd;
            if (!c.Methods.TryGetValue(kv.Key, out cd)) { add(rd.HasBody ? "method missing in candidate" : "method without body missing in candidate", kv.Key, "", "", rd.DeclaringType); continue; }
            MethodDef named = nm == null ? rd : (nm.ResolveToken(rd.MDToken.Raw) as MethodDef ?? rd);
            if (named.Attributes != cd.Attributes) add("method flags", kv.Key, named.Attributes.ToString(), cd.Attributes.ToString(), rd.DeclaringType);
            if (named.ImplAttributes != cd.ImplAttributes) add("method impl flags", kv.Key, named.ImplAttributes.ToString(), cd.ImplAttributes.ToString(), rd.DeclaringType);
            if (rd.HasBody != cd.HasBody) add("method body presence", kv.Key, rd.HasBody.ToString(), cd.HasBody.ToString(), rd.DeclaringType);
            string rp = rd.ImplMap == null ? "" : rd.ImplMap.Module.Name + "!" + rd.ImplMap.Name + " " + rd.ImplMap.Attributes;
            string cp = cd.ImplMap == null ? "" : cd.ImplMap.Module.Name + "!" + cd.ImplMap.Name + " " + cd.ImplMap.Attributes;
            if (rp != cp) add("method pinvoke", kv.Key, rp, cp, rd.DeclaringType);
            var ra = Attributes(named, plainNames); var ca = Attributes(cd, c.Namer);
            if (Join(ra) != Join(ca)) add("method attributes", kv.Key, Join(ra), Join(ca), rd.DeclaringType);
            var rov = new List<string>(); foreach (var o in rd.Overrides) rov.Add(r.Namer.Method(o.MethodDeclaration));
            var cov = new List<string>(); foreach (var o in cd.Overrides) cov.Add(c.Namer.Method(o.MethodDeclaration));
            rov.Sort(StringComparer.Ordinal); cov.Sort(StringComparer.Ordinal);
            if (Join(rov) != Join(cov)) add("method overrides", kv.Key, Join(rov), Join(cov), rd.DeclaringType);
            // Parameters: attributes (in, out, optional), default values. Names are lost.
            var rpa = new List<string>(); foreach (var p in named.ParamDefs) rpa.Add(p.Sequence + ":" + p.Attributes + (p.Constant == null ? "" : "=" + Convert.ToString(p.Constant.Value, CultureInfo.InvariantCulture)));
            var cpa = new List<string>(); foreach (var p in cd.ParamDefs) cpa.Add(p.Sequence + ":" + p.Attributes + (p.Constant == null ? "" : "=" + Convert.ToString(p.Constant.Value, CultureInfo.InvariantCulture)));
            rpa.RemoveAll(s => s.EndsWith(":0", StringComparison.Ordinal)); cpa.RemoveAll(s => s.EndsWith(":0", StringComparison.Ordinal));
            rpa.Sort(StringComparer.Ordinal); cpa.Sort(StringComparer.Ordinal);
            if (Join(rpa) != Join(cpa)) add("method parameters", kv.Key, Join(rpa), Join(cpa), rd.DeclaringType);
        }
        foreach (var kv in c.Methods) if (!r.Methods.ContainsKey(kv.Key)) add("method only in candidate", kv.Key, "", "", null);

        foreach (var kv in r.Fields)
        {
            FieldDef rf = kv.Value, cf;
            if (!c.Fields.TryGetValue(kv.Key, out cf)) { add("field missing in candidate", kv.Key, "", "", rf.DeclaringType); continue; }
            FieldDef named = nm == null ? rf : (nm.ResolveToken(rf.MDToken.Raw) as FieldDef ?? rf);
            if (named.Attributes != cf.Attributes) add("field flags", kv.Key, named.Attributes.ToString(), cf.Attributes.ToString(), rf.DeclaringType);
            string rc = rf.Constant == null ? "" : Convert.ToString(rf.Constant.Value, CultureInfo.InvariantCulture);
            string cc = cf.Constant == null ? "" : Convert.ToString(cf.Constant.Value, CultureInfo.InvariantCulture);
            if (rc != cc || (rf.Constant == null) != (cf.Constant == null)) add("field constant", kv.Key, rc, cc, rf.DeclaringType);
            if (rf.FieldOffset != cf.FieldOffset) add("field offset", kv.Key, Convert.ToString(rf.FieldOffset), Convert.ToString(cf.FieldOffset), rf.DeclaringType);
            var ra = Attributes(named, plainNames); var ca = Attributes(cf, c.Namer);
            if (Join(ra) != Join(ca)) add("field attributes", kv.Key, Join(ra), Join(ca), rf.DeclaringType);
        }
        foreach (var kv in c.Fields) if (!r.Fields.ContainsKey(kv.Key)) add("field only in candidate", kv.Key, "", "", null);

        // Resources: name, kind and content.
        var rres = new Dictionary<string, string>(StringComparer.Ordinal);
        var cres = new Dictionary<string, string>(StringComparer.Ordinal);
        // (Read in the names module: a resource named after a type follows its name.)
        var rorder = new List<string>(); var corder = new List<string>();
        foreach (var pair in new[] { new KeyValuePair<ModuleDefMD, Dictionary<string, string>>(nm ?? rm, rres), new KeyValuePair<ModuleDefMD, Dictionary<string, string>>(cm, cres) })
            foreach (var res in pair.Key.Resources)
            {
                (pair.Value == rres ? rorder : corder).Add(res.Name);
                string value = res.ResourceType + " " + (res.Attributes & ManifestResourceAttributes.VisibilityMask);
                var e = res as EmbeddedResource;
                if (e != null)
                {
                    byte[] data = e.GetResourceData();
                    using (var sha = System.Security.Cryptography.SHA1.Create())
                        value += " " + data.Length + " " + BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
                }
                pair.Value[res.Name] = value;
            }
        foreach (var kv in rres)
        {
            string cv;
            if (!cres.TryGetValue(kv.Key, out cv)) add("resource missing in candidate", kv.Key, kv.Value, "", null);
            else if (cv != kv.Value) add("resource content", kv.Key, kv.Value, cv, null);
        }
        foreach (var kv in cres) if (!rres.ContainsKey(kv.Key)) add("resource only in candidate", kv.Key, "", kv.Value, null);
        if (rres.Count == cres.Count && Join(rorder) != Join(corder)) add("resource order", "module", "", "", null);
        diffs.Add(new MetaDiff { Kind = "(count) resources", Item = "", Reference = rres.Count.ToString(), Candidate = cres.Count.ToString(), Category = "" });

        // Win32 resources (icons, version, manifest): type, name, language and content.
        var rw = Win32(rm); var cw = Win32(cm);
        foreach (var kv in rw)
        {
            string cv;
            if (!cw.TryGetValue(kv.Key, out cv)) add("win32 resource missing in candidate", kv.Key, kv.Value, "", null);
            else if (cv != kv.Value) add("win32 resource content", kv.Key, kv.Value, cv, null);
        }
        foreach (var kv in cw) if (!rw.ContainsKey(kv.Key)) add("win32 resource only in candidate", kv.Key, "", kv.Value, null);
        diffs.Add(new MetaDiff { Kind = "(count) win32 resources", Item = "", Reference = rw.Count.ToString(), Candidate = cw.Count.ToString(), Category = "" });

        // The assembly and the module.
        var raa = Attributes((nm ?? rm).Assembly, plainNames); var caa = Attributes(cm.Assembly, c.Namer);
        if (Join(raa) != Join(caa)) add("assembly attributes", "assembly", Join(raa), Join(caa), null);
        var rrefs = new List<string>(); foreach (var a in rm.GetAssemblyRefs()) rrefs.Add(a.Name + " " + a.Version);
        var crefs = new List<string>(); foreach (var a in cm.GetAssemblyRefs()) crefs.Add(a.Name + " " + a.Version);
        rrefs.Sort(StringComparer.Ordinal); crefs.Sort(StringComparer.Ordinal);
        if (Join(rrefs) != Join(crefs)) add("assembly references", "module", Join(rrefs), Join(crefs), null);
        if (rm.Assembly.Version != cm.Assembly.Version) add("assembly version", "assembly", rm.Assembly.Version.ToString(), cm.Assembly.Version.ToString(), null);
        string rk = rm.Kind + " " + rm.Machine + " " + rm.Cor20HeaderFlags + " " + rm.RuntimeVersion;
        string ck = cm.Kind + " " + cm.Machine + " " + cm.Cor20HeaderFlags + " " + cm.RuntimeVersion;
        if (rk != ck) add("module header", "module", rk, ck, null);
        string re = rm.EntryPoint == null ? "" : r.Namer.Method(rm.EntryPoint), ce = cm.EntryPoint == null ? "" : c.Namer.Method(cm.EntryPoint);
        if (re != ce) add("entry point", "module", re, ce, null);
        foreach (string a in r.Ambiguous) add("ambiguous name in reference", a, "", "", null);
        foreach (string a in c.Ambiguous) add("ambiguous name in candidate", a, "", "", null);

        diffs.Insert(0, new MetaDiff { Kind = "(count) types", Item = "", Reference = r.Types.Count.ToString(), Candidate = c.Types.Count.ToString(), Category = "" });
        diffs.Insert(1, new MetaDiff { Kind = "(count) methods", Item = "", Reference = r.Methods.Count.ToString(), Candidate = c.Methods.Count.ToString(), Category = "" });
        diffs.Insert(2, new MetaDiff { Kind = "(count) fields", Item = "", Reference = r.Fields.Count.ToString(), Candidate = c.Fields.Count.ToString(), Category = "" });
        rm.Dispose(); cm.Dispose(); if (nm != null) nm.Dispose();
        return diffs;
    }
}
