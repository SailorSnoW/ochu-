// Puts back the calls to small static methods that the obfuscator inlined at every call
// site and then deleted.
//
// The obfuscator inlines a call by popping the arguments into fresh locals, last one first,
// and pasting the body with ldarg replaced by ldloc. A 'ret' becomes a jump to the end of
// the pasted code, the returned value staying on the stack. Three shapes are undone here:
//
//   Test      bool M(T x, T y) { return (x & y) > 0; }
//                 stloc B ; stloc A ; ldloc A ; ldloc B ; and ; ldc.i4.0 ; cgt
//
//   Wrapper   R W(T y) { return M(<loads that need no local>, y); }
//                 stloc A ; ... ldloc A ... ; call M
//
//   Body      a method with its own locals and exception handlers, found by the static
//             field it locks on. A protected block can only be entered with an empty
//             stack, so what the caller had on the stack is parked in more locals around
//             the pasted code:
//                 stloc D ; stloc P1 ; stloc P2 ; <body> ; stloc R ; ldloc P2 ; ldloc P1 ; ldloc R
//
// The locals involved are touched by nothing else in the calling method. No C# compiles to
// the Test shape (a compiler never stores both operands of an 'and' to read them straight
// back) nor to a statement in the middle of an expression (Body). The Wrapper shape could
// be written by hand ('T a = y; M(F, a)'): it is only undone where the local has no other use.
//
// Each site becomes a call again, its locals are removed, and the methods are added to the
// module. Existing metadata tokens do not move: the methods get the next free tokens.
// Compiled by scripts/undo-obfuscator.ps1 with Add-Type (Windows PowerShell 5.1: C# 5 only).
using System;
using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

public sealed class UndoInlining
{
    public sealed class Result
    {
        public string Kind; public MethodDef Method; public int Sites, Methods, Kept; public string Detail = "";
    }

    public readonly List<Result> Results = new List<Result>();
    public readonly List<string> Problems = new List<string>();
    // token <TAB> sites <TAB> kind, one line per rewritten method and kind
    public readonly List<string> Touched = new List<string>();

    readonly ModuleDefMD mod;
    public UndoInlining(ModuleDefMD mod) { this.mod = mod; }

    // One inlined call: instruction Index becomes the call, the next Removed ones go away.
    sealed class Site
    {
        public int Index, Removed; public List<Local> Locals = new List<Local>();
        public List<ExceptionHandler> Handlers = new List<ExceptionHandler>();
        public string Shape; public MethodDef Callee;
        public bool Virtual;   // callvirt: an instance method called on something else than 'this'
    }

    sealed class Work { public MethodDef Method; public List<Site> Sites; }

    static Local LocalOf(Instruction i, IList<Local> vars)
    {
        if (i.IsLdloc() || i.IsStloc()) return i.GetLocal(vars);
        if (i.OpCode.Code == Code.Ldloca || i.OpCode.Code == Code.Ldloca_S) return i.Operand as Local;
        return null;
    }

    static HashSet<Instruction> Range(MethodDef m, List<Site> sites, bool withFirst)
    {
        var ins = m.Body.Instructions;
        var set = new HashSet<Instruction>();
        foreach (var s in sites) for (int j = withFirst ? 0 : 1; j <= s.Removed; j++) set.Add(ins[s.Index + j]);
        return set;
    }

    // The instructions that go away must not be entered from the rest of the method.
    static bool Enclosed(MethodDef m, List<Site> sites)
    {
        var gone = Range(m, sites, false);
        var whole = Range(m, sites, true);
        var own = new HashSet<ExceptionHandler>();
        foreach (var s in sites) foreach (var h in s.Handlers) own.Add(h);
        foreach (var i in m.Body.Instructions)
        {
            if (whole.Contains(i)) continue;
            var one = i.Operand as Instruction; var many = i.Operand as IList<Instruction>;
            if (one != null && gone.Contains(one)) return false;
            if (many != null) foreach (var x in many) if (x != null && gone.Contains(x)) return false;
        }
        foreach (var eh in m.Body.ExceptionHandlers)
        {
            if (own.Contains(eh)) continue;
            foreach (var x in new[] { eh.TryStart, eh.TryEnd, eh.HandlerStart, eh.HandlerEnd, eh.FilterStart })
                if (x != null && gone.Contains(x)) return false;
        }
        return true;
    }

    // The locals of the sites must belong to the sites alone.
    static bool Dedicated(MethodDef m, List<Site> sites)
    {
        var owned = new HashSet<Local>();
        foreach (var s in sites) foreach (var l in s.Locals) owned.Add(l);
        var whole = Range(m, sites, true);
        foreach (var i in m.Body.Instructions)
        {
            if (whole.Contains(i)) continue;
            var l = LocalOf(i, m.Body.Variables);
            if (l != null && owned.Contains(l)) return false;
        }
        return true;
    }

    static void Rewrite(MethodDef m, List<Site> sites, MethodDef callee)
    {
        var ins = m.Body.Instructions; var vars = m.Body.Variables;
        // Short forms (ldloc.2) carry their index in the opcode: note which local each
        // instruction means before the indices move.
        var meant = new Dictionary<Instruction, Local>();
        foreach (var i in ins) { var l = LocalOf(i, vars); if (l != null) meant[i] = l; }
        var dead = new HashSet<Local>();
        for (int n = sites.Count - 1; n >= 0; n--)
        {
            var s = sites[n];
            foreach (var l in s.Locals) dead.Add(l);
            foreach (var h in s.Handlers) m.Body.ExceptionHandlers.Remove(h);
            // The first instruction keeps its identity: branches to the site still land on it.
            meant.Remove(ins[s.Index]);
            ins[s.Index].OpCode = s.Virtual ? OpCodes.Callvirt : OpCodes.Call;
            ins[s.Index].Operand = s.Callee ?? callee;
            for (int j = s.Removed; j >= 1; j--) { meant.Remove(ins[s.Index + j]); ins.RemoveAt(s.Index + j); }
        }
        // Pasting a body that has protected blocks made the obfuscator write the handler
        // table again, last block first. The compiler lists handlers as their blocks close
        // (docs/matching.md).
        bool pasted = false;
        foreach (var s in sites) if (s.Handlers.Count > 0) pasted = true;
        if (pasted && m.Body.ExceptionHandlers.Count > 1)
        {
            var at = new Dictionary<Instruction, int>();
            for (int i = 0; i < ins.Count; i++) at[ins[i]] = i;
            var sorted = new List<ExceptionHandler>();
            foreach (var h in m.Body.ExceptionHandlers)
            {
                int end = h.HandlerEnd != null && at.ContainsKey(h.HandlerEnd) ? at[h.HandlerEnd] : int.MaxValue;
                int k = sorted.Count;
                while (k > 0)
                {
                    var p = sorted[k - 1];
                    int pe = p.HandlerEnd != null && at.ContainsKey(p.HandlerEnd) ? at[p.HandlerEnd] : int.MaxValue;
                    if (pe <= end) break;
                    k--;
                }
                sorted.Insert(k, h);
            }
            m.Body.ExceptionHandlers.Clear();
            foreach (var h in sorted) m.Body.ExceptionHandlers.Add(h);
        }
        foreach (var l in dead) vars.Remove(l);
        foreach (var kv in meant) Encode(kv.Key, kv.Value);
        m.Body.KeepOldMaxStack = true;
    }

    static void Encode(Instruction i, Local l)
    {
        int x = l.Index;
        if (i.IsLdloc())
        {
            if (x == 0) Set(i, OpCodes.Ldloc_0, null); else if (x == 1) Set(i, OpCodes.Ldloc_1, null);
            else if (x == 2) Set(i, OpCodes.Ldloc_2, null); else if (x == 3) Set(i, OpCodes.Ldloc_3, null);
            else Set(i, x < 256 ? OpCodes.Ldloc_S : OpCodes.Ldloc, l);
        }
        else if (i.IsStloc())
        {
            if (x == 0) Set(i, OpCodes.Stloc_0, null); else if (x == 1) Set(i, OpCodes.Stloc_1, null);
            else if (x == 2) Set(i, OpCodes.Stloc_2, null); else if (x == 3) Set(i, OpCodes.Stloc_3, null);
            else Set(i, x < 256 ? OpCodes.Stloc_S : OpCodes.Stloc, l);
        }
        else Set(i, x < 256 ? OpCodes.Ldloca_S : OpCodes.Ldloca, l);
    }

    static void Set(Instruction i, OpCode op, object operand) { i.OpCode = op; i.Operand = operand; }

    MethodDef Create(TypeDef host, string name, MethodSig sig, IEnumerable<Instruction> code)
    {
        foreach (var x in host.Methods)
            if (x.Name == name && new SigComparer().Equals(x.MethodSig, sig))
            { Problems.Add(host.FullName + " already has a method " + name + " with this signature"); return null; }
        var m = new MethodDefUser(name, sig, MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Assembly | MethodAttributes.HideBySig | (sig.HasThis ? 0 : MethodAttributes.Static));
        var body = new CilBody();
        foreach (var i in code) body.Instructions.Add(i);
        body.MaxStack = 8;
        body.KeepOldMaxStack = true;
        m.Body = body;
        host.Methods.Add(m);
        return m;
    }

    Result Apply(string kind, MethodDef callee, List<Work> work)
    {
        var r = new Result { Kind = kind, Method = callee };
        foreach (var w in work)
        {
            Rewrite(w.Method, w.Sites, callee);
            r.Sites += w.Sites.Count; r.Methods++;
            Touched.Add("0x" + w.Method.MDToken.Raw.ToString("X8") + "\t" + w.Sites.Count + "\t" + kind);
        }
        Results.Add(r);
        return r;
    }

    IEnumerable<MethodDef> Bodies()
    {
        foreach (var t in mod.GetTypes())
            foreach (var m in t.Methods)
                if (m.HasBody) yield return m;
    }

    // ---- Test ------------------------------------------------------------------------

    // hostType: the type that gets the method back. The binary no longer says where it
    // was declared; the caller documents its choice.
    public MethodDef UndoTest(string kind, uint hostType, string name)
    {
        var host = mod.ResolveToken(hostType) as TypeDef;
        if (host == null) { Problems.Add("host type 0x" + hostType.ToString("X8") + " not found"); return null; }
        var work = new List<Work>();
        TypeSig argType = null;
        var cmp = new SigComparer();
        foreach (var m in Bodies())
        {
            var sites = new List<Site>();
            var ins = m.Body.Instructions; var vars = m.Body.Variables;
            for (int k = 0; k + 6 < ins.Count; k++)
            {
                if (!ins[k].IsStloc() || !ins[k + 1].IsStloc() || !ins[k + 2].IsLdloc() || !ins[k + 3].IsLdloc()) continue;
                if (ins[k + 4].OpCode.Code != Code.And || ins[k + 5].OpCode.Code != Code.Ldc_I4_0 || ins[k + 6].OpCode.Code != Code.Cgt) continue;
                Local b = ins[k].GetLocal(vars), a = ins[k + 1].GetLocal(vars);
                if (a == null || b == null || a == b) continue;
                if (ins[k + 2].GetLocal(vars) != a || ins[k + 3].GetLocal(vars) != b) continue;
                var s = new Site { Index = k, Removed = 6 };
                s.Locals.Add(a); s.Locals.Add(b);
                sites.Add(s);
                k += 6;
            }
            if (sites.Count == 0) continue;
            string id = "0x" + m.MDToken.Raw.ToString("X8");
            bool ok = true;
            foreach (var s in sites)
            {
                if (argType == null) argType = s.Locals[0].Type;
                if (!cmp.Equals(s.Locals[0].Type, argType) || !cmp.Equals(s.Locals[1].Type, argType))
                { Problems.Add(id + ": locals of another type at instruction " + s.Index); ok = false; }
            }
            if (!Enclosed(m, sites)) { Problems.Add(id + ": a site is a branch target or a handler boundary"); ok = false; }
            if (!Dedicated(m, sites)) { Problems.Add(id + ": a local of a site is used elsewhere"); ok = false; }
            if (ok) work.Add(new Work { Method = m, Sites = sites });
        }
        if (Problems.Count > 0) return null;
        if (work.Count == 0) { Problems.Add(kind + ": no site found"); return null; }

        var restored = Create(host, name, MethodSig.CreateStatic(mod.CorLibTypes.Boolean, argType, argType), new[] {
            OpCodes.Ldarg_0.ToInstruction(), OpCodes.Ldarg_1.ToInstruction(), OpCodes.And.ToInstruction(),
            OpCodes.Ldc_I4_0.ToInstruction(), OpCodes.Cgt.ToInstruction(), OpCodes.Ret.ToInstruction() });
        if (restored == null) return null;
        Apply(kind, restored, work);
        return restored;
    }

    // ---- Wrapper ---------------------------------------------------------------------

    static string Describe(Instruction i)
    {
        var tp = i.Operand as IMDTokenProvider;
        return i.OpCode.Name + (tp == null ? "" : " 0x" + tp.MDToken.Raw.ToString("X8"));
    }

    // stloc A ; up to four loads and calls, one of them 'ldloc A' ; call target
    public void UndoWrappers(string kind, MethodDef target, string name)
    {
        if (target == null) return;
        var byShape = new Dictionary<string, List<Work>>();
        var sample = new Dictionary<string, KeyValuePair<MethodDef, Site>>();
        var order = new List<string>();
        int kept = 0;
        foreach (var m in Bodies())
        {
            if (m == target) continue;
            var ins = m.Body.Instructions; var vars = m.Body.Variables;
            var sites = new List<Site>();
            for (int k = 0; k + 2 < ins.Count; k++)
            {
                if (!ins[k].IsStloc()) continue;
                var a = ins[k].GetLocal(vars);
                int end = -1, reads = 0;
                string shape = "";
                for (int j = k + 1; j < ins.Count && j <= k + 5; j++)
                {
                    var i = ins[j];
                    if (i.OpCode.Code == Code.Call && i.Operand == target) { end = j; break; }
                    if (i.IsLdloc() && i.GetLocal(vars) == a) { reads++; shape += "arg;"; continue; }
                    var c = i.OpCode.Code;
                    if (c != Code.Ldsfld && c != Code.Call && c != Code.Callvirt) break;
                    shape += Describe(i) + ";";
                }
                // 'stloc A ; ldloc A ; call' would be a wrapper that adds nothing.
                if (end < 0 || reads != 1 || shape == "arg;") continue;
                var s = new Site { Index = k, Removed = end - k, Shape = shape };
                s.Locals.Add(a);
                sites.Add(s);
                k = end;
            }
            if (sites.Count == 0) continue;
            // Not an error: this is also what 'T a = y; M(F, a)' compiles to.
            if (!Enclosed(m, sites) || !Dedicated(m, sites)) { kept += sites.Count; continue; }
            foreach (var s in sites)
            {
                if (!byShape.ContainsKey(s.Shape))
                {
                    byShape[s.Shape] = new List<Work>(); order.Add(s.Shape);
                    sample[s.Shape] = new KeyValuePair<MethodDef, Site>(m, s);
                }
            }
            // A method whose sites have several shapes is rewritten once per shape; the
            // sites are found again each time, their indices having moved.
            var first = sites[0].Shape;
            bool mixed = false;
            foreach (var s in sites) if (s.Shape != first) mixed = true;
            if (mixed) { Problems.Add("0x" + m.MDToken.Raw.ToString("X8") + ": wrapper sites of several shapes in one method"); continue; }
            byShape[first].Add(new Work { Method = m, Sites = sites });
        }
        if (Problems.Count > 0) return;
        foreach (var shape in order)
        {
            var sm = sample[shape].Key; var ss = sample[shape].Value;
            var code = new List<Instruction>();
            for (int j = ss.Index + 1; j <= ss.Index + ss.Removed; j++)
            {
                var i = sm.Body.Instructions[j];
                code.Add(i.IsLdloc() ? OpCodes.Ldarg_0.ToInstruction() : new Instruction(i.OpCode, i.Operand));
            }
            code.Add(OpCodes.Ret.ToInstruction());
            var restored = Create(target.DeclaringType, name, MethodSig.CreateStatic(target.MethodSig.RetType, ss.Locals[0].Type), code);
            if (restored == null) return;
            var r = Apply(kind, restored, byShape[shape]);
            r.Kept = kept; kept = 0;
            r.Detail = shape;
        }
    }

    // ---- Body ------------------------------------------------------------------------

    static string OpName(Instruction i)
    {
        if (i.IsLdloc()) return "ldloc";
        if (i.IsStloc()) return "stloc";
        string n = i.OpCode.Name;
        return n.EndsWith(".s") ? n.Substring(0, n.Length - 2) : n;
    }

    sealed class Region
    {
        public MethodDef Method; public int Start, End;   // End: last instruction of the pasted body
        public Local Arg, Ret; public List<Local> Locals = new List<Local>();
        public List<ExceptionHandler> Handlers = new List<ExceptionHandler>();
        public List<string> Lines = new List<string>();
    }

    // The pasted body starts at 'ldsfld field ; stloc' (the lock) and ends at the 'ldloc'
    // that the return inside the catch block leaves to.
    Region ReadRegion(MethodDef m, int k)
    {
        var ins = m.Body.Instructions; var vars = m.Body.Variables;
        var at = new Dictionary<Instruction, int>();
        for (int i = 0; i < ins.Count; i++) at[ins[i]] = i;
        var r = new Region { Method = m, Start = k };
        ExceptionHandler fin = null, cat = null;
        foreach (var eh in m.Body.ExceptionHandlers)
            if (eh.HandlerType == ExceptionHandlerType.Finally && eh.TryStart != null && at[eh.TryStart] == k + 4) fin = eh;
        if (fin == null || fin.HandlerStart == null) return null;
        foreach (var eh in m.Body.ExceptionHandlers)
            if (eh.HandlerType == ExceptionHandlerType.Catch && eh.TryStart != null && eh.HandlerEnd != null
                && at[eh.TryStart] > k + 4 && at[eh.HandlerEnd] <= at[fin.HandlerStart]) { if (cat != null) return null; cat = eh; }
        if (cat == null) return null;
        int last = at[cat.HandlerEnd] - 1;
        if (ins[last].OpCode.Code != Code.Leave && ins[last].OpCode.Code != Code.Leave_S) return null;
        var x = ins[last].Operand as Instruction;
        if (x == null || !ins[last - 1].IsStloc() || !x.IsLdloc()) return null;
        r.Ret = ins[last - 1].GetLocal(vars);
        r.End = at[x];
        if (x.GetLocal(vars) != r.Ret || r.End + 1 >= ins.Count) return null;
        var before = ins[r.End - 1];
        if ((before.OpCode.Code != Code.Br && before.OpCode.Code != Code.Br_S) || before.Operand != ins[r.End + 1]) return null;
        r.Handlers.Add(cat); r.Handlers.Add(fin);

        // Locals in order of first appearance; the one that is read first is the argument.
        for (int i = r.Start; i <= r.End; i++)
        {
            var l = LocalOf(ins[i], vars);
            if (l == null || r.Locals.Contains(l)) continue;
            r.Locals.Add(l);
            if (ins[i].IsLdloc()) { if (r.Arg != null) return null; r.Arg = l; }
        }
        if (r.Arg == null) return null;

        for (int i = r.Start; i <= r.End; i++)
        {
            var op = ins[i].Operand;
            var l = LocalOf(ins[i], vars);
            string text;
            var target = op as Instruction; var tp = op as IMDTokenProvider;
            if (l != null) text = "L" + r.Locals.IndexOf(l);
            else if (target != null)
            {
                int t = at[target];
                if (t == r.End + 1) { if (i != r.End - 1) return null; text = "end"; }
                else if (t < r.Start || t > r.End) return null;
                else text = "@" + (t - r.Start);
            }
            else if (op is IList<Instruction>) return null;
            else if (tp != null) text = "0x" + tp.MDToken.Raw.ToString("X8");
            else if (op is string) text = "\"" + op + "\"";
            else text = op == null ? "" : op.ToString();
            r.Lines.Add(OpName(ins[i]) + " " + text);
        }
        foreach (var eh in r.Handlers)
            r.Lines.Add("eh " + eh.HandlerType + " " + (at[eh.TryStart] - k) + " " + (at[eh.TryEnd] - k) + " " + (at[eh.HandlerStart] - k)
                + " " + (at[eh.HandlerEnd] - k) + " " + (eh.CatchType == null ? "" : "0x" + eh.CatchType.MDToken.Raw.ToString("X8")));
        return r;
    }

    // lockField: the static field the inlined methods lock on. Its type gets them back.
    // names: return type -> method name, for each distinct body found.
    public List<MethodDef> UndoBodies(string kind, uint lockField, Dictionary<string, string> names)
    {
        var restoredAll = new List<MethodDef>();
        var field = mod.ResolveToken(lockField) as FieldDef;
        if (field == null) { Problems.Add("field 0x" + lockField.ToString("X8") + " not found"); return restoredAll; }
        var templates = new Dictionary<string, Region>();
        var order = new List<string>();
        var work = new List<Work>();
        foreach (var m in Bodies())
        {
            var ins = m.Body.Instructions; var vars = m.Body.Variables;
            var sites = new List<Site>();
            string id = "0x" + m.MDToken.Raw.ToString("X8");
            for (int k = 1; k + 12 < ins.Count; k++)
            {
                if (ins[k].OpCode.Code != Code.Ldsfld || ins[k].Operand != field || !ins[k + 1].IsStloc()) continue;
                var r = ReadRegion(m, k);
                if (r == null) { Problems.Add(id + ": the code at instruction " + k + " does not have the expected layout"); break; }
                // Copies of one method are identical instruction for instruction.
                string shape = string.Join("\n", r.Lines.ToArray());
                if (!templates.ContainsKey(shape)) { templates[shape] = r; order.Add(shape); }

                // Before: stloc Arg, then the caller's stack parked. After: the same unparked.
                var parked = new List<Local>();
                int first = k - 1;
                while (first > 0 && ins[first].IsStloc() && ins[first].GetLocal(vars) != r.Arg) { parked.Add(ins[first].GetLocal(vars)); first--; }
                if (!ins[first].IsStloc() || ins[first].GetLocal(vars) != r.Arg) { Problems.Add(id + ": no argument store before instruction " + k); break; }
                int end = r.End;
                var s = new Site { Index = first, Shape = shape };
                s.Locals.AddRange(r.Locals);
                s.Handlers.AddRange(r.Handlers);
                if (parked.Count > 0)
                {
                    bool ok = end + parked.Count + 2 < ins.Count && ins[end + 1].IsStloc();
                    Local result = ok ? ins[end + 1].GetLocal(vars) : null;
                    for (int j = 0; ok && j < parked.Count; j++)
                        ok = ins[end + 2 + j].IsLdloc() && ins[end + 2 + j].GetLocal(vars) == parked[j];
                    ok = ok && ins[end + 2 + parked.Count].IsLdloc() && ins[end + 2 + parked.Count].GetLocal(vars) == result;
                    if (!ok) { Problems.Add(id + ": the parked stack is not restored after instruction " + end); break; }
                    s.Locals.AddRange(parked); s.Locals.Add(result);
                    end += parked.Count + 2;
                }
                s.Removed = end - first;
                sites.Add(s);
                k = end;
            }
            if (sites.Count == 0) continue;
            if (!Enclosed(m, sites)) { Problems.Add(id + ": a site is entered from outside"); continue; }
            if (!Dedicated(m, sites)) { Problems.Add(id + ": a local of a site is used elsewhere"); continue; }
            work.Add(new Work { Method = m, Sites = sites });
        }
        if (Problems.Count > 0) return restoredAll;
        if (order.Count == 0) { Problems.Add(kind + ": no site found"); return restoredAll; }

        var byShape = new Dictionary<string, Result>();
        foreach (var shape in order)
        {
            var template = templates[shape];
            string name;
            if (!names.TryGetValue(template.Ret.Type.FullName, out name))
            { Problems.Add(kind + ": no name given for the body that returns " + template.Ret.Type.FullName + " (first copy in 0x" + template.Method.MDToken.Raw.ToString("X8") + ")"); return restoredAll; }
            var restored = Build(field.DeclaringType, name, template);
            if (restored == null) return restoredAll;
            restoredAll.Add(restored);
            var res = new Result { Kind = kind, Method = restored };
            byShape[shape] = res;
            Results.Add(res);
        }
        foreach (var w in work)
        {
            var count = new Dictionary<string, int>();
            foreach (var s in w.Sites)
            {
                s.Callee = byShape[s.Shape].Method;
                int n; count.TryGetValue(s.Shape, out n); count[s.Shape] = n + 1;
            }
            Rewrite(w.Method, w.Sites, null);
            foreach (var kv in count)
            {
                byShape[kv.Key].Sites += kv.Value; byShape[kv.Key].Methods++;
                Touched.Add("0x" + w.Method.MDToken.Raw.ToString("X8") + "\t" + kv.Value + "\t" + kind + ":" + byShape[kv.Key].Method.Name);
            }
        }
        return restoredAll;
    }

    // The method, from one copy: the argument local becomes the parameter again, the jump
    // to the end and the end itself become 'ret'.
    MethodDef Build(TypeDef host, string name, Region template)
    {
        var sig = MethodSig.CreateStatic(template.Ret.Type, template.Arg.Type);
        var restored = Create(host, name, sig, new Instruction[0]);
        if (restored == null) return null;
        var body = restored.Body;
        body.InitLocals = true;
        var src = template.Method.Body.Instructions; var srcVars = template.Method.Body.Variables;
        var locals = new Dictionary<Local, Local>();
        foreach (var l in template.Locals)
            if (l != template.Arg) { var n = new Local(l.Type); body.Variables.Add(n); locals[l] = n; }
        var map = new Dictionary<Instruction, Instruction>();
        var param = restored.Parameters[0];
        for (int i = template.Start; i <= template.End; i++)
        {
            var o = src[i];
            var l = LocalOf(o, srcVars);
            Instruction n;
            if (l == template.Arg)
            {
                if (o.IsLdloc()) n = OpCodes.Ldarg_0.ToInstruction();
                else if (o.IsStloc()) n = new Instruction(OpCodes.Starg_S, param);
                else n = new Instruction(OpCodes.Ldarga_S, param);
            }
            else if (l != null)
            {
                n = new Instruction(OpCodes.Nop, null);
                body.Instructions.Add(n);
                // Encode needs the instruction kind: give it the long form first.
                n.OpCode = o.IsLdloc() ? OpCodes.Ldloc : o.IsStloc() ? OpCodes.Stloc : OpCodes.Ldloca;
                Encode(n, locals[l]);
                map[o] = n;
                continue;
            }
            else if (i == template.End - 1) n = OpCodes.Ret.ToInstruction();
            else n = new Instruction(o.OpCode, o.Operand);
            body.Instructions.Add(n);
            map[o] = n;
        }
        body.Instructions.Add(OpCodes.Ret.ToInstruction());
        foreach (var n in body.Instructions)
        {
            var t = n.Operand as Instruction;
            if (t != null) n.Operand = map[t];
        }
        foreach (var eh in template.Handlers)
        {
            var n = new ExceptionHandler(eh.HandlerType);
            n.TryStart = map[eh.TryStart]; n.TryEnd = map[eh.TryEnd];
            n.HandlerStart = map[eh.HandlerStart]; n.HandlerEnd = map[eh.HandlerEnd];
            n.CatchType = eh.CatchType;
            body.ExceptionHandlers.Add(n);
        }
        return restored;
    }

    // ---- Shared body -----------------------------------------------------------------

    // The code from 'start' on, written so that two copies of the same pasted method read
    // the same: locals by order of first appearance, branch targets relative to 'start'.
    static List<string> Canon(MethodDef m, int start)
    {
        var ins = m.Body.Instructions; var vars = m.Body.Variables;
        var at = new Dictionary<Instruction, int>();
        for (int i = 0; i < ins.Count; i++) at[ins[i]] = i;
        var seen = new List<Local>();
        var lines = new List<string>();
        for (int i = start; i < ins.Count; i++)
        {
            var op = ins[i].Operand;
            var l = LocalOf(ins[i], vars);
            var target = op as Instruction; var targets = op as IList<Instruction>; var tp = op as IMDTokenProvider;
            string text;
            if (l != null) { if (!seen.Contains(l)) seen.Add(l); text = "L" + seen.IndexOf(l); }
            else if (target != null) text = "@" + (at[target] - start);
            else if (targets != null) { text = ""; foreach (var t in targets) text += "@" + (at[t] - start); }
            else if (tp != null) text = "0x" + tp.MDToken.Raw.ToString("X8");
            else if (op is string) text = "\"" + op + "\"";
            else text = op == null ? "" : op.ToString();
            lines.Add(OpName(ins[i]) + " " + text);
        }
        return lines;
    }

    // Index where the code that pushes the last 'count' stack values before 'end' begins,
    // in straight-line code; -1 when it cannot be told.
    static int Begin(IList<Instruction> ins, int end, int count)
    {
        if (count == 0) return end;
        int need = count;
        for (int i = end - 1; i >= 0; i--)
        {
            var flow = ins[i].OpCode.FlowControl;
            if (flow == FlowControl.Branch || flow == FlowControl.Cond_Branch || flow == FlowControl.Return || flow == FlowControl.Throw) return -1;
            int pushes, pops;
            ins[i].CalculateStackUsage(out pushes, out pops);
            if (pops < 0) return -1;
            need += pops - pushes;
            if (need <= 0) return need == 0 ? i : -1;
        }
        return -1;
    }

    // A void instance method pasted at several sites, found by 'ldloc X ; ldloc X ; ldfld
    // field' at its start: the 'this.field++' of its first statement, 'this' being a local
    // now. Nothing marks the end of a copy. It is taken where the copies stop being
    // identical, cut back to the farthest place their own branches go to: a 'ret' became a
    // jump to the end.
    public MethodDef UndoShared(string kind, uint anchorField, string name)
    {
        var field = mod.ResolveToken(anchorField) as FieldDef;
        if (field == null) { Problems.Add("field 0x" + anchorField.ToString("X8") + " not found"); return null; }
        var host = field.DeclaringType;
        var where = new List<KeyValuePair<MethodDef, int>>();
        foreach (var m in Bodies())
        {
            var ins = m.Body.Instructions; var vars = m.Body.Variables;
            for (int k = 0; k + 2 < ins.Count; k++)
            {
                if (!ins[k].IsLdloc() || !ins[k + 1].IsLdloc() || ins[k + 2].OpCode.Code != Code.Ldfld || ins[k + 2].Operand != field) continue;
                if (ins[k].GetLocal(vars) != ins[k + 1].GetLocal(vars)) continue;
                where.Add(new KeyValuePair<MethodDef, int>(m, k));
            }
        }
        if (where.Count < 2) { Problems.Add(kind + ": " + where.Count + " copy found, two are needed to tell where the code ends"); return null; }

        var first = Canon(where[0].Key, where[0].Value);
        int common = first.Count;
        for (int c = 1; c < where.Count; c++)
        {
            var other = Canon(where[c].Key, where[c].Value);
            int n = 0;
            while (n < common && n < other.Count && other[n] == first[n]) n++;
            common = n;
        }
        // The end: the farthest target of a branch of the common code that stays within it.
        var tm = where[0].Key; int ts = where[0].Value;
        var tins = tm.Body.Instructions;
        var tat = new Dictionary<Instruction, int>();
        for (int i = 0; i < tins.Count; i++) tat[tins[i]] = i;
        int length = 0;
        for (int i = 0; i < common; i++)
        {
            var t = tins[ts + i].Operand as Instruction;
            if (t == null) continue;
            int rel = tat[t] - ts;
            if (rel <= common && rel > length) length = rel;
        }
        if (length < 3) { Problems.Add(kind + ": the end of the pasted code cannot be told (" + common + " common instructions)"); return null; }
        for (int i = 0; i < length; i++)
        {
            var t = tins[ts + i].Operand as Instruction;
            if (tins[ts + i].Operand is IList<Instruction>) { Problems.Add(kind + ": switch in the pasted code"); return null; }
            if (t != null && (tat[t] < ts || tat[t] > ts + length)) { Problems.Add(kind + ": the pasted code jumps out of itself"); return null; }
        }
        var before = tins[ts + length - 1].OpCode.FlowControl;
        if (before != FlowControl.Next && before != FlowControl.Call) { Problems.Add(kind + ": the pasted code does not fall through to its end"); return null; }
        return Restore(kind, host, name, where, length);
    }

    // ---- Single copy -----------------------------------------------------------------

    // A void instance method without parameters pasted once, into a method of its own type:
    //
    //     ldarg.0 ; stloc X ; <body, 'ldloc X' for 'this'>
    //
    // X has the declaring type, this one store, and is not what a 'lock (this)' holds. With a
    // single copy there is nothing to compare: the code ends at the farthest place its own
    // branches go to (a 'ret' became a jump to the end), past the last use of X and of the
    // locals that came with it, where the stack is empty again.
    //
    // names: a field of the type that the pasted code reaches through X -> method name. A
    // copy that no name claims is an error: nothing is restored unnamed.
    public List<MethodDef> UndoSelf(string kind, Dictionary<uint, string> names)
    {
        var restored = new List<MethodDef>();
        var found = new List<KeyValuePair<MethodDef, int>>();
        foreach (var m in Bodies())
        {
            if (m.IsStatic) continue;
            var ins = m.Body.Instructions; var vars = m.Body.Variables;
            for (int k = 0; k + 2 < ins.Count; k++)
            {
                if (ins[k].OpCode.Code != Code.Ldarg_0 || !ins[k + 1].IsStloc()) continue;
                var x = ins[k + 1].GetLocal(vars);
                if (x == null || x.Type.IsValueType || !new SigComparer().Equals(x.Type.ToTypeDefOrRef(), m.DeclaringType)) continue;
                int stores = 0, loads = 0; bool held = false;
                for (int i = 0; i < ins.Count; i++)
                {
                    if (LocalOf(ins[i], vars) != x) continue;
                    if (ins[i].IsStloc()) { stores++; continue; }
                    loads++;
                    // lock (this): the copy goes to Monitor.Enter, directly or after 'ldloca taken'.
                    for (int j = i + 1; j <= i + 2 && j < ins.Count; j++)
                    {
                        var callee = ins[j].Operand as IMethod;
                        if (callee != null && callee.DeclaringType != null && callee.DeclaringType.FullName == "System.Threading.Monitor") held = true;
                    }
                }
                if (stores != 1 || loads == 0 || held) continue;
                found.Add(new KeyValuePair<MethodDef, int>(m, k + 2));
            }
        }
        // Last copy of a method first: the indices of the copies before it do not move.
        for (int n = found.Count - 1; n >= 0; n--)
        {
            var m = found[n].Key; int start = found[n].Value;
            var ins = m.Body.Instructions; var vars = m.Body.Variables;
            string id = "0x" + m.MDToken.Raw.ToString("X8") + " at instruction " + start;
            var x = ins[start - 1].GetLocal(vars);
            int length = SelfLength(m, start, x);
            if (length < 0) { Problems.Add(kind + ": " + id + ": the end of the pasted code cannot be told"); continue; }
            string name = null;
            for (int i = start; i + 1 < start + length && name == null; i++)
            {
                var f = ins[i + 1].Operand as IField;
                if (ins[i].IsLdloc() && ins[i].GetLocal(vars) == x && f != null) names.TryGetValue(f.MDToken.Raw, out name);
            }
            if (name == null) { Problems.Add(kind + ": " + id + ": pasted code that no name is given for"); continue; }
            var where = new List<KeyValuePair<MethodDef, int>> { new KeyValuePair<MethodDef, int>(m, start) };
            var r = Restore(kind + ":" + name, m.DeclaringType, name, where, length);
            if (r != null) restored.Insert(0, r);
        }
        return restored;
    }

    // Number of instructions of the code pasted at 'start', -1 when it cannot be told.
    static int SelfLength(MethodDef m, int start, Local x)
    {
        var ins = m.Body.Instructions; var vars = m.Body.Variables;
        var at = new Dictionary<Instruction, int>();
        for (int i = 0; i < ins.Count; i++) at[ins[i]] = i;
        var last = new Dictionary<Local, int>();
        for (int i = 0; i < ins.Count; i++) { var l = LocalOf(ins[i], vars); if (l != null) last[l] = i; }
        int end = last[x] + 1;   // exclusive
        while (true)
        {
            int grown = end;
            for (int i = start; i < grown && i < ins.Count; i++)
            {
                var t = ins[i].Operand as Instruction; var ts = ins[i].Operand as IList<Instruction>;
                if (t != null && at[t] > grown) grown = at[t];
                if (ts != null) foreach (var y in ts) if (at[y] > grown) grown = at[y];
                var l = LocalOf(ins[i], vars);
                if (l != null && last[l] + 1 > grown) grown = last[l] + 1;
            }
            // Up to the end of the statement: the stack is empty where the call returned.
            var known = new Dictionary<int, int>();
            int depth = 0, stop = -1;
            for (int i = start; i < ins.Count; i++)
            {
                int d;
                if (known.TryGetValue(i, out d)) depth = d;
                if (i >= grown && depth == 0) { stop = i; break; }
                if (depth < 0) { if (i >= grown) break; continue; }
                int pushes, pops;
                ins[i].CalculateStackUsage(out pushes, out pops);
                if (pops < 0) break;
                depth += pushes - pops;
                var t = ins[i].Operand as Instruction; var ts = ins[i].Operand as IList<Instruction>;
                if (t != null) known[at[t]] = depth;
                if (ts != null) foreach (var y in ts) known[at[y]] = depth;
                var flow = ins[i].OpCode.FlowControl;
                if (flow == FlowControl.Branch || flow == FlowControl.Throw || flow == FlowControl.Return) depth = -1;
            }
            if (stop < 0) return -1;
            if (stop == end) break;
            end = stop;
        }
        for (int i = start; i < end; i++)
        {
            var t = ins[i].Operand as Instruction; var ts = ins[i].Operand as IList<Instruction>;
            if (ins[i].OpCode.Code == Code.Ret || ts != null) return -1;
            if (t != null && (at[t] < start || at[t] > end)) return -1;
        }
        return end - start;
    }

    // ---- Restoring a pasted instance method -------------------------------------------

    // where: the first instruction of each copy; length: their common number of instructions.
    MethodDef Restore(string kind, TypeDef host, string name, List<KeyValuePair<MethodDef, int>> where, int length)
    {
        var tm = where[0].Key; int ts = where[0].Value;
        var tins = tm.Body.Instructions;
        var tat = new Dictionary<Instruction, int>();
        for (int i = 0; i < tins.Count; i++) tat[tins[i]] = i;

        var work = new List<Work>();
        List<Local> templateArgs = null, templateOwn = null;
        var templateHandlers = new List<ExceptionHandler>();
        foreach (var w in where)
        {
            var m = w.Key; int start = w.Value;
            var ins = m.Body.Instructions; var vars = m.Body.Variables;
            string id = "0x" + m.MDToken.Raw.ToString("X8");
            var at = new Dictionary<Instruction, int>();
            for (int i = 0; i < ins.Count; i++) at[ins[i]] = i;
            // Read before written: an argument. Written first: a local of the pasted method.
            var args = new List<Local>(); var own = new List<Local>();
            for (int i = start; i < start + length; i++)
            {
                var l = LocalOf(ins[i], vars);
                if (l == null || args.Contains(l) || own.Contains(l)) continue;
                if (ins[i].IsStloc()) own.Add(l); else args.Add(l);
            }
            // The arguments were popped last one first: the first one is stored just before the code.
            var ordered = new List<Local>();
            bool ok = start >= args.Count;
            for (int j = 0; ok && j < args.Count; j++)
            {
                var i = ins[start - 1 - j];
                var l = i.IsStloc() ? i.GetLocal(vars) : null;
                ok = l != null && args.Contains(l) && !ordered.Contains(l);
                if (ok) ordered.Add(l);
            }
            if (!ok || ordered.Count == 0) { Problems.Add(id + ": no argument stores before instruction " + start); continue; }
            if (!new SigComparer().Equals(ordered[0].Type.ToTypeDefOrRef(), host)) { Problems.Add(id + ": the first argument is not a " + host.FullName); continue; }

            var s = new Site { Index = start - ordered.Count, Removed = ordered.Count - 1 + length };
            s.Locals.AddRange(ordered); s.Locals.AddRange(own);
            foreach (var eh in m.Body.ExceptionHandlers)
            {
                int inside = 0, total = 0;
                foreach (var x in new[] { eh.TryStart, eh.TryEnd, eh.HandlerStart, eh.HandlerEnd, eh.FilterStart })
                {
                    if (x == null) continue;
                    total++;
                    if (at[x] >= start && at[x] <= start + length) inside++;
                }
                if (eh.TryStart != null && at[eh.TryStart] >= start && at[eh.TryStart] < start + length)
                {
                    if (inside != total) { Problems.Add(id + ": a handler crosses the end of the pasted code"); ok = false; }
                    s.Handlers.Add(eh);
                }
            }
            if (!ok) continue;
            // C# calls an instance method with 'call' on a bare 'this', with 'callvirt' otherwise.
            int all = Begin(ins, s.Index, ordered.Count), rest = Begin(ins, s.Index, ordered.Count - 1);
            bool bareThis = !m.IsStatic && m.DeclaringType == host && all >= 0 && rest == all + 1 && ins[all].OpCode.Code == Code.Ldarg_0;
            s.Virtual = !bareThis;
            var sites = new List<Site> { s };
            if (!Enclosed(m, sites)) { Problems.Add(id + ": the site is entered from outside"); continue; }
            if (!Dedicated(m, sites)) { Problems.Add(id + ": a local of the site is used elsewhere"); continue; }
            Work existing = null;
            foreach (var x in work) if (x.Method == m) existing = x;
            if (existing != null) { Problems.Add(id + ": two copies in one method"); continue; }
            work.Add(new Work { Method = m, Sites = sites });
            if (templateArgs == null) { templateArgs = ordered; templateOwn = own; templateHandlers = s.Handlers; }
        }
        if (Problems.Count > 0) return null;

        // The method, from the first copy.
        var sig = MethodSig.CreateInstance(mod.CorLibTypes.Void);
        for (int j = 1; j < templateArgs.Count; j++) sig.Params.Add(templateArgs[j].Type);
        var restored = Create(host, name, sig, new Instruction[0]);
        if (restored == null) return null;
        var body = restored.Body;
        body.InitLocals = true;
        var locals = new Dictionary<Local, Local>();
        foreach (var l in templateOwn) { var n = new Local(l.Type); body.Variables.Add(n); locals[l] = n; }
        var map = new Dictionary<Instruction, Instruction>();
        var end = OpCodes.Ret.ToInstruction();
        var tvars = tm.Body.Variables;
        for (int i = ts; i < ts + length; i++)
        {
            var o = tins[i];
            var l = LocalOf(o, tvars);
            Instruction n;
            int arg = l == null ? -1 : templateArgs.IndexOf(l);
            if (arg >= 0)
            {
                var p = restored.Parameters[arg];
                if (o.IsStloc()) n = new Instruction(OpCodes.Starg_S, p);
                else if (!o.IsLdloc()) n = new Instruction(OpCodes.Ldarga_S, p);
                else if (arg == 0) n = OpCodes.Ldarg_0.ToInstruction();
                else if (arg == 1) n = OpCodes.Ldarg_1.ToInstruction();
                else if (arg == 2) n = OpCodes.Ldarg_2.ToInstruction();
                else if (arg == 3) n = OpCodes.Ldarg_3.ToInstruction();
                else n = new Instruction(OpCodes.Ldarg_S, p);
            }
            else if (l != null)
            {
                n = new Instruction(o.IsLdloc() ? OpCodes.Ldloc : o.IsStloc() ? OpCodes.Stloc : OpCodes.Ldloca, null);
                Encode(n, locals[l]);
            }
            else if ((o.OpCode.Code == Code.Br || o.OpCode.Code == Code.Br_S) && o.Operand == tins[ts + length]) n = OpCodes.Ret.ToInstruction();
            else n = new Instruction(o.OpCode, o.Operand);
            body.Instructions.Add(n);
            map[o] = n;
        }
        map[tins[ts + length]] = end;
        body.Instructions.Add(end);
        foreach (var n in body.Instructions)
        {
            var t = n.Operand as Instruction;
            if (t != null) n.Operand = map[t];
        }
        foreach (var eh in templateHandlers)
        {
            var n = new ExceptionHandler(eh.HandlerType);
            n.TryStart = map[eh.TryStart]; n.TryEnd = map[eh.TryEnd];
            n.HandlerStart = map[eh.HandlerStart]; n.HandlerEnd = map[eh.HandlerEnd];
            if (eh.FilterStart != null) n.FilterStart = map[eh.FilterStart];
            n.CatchType = eh.CatchType;
            body.ExceptionHandlers.Add(n);
        }
        Apply(kind, restored, work);
        return restored;
    }

    // ---- Static method pasted around a parked stack ---------------------------------------
    //
    // A static method with protected blocks, pasted in the middle of an expression:
    //
    //     stloc An .. stloc A1 ; stloc P1 .. stloc Pm ; <body> ; stloc R ; ldloc Pm .. ldloc P1 ; ldloc R
    //
    // A1..An are the arguments, popped last one first; P1..Pm what the caller had on the
    // stack (a protected block is entered with an empty stack); R the returned value. Each
    // of P and R is stored once and read once. No C# gives this: a statement with a try
    // block in the middle of an expression. The body ends where R is stored: a 'ret' became
    // a jump there, the value on the stack.

    sealed class Parked { public int Args, Body, End, Count; }

    static int Uses(MethodDef m, Local l, bool stores)
    {
        int n = 0;
        foreach (var i in m.Body.Instructions)
        {
            if (LocalOf(i, m.Body.Variables) != l) continue;
            if (i.IsStloc() == stores) n++;
        }
        return n;
    }

    // The shape at 'stloc R' = instruction e, or null.
    static Parked ReadParked(MethodDef m, int e)
    {
        var ins = m.Body.Instructions; var vars = m.Body.Variables;
        if (!ins[e].IsStloc()) return null;
        var r = ins[e].GetLocal(vars);
        if (r == null || Uses(m, r, true) != 1 || Uses(m, r, false) != 1) return null;
        var parked = new List<Local>();
        int k = e + 1;
        while (k < ins.Count && ins[k].IsLdloc() && ins[k].GetLocal(vars) != r)
        {
            var p = ins[k].GetLocal(vars);
            if (p == null || parked.Contains(p) || Uses(m, p, true) != 1 || Uses(m, p, false) != 1) return null;
            parked.Add(p); k++;
        }
        if (parked.Count == 0 || k >= ins.Count || !ins[k].IsLdloc() || ins[k].GetLocal(vars) != r) return null;
        // The stores of the parked values, in the reverse order of their loads.
        int first = -1;
        for (int i = 0; i < e; i++)
            if (ins[i].IsStloc() && ins[i].GetLocal(vars) == parked[parked.Count - 1]) first = i;
        if (first < 0 || first + parked.Count > e) return null;
        for (int j = 0; j < parked.Count; j++)
        {
            var i = ins[first + j];
            if (!i.IsStloc() || i.GetLocal(vars) != parked[parked.Count - 1 - j]) return null;
        }
        int body = first + parked.Count;
        // A protected block inside: the reason for parking.
        var at = new Dictionary<Instruction, int>();
        for (int i = 0; i < ins.Count; i++) at[ins[i]] = i;
        bool guarded = false;
        foreach (var eh in m.Body.ExceptionHandlers)
            if (eh.TryStart != null && at[eh.TryStart] >= body && at[eh.TryStart] < e) guarded = true;
        if (!guarded) return null;
        // The arguments: stores just before, to locals written there only and read in the body only.
        int a = first;
        while (a > 0 && ins[a - 1].IsStloc())
        {
            var l = ins[a - 1].GetLocal(vars);
            if (l == null || Uses(m, l, true) != 1) break;
            bool inside = true;
            for (int i = 0; i < ins.Count; i++)
                if (i != a - 1 && LocalOf(ins[i], vars) == l && (i < body || i >= e)) inside = false;
            if (!inside) break;
            a--;
        }
        return new Parked { Args = a, Body = body, End = e, Count = parked.Count };
    }

    // Every pasted body of a method. When a method was pasted several times into the same
    // caller, the copies share the locals of its arguments and its own (only the parked
    // values and the result have locals of their own at each site): an argument is then a
    // local stored in the run of stores before a site and used nowhere but in the pasted
    // bodies and in those runs.
    static List<Parked> ParkedSites(MethodDef m)
    {
        var ins = m.Body.Instructions; var vars = m.Body.Variables;
        var sites = new List<Parked>();
        for (int e = 0; e < ins.Count; e++)
        {
            var p = ReadParked(m, e);
            if (p != null) sites.Add(p);
        }
        if (sites.Count < 2) return sites;
        var allowed = new HashSet<int>();
        foreach (var p in sites)
        {
            for (int i = p.Body; i < p.End; i++) allowed.Add(i);
            for (int i = p.Body - p.Count - 1; i >= 0 && ins[i].IsStloc(); i--) allowed.Add(i);
        }
        foreach (var p in sites)
        {
            int a = p.Body - p.Count;
            while (a > 0 && ins[a - 1].IsStloc())
            {
                var l = ins[a - 1].GetLocal(vars);
                if (l == null) break;
                bool only = true, read = false;
                for (int i = 0; i < ins.Count && only; i++)
                {
                    if (LocalOf(ins[i], vars) != l) continue;
                    if (!allowed.Contains(i)) only = false;
                    if (i >= p.Body && i < p.End) read = true;
                }
                if (!only || !read) break;
                a--;
            }
            p.Args = a;
        }
        return sites;
    }

    sealed class Copy
    {
        public MethodDef Method; public Parked P; public Site Site;
        public List<Local> Args = new List<Local>(), Own = new List<Local>();
        public Local Result; public string Signature, Shape;
    }

    // One pasted body read for what it needs to become a call: its arguments (in parameter
    // order: the first one was stored last), its own locals, its handlers, and its code
    // written so that two copies of one method read the same.
    Copy ReadCopy(MethodDef m, Parked p)
    {
        var ins = m.Body.Instructions; var vars = m.Body.Variables;
        string id = "0x" + m.MDToken.Raw.ToString("X8");
        var at = new Dictionary<Instruction, int>();
        for (int i = 0; i < ins.Count; i++) at[ins[i]] = i;
        var c = new Copy { Method = m, P = p };
        int parkedFrom = p.Body - p.Count;
        for (int i = parkedFrom - 1; i >= p.Args; i--) c.Args.Add(ins[i].GetLocal(vars));
        for (int i = p.Body; i < p.End; i++)
        {
            var l = LocalOf(ins[i], vars);
            if (l == null || c.Args.Contains(l) || c.Own.Contains(l)) continue;
            if (!ins[i].IsStloc()) { Problems.Add(id + ": a local of the pasted body is read before it is written"); return null; }
            c.Own.Add(l);
        }
        c.Result = ins[p.End].GetLocal(vars);
        var s = new Site { Index = p.Args, Removed = p.End + p.Count + 1 - p.Args };
        s.Locals.AddRange(c.Args); s.Locals.AddRange(c.Own); s.Locals.Add(c.Result);
        for (int j = 0; j < p.Count; j++) s.Locals.Add(ins[parkedFrom + j].GetLocal(vars));
        foreach (var eh in m.Body.ExceptionHandlers)
        {
            if (eh.TryStart == null || at[eh.TryStart] < p.Body || at[eh.TryStart] >= p.End) continue;
            foreach (var x in new[] { eh.TryEnd, eh.HandlerStart, eh.HandlerEnd, eh.FilterStart })
                if (x != null && (at[x] < p.Body || at[x] > p.End)) { Problems.Add(id + ": a handler crosses the end of the pasted code"); return null; }
            s.Handlers.Add(eh);
        }
        c.Site = s;
        var types = new List<string>();
        foreach (var a in c.Args) types.Add(a.Type.FullName);
        c.Signature = c.Result.Type.FullName + "(" + string.Join(",", types.ToArray()) + ")";

        var lines = new List<string>();
        for (int i = p.Body; i < p.End; i++)
        {
            var op = ins[i].Operand;
            var l = LocalOf(ins[i], vars);
            var target = op as Instruction; var tp = op as IMDTokenProvider;
            string text;
            if (l != null) text = c.Args.Contains(l) ? "A" + c.Args.IndexOf(l) : "L" + c.Own.IndexOf(l);
            else if (target != null) text = "@" + (at[target] - p.Body);
            else if (op is IList<Instruction>) { text = ""; foreach (var t in (IList<Instruction>)op) text += "@" + (at[t] - p.Body); }
            else if (tp != null) text = "0x" + tp.MDToken.Raw.ToString("X8");
            else if (op is string) text = "\"" + op + "\"";
            else text = op == null ? "" : op.ToString();
            lines.Add(OpName(ins[i]) + " " + text);
        }
        var regions = new List<string>();
        foreach (var eh in s.Handlers)
            regions.Add("eh " + eh.HandlerType + " " + (at[eh.TryStart] - p.Body) + " " + (at[eh.TryEnd] - p.Body) + " " + (at[eh.HandlerStart] - p.Body)
                + " " + (at[eh.HandlerEnd] - p.Body) + " " + (eh.CatchType == null ? "" : "0x" + eh.CatchType.MDToken.Raw.ToString("X8")));
        regions.Sort(StringComparer.Ordinal);
        lines.AddRange(regions);
        c.Shape = string.Join("\n", lines.ToArray());
        return c;
    }

    // Every pasted body left: token, signature, parked count, length, name.
    public List<string> FindParked()
    {
        var found = new List<string>();
        foreach (var m in Bodies())
            foreach (var p in ParkedSites(m))
            {
                var c = ReadCopy(m, p);
                found.Add("0x" + m.MDToken.Raw.ToString("X8") + "\t" + (c == null ? "?" : c.Signature) + "\t" + p.Count + " parked\t" + (p.End - p.Body) + " instructions\t" + m.FullName);
            }
        return found;
    }

    // The pasted bodies of every method, by signature ("Ret(Arg,Arg)", full type names):
    // 'hosts' gives the type that gets the method back, 'names' its name. Copies of one
    // signature must be the same code. A signature that is not named is left as it is
    // (FindParked lists it). A pasted body can hold another one (the obfuscator pasted
    // into a method it then pasted): the innermost are undone first, round after round.
    public List<MethodDef> UndoParked(string kind, List<string> order, Dictionary<string, uint> hosts, Dictionary<string, string> names)
    {
        var restoredAll = new List<MethodDef>();
        var restoredBy = new Dictionary<string, MethodDef>();
        var shapeBy = new Dictionary<string, string>();
        var results = new Dictionary<string, Result>();
        while (true)
        {
            var bySignature = new Dictionary<string, List<Copy>>();
            var byMethod = new Dictionary<MethodDef, List<Copy>>();
            var methods = new List<MethodDef>();
            foreach (var m in Bodies())
            {
                var found = ParkedSites(m);
                foreach (var p in found)
                {
                    bool holds = false;
                    foreach (var q in found) if (q != p && q.Args >= p.Body && q.End < p.End) holds = true;
                    if (holds) continue;
                    var c = ReadCopy(m, p);
                    if (c == null) return restoredAll;
                    if (!hosts.ContainsKey(c.Signature) || !names.ContainsKey(c.Signature)) continue;
                    if (!bySignature.ContainsKey(c.Signature)) bySignature[c.Signature] = new List<Copy>();
                    bySignature[c.Signature].Add(c);
                    if (!byMethod.ContainsKey(m)) { byMethod[m] = new List<Copy>(); methods.Add(m); }
                    byMethod[m].Add(c);
                }
            }
            if (methods.Count == 0) break;
            foreach (var m in methods)
            {
                var sites = new List<Site>();
                foreach (var c in byMethod[m]) sites.Add(c.Site);
                string id = "0x" + m.MDToken.Raw.ToString("X8");
                if (!Enclosed(m, sites)) { Problems.Add(id + ": a pasted body is entered from outside"); return restoredAll; }
                if (!Dedicated(m, sites)) { Problems.Add(id + ": a local of a pasted body is used elsewhere"); return restoredAll; }
            }
            foreach (var signature in order)
            {
                List<Copy> copies;
                if (!bySignature.TryGetValue(signature, out copies)) continue;
                if (!shapeBy.ContainsKey(signature)) shapeBy[signature] = copies[0].Shape;
                foreach (var c in copies)
                    if (c.Shape != shapeBy[signature])
                    { Problems.Add(kind + ": the copies of " + signature + " differ (0x" + c.Method.MDToken.Raw.ToString("X8") + ")"); return restoredAll; }
                MethodDef restored;
                if (!restoredBy.TryGetValue(signature, out restored))
                {
                    var host = mod.ResolveToken(hosts[signature]) as TypeDef;
                    if (host == null) { Problems.Add(kind + ": no host type for " + signature); return restoredAll; }
                    restored = BuildParked(host, names[signature], copies[0]);
                    if (restored == null) return restoredAll;
                    restoredBy[signature] = restored;
                    restoredAll.Add(restored);
                    var res = new Result { Kind = kind, Method = restored };
                    results[signature] = res;
                    Results.Add(res);
                }
                foreach (var c in copies) c.Site.Callee = restored;
            }
            foreach (var m in methods)
            {
                var sites = new List<Site>();
                var count = new Dictionary<string, int>();
                foreach (var c in byMethod[m])
                {
                    sites.Add(c.Site);
                    int n; count.TryGetValue(c.Signature, out n); count[c.Signature] = n + 1;
                }
                sites.Sort(delegate(Site x, Site y) { return x.Index.CompareTo(y.Index); });
                Rewrite(m, sites, null);
                foreach (var kv in count)
                {
                    results[kv.Key].Sites += kv.Value; results[kv.Key].Methods++;
                    Touched.Add("0x" + m.MDToken.Raw.ToString("X8") + "\t" + kv.Value + "\t" + kind + ":" + results[kv.Key].Method.Name);
                }
            }
        }
        foreach (var signature in order)
            if (!restoredBy.ContainsKey(signature)) Problems.Add(kind + ": no pasted body with the signature " + signature);
        return restoredAll;
    }

    // The method, from one copy: the argument locals become the parameters again, a jump to
    // the end and the end itself become 'ret'.
    MethodDef BuildParked(TypeDef host, string name, Copy c)
    {
        var m = c.Method; var p = c.P;
        var ins = m.Body.Instructions; var vars = m.Body.Variables;
        var sig = MethodSig.CreateStatic(c.Result.Type);
        foreach (var a in c.Args) sig.Params.Add(a.Type);
        var restored = Create(host, name, sig, new Instruction[0]);
        if (restored == null) return null;
        var body = restored.Body;
        body.InitLocals = true;
        var locals = new Dictionary<Local, Local>();
        foreach (var l in c.Own) { var n = new Local(l.Type); body.Variables.Add(n); locals[l] = n; }
        var map = new Dictionary<Instruction, Instruction>();
        var end = OpCodes.Ret.ToInstruction();
        for (int i = p.Body; i < p.End; i++)
        {
            var o = ins[i];
            var l = LocalOf(o, vars);
            Instruction n;
            int arg = l == null ? -1 : c.Args.IndexOf(l);
            if (arg >= 0)
            {
                var prm = restored.Parameters[arg];
                if (o.IsStloc()) n = new Instruction(OpCodes.Starg_S, prm);
                else if (!o.IsLdloc()) n = new Instruction(OpCodes.Ldarga_S, prm);
                else if (arg == 0) n = OpCodes.Ldarg_0.ToInstruction();
                else if (arg == 1) n = OpCodes.Ldarg_1.ToInstruction();
                else if (arg == 2) n = OpCodes.Ldarg_2.ToInstruction();
                else if (arg == 3) n = OpCodes.Ldarg_3.ToInstruction();
                else n = new Instruction(OpCodes.Ldarg_S, prm);
            }
            else if (l != null)
            {
                n = new Instruction(o.IsLdloc() ? OpCodes.Ldloc : o.IsStloc() ? OpCodes.Stloc : OpCodes.Ldloca, null);
                Encode(n, locals[l]);
            }
            else if ((o.OpCode.Code == Code.Br || o.OpCode.Code == Code.Br_S) && o.Operand == ins[p.End]) n = OpCodes.Ret.ToInstruction();
            else n = new Instruction(o.OpCode, o.Operand);
            body.Instructions.Add(n);
            map[o] = n;
        }
        map[ins[p.End]] = end;
        body.Instructions.Add(end);
        foreach (var n in body.Instructions)
        {
            var t = n.Operand as Instruction;
            if (t != null) n.Operand = map[t];
        }
        foreach (var eh in c.Site.Handlers)
        {
            var n = new ExceptionHandler(eh.HandlerType);
            n.TryStart = map[eh.TryStart]; n.TryEnd = map[eh.TryEnd];
            n.HandlerStart = map[eh.HandlerStart]; n.HandlerEnd = map[eh.HandlerEnd];
            if (eh.FilterStart != null) n.FilterStart = map[eh.FilterStart];
            n.CatchType = eh.CatchType;
            body.ExceptionHandlers.Add(n);
        }
        // The copy's handlers are in the obfuscator's order (Rewrite): the compiler's here too.
        var sorted = new List<ExceptionHandler>();
        foreach (var h in body.ExceptionHandlers)
        {
            int k = sorted.Count;
            while (k > 0 && body.Instructions.IndexOf(sorted[k - 1].HandlerEnd) > body.Instructions.IndexOf(h.HandlerEnd)) k--;
            sorted.Insert(k, h);
        }
        body.ExceptionHandlers.Clear();
        foreach (var h in sorted) body.ExceptionHandlers.Add(h);
        return restored;
    }

    // ---- Call to an empty method ---------------------------------------------------------
    //
    // Pasting an empty static method leaves its arguments popped, last one first, into
    // locals nothing reads: 'pop ; pop ; pop' after the three values. No C# pushes three
    // values to drop them (an empty 'if' drops the two operands of its test at most).

    // Type of the value an instruction pushes, for the few instructions seen at such sites.
    TypeSig Pushed(Instruction i)
    {
        if (i.OpCode.Code == Code.Ldstr) return mod.CorLibTypes.String;
        if (i.OpCode.Code == Code.Call || i.OpCode.Code == Code.Callvirt)
        {
            var method = i.Operand as IMethod;
            if (method == null || method.MethodSig == null) return null;
            var ret = method.MethodSig.RetType;
            var gv = ret as GenericVar;
            if (gv != null)
            {
                var spec = method.DeclaringType as TypeSpec;
                var inst = spec == null ? null : spec.TypeSig as GenericInstSig;
                return inst != null && gv.Number < inst.GenericArguments.Count ? inst.GenericArguments[(int)gv.Number] : null;
            }
            return ret.ContainsGenericParameter ? null : ret;
        }
        return null;
    }

    // Runs of at least 'least' pops: token, length, name.
    public List<string> FindDropped(int least)
    {
        var found = new List<string>();
        foreach (var m in Bodies())
        {
            var ins = m.Body.Instructions;
            for (int i = 0; i < ins.Count; i++)
            {
                int n = 0;
                while (i + n < ins.Count && ins[i + n].OpCode.Code == Code.Pop) n++;
                if (n >= least) found.Add("0x" + m.MDToken.Raw.ToString("X8") + "\t" + n + " pops\t" + m.FullName);
                i += n;
            }
        }
        return found;
    }

    public MethodDef UndoDropped(string kind, uint caller, uint hostType, string name, int count)
    {
        var m = mod.ResolveToken(caller) as MethodDef;
        var host = mod.ResolveToken(hostType) as TypeDef;
        string id = "0x" + caller.ToString("X8");
        if (m == null || !m.HasBody || host == null) { Problems.Add(kind + ": no method " + id + " or no host type"); return null; }
        var ins = m.Body.Instructions;
        int at = -1;
        for (int i = 0; i < ins.Count; i++)
        {
            int n = 0;
            while (i + n < ins.Count && ins[i + n].OpCode.Code == Code.Pop) n++;
            if (n == count) { if (at >= 0) { Problems.Add(id + ": two runs of " + count + " pops"); return null; } at = i; }
            i += n;
        }
        if (at < 0) { Problems.Add(id + ": no run of " + count + " pops"); return null; }
        var sig = MethodSig.CreateStatic(mod.CorLibTypes.Void);
        for (int below = count - 1; below >= 0; below--)
        {
            int k = Pusher(m, at, below);
            var t = k < 0 ? null : Pushed(ins[k]);
            if (t == null) { Problems.Add(id + ": the type of a dropped value cannot be told"); return null; }
            sig.Params.Add(t);
        }
        var restored = Create(host, name, sig, new[] { OpCodes.Ret.ToInstruction() });
        if (restored == null) return null;
        restored.Body.InitLocals = true;
        var s = new Site { Index = at, Removed = count - 1 };
        var sites = new List<Site> { s };
        if (!Enclosed(m, sites)) { Problems.Add(id + ": the site is entered from outside"); return null; }
        Apply(kind, restored, new List<Work> { new Work { Method = m, Sites = sites } });
        return restored;
    }

    // ---- Stubs of virtualised methods ---------------------------------------------------

    // One line per kind of edit that restores no method: kind, sites, methods.
    public readonly List<string> Edits = new List<string>();

    // Index of the instruction that pushed the value 'below' places under the top of the
    // stack before instruction k; -1 when the code in between is not straight.
    static int Pusher(MethodDef m, int k, int below)
    {
        var ins = m.Body.Instructions;
        int need = below + 1;
        for (int j = k - 1; j >= 0; j--)
        {
            var flow = ins[j].OpCode.FlowControl;
            if (flow != FlowControl.Next && flow != FlowControl.Call) return -1;
            int pushes, pops;
            ins[j].CalculateStackUsage(out pushes, out pops);
            if (pops < 0) return -1;
            need -= pushes;
            if (need <= 0) return j;
            need += pops;
        }
        return -1;
    }

    // The body of a virtualised method is a stub the obfuscator wrote: it fetches the
    // interpreter from a static factory and hands it the arguments. The stub calls the
    // interpreter with 'call' and reads a returned value type with 'unbox ; ldobj'. A C#
    // compiler calls an instance method on the result of another call with 'callvirt' and
    // unboxes with 'unbox.any': both are put back, so that the stub can be written in C#.
    public void UndoStubs(string kind, uint vmType)
    {
        var vm = mod.ResolveToken(vmType) as TypeDef;
        if (vm == null) { Problems.Add("interpreter type 0x" + vmType.ToString("X8") + " not found"); return; }
        int sites = 0, methods = 0;
        foreach (var m in Bodies())
        {
            if (m.DeclaringType == vm) continue;
            var ins = m.Body.Instructions;
            int here = 0;
            for (int k = 0; k < ins.Count; k++)
            {
                if (ins[k].OpCode.Code != Code.Call) continue;
                var callee = ins[k].Operand as IMethod;
                var def = callee == null ? null : callee.ResolveMethodDef();
                if (def == null || def.DeclaringType != vm || def.IsStatic || def.IsConstructor) continue;
                int r = Pusher(m, k, def.MethodSig.Params.Count);
                if (r < 0 || ins[r].OpCode.Code != Code.Call) continue;
                ins[k].OpCode = OpCodes.Callvirt;
                here++;
                if (k + 2 < ins.Count && ins[k + 1].OpCode.Code == Code.Unbox && ins[k + 2].OpCode.Code == Code.Ldobj)
                {
                    var a = ins[k + 1].Operand as ITypeDefOrRef; var b = ins[k + 2].Operand as ITypeDefOrRef;
                    bool target = false;
                    foreach (var i in ins)
                    {
                        if (i.Operand == ins[k + 2]) target = true;
                        var many = i.Operand as IList<Instruction>;
                        if (many != null && many.Contains(ins[k + 2])) target = true;
                    }
                    if (a != null && b != null && new SigComparer().Equals(a, b) && !target)
                    {
                        ins[k + 1].OpCode = OpCodes.Unbox_Any;
                        ins.RemoveAt(k + 2);
                    }
                }
            }
            if (here == 0) continue;
            m.Body.KeepOldMaxStack = true;
            sites += here; methods++;
            Touched.Add("0x" + m.MDToken.Raw.ToString("X8") + "\t" + here + "\t" + kind);
        }
        if (sites == 0) { Problems.Add(kind + ": no site found"); return; }
        Edits.Add(kind + "\t" + sites + "\t" + methods);
    }

    // ---- Mutated constants ----------------------------------------------------------------

    static bool Referenced(MethodDef m, Instruction x)
    {
        foreach (var i in m.Body.Instructions)
        {
            if (i.Operand == x) return true;
            var many = i.Operand as IList<Instruction>;
            if (many != null && many.Contains(x)) return true;
        }
        foreach (var eh in m.Body.ExceptionHandlers)
            if (eh.TryStart == x || eh.TryEnd == x || eh.HandlerStart == x || eh.HandlerEnd == x || eh.FilterStart == x) return true;
        return false;
    }

    // In its own runtime the obfuscator writes an integer constant as another constant
    // followed by a run of 'not' and 'neg' that gives the value back ('ldc.i4 K ; not ; neg ;
    // not ...'). A compiler folds an operator applied to a constant: no compiler output has a
    // constant followed by either instruction (docs/matching.md). The run is folded back
    // into the constant, written as a compiler writes it (shortest form).
    public void UndoConstantMutation(string kind)
    {
        int sites = 0, methods = 0;
        foreach (var m in Bodies())
        {
            var ins = m.Body.Instructions;
            int here = 0;
            for (int k = 0; k + 1 < ins.Count; k++)
            {
                bool wide = ins[k].OpCode.Code == Code.Ldc_I8;
                if (!wide && !ins[k].IsLdcI4()) continue;
                long v = wide ? (long)ins[k].Operand : ins[k].GetLdcI4Value();
                int j = k + 1;
                while (j < ins.Count && (ins[j].OpCode.Code == Code.Not || ins[j].OpCode.Code == Code.Neg) && !Referenced(m, ins[j]))
                {
                    bool not = ins[j].OpCode.Code == Code.Not;
                    if (wide) v = not ? ~v : unchecked(-v);
                    else v = not ? ~(int)v : unchecked(-(int)v);
                    j++;
                }
                if (j == k + 1) continue;
                if (wide) { ins[k].OpCode = OpCodes.Ldc_I8; ins[k].Operand = v; }
                else
                {
                    var c = Instruction.CreateLdcI4((int)v);
                    ins[k].OpCode = c.OpCode; ins[k].Operand = c.Operand;
                }
                for (int x = j - 1; x > k; x--) ins.RemoveAt(x);
                here++;
            }
            if (here == 0) continue;
            m.Body.KeepOldMaxStack = true;
            sites += here; methods++;
            Touched.Add("0x" + m.MDToken.Raw.ToString("X8") + "\t" + here + "\t" + kind);
        }
        if (sites == 0) { Problems.Add(kind + ": no site found"); return; }
        Edits.Add(kind + "\t" + sites + "\t" + methods);
    }

    // ---- Literal called as the result of a call ----------------------------------------------

    // 'ldstr ; ... ; callvirt String::M': a compiler calls a method of a string literal with
    // 'call' (the literal is not null), and with 'callvirt' the result of another call. The
    // obfuscator pastes the body of a method without argument that returns a literal as the
    // bare 'ldstr': nothing else is left of the call. The call is put back, to a restored
    // static method of the caller's type that returns the literal. names: caller -> name.
    public List<MethodDef> UndoLiteralReceivers(string kind, Dictionary<uint, string> names)
    {
        var restored = new List<MethodDef>();
        foreach (var m in new List<MethodDef>(Bodies()))
        {
            var ins = m.Body.Instructions;
            for (int k = 0; k < ins.Count; k++)
            {
                if (ins[k].OpCode.Code != Code.Callvirt) continue;
                var callee = ins[k].Operand as IMethod;
                if (callee == null || callee.MethodSig == null || !callee.MethodSig.HasThis) continue;
                if (callee.DeclaringType == null || callee.DeclaringType.FullName != "System.String") continue;
                int r = Pusher(m, k, callee.MethodSig.Params.Count);
                if (r < 0 || ins[r].OpCode.Code != Code.Ldstr) continue;
                string name;
                if (!names.TryGetValue(m.MDToken.Raw, out name))
                { Problems.Add(kind + ": no name for the method that returned the literal called in 0x" + m.MDToken.Raw.ToString("X8")); continue; }
                var code = new List<Instruction> { new Instruction(OpCodes.Ldstr, ins[r].Operand), new Instruction(OpCodes.Ret) };
                var getter = Create(m.DeclaringType, name, MethodSig.CreateStatic(mod.CorLibTypes.String), code);
                if (getter == null) continue;
                ins[r].OpCode = OpCodes.Call; ins[r].Operand = getter;
                m.Body.KeepOldMaxStack = true;
                Touched.Add("0x" + m.MDToken.Raw.ToString("X8") + "\t1\t" + kind + ":" + name);
                Results.Add(new Result { Kind = kind, Method = getter, Sites = 1, Methods = 1 });
                restored.Add(getter);
            }
        }
        return restored;
    }

    // ---- Methods looked up by name ----------------------------------------------------------

    IMethod FindMemberRef(string fullName)
    {
        foreach (var m in Bodies())
            foreach (var i in m.Body.Instructions)
            {
                var r = i.Operand as IMethod;
                if (r != null && r.IsMethod && r.FullName == fullName) return r;
            }
        return null;
    }

    // 'typeof(T).GetMethod("Name")' no longer finds a renamed method: the obfuscator replaces
    // the lookup by the handle of the method it designated,
    //     ldtoken <method> ; call MethodBase::GetMethodFromHandle ; castclass MethodInfo
    // which has no C# form. The lookup is put back,
    //     ldtoken <type> ; call Type::GetTypeFromHandle ; ldstr <name> ; call Type::GetMethod(string)
    // with the name the method has in the source: 'names' (docs/names.tsv, by token). A site
    // whose method has no name there is a problem: the string cannot be guessed.
    public void UndoMethodHandles(string kind, Dictionary<uint, string> names)
    {
        const string fromHandle = "System.Reflection.MethodBase System.Reflection.MethodBase::GetMethodFromHandle(System.RuntimeMethodHandle)";
        int sites = 0, methods = 0;
        foreach (var m in Bodies())
        {
            var ins = m.Body.Instructions;
            int here = 0;
            for (int k = 0; k + 2 < ins.Count; k++)
            {
                if (ins[k].OpCode.Code != Code.Ldtoken) continue;
                var handle = ins[k].Operand as IMethod;
                if (handle == null || !handle.IsMethod) continue;
                var def = handle.ResolveMethodDef();
                var call = ins[k + 1].Operand as IMethod;
                var cast = ins[k + 2].Operand as ITypeDefOrRef;
                if (def == null || def.Module != mod || ins[k + 1].OpCode.Code != Code.Call || call == null || call.FullName != fromHandle
                    || ins[k + 2].OpCode.Code != Code.Castclass || cast == null || cast.FullName != "System.Reflection.MethodInfo"
                    || Referenced(m, ins[k + 1]) || Referenced(m, ins[k + 2]))
                { Problems.Add(kind + ": method handle of another shape in 0x" + m.MDToken.Raw.ToString("X8")); continue; }
                string name;
                if (!names.TryGetValue(def.MDToken.Raw, out name))
                { Problems.Add(kind + ": no name in names.tsv for 0x" + def.MDToken.Raw.ToString("X8") + ", looked up by name in 0x" + m.MDToken.Raw.ToString("X8")); continue; }
                int same = 0;
                foreach (var x in def.DeclaringType.Methods) if (x == def || (names.ContainsKey(x.MDToken.Raw) && names[x.MDToken.Raw] == name)) same++;
                if (same != 1 || def.DeclaringType.HasGenericParameters)
                { Problems.Add(kind + ": GetMethod(string) would not find 0x" + def.MDToken.Raw.ToString("X8")); continue; }
                var typeOf = FindMemberRef("System.Type System.Type::GetTypeFromHandle(System.RuntimeTypeHandle)");
                if (typeOf == null) { Problems.Add(kind + ": no reference to Type::GetTypeFromHandle in the module"); continue; }
                var type = mod.CorLibTypes.GetTypeRef("System", "Type");
                var info = cast;
                IMethod getMethod = FindMemberRef("System.Reflection.MethodInfo System.Type::GetMethod(System.String)");
                if (getMethod == null)
                    getMethod = new MemberRefUser(mod, "GetMethod", MethodSig.CreateInstance(new ClassSig(info), mod.CorLibTypes.String), type);
                ins[k].Operand = def.DeclaringType;
                ins[k + 1].Operand = typeOf;
                ins[k + 2].OpCode = OpCodes.Ldstr; ins[k + 2].Operand = name;
                ins.Insert(k + 3, new Instruction(OpCodes.Call, getMethod));
                here++;
            }
            if (here == 0) continue;
            m.Body.KeepOldMaxStack = true;
            sites += here; methods++;
            Touched.Add("0x" + m.MDToken.Raw.ToString("X8") + "\t" + here + "\t" + kind);
        }
        if (sites == 0) { Problems.Add(kind + ": no site found"); return; }
        Edits.Add(kind + "\t" + sites + "\t" + methods);
    }
}
